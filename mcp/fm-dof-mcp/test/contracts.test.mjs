import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "..");
const read = (file) => readFileSync(join(root, file), "utf8");

test("query_players exposes bounded age and position filters", () => {
  const mcp = read("mcp/fm-dof-mcp/src/index.ts");
  const bridge = read("bridge/FMBridge/src/World/QueryPlayers.cs");
  assert.match(mcp, /age_min: z\.number\(\)\.int\(\)\.min\(0\)\.max\(120\)/);
  assert.match(mcp, /age_max: z\.number\(\)\.int\(\)\.min\(0\)\.max\(120\)/);
  assert.match(mcp, /positions: z\.array\(z\.string\(\)\.min\(1\)\)/);
  assert.match(bridge, /private static readonly string\[\] FilterProps = \{ "Age", "Position" \}/);
  assert.match(bridge, /PositionDecode\.MatchesSlot\(actualRaw, wanted\)/);
  assert.match(bridge, /private const int FilterBatchSize = HardEnrichMax/);
  assert.match(bridge, /\["candidate_window"\] = new JsonObject/);
  assert.match(bridge, /BatchEnrich\(queue, chunk, null, true\)/);
  const enqueue = bridge.slice(bridge.indexOf("public static async Task<JsonObject> Enqueue"));
  assert.ok(enqueue.indexOf("BatchEnrich(queue, chunk, null, true)") < enqueue.indexOf("ReadTransferValueTexts"));
});

test("position matching has executable standalone coverage", () => {
  const project = read("bridge/FMBridge/tests/PositionDecodeTests.csproj");
  const tests = read("bridge/FMBridge/tests/Program.cs");
  assert.match(project, /PositionDecode\.cs/);
  assert.match(tests, /ST\(C\) should match ST\(C\)/);
  assert.match(tests, /D\(C\) should match D\(LC\)/);
  assert.match(tests, /invalid labels must not match/);
});

test("read_entity and persona bound player fan-out", () => {
  const mcp = read("mcp/fm-dof-mcp/src/index.ts");
  const bridge = read("bridge/FMBridge/src/World/ReadEntity.cs");
  const persona = read("mcp/fm-dof-mcp/prompts/dof-persona.md");
  assert.match(mcp, /z\.array\(z\.number\(\)\.int\(\)\)\.min\(1\)\.max\(20\)/);
  assert.match(bridge, /private const int MaxUids = 20;/);
  assert.match(persona, /are capped at 20 ids/);
});

test("agent timeout cleanup and stale-session retry are classified separately", () => {
  const service = read("scripts/dof_chat_service.mjs");
  assert.match(service, /detached: true/);
  assert.match(service, /process\.kill\(-child\.pid, "SIGTERM"\)/);
  assert.match(service, /process\.kill\(-child\.pid, "SIGKILL"\)/);
  assert.match(service, /if \(sessionId && isStaleResumeError\(err \+ buf\)\)/);
  assert.doesNotMatch(service, /if \(sessionId\) \{\s*\/\/ stale/);
});

test("bridge serializes UI-backed operations across clients", () => {
  const server = read("bridge/FMBridge/src/Voice/VoiceServer.cs");
  assert.match(server, /_uiOperationGate = new SemaphoreSlim\(1, 1\)/);
  assert.match(server, /await _uiOperationGate\.WaitAsync\(_cts\.Token\)/);
  assert.match(server, /if \(needsUiGate\) _uiOperationGate\.Release\(\)/);
});

test("navigation and overlay mutations run at a safe frame boundary and fail closed", () => {
  const plugin = read("bridge/FMBridge/src/BridgePlugin.cs");
  const nav = read("bridge/FMBridge/src/World/Navigator.Nav.cs");
  const overlay = read("bridge/FMBridge/src/World/UiInject.cs");
  assert.match(plugin, /UIElementsRuntimeUtility/);
  assert.match(plugin, /nameof\(BeforeRepaintPanels\)/);
  assert.match(nav, /mutation-aborted/);
  assert.match(nav, /recovering from a failed panel mutation via PortalScreen/);
  assert.match(overlay, /FindByName\(root, OverlayName, 0\) \?\? ReattachCachedOverlay\(root\)/);
  assert.match(overlay, /live\.BringToFront\(\)/);
});

test("transfer values are uid anchored and ambiguous UI row mappings fail closed", () => {
  const bridge = read("bridge/FMBridge/src/World/QueryPlayers.cs");
  const persona = read("mcp/fm-dof-mcp/prompts/dof-persona.md");
  assert.match(bridge, /globalIndexByUid\[uid\] = AsInt\(ro\["index"\]\)/);
  assert.match(bridge, /transferValueTexts\.TryGetValue\(ctx\.Uid/);
  assert.match(bridge, /ordered\.Count != winCount/);
  assert.match(bridge, /afterStart != winStart \|\| afterCount != winCount/);
  assert.match(bridge, /verifiedValue\["uid_verified"\] = ctx\.Uid/);
  assert.match(persona, /uid_verified.*equal to that player's uid/);
});
