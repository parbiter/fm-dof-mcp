#!/usr/bin/env node
// In-game DoF chat service (host side).
//
// The FMBridge overlay is a dumb front-end — this process owns the LLM loop.
// It polls the bridge for text the player typed into the in-game "Chat with
// DoF" panel, answers via a headless Codex or Claude Code session with the
// fm-dof-mcp tool surface attached, and posts the reply back as a chat bubble.
// Auth rides on the selected CLI's local login; no API key is stored here.
//
//   node scripts/dof_chat_service.mjs
//
// Requires: FM26 running with a career loaded (bridge on ws://127.0.0.1:7777),
// mcp/fm-dof-mcp built (npm run build), selected CLI on PATH and logged in.

import { spawn } from "node:child_process";
import { mkdtempSync, writeFileSync, readFileSync, unlinkSync } from "node:fs";
import { createServer } from "node:net";
import { tmpdir } from "node:os";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const REPO = dirname(dirname(fileURLToPath(import.meta.url)));
const BRIDGE_URL = "ws://127.0.0.1:7777/";
const POLL_MS = 700;
const AGENT_TIMEOUT_MS = 240_000;
const BUBBLE_CAP = 3900; // bridge caps overlay_post text at 4000
const LOCK_PORT = 7778; // singleton guard (localhost only, nothing served)

// Set by the bridge plugin when it spawns this service alongside the game:
// lifecycle is then game-managed, so once the bridge socket drops after a
// successful connection the game is gone and this process exits with it
// (a manually started service keeps retrying across game restarts instead).
const MANAGED = process.env.DOF_CHAT_MANAGED === "1";

// Select with DOF_CHAT_PROVIDER=codex|claude. DOF_CHAT_MODEL overrides the
// provider-specific default without coupling a model name to the other CLI.
const PROVIDER = (process.env.DOF_CHAT_PROVIDER || "codex").toLowerCase();
if (PROVIDER !== "codex" && PROVIDER !== "claude") {
  console.error(`DOF_CHAT_PROVIDER must be "codex" or "claude" (got ${JSON.stringify(PROVIDER)})`);
  process.exit(1);
}
const MODEL = process.env.DOF_CHAT_MODEL || (PROVIDER === "codex" ? "gpt-5.6-luna" : "sonnet");

// How often the growing reply is pushed into the panel while streaming.
const UPDATE_MS = 400;

// In-character status lines shown while the DoF's tools run (the tool
// phase produces no reply text, so without these the panel just sits on
// a spinner for the slowest part of the answer).
const TOOL_LABELS = {
  game_status: "checking the day's diary…",
  my_club: "checking the books…",
  squad_report: "walking the training ground…",
  query_players: "flicking through scout reports…",
  read_entity: "pulling a file from the drawer…",
  get_role_attributes: "pulling a file from the drawer…",
  shortlist: "updating the shortlist…",
  inbox: "going through the mail…",
};
// MCP tool names arrive as mcp__<server>__<tool>; the last segment keys the map.
const toolLabel = (name) => TOOL_LABELS[String(name).split("__").pop()] ?? "working on it…";

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
For live-save information and actions, use only the fm-dof MCP tools. Never
use shell commands, repository files, web search, or general football memory
as a substitute for those tools.

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

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
async function terminateProcessTree(child) {
  if (!child?.pid) return;
  try { process.kill(-child.pid, "SIGTERM"); } catch { try { child.kill("SIGTERM"); } catch {} }
  await sleep(250);
  // Escalate the process group even if the direct CLI parent has already
  // exited: a spawned MCP server can otherwise survive its parent. ESRCH is
  // harmless and simply means the whole group honored SIGTERM.
  try { process.kill(-child.pid, "SIGKILL"); }
  catch {
    if (child.exitCode === null && child.signalCode === null) {
      try { child.kill("SIGKILL"); } catch {}
    }
  }
}
const isStaleResumeError = (text) =>
  /(?:session|thread)\b[^\n]{0,120}(?:not found|unknown|does not exist|expired|invalid|missing|no such|no conversation|could not resume)/i.test(text);

// ---------------------------------------------------------------- bridge WS
let ws = null;
let nextId = 1;
let everConnected = false;
const pending = new Map(); // id -> {resolve, reject, timer}
let cancelActiveRun = null;

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

// -------------------------------------------------------------- agent setup
const persona = readFileSync(join(REPO, "mcp/fm-dof-mcp/prompts/dof-persona.md"), "utf8") + CHAT_STYLE;
const mcpServerPath = join(REPO, "mcp/fm-dof-mcp/dist/index.js");

// Claude Code accepts an isolated MCP JSON file per invocation. Codex accepts
// the equivalent server definition through per-run config overrides below.
const mcpConfigPath = join(mkdtempSync(join(tmpdir(), "dof-chat-")), "mcp.json");
writeFileSync(mcpConfigPath, JSON.stringify({
  mcpServers: {
    "fm-dof": { command: "node", args: [mcpServerPath] },
  },
}));

// Keep the automation isolated from personal Codex configuration and put the
// agent itself in a read-only sandbox. The one configured MCP server is the
// complete DoF surface; its own advise-only contract governs the sole write
// capability (shortlist management).
const codexOptions = [
  "--model", MODEL,
  "--skip-git-repo-check",
  "--ignore-user-config",
  "--ignore-rules",
  "--strict-config",
  "--json",
  "--disable", "shell_tool",
  "--disable", "unified_exec",
  "-c", 'sandbox_mode="read-only"',
  "-c", 'mcp_servers.fm-dof.command="node"',
  "-c", `mcp_servers.fm-dof.args=${JSON.stringify([mcpServerPath])}`,
  "-c", 'mcp_servers.fm-dof.default_tools_approval_mode="approve"',
];

// Provider-specific conversation memory, carried across bubbles via resume
// and across service restarts via a state file. Keeping the files separate
// makes provider switching safe.
const SESSION_FILE = join(tmpdir(), PROVIDER === "codex" ? "dof-chat-codex-thread-id" : "dof-chat-session-id");
let sessionId = null;
try { sessionId = readFileSync(SESSION_FILE, "utf8").trim() || null; } catch { }
if (sessionId) log("resuming conversation", sessionId);
function saveSession(id) {
  if (!id || id === sessionId) { sessionId = id ?? sessionId; return; }
  sessionId = id;
  try { writeFileSync(SESSION_FILE, id); } catch { }
}

function runCodex(userText, gen) {
  return new Promise((resolve) => {
    // The persona is seeded into a new thread once; resumed turns retain it.
    // `codex exec --json` emits completed agent messages and MCP lifecycle
    // events as JSONL. Unlike token-delta streaming, this still lets the panel
    // show any brief pre-tool aside before replacing it with the final answer.
    const prompt = sessionId ? userText : `${persona}\n\n## Current request\n\n${userText}`;
    const args = sessionId
      ? ["exec", "resume", ...codexOptions, sessionId, prompt]
      : ["exec", ...codexOptions, prompt];
    const child = spawn("codex", args, { cwd: REPO, stdio: ["ignore", "pipe", "pipe"], detached: true });
    let buf = "", err = "";
    let timedOut = false, cancelled = false, stopping = false;
    const stop = async (reason) => {
      if (stopping) return;
      stopping = true;
      if (reason === "timeout") timedOut = true;
      if (reason === "cancelled") cancelled = true;
      await terminateProcessTree(child);
    };
    cancelActiveRun = () => { void stop("cancelled"); };
    const kill = setTimeout(() => { void stop("timeout"); }, AGENT_TIMEOUT_MS);

    // `text` holds the latest completed agent message, so a "let me check the
    // books" aside is replaced by the final answer rather than concatenated
    // with it. If the bridge predates overlay_update, the first failure flips
    // streamOk and the reply falls back to drainQueue's single overlay_post.
    let text = "", sawText = false, streamOk = true;
    let pushTimer = null;
    let threadId = null;
    let turnCompleted = false;

    // Throttled push of the accumulated text into the growing bubble.
    const push = () => {
      if (pushTimer || !streamOk || gen !== chatGen) return;
      pushTimer = setTimeout(() => {
        pushTimer = null;
        if (!streamOk || gen !== chatGen || !text) return;
        // call() resolves bridge-level errors (an old dll answers
        // {ok:false, error:"unknown-action:…"}) rather than rejecting,
        // so failure has two shapes and both must flip the fallback.
        call({ method: "ui_inject", action: "overlay_update", text: text.slice(0, BUBBLE_CAP), done: false })
          .then((res) => { if (res?.result?.ok === false) streamOk = false; })
          .catch(() => { streamOk = false; });
      }, UPDATE_MS);
    };

    const onEvent = (j) => {
      if (j.type === "thread.started") { threadId = j.thread_id; return; }
      if (j.type === "turn.completed") { turnCompleted = true; return; }
      if (gen !== chatGen) return;
      const item = j.item ?? {};
      if (j.type === "item.started" && item.type === "mcp_tool_call") {
        // No labels once prose has started: the first overlay_update
        // already hid the indicator, and the text is company enough.
        if (!sawText && streamOk)
          call({ method: "ui_inject", action: "overlay_thinking", on: true, label: toolLabel(item.tool) }).catch(() => {});
      } else if (j.type === "item.completed" && item.type === "agent_message") {
        text = item.text ?? "";
        sawText = true;
        push();
      }
    };

    child.stdout.on("data", (d) => {
      buf += d;
      let nl;
      while ((nl = buf.indexOf("\n")) >= 0) {
        const line = buf.slice(0, nl);
        buf = buf.slice(nl + 1);
        if (!line.trim()) continue;
        try { onEvent(JSON.parse(line)); } catch { }
      }
    });
    child.stderr.on("data", (d) => (err += d));
    child.on("error", (e) => { err += e.message; });

    child.on("close", async (code) => {
      clearTimeout(kill);
      if (cancelActiveRun) cancelActiveRun = null;
      if (pushTimer) { clearTimeout(pushTimer); pushTimer = null; }
      if (timedOut) return resolve({ ok: false, text: "Sorry, that took too long. Ask me again with a narrower brief." });
      if (cancelled) return resolve({ ok: false, text: "I stopped that search. Ask me again when ready." });
      if (code !== 0) {
        log("codex exited", code, err.slice(0, 300));
        if (sessionId && isStaleResumeError(err + buf)) {
          // Stale thread (service outlived Codex's session store) —
          // drop it and retry once with a fresh conversation
          log("dropping session, retrying fresh");
          sessionId = null;
          return resolve(runCodex(userText, gen));
        }
        return resolve({ ok: false, text: "Sorry, I couldn't reach my desk just now. Try me again." });
      }
      if (!turnCompleted || !text.trim()) {
        return resolve({ ok: false, text: "Sorry, I garbled that one. Ask me again." });
      }
      // A "New chat" click mid-answer bumps chatGen; saving this run's
      // session id then would resurrect the abandoned conversation.
      if (threadId && gen === chatGen) saveSession(threadId);
      const finalText = text.trim();
      // Finalize the streamed bubble in place; streamed:true tells
      // drainQueue the reply already landed in the panel.
      let streamed = false;
      if (streamOk && gen === chatGen) {
        try {
          const res = await call({ method: "ui_inject", action: "overlay_update", text: finalText.slice(0, BUBBLE_CAP), done: true });
          if (res?.result?.ok === false) throw new Error(res.result.error || "overlay_update refused");
          streamed = true;
          call({ method: "ui_inject", action: "overlay_thinking", on: false }).catch(() => {});
        } catch { /* fall through to overlay_post in drainQueue */ }
      }
      resolve({ ok: true, text: finalText, streamed });
    });
  });
}

function runClaude(userText, gen) {
  return new Promise((resolve) => {
    const args = [
      "-p", userText,
      "--model", MODEL,
      // stream-json with partial chunks lets the reply grow in the panel;
      // Claude Code requires --verbose alongside it in print mode.
      "--output-format", "stream-json",
      "--include-partial-messages",
      "--verbose",
      "--mcp-config", mcpConfigPath,
      "--strict-mcp-config",
      "--tools", "",
      "--allowedTools", "mcp__fm-dof__*",
      "--append-system-prompt", persona,
    ];
    if (sessionId) args.push("--resume", sessionId);
    const child = spawn("claude", args, { cwd: REPO, stdio: ["ignore", "pipe", "pipe"], detached: true });
    let buf = "", err = "";
    let timedOut = false, cancelled = false, stopping = false;
    const stop = async (reason) => {
      if (stopping) return;
      stopping = true;
      if (reason === "timeout") timedOut = true;
      if (reason === "cancelled") cancelled = true;
      await terminateProcessTree(child);
    };
    cancelActiveRun = () => { void stop("cancelled"); };
    const kill = setTimeout(() => { void stop("timeout"); }, AGENT_TIMEOUT_MS);

    // Claude emits token deltas. Reset on each assistant message so a brief
    // pre-tool aside is replaced by the final answer in the same bubble.
    let text = "", sawText = false, streamOk = true;
    let pushTimer = null;
    let resultEvent = null;

    const push = () => {
      if (pushTimer || !streamOk || gen !== chatGen) return;
      pushTimer = setTimeout(() => {
        pushTimer = null;
        if (!streamOk || gen !== chatGen || !text) return;
        call({ method: "ui_inject", action: "overlay_update", text: text.slice(0, BUBBLE_CAP), done: false })
          .then((res) => { if (res?.result?.ok === false) streamOk = false; })
          .catch(() => { streamOk = false; });
      }, UPDATE_MS);
    };

    const onEvent = (j) => {
      if (j.type === "result") { resultEvent = j; return; }
      if (j.type !== "stream_event" || gen !== chatGen) return;
      const ev = j.event ?? {};
      if (ev.type === "message_start") {
        text = "";
      } else if (ev.type === "content_block_start" && ev.content_block?.type === "tool_use") {
        if (!sawText && streamOk)
          call({ method: "ui_inject", action: "overlay_thinking", on: true, label: toolLabel(ev.content_block.name) }).catch(() => {});
      } else if (ev.type === "content_block_delta" && ev.delta?.type === "text_delta") {
        text += ev.delta.text;
        sawText = true;
        push();
      }
    };

    child.stdout.on("data", (d) => {
      buf += d;
      let nl;
      while ((nl = buf.indexOf("\n")) >= 0) {
        const line = buf.slice(0, nl);
        buf = buf.slice(nl + 1);
        if (!line.trim()) continue;
        try { onEvent(JSON.parse(line)); } catch { }
      }
    });
    child.stderr.on("data", (d) => (err += d));
    child.on("error", (e) => { err += e.message; });

    child.on("close", async (code) => {
      clearTimeout(kill);
      if (cancelActiveRun) cancelActiveRun = null;
      if (pushTimer) { clearTimeout(pushTimer); pushTimer = null; }
      if (timedOut) return resolve({ ok: false, text: "Sorry, that took too long. Ask me again with a narrower brief." });
      if (cancelled) return resolve({ ok: false, text: "I stopped that search. Ask me again when ready." });
      if (code !== 0) {
        log("claude exited", code, err.slice(0, 300));
        if (sessionId && isStaleResumeError(err + buf)) {
          log("dropping session, retrying fresh");
          sessionId = null;
          return resolve(runClaude(userText, gen));
        }
        return resolve({ ok: false, text: "Sorry, I couldn't reach my desk just now. Try me again." });
      }
      if (!resultEvent) {
        return resolve({ ok: false, text: "Sorry, I garbled that one. Ask me again." });
      }
      if (resultEvent.session_id && gen === chatGen) saveSession(resultEvent.session_id);
      const finalText = (resultEvent.result ?? "").trim() || "(no answer)";
      let streamed = false;
      if (streamOk && gen === chatGen) {
        try {
          const res = await call({ method: "ui_inject", action: "overlay_update", text: finalText.slice(0, BUBBLE_CAP), done: true });
          if (res?.result?.ok === false) throw new Error(res.result.error || "overlay_update refused");
          streamed = true;
          call({ method: "ui_inject", action: "overlay_thinking", on: false }).catch(() => {});
        } catch { /* fall through to overlay_post in drainQueue */ }
      }
      resolve({ ok: true, text: finalText, streamed });
    });
  });
}

const runAgent = PROVIDER === "claude" ? runClaude : runCodex;

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
    const reply = await runAgent(text, gen);
    log(`A (${((Date.now() - t0) / 1000).toFixed(1)}s):`, reply.text.slice(0, 120).replace(/\n/g, " "));
    if (gen !== chatGen) {
      log("dropping reply: new chat started while answering");
      call({ method: "ui_inject", action: "overlay_thinking", on: false }).catch(() => {});
      continue;
    }
    if (reply.streamed) continue; // already finalized in the panel by provider
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
      if (cancelActiveRun) cancelActiveRun();
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
  log("DoF chat service starting; repo:", REPO, "provider:", PROVIDER, "model:", MODEL, MANAGED ? "(game-managed)" : "");
  connect();
  poll();
}
