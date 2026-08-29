using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using SI.Bindable;
using SI.Bindable.Reference.Core;
using SI.Core;

namespace FMBridge.World;

/// <summary>
/// query_players verb: one call -> a uid list
/// off the Player Database's full worldwide player table, with an optional
/// small "enrich" step. Architecture decision (see class-level notes
/// below): IN-VERB UI-driving, not an LLM-facing recipe -- the nav ->
/// tab-click -> (optional toggle) -> list-read sequence proved fully
/// deterministic and cheap (all steps &lt;1s except the initial screen load),
/// so there was no brittleness forcing a fallback to a caller-driven recipe
/// of smaller verbs. The UI filter this verb can actually drive
/// ("scouted_only") is exposed as a plain boolean flag rather than a
/// free-form filter DSL, because live probing found the game's
/// own full condition-editor ("Edit Search" -> Add Condition) unreachable
/// via UI-Toolkit-tree click simulation (it is a native
/// dropdown/overlay, not part of the VisualElement tree the UiFind2/UiClick
/// walk covers). Age and position are instead post-table filters over the
/// bounded candidate window, using small game-data binding batches. Contract-
/// expiry and market-value RANGE filtering are therefore NOT supported by
/// this verb; per-row transfer-value text
/// (already a visible table column) can still be read via `enrich`.
///
/// Reach-reliability: every call re-drives navigation from scratch
/// (PortalScreen -> RecruitmentScreen -> Player Database tab) rather than
/// trusting whatever screen the caller left the game on, so it works
/// reliably from arbitrary screen state. The recipe itself
/// (open RecruitmentScreen -> SIButton index:1 click ->
/// list-read "playertable") was proven live, with a short main-thread poll
/// loop added at each step since a fresh caller state may need one real
/// screen-load frame that an already-warm screen doesn't.
///
/// Enrichment reuses the SAME overlapped batch-plant/poll PATTERN
/// SquadReport.cs uses (fabricate every player, plant Phase-1 props on all
/// of them, one shared poll loop, chain into Phase-2 FullContract props the
/// moment each player's FullContract lands, one shared poll loop, collect
/// all) -- not the same code (SquadReport's PlayerCtx/PropSlot are private
/// to that file), but the identical two-phase overlapped-fan-out shape, for
/// the same reason: N serial read_entity calls at ~2.6s/uid would dominate
/// latency for anything but a tiny `max`.
/// </summary>
internal static class QueryPlayers
{
    private const int PollIntervalMs = 200;
    private const int NavDeadlineMs = 6000;
    private const int FilterDeadlineMs = 4000;
    private const int EnrichPhaseWaitMs = 6000;
    private const int QueueTimeoutMs = 15000;
    private const int DefaultMax = 200;
    private const int MaxRows = 2000; // ListRead's own hard cap
    private const int DefaultEnrichMax = 10;
    // Earlier revisions silently clamped enrich_max
    // to 25 with no signal to the caller that a larger request had been cut
    // down. Raised to 50 -- live-measured latency for a 40-uid batch was well
    // under the old per-uid-serial-read cost thanks to the overlapped
    // two-phase batch plant below (see BatchEnrich); the response now always
    // reports enrich_requested/enrich_done/enrich_truncated/enrich_cap so a
    // silent cut is never possible again even if this constant changes again.
    private const int HardEnrichMax = 50;
    // Filtering uses the same upper bound as the proven-safe enrichment
    // plant. Chunks are fully collected (and their bindings closed) before
    // the next chunk starts; max never becomes one giant binding batch.
    private const int FilterBatchSize = HardEnrichMax;

    // Toggle-click retry tuning (repeat-call bug fix -- see the
    // ClickToggleWithRetry doc comment for the root cause).
    private const int ToggleClickDeadlineMs = 6000;
    // Checkbox-column activation marker (PollPlayerDatabaseActive): quick
    // probe for the fast-path (we're either already there or we're not),
    // longer allowance after a fresh tab click where the column can lag
    // the table itemCount by seconds mid-transition.
    private const int FastPathActiveCheckMs = 1500;
    private const int PdActiveDeadlineMs = 4000;
    private const int PdActiveRecheckSettleMs = 500;
    private const int PdActiveRecheckDeadlineMs = 1500;
    private const int ToggleSettleDelayMs = 500;
    // Longer settle before a revert RETRY attempt (live verification
    // found a silent stuck-toggle case) -- gives the UI more
    // breathing room than the normal post-click settle before trying again.
    private const int RevertRetrySettleMs = 1200;
    // Bumped from the original 4000ms budget shared with FilterDeadlineMs:
    // under back-to-back calls the uid-list read can lag a bit further
    // behind the toggle's visual settle than a single-shot call does.
    private const int UidListRetryDeadlineMs = 6000;

    // Outer wrapper name is the normally-clickable target. The inner name
    // (no "-5" suffix) is its child node at effectively the same on-screen
    // position (live-confirmed: outer x=1134,y=177,w=56,h=32;
    // inner x=1138,y=179,w=47,h=29) -- a sibling fallback for the rare case
    // the outer wrapper reports "zero-bounds" (a known Navigator.UiClick
    // failure mode for layout-collapsed pass-through nodes; the proven fix
    // is clicking a differently-named sibling at the same position).
    private static readonly string[] ScoutedToggleCandidates =
        { "inputs-switch-toggle-large-default-5", "inputs-switch-toggle-large-default" };

    // Phase-1 (one hop off the fabricated PersonReference) / Phase-2 (one hop
    // off the landed FullContract) prop sets for `enrich` -- deliberately a
    // small subset of SquadReport's list: just the essentials (age,
    // position fit, PA, wage, contract end), not the full 14-way Ability*
    // positional-fit family.
    private static readonly string[] Phase1Props = { "Name", "Age", "Position", "PerceivedPotentialAbility", "FullContract" };
    private static readonly string[] FilterProps = { "Age", "Position" };
    private static readonly string[] Phase2Props = { "Wage", "EndDate" };

    // ---------------------------------------------------- transfer_value
    // Live recon found the
    // per-row UI cell ("data_display-cell-icon-string-transfer-value-unpacked",
    // already a visible playertable column) renders real text for any
    // materialized row -- but there is no bound property/channel behind it
    // (it's computed purely for display), so unlike Wage/EndDate this can
    // only be read by scrolling the row into view and scraping the rendered
    // cell text, never via the Phase1/Phase2 plant/poll/collect channel
    // machinery above. Where the game genuinely has nothing to show (e.g.
    // "Not for Sale") that text is parsed and reported honestly (sort:null,
    // see ParseTransferValue) -- never guessed at.
    //
    // enrichUids[i] is exactly global table row index i (every playertable
    // row is a Person ref; `uids` is built from the SAME top-to-bottom
    // ListRead order enrichUids is sliced from), so the enrich batch's
    // transfer-value cells can be read by chunking ScrollToIndex + a
    // region-scoped UiFind2 scan across the render window instead of one
    // scroll per uid. Both readers below use only the bounded, single-call
    // Navigator.ScrollToIndex/GetVisibleWindow primitives added for the
    // shortlist fix -- never an unbounded scan (see Shortlist.cs class
    // doc for why that shape is forbidden here).
    private const string TransferValueCellName = "data_display-cell-icon-string-transfer-value-unpacked";
    private const int TransferValueWindowSettleMs = 3000;
    private const int TransferValueMaxChunks = 12;

    // Bounded chunked-uid-locate tuning for ReadTransferValueForUid (read_entity's
    // single-uid path) -- identical shape/reasoning to Shortlist.cs's
    // ScanChunkSize/ScanChunkDelayMs/ScanMaxChunks.
    private const int LocateScanChunkSize = 1000;
    private const int LocateScanChunkDelayMs = 120;
    private const int LocateScanMaxChunks = 60;

    /// <summary>
    /// Chunked reader used by query_players' enrich path: scrolls
    /// "playertable" through consecutive windows starting at row 0 until
    /// every index in [0,count) has been covered (or TransferValueMaxChunks
    /// is hit), region-scoping each ui_find2 scan to the table's own rect so
    /// only that table's cells are matched. Returns a sparse map of
    /// globalIndex -> raw cell text (e.g. "€92M - €113M", "Not for Sale")
    /// for every index it managed to read; a missing key means the
    /// scroll/settle/rect/scan chain failed for that chunk, which the caller
    /// surfaces as a per-player "missing" entry rather than fabricating a
    /// value.
    /// </summary>
    private static async Task<Dictionary<int, string>> ReadTransferValueTexts(
        FMBridge.Voice.MainThreadQueue queue, List<int> uids, Dictionary<int, int> globalIndexByUid)
    {
        var result = new Dictionary<int, string>();
        if (uids == null || uids.Count == 0 || globalIndexByUid == null) return result;

        var unresolved = new HashSet<int>(uids);
        int chunks = 0;
        foreach (var uid in uids)
        {
            if (!unresolved.Contains(uid) || chunks++ >= TransferValueMaxChunks) continue;
            if (!globalIndexByUid.TryGetValue(uid, out var targetIndex)) continue;
            await OnQueue(queue, _ => Navigator.ScrollToIndex("playertable", targetIndex));

            int winStart = -1, winCount = -1;
            var deadline = Environment.TickCount64 + TransferValueWindowSettleMs;
            while (Environment.TickCount64 < deadline)
            {
                var probe = await OnQueue(queue, _ => Navigator.GetVisibleWindow("playertable"));
                var win = probe?["visibleWindow"] as JsonObject;
                winStart = AsInt(win?["start"]);
                winCount = AsInt(win?["count"]);
                if (winStart >= 0 && winCount > 0 && targetIndex >= winStart && targetIndex < winStart + winCount) break;
                await Task.Delay(PollIntervalMs);
            }
            if (winStart < 0 || winCount <= 0) continue;

            var rect = await ResolveTableRect(queue, "playertable");
            if (rect == null) continue;

            var cellsR = await OnQueue(queue, _ => Navigator.UiFind2(
                TransferValueCellName, 200, rect.Value.x, rect.Value.y, rect.Value.x + rect.Value.w, rect.Value.y + rect.Value.h, true));
            var after = await OnQueue(queue, _ => Navigator.GetVisibleWindow("playertable"));
            int afterStart = AsInt(after?["visibleWindow"]?["start"]);
            int afterCount = AsInt(after?["visibleWindow"]?["count"]);
            if (afterStart != winStart || afterCount != winCount) continue; // window moved while scraping

            if (cellsR?["ok"]?.GetValue<bool>() == true && cellsR["rows"] is JsonArray cellRows
                && TryMapTransferValueWindow(cellRows, winStart, winCount, out var windowValues))
            {
                foreach (var pendingUid in new List<int>(unresolved))
                {
                    if (!globalIndexByUid.TryGetValue(pendingUid, out var idx)) continue;
                    if (!windowValues.TryGetValue(idx, out var text)) continue;
                    result[pendingUid] = text;
                    unresolved.Remove(pendingUid);
                }
            }
        }
        return result;
    }

    /// <summary>Fail-closed row alignment for UI-only transfer values. UiFind2
    /// returns DFS order, which is not row order; pairing that array directly
    /// with VisibleView caused values to migrate between players. Sort by row
    /// geometry and accept the window only when it is a complete, one-cell-per-
    /// visible-row set. Any ambiguity produces no values for the window.</summary>
    private static bool TryMapTransferValueWindow(JsonArray cells, int winStart, int winCount,
        out Dictionary<int, string> values)
    {
        values = new Dictionary<int, string>();
        if (cells == null || winStart < 0 || winCount <= 0) return false;
        var ordered = new List<(double y, string text)>();
        foreach (var node in cells)
        {
            var row = node as JsonObject;
            var text = (string)row?["text"];
            if (string.IsNullOrWhiteSpace(text)) return false;
            int y = AsInt(row?["y"]), h = AsInt(row?["h"]);
            if (h <= 0) return false;
            ordered.Add((y + h / 2.0, text));
        }
        if (ordered.Count != winCount) return false;
        ordered.Sort((a, b) => a.y.CompareTo(b.y));
        for (int i = 0; i < ordered.Count; i++)
        {
            if (i > 0 && ordered[i].y - ordered[i - 1].y < 4.0) return false;
            values[winStart + i] = ordered[i].text;
        }
        return true;
    }

    /// <summary>
    /// Bounded chunked scan (Navigator.ScanUidChunk, never one big call --
    /// see Shortlist.cs class doc) locating a single uid's global row index
    /// in "playertable". Returns -1 if not found within LocateScanMaxChunks
    /// chunks. Shared by read_entity's single-uid TransferValue path.
    /// </summary>
    private static async Task<int> LocateGlobalIndexForUid(FMBridge.Voice.MainThreadQueue queue, int uid)
    {
        int chunkStart = 0;
        for (int i = 0; i < LocateScanMaxChunks; i++)
        {
            var chunk = await OnQueue(queue, _ => Navigator.ScanUidChunk("playertable", chunkStart, LocateScanChunkSize));
            if (chunk?["ok"]?.GetValue<bool>() != true) return -1;
            if (chunk["rows"] is JsonArray rows)
            {
                foreach (var r in rows)
                {
                    var ro = r as JsonObject;
                    if (ro?["uid"] != null && (string)ro["refType"] == "Person" && (int)ro["uid"] == uid)
                        return AsInt(ro["index"]);
                }
            }
            int scanned = AsInt(chunk["scanned"]);
            int total = AsInt(chunk["total"]);
            if (scanned < LocateScanChunkSize) break;
            chunkStart += LocateScanChunkSize;
            if (total > 0 && chunkStart >= total) break;
            await Task.Delay(LocateScanChunkDelayMs);
        }
        return -1;
    }

    /// <summary>
    /// read_entity's single-uid sibling of ReadTransferValueTexts: reaches
    /// the Player Database (idempotent -- ReachPlayerDatabase's own fast
    /// path short-circuits when already there), locates the uid via the
    /// bounded chunked scan above, scrolls it into view only if it isn't
    /// already rendered, then reads exactly the one cell at that row's
    /// offset within the settled window. Deliberately does NOT batch across
    /// uids the way the enrich path does -- callers asking for TransferValue
    /// on many uids already pay one nav+scroll per uid, which ReadEntity.cs
    /// caps (see TransferValueUiScrapeMaxUids). Returns null (never throws)
    /// when the uid can't be found/scrolled/read -- caller reports that as
    /// a normal missing[] entry rather than guessing at a value.
    /// </summary>
    internal static async Task<string> ReadTransferValueForUid(FMBridge.Voice.MainThreadQueue queue, int uid)
    {
        try
        {
            var (reached, _, _, _) = await ReachPlayerDatabase(queue);
            if (!reached) return null;

            int globalIndex = await LocateGlobalIndexForUid(queue, uid);
            if (globalIndex < 0) return null;

            var winCheck = await OnQueue(queue, _ => Navigator.GetVisibleWindow("playertable"));
            int winStart = AsInt(winCheck?["visibleWindow"]?["start"]);
            int winCount = AsInt(winCheck?["visibleWindow"]?["count"]);
            bool inWindow = winCheck?["ok"]?.GetValue<bool>() == true && winStart >= 0 && winCount > 0
                && globalIndex >= winStart && globalIndex < winStart + winCount;

            if (!inWindow)
            {
                var scroll = await OnQueue(queue, _ => Navigator.ScrollToIndex("playertable", globalIndex));
                if (scroll?["ok"]?.GetValue<bool>() != true) return null;

                var deadline = Environment.TickCount64 + TransferValueWindowSettleMs;
                while (Environment.TickCount64 < deadline)
                {
                    var probe = await OnQueue(queue, _ => Navigator.GetVisibleWindow("playertable"));
                    var win = probe?["visibleWindow"] as JsonObject;
                    winStart = AsInt(win?["start"]);
                    winCount = AsInt(win?["count"]);
                    if (winStart >= 0 && winCount > 0 && globalIndex >= winStart && globalIndex < winStart + winCount) break;
                    await Task.Delay(PollIntervalMs);
                }
            }
            if (winStart < 0 || winCount <= 0 || globalIndex < winStart || globalIndex >= winStart + winCount) return null;

            var rect = await ResolveTableRect(queue, "playertable");
            if (rect == null) return null;

            var cellsR = await OnQueue(queue, _ => Navigator.UiFind2(
                TransferValueCellName, 200, rect.Value.x, rect.Value.y, rect.Value.x + rect.Value.w, rect.Value.y + rect.Value.h, true));
            if (cellsR?["ok"]?.GetValue<bool>() != true || cellsR["rows"] is not JsonArray cellRows) return null;
            var after = await OnQueue(queue, _ => Navigator.GetVisibleWindow("playertable"));
            if (AsInt(after?["visibleWindow"]?["start"]) != winStart
                || AsInt(after?["visibleWindow"]?["count"]) != winCount) return null;
            if (!TryMapTransferValueWindow(cellRows, winStart, winCount, out var mapped)) return null;
            return mapped.TryGetValue(globalIndex, out var text) ? text : null;
        }
        catch { return null; }
    }

    /// <summary>Largest-area "playertable"-named rect currently on screen --
    /// shared by both transfer-value readers above so a region-scoped
    /// ui_find2 scan never picks up cells from an unrelated overlapping
    /// widget.</summary>
    private static async Task<(int x, int y, int w, int h)?> ResolveTableRect(FMBridge.Voice.MainThreadQueue queue, string query)
    {
        var rectR = await OnQueue(queue, _ => Navigator.UiFind2(query, 10, 0, 0, 0, 0, false));
        if (rectR?["ok"]?.GetValue<bool>() != true || rectR["rows"] is not JsonArray rows || rows.Count == 0) return null;
        JsonObject best = null;
        long bestArea = -1;
        foreach (var r in rows)
        {
            var ro = r as JsonObject;
            if (ro == null) continue;
            long area = (long)AsInt(ro["w"]) * AsInt(ro["h"]);
            if (area > bestArea) { bestArea = area; best = ro; }
        }
        if (best == null) return null;
        return (AsInt(best["x"]), AsInt(best["y"]), AsInt(best["w"]), AsInt(best["h"]));
    }

    /// <summary>
    /// Parses the raw transfer-value cell text into the {display, sort}
    /// shape ReadEntity.Decode uses for resolved-display channel props (see
    /// its doc comment) -- kept consistent even though this field has no
    /// channel behind it. "sort" is a plain double in the same currency
    /// units the cell displays: a bare "€92M" parses to 92000000; a
    /// "€92M - €113M" range sorts by its MIDPOINT (102500000) since there is
    /// no single authoritative number to prefer; "Not for Sale"/"Unsellable"
    /// (or any text with no parseable number) keeps display but sort:null
    /// so a caller sorting on this field doesn't mistake "unsellable" for
    /// "free" -- the game's gating is reported honestly, never guessed at.
    /// </summary>
    internal static JsonObject ParseTransferValue(string text)
    {
        var node = new JsonObject { ["display"] = text };
        var t = (text ?? "").Trim();
        var lower = t.ToLowerInvariant();
        if (lower.Length == 0 || lower.Contains("not for sale") || lower.Contains("unsellable"))
        {
            node["sort"] = null;
            return node;
        }
        var parts = t.Split(" - ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        double? low = parts.Length > 0 ? ParseMoney(parts[0]) : null;
        double? high = parts.Length > 1 ? ParseMoney(parts[1]) : low;
        if (low.HasValue && high.HasValue) node["sort"] = (low.Value + high.Value) / 2.0;
        else node["sort"] = null;
        return node;
    }

    private static double? ParseMoney(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimStart('€', '$', '£', '¥');
        if (s.Length == 0) return null;
        double mult = 1;
        char suffix = s[^1];
        if (suffix is 'K' or 'k') { mult = 1_000; s = s[..^1]; }
        else if (suffix is 'M' or 'm') { mult = 1_000_000; s = s[..^1]; }
        else if (suffix is 'B' or 'b') { mult = 1_000_000_000; s = s[..^1]; }
        s = s.Replace(",", "").Trim();
        return double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v * mult
            : null;
    }

    public static async Task<JsonObject> Enqueue(FMBridge.Voice.MainThreadQueue queue, string requestJson)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            JsonObject request = null;
            try { request = JsonNode.Parse(requestJson ?? "") as JsonObject; } catch { }

            int max = DefaultMax;
            if (request?["max"] is JsonValue mv && mv.TryGetValue<int>(out var mi)) max = mi;
            max = Math.Clamp(max <= 0 ? DefaultMax : max, 1, MaxRows);

            bool enrich = request?["enrich"] is JsonValue ev && ev.TryGetValue<bool>(out var eb) && eb;
            int enrichMaxRequested = DefaultEnrichMax;
            if (request?["enrich_max"] is JsonValue emv && emv.TryGetValue<int>(out var emi)) enrichMaxRequested = emi;
            int enrichMax = Math.Clamp(enrichMaxRequested <= 0 ? DefaultEnrichMax : enrichMaxRequested, 1, HardEnrichMax);

            bool scoutedOnly = request?["filters"]?["scouted_only"] is JsonValue sv && sv.TryGetValue<bool>(out var sb) && sb;
            int? ageMin = ReadOptionalInt(request?["filters"]?["age_min"]);
            int? ageMax = ReadOptionalInt(request?["filters"]?["age_max"]);
            var positions = ReadStringArray(request?["filters"]?["positions"]);
            if (ageMin.HasValue && ageMax.HasValue && ageMin.Value > ageMax.Value)
                return Fail("filters.age_min cannot be greater than filters.age_max", sw, null);
            bool hasDataFilters = ageMin.HasValue || ageMax.HasValue || positions.Count > 0;

            // ---------------------------------------------- 1. reach the screen
            // (ReachPlayerDatabase's own final poll already waits for a
            // STABLE playertable count -- see its comment -- so that value
            // is reused directly as the baseline rather than re-reading
            // immediately, which live testing showed can still race a
            // trailing stream-in frame.)
            var (reached, reachError, reachSteps, baselineCount) = await ReachPlayerDatabase(queue);
            if (!reached)
                return Fail("could-not-reach-player-database: " + reachError, sw, reachSteps);

            // ---------------------------------------------- 3. apply filter
            bool toggled = false;
            int? filteredCount = null;
            string filterNote = "none-requested";
            if (scoutedOnly)
            {
                var (applyOk, applyErr) = await ClickToggleWithRetry(queue, ToggleClickDeadlineMs);
                if (!applyOk)
                {
                    filterNote = "click-failed:" + applyErr;
                }
                else
                {
                    toggled = true;
                    // Require the post-toggle count to differ from baseline
                    // AND be stable across two consecutive reads -- the same
                    // transient-partial-count hazard documented above on the
                    // initial nav (observed there settling from a low
                    // partial value up to the true total) applies here too;
                    // without the stability check this loop can latch onto
                    // an in-between streaming count instead of the real
                    // filtered total.
                    var deadline = Environment.TickCount64 + FilterDeadlineMs;
                    int prevFilterCount = baselineCount;
                    while (Environment.TickCount64 < deadline)
                    {
                        var afterRead = await OnQueue(queue, _ => Navigator.ListRead("playertable", 1, -1));
                        var c = AsInt(afterRead?["count"]);
                        bool ok = afterRead?["ok"]?.GetValue<bool>() == true;
                        if (ok && c != baselineCount && c == prevFilterCount) { filteredCount = c; break; }
                        if (ok) prevFilterCount = c;
                        await Task.Delay(PollIntervalMs);
                    }
                    filterNote = filteredCount.HasValue
                        ? "applied:scouted_only"
                        : "clicked-but-count-unchanged (baseline=" + baselineCount + ")";
                }
            }

            // ---------------------------------------------- 4/5. uid list + enrich
            // Steps 4 (uid list) and 5 (enrich) are wrapped so that ANY
            // failure here -- including the early `return` this block used
            // to take on a list_read timeout -- still falls through to step
            // 6 (revert). Live testing found the ORIGINAL bug
            // here: an early `return Fail(...)` on list_read failure skipped
            // revert entirely, leaving the "Scouted players only" toggle
            // stuck ON for every subsequent call -- which is exactly why
            // repeated calls degraded (each one's baseline was already
            // silently filtered from the previous call's abandoned toggle).
            string listError = null;
            var uids = new List<int>();
            var globalIndexByUid = new Dictionary<int, int>();
            JsonArray enriched = null;
            int candidateWindowTotal = -1;
            int filterCandidatesScanned = 0;
            int filterCandidatesMatched = 0;
            {
                JsonObject listResult = null;
                var listDeadline = Environment.TickCount64 + UidListRetryDeadlineMs;
                do
                {
                    listResult = await OnQueue(queue, _ => Navigator.ListRead("playertable", max, -1));
                    if (listResult?["ok"]?.GetValue<bool>() == true) break;
                    await Task.Delay(PollIntervalMs);
                } while (Environment.TickCount64 < listDeadline);

                if (listResult?["ok"]?.GetValue<bool>() != true)
                {
                    listError = "list_read failed: " + (string)listResult?["error"];
                }
                else
                {
                    candidateWindowTotal = AsInt(listResult["count"]);
                    if (listResult["rows"] is JsonArray rowsArr)
                    {
                        foreach (var r in rowsArr)
                        {
                            var ro = r as JsonObject;
                            if (ro?["uid"] != null && (string)ro["refType"] == "Person")
                            {
                                int uid = (int)ro["uid"];
                                uids.Add(uid);
                                if (ro["index"] != null) globalIndexByUid[uid] = AsInt(ro["index"]);
                            }
                        }
                    }
                    if (hasDataFilters && uids.Count > 0)
                    {
                        // These two fields are the minimum game data needed
                        // to filter. Process bounded chunks so max=2000 can
                        // never plant 2000 bindings at once. BatchEnrich's
                        // collect closes every chunk's bindings before this
                        // loop starts the next one.
                        var candidateUids = uids;
                        var kept = new List<int>(candidateUids.Count);
                        for (var offset = 0; offset < candidateUids.Count; offset += FilterBatchSize)
                        {
                            var chunk = candidateUids.GetRange(offset, Math.Min(FilterBatchSize, candidateUids.Count - offset));
                            var filterRows = await BatchEnrich(queue, chunk, null, true);
                            filterCandidatesScanned += chunk.Count;
                            for (var i = 0; i < filterRows.Count && i < chunk.Count; i++)
                            {
                                var row = filterRows[i] as JsonObject;
                                if (MatchesDataFilters(row, ageMin, ageMax, positions))
                                {
                                    kept.Add(chunk[i]);
                                    filterCandidatesMatched++;
                                }
                            }
                        }
                        uids = kept;
                    }
                    if (enrich && uids.Count > 0)
                    {
                        var enrichUids = uids.GetRange(0, Math.Min(uids.Count, enrichMax));
                        // Read transfer-value cells BEFORE the
                        // channel-based batch below -- it scrolls the table
                        // (ScrollToIndex), which the channel path never
                        // depends on (PlantPlayers/CollectPlayers read off
                        // fabricated PersonReferences, not the visible
                        // window), so ordering here is latency-only, not a
                        // correctness dependency.
                        var transferValueTexts = await ReadTransferValueTexts(queue, enrichUids, globalIndexByUid);
                        enriched = await BatchEnrich(queue, enrichUids, transferValueTexts);
                    }
                }
            }

            // ---------------------------------------------- 6. revert filter
            // ALWAYS attempted when toggled==true, regardless of whether
            // step 4/5 above succeeded -- see the comment on that block.
            bool reverted = !toggled;
            string revertNote = toggled ? "not-attempted" : "no-filter-was-set";
            if (toggled)
            {
                var (revertOk, revertErr) = await ClickToggleWithRetry(queue, ToggleClickDeadlineMs);
                if (revertOk)
                {
                    var deadline = Environment.TickCount64 + FilterDeadlineMs;
                    while (Environment.TickCount64 < deadline)
                    {
                        var chk = await OnQueue(queue, _ => Navigator.ListRead("playertable", 1, -1));
                        if (chk?["ok"]?.GetValue<bool>() == true && AsInt(chk["count"]) == baselineCount)
                        {
                            reverted = true;
                            revertNote = "reverted:count-restored-to-" + baselineCount;
                            break;
                        }
                        await Task.Delay(PollIntervalMs);
                    }
                    if (!reverted) revertNote = "revert-click-ok-but-count-did-not-restore";
                }
                else
                {
                    revertNote = "revert-click-failed:" + revertErr;
                }

                // A revert failure here is not just "this call's result is
                // slightly off" -- it silently filters the Player Database
                // for EVERY subsequent call (live verification
                // found exactly this: a stuck "Scouted players
                // only" toggle made an unrelated shortlist add's own uid
                // scan see total=872 instead of 10000 and honestly-but-
                // misleadingly report uid-not-found). One extra attempt
                // after a longer settle before giving up, and if it's still
                // stuck, make that fact unmissable in revert_note rather
                // than a note a caller could plausibly skim past.
                if (!reverted)
                {
                    await Task.Delay(RevertRetrySettleMs);
                    var (retryOk, retryErr) = await ClickToggleWithRetry(queue, ToggleClickDeadlineMs);
                    string retryOutcome;
                    if (retryOk)
                    {
                        var retryDeadline = Environment.TickCount64 + FilterDeadlineMs;
                        while (Environment.TickCount64 < retryDeadline)
                        {
                            var chk = await OnQueue(queue, _ => Navigator.ListRead("playertable", 1, -1));
                            if (chk?["ok"]?.GetValue<bool>() == true && AsInt(chk["count"]) == baselineCount)
                            {
                                reverted = true;
                                revertNote = "reverted-on-retry:count-restored-to-" + baselineCount;
                                break;
                            }
                            await Task.Delay(PollIntervalMs);
                        }
                        retryOutcome = reverted ? "retry-ok" : "click-ok-but-count-did-not-restore";
                    }
                    else
                    {
                        retryOutcome = "click-failed:" + retryErr;
                    }
                    if (!reverted)
                    {
                        revertNote = "STUCK-FILTER-TOGGLE-NOT-REVERTED (first: " + revertNote + "; retry: " + retryOutcome +
                            ") -- Player Database remains filtered for ALL subsequent calls until manually cleared";
                    }
                }
            }

            sw.Stop();
            if (listError != null)
            {
                var failObj = Fail(listError, sw, reachSteps);
                failObj["reverted"] = reverted;
                failObj["revert_note"] = revertNote;
                return failObj;
            }

            // Never let a truncated enrich pass silently -- report
            // exactly what was asked for (enrich_requested, pre-clamp), what
            // the hard cap allowed (enrich_cap), and what actually landed
            // (enrich_done), with an explicit boolean the caller can branch
            // on instead of having to notice a short array.
            int enrichDone = enriched?.Count ?? 0;
            bool enrichTruncated = enrich && enrichDone < Math.Min(enrichMaxRequested <= 0 ? DefaultEnrichMax : enrichMaxRequested, uids.Count);
            return new JsonObject
            {
                ["ok"] = true,
                ["reach"] = reachSteps,
                ["total_unfiltered"] = baselineCount,
                ["total_after_filter"] = IsCandidateWindowTruncated(candidateWindowTotal, hasDataFilters ? filterCandidatesScanned : uids.Count)
                    ? null : (JsonNode)JsonValue.Create(uids.Count),
                ["returned_after_filter"] = uids.Count,
                ["candidate_window"] = new JsonObject {
                    ["requested_max"] = max,
                    ["available"] = candidateWindowTotal >= 0 ? (JsonNode)JsonValue.Create(candidateWindowTotal) : null,
                    ["scanned"] = hasDataFilters ? filterCandidatesScanned : uids.Count,
                    ["matched"] = hasDataFilters ? filterCandidatesMatched : uids.Count,
                    ["truncated"] = IsCandidateWindowTruncated(candidateWindowTotal, hasDataFilters ? filterCandidatesScanned : uids.Count),
                    ["filter_batch_size"] = hasDataFilters ? FilterBatchSize : null
                },
                ["filters_applied"] = new JsonObject {
                    ["scouted_only"] = scoutedOnly,
                    ["age_min"] = ageMin,
                    ["age_max"] = ageMax,
                    ["positions"] = positions.Count > 0 ? ToJsonArray(positions) : null,
                    ["data_filter_candidates_scanned"] = filterCandidatesScanned,
                    ["data_filter_candidates_matched"] = filterCandidatesMatched,
                    ["note"] = filterNote
                },
                ["uid_count"] = uids.Count,
                ["uids"] = ToJsonArray(uids),
                ["enriched"] = enriched,
                ["enrich_requested"] = enrich ? (JsonNode)JsonValue.Create(enrichMaxRequested <= 0 ? DefaultEnrichMax : enrichMaxRequested) : null,
                ["enrich_done"] = enrich ? (JsonNode)JsonValue.Create(enrichDone) : null,
                ["enrich_truncated"] = enrich ? (JsonNode)JsonValue.Create(enrichTruncated) : null,
                ["enrich_cap"] = HardEnrichMax,
                ["reverted"] = reverted,
                ["revert_note"] = revertNote,
                ["latency_ms"] = sw.Elapsed.TotalMilliseconds,
            };
        }
        catch (Exception e)
        {
            return Fail(e.Message, sw, null);
        }
    }

    // ------------------------------------------------ navigation / reach

    /// <summary>
    /// Always re-drives from PortalScreen so this works from arbitrary caller
    /// screen state, not just from a warm Recruitment session. Returns
    /// (ok, error, stepsLog) -- stepsLog is attached to the response either
    /// way so a failed reach is diagnosable instead of a bare boolean.
    /// </summary>
    /// <summary>internal (not private): reused by Shortlist.cs's add/remove
    /// actions, which need the exact same proven reach-reliably-from-
    /// arbitrary-screen-state recipe to land on the main Player Database
    /// table before driving its row-checkbox/ActionsDropdown mechanism (the
    /// Shortlists tab's OWN table can't be driven the same way -- see
    /// Shortlist.cs's class doc).</summary>
    internal static async Task<(bool ok, string error, JsonArray steps, int baselineCount)> ReachPlayerDatabase(FMBridge.Voice.MainThreadQueue queue)
    {
        var steps = new JsonArray();

        // Fast path: if the caller is calling query_players repeatedly (the
        // exact scenario that exposed the repeat-call bug), the
        // Player Database screen may still be sitting there from the PRIOR
        // call. Re-driving PortalScreen -> RecruitmentScreen -> tab-click
        // every single time forces a full screen teardown/rebuild the game
        // doesn't need, and live testing showed this compounds under rapid
        // back-to-back calls (observed: consecutive full re-navigations
        // pushing the eventual list_read past its retry deadline). A cheap
        // upfront check skips all of that when we're already there.
        // NOTE: "playertable" is a substring
        // of the Shortlists tab's own "playertableshortlist" widget name, so
        // ListRead's substring match returns THAT widget (ok:true, count>0)
        // when the caller is sitting on the Shortlists tab and the real
        // "playertable" isn't even mounted. This fast-path used to trust
        // count>0 alone, which silently accepted the wrong table and let
        // Shortlist.cs's add/remove click a completely different (known
        // non-interactive) checkbox while still reporting success -- proven
        // live: a "remove" call reported ok:true/"remove-committed" against
        // table_rect matching the Shortlists tab, and the oracle
        // (shortlist action:list) showed the uid was never actually removed.
        // Fix: require the RESOLVED widget's exact name to be "playertable"
        // (case-insensitive), not just a substring match.
        var already = await OnQueue(queue, _ => Navigator.ListRead("playertable", 1, -1));
        bool alreadyIsRealTable = string.Equals((string)already?["element"], "playertable", StringComparison.OrdinalIgnoreCase);
        // Also guard the fast-path against the STALE-widget hazard (see the
        // recovery block below): a "playertable" match with a stable
        // itemCount can still be a leftover, no-longer-visible instance
        // while the Overview dashboard tab is actually showing. The check
        // here must be the POSITIVE checkbox-column marker (see
        // PollPlayerDatabaseActive) -- this used to test for the Overview
        // dashboard's "PlayerSearch" tile instead, which also exists on the
        // Player Database screen itself whenever its tiles strip is shown,
        // so the fast-path was silently unreachable and every call re-drove
        // the full Portal->Recruitment->tab teardown/rebuild it was written
        // to avoid.
        if (already?["ok"]?.GetValue<bool>() == true && AsInt(already["count"]) > 0 && alreadyIsRealTable
            && await PollPlayerDatabaseActive(queue, FastPathActiveCheckMs))
        {
            int fastCount = await StabilizePlayertableCount(queue, NavDeadlineMs);
            steps.Add(new JsonObject { ["step"] = "fast-path:already-on-player-database", ["count"] = fastCount });
            if (fastCount > 0) return (true, null, steps, fastCount);
            // Fell through (stabilization failed) -- drop to the full
            // re-nav path below rather than failing outright.
        }

        var portalOpen = await OnQueue(queue, _ => Navigator.NavOpen("PortalScreen"));
        steps.Add(new JsonObject { ["step"] = "nav_open:PortalScreen", ["ok"] = portalOpen?["ok"]?.GetValue<bool>() == true });

        var recruitOpen = await OnQueue(queue, _ => Navigator.NavOpen("RecruitmentScreen"));
        bool recruitOk = recruitOpen?["ok"]?.GetValue<bool>() == true;
        steps.Add(new JsonObject { ["step"] = "nav_open:RecruitmentScreen", ["ok"] = recruitOk });
        if (!recruitOk) return (false, "nav_open RecruitmentScreen failed: " + (string)recruitOpen?["error"], steps, 0);

        // Poll for the "Player Database" sub-tab SIButton to materialize and
        // locate it BY TEXT rather than trusting a fixed DFS index. A fresh
        // navigation can transiently show extra SIButtons (e.g. Overview
        // dashboard tiles still tearing down) before settling to the 7-tab
        // bar, so a hardcoded index (only ever valid against a warm, settled
        // screen) is not reliable here -- live testing caught this: a
        // transient count of 20 SIButtons caused index 1 to click the wrong
        // element and the subsequent playertable poll timed out. Searching
        // by text each attempt is robust to that churn.
        int tabCount = 0;
        int targetIndex = -1;
        var deadline = Environment.TickCount64 + NavDeadlineMs;
        while (Environment.TickCount64 < deadline)
        {
            var find = await OnQueue(queue, _ => Navigator.UiFind2("SIButton", 40, 0, 0, 0, 0, true));
            tabCount = AsInt(find?["count"]);
            if (find?["rows"] is JsonArray rowsArr)
            {
                for (int i = 0; i < rowsArr.Count; i++)
                {
                    if ((rowsArr[i] as JsonObject)?["text"]?.ToString() == "Player Database") { targetIndex = i; break; }
                }
            }
            if (targetIndex >= 0) break;
            await Task.Delay(PollIntervalMs);
        }
        steps.Add(new JsonObject { ["step"] = "poll:SIButton-tabs", ["count"] = tabCount, ["found_at_index"] = targetIndex });
        if (targetIndex < 0) return (false, "player-database-tab-not-found (count=" + tabCount + ")", steps, 0);

        // Retry the click a few times: live testing showed the
        // FIRST click right after a fresh nav can land mid-way through a
        // Unity UI-Toolkit style-transition repaint pass and throw a caught
        // (non-fatal, no game crash) InvalidOperationException
        // ("VisualElements cannot change background color under an active
        // visual tree during generateVisualContent callback execution");
        // waiting one poll interval and retrying reliably succeeds.
        //
        // NOTE: this used to click
        // via Navigator.UiClick("SIButton", targetIndex) -- targetIndex was
        // resolved a few lines up from a SEPARATE, independently-walked
        // UiFind2/CollectBySubstring2 pass. Live testing here caught that
        // bug reproducibly and repeatedly (including a standalone manual
        // index-1 SIButton click, isolated from any of
        // this file's own retry/recovery logic, landing at (199,62) --
        // Overview's own center -- instead of Player Database at (276,61)):
        // UiClick's own Collect() (exact-name DFS) does NOT always enumerate
        // "SIButton" nodes in the same order as CollectBySubstring2, and
        // this is not a one-off transient -- it reproduced across 5
        // consecutive shortlist-remove attempts in the same live session
        // with a clean, stable, correctly-bounded 7-button tab bar each
        // time. This is exactly the class of bug Navigator.ClickByText
        // (Navigator.Shortlist.cs) was written to eliminate for the
        // shortlist context-menu family: click by CONTENT, resolved and
        // clicked against the SAME single DFS pass, so there is no second,
        // independently-ordered collection for the index to go stale
        // against. Switching this call site to it, too.
        JsonObject click = null;
        bool clickOk = false;
        string clickError = null;
        var clickDeadline = Environment.TickCount64 + NavDeadlineMs;
        do
        {
            click = await OnQueue(queue, _ => Navigator.ClickByText("SIButton", "Player Database"));
            clickOk = click?["ok"]?.GetValue<bool>() == true;
            clickError = (string)click?["error"];
            if (clickOk) break;
            await Task.Delay(PollIntervalMs);
        } while (Environment.TickCount64 < clickDeadline);
        steps.Add(new JsonObject { ["step"] = "ui_click:SIButton[text=Player Database]", ["ok"] = clickOk });
        if (!clickOk) return (false, "player-database-tab-click-failed: " + clickError, steps, 0);

        // Poll for the playertable widget to report a real (>0) itemCount,
        // THEN require it to be stable across two consecutive reads before
        // trusting it. Live testing caught a transient window
        // right after the tab click where the table briefly reports
        // "no-list-widget-matches" (not yet materialized), then a low
        // partial count (observed: 100) while more rows stream in, before
        // settling at its real value a second or two later -- accepting the
        // first count>0 reading here silently under-reports the total.
        int finalCount = await StabilizePlayertableCount(queue, NavDeadlineMs);
        steps.Add(new JsonObject { ["step"] = "poll:playertable-stable", ["count"] = finalCount });
        if (finalCount <= 0) return (false, "playertable-not-populated-after-nav", steps, 0);

        // Live testing caught a case where
        // StabilizePlayertableCount reports a perfectly stable, correct
        // itemCount even though the tab click above SILENTLY DID NOT
        // switch the visible screen: FM apparently keeps the Player
        // Database table widget (and its itemCount property) alive/mounted
        // in the tree even while the RecruitmentScreen's "Overview" tab is
        // the one actually showing on top -- so the widget-presence check
        // is fooled the same way the earlier "playertable"-substring bug
        // fooled it, just via staleness instead of substring collision.
        // Reproducibly fixed live by toggling to a different tab and back.
        // Verify activation with the POSITIVE checkbox-column marker (see
        // PollPlayerDatabaseActive -- the old "PlayerSearch is gone"
        // negative marker false-positived on the real Player Database
        // screen); if it never shows, toggle off Overview and retry, and if
        // it STILL never shows, FAIL LOUDLY. The previous version of this
        // loop exited without error when its recovery attempts ran out,
        // which let callers (proven live: shortlist add) drive checkbox/
        // context-menu clicks against a screen that was never actually
        // switched.
        bool pdActive = false;
        for (int recoveryAttempt = 0; recoveryAttempt < 3; recoveryAttempt++)
        {
            if (recoveryAttempt > 0)
            {
                // Same ClickByText fix as above: resolve+click "Overview"
                // and "Player Database" by their own rendered text instead
                // of a raw UiClick index that can silently disagree with
                // whatever collection originally located the tab.
                var awayClick = await OnQueue(queue, _ => Navigator.ClickByText("SIButton", "Overview"));
                await Task.Delay(PollIntervalMs);
                var backClick = await OnQueue(queue, _ => Navigator.ClickByText("SIButton", "Player Database"));
                steps.Add(new JsonObject
                {
                    ["step"] = "recovery:toggle-overview-then-playerdatabase",
                    ["attempt"] = recoveryAttempt,
                    ["awayOk"] = awayClick?["ok"]?.GetValue<bool>() == true,
                    ["backOk"] = backClick?["ok"]?.GetValue<bool>() == true,
                });

                finalCount = await StabilizePlayertableCount(queue, NavDeadlineMs);
                steps.Add(new JsonObject { ["step"] = "poll:playertable-stable-after-recovery", ["count"] = finalCount });
                if (finalCount <= 0) return (false, "playertable-not-populated-after-recovery", steps, 0);
            }

            // Require the marker to hold across TWO reads separated by a
            // settle: live testing (2026-08-29, remove-after-list) caught a
            // single positive read during a tab switch that only took
            // TRANSIENTLY -- coming from the Shortlists dropdown tab, the
            // Player Database screen mounted long enough to pass one
            // checkbox scan, then flipped back to Shortlists (playertable
            // gone from the visual tree entirely) before the caller's own
            // row scan began. The toggle below is the same recovery proven
            // to unstick exactly this flip-back case.
            pdActive = await PollPlayerDatabaseActive(queue, PdActiveDeadlineMs);
            if (pdActive)
            {
                await Task.Delay(PdActiveRecheckSettleMs);
                pdActive = await PollPlayerDatabaseActive(queue, PdActiveRecheckDeadlineMs);
            }
            steps.Add(new JsonObject { ["step"] = "check:pd-checkbox-column", ["attempt"] = recoveryAttempt, ["active"] = pdActive });
            if (pdActive) break;
        }
        if (!pdActive)
            return (false, "player-database-tab-activation-failed (checkbox column never appeared; screen likely still on Overview)", steps, 0);

        return (true, null, steps, finalCount);
    }

    /// <summary>
    /// True when the Player Database view is ACTUALLY interactive: the
    /// playertable widget has a real on-screen rect AND at least one row
    /// checkbox ("unity-checkmark") inside that rect. This is the positive
    /// marker proven live (2026-08-28 session, documented at length in
    /// Shortlist.cs's TryResolveClickAndVerify): the checkbox column exists
    /// only on the real Player Database view and is entirely absent while
    /// the table is merely stale-mounted behind Overview. The previous
    /// marker here was NEGATIVE -- "the Overview dashboard's PlayerSearch
    /// tile is gone" -- and live testing on 2026-08-29 showed that tile
    /// also matches on the Player Database screen itself when its tiles
    /// strip is visible, so every reach false-positived into two pointless
    /// Overview toggles and the already-there fast-path never fired.
    /// Polls under a deadline because the checkbox column can lag the
    /// table's own itemCount by seconds mid-transition (same hazard
    /// documented in Shortlist.cs's rect+checkbox stabilization loop).
    /// </summary>
    private static async Task<bool> PollPlayerDatabaseActive(FMBridge.Voice.MainThreadQueue queue, long deadlineMs)
    {
        var deadline = Environment.TickCount64 + deadlineMs;
        while (true)
        {
            var tableRect = await OnQueue(queue, _ => Navigator.UiFind2("playertable", 5, 0, 0, 0, 0, false));
            JsonObject best = null;
            if (tableRect?["ok"]?.GetValue<bool>() == true && tableRect["rows"] is JsonArray trArr)
            {
                foreach (var r in trArr)
                {
                    var ro = r as JsonObject;
                    if (ro == null) continue;
                    // UiFind2 matches by SUBSTRING, so the Shortlists tab's
                    // own "playertableshortlist" widget matches too -- and
                    // its rows carry checkmarks, which would false-positive
                    // this marker on the wrong screen. Exact name only.
                    if (!string.Equals((string)ro["name"], "playertable", StringComparison.OrdinalIgnoreCase)) continue;
                    if (best == null || AsInt(ro["w"]) * AsInt(ro["h"]) > AsInt(best["w"]) * AsInt(best["h"]))
                        best = ro;
                }
            }
            if (best != null && AsInt(best["w"]) > 0 && AsInt(best["h"]) > 0)
            {
                int x = AsInt(best["x"]), y = AsInt(best["y"]), w = AsInt(best["w"]), h = AsInt(best["h"]);
                var scan = await OnQueue(queue, _ => Navigator.UiFind2("unity-checkmark", 50, x, y, x + w, y + h, false));
                if (scan?["ok"]?.GetValue<bool>() == true && AsInt(scan["count"]) > 0) return true;
            }
            if (Environment.TickCount64 >= deadline) return false;
            await Task.Delay(PollIntervalMs);
        }
    }

    /// <summary>
    /// Poll `playertable`'s itemCount until it reports the SAME value on two
    /// consecutive reads (or the deadline expires). Shared by the fast-path
    /// already-there check and the full nav path's final poll -- both need
    /// the identical settle behavior (see the caller-side comments for the
    /// transient-partial-count hazard this guards against).
    /// </summary>
    private static async Task<int> StabilizePlayertableCount(FMBridge.Voice.MainThreadQueue queue, long deadlineMs)
    {
        int finalCount = -1;
        int prevCount = -1;
        var deadline = Environment.TickCount64 + deadlineMs;
        while (Environment.TickCount64 < deadline)
        {
            var lr = await OnQueue(queue, _ => Navigator.ListRead("playertable", 1, -1));
            bool isRealTable = string.Equals((string)lr?["element"], "playertable", StringComparison.OrdinalIgnoreCase);
            int c = (lr?["ok"]?.GetValue<bool>() == true && isRealTable) ? AsInt(lr["count"]) : -1;
            if (c > 0 && c == prevCount) { finalCount = c; break; }
            prevCount = c;
            await Task.Delay(PollIntervalMs);
        }
        return finalCount;
    }

    /// <summary>
    /// Click the "Scouted players only" toggle, retrying through BOTH
    /// failure modes seen in live repeated-call testing
    /// (first reported after verification runs, then
    /// reproduced live): (a) a caught, non-fatal Unity UI-Toolkit
    /// InvalidOperationException ("VisualElements cannot change background
    /// color / cannot be marked for dirty repaint under an active visual
    /// tree during ... visual tree rendering") when a click lands while a
    /// PRIOR click on the same toggle is still mid style-transition
    /// animation -- this is exactly what happens on back-to-back
    /// query_players calls, since the original implementation only
    /// retried the tab-navigation click, not this one; and (b) transient
    /// "element-not-found"/"zero-bounds" errors when the compact filter
    /// bar hasn't finished rendering yet after a fresh tab click. Fix:
    /// retry on ANY error (not just exceptions) with a poll-interval
    /// backoff up to `deadlineMs`, try the known sibling node name if the
    /// primary name reports "zero-bounds" (same live-confirmed on-screen
    /// position, see ScoutedToggleCandidates), and -- critically -- wait
    /// `ToggleSettleDelayMs` after a SUCCESSFUL click before returning, so
    /// the caller (and any immediately-following query_players call) never
    /// re-clicks this toggle while its transition animation is still
    /// playing. Only reports failure after the full deadline is exhausted.
    /// </summary>
    private static async Task<(bool ok, string error)> ClickToggleWithRetry(FMBridge.Voice.MainThreadQueue queue, long deadlineMs)
    {
        var deadline = Environment.TickCount64 + deadlineMs;
        string lastError = "no-attempt";
        while (Environment.TickCount64 < deadline)
        {
            foreach (var candidate in ScoutedToggleCandidates)
            {
                var click = await OnQueue(queue, _ => Navigator.UiClick(candidate, 0));
                if (click?["ok"]?.GetValue<bool>() == true)
                {
                    await Task.Delay(ToggleSettleDelayMs);
                    return (true, null);
                }
                var err = (string)click?["error"] ?? "unknown-click-error";
                lastError = candidate + ":" + err;
                // Only "zero-bounds" on the primary candidate justifies
                // immediately trying the sibling within this same attempt;
                // any other error (exception, element-not-found) should
                // fall through to the outer sleep+retry instead of
                // hammering the sibling too.
                if (err != "zero-bounds") break;
            }
            await Task.Delay(PollIntervalMs);
        }
        return (false, lastError);
    }

    // ------------------------------------------------------------ enrich

    private sealed class PropSlot
    {
        public string Name;
        public Bindings.Key Key;
        public Bindings.ValueChangedCallback Cb;
        public bool Bound;
        public string Note;
        public string Value;
        public int? RefUid;
        public string RefTable;
        public string ResolvedDisplay;
        public double? ResolvedSort;
        public bool NeedsRecheck;
    }

    private sealed class PlayerCtx
    {
        public int Uid;
        public string Error;
        public Bindings.Key RootKey;
        public FM.GamePlugin.GameInteropSubsystem Interop;
        public readonly Dictionary<string, PropSlot> Phase1 = new();
        public readonly Dictionary<string, PropSlot> Phase2 = new();
        public bool Phase2Attempted;
        public bool Phase2Started;
    }

    private static int _counter;

    private static async Task<JsonArray> BatchEnrich(FMBridge.Voice.MainThreadQueue queue, List<int> uids, Dictionary<int, string> transferValueTexts = null, bool filterOnly = false)
    {
        var plants = await OnQueue(queue, b => PlantPlayers(b, uids, filterOnly ? FilterProps : Phase1Props));
        JsonArray result = null;
        try
        {
            var deadline = Environment.TickCount64 + EnrichPhaseWaitMs * 2;
            while (Environment.TickCount64 < deadline)
            {
                var pending = await OnQueue(queue, b => AdvancePlayers(b, plants));
                if (pending == 0) break;
                await Task.Delay(PollIntervalMs);
            }
        }
        finally
        {
            // CollectPlayers unbinds and closes every plant, including on a
            // timeout/exception. This is what makes the chunk boundary a
            // real cleanup boundary rather than merely a smaller loop.
            result = await OnQueue(queue, b => CollectPlayers(b, plants, transferValueTexts));
        }
        return result;
    }

    private static List<PlayerCtx> PlantPlayers(BindingSubsystem bindings, List<int> uids, string[] phase1Props = null)
    {
        var result = new List<PlayerCtx>(uids.Count);
        if (!GameSubsystems.TryGet<FM.GamePlugin.GameInteropSubsystem>(out var interop) || interop == null)
        {
            foreach (var uid in uids) result.Add(new PlayerCtx { Uid = uid, Error = "game-interop-subsystem-not-found" });
            return result;
        }
        foreach (var uid in uids)
        {
            var ctx = new PlayerCtx { Uid = uid, Interop = interop };
            try
            {
                var fabricated = new FM.UI.PersonReference(uid);
                var wrapped = TypedValue.Create<Il2CppSystem.Object>(fabricated.Cast<Il2CppSystem.Object>());
                var path = "__bridge.qp." + Interlocked.Increment(ref _counter);
                var rootKey = NativeBindings.CreatePath(bindings, path, Bindings.NodeFlags.Default);
                ctx.RootKey = rootKey;

                foreach (var propName in (phase1Props ?? Phase1Props))
                    ctx.Phase1[propName] = PlantOneProp(bindings, rootKey, propName);

                bindings.Set(ref rootKey, wrapped, Bindings.SetFlags.UpdateHandler | Bindings.SetFlags.ForceUpdateValue);
            }
            catch (Exception e) { ctx.Error = e.Message; }
            result.Add(ctx);
        }
        return result;
    }

    private static PropSlot PlantOneProp(BindingSubsystem bindings, Bindings.Key parentKey, string propName)
    {
        var slot = new PropSlot { Name = propName };
        try
        {
            var span = ReadEntity.SpanOf(propName);
            var propId = PropertyIdentifierSet.Instance.GetID(span);
            if (propId.ID == 0) { slot.Note = "unknown-property-name"; return slot; }
            slot.Key = NativeBindings.CreateRooted(bindings, parentKey, propName, Bindings.NodeFlags.RequiresContext);
            if (slot.Key.m_key == parentKey.m_key) { slot.Note = "child-key-equals-parent"; return slot; }
            slot.Cb = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Bindings.ValueChangedCallback>(
                (Action<Bindings.Key, TypedValue>)((k, v) =>
                {
                    ReadEntity.DescribeAndResolve(v, out var d, out var refUid, out var refTable,
                        out var resolvedDisplay, out var resolvedSort, out var pending);
                    slot.Value = d; slot.RefUid = refUid; slot.RefTable = refTable;
                    slot.ResolvedDisplay = resolvedDisplay; slot.ResolvedSort = resolvedSort;
                    slot.NeedsRecheck = pending;
                }));
            bindings.Bind(ref slot.Key, slot.Cb);
            slot.Bound = true;
        }
        catch (Exception e) { slot.Note = e.Message; }
        return slot;
    }

    private static int AdvancePlayers(BindingSubsystem bindings, List<PlayerCtx> plants)
    {
        var pending = 0;
        foreach (var ctx in plants)
        {
            if (ctx.Error != null) continue;
            foreach (var slot in ctx.Phase1.Values) pending += PeekSlot(bindings, slot);

            if (!ctx.Phase2Attempted && ctx.Phase1.TryGetValue("FullContract", out var fc) && fc.Value != null)
            {
                ctx.Phase2Attempted = true;
                try
                {
                    var contractTv = bindings.Get(ref fc.Key);
                    if (contractTv != null)
                    {
                        foreach (var cp in Phase2Props)
                            ctx.Phase2[cp] = PlantOneProp(bindings, fc.Key, cp);
                        bindings.Set(ref fc.Key, contractTv, Bindings.SetFlags.UpdateHandler | Bindings.SetFlags.ForceUpdateValue);
                        ctx.Phase2Started = true;
                    }
                }
                catch { }
            }

            if (ctx.Phase2Started)
                foreach (var slot in ctx.Phase2.Values) pending += PeekSlot(bindings, slot);
        }
        return pending;
    }

    private static int PeekSlot(BindingSubsystem bindings, PropSlot slot)
    {
        if (!slot.Bound) return 0; // never bound (unknown prop etc.) -- can't ever land
        if (slot.Value != null && !slot.NeedsRecheck) return 0;
        try
        {
            var d = bindings.Get(ref slot.Key);
            if (d != null)
            {
                ReadEntity.DescribeAndResolve(d, out var desc, out var refUid, out var refTable,
                    out var resolvedDisplay, out var resolvedSort, out var pending);
                slot.Value = desc; slot.RefUid = refUid; slot.RefTable = refTable;
                slot.ResolvedDisplay = resolvedDisplay; slot.ResolvedSort = resolvedSort;
                slot.NeedsRecheck = pending;
            }
        }
        catch { }
        return (slot.Value == null || slot.NeedsRecheck) ? 1 : 0;
    }

    private static JsonArray CollectPlayers(BindingSubsystem bindings, List<PlayerCtx> plants, Dictionary<int, string> transferValueTexts = null)
    {
        var players = new JsonArray();
        foreach (var ctx in plants)
        {
            foreach (var slot in ctx.Phase1.Values) { PeekSlot(bindings, slot); CloseSlot(bindings, ctx.Interop, slot); }
            foreach (var slot in ctx.Phase2.Values) { PeekSlot(bindings, slot); CloseSlot(bindings, ctx.Interop, slot); }

            if (ctx.Error != null)
            {
                players.Add(new JsonObject { ["uid"] = ctx.Uid, ["error"] = ctx.Error });
                continue;
            }

            JsonNode DecodeSlot(Dictionary<string, PropSlot> map, string name)
            {
                if (!map.TryGetValue(name, out var s) || s.Value == null) return null;
                return ReadEntity.Decode(s.Value, s.RefUid, s.RefTable, s.ResolvedDisplay, s.ResolvedSort);
            }

            var missing = new JsonArray();
            foreach (var kv in ctx.Phase1) if (kv.Value.Value == null) missing.Add(kv.Value.Note != null ? kv.Key + " (" + kv.Value.Note + ")" : kv.Key);
            foreach (var kv in ctx.Phase2) if (kv.Value.Value == null) missing.Add(kv.Value.Note != null ? kv.Key + " (" + kv.Value.Note + ")" : kv.Key);

            // transfer_value has no channel behind it (UI-scrape
            // only, see ReadTransferValueTexts) -- a dict miss means the
            // scroll/scan chain never covered this row, not that the game
            // reported "no value", so it's reported as missing rather than
            // silently defaulted to null-with-no-explanation.
            JsonNode transferValueNode = null;
            if (transferValueTexts != null && transferValueTexts.TryGetValue(ctx.Uid, out var tvText) && !string.IsNullOrEmpty(tvText))
            {
                transferValueNode = ParseTransferValue(tvText);
                if (transferValueNode is JsonObject verifiedValue)
                {
                    verifiedValue["source"] = "player-database-ui";
                    verifiedValue["uid_verified"] = ctx.Uid;
                }
            }
            else if (transferValueTexts != null)
                missing.Add("transfer_value (ui-cell-not-read-for-this-row)");

            players.Add(new JsonObject
            {
                ["uid"] = ctx.Uid,
                ["name"] = DecodeSlot(ctx.Phase1, "Name")?.ToString(),
                ["age"] = DecodeSlot(ctx.Phase1, "Age"),
                ["position"] = DecodeSlot(ctx.Phase1, "Position")?.ToString(),
                ["position_decoded"] = ctx.Phase1.TryGetValue("Position", out var posSlot) ? PositionDecode.Decode(posSlot.Value) : null,
                ["perceived_potential_ability"] = DecodeSlot(ctx.Phase1, "PerceivedPotentialAbility"),
                ["wage"] = DecodeSlot(ctx.Phase2, "Wage"),
                ["contract_end_date"] = DecodeSlot(ctx.Phase2, "EndDate"),
                ["transfer_value"] = transferValueNode,
                ["missing"] = missing,
            });
        }
        return players;
    }

    private static void CloseSlot(BindingSubsystem bindings, FM.GamePlugin.GameInteropSubsystem interop, PropSlot slot)
    {
        if (!slot.Bound) return;
        try { var k = slot.Key; bindings?.Unbind(ref k, slot.Cb); } catch { }
        try { interop?.CloseChannel(slot.Key); } catch { }
    }

    // ------------------------------------------------------------- helpers

    private static async Task<T> OnQueue<T>(FMBridge.Voice.MainThreadQueue queue, Func<BindingSubsystem, T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Enqueue(() =>
        {
            try
            {
                var bindings = FMBridge.Eyes.SpikeHooks.CapturedBindings;
                if (bindings == null) { tcs.SetException(new InvalidOperationException("bindings-not-captured")); return; }
                tcs.SetResult(func(bindings));
            }
            catch (Exception e) { try { tcs.SetException(e); } catch { } }
        });
        var done = await Task.WhenAny(tcs.Task, Task.Delay(QueueTimeoutMs));
        if (done != tcs.Task) throw new TimeoutException("main thread did not answer in " + QueueTimeoutMs + "ms");
        return tcs.Task.Result;
    }

    private static int AsInt(JsonNode n)
    {
        if (n is JsonValue jv && jv.TryGetValue<int>(out var i)) return i;
        return -1;
    }

    private static int? ReadOptionalInt(JsonNode node)
    {
        if (node == null) return null;
        if (node is JsonValue jv && jv.TryGetValue<int>(out var i)) return i;
        return int.TryParse(node.ToString(), out var parsed) ? parsed : null;
    }

    private static List<string> ReadStringArray(JsonNode node)
    {
        var result = new List<string>();
        if (node is not JsonArray arr) return result;
        foreach (var item in arr)
        {
            var value = item?.ToString()?.Trim();
            if (!string.IsNullOrEmpty(value)) result.Add(value);
        }
        return result;
    }

    private static bool MatchesDataFilters(JsonObject row, int? ageMin, int? ageMax, List<string> positions)
    {
        if (row == null || row["error"] != null) return false;
        if (ageMin.HasValue || ageMax.HasValue)
        {
            var age = ReadOptionalInt(row["age"]);
            if (!age.HasValue || (ageMin.HasValue && age.Value < ageMin.Value) || (ageMax.HasValue && age.Value > ageMax.Value)) return false;
        }
        if (positions.Count > 0)
        {
            var actualRaw = row["position"]?.ToString();
            var found = false;
            foreach (var wanted in positions)
            {
                if (PositionDecode.MatchesSlot(actualRaw, wanted)) { found = true; break; }
            }
            if (!found) return false;
        }
        return true;
    }

    private static bool IsCandidateWindowTruncated(int available, int scanned) =>
        available >= 0 && scanned >= 0 && available > scanned;

    private static JsonArray ToJsonArray(List<int> uids)
    {
        var arr = new JsonArray();
        foreach (var u in uids) arr.Add(u);
        return arr;
    }

    private static JsonArray ToJsonArray(List<string> values)
    {
        var arr = new JsonArray();
        foreach (var value in values) arr.Add(value);
        return arr;
    }

    private static JsonObject Fail(string error, Stopwatch sw, JsonArray steps)
    {
        sw.Stop();
        var o = new JsonObject { ["ok"] = false, ["error"] = error, ["latency_ms"] = sw.Elapsed.TotalMilliseconds };
        if (steps != null) o["reach"] = steps;
        return o;
    }
}
