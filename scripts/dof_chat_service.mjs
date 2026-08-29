#!/usr/bin/env node
// In-game DoF chat service (host side).
//
// The FMBridge overlay is a dumb front-end — this process owns the LLM loop.
// It polls the bridge for text the player typed into the in-game "Chat with
// DoF" panel, answers via headless `claude -p` with the fm-dof-mcp tool
// surface attached, and posts the reply back as a chat bubble. Auth rides on
// the local Claude Code login; no API key is stored or read here.
//
//   node scripts/dof_chat_service.mjs
//
// Requires: FM26 running with a career loaded (bridge on ws://127.0.0.1:7777),
// mcp/fm-dof-mcp built (npm run build), `claude` CLI on PATH.

import { spawn } from "node:child_process";
import { mkdtempSync, writeFileSync, readFileSync, unlinkSync } from "node:fs";
import { createServer } from "node:net";
import { tmpdir } from "node:os";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const REPO = dirname(dirname(fileURLToPath(import.meta.url)));
const BRIDGE_URL = "ws://127.0.0.1:7777/";
const POLL_MS = 700;
const CLAUDE_TIMEOUT_MS = 240_000;
const BUBBLE_CAP = 3900; // bridge caps overlay_post text at 4000
const LOCK_PORT = 7778; // singleton guard (localhost only, nothing served)

// Set by the bridge plugin when it spawns this service alongside the game:
// lifecycle is then game-managed, so once the bridge socket drops after a
// successful connection the game is gone and this process exits with it
// (a manually started service keeps retrying across game restarts instead).
const MANAGED = process.env.DOF_CHAT_MANAGED === "1";

// Chat-only layer on top of the canonical dof-persona.md (which the MCP
// server also serves, roleplay-free): in-character voice + panel-sized
// formatting. The persona's data-honesty rules still win — character never
// invents numbers.
const CHAT_STYLE = `

## In-game chat mode

You are replying inside a small chat panel docked in the manager's FM26
screen, mid-session. Stay in character the whole time: you are the club's
Director of Football — a seasoned, dry-witted football man who has seen a
thousand transfer windows. You answer to the board, not to the manager:
speak as a senior colleague between equals — no "boss", no "gaffer", no
deference. Opinionated, warm underneath, allergic to waffle.
You may banter briefly, but you never bluff about data: every number still
comes from your tools, and missing data is stated plainly, in character
("my scouts haven't priced him yet").

You are talking to a person, not filing a report — this overrides the
persona's cite-uids rule, which is for written analysis, not chat:
- Never mention uids, internal field or variable names, tool names, or
  the machinery behind your answers (data reads, flags, faults). If a
  lookup fails, one in-character line ("couldn't get his numbers today")
  and move on — no diagnostics.
- Translate data into football speech: "a natural left-back", "on about
  €1.1M a year", "the bigger prospect" — not "LeftBack familiarity 20"
  or "PA 178".
- Numbers only where they carry the argument (a fee, a wage, an age) —
  two or three per reply, at most.

Formatting, strictly: match length to the question — a simple question
gets two or three sentences; 120 words is a hard ceiling for even the
biggest ask, not a target. Roughly 50 characters per line wrap, plain
prose or short dashed lists only — no markdown headers, no tables, no
bold. Lead with the recommendation, then what justifies it.`;

const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);

// ---------------------------------------------------------------- bridge WS
let ws = null;
let nextId = 1;
let everConnected = false;
const pending = new Map(); // id -> {resolve, reject, timer}

function connect() {
  ws = new WebSocket(BRIDGE_URL);
  ws.onopen = async () => {
    log("bridge connected");
    everConnected = true;
    try {
      await call({ method: "ui_inject", action: "overlay_add" });
      // Arms the "Chat with DoF" row; the bridge attaches it whenever
      // the Recruitment nav dropdown exists and re-attaches after
      // screen changes, so once per connection is enough.
      await call({ method: "ui_inject", action: "menu_add" });
      log("overlay up, menu armed");
    } catch (e) {
      log("ui setup failed:", e.message);
    }
  };
  ws.onmessage = (ev) => {
    let msg;
    try { msg = JSON.parse(ev.data); } catch { return; }
    if (msg.event || !pending.has(msg.id)) return;
    const p = pending.get(msg.id);
    pending.delete(msg.id);
    clearTimeout(p.timer);
    p.resolve(msg);
  };
  ws.onclose = () => {
    for (const p of pending.values()) { clearTimeout(p.timer); p.reject(new Error("bridge closed")); }
    pending.clear();
    ws = null;
    if (MANAGED && everConnected) {
      log("bridge gone — exiting (game-managed lifecycle)");
      process.exit(0);
    }
    setTimeout(connect, 2000); // game restarting / not up yet — keep trying
  };
  ws.onerror = () => {}; // onclose follows and handles retry
}

function call(req, timeoutMs = 30_000) {
  return new Promise((resolve, reject) => {
    if (!ws || ws.readyState !== WebSocket.OPEN) return reject(new Error("bridge not connected"));
    const id = nextId++;
    const timer = setTimeout(() => { pending.delete(id); reject(new Error("bridge timeout")); }, timeoutMs);
    pending.set(id, { resolve, reject, timer });
    ws.send(JSON.stringify({ ...req, id }));
  });
}

// ---------------------------------------------------------------- claude
const mcpConfigPath = join(mkdtempSync(join(tmpdir(), "dof-chat-")), "mcp.json");
writeFileSync(mcpConfigPath, JSON.stringify({
  mcpServers: {
    "fm-dof": {
      command: "node",
      args: [join(REPO, "mcp/fm-dof-mcp/dist/index.js")],
    },
  },
}));

const persona = readFileSync(join(REPO, "mcp/fm-dof-mcp/prompts/dof-persona.md"), "utf8") + CHAT_STYLE;

// Conversation memory: claude session id, carried across bubbles via
// --resume and across service restarts via a state file (cleared on
// reboot with the tmpdir; delete the file to start a fresh conversation).
const SESSION_FILE = join(tmpdir(), "dof-chat-session-id");
let sessionId = null;
try { sessionId = readFileSync(SESSION_FILE, "utf8").trim() || null; } catch { }
if (sessionId) log("resuming conversation", sessionId);
function saveSession(id) {
  if (!id || id === sessionId) { sessionId = id ?? sessionId; return; }
  sessionId = id;
  try { writeFileSync(SESSION_FILE, id); } catch { }
}

function runClaude(userText, gen) {
  return new Promise((resolve) => {
    const args = [
      "-p", userText,
      "--model", "sonnet",
      "--output-format", "json",
      "--mcp-config", mcpConfigPath,
      "--strict-mcp-config",
      "--allowedTools", "mcp__fm-dof__*",
      "--append-system-prompt", persona,
    ];
    if (sessionId) args.push("--resume", sessionId);
    const child = spawn("claude", args, { cwd: REPO, stdio: ["ignore", "pipe", "pipe"] });
    let out = "", err = "";
    const kill = setTimeout(() => child.kill("SIGKILL"), CLAUDE_TIMEOUT_MS);
    child.stdout.on("data", (d) => (out += d));
    child.stderr.on("data", (d) => (err += d));
    child.on("close", (code) => {
      clearTimeout(kill);
      if (code !== 0) {
        log("claude exited", code, err.slice(0, 300));
        if (sessionId) {
          // stale session (service outlived claude's session store) —
          // drop it and retry once with a fresh conversation
          log("dropping session, retrying fresh");
          sessionId = null;
          return resolve(runClaude(userText, gen));
        }
        return resolve({ ok: false, text: "Sorry, I couldn't reach my desk just now. Try me again." });
      }
      try {
        const j = JSON.parse(out);
        // A "New chat" click mid-answer bumps chatGen; saving this run's
        // session id then would resurrect the abandoned conversation.
        if (j.session_id && gen === chatGen) saveSession(j.session_id);
        const text = (j.result ?? "").trim();
        resolve({ ok: true, text: text || "(no answer)" });
      } catch {
        resolve({ ok: false, text: "Sorry, I garbled that one. Ask me again." });
      }
    });
  });
}

// ---------------------------------------------------------------- main loop
const queue = [];
let busy = false;
// Bumped by the overlay's "New chat" button (overlay_poll new_chat flag).
// A reply computed under an older generation is dropped instead of posted —
// it belongs to the conversation the player just abandoned.
let chatGen = 0;

async function drainQueue() {
  if (busy) return;
  busy = true;
  while (queue.length) {
    const text = queue.shift();
    const gen = chatGen;
    log("Q:", text);
    // In-character wait indicator; the bridge removes it automatically when
    // the dof reply bubble lands, so the explicit off below is only cleanup
    // for the failure/dropped paths.
    call({ method: "ui_inject", action: "overlay_thinking", on: true }).catch(() => {});
    const t0 = Date.now();
    const reply = await runClaude(text, gen);
    log(`A (${((Date.now() - t0) / 1000).toFixed(1)}s):`, reply.text.slice(0, 120).replace(/\n/g, " "));
    if (gen !== chatGen) {
      log("dropping reply: new chat started while answering");
      call({ method: "ui_inject", action: "overlay_thinking", on: false }).catch(() => {});
      continue;
    }
    try {
      await call({ method: "ui_inject", action: "overlay_post", from: "dof", text: reply.text.slice(0, BUBBLE_CAP) });
    } catch (e) {
      log("overlay_post failed:", e.message); // bubble lost; the reply is in the log
      call({ method: "ui_inject", action: "overlay_thinking", on: false }).catch(() => {});
    }
  }
  busy = false;
}

async function poll() {
  try {
    const res = await call({ method: "ui_inject", action: "overlay_poll" }, 10_000);
    if (res?.result?.new_chat) {
      chatGen++;
      queue.length = 0;
      sessionId = null;
      try { unlinkSync(SESSION_FILE); } catch { }
      log("new chat: session dropped, queue cleared");
    }
    const msgs = res?.result?.messages ?? [];
    for (const m of msgs) if (m.text) queue.push(m.text);
    if (msgs.length) drainQueue();
  } catch { /* disconnected; connect() loop handles it */ }
  setTimeout(poll, POLL_MS);
}

// Singleton guard: two services polling the same panel would each steal
// half the messages. Holding a localhost port is the lock — it releases
// itself no matter how this process dies. Taken before touching the
// bridge, so a bridge-spawned copy and a manual one can never both run.
const lock = createServer();
lock.once("error", (e) => {
  if (e.code === "EADDRINUSE") {
    log("another DoF chat service is already running — exiting");
    process.exit(0);
  }
  log("lock port unavailable (" + e.code + ") — continuing without singleton guard");
  start();
});
lock.once("listening", start);
lock.listen(LOCK_PORT, "127.0.0.1");

function start() {
  log("DoF chat service starting; repo:", REPO, MANAGED ? "(game-managed)" : "");
  connect();
  poll();
}
