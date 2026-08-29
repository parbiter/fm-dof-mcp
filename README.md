# fm-dof-mcp

An advise-only **Director of Football you chat with inside Football
Manager 26**. A "Chat with DoF" entry appears in the game's Recruitment
menu and opens a chat panel docked over your running career: ask it
football questions — squad depth, transfer targets, wage headroom — and
its answers are backed by live reads from the game itself, not guesses.

It is three pieces:

- **`bridge/`** — a [BepInEx 6](https://docs.bepinex.dev/) IL2CPP plugin
  that runs inside FM26. It draws the chat UI (menu entry, panel, bubbles)
  and exposes a small, read-mostly set of operations over a localhost
  WebSocket (game state, entity reads, squad/player queries, and shortlist
  management — nothing that saves or advances the game).
- **`mcp/fm-dof-mcp/`** — an [MCP](https://modelcontextprotocol.io) server
  (Node/TypeScript) that turns those operations into 8 MCP tools plus a
  `dof-persona` prompt. This is the DoF's entire tool surface — the chat
  runs on it, and you can also point any external MCP client at it (see
  below).
- **`scripts/dof_chat_service.mjs`** — the host-side service that connects
  the two: it polls the panel for what you typed, answers via a headless
  Codex or Claude Code session with the MCP tools attached, and posts the
  reply back as a chat bubble. Codex with GPT-5.6 Luna is the default; the
  bridge launches the selected provider automatically once deployed.

## Demo

> "Find me a left-back to replace Estupiñán, under €25M, who'd accept our
> wages." → argued recommendations with real numbers → "shortlist the top
> five" → the shortlist appears in the running game.

That's the shape of a session. A condensed excerpt from a real run, over the
actual 8-tool MCP surface, against a live (never saved or advanced) career:

```
> game_status
{ "processing": false, "date_iso": "2025-11-01 09:00", "continue_state": "CanContinue" }

> my_club
{ "budgets": { "transfer_budget": 17537989 },
  "wages": { "wage_budget_used_pct": 95.21 } }
```

Reading this honestly: the stated transfer budget is below the manager's
€25M ceiling, and wages are already at 95.2% of budget — any incoming
signing is effectively paid for by outgoing wages, not fresh headroom.

```
> get_role_attributes { "position": "D (L)" }
{ "role_name": "Wing Back",
  "key_attributes": ["Crossing","Marking","Tackling","Teamwork","Work Rate","Acceleration","Stamina","Pace"] }

> squad_report { "squad": "first" }
# Estupiñán: uid 32444, PPA 146, wage €4.63M p/a — the baseline to beat

> query_players { "max": 900, "filters": { "scouted_only": true }, "enrich": true, "enrich_max": 50 }
# 4 genuine D(L)/WB(L) matches inside the enrich window, e.g.:
{ "uid": 31521, "name": "Raphaël Guerreiro", "perceived_potential_ability": 148,
  "wage": { "display": "€5.34M - €6.96M p/a" },
  "transfer_value": { "display": "€8.8M - €10.5M", "sort": 9650000 } }
```

The recommendation that followed argued each name on its own numbers —
"pursue now," "flag the price," "monitor, don't commit," "watchlist only,
over budget" — never a bare "good player." Then, on request:

```
> shortlist { "action": "create", "name": "DoF Demo" }
> shortlist { "action": "add", "uid": 31521, "name": "DoF Demo" }
...
> shortlist { "action": "list" }
{ "selection": "DoF Demo | 5 Players", "uids": [31521, 9793, 29291, 23991, 12117] }
```

Five uids, verified present in the running game's own shortlist — the same
check any MCP client would run, no internal cross-check needed. The game's
in-game date was identical before and after: nothing was saved or advanced.

A captured demo video/GIF of a full session is coming — this section will be
updated with it.

## How the chat works

The in-game panel is deliberately dumb — it renders bubbles and collects
typed text, with a thinking indicator while the DoF works and a "New chat"
button to start over. `scripts/dof_chat_service.mjs` runs on the host,
polls the panel over the same localhost WebSocket, answers via headless
Codex or Claude Code with the MCP tools attached (auth rides on the selected
CLI's existing login), and posts the reply back. Choose with
`DOF_CHAT_PROVIDER=codex|claude`; Codex is the default. Once
`deploy_bridge.sh` has run, the bridge starts the service automatically with
the game and stops it on exit — there is no manual step. The chat's tools are
the MCP server's tools, nothing more — the advise-only surface below applies to
everything the DoF can do. See
[`docs/INSTALL.md`](docs/INSTALL.md#4-open-the-in-game-chat) for setup.

## Using it from an external MCP client (optional)

The same MCP server the chat runs on can be attached to any MCP client —
Codex, Claude Code, Claude Desktop, or anything else that speaks MCP — for
longer written analysis outside the game window. The demo transcript above is
exactly that surface; see
[`docs/INSTALL.md`](docs/INSTALL.md#optional-point-an-external-mcp-client-at-it)
for client wiring.

## Design principles

- **Advise-only.** There is no tool that saves the game, advances time, or
  drives arbitrary UI. The action surface is exactly one thing: shortlist
  management (`create`/`add`/`remove`), and only when explicitly asked.
  Recommending, arguing, and deciding stay separated from acting. (The
  in-game chat overlay adds UI elements of its own to draw bubbles in —
  it never reads or drives the game's screens.)
- **Honest data, not guesses.** Every claim traces to a tool response. Reads
  report `found:false` rather than fabricating plausible-looking values for
  something that doesn't resolve; a `null` transfer value is reported as
  "unknown or not for sale," never silently treated as zero or as "cheap."
- **No save/continue/screen-driving verbs, by construction.** This isn't a
  policy enforced by a persona prompt — the server literally does not
  register those tools, and the bridge itself doesn't compile the
  corresponding dispatch code into the shipped DLL. A misbehaving or
  jailbroken client still can't reach a save/advance/raw-UI-drive path,
  because it doesn't exist in this binary.
- **Vision = scout, not oracle.** The bundled `dof-persona` prompt instructs
  the model to reason only from what the tools actually return for this
  save, never from real-world football knowledge or memory of other saves —
  FM26 changed its own tactical role system enough that outside priors are
  often simply wrong for this version.

## Platform support (read this first)

This is experimental, single-platform software:

- **macOS, Apple Silicon (arm64) only.** No Windows or Intel Mac support.
- Requires **BepInEx 6 IL2CPP**, which is itself bleeding-edge/unstable for
  this game. Expect friction.
- Not affiliated with, endorsed by, or supported by SEGA or Sports
  Interactive. Football Manager is a trademark of Sports Interactive/SEGA.
  Use at your own risk against your own game install.

## Install

See [`docs/INSTALL.md`](docs/INSTALL.md) for the full walkthrough: BepInEx
setup, building and deploying the bridge, building the MCP server, and
opening the in-game chat.

## License

MIT — see [`LICENSE`](LICENSE).
