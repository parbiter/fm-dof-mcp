# fm-dof-mcp

An advise-only **Director of Football** for Football Manager 26, exposed as
an [MCP](https://modelcontextprotocol.io) server. Point an MCP client (Claude
Code, Claude Desktop, or anything else that speaks MCP) at your running
career and ask it football questions — squad depth, transfer targets, wage
headroom — backed by live reads from the game itself, not guesses.

It is three pieces:

- **`bridge/`** — a [BepInEx 6](https://docs.bepinex.dev/) IL2CPP plugin that
  runs inside FM26 and exposes a small, read-mostly set of operations over a
  localhost WebSocket (game state, entity reads, squad/player queries, and
  shortlist management — nothing that saves or advances the game). It can
  also draw an optional in-game chat panel (see below).
- **`mcp/fm-dof-mcp/`** — an MCP server (Node/TypeScript) that turns those
  operations into 8 MCP tools plus a `dof-persona` prompt, and talks to the
  bridge over that same WebSocket.
- **`scripts/dof_chat_service.mjs`** — an optional host-side service that
  wires the in-game chat panel to a headless Claude Code session with the
  MCP tools attached, so you can talk to the DoF without leaving the game.

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

## In-game chat (optional)

Instead of (or alongside) an external MCP client, you can talk to the DoF
from inside FM26 itself. The bridge can inject a **"Chat with DoF"** entry
into the Recruitment navigation dropdown, which opens a chat panel docked
over the game: type a question, get the DoF's answer as a chat bubble, with
a thinking indicator while it works and a "New chat" button to start over.

The panel is deliberately dumb — it renders bubbles and collects typed text.
`scripts/dof_chat_service.mjs` runs on the host, polls the panel over the
same localhost WebSocket, answers via headless `claude -p` with the MCP
tools attached (auth rides on your existing Claude Code login), and posts
the reply back. The same advise-only surface applies: the chat's tools are
the MCP server's tools, nothing more. See
[`docs/INSTALL.md`](docs/INSTALL.md#7-in-game-chat-overlay-optional) for
setup.

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
wiring it into Claude Code or Claude Desktop.

## License

MIT — see [`LICENSE`](LICENSE).
