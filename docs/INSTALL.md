# Install

This walks a stranger through running fm-dof-mcp end to end: BepInEx, the
bridge plugin, the MCP server, and the in-game chat. It assumes **macOS on
Apple Silicon (M1/M2/M3/M4)** and a Steam copy of Football Manager 26 —
nothing else is supported (see the platform note in the [README](../README.md)).

## 0. Prerequisites

- FM26 installed via Steam.
- [.NET SDK 6+](https://dotnet.microsoft.com/download) (for building the
  bridge plugin).
- [Node.js 20+](https://nodejs.org/) (for building/running the MCP server
  and the chat service).
- At least one supported agent CLI, installed and logged in:
  [Codex](https://developers.openai.com/codex/cli) or
  [Claude Code](https://claude.com/claude-code). The default is Codex; no API
  key is configured anywhere by this project.

## 1. Install BepInEx 6 (IL2CPP) for FM26 on macOS

Stock BepInEx does not run on FM26 on Apple Silicon: the game binary lacks
the JIT entitlement CoreCLR needs, and `Il2CppInterop.ClassInjector` can't
resolve hook targets on arm64. fm-dof-mcp's bridge plugin also needs a
`MainThreadTick` event that isn't part of upstream BepInEx at all — it ticks
by subscribing to `BepInEx.Unity.IL2CPP.IL2CPPChainloader.MainThreadTick`
(see `bridge/FMBridge/src/Pump/MainThreadTickPump.cs`).

All three problems are solved by a third-party, MIT-licensed compatibility
layer for FM26 on macOS arm64:
**[github.com/DadMych/fm26-player-export-macos](https://github.com/DadMych/fm26-player-export-macos)**.
It ships a native arm64 launcher, a JIT-entitled "shadow bundle" of the game,
and a patched BepInEx core (including the `MainThreadTick` event fm-dof-mcp
relies on). This project is unaffiliated with that one; it's simply the
compatibility layer that makes any BepInEx plugin — including this one —
loadable on macOS arm64 at all.

```bash
git clone https://github.com/DadMych/fm26-player-export-macos.git
cd fm26-player-export-macos
FM26_GAME="/path/to/Football Manager 26" bash install_macos.sh
```

Replace `/path/to/Football Manager 26` with your actual install — typically:

```
$HOME/Library/Application Support/Steam/steamapps/common/Football Manager 26
```

Launch the game via the launcher that installer sets up
(`run_bepinex_arm64.sh` in the game folder, or via Steam if the installer
configured your launch options) — not directly through Steam without it.
Confirm BepInEx is active by checking `BepInEx/LogOutput.log` in the game
folder for chainloader startup messages after launch. If it doesn't come up
cleanly, see that project's own troubleshooting table — the failure modes
(JIT entitlement, Rosetta vs. arm64 launch, `ClassInjector` hook failures)
are all specific to macOS/BepInEx, not to this repo.

## 2. Build and deploy the bridge plugin

From this repo's root:

```bash
export FM26_DIR="$HOME/Library/Application Support/Steam/steamapps/common/Football Manager 26"
./scripts/build_bridge.sh
./scripts/deploy_bridge.sh
```

`FM26_DIR` defaults to that same standard Steam path if you don't set it, so
you can omit the `export` if your install is there. `build_bridge.sh`
compiles `bridge/FMBridge` against the BepInEx core/interop assemblies under
`$FM26_DIR/BepInEx`; `deploy_bridge.sh` copies the resulting `FMBridge.dll`
into `$FM26_DIR/BepInEx/plugins/FMBridge/` and records where `node` and
this repo live so the bridge can start the chat service with the game
(step 4).

Restart FM26 (via the arm64 launcher from step 1). Load into a career, then
check `BepInEx/LogOutput.log` for a line like:

```
[Info   :Bridge] loaded, pump=MainThreadTick (DadMych fork)
```

That confirms the plugin loaded and is ticking on the main thread. The
bridge listens on `ws://127.0.0.1:7777`.

## 3. Build the MCP server

```bash
cd mcp/fm-dof-mcp
npm install
npm run build
```

This compiles TypeScript to `dist/index.js`. The server talks to the bridge
over the same local WebSocket, so FM26 needs to be running (with a career
loaded) for tool calls to return real data — the server itself will start
without it, but tool calls will fail until the bridge is reachable.

## 4. Open the in-game chat

The chat service starts automatically with the game: `deploy_bridge.sh`
(step 2) wrote a small `chat_service.env` next to the deployed plugin, and
the bridge uses it to launch `scripts/dof_chat_service.mjs` when FM26
starts — and stops it again when the game exits. The service answers via
the selected headless CLI on its existing login. Codex with `gpt-5.6-luna`
is the default. To use Claude Code instead, deploy with:

```bash
DOF_CHAT_PROVIDER=claude ./scripts/deploy_bridge.sh
```

Use `DOF_CHAT_PROVIDER=codex` to select Codex explicitly. The two providers
keep separate conversation session files, so switching between them cannot
resume a thread through the wrong CLI.

For a manual start, use the same environment variable:

```bash
DOF_CHAT_PROVIDER=claude node scripts/dof_chat_service.mjs
```

Launch FM26 (via the arm64 launcher from step 1), load a career, then open
**Recruitment → Chat with DoF** in the sidebar navigation. Type a question,
and the reply arrives as a chat bubble — expect roughly 10–30 seconds for
tool-heavy questions; the DoF is really reading your save. "New chat"
starts a fresh conversation; conversation memory otherwise persists across
questions and game sessions.

The service's log lives next to the plugin:
`$FM26_DIR/BepInEx/plugins/FMBridge/chat_service.log`. You can also run
the service by hand from the repo root — `node scripts/dof_chat_service.mjs`
— for example if autostart was disabled (no `node` on PATH at deploy time)
or you want the log in your terminal; a localhost lock port guarantees only
one instance ever serves the chat. If you move this repo, rerun
`deploy_bridge.sh` so `chat_service.env` points at the right place.

The panel shows a short status line while the DoF digs through tools. Claude
Code streams token-by-token; Codex replaces status/intermediate messages with
the completed reply. Provider defaults are `gpt-5.6-luna` for Codex and
`sonnet` for Claude. Set `DOF_CHAT_MODEL` before `scripts/deploy_bridge.sh`
(or a manual start) to override the selected provider's model.

The panel lives only in the running game session — it is never written
into your save.

The chat runs on its own dedicated system prompt — `mcp/fm-dof-mcp/prompts/dof-persona.md`
plus `mcp/fm-dof-mcp/prompts/dof-chat-style.md`, composed at service startup
— rather than the Codex or Claude Code CLI's default agent prompt, so the
DoF never answers like a coding assistant. Editing either file changes how
the DoF talks and what it does or doesn't know about on the next service
start (a game restart, or a manual restart of `dof_chat_service.mjs`).

## Optional: point an external MCP client at it

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

The server exposes a `dof-persona` MCP prompt (`mcp/fm-dof-mcp/prompts/dof-persona.md`).
Most MCP clients let you insert a server-provided prompt into the
conversation (in Claude Code, it shows up as a slash command named after the
server and prompt, e.g. `/fm-dof-mcp:dof-persona`). Load it at the start of
a session so the model reasons only from what the tools return for your
save, not from general football knowledge.

### First-run smoke test

With FM26 running and a career loaded, ask your MCP client to call the
`game_status` tool (or just ask "what's the game status?" once the
dof-persona prompt or server is loaded). A healthy response looks like:

```json
{ "processing": false, "date_iso": "2025-11-01 09:00", "continue_state": "CanContinue" }
```

If that comes back, the whole chain — bridge plugin, WebSocket, MCP server,
MCP client — is working. From there, try `my_club`, `squad_report`, or
`query_players`.

## Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| MCP client can't start the server / immediate exit | Check `npm run build` succeeded and `dist/index.js` exists; run `node dist/index.js` directly to see startup errors. |
| Tool calls time out or error connecting | FM26 isn't running, no career is loaded, or the bridge plugin didn't load — check `BepInEx/LogOutput.log` for the `[Bridge] loaded, pump=...` line. |
| `pump=win-x64 Harmony pump` / "not implemented yet" warning | You're not on macOS, or `MainThreadTickPump.IsSupported` returned false. This project only supports macOS arm64 today. |
| BepInEx never loads at all (no `LogOutput.log`, instant crash, or game exits after ~30s at the main menu) | This is a BepInEx/macOS issue, not an fm-dof-mcp one — see the troubleshooting table in [DadMych/fm26-player-export-macos](https://github.com/DadMych/fm26-player-export-macos#troubleshooting). |
| `build_bridge.sh` fails looking for BepInEx assemblies | Confirm `$FM26_DIR/BepInEx/core` and `$FM26_DIR/BepInEx/interop` exist — if not, step 1 didn't complete successfully. Note: always build via `./scripts/build_bridge.sh` — a bare `dotnet build` can't find the BepInEx assemblies (the script passes their paths to the compiler; `FM26_DIR` alone isn't enough). |
| `game_status` returns but the date never matches your actual save | You're pointed at the wrong `FM26_DIR`, or a stale plugin copy is deployed — rerun `deploy_bridge.sh` after any rebuild. |
| Chat panel never appears under Recruitment | Check `chat_service.log` next to the deployed plugin — it should say `bridge connected` then `overlay up, menu armed`. If the log is missing, autostart didn't run: check `BepInEx/LogOutput.log` for `[Bridge] chat service` lines and rerun `deploy_bridge.sh`. The menu row is injected when the Recruitment dropdown exists; navigate to a main squad screen once and reopen the dropdown. |
| Chat replies with "couldn't reach my desk" | The selected `codex` or `claude` CLI is not on the PATH recorded at deploy time, is not logged in, or the MCP server isn't built (step 3) — check `chat_service.log` for the provider exit message. |
