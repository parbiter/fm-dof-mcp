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
/// squad_report verb: one call -> structured
/// depth-chart payload for one of the human club's teams (first team / a
/// youth-tier team), per player: name, age, position(s), positional fit
/// (Ability&lt;Position&gt; family), PerceivedPotentialAbility, wage
/// (FullContract.Wage {display,sort}) and contract (FullContract.EndDate/
/// ContractLength/ContractDaysElapsed).
///
/// Squad-uid discovery (headless, no nav dependency): club-level team
/// reference props (MainTeam / HighestLevelYouthTeam / YouthTeam, all live
/// TeamReference $refs off a bare club read) give a team uid; that team's
/// own "Players" property (Team.Players, a confirmed real root binding
/// path) delivers a real, populated `List&lt;TypedValue&gt;` when
/// fabricated+bound directly (the planted fabricate path was proven live
/// against a team before this verb was built on it). This file reads that
/// List the exact same safe way
/// Navigator.ListRead already reads a UI widget's backing IList (Count +
/// indexer only, never foreach) via the shared <see cref="Navigator.ReadRows"/>
/// helper -- same crash-safety guarantee, new data source.
///
/// Per-player batch read is a two-phase chained plant, generalized from
/// ReadEntity's single-uid plant/poll/collect to run ACROSS all uids at once
/// (plant every player's channels first, then one shared poll loop, then
/// collect every player) instead of ReadEntity's per-uid serial loop -- a
/// pure performance restructuring. No new
/// threading: every step is still a single main-thread queue action: a
/// bigger one-shot fan-out instead of N small ones.
///
/// Phase 1 (per player, one hop off the fabricated PersonReference): Name,
/// Age, Position, PerceivedPotentialAbility, the 14 Ability* positional-fit
/// scalars, and FullContract (lands as a ContractReference $ref -- the
/// chain root for phase 2). PositionRole is intentionally NOT planted here
/// (latency fix) -- see the comment on `TopProps` for why it never
/// resolves and how leaving it bound pinned every poll tick to the full
/// deadline.
/// Phase 2 (per player, one hop off the now-landed FullContract node):
/// Wage (DynamicReference -> {display,sort} via ReadEntity.DescribeAndResolve),
/// EndDate (GameDate, decoded to an ISO-ish string by TreeWalker.Describe's
/// native GameDate.ToDateTime route), ContractLength, ContractDaysElapsed.
/// This is exactly the three-hop chaining recipe proven manually live
/// (fabricate -> bind child A -> once landed, bind child B off A's own
/// path -> re-Set A's own key to trigger the dirty walk) -- done here in one
/// verb call instead of three manual round trips.
/// </summary>
internal static class SquadReport
{
    private const int MaxPlayers = 60;
    private const int PollIntervalMs = 150;
    private const int PhaseWaitMs = 4000;
    private const int TotalDeadlineMs = 28000;
    private const int QueueTimeoutMs = 15000;

    // Positional-fit family (~1-20 scale, NOT the 1-200 CA/PA scale) --
    // EntityVocabulary.cs person.general section, "Ability*" names.
    private static readonly string[] AbilityProps =
    {
        "AbilityGoalkeeper", "AbilityCentreBack", "AbilityLeftBack", "AbilityRightBack",
        "AbilityLeftWingBack", "AbilityRightWingBack", "AbilityDefensiveMidfielder",
        "AbilityCentreMidfielder", "AbilityLeftMidfielder", "AbilityRightMidfielder",
        "AbilityAttackingMidfielder", "AbilityLeftWinger", "AbilityRightWinger", "AbilityForward",
    };

    // PositionRole is deliberately NOT planted here (known gap:
    // PositionRole never resolves): across 114 live player-reads
    // it bound successfully but its callback never fired within any poll
    // budget tried, so its PropSlot sat "pending" forever and pinned every
    // AdvancePlayers tick to the full deadline (the shared poll loop below
    // already breaks the instant `pending==0` -- a dead prop is what was
    // preventing that from ever happening). It is reported as a known-dead
    // field in each player's `missing` array instead of being bound.
    private static readonly string[] TopProps = BuildTopProps();

    private static string[] BuildTopProps()
    {
        var list = new List<string> { "Name", "Age", "Position", "PerceivedPotentialAbility", "FullContract" };
        list.AddRange(AbilityProps);
        return list.ToArray();
    }

    private static readonly string[] ContractProps = { "Wage", "EndDate", "ContractLength", "ContractDaysElapsed" };

    // squad selector -> club-level team-reference prop name. AC Milan (club
    // 656, live-probed) exposes exactly three headless team refs:
    // MainTeam (first team, uid 839, name "Milan"), HighestLevelYouthTeam
    // (uid 28341, name "Casciavit B" -- despite the generic property name
    // this IS the club's reserve/B side for this club) and
    // YouthTeam==SecondHighestLevelYouthTeam (uid 30618, name "Milan U20s").
    //
    // RENAMED after a live finding:
    // the original "u18"/"u23"/"b" alias names were semantically dishonest --
    // for Milan, HighestLevelYouthTeam is actually the B team/reserve side
    // ("Casciavit B" == Milan Futuro) while YouthTeam is the actual youth
    // squad ("Milan U20s"). Since club naming/tiering conventions vary by
    // country/club, age-numbered aliases (u18/u23) imply a specific age-group
    // guarantee FM's data model doesn't make. Selector is now named after the
    // PROPERTY tier, not a guessed age band: "first"->MainTeam,
    // "b"->HighestLevelYouthTeam, "youth"->YouthTeam. The response still
    // always echoes the real in-game team name+prop so a caller sees exactly
    // which squad they got.
    private static readonly Dictionary<string, string> SquadTeamProp = new()
    {
        ["first"] = "MainTeam",
        ["b"] = "HighestLevelYouthTeam",
        ["youth"] = "YouthTeam",
    };

    public static async Task<JsonObject> Enqueue(FMBridge.Voice.MainThreadQueue queue, string requestJson)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            JsonObject request = null;
            try { request = JsonNode.Parse(requestJson ?? "") as JsonObject; } catch { }

            int? overrideUid = null;
            if (request?["club_uid"] is JsonValue cjv && cjv.TryGetValue<int>(out var cov)) overrideUid = cov;

            var squad = (string)request?["squad"];
            if (string.IsNullOrEmpty(squad)) squad = "first";
            if (!SquadTeamProp.TryGetValue(squad, out var teamProp))
                return Fail("unknown 'squad' value '" + squad + "'; valid: " + string.Join(", ", SquadTeamProp.Keys), sw);

            int clubUid;
            string discovery;
            if (overrideUid.HasValue)
            {
                clubUid = overrideUid.Value;
                discovery = "override:club_uid param";
            }
            else
            {
                var discovered = await ReadEntity.OnQueue(queue, MyClub.DiscoverHumanClubUid);
                if (discovered == null)
                    return Fail("human-club-not-discoverable: pass 'club_uid' explicitly (see my_club)", sw);
                clubUid = discovered.Value;
                discovery = "dynamic:Human.Club";
            }

            // Step 1: club -> team uid, one headless read_entity hop (reuses
            // the exact same fabricate/plant/bind/poll/collect machinery
            // my_club uses, no new plumbing).
            var clubEntry = await ReadEntity.ReadOneEntity(queue, "club", clubUid, new List<string> { "Name", teamProp });
            if (clubEntry["error"] != null) return Fail("club read failed: " + (string)clubEntry["error"], sw);
            var clubData = clubEntry["data"] as JsonObject;
            var clubName = clubData?["Name"]?.ToString();
            var teamRefNode = clubData?[teamProp] as JsonObject;
            var teamUidNode = teamRefNode?["uid"];
            if (teamUidNode == null)
                return Fail("club prop '" + teamProp + "' did not resolve to a TeamReference (club " + clubUid + ")", sw);
            var teamUid = (int)teamUidNode;

            // Step 2: fabricate the team, bind Name + Players, read the
            // headless roster list.
            var teamPlant = await OnQueue(queue, b => PlantTeam(b, teamUid));
            if (teamPlant.Error != null) return Fail("team fabricate failed: " + teamPlant.Error, sw);
            var teamDeadline = Environment.TickCount64 + PhaseWaitMs;
            while (Environment.TickCount64 < teamDeadline)
            {
                var pending = await OnQueue(queue, b => PeekTeam(b, teamPlant));
                if (pending == 0) break;
                await Task.Delay(PollIntervalMs);
            }
            var teamResult = await OnQueue(queue, b => CollectTeam(b, teamPlant));
            var teamName = (string)teamResult["name"];
            var uids = new List<int>();
            if (teamResult["uids"] is JsonArray uidArr)
                foreach (var n in uidArr) if (n != null) uids.Add((int)n);

            var route = teamResult["route"]?.ToString() ?? "headless:Team.Players";
            if (uids.Count == 0)
            {
                return Fail("squad-uid discovery failed for team " + teamUid + " (" + teamProp +
                            "): Team.Players did not yield readable player rows; route=" + route +
                            "; note=" + teamResult["note"], sw);
            }
            if (uids.Count > MaxPlayers) uids = uids.GetRange(0, MaxPlayers);

            // Step 3: batch two-phase chained read across ALL players,
            // overlapped (plant-all -> shared poll -> collect-all).
            var plants = await OnQueue(queue, b => PlantPlayers(b, uids));
            var deadline = Environment.TickCount64 + PhaseWaitMs * 2;
            while (Environment.TickCount64 < deadline)
            {
                var pending = await OnQueue(queue, b => AdvancePlayers(b, plants));
                if (pending == 0) break;
                await Task.Delay(PollIntervalMs);
            }
            var players = await OnQueue(queue, b => CollectPlayers(b, plants));

            SortPlayers(players);

            sw.Stop();
            return new JsonObject
            {
                ["ok"] = true,
                ["club"] = new JsonObject { ["uid"] = clubUid, ["name"] = clubName },
                ["team"] = new JsonObject { ["uid"] = teamUid, ["name"] = teamName, ["prop"] = teamProp },
                ["squad"] = squad,
                ["route"] = route,
                ["players"] = players,
                ["count"] = players.Count,
                ["discovery"] = discovery,
                ["latency_ms"] = sw.Elapsed.TotalMilliseconds,
            };
        }
        catch (Exception e)
        {
            return Fail(e.Message, sw);
        }
    }

    private static void SortPlayers(JsonArray players)
    {
        // Group/sort sensibly: by position text, then by name -- cheap,
        // stable, no numeric parsing needed. JsonArray has no in-place sort,
        // so extract/sort/rebuild.
        var items = new List<JsonNode>();
        foreach (var p in players) items.Add(p);
        players.Clear();
        items.Sort((a, b) =>
        {
            var pa = (a as JsonObject)?["position"]?.ToString() ?? "";
            var pb = (b as JsonObject)?["position"]?.ToString() ?? "";
            var c = string.CompareOrdinal(pa, pb);
            if (c != 0) return c;
            var na = (a as JsonObject)?["name"]?.ToString() ?? "";
            var nb = (b as JsonObject)?["name"]?.ToString() ?? "";
            return string.CompareOrdinal(na, nb);
        });
        foreach (var it in items) players.Add(it);
    }

    // ---------------------------------------------------------- team plant

    private sealed class TeamPlant
    {
        public string Error;
        public Bindings.Key RootKey;
        public Bindings.Key NameKey;
        public Bindings.Key PlayersKey;
        public Bindings.ValueChangedCallback NameCb;
        public Bindings.ValueChangedCallback PlayersCb;
        public FM.GamePlugin.GameInteropSubsystem Interop;
        public string NameValue;
        public TypedValue PlayersValue;
        public bool PlayersLanded;
    }

    private static TeamPlant PlantTeam(BindingSubsystem bindings, int teamUid)
    {
        var plant = new TeamPlant();
        if (!GameSubsystems.TryGet<FM.GamePlugin.GameInteropSubsystem>(out var interop) || interop == null)
        {
            plant.Error = "game-interop-subsystem-not-found";
            return plant;
        }
        plant.Interop = interop;

        FM.UI.TeamReference fabricated;
        try { fabricated = new FM.UI.TeamReference(teamUid); }
        catch (Exception e) { plant.Error = "fabricate-ctor-failed: " + e.Message; return plant; }

        TypedValue wrapped;
        try { wrapped = TypedValue.Create<Il2CppSystem.Object>(fabricated.Cast<Il2CppSystem.Object>()); }
        catch (Exception e) { plant.Error = "typedvalue-create-failed: " + e.Message; return plant; }

        var path = "__bridge.sqteam." + Interlocked.Increment(ref _counter);
        Bindings.Key rootKey;
        try { rootKey = NativeBindings.CreatePath(bindings, path, Bindings.NodeFlags.Default); }
        catch (Exception e) { plant.Error = "create-parent-failed: " + e.Message; return plant; }
        plant.RootKey = rootKey;

        try
        {
            plant.NameKey = NativeBindings.CreateRooted(bindings, rootKey, "Name", Bindings.NodeFlags.RequiresContext);
            plant.NameCb = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Bindings.ValueChangedCallback>(
                (Action<Bindings.Key, TypedValue>)((k, v) =>
                {
                    try { plant.NameValue = FMBridge.Eyes.TreeWalker.Describe(v); } catch { }
                }));
            bindings.Bind(ref plant.NameKey, plant.NameCb);

            plant.PlayersKey = NativeBindings.CreateRooted(bindings, rootKey, "Players", Bindings.NodeFlags.RequiresContext);
            plant.PlayersCb = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Bindings.ValueChangedCallback>(
                (Action<Bindings.Key, TypedValue>)((k, v) =>
                {
                    plant.PlayersValue = v;
                    plant.PlayersLanded = v != null;
                }));
            bindings.Bind(ref plant.PlayersKey, plant.PlayersCb);
        }
        catch (Exception e) { plant.Error = "bind-failed: " + e.Message; return plant; }

        try
        {
            bindings.Set(ref rootKey, wrapped, Bindings.SetFlags.UpdateHandler | Bindings.SetFlags.ForceUpdateValue);
        }
        catch (Exception e) { plant.Error = "parent-set-failed: " + e.Message; }
        return plant;
    }

    private static int PeekTeam(BindingSubsystem bindings, TeamPlant plant)
    {
        if (plant.Error != null) return 0;
        var pending = 0;
        if (plant.NameValue == null)
        {
            try
            {
                var d = bindings.Get(ref plant.NameKey);
                if (d != null) plant.NameValue = FMBridge.Eyes.TreeWalker.Describe(d);
            }
            catch { }
            if (plant.NameValue == null) pending++;
        }
        if (!plant.PlayersLanded)
        {
            try
            {
                var d = bindings.Get(ref plant.PlayersKey);
                if (d != null) { plant.PlayersValue = d; plant.PlayersLanded = true; }
            }
            catch { }
            if (!plant.PlayersLanded) pending++;
        }
        return pending;
    }

    private static JsonObject CollectTeam(BindingSubsystem bindings, TeamPlant plant)
    {
        PeekTeam(bindings, plant);
        // Strip the "Type:" prefix Describe leaves on plain strings (e.g.
        // "String:Milan" -> "Milan") through the same ladder read_entity/
        // my_club already use for every other decoded field.
        var teamNameNode = plant.NameValue != null ? ReadEntity.Decode(plant.NameValue) : null;
        var result = new JsonObject { ["name"] = teamNameNode, ["uids"] = new JsonArray() };
        if (plant.Error != null) { result["note"] = plant.Error; }
        else if (!plant.PlayersLanded)
        {
            result["note"] = "Players-never-landed";
        }
        else
        {
            var rows = new JsonArray();
            var returned = 0;
            string note = "no-list";
            try
            {
                var boxed = plant.PlayersValue.Get();
                var list = boxed?.TryCast<Il2CppSystem.Collections.IList>();
                if (list != null)
                {
                    Navigator.ReadRows(list, MaxPlayers, rows, ref returned);
                    note = "headless:Team.Players:" + returned + "-rows";
                }
                else note = "players-value-not-ilist:" + SafeDescribe(plant.PlayersValue);
            }
            catch (Exception e) { note = "players-read-failed:" + e.Message; }
            result["note"] = note;
            result["route"] = "headless:Team.Players";
            var uids = new JsonArray();
            foreach (var r in rows)
            {
                var ro = r as JsonObject;
                if (ro?["uid"] != null && (string)ro["refType"] == "Person") uids.Add((int)ro["uid"]);
            }
            result["uids"] = uids;
        }

        try
        {
            var nk = plant.NameKey; bindings?.Unbind(ref nk, plant.NameCb);
            plant.Interop?.CloseChannel(nk);
        }
        catch { }
        try
        {
            var pk = plant.PlayersKey; bindings?.Unbind(ref pk, plant.PlayersCb);
            plant.Interop?.CloseChannel(pk);
        }
        catch { }
        return result;
    }

    // -------------------------------------------------------- player plant

    private sealed class PropSlot
    {
        public string Name;
        public Bindings.Key Key;
        public Bindings.ValueChangedCallback Cb;
        public bool Bound;
        public bool Unresolved;
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
        public readonly Dictionary<string, PropSlot> Top = new();
        public readonly Dictionary<string, PropSlot> Contract = new();
        public bool ContractPhaseStarted;
        public bool ContractPhaseAttempted;
    }

    private static int _counter;

    private static List<PlayerCtx> PlantPlayers(BindingSubsystem bindings, List<int> uids)
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
                var path = "__bridge.sqp." + Interlocked.Increment(ref _counter);
                var rootKey = NativeBindings.CreatePath(bindings, path, Bindings.NodeFlags.Default);
                ctx.RootKey = rootKey;

                foreach (var propName in TopProps)
                {
                    var slot = PlantOneProp(bindings, rootKey, propName);
                    ctx.Top[propName] = slot;
                }

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
            if (propId.ID == 0) { slot.Unresolved = true; slot.Note = "unknown-property-name"; return slot; }
            slot.Key = NativeBindings.CreateRooted(bindings, parentKey, propName, Bindings.NodeFlags.RequiresContext);
            if (slot.Key.m_key == parentKey.m_key) { slot.Unresolved = true; slot.Note = "child-key-equals-parent"; return slot; }
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
        catch (Exception e) { slot.Unresolved = true; slot.Note = e.Message; }
        return slot;
    }

    /// <summary>One shared poll tick across every player at once (the
    /// perf restructuring the class doc describes): sweeps phase-1 slots,
    /// starts phase-2 (contract chain) the moment FullContract lands for a
    /// given player, and sweeps phase-2 slots for players already past that
    /// point -- all in one main-thread queue action per tick instead of one
    /// per uid.</summary>
    private static int AdvancePlayers(BindingSubsystem bindings, List<PlayerCtx> plants)
    {
        var pending = 0;
        foreach (var ctx in plants)
        {
            if (ctx.Error != null) continue;
            foreach (var slot in ctx.Top.Values) pending += PeekSlot(bindings, slot);

            if (!ctx.ContractPhaseAttempted && ctx.Top.TryGetValue("FullContract", out var fc) && fc.Value != null)
            {
                ctx.ContractPhaseAttempted = true;
                try
                {
                    var contractTv = bindings.Get(ref fc.Key);
                    if (contractTv != null)
                    {
                        foreach (var cp in ContractProps)
                        {
                            var slot = PlantOneProp(bindings, fc.Key, cp);
                            ctx.Contract[cp] = slot;
                        }
                        bindings.Set(ref fc.Key, contractTv, Bindings.SetFlags.UpdateHandler | Bindings.SetFlags.ForceUpdateValue);
                        ctx.ContractPhaseStarted = true;
                    }
                }
                catch { }
            }

            // Top-level FullContract slot itself already contributed to
            // `pending` via the sweep above (while its Value is null); once
            // it lands, ContractPhaseAttempted flips true on the very next
            // tick and phase-2 slots take over the counting. Nothing else to
            // add here -- avoids double-counting the same wait.
            if (ctx.ContractPhaseStarted)
                foreach (var slot in ctx.Contract.Values) pending += PeekSlot(bindings, slot);
        }
        return pending;
    }

    /// <summary>Re-checks one bound prop slot if it hasn't landed yet (or
    /// landed as a still-populating Record, DescribeAndResolve's `pending`
    /// guard). Returns 1 while still waiting, 0 once resolved (or if the
    /// prop name never resolved at all, which can't ever land).</summary>
    private static int PeekSlot(BindingSubsystem bindings, PropSlot slot)
    {
        if (slot.Unresolved) return 0;
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

    private static JsonArray CollectPlayers(BindingSubsystem bindings, List<PlayerCtx> plants)
    {
        var players = new JsonArray();
        foreach (var ctx in plants)
        {
            // Final sweep + cleanup (unbind/close every channel opened for this player).
            foreach (var slot in ctx.Top.Values) { PeekSlot(bindings, slot); CloseSlot(bindings, ctx.Interop, slot); }
            foreach (var slot in ctx.Contract.Values) { PeekSlot(bindings, slot); CloseSlot(bindings, ctx.Interop, slot); }

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

            // The game props are named "Ability*" but the ~1-20 value is
            // positional FAMILIARITY (20 = natural in that slot), not
            // quality -- strip the misleading prefix so LLM consumers
            // don't read "AbilityLeftBack: 20" as a merit score.
            var familiarity = new JsonObject();
            foreach (var ab in AbilityProps)
            {
                var v = DecodeSlot(ctx.Top, ab);
                if (v != null) familiarity[ab.Substring("Ability".Length)] = v;
            }

            var contract = new JsonObject
            {
                ["end_date"] = DecodeSlot(ctx.Contract, "EndDate"),
                ["length"] = DecodeSlot(ctx.Contract, "ContractLength"),
                ["days_elapsed"] = DecodeSlot(ctx.Contract, "ContractDaysElapsed"),
            };
            var wageNode = DecodeSlot(ctx.Contract, "Wage");

            var missing = new JsonArray();
            foreach (var kv in ctx.Top) if (kv.Value.Value == null) missing.Add(kv.Value.Note != null ? kv.Key + " (" + kv.Value.Note + ")" : kv.Key);
            foreach (var kv in ctx.Contract) if (kv.Value.Value == null) missing.Add(kv.Value.Note != null ? kv.Key + " (" + kv.Value.Note + ")" : kv.Key);
            // PositionRole is never planted at all (see BuildTopProps) --
            // still surfaced here so callers see it's known-unavailable
            // rather than silently absent.
            missing.Add("PositionRole (known-dead, pruned pre-poll)");

            players.Add(new JsonObject
            {
                ["uid"] = ctx.Uid,
                ["name"] = DecodeSlot(ctx.Top, "Name")?.ToString(),
                ["age"] = DecodeSlot(ctx.Top, "Age"),
                ["position"] = DecodeSlot(ctx.Top, "Position")?.ToString(),
                ["position_decoded"] = ctx.Top.TryGetValue("Position", out var posSlot) ? PositionDecode.Decode(posSlot.Value) : null,
                ["position_role"] = null,
                ["position_familiarity"] = familiarity,
                ["perceived_potential_ability"] = DecodeSlot(ctx.Top, "PerceivedPotentialAbility"),
                ["wage"] = wageNode,
                ["contract"] = contract,
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

    private static JsonObject Fail(string error, Stopwatch sw)
    {
        sw.Stop();
        return new JsonObject { ["ok"] = false, ["error"] = error, ["latency_ms"] = sw.Elapsed.TotalMilliseconds };
    }

    private static string SafeDescribe(TypedValue v)
    {
        try { return FMBridge.Eyes.TreeWalker.Describe(v); } catch { return "?"; }
    }
}
