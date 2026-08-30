# fm-dof-mcp

An advise-only **Director of Football you chat with inside Football
Manager 26**. It's a mod for macOS (Apple Silicon) that adds a "Chat with
DoF" entry to the game's Recruitment menu and opens a chat panel docked
over your running career. Ask it football questions — squad depth,
transfer targets, wage headroom — and its answers are backed by live reads
from your actual save, not generic football takes. It never saves,
advances time, or does anything else on your behalf — it only talks.

## Example

> "Find me a left-back to replace Estupiñán, under €25M, who'd accept our
> wages."

This is from a real (never saved or advanced) career session, lightly
edited for length. The DoF checked the actual transfer budget and wage
bill first — in this save, wages were already at 95% of budget, so any
marquee signing would have to be paid for by outgoing wages, not fresh
room. It then pulled up scouted left-backs and wing-backs and argued each
one on its own numbers instead of a vague "he's decent": one flagged
"pursue now," another "monitor, don't commit," a third "watchlist only,
over budget." One recommendation, Raphaël Guerreiro, came back with his
actual wage range (€5.34M–€6.96M) and transfer value (€8.8M–€10.5M),
pulled straight from the game — and the in-game date was identical before
and after the conversation: nothing was saved or advanced.

A captured demo video/GIF of a full session is coming — this section will
be updated with it.

## Requirements at a glance

- **A Mac with Apple Silicon** (M1 or newer). No Windows, no Intel Mac —
  see [Platform support](#platform-support) below.
- **Football Manager 26** via Steam.
- Comfortable enough with Terminal to paste roughly fifteen commands
  across 8 steps — see [`docs/INSTALL.md`](docs/INSTALL.md) for every
  step, exact commands included.
- An account with **one** of the two AI providers the chat can use:
  [Claude Code](https://claude.com/claude-code) (needs an Anthropic
  account) or [Codex](https://developers.openai.com/codex/cli) (needs an
  OpenAI account). Either can involve a subscription or paid API usage —
  check the provider's own site for current pricing before you pick one.

## Install

```bash
git clone https://github.com/parbiter/fm-dof-mcp.git
cd fm-dof-mcp
```

Then see [`docs/INSTALL.md`](docs/INSTALL.md) for the full, numbered
walkthrough — installing the mod loader, building the two small helper
programs, signing in to an AI provider, and launching the game so the mod
actually loads.

## Playing with it

1. Launch Football Manager 26 through the special launcher script set up
   during install (`run_bepinex_arm64.sh`, placed inside your FM26 game
   folder) — not a plain double-click through Steam, which can skip the
   mod loader entirely. See
   [`docs/INSTALL.md`](docs/INSTALL.md#7-launch-the-game-so-the-mod-loads-2-min)
   for why and how.
2. Load into a career.
3. Open the **Recruitment** menu in the sidebar and click **Chat with
   DoF**.
4. Type a question and press Enter. A short status line shows while it
   works — tool-heavy questions take roughly 10–30 seconds.
5. Click **New chat** any time to start a fresh conversation; otherwise
   the DoF remembers earlier questions, even across game sessions.

## What it can and can't do

**Can:**

- Read your live save — squad, budgets, wages, scouted players, tactical
  roles, and more.
- Answer with real numbers pulled from your save, not generic takes.

**Can't:**

- Save your game, advance time, or continue to the next match/day.
- Make a transfer offer, negotiate, or take any action in the game beyond
  answering in chat.
- Click around the game's other screens for you.
- Make up a stat it doesn't have — if something isn't available, it says
  so instead of guessing.

## Privacy

Your save file itself is never uploaded anywhere. What does leave your Mac
is the text you type into the chat, plus whatever player and club data
your questions pull from the game (names, stats, wages, and similar) —
sent to whichever AI provider you chose (Anthropic for Claude Code, OpenAI
for Codex) so it can write the reply, the same way any normal use of that
provider's chat tool works. If you'd rather nothing about your save left
the machine, don't use the chat feature.

## For developers

The chat runs on a small [MCP](https://modelcontextprotocol.io) (Model
Context Protocol — the standard way AI tools connect to external data and
actions) server exposing 8 tools plus a `dof-persona` prompt. A
BepInEx plugin inside the game exposes the read-mostly game data over a
localhost WebSocket; the MCP server wraps that as tools; a small
host-side service runs the actual chat loop. You can point any
MCP-speaking client — Codex, Claude Code, Claude Desktop, or otherwise —
at the same server for longer written analysis outside the game window.
See [`docs/INSTALL.md`](docs/INSTALL.md#advanced-external-mcp-clients) for
client wiring.

<details>
<summary>Design principles</summary>

- **Advise-only.** There is no tool that saves the game, advances time, or
  drives arbitrary UI. Recommending, arguing, and deciding stay separated
  from acting. (The in-game chat overlay adds UI elements of its own to
  draw bubbles in — it never reads or drives the game's screens.)
- **Honest data, not guesses.** Every claim traces to a tool response.
  Reads report "not found" rather than fabricating plausible-looking
  values for something that doesn't resolve; an unknown transfer value is
  reported as "unknown or not for sale," never silently treated as zero
  or as "cheap."
- **No save/continue/screen-driving verbs, by construction.** This isn't a
  policy enforced by a prompt — the server literally does not register
  those tools, and the bridge itself doesn't compile the corresponding
  code into the shipped plugin. A misbehaving or jailbroken client still
  can't reach a save/advance/raw-UI-drive path, because it doesn't exist
  in this binary.
- **Vision = scout, not oracle.** The bundled persona prompt instructs the
  model to reason only from what the tools actually return for this save,
  never from real-world football knowledge or memory of other saves — FM26
  changed its own tactical role system enough that outside priors are
  often simply wrong for this version.

</details>

## Platform support

This is experimental, single-platform software:

- **macOS, Apple Silicon (arm64) only.** No Windows or Intel Mac support.
- Requires a community-made compatibility layer to run BepInEx (the mod
  loader) on this game and this chip at all — see
  [`docs/INSTALL.md`](docs/INSTALL.md) for details. Expect friction; this
  layer is itself new and can be unstable.
- Not affiliated with, endorsed by, or supported by SEGA or Sports
  Interactive. Football Manager is a trademark of Sports
  Interactive/SEGA. Use at your own risk against your own game install.

## License

MIT — see [`LICENSE`](LICENSE).
