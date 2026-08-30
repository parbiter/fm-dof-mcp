#!/usr/bin/env node
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import { BridgeClient, BridgeRpcError, BridgeUnavailableError } from "./bridge-client.js";
import fm26Roles from "./data/fm26-roles.json" with { type: "json" };

// This server is a dumb proxy onto the in-game WebSocket bridge (fm-bridge).
// No game logic, no orchestration — every tool below is a ~1:1 mapping onto a
// bridge RPC method (the only exceptions being thin merges/multiplexers of
// bridge verbs onto a smaller surface). Planning/orchestration lives entirely
// on the MCP client (LLM) side.
//
// This is the advise-only Director of Football surface: read-only/advisory
// tools plus shortlist management. There is no game-advancing or raw-screen-
// driving verb — that is a deliberate safety property, not an oversight.

const bridge = new BridgeClient();

function textResult(value: unknown): CallToolResult {
  const text = typeof value === "string" ? value : JSON.stringify(value, null, 2);
  return { content: [{ type: "text", text }] };
}

function errorResult(message: string): CallToolResult {
  return { content: [{ type: "text", text: message }], isError: true };
}

/** Calls a bridge method and converts failures into MCP tool errors. */
async function callBridge(
  method: string,
  args: Record<string, unknown> = {},
): Promise<CallToolResult> {
  try {
    const result = await bridge.call(method, args);
    return textResult(result);
  } catch (err) {
    if (err instanceof BridgeUnavailableError) {
      return errorResult(err.message);
    }
    if (err instanceof BridgeRpcError) {
      return errorResult(`${err.method} failed: ${err.message}`);
    }
    return errorResult(err instanceof Error ? err.message : String(err));
  }
}

const server = new McpServer({ name: "fm-dof-mcp", version: "0.1.0" });

// ---------------------------------------------------------------------------
// Persona prompt: the canonical text lives in prompts/dof-persona.md (also
// readable directly as a file); registered here via the MCP prompts
// capability so any MCP client can pull it over the protocol.
// ---------------------------------------------------------------------------

const DOF_PERSONA_MD = readFileSync(
  join(dirname(fileURLToPath(import.meta.url)), "..", "prompts", "dof-persona.md"),
  "utf8",
);

server.registerPrompt(
  "dof-persona",
  {
    title: "Director of Football persona",
    description:
      "Advise-only Director of Football persona: vision=scout (reason only from tool output, never " +
      "real-world football priors), how to argue recommendations with uids/numbers/role attributes, " +
      "and the practical tool flow. Load this before acting as the DoF.",
  },
  async () => ({
    messages: [{ role: "user", content: { type: "text", text: DOF_PERSONA_MD } }],
  }),
);

// ---------------------------------------------------------------------------
// Core loop
// ---------------------------------------------------------------------------

server.registerTool(
  "game_status",
  {
    description:
      "Cheap liveness/state snapshot of the running career: date_iso (current in-game date) plus " +
      "processing/continue flags. Call this first in any turn to orient yourself. continue_state is " +
      "the agent-loop dispatcher: 'CanContinue' means the game itself is free to progress time; any " +
      "other value names the blocking activity that must be handled first (observed: 'NeedAction' = " +
      "inbox response required, 'PressConference', 'Tactics' = matchday team selection). " +
      "continue_label is the continue button's display text (e.g. 'Attend Press Conference', " +
      "'Review Team Selection'). This tool never advances the game — it only reports state.",
    inputSchema: {},
  },
  async () => callBridge("game_status"),
);

// ---------------------------------------------------------------------------
// Data reads
// ---------------------------------------------------------------------------

const MANIFEST_ON_READ_NOTE =
  "Discovery is manifest-on-read: calling WITHOUT 'sections' returns the entity's identity plus an " +
  "'available_sections' manifest naming everything readable for this kind — read the manifest " +
  "first, then request the section names you actually need (batch several uids in one call). " +
  "'props' is the raw escape hatch: direct property reads outside the section vocabulary.";

const UID_SCHEMA = z
  .union([z.number().int(), z.array(z.number().int()).min(1).max(20)])
  .describe("Entity uid: a single id number, or an array of at most 20 ids for a bounded batch read");
const SECTIONS_SCHEMA = z
  .array(z.string())
  .min(1)
  .optional()
  .describe("Section names to fetch (discover them via 'available_sections' from a call without sections)");
const PROPS_SCHEMA = z
  .array(z.string())
  .min(1)
  .optional()
  .describe("Raw property names to read directly (escape hatch when no section covers what you need)");

const READ_ENTITY_KIND_LEAD =
  "Read an entity by its unique id(s), selected via 'kind': person (players, staff, managers, " +
  "board members — identity plus grouped data such as attributes, contracts, history), club " +
  "(identity plus grouped data such as finances, staff and squad membership), nation (identity " +
  "plus grouped national-team/league data), or competition (identity plus grouped data such as " +
  "stages and season structure).";
const REF_UID_NOTE =
  "$ref values in the result now carry the referenced entity's 'uid' (and 'table'), so they can " +
  "be fed straight into another read_entity call without a separate lookup.";
const FOUND_FALSE_NOTE =
  "Each entity's 'found' is a genuine resolution check, not just 'did any prop come back': a " +
  "nonexistent uid returns found:false with an 'error' explaining why (checked via the game's own " +
  "IsValid flag where available, falling back to a Name sanity check the bridge requests " +
  "automatically — sections-only reads validate too) instead of " +
  "found:true with garbage placeholder values — this call never guesses. A 'Position' prop on a " +
  "person also comes back with an additive 'position_decoded' sibling (e.g. \"D (RLC)\", " +
  "\"AM (RL)\", \"ST (C)\") alongside the raw bitmask. Every response — success or found:false — " +
  "carries 'latency_ms' for the round trip.";
const LOAN_NOTE =
  "Requesting a person's 'OnLoanFrom' or 'LoanContract' prop (directly, via 'props', or via the " +
  "contract section) auto-attaches a 'Loan' object alongside the normal contract data: " +
  "{status: \"loaned_in\"|\"loaned_out\"|null, club: <other club name or null>, " +
  "until: <return/expiry date or null>, recallable: <bool or null>}. status:null means the player " +
  "is not on loan at all. \"loaned_in\" means the human club is BORROWING this player — they " +
  "belong to 'club' and are not the human club's to sell (advise sending them back early or not " +
  "making the move permanent, never a sale); \"loaned_out\" means the human club still OWNS this " +
  "player but has sent them away to 'club' — they can still be sold, and can only be recalled " +
  "early when 'recallable' is true. 'recallable' has no discoverable binding in-game and is " +
  "currently always null — treat null as unknown, never as false.";

server.registerTool(
  "read_entity",
  {
    description: `${READ_ENTITY_KIND_LEAD} ${MANIFEST_ON_READ_NOTE} ${REF_UID_NOTE} ${FOUND_FALSE_NOTE} ${LOAN_NOTE}`,
    inputSchema: {
      kind: z.enum(["person", "club", "nation", "competition"]).describe("Entity kind to read"),
      uids: UID_SCHEMA,
      sections: SECTIONS_SCHEMA,
      props: PROPS_SCHEMA,
    },
  },
  async ({ kind, uids, sections, props }) =>
    callBridge("read_entity", {
      kind,
      uids: Array.isArray(uids) ? uids : [uids],
      ...(sections ? { sections } : {}),
      ...(props ? { props } : {}),
    }),
);

server.registerTool(
  "my_club",
  {
    description:
      "One-call standing context for the human manager's own club: identity, budgets " +
      "(transfer budget, next-season transfer budget, overall balance), wages, financial status, " +
      "and the Human*Budget reallocation deltas. Every money field is unit-labeled in its own key " +
      "name (never a bare ambiguous number). The target club is discovered dynamically (via the " +
      "game's own 'Human.Club' binding) — no uid lookup needed. Wage figures are calibrated: " +
      "'committed_weekly_eur' / 'current_weekly_eur' are the game's WEEKLY (p/w) scalars at source " +
      "(committed wage spending is weekly, not annual — confirmed live against ground truth); " +
      "'*_annual_estimate_eur' is that weekly figure x52 (a cheap estimate); " +
      "'current_wage_total_annual_eur' and 'wage_budget_annual_display' are the game's own ANNUAL " +
      "(p/a) ground truth chain-read off Club.WagesData (the same binding the in-game Finances > " +
      "Wages tile displays), live cross-checked against the squad's summed wage bill. See " +
      "'wages.units_note' in the response for the full explanation.",
    inputSchema: {
      club_uid: z
        .number()
        .int()
        .optional()
        .describe(
          "Override the dynamically-discovered human club uid. Documented fallback only — leave " +
            "unset to let the bridge discover the human manager's club automatically.",
        ),
    },
  },
  async ({ club_uid }) => callBridge("my_club", club_uid !== undefined ? { club_uid } : {}),
);

server.registerTool(
  "squad_report",
  {
    description:
      "One-call structured depth-chart for one of the human club's teams: per player, name, age, " +
      "position (raw bitmask plus an additive 'position_decoded' compact label, e.g. \"D (RLC)\", " +
      "\"AM (RL)\", \"ST (C)\"), position_familiarity (~1-20 per position: how NATURAL the player is " +
      "in that slot — 20 means plays-there-natively, NOT a quality/ability score; use it to filter " +
      "candidates, never to rank them), perceived potential ability, wage (display string + raw " +
      "sortable number), contract (end date, length, days elapsed), and loan " +
      "{status: \"loaned_in\"|\"loaned_out\"|null, club: <other club name or null>, " +
      "until: <return/expiry date or null>, recallable: <bool or null>}. status:null means not on " +
      "loan. \"loaned_in\" players are on loan FROM 'club' — they belong to someone else and are " +
      "not this club's to sell (advise sending back early / not making it permanent instead). " +
      "\"loaned_out\" players are still owned by this club but currently away at 'club' — they can " +
      "still be sold, and can only be recalled before their loan ends when 'recallable' is true " +
      "(it has no discoverable binding in-game and is currently always null — treat null as " +
      "unknown, never as false). Players out on loan are frequently NOT part of the team's own " +
      "roster binding (they sit on their loan destination's team instead), so this report also " +
      "returns a separate top-level 'loaned_out' array (same row shape, plus 'loaned_out_count') " +
      "covering every player the club has out on loan regardless of which team they currently sit " +
      "on — check it in addition to 'players' before concluding a loaned-out player doesn't exist. " +
      "The target club is discovered " +
      "dynamically like my_club unless club_uid overrides it. 'squad' selects which team via FM's own " +
      "club-level team reference (not a guessed age band — naming/tiering conventions vary by " +
      "club/country): 'first' (default, MainTeam), 'b' (HighestLevelYouthTeam — for many clubs this " +
      "is actually the reserve/B side, e.g. Milan's 'Casciavit B'), 'youth' (YouthTeam — the club's " +
      "youth squad, e.g. Milan's 'Milan U20s'). FM exposes only these two non-first-team refs " +
      "headlessly; the response's 'team.name'/'team.prop' always echo the real in-game name and " +
      "property so you can see exactly which squad you got rather than trusting the selector label " +
      "alone.",
    inputSchema: {
      club_uid: z
        .number()
        .int()
        .optional()
        .describe(
          "Override the dynamically-discovered human club uid. Documented fallback only — leave " +
            "unset to let the bridge discover the human manager's club automatically.",
        ),
      squad: z
        .enum(["first", "b", "youth"])
        .default("first")
        .describe("Which squad/team to report on (default 'first'); see description for mapping"),
    },
  },
  async ({ club_uid, squad }) =>
    callBridge("squad_report", { ...(club_uid !== undefined ? { club_uid } : {}), squad }),
);

server.registerTool(
  "query_players",
  {
    description:
      "One-call query against FM's full worldwide Player Database (~31k players): drives the " +
      "Recruitment > Player Database screen internally (from whatever screen the game is currently " +
      "on) and returns a uid list off its results table, respecting 'max' as the candidate-window " +
      "limit (not a promise to scan the entire database). Filtering support is " +
      "intentionally limited and NESTED under 'filters': the game's full condition-editor ('Edit " +
      "Search' > Add Condition, where contract-expiry and market-value RANGE filters would live) " +
      "opens but never renders a usable condition-type picker under UI-click simulation — likely a " +
      "native dropdown overlay outside the reachable UI tree. 'age_min', 'age_max', and 'positions' " +
      "are genuine post-table data filters: the bridge obtains age/position from the game in safe " +
      "chunks, filters that candidate window before returning and before transfer-value scraping, " +
      "and reports candidate-window truncation honestly in candidate_window (requested_max, available, " +
      "scanned, matched, truncated). 'filters.scouted_only' is the only UI " +
      "toggle; it is applied, read, and then reverted to its prior state before the call " +
      "returns (see 'reverted'/'revert_note' in the response). Optional 'enrich' reads a small " +
      "per-uid detail batch (age, position — plus an additive 'position_decoded' compact label — " +
      "perceived potential ability, wage, contract end date, and transfer_value " +
      "{display, sort, source:'player-database-ui', uid_verified} " +
      "where the game exposes a value) for the first 'enrich_max' returned uids using an overlapped " +
      "batch read — still noticeably slower than the base uid list, so keep 'enrich_max' small. " +
      "transfer_value gates honestly: a player marked 'Not for Sale' yields sort:null — do NOT " +
      "treat null as zero, it means no market value is being quoted, not 'worth nothing'. A value is " +
      "safe to attribute only when uid_verified matches that enriched row's uid; ambiguous UI row " +
      "alignment is omitted as missing instead of guessed. Enrich is " +
      "capped at 50 and a truncated enrich is never silent: the response always includes " +
      "'enrich_requested' (what you asked for, pre-cap), 'enrich_done' (how many actually came " +
      "back), 'enrich_truncated' (bool), and 'enrich_cap' (the hard ceiling, currently 50) — silent " +
      "truncation was deliberately eliminated.",
    inputSchema: {
      max: z
        .number()
        .int()
        .min(1)
        .max(2000)
        .default(200)
        .describe("Max number of player uids to return (default 200, hard cap 2000)."),
      filters: z
        .object({
          scouted_only: z
            .boolean()
            .optional()
            .describe("Restrict to scouted players only (the only proven-drivable filter toggle)."),
          age_min: z.number().int().min(0).max(120).optional().describe("Inclusive minimum age, applied to game-returned player data."),
          age_max: z.number().int().min(0).max(120).optional().describe("Inclusive maximum age, applied to game-returned player data."),
          positions: z.array(z.string().min(1)).min(1).max(20).optional().describe("Accepted FM position labels, e.g. ['D (R)', 'D (C)']; matched against the decoded position label."),
        })
        .optional()
        .describe("Filters applied after game data is available; scouted_only is the UI toggle, age/position are post-table data filters."),
      enrich: z
        .boolean()
        .optional()
        .describe("If true, also read a small per-player detail batch for the top uids returned."),
      enrich_max: z
        .number()
        .int()
        .min(1)
        .max(50)
        .default(10)
        .describe("How many of the returned uids to enrich when 'enrich' is true (default 10, cap 50)."),
    },
  },
  async ({ max, filters, enrich, enrich_max }) =>
    callBridge("query_players", {
      max,
      ...(filters !== undefined ? { filters } : {}),
      ...(enrich !== undefined ? { enrich } : {}),
      enrich_max,
    }),
);

type Fm26RolesData = {
  _meta: Record<string, unknown>;
  positions: Record<string, Array<Record<string, unknown>>>;
};
const ROLE_DATA = fm26Roles as Fm26RolesData;

/** "d(r)", "D (R)", " d r " all normalize to "D(R)" so position lookups are forgiving of spacing. */
function normalizePositionKey(s: string): string {
  return s.toUpperCase().replace(/\s+/g, "");
}
const POSITION_LOOKUP = new Map(
  Object.keys(ROLE_DATA.positions).map((k) => [normalizePositionKey(k), k]),
);

server.registerTool(
  "get_role_attributes",
  {
    description:
      "Static, game-sourced reference data for FM26 tactical roles — because FM26 overhauled its " +
      "role/duty system, any LLM's football-knowledge priors about role names, duties, or key " +
      "attributes are likely WRONG for this version. This data was harvested directly from FM26's " +
      "own Tactics Planner > Player Roles picker UI (see the returned '_meta' block for full harvest " +
      "method/caveats) — it records only what the game itself displayed, never filled in from " +
      "outside football knowledge. For each role this gives the exact role_name and the NAMES of the " +
      "attributes the game's 'Key Attributes' panel highlighted; 'preferable_attributes' is " +
      "explicitly 'missing' because FM26's UI shows only one attribute tier, not two. Numeric " +
      "attribute values and player-suitability ratings are NOT included here (those are per-player, " +
      "not role-intrinsic — read them live via read_entity/squad_report instead). Coverage: 9 of the " +
      "game's position groups (GK, D(R), D(C), D(L), DM, AM(R), AM(C), AM(L), ST(C)), 43 roles total; " +
      "M(C) and D/WB(L) were not reachable without changing the human manager's tactic and so are " +
      "absent (see '_meta.coverage_caveat' for why, spelled out in full). Call with no arguments to " +
      "list available position groups; pass 'position' (e.g. 'D (R)' — spacing/case insensitive) to " +
      "list that group's roles; pass 'role_name' (substring match) to find a role by name across all " +
      "position groups; pass both to scope a role search to one group.",
    inputSchema: {
      position: z
        .string()
        .optional()
        .describe("Position group key, e.g. 'GK', 'D (R)', 'AM (C)', 'ST (C)' (case/spacing insensitive)"),
      role_name: z
        .string()
        .optional()
        .describe("Role name substring to search for, e.g. 'Wing Back' or 'playmaker' (case-insensitive)"),
    },
  },
  async ({ position, role_name }) => {
    if (!position && !role_name) {
      return textResult({
        meta: ROLE_DATA._meta,
        available_positions: Object.keys(ROLE_DATA.positions),
      });
    }

    let positionKeys = Object.keys(ROLE_DATA.positions);
    if (position !== undefined) {
      const matchedKey = POSITION_LOOKUP.get(normalizePositionKey(position));
      if (!matchedKey) {
        return errorResult(
          `Unknown position '${position}'. Available: ${Object.keys(ROLE_DATA.positions).join(", ")}`,
        );
      }
      positionKeys = [matchedKey];
    }

    const needle = role_name?.toLowerCase();
    const results: Record<string, Array<Record<string, unknown>>> = {};
    for (const key of positionKeys) {
      const roles = ROLE_DATA.positions[key];
      const filtered = needle
        ? roles.filter((r) => String(r.role_name).toLowerCase().includes(needle))
        : roles;
      if (filtered.length > 0) results[key] = filtered;
    }

    if (Object.keys(results).length === 0) {
      return errorResult(
        `No roles found${role_name ? ` matching '${role_name}'` : ""}${position ? ` in position '${position}'` : ""}.`,
      );
    }

    return textResult({ harvest_date: ROLE_DATA._meta.harvest_date, positions: results });
  },
);

// ---------------------------------------------------------------------------
// Domain
// ---------------------------------------------------------------------------

// Read-only at the SCHEMA level, not just in prose: the 'respond' enum value
// is simply absent from the action enum, so the SDK's own zod validation
// rejects a respond call before the handler ever runs — the DoF is
// advise-only by construction, not by convention.
server.registerTool(
  "inbox",
  {
    description:
      "Interact with the FM inbox. action='list' returns recent messages; action='read' (requires " +
      "id) returns one message's full body including any response options it offers. This is an " +
      "advise-only surface: action='respond' does not exist here (schema-level, not just prose) — " +
      "the DoF never sends inbox replies.",
    inputSchema: {
      action: z.enum(["list", "read"]).describe("What to do in the inbox"),
      id: z
        .union([z.string(), z.number()])
        .optional()
        .describe("Message id (required for 'read')"),
    },
  },
  async ({ action, id }) => {
    if (action === "list") return callBridge("inbox_list");
    if (id === undefined) {
      return errorResult(`action='${action}' requires the message id`);
    }
    return callBridge("inbox_read", { mid: id });
  },
);

server.registerTool(
  "shortlist",
  {
    description:
      "Manage the human manager's transfer shortlist(s) by driving the game's own Shortlists " +
      "UI (Recruitment > Shortlists tab) headlessly — oracle-verified live, not a raw event " +
      "send. action='list' reads the currently-selected shortlist: its name/player-count " +
      "header and the full member list (uid per row). action='create' (requires name) opens " +
      "a new, empty shortlist with that name. action='add' (requires uid + name — the target " +
      "shortlist's name; optional duration, default 'Indefinitely') locates the player in the " +
      "Player Database (scrolling to it if needed) and adds them; if already a member, reports " +
      "that instead of erroring. action='remove' (requires uid; name is an optional hint only) " +
      "removes the player from whatever shortlist they're on via the confirmation dialog; if not " +
      "a member, reports that instead of erroring. Known limits: the Player Database's backing " +
      "table caps at 10,000 rows, so a uid outside that table fails honestly with an explicit " +
      "uid-not-found error rather than silently doing nothing (self-consistent — query_players' " +
      "recommendations come from the same table). A residual wrong-row flake exists: rarely, the " +
      "scroll lands on the wrong row and the tool detects the name mismatch and aborts honestly " +
      "(nothing is committed, the menu is closed) rather than acting on the wrong player — a " +
      "simple retry of the same call succeeds. Each add/remove is UI-driven and takes on the " +
      "order of seconds, not milliseconds — batch requests should budget for that. RISK: create/" +
      "add/remove are live in-game mutations — reversible in-game (shortlist membership can be " +
      "undone by removing/re-adding), but not undoable from here; do not save the game " +
      "immediately after testing these.",
    inputSchema: {
      action: z.enum(["list", "create", "add", "remove"]).describe("What to do with shortlists"),
      name: z
        .string()
        .optional()
        .describe(
          "Shortlist name — required for 'create' and 'add' (the target shortlist); " +
            "optional hint for 'remove'",
        ),
      uid: z.number().optional().describe("Player uid — required for 'add'/'remove'"),
      duration: z
        .string()
        .optional()
        .describe("Shortlist duration for 'add' (default 'Indefinitely')"),
    },
  },
  async ({ action, name, uid, duration }) => {
    if (action === "list") return callBridge("shortlist", { action: "list" });
    if (action === "create") {
      if (!name) return errorResult("action='create' requires 'name'");
      return callBridge("shortlist", { action: "create", name });
    }
    if (uid === undefined) {
      return errorResult(`action='${action}' requires 'uid'`);
    }
    if (action === "add" && !name) {
      return errorResult("action='add' requires 'name' (target shortlist name)");
    }
    return callBridge("shortlist", { action, uid, name, duration });
  },
);

async function main() {
  const transport = new StdioServerTransport();
  await server.connect(transport);
  process.stderr.write(`[fm-dof-mcp] server connected (stdio); bridge target: ` +
    (process.env.FM_BRIDGE_URL || "ws://127.0.0.1:7777/") + "\n");
}

main().catch((err) => {
  process.stderr.write(`[fm-dof-mcp] fatal: ${err instanceof Error ? err.stack ?? err.message : String(err)}\n`);
  process.exit(1);
});
