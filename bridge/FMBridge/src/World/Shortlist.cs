using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using SI.Bindable;

namespace FMBridge.World;

/// <summary>
/// shortlist: the bridge's shortlist verb -- {"action":"list"|"create"|"add"|
/// "remove"}.
///
/// Architecture history: the FIRST implementation of this file fired
/// FM.GamePlugin.GameInteropSubsystem.SendEvent directly with
/// EventFmxCreateShortlist / PersonAddPlayersToShortlist /
/// PersonRemovePlayersFromShortlist -- calls returned ok, but a live
/// oracle check (RecruitmentScreen's Shortlists tab) proved every one of
/// those sends was a SILENT NO-OP: no shortlist was ever created, no player
/// was ever added/removed. A full live investigation then UI-DROVE the
/// real Shortlists tab and Player Database context menu by hand (raw
/// UiClick/UiFind2/TypeText/ListRead steps) until every one of the four
/// actions below was oracle-confirmed working through the game's own UI
/// Toolkit widgets -- THIS file encodes those proven-live recipes. No
/// SendEvent call remains anywhere below.
///
/// Reach reliability mirrors QueryPlayers.cs: every call re-drives
/// navigation from scratch (fast-path skip if already on the right screen),
/// with poll+retry discipline rather than fire-and-hope. Menu/dialog button
/// clicks use Navigator.ClickByText (Navigator.Shortlist.cs) -- a
/// SCOPED-BY-CONTENT click, not global name+index -- because live testing
/// found several unrelated FM UITK templates share the exact same node name
/// (e.g. "button-secondary-label" is both CreateShortlistDialog's Cancel
/// AND an unrelated "Clear" filter button elsewhere on screen; every row of
/// the ActionsDropdown context menu -- player header, "Add To Shortlist",
/// per-shortlist submenu entries, duration options, a dynamic
/// "Remove From Shortlist (X)" -- is literally named "ContentBaseElement").
///
/// Search-assist for out-of-window uids: the obvious plan would be "type
/// the player's name into the Player Database search box so the row
/// materializes" -- live recon found no such box exists to type into (a
/// scoped UiFind2 sweep of the filter bar plus a full-screen
/// TextField/unity-text-input scan both came back empty; the only
/// text-adjacent control, "Edit Search", opens a native, non-UITK,
/// non-drivable condition picker -- the same dead end that rules out
/// driving the table's range filters). Instead,
/// `add`/`remove` locate a target uid by SCROLLING it into view:
/// SI.UI.VirtualisedList exposes a public ScrollTo(int index)
/// (Navigator.ScrollToIndex), and the uid's global row index is found via a
/// CHUNKED, bounded scan (Navigator.ScanUidChunk, capped at 2000 rows/call --
/// ListRead's own already-live-proven-safe per-call magnitude) chained
/// across the table with a real awaited delay between chunks so Unity gets
/// actual frames between each bounded main-thread work item. This replaces
/// an earlier attempt that scanned the entire ~10,000-row backing list in
/// ONE synchronous main-thread callback and WEDGED THE GAME -- that
/// unbounded single-call shape must never come back here; every locate is a
/// bounded call sequence, never a single big one.
/// </summary>
internal static class Shortlist
{
    private const int PollIntervalMs = 200;
    private const int NavDeadlineMs = 6000;
    private const int MenuDeadlineMs = 4000;
    private const int SettleDelayMs = 400;
    private const int MaxListRows = 2000; // ListRead's own hard cap
    private const string DefaultDuration = "Indefinitely";

    // Live testing proved the "Yes" confirmation modal's close
    // animation/teardown is genuinely just SLOW, not stuck -- two separate
    // live "remove" runs both eventually self-closed the modal and the
    // oracle confirmed the removal really committed each time, but one run
    // took long enough (tens of seconds, well past NavDeadlineMs's 6000ms)
    // that the original modal-close poll gave up early and reported a
    // false "confirmation-modal-still-open-after-deadline" warning while
    // the very next oracle "list" call failed outright because the modal
    // was STILL silently eating its clicks. Give the close its own, much
    // longer budget instead of reusing the general nav deadline.
    private const int ModalCloseDeadlineMs = 30000;

    public static async Task<JsonObject> Enqueue(FMBridge.Voice.MainThreadQueue queue, string requestJson)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            JsonObject request;
            try { request = JsonNode.Parse(requestJson ?? "") as JsonObject; }
            catch (Exception e) { return Fail("bad json: " + e.Message, sw); }
            if (request == null) return Fail("request must be a json object", sw);

            var action = (string)request["action"];
            if (string.IsNullOrEmpty(action)) return Fail("shortlist requires 'action' (list|create|add|remove)", sw);

            switch (action)
            {
                case "list":
                {
                    var result = await DoList(queue);
                    return Finish(result, sw);
                }

                case "create":
                {
                    var name = (string)request["name"];
                    if (string.IsNullOrEmpty(name)) return Fail("create requires non-empty 'name'", sw);
                    var result = await DoCreate(queue, name);
                    return Finish(result, sw);
                }

                case "add":
                case "remove":
                {
                    var uidNode = request["uid"];
                    if (uidNode == null || !int.TryParse(uidNode.ToJsonString(), out var uid))
                        return Fail(action + " requires integer 'uid'", sw);
                    var name = (string)request["name"]; // target shortlist name; required for add, optional hint for remove
                    var duration = (string)request["duration"];
                    if (string.IsNullOrEmpty(duration)) duration = DefaultDuration;

                    if (action == "add")
                    {
                        if (string.IsNullOrEmpty(name)) return Fail("add requires 'name' (target shortlist name)", sw);
                        var result = await DoAdd(queue, uid, name, duration);
                        return Finish(result, sw);
                    }
                    else
                    {
                        var result = await DoRemove(queue, uid, name);
                        return Finish(result, sw);
                    }
                }

                default:
                    return Fail("unknown action '" + action + "'; valid: list, create, add, remove", sw);
            }
        }
        catch (Exception e)
        {
            return Fail(e.Message, sw);
        }
    }

    // ---------------------------------------------------------- actions

    /// <summary>
    /// Oracle-proven read recipe: RecruitmentScreen -> SecondaryTabDropdownButton
    /// index 0 (Shortlists tab) -> shortlist-title / shortlist-selection-dropdown
    /// text + playertableshortlist's full row list (uid per row).
    /// </summary>
    private static async Task<JsonObject> DoList(FMBridge.Voice.MainThreadQueue queue)
    {
        var (reached, reachError, steps) = await ReachShortlistsTab(queue);
        if (!reached) return new JsonObject { ["ok"] = false, ["error"] = "could-not-reach-shortlists-tab: " + reachError, ["reach"] = steps };

        var title = await OnQueue(queue, () => Navigator.FindByNameTexts("shortlist-title"));
        var selection = await OnQueue(queue, () => Navigator.FindByNameTexts("shortlist-selection-dropdown"));
        var rows = await OnQueue(queue, () => Navigator.ListRead("playertableshortlist", MaxListRows, -1));

        // NOTE: rows["rows"] is a JsonNode already parented under `rows`
        // (System.Text.Json.Nodes enforces single ownership -- reassigning an
        // already-attached child node into a NEW JsonObject throws
        // "The node already has a parent", live-caught during this
        // implementation). Build a fresh, standalone array instead of
        // aliasing the existing one.
        var uids = new JsonArray();
        var players = new JsonArray();
        if (rows?["ok"]?.GetValue<bool>() == true && rows["rows"] is JsonArray rowsArr)
        {
            foreach (var r in rowsArr)
            {
                var ro = r as JsonObject;
                if (ro?["uid"] == null) continue;
                var uid = (int)ro["uid"];
                uids.Add(uid);
                players.Add(new JsonObject { ["uid"] = uid, ["refType"] = (string)ro["refType"], ["text"] = (string)ro["text"] });
            }
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["reach"] = steps,
            ["title"] = FirstText(title),
            ["selection"] = FirstText(selection),
            ["count"] = AsInt(rows?["count"]),
            ["uids"] = uids,
            ["rows"] = players,
        };
    }

    /// <summary>
    /// Oracle-proven create recipe: Shortlists tab -> create-shortlist-button ->
    /// CreateShortlistDialog opens -> type name into its unity-text-input ->
    /// click the dialog's "Ok" (button-primary-label, matched BY TEXT, not
    /// global index -- see class doc comment) -> poll for the dialog to close
    /// and shortlist-title's count to change.
    /// </summary>
    private static async Task<JsonObject> DoCreate(FMBridge.Voice.MainThreadQueue queue, string name)
    {
        var (reached, reachError, steps) = await ReachShortlistsTab(queue);
        if (!reached) return new JsonObject { ["ok"] = false, ["error"] = "could-not-reach-shortlists-tab: " + reachError, ["reach"] = steps };

        var titleBefore = FirstText(await OnQueue(queue, () => Navigator.FindByNameTexts("shortlist-title")));

        var openClick = await OnQueue(queue, () => Navigator.UiClick("create-shortlist-button", 0));
        if (openClick?["ok"]?.GetValue<bool>() != true)
            return new JsonObject { ["ok"] = false, ["error"] = "create-button-click-failed: " + (string)openClick?["error"], ["reach"] = steps };

        bool dialogOpen = await PollUntil(queue,
            () => Navigator.FindByNameTexts("CreateShortlistDialog"),
            r => AsInt(r?["count"]) > 0, MenuDeadlineMs);
        if (!dialogOpen)
            return new JsonObject { ["ok"] = false, ["error"] = "create-shortlist-dialog-did-not-open", ["reach"] = steps };

        var typed = await OnQueue(queue, () => TypeText.Run("unity-text-input", name, 0, false));
        if (typed?["ok"]?.GetValue<bool>() != true)
            return new JsonObject { ["ok"] = false, ["error"] = "type-name-failed: " + (string)typed?["error"], ["reach"] = steps };

        var confirm = await OnQueue(queue, () => Navigator.ClickByText("button-primary-label", "Ok"));
        if (confirm?["ok"]?.GetValue<bool>() != true)
            return new JsonObject { ["ok"] = false, ["error"] = "confirm-click-failed: " + (string)confirm?["error"], ["typed"] = typed, ["reach"] = steps };

        bool dialogClosed = await PollUntil(queue,
            () => Navigator.FindByNameTexts("CreateShortlistDialog"),
            r => AsInt(r?["count"]) == 0, MenuDeadlineMs);

        string titleAfter = null;
        bool titleChanged = false;
        var deadline = Environment.TickCount64 + MenuDeadlineMs;
        while (Environment.TickCount64 < deadline)
        {
            titleAfter = FirstText(await OnQueue(queue, () => Navigator.FindByNameTexts("shortlist-title")));
            if (!string.IsNullOrEmpty(titleAfter) && titleAfter != titleBefore) { titleChanged = true; break; }
            await Task.Delay(PollIntervalMs);
        }

        return new JsonObject
        {
            ["ok"] = titleChanged,
            ["name"] = name,
            ["dialog_closed"] = dialogClosed,
            ["title_before"] = titleBefore,
            ["title_after"] = titleAfter,
            ["note"] = titleChanged ? "shortlist-created" : "dialog-confirmed-but-title-did-not-change",
            ["reach"] = steps,
        };
    }

    /// <summary>
    /// Oracle-proven add recipe: Player Database -> locate uid's row (must be
    /// within the currently-materialized virtualized window) -> click its
    /// unity-checkmark -> ActionsDropdown -> "Add To Shortlist" -> the target
    /// shortlist's name -> a duration -- COMMITS. If the player is already a
    /// member, the top-level menu instead shows "Remove From Shortlist (X)"
    /// with no submenu; that's detected and reported as already-a-member
    /// rather than mis-clicking through a nonexistent "Add To Shortlist".
    /// </summary>
    private static async Task<JsonObject> DoAdd(FMBridge.Voice.MainThreadQueue queue, int uid, string shortlistName, string duration)
    {
        var (located, locateError, locateInfo) = await LocateAndOpenActionsMenu(queue, uid);
        if (!located) return new JsonObject { ["ok"] = false, ["error"] = locateError, ["locate"] = locateInfo };

        var menu = await OnQueue(queue, () => Navigator.FindByNameTexts("ContentBaseElement"));
        bool alreadyMember = TextsContain(menu, "Remove From Shortlist");
        if (alreadyMember)
        {
            await CloseActionsMenu(queue);
            return new JsonObject { ["ok"] = true, ["uid"] = uid, ["name"] = shortlistName, ["note"] = "already-a-shortlist-member (menu showed Remove From Shortlist)", ["locate"] = locateInfo };
        }

        var addClick = await OnQueue(queue, () => Navigator.ClickByText("ContentBaseElement", "Add To Shortlist"));
        if (addClick?["ok"]?.GetValue<bool>() != true)
            return new JsonObject { ["ok"] = false, ["error"] = "add-to-shortlist-click-failed: " + (string)addClick?["error"], ["menu"] = menu, ["locate"] = locateInfo };

        // The "Add To Shortlist" submenu has TWO shapes. With more than one
        // shortlist it lists shortlist NAMES (each opening its own duration
        // sub-level). With exactly ONE shortlist the game SKIPS the name
        // level entirely and opens the duration list directly -- live-
        // reproduced 2026-08-29 on a save whose only shortlist was
        // "(Default)": the submenu showed Indefinitely/1 Month/3 Months/
        // 6 Months/1 Year and no shortlist name anywhere, so the old
        // names-only poll below timed out and every add failed with
        // shortlist-name-not-in-submenu. Poll for EITHER shape and branch.
        bool namesShown = false, durationsDirect = false;
        var submenuDeadline = Environment.TickCount64 + MenuDeadlineMs;
        while (Environment.TickCount64 < submenuDeadline)
        {
            var sub = await OnQueue(queue, () => Navigator.FindByNameTexts("ContentBaseElement"));
            if (TextsContain(sub, shortlistName)) { namesShown = true; break; }
            if (TextsContain(sub, duration)) { durationsDirect = true; break; }
            await Task.Delay(PollIntervalMs);
        }
        if (!namesShown && !durationsDirect)
        {
            var seen = await OnQueue(queue, () => Navigator.FindByNameTexts("ContentBaseElement"));
            await CloseActionsMenu(queue);
            return new JsonObject { ["ok"] = false, ["error"] = "shortlist-name-not-in-submenu:" + shortlistName, ["seen"] = seen, ["locate"] = locateInfo };
        }

        string submenuNote = "name-level-clicked";
        if (namesShown)
        {
            var nameClick = await OnQueue(queue, () => Navigator.ClickByText("ContentBaseElement", shortlistName));
            if (nameClick?["ok"]?.GetValue<bool>() != true)
                return new JsonObject { ["ok"] = false, ["error"] = "shortlist-name-click-failed: " + (string)nameClick?["error"], ["locate"] = locateInfo };

            bool durationsShown = await PollUntil(queue,
                () => Navigator.FindByNameTexts("ContentBaseElement"),
                r => TextsContain(r, duration), MenuDeadlineMs);
            if (!durationsShown)
            {
                var seen = await OnQueue(queue, () => Navigator.FindByNameTexts("ContentBaseElement"));
                return new JsonObject { ["ok"] = false, ["error"] = "duration-not-in-submenu:" + duration, ["seen"] = seen, ["locate"] = locateInfo };
            }
        }
        else
        {
            // Single-shortlist shape: the game offers no name to click, so
            // the add can only go to the one shortlist that exists. Note it
            // in the result so a caller who asked for a name that does NOT
            // match that sole shortlist can see what actually happened.
            submenuNote = "single-shortlist:name-level-skipped-by-game";
        }

        var durationClick = await OnQueue(queue, () => Navigator.ClickByText("ContentBaseElement", duration));
        if (durationClick?["ok"]?.GetValue<bool>() != true)
            return new JsonObject { ["ok"] = false, ["error"] = "duration-click-failed: " + (string)durationClick?["error"], ["locate"] = locateInfo };

        await Task.Delay(SettleDelayMs);
        return new JsonObject { ["ok"] = true, ["uid"] = uid, ["name"] = shortlistName, ["duration"] = duration, ["note"] = "add-committed", ["submenu"] = submenuNote, ["locate"] = locateInfo };
    }

    /// <summary>
    /// Oracle-proven remove recipe: same checkbox->ActionsDropdown reach as
    /// add, then click the single top-level "Remove From Shortlist (X)" item
    /// (no submenu) -- which opens a MANDATORY confirmation modal (FM's own
    /// UITK GenericModalDialog family, not a native blocking dialog) that
    /// silently blocks all further input until dismissed. Clicking its
    /// "Yes" (buttons-button-primary-default) commits the removal.
    /// </summary>
    private static async Task<JsonObject> DoRemove(FMBridge.Voice.MainThreadQueue queue, int uid, string shortlistName)
    {
        var (located, locateError, locateInfo) = await LocateAndOpenActionsMenu(queue, uid);
        if (!located) return new JsonObject { ["ok"] = false, ["error"] = locateError, ["locate"] = locateInfo };

        var menu = await OnQueue(queue, () => Navigator.FindByNameTexts("ContentBaseElement"));
        if (!TextsContain(menu, "Remove From Shortlist"))
        {
            await CloseActionsMenu(queue);
            return new JsonObject { ["ok"] = true, ["uid"] = uid, ["note"] = "not-a-shortlist-member (no Remove From Shortlist item present)", ["menu"] = menu, ["locate"] = locateInfo };
        }

        var removeClick = await OnQueue(queue, () => Navigator.ClickByText("ContentBaseElement",
            string.IsNullOrEmpty(shortlistName) ? "Remove From Shortlist" : "Remove From Shortlist (" + shortlistName + ")"));
        if (removeClick?["ok"]?.GetValue<bool>() != true)
        {
            // Fall back to the bare phrase if the caller's exact name/casing
            // doesn't match what's rendered -- still text-scoped, just less
            // specific.
            removeClick = await OnQueue(queue, () => Navigator.ClickByText("ContentBaseElement", "Remove From Shortlist"));
        }
        if (removeClick?["ok"]?.GetValue<bool>() != true)
            return new JsonObject { ["ok"] = false, ["error"] = "remove-click-failed: " + (string)removeClick?["error"], ["menu"] = menu, ["locate"] = locateInfo };

        bool modalShown = await PollUntil(queue,
            () => Navigator.FindByNameTexts("buttons-button-primary-default"),
            r => AsInt(r?["count"]) > 0, MenuDeadlineMs);
        if (!modalShown)
            return new JsonObject { ["ok"] = false, ["error"] = "confirmation-modal-did-not-appear", ["locate"] = locateInfo };

        // Live testing found the modal's own OPEN animation is
        // still running at the instant its buttons first appear in the
        // tree -- a click sent right on that transition can be silently
        // swallowed (same class of hazard as the tab-switch clicks
        // documented elsewhere in this file), leaving the confirmation
        // modal open INDEFINITELY, not just slow to close: one live run sat
        // unchanged for 60+ seconds until a manual re-click of "Yes"
        // dismissed it instantly. A single fire-and-poll click isn't
        // reliable; settle briefly after the modal appears, then click and
        // RE-CLICK "Yes" on a short cadence until it's actually gone,
        // bounded by ModalCloseDeadlineMs overall.
        await Task.Delay(SettleDelayMs);

        bool modalClosed = false;
        int confirmClickAttempts = 0;
        JsonObject lastConfirm = null;
        var confirmDeadline = Environment.TickCount64 + ModalCloseDeadlineMs;
        while (Environment.TickCount64 < confirmDeadline)
        {
            confirmClickAttempts++;
            lastConfirm = await OnQueue(queue, () => Navigator.ClickByText("buttons-button-primary-default", "Yes"));
            // A click failing with "element-not-found" here means the modal
            // already closed between our last check and this click attempt
            // -- that's success, not an error.
            bool closedNow = await PollUntil(queue,
                () => Navigator.FindByNameTexts("notifications-modal-dialog-default-default"),
                r => AsInt(r?["count"]) == 0, 3000);
            if (closedNow) { modalClosed = true; break; }
        }
        var result = new JsonObject
        {
            ["ok"] = true,
            ["uid"] = uid,
            ["name"] = shortlistName,
            ["note"] = "remove-committed",
            ["locate"] = locateInfo,
            ["modal_closed"] = modalClosed,
            ["confirm_click_attempts"] = confirmClickAttempts,
        };
        if (!modalClosed)
        {
            // Don't silently swallow this: report ok:true (the click itself
            // succeeded at least once) but flag that the modal was still
            // visible when we gave up waiting/retrying, so a caller relying
            // on immediate follow-up calls knows why they might still fail.
            result["warning"] = "confirmation-modal-still-open-after-deadline";
            result["last_confirm_click"] = lastConfirm;
        }
        await Task.Delay(SettleDelayMs);
        return result;
    }

    // ------------------------------------------------ shared reach/locate

    /// <summary>Closes an open ActionsDropdown/ContentBaseElement menu by
    /// re-clicking the same checkbox that opened it (proven to toggle it
    /// closed), so an early-return path (already-a-member / not-a-member)
    /// never leaves the menu stuck open for the next call.</summary>
    private static async Task CloseActionsMenu(FMBridge.Voice.MainThreadQueue queue)
    {
        try { await OnQueue(queue, () => Navigator.ClickPoint(_lastClickPointX, _lastClickPointY)); }
        catch { }
        await Task.Delay(PollIntervalMs);
    }

    private static int _lastClickPointX = 0;
    private static int _lastClickPointY = 0;

    // Bounded chunked-scan tuning (see class doc comment / ScanUidChunk doc
    // comment in Navigator.ListRead.cs): each chunk is well under
    // ScanUidChunk's own 2000-row hard cap, and a REAL awaited delay
    // separates consecutive chunks so Unity's Update loop gets actual frames
    // between bounded main-thread work items -- this is the load-bearing
    // difference from the earlier single-call full scan that wedged the game.
    private const int ScanChunkSize = 1000;
    private const int ScanChunkDelayMs = 120;
    private const int ScanMaxChunks = 60; // hard ceiling: 60,000 rows scanned, never open-ended

    // A backing-list total this small almost certainly means a stray filter
    // (e.g. "Scouted players only") is still applied, not that the full
    // Player Database genuinely shrank -- see the uid-not-found hint below.
    private const int SuspiciouslySmallTableThreshold = 5000;

    // Row-0-after-scroll settle (live-reproduced independently in
    // verification runs): a short extra delay after a scroll settles by
    // window-range but before the checkbox scan, on top of the table_rect
    // stabilize loop already below.
    private const int PostScrollSettleMs = 500;

    // Row-resolve retry tuning: bounded re-attempts of the
    // checkbox-resolve->click->menu->name-check sequence when the name
    // cross-check catches a wrong-row click (or the click/menu path fails
    // outright) -- never open-ended, and every attempt still goes through
    // the same name-check safety net, so a retry can never silently commit
    // against the wrong player.
    private const int MaxRowResolveAttempts = 3;
    private const int RowResolveRetryDelayMs = 800;

    // Bounded retry window for the ActionsDropdown's own first click landing
    // on a not-yet-laid-out ("zero-bounds") element right after it appears.
    private const int ActionsDropdownClickDeadlineMs = 1600;

    /// <summary>
    /// Reaches the main Player Database table (reusing QueryPlayers'
    /// proven-live navigation recipe), locates the target uid's row via a
    /// bounded chunked scan of the full backing list (Navigator.ScanUidChunk,
    /// never one big scan -- see class doc comment), scrolls it into view
    /// with Navigator.ScrollToIndex if it isn't already rendered, clicks its
    /// checkbox, and opens the row's ActionsDropdown menu. Returns (ok,
    /// error, diagnostics) -- diagnostics always attached so a failure to
    /// locate/click is debuggable.
    /// </summary>
    private static async Task<(bool ok, string error, JsonObject info)> LocateAndOpenActionsMenu(FMBridge.Voice.MainThreadQueue queue, int uid)
    {
        var info = new JsonObject { ["uid"] = uid };

        var guardSteps = new JsonArray();
        bool stuckModalCleared = await WaitForStuckModalToClear(queue, guardSteps);
        if (guardSteps.Count > 0) info["modal_guard"] = guardSteps;
        if (!stuckModalCleared) return (false, "stuck-confirmation-modal-did-not-clear", info);

        var (reached, reachError, reachSteps, _) = await QueryPlayers.ReachPlayerDatabase(queue);
        info["reach"] = reachSteps;
        if (!reached) return (false, "could-not-reach-player-database: " + reachError, info);

        int globalIndex = -1;
        int total = -1;
        int chunkStart = 0;
        int chunksScanned = 0;
        while (chunksScanned < ScanMaxChunks)
        {
            var chunk = await OnQueue(queue, () => Navigator.ScanUidChunk("playertable", chunkStart, ScanChunkSize));
            chunksScanned++;
            if (chunk?["ok"]?.GetValue<bool>() != true)
                return (false, "scan-uid-chunk-failed: " + (string)chunk?["error"], info);

            total = AsInt(chunk["total"]);
            int scanned = AsInt(chunk["scanned"]);
            if (chunk["rows"] is JsonArray chunkRows)
            {
                foreach (var r in chunkRows)
                {
                    var ro = r as JsonObject;
                    if (ro?["uid"] != null && (string)ro["refType"] == "Person" && (int)ro["uid"] == uid)
                    { globalIndex = AsInt(ro["index"]); break; }
                }
            }
            if (globalIndex >= 0) break;
            if (scanned < ScanChunkSize) break; // ran past the end of the backing list
            chunkStart += ScanChunkSize;
            if (total > 0 && chunkStart >= total) break;
            await Task.Delay(ScanChunkDelayMs); // real frame gap between bounded chunks
        }
        info["chunks_scanned"] = chunksScanned;
        info["total_rows"] = total;
        if (globalIndex < 0)
        {
            // Self-diagnosing hint (found during live verification):
            // a stuck "Scouted players only" filter toggle (left on by a
            // prior query_players call whose revert silently failed) makes
            // this exhaustive scan see a much smaller filtered backing list
            // (observed total=872) instead of the real unfiltered 10000 --
            // an honest uid-not-found in that state is misleading, not
            // wrong: the uid may well exist but never appear in the
            // filtered set. A suspiciously small total is a strong signal
            // to check for that before concluding the uid genuinely doesn't
            // exist in the game world.
            string hint = (total > 0 && total < SuspiciouslySmallTableThreshold)
                ? $" (table-may-be-filtered: total={total} < {SuspiciouslySmallTableThreshold} -- check for a stuck filter toggle, e.g. 'Scouted players only', before concluding the uid genuinely doesn't exist)"
                : "";
            return (false, "uid-not-found-in-player-database:" + uid + hint, info);
        }
        info["global_index"] = globalIndex;

        // DIAGNOSTIC: a bounded, cheap re-scan of the backing
        // list immediately around globalIndex, to distinguish "the
        // chunked-scan's global_index for this uid is itself correct but
        // the checkbox/row click still lands on the wrong player" from
        // "global_index was never right in the first place" when a
        // name-check mismatch is being investigated. Costs one extra tiny
        // (<=8-row) ScanUidChunk call, well within the proven-safe per-call
        // budget.
        try
        {
            int nearbyStart = Math.Max(0, globalIndex - 3);
            var nearbyChunk = await OnQueue(queue, () => Navigator.ScanUidChunk("playertable", nearbyStart, 8));
            if (nearbyChunk?["ok"]?.GetValue<bool>() == true)
            {
                var rowsJson = nearbyChunk["rows"]?.ToJsonString();
                info["nearby_backing_list"] = rowsJson != null ? JsonNode.Parse(rowsJson) : null;
            }
        }
        catch { }

        // Is the row already rendered? Cheap window-only read, no decode.
        var winCheck = await OnQueue(queue, () => Navigator.GetVisibleWindow("playertable"));
        int winStart = AsInt(winCheck?["visibleWindow"]?["start"]);
        int winCount = AsInt(winCheck?["visibleWindow"]?["count"]);
        bool inWindow = winCheck?["ok"]?.GetValue<bool>() == true && winStart >= 0 && winCount > 0
            && globalIndex >= winStart && globalIndex < winStart + winCount;

        if (!inWindow)
        {
            // Row-0-after-scroll hazard (independently reproduced live,
            // twice, against the same target uid --
            // scroll succeeded, window settled with the target at
            // window_index 0, checkbox click reported ok:true, but the
            // ActionsDropdown never appeared). Scrolling
            // the exact target index to the TOP of the window
            // (SI.UI.VirtualisedList.ScrollTo's own behavior) puts it right
            // where the "select-all" header checkbox hazard lives and/or
            // where the checkbox column is most likely to still be stale
            // immediately post-scroll. Scroll to a point that lands the
            // target row in the MIDDLE of the window instead, so it never
            // sits at window_index 0.
            int approxWinCount = winCount > 0 ? winCount : 15; // pre-scroll GetVisibleWindow may not have returned a usable count
            int scrollTarget = Math.Max(0, globalIndex - approxWinCount / 2);
            var scroll = await OnQueue(queue, () => Navigator.ScrollToIndex("playertable", scrollTarget));
            info["scroll_to_index"] = scroll;
            info["scroll_target"] = scrollTarget;
            if (scroll?["ok"]?.GetValue<bool>() != true)
                return (false, "scroll-to-index-failed: " + (string)scroll?["error"], info);

            bool settled = false;
            var scrollDeadline = Environment.TickCount64 + NavDeadlineMs;
            while (Environment.TickCount64 < scrollDeadline)
            {
                var w = await OnQueue(queue, () => Navigator.GetVisibleWindow("playertable"));
                winStart = AsInt(w?["visibleWindow"]?["start"]);
                winCount = AsInt(w?["visibleWindow"]?["count"]);
                if (w?["ok"]?.GetValue<bool>() == true && winStart >= 0 && winCount > 0
                    && globalIndex >= winStart && globalIndex < winStart + winCount)
                { settled = true; break; }
                await Task.Delay(PollIntervalMs);
            }
            if (!settled)
                return (false, $"scroll-did-not-bring-row-into-view: global_index={globalIndex}, last_window=[{winStart},{winStart + winCount})", info);

            // Extra settle beyond "the window range already covers the
            // target" -- live testing elsewhere in this function found the
            // checkbox column specifically can still be transiently
            // stale/incomplete right after a scroll settles by window-range
            // alone (the table_rect stabilize loop below already guards
            // against this generally; this is a narrower belt-and-braces
            // delay for the "just scrolled" transition specifically).
            await Task.Delay(PostScrollSettleMs);
        }

        int windowIndex = globalIndex - winStart;
        info["window"] = new JsonObject { ["start"] = winStart, ["count"] = winCount };
        info["window_index"] = windowIndex;
        if (windowIndex < 0 || windowIndex >= winCount)
            return (false, $"uid-outside-rendered-window-after-scroll: global_index={globalIndex}, window=[{winStart},{winStart + winCount})", info);

        // Independently resolve the target uid's real name ONCE, up front --
        // this doesn't depend on any click/scroll state, only on the uid
        // itself (a fresh PersonReference fabrication via ReadEntity), so
        // it's reused across every row-resolve attempt below.
        string expectedName = null;
        try
        {
            var nameRead = await ReadEntity.ReadOneEntity(queue, "person", uid, new List<string> { "Name" });
            if (nameRead?["found"]?.GetValue<bool>() == true)
                expectedName = nameRead["data"]?["Name"]?.ToString();
        }
        catch { }
        info["expected_name"] = expectedName;

        // Row-resolve retry loop (added after live verification runs):
        // the checkbox-completeness hazard documented below (scoped
        // unity-checkmark scan landing short of winCount) doesn't always
        // resolve within one 6-second stabilize window, and when it doesn't,
        // indexing into the incomplete/possibly-misordered result can land
        // on the WRONG row. The name-check safety net below already
        // guarantees this is never a SILENT wrong commit, but a single
        // mismatch used to just give up outright. Retry the whole
        // resolve->click->menu->name-check sequence a bounded few times --
        // a fresh rect+checkbox scan on a later attempt has a real chance of
        // landing on a more complete/differently-ordered result -- before
        // finally reporting failure.
        var resolveAttempts = new JsonArray();
        string lastError = "row-resolve-attempts-exhausted";
        for (int attempt = 1; attempt <= MaxRowResolveAttempts; attempt++)
        {
            var (attemptOk, attemptError) = await TryResolveClickAndVerify(queue, uid, expectedName, windowIndex, winCount, info);
            resolveAttempts.Add(new JsonObject { ["attempt"] = attempt, ["ok"] = attemptOk, ["error"] = attemptError });
            if (attemptOk)
            {
                info["row_resolve_attempts"] = resolveAttempts;
                return (true, null, info);
            }
            lastError = attemptError;
            await Task.Delay(RowResolveRetryDelayMs);
        }
        info["row_resolve_attempts"] = resolveAttempts;
        return (false, lastError, info);
    }

    /// <summary>
    /// One attempt at: stabilize the playertable rect + its scoped checkbox
    /// column, click the target windowIndex's checkbox, open the
    /// ActionsDropdown/ContentBaseElement menu, and verify (via
    /// expectedName, when resolvable) that the menu that opened really is
    /// for the target player. Populates the shared `info` diagnostic object
    /// with this attempt's findings (overwriting any previous attempt's --
    /// the full per-attempt history lives in the caller's
    /// `row_resolve_attempts` array). On any failure, best-effort closes
    /// whatever got opened/checked so the next attempt (or the caller) sees
    /// clean state.
    /// </summary>
    private static async Task<(bool ok, string error)> TryResolveClickAndVerify(
        FMBridge.Voice.MainThreadQueue queue, int uid, string expectedName, int windowIndex, int winCount, JsonObject info)
    {
        // Resolve the row's checkbox by REGION, not a raw global name+index:
        // live testing found a "select-all" header checkbox (positioned
        // ABOVE the table's own worldBound) plus several unrelated
        // off-screen panels' checkmarks all share the exact node name
        // "unity-checkmark" -- a naive index computed from windowIndex alone
        // landed one row off (target uid 8293 at window index 4 actually
        // clicked uid 6716's row). Scoping the scan to the playertable
        // widget's own worldBound excludes all of that, so the Nth match
        // inside the region really is the Nth visible ROW.
        // Stabilize the table's own worldBound AND its checkbox column
        // before trusting either: live testing (after the
        // playertable/playertableshortlist naming fix) found that
        // ReachPlayerDatabase's itemCount-stable check does NOT imply the
        // widget's on-screen layout has finished settling -- back-to-back
        // calls (especially right after leaving the Shortlists tab, which
        // apparently lands Player Database in a transiently narrower/
        // still-animating layout) observed table_rect visibly narrower than
        // its final width (e.g. w:1596 settled vs w:1436/1439/1495/1523 seen
        // mid-transition) for several seconds, AND during that same window
        // the checkbox column can be entirely unrendered (scoped_count:0)
        // even though the table_rect itself had already stopped moving
        // between two consecutive reads -- i.e. rect stability alone is not
        // a reliable proxy for "the row is actually clickable". Poll BOTH:
        // table_rect stable across 2 consecutive reads AND a checkbox scan
        // scoped to that rect returns at least winCount checkmarks (one per
        // visible row) before committing to a click.
        JsonObject tableRow = null;
        JsonObject prevRow = null;
        JsonArray checkRows = null;
        var rectDeadline = Environment.TickCount64 + NavDeadlineMs;
        int rectAttempts = 0;
        while (Environment.TickCount64 < rectDeadline)
        {
            rectAttempts++;
            var tableRect = await OnQueue(queue, () => Navigator.UiFind2("playertable", 5, 0, 0, 0, 0, false));
            JsonObject candidate = null;
            if (tableRect?["ok"]?.GetValue<bool>() == true && tableRect["rows"] is JsonArray trArr)
            {
                foreach (var r in trArr)
                {
                    var ro = r as JsonObject;
                    if (ro == null) continue;
                    if (candidate == null || AsInt(ro["w"]) * AsInt(ro["h"]) > AsInt(candidate["w"]) * AsInt(candidate["h"]))
                        candidate = ro;
                }
            }
            if (candidate != null)
            {
                var fresh = new JsonObject { ["x"] = AsInt(candidate["x"]), ["y"] = AsInt(candidate["y"]), ["w"] = AsInt(candidate["w"]), ["h"] = AsInt(candidate["h"]) };
                bool rectStable = prevRow != null && AsInt(prevRow["x"]) == AsInt(fresh["x"]) && AsInt(prevRow["y"]) == AsInt(fresh["y"])
                    && AsInt(prevRow["w"]) == AsInt(fresh["w"]) && AsInt(prevRow["h"]) == AsInt(fresh["h"]);
                prevRow = fresh;
                tableRow = fresh; // last-seen fallback if we hit the deadline
                if (rectStable)
                {
                    int ftx = AsInt(fresh["x"]), fty = AsInt(fresh["y"]), ftw = AsInt(fresh["w"]), fth = AsInt(fresh["h"]);
                    var scan = await OnQueue(queue, () => Navigator.UiFind2("unity-checkmark", 200, ftx, fty, ftx + ftw, fty + fth, false));
                    if (scan?["ok"]?.GetValue<bool>() == true && scan["rows"] is JsonArray scanRows && scanRows.Count >= winCount)
                    {
                        checkRows = scanRows;
                        break;
                    }
                }
            }
            await Task.Delay(PollIntervalMs);
        }
        if (tableRow == null)
            return (false, "playertable-widget-bounds-not-found");
        int tx = AsInt(tableRow["x"]), ty = AsInt(tableRow["y"]), tw = AsInt(tableRow["w"]), th = AsInt(tableRow["h"]);
        info["table_rect"] = new JsonObject { ["x"] = tx, ["y"] = ty, ["w"] = tw, ["h"] = th };
        info["table_rect_stabilize_attempts"] = rectAttempts;

        if (checkRows == null)
        {
            // Deadline hit without ever seeing a stable rect + full checkbox
            // column together -- fall back to one last scan against
            // whatever rect we last saw, so the error diagnostics show the
            // real on-screen state instead of a bare timeout.
            var scopedChecks = await OnQueue(queue, () => Navigator.UiFind2("unity-checkmark", 200, tx, ty, tx + tw, ty + th, false));
            if (scopedChecks?["ok"]?.GetValue<bool>() != true || scopedChecks["rows"] is not JsonArray fallbackRows)
                return (false, "checkbox-scoped-scan-failed: " + (string)scopedChecks?["error"]);
            checkRows = fallbackRows;
        }

        // Resolve windowIndex's checkbox by EXPECTED ON-SCREEN POSITION, not
        // by indexing into the scan array (live-reproduced twice): a
        // plain "sort by y then index by windowIndex" fix is NOT
        // sufficient on its own, because the scoped unity-checkmark scan can
        // also be INCOMPLETE relative to winCount (observed
        // checkbox_scoped_count 15 vs winCount 19 on the very case that
        // exposed this) -- once even one row's checkbox goes undetected,
        // every subsequent sorted-array index is off by one relative to
        // true visual row position, so positional indexing (sorted or not)
        // is fundamentally unsound whenever the scan is short.
        //
        // A PURE geometry estimate (uniform row height = table height /
        // winCount) ALSO proved insufficient on its own (live testing
        // after the geometry-only fix still landed on the wrong
        // player twice in a row, with DIFFERENT wrong players each time --
        // "Rúben Neves" then "Memphis Depay" -- for the identical uid,
        // window_index and table_rect, which rules out a simple static
        // offset and points at winCount/row-geometry themselves not being
        // trustworthy to sub-row precision here).
        //
        // So: when we can independently resolve the target's real display
        // name (expectedName, from ReadEntity -- already resolved above,
        // with zero dependency on any of this table/scroll state), scan the
        // table's own rendered TEXT for that name FIRST and anchor the
        // target y to wherever it actually rendered -- this ties row
        // selection to ground truth (what is really on screen right now)
        // instead of an assumption about row layout. Only fall back to the
        // geometry estimate when the name can't be found in the rendered
        // text at all (e.g. expectedName unresolved, or a rendering/timing
        // gap) -- in which case the row-resolve retry loop and the
        // independent name-check safety net below are the backstop.
        double? nameMatchCenterY = null;
        if (!string.IsNullOrEmpty(expectedName))
        {
            var normExpected = NormalizeForMatch(expectedName);
            // max=2000 (the API's own hard ceiling): a table row with many
            // stat/status columns can carry dozens of elements each, so a
            // ~15-row visible window can exceed a few hundred matches before
            // even reaching the target row -- live testing with max=500
            // found ZERO matches for a name that genuinely was on screen,
            // consistent with the scan's match cap being hit mid-window
            // (DFS visits rows top-to-bottom) before reaching the target row.
            var textScan = await OnQueue(queue, () => Navigator.UiFind2("", 2000, tx, ty, tx + tw, ty + th, true));
            if (textScan?["ok"]?.GetValue<bool>() == true && textScan["rows"] is JsonArray textRows)
            {
                foreach (var tr in textRows)
                {
                    if (tr is not JsonObject to) continue;
                    var txt = (string)to["text"];
                    if (!string.IsNullOrEmpty(txt) && NormalizeForMatch(txt).Contains(normExpected))
                    {
                        nameMatchCenterY = AsInt(to["y"]) + AsInt(to["h"]) / 2.0;
                        break;
                    }
                }
            }
        }
        double rowHeight = winCount > 0 ? (double)th / winCount : 0;
        double targetCenterY = nameMatchCenterY ?? (ty + (windowIndex + 0.5) * rowHeight);
        info["row_match_source"] = nameMatchCenterY.HasValue ? "name-text-scan" : "geometry-estimate";
        JsonObject cbRow = null;
        double bestDist = double.MaxValue;
        foreach (var r in checkRows)
        {
            if (r is not JsonObject ro) continue;
            double rowCenterY = AsInt(ro["y"]) + AsInt(ro["h"]) / 2.0;
            double dist = Math.Abs(rowCenterY - targetCenterY);
            if (dist < bestDist)
            {
                bestDist = dist;
                cbRow = ro;
            }
        }
        info["checkbox_scan_order"] = "matched-by-expected-position";
        info["row_match_target_y"] = targetCenterY;
        info["row_match_best_distance"] = bestDist;
        info["checkbox_scoped_count"] = checkRows.Count;
        if (cbRow == null)
            return (false, $"checkbox-not-in-scoped-region: window_index={windowIndex}, scoped_count={checkRows.Count}");
        // A best match further off than half a row's height means no
        // scanned checkbox is actually plausible for this row -- almost
        // certainly this row's own checkbox is one of the ones missing
        // from an incomplete scan, not a genuine (if imperfect) hit.
        if (rowHeight > 0 && bestDist > rowHeight * 0.75)
            return (false, $"checkbox-position-match-too-far: window_index={windowIndex}, target_y={targetCenterY:F1}, best_y={AsInt(cbRow["y"]) + AsInt(cbRow["h"]) / 2.0:F1}, distance={bestDist:F1}, scoped_count={checkRows.Count}");

        int cx = AsInt(cbRow["x"]) + AsInt(cbRow["w"]) / 2;
        int cy = AsInt(cbRow["y"]) + AsInt(cbRow["h"]) / 2;
        info["checkbox_point"] = new JsonObject { ["x"] = cx, ["y"] = cy };

        _lastClickPointX = cx;
        _lastClickPointY = cy;
        var check = await OnQueue(queue, () => Navigator.ClickPoint(cx, cy));
        info["checkbox_click"] = check;
        if (check?["ok"]?.GetValue<bool>() != true)
            return (false, "checkbox-click-failed: " + (string)check?["error"]);

        bool dropdownShown = await PollUntil(queue,
            () => Navigator.FindByNameTexts("ActionsDropdown"),
            r => AsInt(r?["count"]) > 0, MenuDeadlineMs);
        if (!dropdownShown)
        {
            // Best-effort: the click may have toggled the checkbox ON with
            // no dropdown ever appearing -- re-click the same point so a
            // retry (or the caller) doesn't inherit a stray checked row.
            await CloseActionsMenu(queue);
            return (false, "actionsdropdown-did-not-appear-after-checkbox-click");
        }

        // ActionsDropdown existing (dropdownShown above) only proves the
        // NODE is queryable, not that its layout bounds are already
        // non-zero -- live testing found a first click right
        // after the node appears can fail with "zero-bounds" (the same
        // transient-layout hazard QueryPlayers.ClickToggleWithRetry already
        // special-cases for a different element) even though a click a few
        // hundred ms later on the exact same node succeeds. Retry briefly
        // on that specific error before giving up.
        JsonObject openMenu = null;
        var dropdownClickDeadline = Environment.TickCount64 + ActionsDropdownClickDeadlineMs;
        while (Environment.TickCount64 < dropdownClickDeadline)
        {
            openMenu = await OnQueue(queue, () => Navigator.UiClick("ActionsDropdown", 0));
            if (openMenu?["ok"]?.GetValue<bool>() == true) break;
            if ((string)openMenu?["error"] != "zero-bounds") break;
            await Task.Delay(PollIntervalMs);
        }
        info["actionsdropdown_click"] = openMenu;
        if (openMenu?["ok"]?.GetValue<bool>() != true)
        {
            await CloseActionsMenu(queue);
            return (false, "actionsdropdown-click-failed: " + (string)openMenu?["error"]);
        }

        bool menuShown = await PollUntil(queue,
            () => Navigator.FindByNameTexts("ContentBaseElement"),
            r => AsInt(r?["count"]) > 0, MenuDeadlineMs);
        if (!menuShown)
        {
            await CloseActionsMenu(queue);
            return (false, "context-menu-did-not-appear-after-actionsdropdown-click");
        }

        // CRITICAL SAFETY NET (live testing): the checkbox->row
        // mapping above can silently resolve to the WRONG row when the
        // scoped unity-checkmark scan is incomplete relative to winCount
        // (observed checkbox_scoped_count of 15-16 vs winCount of 19,
        // table_rect_stabilize_attempts as high as 22) -- the fallback path
        // still blindly indexes checkRows[windowIndex] from that
        // incomplete/possibly-misordered list. A wrong click still opens A
        // context menu (just for the wrong player), so `menuShown` above
        // proves only that SOME menu opened, not WHICH player's menu it is.
        // A live run reproduced exactly this: an "add uid 8293" call
        // reported ok:true/add-committed, but the oracle showed uid 32173
        // (a different player entirely) had actually been added -- with no
        // signal of the mistake anywhere in the response. Independently
        // resolve the target uid's real name (a fresh PersonReference
        // fabrication via ReadEntity -- no dependency on the player-database
        // navigation/scroll state at all) and cross-check it against the
        // just-opened menu's own displayed text before trusting this
        // locate. "Never guess" applies to a false "opened the right menu"
        // exactly as much as to a fabricated stat: abort honestly on a
        // mismatch rather than let DoAdd/DoRemove commit against the wrong
        // player.
        var openedMenu = await OnQueue(queue, () => Navigator.FindByNameTexts("ContentBaseElement"));
        // openedMenu["texts"] is already parented under openedMenu --
        // System.Text.Json.Nodes forbids re-parenting an attached child into
        // a different JsonObject (see DoList's comment above for the same
        // hazard), so re-parse a standalone copy rather than alias it.
        try
        {
            var textsJson = openedMenu?["texts"]?.ToJsonString();
            info["opened_menu_texts"] = textsJson != null ? JsonNode.Parse(textsJson) : null;
        }
        catch { }
        if (!string.IsNullOrEmpty(expectedName))
        {
            bool nameMatches = TextsContain(openedMenu, expectedName);
            info["name_check"] = nameMatches ? "matched" : "mismatch";
            if (!nameMatches)
            {
                await CloseActionsMenu(queue);
                return (false, $"wrong-row-clicked: expected-name=\"{expectedName}\" (uid {uid}) not found in opened menu -- checkbox row mapping landed on the wrong player; menu closed, nothing committed");
            }
        }
        else
        {
            // Couldn't independently resolve a name to check against (rare)
            // -- report that verification was skipped rather than silently
            // pretending it passed, but don't block a call we have no way
            // to actually validate.
            info["name_check"] = "skipped-could-not-resolve-expected-name";
        }

        return (true, null);
    }

    /// <summary>
    /// Oracle-proven read recipe reach: RecruitmentScreen -> click the
    /// Shortlists tab (SecondaryTabDropdownButton index 0). Live testing
    /// found this click can silently no-op when a foreground Report panel
    /// (e.g. a player search report) is stacked ON TOP of RecruitmentScreen
    /// -- UiFind2 still finds the buried tab-bar element (it walks the
    /// whole panel stack) but position-based click dispatch lands on the
    /// foreground panel instead, so nav_history/shortlist-title never
    /// change. Self-heals by closing the top panel and re-opening
    /// RecruitmentScreen between retries.
    /// </summary>
    private static async Task<(bool ok, string error, JsonArray steps)> ReachShortlistsTab(FMBridge.Voice.MainThreadQueue queue)
    {
        var steps = new JsonArray();

        // Defensive: a PRIOR remove call's confirmation modal can still be
        // closing (see ModalCloseDeadlineMs doc comment) when this call
        // starts. While open it silently swallows every click, including
        // the tab-bar click below -- wait it out here so an unrelated
        // "list"/"create"/"add" call issued right after a slow "remove"
        // doesn't inherit that failure.
        bool stuckModalClearedFirst = await WaitForStuckModalToClear(queue, steps);
        if (!stuckModalClearedFirst)
            return (false, "stuck-confirmation-modal-did-not-clear", steps);

        var already = await OnQueue(queue, () => Navigator.FindByNameTexts("shortlist-title"));
        if (already?["ok"]?.GetValue<bool>() == true && AsInt(already["count"]) > 0)
        {
            steps.Add(new JsonObject { ["step"] = "fast-path:already-on-shortlists-tab", ["ok"] = true });
            return (true, null, steps);
        }

        var portalOpen = await OnQueue(queue, () => Navigator.NavOpen("PortalScreen"));
        steps.Add(new JsonObject { ["step"] = "nav_open:PortalScreen", ["ok"] = portalOpen?["ok"]?.GetValue<bool>() == true });

        var recruitOpen = await OnQueue(queue, () => Navigator.NavOpen("RecruitmentScreen"));
        bool recruitOk = recruitOpen?["ok"]?.GetValue<bool>() == true;
        steps.Add(new JsonObject { ["step"] = "nav_open:RecruitmentScreen", ["ok"] = recruitOk });
        if (!recruitOk) return (false, "nav_open RecruitmentScreen failed: " + (string)recruitOpen?["error"], steps);

        await Task.Delay(SettleDelayMs);

        bool found = false;
        string lastError = "no-attempt";
        var deadline = Environment.TickCount64 + NavDeadlineMs;
        int attempt = 0;
        while (Environment.TickCount64 < deadline && !found)
        {
            attempt++;
            var click = await OnQueue(queue, () => Navigator.UiClick("SecondaryTabDropdownButton", 0));
            lastError = click?["ok"]?.GetValue<bool>() == true ? null : "click:" + (string)click?["error"];
            await Task.Delay(800);

            var check = await OnQueue(queue, () => Navigator.FindByNameTexts("shortlist-title"));
            if (check?["ok"]?.GetValue<bool>() == true && AsInt(check["count"]) > 0) { found = true; break; }
            lastError ??= "tab-click-ok-but-shortlist-title-still-absent";

            // Self-heal: a foreground Report panel may be silently eating the
            // click. Close whatever's on top and re-drive RecruitmentScreen
            // before the next attempt.
            await OnQueue(queue, () => Navigator.CloseTop());
            await OnQueue(queue, () => Navigator.NavOpen("RecruitmentScreen"));
            await Task.Delay(SettleDelayMs);
        }
        steps.Add(new JsonObject { ["step"] = "click+poll:SecondaryTabDropdownButton->shortlist-title", ["ok"] = found, ["attempts"] = attempt });
        if (!found) return (false, "shortlists-tab-not-reached: " + lastError, steps);
        return (true, null, steps);
    }

    // ------------------------------------------------------------- helpers

    private static async Task<T> OnQueue<T>(FMBridge.Voice.MainThreadQueue queue, Func<T> func)
    {
        return await ReadEntity.OnQueue(queue, _ => func());
    }

    /// <summary>
    /// Defensive entry-point guard: checks whether a confirmation modal
    /// (notifications-modal-dialog-default-default -- the same "Yes/No"
    /// family DoRemove waits on) is currently on screen and, if so, waits
    /// up to ModalCloseDeadlineMs for it to close on its own before letting
    /// the caller proceed. No-op (returns true immediately, no step
    /// recorded) when nothing is stuck -- this only costs a call when
    /// there's actually something to wait for.
    /// </summary>
    private static async Task<bool> WaitForStuckModalToClear(FMBridge.Voice.MainThreadQueue queue, JsonArray steps)
    {
        var check = await OnQueue(queue, () => Navigator.FindByNameTexts("notifications-modal-dialog-default-default"));
        if (AsInt(check?["count"]) <= 0) return true;

        var deadline = Environment.TickCount64 + ModalCloseDeadlineMs;
        while (Environment.TickCount64 < deadline)
        {
            var r = await OnQueue(queue, () => Navigator.FindByNameTexts("notifications-modal-dialog-default-default"));
            if (AsInt(r?["count"]) <= 0)
            {
                steps.Add(new JsonObject { ["step"] = "guard:waited-out-stuck-confirmation-modal", ["ok"] = true });
                return true;
            }
            await Task.Delay(PollIntervalMs);
        }
        steps.Add(new JsonObject { ["step"] = "guard:waited-out-stuck-confirmation-modal", ["ok"] = false });
        return false;
    }

    private static async Task<bool> PollUntil(FMBridge.Voice.MainThreadQueue queue, Func<JsonObject> probe, Func<JsonObject, bool> satisfied, int deadlineMs)
    {
        var deadline = Environment.TickCount64 + deadlineMs;
        while (Environment.TickCount64 < deadline)
        {
            var r = await OnQueue(queue, probe);
            if (satisfied(r)) return true;
            await Task.Delay(PollIntervalMs);
        }
        return false;
    }

    private static bool TextsContain(JsonObject findResult, string needle)
    {
        if (findResult?["ok"]?.GetValue<bool>() != true) return false;
        if (findResult["texts"] is not JsonArray texts) return false;
        var lower = NormalizeForMatch(needle);
        foreach (var t in texts)
        {
            var s = t?.ToString();
            if (!string.IsNullOrEmpty(s) && NormalizeForMatch(s).Contains(lower)) return true;
        }
        return false;
    }

    /// <summary>
    /// Diacritic- and case-insensitive comparison key (live testing):
    /// a plain ToLowerInvariant/IndexOf name-text scan for
    /// "Raphaël Guerreiro" came back with ZERO matches anywhere in the
    /// rendered table, even though the row is genuinely on screen in the
    /// scanned window -- the discovered/rendered text almost certainly
    /// differs from ReadEntity's resolved name only in how the "ë" is
    /// encoded/rendered (precomposed vs. decomposed Unicode, or a
    /// font/text-system substitution), which a byte-for-byte substring
    /// match cannot see past. Decompose to NFD and drop non-spacing marks
    /// so "Raphaël" and "Raphael" (and similarly for any other accented
    /// name) compare equal.
    /// </summary>
    private static string NormalizeForMatch(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var formD = s.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(formD.Length);
        foreach (var c in formD)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString().ToLowerInvariant();
    }

    private static string FirstText(JsonObject findResult)
    {
        if (findResult?["ok"]?.GetValue<bool>() != true) return null;
        if (findResult["texts"] is not JsonArray texts || texts.Count == 0) return null;
        return texts[0]?.ToString();
    }

    private static int AsInt(JsonNode n)
    {
        if (n is JsonValue jv && jv.TryGetValue<int>(out var i)) return i;
        return -1;
    }

    private static JsonObject Finish(JsonObject inner, Stopwatch sw)
    {
        sw.Stop();
        inner["latency_ms"] = sw.Elapsed.TotalMilliseconds;
        return inner;
    }

    private static JsonObject Fail(string error, Stopwatch sw)
    {
        sw.Stop();
        return new JsonObject { ["ok"] = false, ["error"] = error, ["latency_ms"] = sw.Elapsed.TotalMilliseconds };
    }
}
