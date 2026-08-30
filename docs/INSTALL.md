# Install

This walks you through running fm-dof-mcp end to end, one numbered step at
a time, with exact commands to paste into Terminal and a way to check each
step actually worked before moving to the next. Total time is roughly
45–75 minutes, most of it spent waiting on downloads and one game restart.

It assumes **macOS on Apple Silicon (M1/M2/M3/M4)** and a Steam copy of
Football Manager 26 — nothing else is supported (see the platform note in
the [README](../README.md)).

A few terms you'll run into:

- **BepInEx** — the "mod loader" that lets Football Manager run plugins
  like this one. FM26 doesn't support plugins on its own.
- **IL2CPP** — the specific way FM26 itself is built; it needs a less
  common variant of BepInEx, covered in step 2.
- **MCP (Model Context Protocol)** — the standard the chat's AI tools use
  to read your game data.
- **.NET SDK** and **Node.js** — two developer toolkits used to build this
  mod's two small helper programs. You don't need to know how to code —
  you're just running the build commands below.

## 1. Check the basics (~5 min)

**Why:** you need Football Manager 26 itself, two small developer
toolkits to build this mod's helper programs, and — later — an AI CLI
account.

```bash
dotnet --version   # need 6.0 or newer
node --version     # need 20.0 or newer
```

If either command isn't found, or the version printed is older than what's
needed, install it:

- [.NET SDK](https://dotnet.microsoft.com/download) — get 6.0 or later.
- [Node.js](https://nodejs.org/) — get 20 or later.

<!-- TODO: confirm whether Xcode Command Line Tools and/or Homebrew are
     required on a clean Mac (e.g. for `dotnet build` or `git` to work at
     all) — not verified from this repo; add here if a maintainer confirms
     they're needed. -->

**How you'll know it worked:** both commands print a version number that
meets the minimum above.

## 2. Install the mod loader (BepInEx 6, IL2CPP) (~15–20 min)

**Why:** stock BepInEx does not run on FM26 on Apple Silicon — the game
binary lacks an entitlement the mod loader's runtime needs, and its
interop layer can't resolve hook targets on this chip. fm-dof-mcp's plugin
also needs one small feature that isn't part of upstream BepInEx at all. A
third-party, MIT-licensed compatibility layer for FM26 on macOS solves all
of this.

```bash
git clone https://github.com/DadMych/fm26-player-export-macos.git
cd fm26-player-export-macos
FM26_GAME="/path/to/Football Manager 26" bash install_macos.sh
```

Replace `/path/to/Football Manager 26` with your actual install — typically:

```
$HOME/Library/Application Support/Steam/steamapps/common/Football Manager 26
```

This installer sets up a launcher script you'll use to start the game from
now on (see step 6) — launching a plain double-click through Steam skips
the mod loader unless this installer also changed your Steam launch
options to point at that launcher.

[DadMych/fm26-player-export-macos](https://github.com/DadMych/fm26-player-export-macos)
is unaffiliated with fm-dof-mcp; it's simply the compatibility layer that
makes any BepInEx plugin — including this one — loadable on macOS Apple
Silicon at all.

**How you'll know it worked:** launch the game via the launcher that
installer set up, then check `BepInEx/LogOutput.log` inside the game
folder for chainloader startup messages. If it doesn't come up cleanly,
see that project's own troubleshooting table — those failure modes are
specific to macOS/BepInEx, not to this repo.

## 3. Build and deploy the bridge (~5 min)

**Why:** this compiles and installs the small plugin ("the bridge") that
runs inside FM26 and lets the chat read your save.

From this repo's root:

```bash
export FM26_DIR="$HOME/Library/Application Support/Steam/steamapps/common/Football Manager 26"
./scripts/build_bridge.sh
./scripts/deploy_bridge.sh
```

If your game lives at the standard Steam path above, you can skip the
`export` line — that's the default. `build_bridge.sh` compiles the
plugin against BepInEx's own files under `$FM26_DIR/BepInEx`;
`deploy_bridge.sh` copies the result into
`$FM26_DIR/BepInEx/plugins/FMBridge/` and records where `node` and this
repo live, so the game can start the chat service by itself later (step
6).

Restart FM26 (via the launcher from step 2) and load into a career.

**How you'll know it worked:** `BepInEx/LogOutput.log` shows a line like:

```
[Info   :Bridge] loaded, pump=MainThreadTick (DadMych fork)
```

## 4. Build the MCP server (~2 min)

**Why:** this is the DoF's actual tool set — the code that turns your
questions into reads of your save.

```bash
cd mcp/fm-dof-mcp
npm install
npm run build
```

**How you'll know it worked:** both commands finish without errors, and
`mcp/fm-dof-mcp/dist/index.js` exists.

## 5. Choose and sign in to an AI CLI (~5–10 min)

**Why:** the chat itself doesn't think — it hands your question to an AI
CLI you're signed in to, which does the actual reasoning and decides which
tools to call. Pick whichever one you already use or want to pay for:

- **[Codex](https://developers.openai.com/codex/cli)** — needs an OpenAI
  account. This is the default if you don't choose otherwise.
- **[Claude Code](https://claude.com/claude-code)** — needs an Anthropic
  account.

Either can involve a subscription or metered API usage — check each
provider's own page for current pricing before picking. Follow the
instructions on whichever page you choose to install that CLI and sign
in; the same page covers how to confirm you're signed in.

If you're using Claude Code instead of the default Codex, tell the bridge
by redeploying (from this repo's root, after step 3 has already produced
a build):

```bash
DOF_CHAT_PROVIDER=claude ./scripts/deploy_bridge.sh
```

Use `DOF_CHAT_PROVIDER=codex` to select Codex explicitly if you ever need
to switch back. The two providers keep separate conversation histories, so
switching cannot resume a conversation through the wrong CLI. You can also
set `DOF_CHAT_MODEL` before this command to pick a specific model instead
of the provider's default.

**How you'll know it worked:** running `codex` (or `claude`) opens the CLI
without prompting you to sign in again.

## 6. Launch the game so the mod loads (~2 min)

**Why:** Football Manager only loads BepInEx — and this mod — when it's
started through the launcher script from step 2, not a plain Steam
launch, unless that installer changed your Steam launch options to already
point at it.

Launch FM26 using the launcher script step 2 set up (or via Steam, only if
that installer configured your launch options to use it), then load into
a career.

**How you'll know it worked:** `BepInEx/LogOutput.log` shows the
`[Info   :Bridge] loaded, ...` line from step 3, and — a few seconds later —
`chat_service.log` next to the deployed plugin
(`$FM26_DIR/BepInEx/plugins/FMBridge/chat_service.log`) appears and starts
growing.

## 7. First chat smoke test (~2 min)

**Why:** confirms the whole chain — mod, chat service, AI CLI — is wired
up correctly before you rely on it mid-career.

With FM26 running and a career loaded, open **Recruitment → Chat with
DoF** in the sidebar and ask something simple, like "what's today's date?"
or "how much is in my transfer budget?"

**How you'll know it worked:** a reply appears as a chat bubble within
about 30 seconds, with a real answer pulled from your save (not an error
message).

## Updating later

When you pull a newer version of this repo:

```bash
git pull
./scripts/build_bridge.sh
./scripts/deploy_bridge.sh
cd mcp/fm-dof-mcp && npm install && npm run build
```

Restart FM26 afterwards. If you use Claude Code, re-run the
`DOF_CHAT_PROVIDER=claude` deploy command from step 5 again — a plain
`deploy_bridge.sh` resets the chat back to the default provider (Codex).

## Uninstalling

To remove fm-dof-mcp itself:

- Delete `$FM26_DIR/BepInEx/plugins/FMBridge/` (the plugin, its config,
  and its logs).
- Delete the `fm-dof-mcp` folder you cloned.

The mod loader (BepInEx) and its macOS compatibility layer are a separate,
third-party install — see
[DadMych/fm26-player-export-macos](https://github.com/DadMych/fm26-player-export-macos)
for how to remove those too, if you want the game back to a fully stock
state.

Your AI CLI (Codex or Claude Code) is independent software — leave it
installed or remove it separately, entirely up to you.

## Advanced: external MCP clients

The same MCP server the chat runs on can be attached to any MCP client —
Codex, Claude Code, Claude Desktop, or anything else that speaks MCP — for
longer written analysis outside the game window.

### Codex

```bash
codex mcp add fm-dof-mcp -- node "/absolute/path/to/fm-dof-mcp/mcp/fm-dof-mcp/dist/index.js"
```

Use the absolute path to wherever you cloned this repo.

### Claude Code

```bash
claude mcp add fm-dof-mcp -- node "/absolute/path/to/fm-dof-mcp/mcp/fm-dof-mcp/dist/index.js"
```

Use the absolute path to wherever you cloned this repo.

### Claude Desktop

Add to your Claude Desktop config (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "fm-dof-mcp": {
      "command": "node",
      "args": ["/absolute/path/to/fm-dof-mcp/mcp/fm-dof-mcp/dist/index.js"]
    }
  }
}
```

Restart the client after editing the config.

### Use the `dof-persona` prompt

The server exposes a `dof-persona` MCP prompt
(`mcp/fm-dof-mcp/prompts/dof-persona.md`). Most MCP clients let you insert
a server-provided prompt into the conversation (in Claude Code, it shows
up as a slash command named after the server and prompt, e.g.
`/fm-dof-mcp:dof-persona`). Load it at the start of a session so the model
reasons only from what the tools return for your save, not from general
football knowledge.

### First-run smoke test

With FM26 running and a career loaded, ask your MCP client to call the
`game_status` tool (or just ask "what's the game status?" once the
dof-persona prompt or server is loaded). A healthy response looks like:

```json
{ "processing": false, "date_iso": "2025-11-01 09:00", "continue_state": "CanContinue" }
```

If that comes back, the whole chain — bridge plugin, WebSocket, MCP
server, MCP client — is working. From there, try `my_club`,
`squad_report`, or `query_players`.

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| Nothing works at all — no `LogOutput.log`, chat never appears, game looks completely stock | FM26 was started directly through Steam without going through the launcher from step 2, and Steam's launch options weren't set to use it either, so BepInEx never loaded. | Quit and relaunch using the launcher script from step 2. If you'd rather always launch through Steam, follow that installer's own instructions for pointing your Steam launch options at it. |
| Chat replies with "Sorry, I couldn't reach my desk just now." | The selected `codex` or `claude` CLI is not on the PATH recorded when you last ran `deploy_bridge.sh`, is not signed in, or the MCP server isn't built (step 4). | Confirm the CLI works and is signed in from a normal Terminal window, confirm step 4's build exists, then re-run `deploy_bridge.sh` (this re-captures your current PATH) and restart FM26. Check `chat_service.log` next to the deployed plugin for the exact provider error. |
| "It can't find any players outside my squad" / recommendations feel very limited | The DoF reads from Football Manager's own Player Database screen (Recruitment tab) — it can only see the players that screen is currently showing you, which depends on your save's scouting network and world-detail/competition settings. | Widen what the game itself shows first (scout more competitions/leagues, adjust world detail), then ask again — the DoF picks up whatever the screen now shows. Also try asking without restricting to scouted-only players. |
| Bridge plugin never starts its WebSocket server, or tool calls never connect even though the plugin loaded | Something else on your Mac is already using port 7777, which this project uses by default. | Quit whatever's using it (`lsof -i :7777` in Terminal shows what) and restart FM26. <!-- TODO: the port is configurable via BepInEx/config/dev.fmdofmcp.bridge.cfg under [Voice] Port, but changing it also requires matching changes to the in-game chat service and MCP server that aren't currently wired up as a simple option — flagging for a maintainer rather than documenting an unsupported workaround. --> |
| MCP client can't start the server / immediate exit | `npm run build` didn't succeed, or `dist/index.js` is missing. | Re-run step 4; run `node dist/index.js` directly to see the startup error. |
| Tool calls time out or error connecting | FM26 isn't running, no career is loaded, or the bridge plugin didn't load. | Check `BepInEx/LogOutput.log` for the `[Bridge] loaded, pump=...` line; if it's missing, redo step 6. |
| `pump=win-x64 Harmony pump` / "not implemented yet" warning | You're not on macOS Apple Silicon. | This project only supports macOS arm64 today — see the platform note in the [README](../README.md). |
| BepInEx never loads at all (no `LogOutput.log`, instant crash, or the game exits after ~30s at the main menu) | This is a BepInEx/macOS issue, not an fm-dof-mcp one. | See the troubleshooting table in [DadMych/fm26-player-export-macos](https://github.com/DadMych/fm26-player-export-macos#troubleshooting). |
| `build_bridge.sh` fails looking for BepInEx assemblies | `$FM26_DIR/BepInEx/core` and `$FM26_DIR/BepInEx/interop` don't exist — step 2 didn't complete successfully, or `FM26_DIR` points at the wrong folder. | Confirm those folders exist; always build via `./scripts/build_bridge.sh` rather than a bare `dotnet build`, which can't find the BepInEx assemblies on its own. |
| `game_status` returns, but the date never matches your actual save | You're pointed at the wrong `FM26_DIR`, or a stale plugin copy is deployed. | Rerun `deploy_bridge.sh` after any rebuild, and confirm `FM26_DIR` matches your actual install. |
| Chat panel never appears under Recruitment | Autostart didn't run, or the menu hasn't attached yet. | Check `chat_service.log` next to the deployed plugin — it should say `bridge connected` then `overlay up, menu armed`. If the log is missing, check `BepInEx/LogOutput.log` for `[Bridge] chat service` lines and rerun `deploy_bridge.sh`. The menu row is injected once the Recruitment dropdown exists — visit a main squad screen once, then reopen the dropdown. |
