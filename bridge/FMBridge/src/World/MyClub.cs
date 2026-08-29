using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using SI.Bindable;
using SI.Bindable.Reference.Core;
using SI.Core;

namespace FMBridge.World;

/// <summary>
/// my_club verb: one-call standing club context
/// for the human manager's own club — identity, budgets, wages, financial
/// status, and the Human*Budget reallocation-delta family. Reuses
/// ReadEntity's fabricate/plant/bind/poll/collect machinery via
/// <see cref="ReadEntity.ReadOneEntity"/> with a curated club prop list; this
/// file adds no new channel plumbing of its own.
///
/// Human-club discovery: "Human.Club" is a real, already-bound root-level
/// binding path that resolves LIVE, with no fabrication needed, straight
/// off the current tree root — proven live (it fires as
/// "ClubReference:FM.UI.ClubReference"). This method reads that same
/// node's TypedValue directly and TryCasts it to
/// FM.UI.DatabaseRecordReference (the same extraction ReadEntity's
/// DescribeAndResolve / Navigator.ListRead already use) to pull out the
/// target uid — never hardcoding a club uid. An optional 'club_uid'
/// request field overrides discovery entirely (documented fallback if
/// Human.Club is ever not bound, e.g. very early in a session before a
/// screen has touched that binding scope).
/// </summary>
internal static class MyClub
{
    // Curated prop list -- every name here was live-verified against a
    // real club record, resolving to a direct scalar (or, for FinancialStatus, an
    // inline-resolved {display,sort} composite via ReadEntity's
    // DescribeAndResolve). Order doesn't matter -- read_entity binds all of
    // these in one plant/collect cycle.
    private static readonly string[] CoreProps =
    {
        // identity
        "Name", "ShortName",
        // budgets
        "TransferBudget", "OriginalTransferBudget", "TransferBudgetNextSeason", "OverallBalance",
        // wages
        "CommittedWageSpending", "CurrentWageSpending",
        // status (DynamicReference -> {display,sort} via inline resolution)
        "FinancialStatus",
        // reallocation state (Human*Budget delta family)
        "HumanPurchasedTransferBudget", "HumanPurchasedWageBudget", "HumanPurchasedTransferMovedToWageBudget",
    };

    public static async Task<JsonObject> Enqueue(FMBridge.Voice.MainThreadQueue queue, string requestJson)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            JsonObject request = null;
            try { request = JsonNode.Parse(requestJson ?? "") as JsonObject; } catch { }

            int? overrideUid = null;
            if (request?["club_uid"] is JsonValue jv && jv.TryGetValue<int>(out var ov)) overrideUid = ov;

            int uid;
            string discovery;
            if (overrideUid.HasValue)
            {
                uid = overrideUid.Value;
                discovery = "override:club_uid param";
            }
            else
            {
                var discovered = await ReadEntity.OnQueue(queue, DiscoverHumanClubUid);
                if (discovered == null)
                {
                    return Fail("human-club-not-discoverable: 'Human.Club' binding path did not resolve to a " +
                                "ClubReference; pass 'club_uid' explicitly as a documented fallback", sw);
                }
                uid = discovered.Value;
                discovery = "dynamic:Human.Club";
            }

            var propNames = new List<string>(CoreProps);

            var entry = await ReadEntity.ReadOneEntity(queue, "club", uid, propNames);
            if (entry["error"] != null)
                return Fail("read_entity failed: " + (string)entry["error"], sw);

            var data = entry["data"] as JsonObject ?? new JsonObject();
            var missingSrc = entry["missing"] as JsonArray;
            var missingArr = missingSrc == null ? new JsonArray() : (JsonArray)JsonNode.Parse(missingSrc.ToJsonString());

            // JsonNode instances are single-parented in System.Text.Json.Nodes --
            // round-trip through text to detach a value read from `data` before
            // it can be re-inserted into one of the shaped sub-objects below.
            JsonNode Take(string name)
            {
                var n = data[name];
                return n == null ? null : JsonNode.Parse(n.ToJsonString());
            }

            var identity = new JsonObject
            {
                ["uid"] = uid,
                ["name"] = Take("Name"),
                ["short_name"] = Take("ShortName"),
            };

            var budgets = new JsonObject
            {
                ["transfer_budget"] = Take("TransferBudget"),
                ["original_transfer_budget"] = Take("OriginalTransferBudget"),
                ["transfer_budget_next_season"] = Take("TransferBudgetNextSeason"),
                ["overall_balance"] = Take("OverallBalance"),
            };

            // CommittedWageSpending/CurrentWageSpending are WEEKLY scalars
            // (confirmed live by chain-reading Club.WagesData -- the same
            // node the Finances screen's own "Wages" tile binds -- and
            // cross-checking its CurrentWageTotalValue (an explicit "€X p/a"
            // ANNUAL ground truth) against 52x the weekly scalar). Every
            // money field below carries its period explicitly in its own
            // key name -- no bare ambiguous numbers.
            var wagesDataResult = await ReadWagesData(queue, uid);
            var wd = wagesDataResult.Data ?? new JsonObject();
            double? weeklyCommitted = ToDouble(Take("CommittedWageSpending"));
            double? weeklyCurrent = ToDouble(Take("CurrentWageSpending"));
            JsonNode currentAnnualEur = null;
            if (wd["CurrentWageTotalValue"] != null)
            {
                var parsed = ParseEuroAmount(wd["CurrentWageTotalValue"]!.ToString());
                if (parsed.HasValue) currentAnnualEur = JsonValue.Create(parsed.Value);
            }
            var wages = new JsonObject
            {
                ["committed_weekly_eur"] = Take("CommittedWageSpending"),
                ["committed_annual_estimate_eur"] = weeklyCommitted.HasValue ? JsonValue.Create(weeklyCommitted.Value * 52) : null,
                ["current_weekly_eur"] = Take("CurrentWageSpending"),
                ["current_annual_estimate_eur"] = weeklyCurrent.HasValue ? JsonValue.Create(weeklyCurrent.Value * 52) : null,
                ["current_wage_total_annual_eur"] = currentAnnualEur,
                ["wage_budget_annual_display"] = wd["CurrentWageBudgetValueWithText"] != null ? JsonNode.Parse(wd["CurrentWageBudgetValueWithText"]!.ToJsonString()) : null,
                ["wage_budget_used_pct"] = wd["WageBudgetPercentage"] != null ? JsonNode.Parse(wd["WageBudgetPercentage"]!.ToJsonString()) : null,
                ["over_wage_budget"] = ParseBoolNode(wd["IsTotalBudgetOverWages"]),
                ["units_note"] = "committed/current_weekly_eur are WEEKLY (p/w), read directly off Club.CommittedWageSpending/" +
                                  "CurrentWageSpending. *_annual_estimate_eur = that weekly figure x52 (a cheap estimate, " +
                                  "NOT the game's own figure). current_wage_total_annual_eur and wage_budget_annual_display " +
                                  "ARE the game's own ANNUAL (p/a) ground truth, chain-read off Club.WagesData -- the same " +
                                  "binding the in-game Finances > Wages tile displays (\"€X p/a\") -- and live cross-checked " +
                                  "against squad_report's summed wage bill.",
            };
            if (wagesDataResult.Missing != null && wagesDataResult.Missing.Count > 0)
                wages["wages_data_missing"] = wagesDataResult.Missing;
            if (wagesDataResult.Error != null) wages["wages_data_error"] = wagesDataResult.Error;

            var status = new JsonObject
            {
                ["financial_status"] = Take("FinancialStatus"),
            };

            var reallocation = new JsonObject
            {
                ["human_purchased_transfer_budget"] = Take("HumanPurchasedTransferBudget"),
                ["human_purchased_wage_budget"] = Take("HumanPurchasedWageBudget"),
                ["human_purchased_transfer_moved_to_wage_budget"] = Take("HumanPurchasedTransferMovedToWageBudget"),
            };

            sw.Stop();
            return new JsonObject
            {
                ["ok"] = true,
                ["club"] = identity,
                ["budgets"] = budgets,
                ["wages"] = wages,
                ["status"] = status,
                ["reallocation"] = reallocation,
                ["discovery"] = discovery,
                ["missing"] = missingArr ?? new JsonArray(),
                ["latency_ms"] = sw.Elapsed.TotalMilliseconds,
            };
        }
        catch (Exception e)
        {
            return Fail(e.Message, sw);
        }
    }

    /// <summary>
    /// Main-thread-only: reads the live "Human.Club" binding node (a
    /// root-level path proven live -- no fabrication needed, unlike
    /// person/club reads elsewhere in this bridge which fabricate a
    /// scratch reference) and extracts the target club uid the same way
    /// ReadEntity/Navigator.ListRead already extract uids from a landed
    /// DatabaseRecordReference: TryCast, read m_index. Returns null on
    /// any failure (node not found, wrong type, cast failure) so the caller
    /// can fall back to the documented 'club_uid' override.
    ///
    /// internal (not private): reused as-is by SquadReport.cs for the same
    /// "no club_uid override given" discovery case, so both verbs share one
    /// human-club-discovery implementation.
    /// </summary>
    internal static int? DiscoverHumanClubUid(BindingSubsystem bindings)
    {
        try
        {
            var tv = FMBridge.Eyes.TreeWalker.FindValueTyped(bindings, "Human.Club");
            if (tv == null) return null;
            var dbRef = tv.Get()?.TryCast<FM.UI.DatabaseRecordReference>();
            if (dbRef == null) return null;
            return dbRef.m_index;
        }
        catch { return null; }
    }

    private static JsonObject Fail(string error, Stopwatch sw)
    {
        sw.Stop();
        return new JsonObject { ["ok"] = false, ["error"] = error, ["latency_ms"] = sw.Elapsed.TotalMilliseconds };
    }

    // -------------------------------------------------- wages chain
    //
    // Club.WagesData resolves as a DynamicReference one hop too shallow for
    // ReadEntity's inline PropertyValue/DisplayValue+SortValue resolution --
    // its own child props (CurrentWageTotalValue, CurrentWageBudgetValueWithText,
    // WageBudgetPercentage, IsTotalBudgetOverWages) need a second chained
    // plant, exactly like SquadReport/QueryPlayers already chain a second hop
    // off a landed FullContract. Self-contained here (own ChainSlot/counter,
    // no shared state with ReadEntity's PropRead or QueryPlayers' PropSlot)
    // to keep this a single-club, single-node chain rather than reusing the
    // N-entity batch machinery those files need.

    private static int _chainCounter;

    private static readonly string[] WagesDataProps =
        { "CurrentWageTotalValue", "CurrentWageBudgetValueWithText", "WageBudgetPercentage", "IsTotalBudgetOverWages" };

    private sealed class ChainSlot
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

    private static ChainSlot PlantChainProp(BindingSubsystem bindings, Bindings.Key parentKey, string propName)
    {
        var slot = new ChainSlot { Name = propName };
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

    private static int PeekChainSlot(BindingSubsystem bindings, ChainSlot slot)
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

    private static void CloseChainSlot(BindingSubsystem bindings, FM.GamePlugin.GameInteropSubsystem interop, ChainSlot slot)
    {
        if (!slot.Bound) return;
        try { var k = slot.Key; bindings?.Unbind(ref k, slot.Cb); } catch { }
        try { interop?.CloseChannel(slot.Key); } catch { }
    }

    private sealed class WagesPlant
    {
        public string Error;
        public FM.GamePlugin.GameInteropSubsystem Interop;
        public ChainSlot WagesDataSlot;
        public readonly Dictionary<string, ChainSlot> Sub = new();
        public bool Phase2Attempted;
        public bool Phase2Started;
    }

    private static WagesPlant PlantWages(BindingSubsystem bindings, int uid)
    {
        var plant = new WagesPlant();
        if (!GameSubsystems.TryGet<FM.GamePlugin.GameInteropSubsystem>(out var interop) || interop == null)
        {
            plant.Error = "game-interop-subsystem-not-found";
            return plant;
        }
        plant.Interop = interop;
        try
        {
            var fabricated = new FM.UI.ClubReference(uid);
            var wrapped = TypedValue.Create<Il2CppSystem.Object>(fabricated.Cast<Il2CppSystem.Object>());
            var path = "__bridge.mc." + Interlocked.Increment(ref _chainCounter);
            var rootKey = NativeBindings.CreatePath(bindings, path, Bindings.NodeFlags.Default);
            plant.WagesDataSlot = PlantChainProp(bindings, rootKey, "WagesData");
            bindings.Set(ref rootKey, wrapped, Bindings.SetFlags.UpdateHandler | Bindings.SetFlags.ForceUpdateValue);
        }
        catch (Exception e) { plant.Error = e.Message; }
        return plant;
    }

    private static int AdvanceWages(BindingSubsystem bindings, WagesPlant plant)
    {
        if (plant.Error != null || plant.WagesDataSlot == null) return 0;
        var pending = PeekChainSlot(bindings, plant.WagesDataSlot);

        if (!plant.Phase2Attempted && plant.WagesDataSlot.Value != null)
        {
            plant.Phase2Attempted = true;
            try
            {
                var wagesDataTv = bindings.Get(ref plant.WagesDataSlot.Key);
                if (wagesDataTv != null)
                {
                    foreach (var propName in WagesDataProps)
                        plant.Sub[propName] = PlantChainProp(bindings, plant.WagesDataSlot.Key, propName);
                    bindings.Set(ref plant.WagesDataSlot.Key, wagesDataTv,
                        Bindings.SetFlags.UpdateHandler | Bindings.SetFlags.ForceUpdateValue);
                    plant.Phase2Started = true;
                }
            }
            catch { }
        }

        if (plant.Phase2Started)
            foreach (var slot in plant.Sub.Values) pending += PeekChainSlot(bindings, slot);

        return pending;
    }

    private sealed class WagesDataResult
    {
        public JsonObject Data;
        public JsonArray Missing;
        public string Error;
    }

    private static WagesDataResult CollectWages(BindingSubsystem bindings, WagesPlant plant)
    {
        if (plant.Error != null) return new WagesDataResult { Error = plant.Error };
        if (plant.WagesDataSlot == null) return new WagesDataResult { Error = "wages-plant-not-started" };

        PeekChainSlot(bindings, plant.WagesDataSlot);
        CloseChainSlot(bindings, plant.Interop, plant.WagesDataSlot);
        foreach (var slot in plant.Sub.Values) { PeekChainSlot(bindings, slot); CloseChainSlot(bindings, plant.Interop, slot); }

        var missing = new JsonArray();
        if (!plant.Phase2Started)
        {
            missing.Add("WagesData (never-landed" + (plant.WagesDataSlot.Note != null ? ":" + plant.WagesDataSlot.Note : "") + ")");
            return new WagesDataResult { Data = new JsonObject(), Missing = missing };
        }

        var data = new JsonObject();
        foreach (var kv in plant.Sub)
        {
            if (kv.Value.Value != null)
                data[kv.Key] = ReadEntity.Decode(kv.Value.Value, kv.Value.RefUid, kv.Value.RefTable, kv.Value.ResolvedDisplay, kv.Value.ResolvedSort);
            else
                missing.Add(kv.Value.Note != null ? kv.Key + " (" + kv.Value.Note + ")" : kv.Key);
        }
        return new WagesDataResult { Data = data, Missing = missing };
    }

    private static async Task<WagesDataResult> ReadWagesData(FMBridge.Voice.MainThreadQueue queue, int uid)
    {
        const int pollIntervalMs = 150;
        const int waitMs = 3000;

        var plant = await ReadEntity.OnQueue(queue, b => PlantWages(b, uid));
        if (plant.Error != null) return new WagesDataResult { Error = plant.Error };

        var deadline = Environment.TickCount64 + waitMs;
        var pending = await ReadEntity.OnQueue(queue, b => AdvanceWages(b, plant));
        while (pending > 0 && Environment.TickCount64 < deadline)
        {
            await Task.Delay(pollIntervalMs);
            pending = await ReadEntity.OnQueue(queue, b => AdvanceWages(b, plant));
        }
        return await ReadEntity.OnQueue(queue, b => CollectWages(b, plant));
    }

    /// <summary>Strips a display string like "€135,929,353 p/a" down to its
    /// numeric euro amount (135929353). Digits-only extraction -- FM's whole-
    /// euro financial displays never carry decimals, so no decimal-point
    /// handling is needed; a leading '-' is preserved for negative balances.</summary>
    private static double? ParseEuroAmount(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var sb = new StringBuilder();
        var neg = false;
        foreach (var c in s)
        {
            if (c == '-') neg = true;
            else if (char.IsDigit(c)) sb.Append(c);
        }
        if (sb.Length == 0) return null;
        if (!double.TryParse(sb.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var num)) return null;
        return neg ? -num : num;
    }

    private static double? ToDouble(JsonNode n)
    {
        if (n is JsonValue jv)
        {
            if (jv.TryGetValue<double>(out var d)) return d;
            if (jv.TryGetValue<int>(out var i)) return i;
        }
        return null;
    }

    private static JsonNode ParseBoolNode(JsonNode n)
    {
        if (n == null) return null;
        var s = n.ToString();
        if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(true);
        if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(false);
        return JsonNode.Parse(n.ToJsonString());
    }
}
