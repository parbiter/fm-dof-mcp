using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SI.Bindable;
using SI.Bindable.Reference.Core;
using SI.Core;

namespace FMBridge.World;

/// <summary>
/// read_entity: generic entity reader feeding the four MCP kind tools.
/// For each uid it fabricates the matching FM.UI reference
/// (PersonReference/ClubReference/NationReference/CompReference — all
/// classes with a public .ctor(int)), plants it on a __bridge.re.N scratch
/// node, binds one child node per requested prop and re-Sets the parent so
/// the game's own dirty-node walk opens the channels — fabricate-mode
/// machinery proven live against the running game.
///
/// This is one synchronous verb: planting, value collection and cleanup
/// are separate main-thread queue actions with real frames between them
/// (await Task.Delay on the WS thread — a >=1-Update-tick gap between
/// steps). Props resolve via PropertyIdentifierSet.GetID(name); unknown
/// names simply never fire and land in missing[]. Values decode via
/// TreeWalker.Describe (which strips name markup through
/// TextFunctions.SubString(removeEmbeddedData:true) / TranslationManager.Format).
/// </summary>
internal static class ReadEntity
{
    // A batch read is deliberately bounded at the bridge boundary as well as
    // in the MCP schema. This prevents another MCP client (or a raw WS caller)
    // from turning one request into hundreds of sequential player reads.
    private const int MaxUids = 20;
    private const int MaxProps = 128;
    private const int PollIntervalMs = 150;
    private const int PerUidWaitMs = 2500;
    private const int QueueTimeoutMs = 10000;
    private const int TotalDeadlineMs = 25000;

    // See the RunAsync loop's doc comment above wantsTransferValue for the
    // full rationale.
    private const int TransferValueUiScrapeMaxUids = 8;

    private static int _counter;

    // Inline DynamicReference resolution: DisplayValue/SortValue is FM's UI
    // binding-framework convention for any displayable+sortable composite
    // property (Wage, FinancialStatus, ...) -- the pinned numeric ids below
    // were recovered live. PropertyValue (plain attribute wrappers, e.g.
    // AttributeCrossing) has no stable numeric id known ahead of time, so it
    // is resolved by name once and cached, exactly like every other
    // property name.
    private const uint DisplayValueId = 1903577654;
    private const uint SortValueId = 1903577653;
    private static uint _propertyValueId;
    private static bool _propertyValueIdChecked;

    private static uint PropertyValueId
    {
        get
        {
            if (!_propertyValueIdChecked)
            {
                try { _propertyValueId = PropertyIdentifierSet.Instance.GetID(SpanOf("PropertyValue")).ID; }
                catch { _propertyValueId = 0; }
                _propertyValueIdChecked = true;
            }
            return _propertyValueId;
        }
    }

    private sealed class PropRead
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
        /// <summary>True when the value is a landed DynamicReference/Record
        /// whose dictionary didn't yet contain a known key (PropertyValue or
        /// DisplayValue) on the last check -- Peek keeps re-resolving this
        /// prop on later poll iterations instead of freezing the first
        /// (possibly not-yet-populated) result -- an async-population
        /// guard.</summary>
        public bool NeedsRecheck;
        public readonly List<FiredValue> Fired = new();
    }

    /// <summary>One callback firing: the described string plus, when the
    /// underlying value was a DatabaseRecordReference, the target uid/table
    /// captured at the same moment (the same extraction pattern
    /// Navigator.ListRead uses) so $ref payloads can be
    /// chained without a second read. ResolvedDisplay/ResolvedSort/RecordPending
    /// carry the inline DynamicReference resolution outcome (see
    /// DescribeAndResolve) for the same firing.</summary>
    private sealed class FiredValue
    {
        public string Desc;
        public int? RefUid;
        public string RefTable;
        public string ResolvedDisplay;
        public double? ResolvedSort;
        public bool RecordPending;
    }

    private sealed class EntityPlant
    {
        public string Error;
        public string ParentPath;
        public Bindings.Key ParentKey;
        public FM.GamePlugin.GameInteropSubsystem Interop;
        public readonly List<PropRead> Props = new();
    }

    /// <summary>Queue wrapper: mirrors TypeText.Enqueue so the VoiceServer
    /// switch case stays a one-liner. Parses its own request JSON to keep the
    /// shared HandleMessage argument block untouched.</summary>
    public static async Task<JsonObject> Enqueue(FMBridge.Voice.MainThreadQueue queue, string requestJson)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            return await RunAsync(queue, requestJson, sw);
        }
        catch (Exception e)
        {
            return Fail(e.Message, sw);
        }
    }

    private static async Task<JsonObject> RunAsync(FMBridge.Voice.MainThreadQueue queue, string requestJson, Stopwatch sw)
    {
        JsonObject request;
        try { request = JsonNode.Parse(requestJson ?? "") as JsonObject; }
        catch (Exception e) { return Fail("bad json: " + e.Message, sw); }
        if (request == null) return Fail("request must be a json object", sw);

        var kind = (string)request["kind"];
        if (!EntityVocabulary.IsKind(kind))
            return Fail("unknown kind '" + (kind ?? "") + "'; valid kinds: " + string.Join(", ", EntityVocabulary.Kinds), sw);

        var uids = new List<int>();
        if (request["uids"] is JsonArray uidsNode)
        {
            foreach (var n in uidsNode)
            {
                if (n == null || !int.TryParse(n.ToJsonString(), out var uid))
                    return Fail("uids must be an array of integers", sw);
                uids.Add(uid);
            }
        }
        if (uids.Count == 0) return Fail("read_entity requires non-empty 'uids'", sw);
        if (uids.Count > MaxUids) return Fail("too many uids (" + uids.Count + "); max " + MaxUids, sw);

        var sections = StringArray(request["sections"], "sections", out var sectionsError);
        if (sectionsError != null) return Fail(sectionsError, sw);
        var extraProps = StringArray(request["props"], "props", out var propsError);
        if (propsError != null) return Fail(propsError, sw);

        var propNames = new List<string>();
        if ((sections == null || sections.Count == 0) && (extraProps == null || extraProps.Count == 0))
        {
            AddUnique(propNames, EntityVocabulary.Identity[kind]);
        }
        else
        {
            if (sections != null)
            {
                for (var i = 0; i < sections.Count; i++)
                {
                    if (!EntityVocabulary.TryGetSection(kind, sections[i], out var props))
                        return Fail("unknown section '" + sections[i] + "' for kind '" + kind +
                                    "'; valid sections: " + string.Join(", ", EntityVocabulary.SectionNames(kind)), sw);
                    AddUnique(propNames, props);
                }
            }
            if (extraProps != null) AddUnique(propNames, extraProps);
        }
        if (propNames.Count == 0) return Fail("no props resolved for kind '" + kind + "'", sw);
        if (propNames.Count > MaxProps) return Fail("too many props (" + propNames.Count + "); max " + MaxProps, sw);

        // Also request "IsValid" (not part of any vocabulary section, but a
        // confirmed live-registry property name for at least "person" --
        // EntityVocabulary.cs identity section) so PostProcessFound can tell a
        // genuinely-resolved uid apart from one that resolved to a garbage
        // placeholder record. Harmless to request for kinds where it doesn't
        // resolve -- it simply lands in missing[] like any other unknown name,
        // and PostProcessFound falls back to the Name-heuristic in that case.
        var checkPropNames = new List<string>(propNames);
        if (!checkPropNames.Contains("IsValid")) checkPropNames.Add("IsValid");
        // "Name" is the fallback validity check when IsValid doesn't bind —
        // which is now the norm for person (IsValid lands in missing[] on a
        // live career; observed repeatedly). A sections-only read (e.g.
        // ["attributes"]) doesn't include Name, so without this the fallback
        // had nothing to inspect and every such read was branded
        // placeholder/garbage despite returning real data. Requested
        // synthetically and stripped again in PostProcessFound.
        bool nameSynthetic = !checkPropNames.Contains("Name");
        if (nameSynthetic) checkPropNames.Add("Name");

        var available = EntityVocabulary.SectionNames(kind);
        Array.Sort(available, StringComparer.Ordinal);
        var availableNode = new JsonArray();
        for (var i = 0; i < available.Length; i++) availableNode.Add(available[i]);

        // "TransferValue" is not a
        // real bound property -- there is no channel behind FM's per-row
        // transfer-value UI cell (see QueryPlayers.cs's class-doc for the
        // live recon). Requesting it as a prop name is honored as a special
        // case: it lands in missing[] via the normal unknown-property-name
        // path like any other channel prop would, then this loop separately
        // drives the SAME Player-Database nav+scroll+scrape
        // QueryPlayers.ReadTransferValueForUid uses for query_players'
        // enrich path, replacing that missing[] entry with real
        // {display,sort} data on success. Capped well below MaxUids because
        // each uid here costs a full nav+scroll round-trip (~1-5s), unlike
        // every other read_entity prop which is a cheap channel bind -- a
        // caller wanting TransferValue on many uids should use
        // query_players's enrich instead, which reads the whole render
        // window per scroll rather than one uid at a time.
        bool wantsTransferValue = string.Equals(kind, "person", StringComparison.OrdinalIgnoreCase)
            && propNames.Contains("TransferValue");
        int transferValueScrapesAttempted = 0;

        // Loan resolution: the caller asked for OnLoanFrom and/or
        // LoanContract (see EntityVocabulary.cs person.contract/relations),
        // both of which land as bare unresolved $refs with no further
        // read_entity call able to chase them (ContractReference/
        // ClubReference-by-arbitrary-uid aren't among the four supported
        // kinds) -- so this synthesizes a "Loan" field with the same
        // {status, club, until, recallable} shape squad_report's per-player
        // `loan` object uses, reusing the exact same live-proven logic
        // (compare OnLoanFrom's resolved uid against the human club's own
        // uid; Club is the player's CURRENT employing club, which is the
        // loan destination while loaned out). "Club" and "OnLoanFrom" are
        // synthetically requested when needed (mirroring the IsValid/Name
        // pattern above) and stripped back out unless the caller asked for
        // them directly.
        bool wantsLoan = string.Equals(kind, "person", StringComparison.OrdinalIgnoreCase)
            && (propNames.Contains("OnLoanFrom") || propNames.Contains("LoanContract"));
        bool onLoanFromSynthetic = false, clubSynthetic = false;
        if (wantsLoan)
        {
            if (!checkPropNames.Contains("OnLoanFrom")) { checkPropNames.Add("OnLoanFrom"); onLoanFromSynthetic = true; }
            if (!checkPropNames.Contains("Club")) { checkPropNames.Add("Club"); clubSynthetic = true; }
        }
        int? myClubUid = null;
        if (wantsLoan)
        {
            try { myClubUid = await OnQueue(queue, MyClub.DiscoverHumanClubUid); } catch { }
        }

        var entities = new JsonArray();
        var startedMs = Environment.TickCount64;
        for (var i = 0; i < uids.Count; i++)
        {
            var uid = uids[i];
            JsonObject entry;
            if (Environment.TickCount64 - startedMs > TotalDeadlineMs)
            {
                entry = new JsonObject { ["found"] = false, ["error"] = "total deadline exceeded" };
            }
            else
            {
                try
                {
                    entry = await ReadOneEntity(queue, kind, uid, checkPropNames, startedMs, TotalDeadlineMs);
                    PostProcessFound(entry, nameSynthetic);
                    ApplyScoutingSummary(entry, kind);

                    if (wantsTransferValue)
                    {
                        if (transferValueScrapesAttempted < TransferValueUiScrapeMaxUids)
                        {
                            transferValueScrapesAttempted++;
                            string tvText = null;
                            try { tvText = await QueryPlayers.ReadTransferValueForUid(queue, uid); } catch { }
                            ApplyTransferValueScrape(entry, uid, tvText);
                        }
                        else if (entry["missing"] is JsonArray missingCap)
                        {
                            missingCap.Add("TransferValue (ui-scrape-cap-reached; max " + TransferValueUiScrapeMaxUids + " uids per read_entity call)");
                        }
                    }

                    if (wantsLoan)
                    {
                        try { await ApplyLoanResolution(queue, entry, uid, myClubUid); } catch { }
                        if (onLoanFromSynthetic) StripSyntheticProp(entry, "OnLoanFrom");
                        if (clubSynthetic) StripSyntheticProp(entry, "Club");
                    }
                }
                catch (Exception e)
                {
                    entry = new JsonObject { ["found"] = false, ["error"] = e.Message };
                }
            }
            entry["uid"] = uid;
            entry["kind"] = kind;
            entities.Add(entry);
        }

        sw.Stop();
        return new JsonObject
        {
            ["ok"] = true,
            ["entities"] = entities,
            ["available_sections"] = availableNode,
            ["latency_ms"] = sw.Elapsed.TotalMilliseconds,
        };
    }

    /// <summary>
    /// Trust fix: Collect() previously
    /// set found = data.Count > 0, which is true even for a nonexistent uid --
    /// fabricating a PersonReference/ClubReference/etc for a bogus uid still
    /// lands every bound prop, just with placeholder content ("-", 0, "null")
    /// instead of failing to bind at all.
    /// This runs after ReadOneEntity/Collect and overrides ["found"]
    /// using the extra "IsValid" prop RunAsync appended to the request:
    ///   IsValid == "True"  -> genuinely resolved; found iff any requested
    ///                         prop besides IsValid actually landed.
    ///   IsValid == "False" -> uid did not resolve; found = false.
    ///   IsValid unavailable (kind doesn't expose it, or it stayed unbound —
    ///   the norm for person on a live career)
    ///                      -> fall back to the Name heuristic: a real
    ///                         record's Name is never null/blank/"-"
    ///                         /"null" (all four are exactly what a bogus-uid
    ///                         fabricate returns for every string prop).
    ///                         RunAsync appends "Name" synthetically when the
    ///                         caller didn't request it, so this branch always
    ///                         has something to inspect even on sections-only
    ///                         reads (["attributes"] etc.).
    /// The synthetic "IsValid" prop — and "Name", when it was synthetic — are
    /// stripped from data/missing either way so this is purely a found/error
    /// decision, never a payload change.
    /// </summary>
    private static void PostProcessFound(JsonObject entry, bool nameSynthetic)
    {
        if (entry["data"] is not JsonObject data) return;

        string isValidRaw = null;
        if (data.Remove("IsValid", out var iv)) isValidRaw = iv?.ToString();

        var nameStr = data["Name"]?.ToString();
        if (nameSynthetic) data.Remove("Name");

        if (entry["missing"] is JsonArray missingArr)
        {
            for (var i = missingArr.Count - 1; i >= 0; i--)
            {
                var s = missingArr[i]?.ToString() ?? "";
                if (s == "IsValid" || s.StartsWith("IsValid (", StringComparison.Ordinal) ||
                    (nameSynthetic && (s == "Name" || s.StartsWith("Name (", StringComparison.Ordinal))))
                    missingArr.RemoveAt(i);
            }
        }

        bool valid;
        string reason = null;
        if (string.Equals(isValidRaw, "True", StringComparison.OrdinalIgnoreCase))
        {
            valid = data.Count > 0;
            if (!valid) reason = "uid resolved (IsValid=True) but no requested props returned data";
        }
        else if (string.Equals(isValidRaw, "False", StringComparison.OrdinalIgnoreCase))
        {
            valid = false;
            reason = "uid did not resolve to a real record (IsValid=False)";
        }
        else
        {
            var nameLooksReal = !string.IsNullOrWhiteSpace(nameStr) && nameStr != "-" &&
                                 !string.Equals(nameStr, "null", StringComparison.OrdinalIgnoreCase);
            valid = data.Count > 0 && nameLooksReal;
            if (!valid) reason = nameLooksReal
                ? "uid resolved (Name check passed) but no requested props returned data"
                : "uid did not resolve to a real record (placeholder/garbage data returned; IsValid unavailable for this kind)";
        }

        entry["found"] = valid;
        if (!valid) entry["error"] = reason;
    }

    /// <summary>
    /// Replaces the "TransferValue (unknown-property-name)"
    /// missing[] entry PostProcessFound already left in place with real
    /// {display,sort} data on a successful UI scrape (see QueryPlayers.
    /// ReadTransferValueForUid/ParseTransferValue) -- or, on failure, with a
    /// clearer honest reason than the generic unknown-property message,
    /// never a guessed value. A prior IsValid=True-but-only-TransferValue-
    /// requested call can leave found=false purely because IsValid was the
    /// only landed prop and got stripped by PostProcessFound above; a
    /// successful scrape here means the uid's identity + the one requested
    /// field both resolved, so found/error are corrected to reflect that.
    /// </summary>
    private static void ApplyTransferValueScrape(JsonObject entry, int uid, string text)
    {
        if (entry?["missing"] is JsonArray missing)
        {
            for (var i = missing.Count - 1; i >= 0; i--)
            {
                var s = missing[i]?.ToString() ?? "";
                if (s == "TransferValue" || s.StartsWith("TransferValue (", StringComparison.Ordinal))
                    missing.RemoveAt(i);
            }
        }
        if (!string.IsNullOrEmpty(text) && entry?["data"] is JsonObject data)
        {
            var value = QueryPlayers.ParseTransferValue(text);
            value["source"] = "player-database-ui";
            value["uid_verified"] = uid;
            data["TransferValue"] = value;
            entry["found"] = true;
            entry.Remove("error");
        }
        else if (entry?["missing"] is JsonArray missing2)
        {
            missing2.Add("TransferValue (ui-cell-not-read; player database row could not be located/scrolled/scraped)");
        }
    }

    /// <summary>
    /// Per-player scouting summary, sibling of "data"/"missing" on every
    /// person entry. This is a live-search finding, not a design choice: an
    /// exhaustive live probe (2026-08-30, uid 25860 unscouted vs 7220/own-
    /// squad fully known) found no per-player scouting-KNOWLEDGE-PERCENTAGE
    /// binding reachable through the fabricate/bind channel machinery this
    /// file uses -- candidates tried and failed to land a value: KnowledgeLevel
    /// (a real PropertyIdentifierSet id that never fires off a fabricated
    /// PersonReference), CanViewScoutingKnowledge (a club-scoped bool, wrong
    /// context), PlayerKnowledge/ScoutingKnowledge/KnowledgePercentage/
    /// ScoutingCompleteness/ScoutedPercentage/IsScouted/ScoutStatus and a
    /// dozen more name guesses (all unknown-property-name), and
    /// ComparisonPlayerReport/NationalReport (real PlayerReportReference
    /// $refs, but that reference type doesn't cast to
    /// DatabaseRecordReference so its uid can't be chased the way Club/
    /// Nation refs are). So knowledge_pct is always null here -- honestly
    /// reporting "no percentage source", never a guess -- and the two counts
    /// are computed directly from whatever "Attribute*" fields this exact
    /// call actually returned: known (landed as an exact {"value"}) vs
    /// ranged (landed as a scouting-bounded {"min","max"}) -- see
    /// StructureAttributeValue. Both are 0 when the call didn't touch the
    /// attributes section at all, which is not itself "unscouted", just "not
    /// asked" -- callers wanting a real signal should read the "attributes"
    /// section (or explicit Attribute* props).
    /// </summary>
    private static void ApplyScoutingSummary(JsonObject entry, string kind)
    {
        if (!string.Equals(kind, "person", StringComparison.OrdinalIgnoreCase)) return;
        if (entry?["data"] is not JsonObject data) return;

        int known = 0, ranged = 0;
        foreach (var kv in data)
        {
            if (!kv.Key.StartsWith("Attribute", StringComparison.Ordinal)) continue;
            if (kv.Value is not JsonObject o) continue;
            if (o.ContainsKey("value")) known++;
            else if (o.ContainsKey("min")) ranged++;
        }

        entry["scouting"] = new JsonObject
        {
            ["knowledge_pct"] = null,
            ["attributes_known"] = known,
            ["attributes_ranged"] = ranged,
        };
    }

    // Matches the plain "N-M" text FM renders for an unscouted attribute's
    // estimated bound (live-observed, e.g. "2-4", "12-16") -- never a decimal
    // or a signed number in practice (attributes are 1-20 integers), but the
    // pattern is deliberately unambitious: anything it doesn't confidently
    // recognize falls through to the {"raw"} bucket instead of a guessed split.
    private static readonly Regex AttributeRangeRe = new(@"^(\d+)\s*-\s*(\d+)$", RegexOptions.Compiled);

    /// <summary>
    /// Structures an already-Decode()'d attribute value into the caller-
    /// facing {"value"}|{"min","max"}|{"raw"} shape (see class doc / the
    /// design note on ApplyScoutingSummary). Applied to every
    /// "Attribute*"-prefixed prop read_entity returns, and reused by
    /// QueryPlayers for the sample attributes its enrich path reads for the
    /// same purpose -- one shared rule, not two parallel guesses at the same
    /// text. A fully-known attribute decodes to a plain JSON number (the
    /// DynamicNumber path in Decode()) -> {"value": N}. An unscouted
    /// attribute the game can only bound-estimate decodes to a plain "N-M"
    /// string -> {"min", "max"}. Anything else (a hidden personality stat
    /// that returns literal text "Failed to get data", or any shape this
    /// hasn't seen before) is carried through as {"raw": "<text>"} rather
    /// than invented.
    /// </summary>
    internal static JsonObject StructureAttributeValue(JsonNode decoded)
    {
        if (decoded is JsonValue jv)
        {
            if (jv.TryGetValue<double>(out var num))
                return new JsonObject { ["value"] = num };
            if (jv.TryGetValue<string>(out var s) && s != null)
            {
                var m = AttributeRangeRe.Match(s.Trim());
                if (m.Success
                    && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lo)
                    && double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hi))
                    return new JsonObject { ["min"] = lo, ["max"] = hi };
                return new JsonObject { ["raw"] = s };
            }
        }
        return new JsonObject { ["raw"] = decoded?.ToString() };
    }

    /// <summary>Removes a synthetically-requested prop (one RunAsync added to
    /// checkPropNames on the caller's behalf, e.g. "Club" needed only to
    /// resolve loan direction) from both data and missing[] so it never
    /// leaks into a response the caller didn't ask for -- same pattern as
    /// the IsValid/Name stripping in PostProcessFound.</summary>
    private static void StripSyntheticProp(JsonObject entry, string propName)
    {
        if (entry?["data"] is JsonObject data) data.Remove(propName);
        if (entry?["missing"] is JsonArray missing)
        {
            for (var i = missing.Count - 1; i >= 0; i--)
            {
                var s = missing[i]?.ToString() ?? "";
                if (s == propName || s.StartsWith(propName + " (", StringComparison.Ordinal))
                    missing.RemoveAt(i);
            }
        }
    }

    /// <summary>Synthesizes entry.data["Loan"] = {status, club, until,
    /// recallable} from the already-landed OnLoanFrom/Club refs plus one
    /// extra targeted fetch (LoanContract.EndDate + the other club's Name)
    /// -- see the wantsLoan comment in RunAsync for the direction logic.
    /// recallable has no discoverable binding (exhaustive live search) and
    /// is always null, matching squad_report's own loan object.</summary>
    private static async Task ApplyLoanResolution(FMBridge.Voice.MainThreadQueue queue, JsonObject entry, int uid, int? myClubUid)
    {
        if (entry?["data"] is not JsonObject data) return;
        if (data["OnLoanFrom"] is not JsonObject olf) return; // never landed / unknown-property-name

        int? olfUid = olf["uid"] != null ? (int)olf["uid"] : (int?)null;
        string status = null;
        int? otherClubUid = null;
        if (olfUid.HasValue)
        {
            if (myClubUid.HasValue && olfUid.Value == myClubUid.Value)
            {
                status = "loaned_out";
                otherClubUid = (data["Club"] as JsonObject)?["uid"] is JsonValue cv && cv.TryGetValue<int>(out var cuid) ? cuid : (int?)null;
            }
            else
            {
                status = "loaned_in";
                otherClubUid = olfUid;
            }
        }

        var loan = new JsonObject { ["status"] = status, ["club"] = null, ["until"] = null, ["recallable"] = null };
        if (status != null)
        {
            var (clubName, until) = await FetchLoanExtras(queue, uid, otherClubUid);
            loan["club"] = clubName;
            loan["until"] = until;
        }
        data["Loan"] = loan;
    }

    /// <summary>One targeted extra fetch for the two pieces of loan detail
    /// nothing else surfaces: LoanContract.EndDate (a fresh fabricate+chain --
    /// the original per-uid read already tore its own channels down by the
    /// time RunAsync reaches loan post-processing, so this repeats the
    /// fabricate/bind step, same live-proven mechanism, not a new one) and
    /// the other club's display Name (fabricated fresh by uid, exactly like
    /// SquadReport.PlantClubName does for its own loan.club field).</summary>
    private static async Task<(string clubName, JsonNode until)> FetchLoanExtras(FMBridge.Voice.MainThreadQueue queue, int personUid, int? otherClubUid)
    {
        var plant = await OnQueue(queue, b => PlantLoanExtras(b, personUid, otherClubUid));
        var deadline = Environment.TickCount64 + PerUidWaitMs;
        while (Environment.TickCount64 < deadline)
        {
            var pending = await OnQueue(queue, b => PeekLoanExtras(b, plant));
            if (pending == 0) break;
            await Task.Delay(PollIntervalMs);
        }
        await OnQueue(queue, b => { StartLoanContractChain(b, plant); return true; });
        var deadline2 = Environment.TickCount64 + PerUidWaitMs;
        while (Environment.TickCount64 < deadline2)
        {
            var pending = await OnQueue(queue, b => PeekLoanExtras(b, plant));
            if (pending == 0) break;
            await Task.Delay(PollIntervalMs);
        }
        return await OnQueue(queue, b => CollectLoanExtras(b, plant));
    }

    private sealed class LoanExtrasPlant
    {
        public string Error;
        public FM.GamePlugin.GameInteropSubsystem Interop;
        public Bindings.Key LoanContractKey;
        public Bindings.ValueChangedCallback LoanContractCb;
        public TypedValue LoanContractValue;
        public bool LoanContractLanded;
        public Bindings.Key EndDateKey;
        public Bindings.ValueChangedCallback EndDateCb;
        public string EndDateValue;
        public bool EndDateChainStarted;
        public bool HasClub;
        public Bindings.Key ClubNameKey;
        public Bindings.ValueChangedCallback ClubNameCb;
        public string ClubNameValue;
    }

    private static LoanExtrasPlant PlantLoanExtras(BindingSubsystem bindings, int personUid, int? otherClubUid)
    {
        var plant = new LoanExtrasPlant();
        if (!GameSubsystems.TryGet<FM.GamePlugin.GameInteropSubsystem>(out var interop) || interop == null)
        {
            plant.Error = "game-interop-subsystem-not-found";
            return plant;
        }
        plant.Interop = interop;

        try
        {
            var fabricated = new FM.UI.PersonReference(personUid);
            var wrapped = TypedValue.Create<Il2CppSystem.Object>(fabricated.Cast<Il2CppSystem.Object>());
            var path = "__bridge.rele." + Interlocked.Increment(ref _counter);
            var rootKey = NativeBindings.CreatePath(bindings, path, Bindings.NodeFlags.Default);
            plant.LoanContractKey = NativeBindings.CreateRooted(bindings, rootKey, "LoanContract", Bindings.NodeFlags.RequiresContext);
            plant.LoanContractCb = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Bindings.ValueChangedCallback>(
                (Action<Bindings.Key, TypedValue>)((k, v) => { plant.LoanContractValue = v; plant.LoanContractLanded = v != null; }));
            bindings.Bind(ref plant.LoanContractKey, plant.LoanContractCb);
            bindings.Set(ref rootKey, wrapped, Bindings.SetFlags.UpdateHandler | Bindings.SetFlags.ForceUpdateValue);
        }
        catch (Exception e) { plant.Error = "person-fabricate-failed: " + e.Message; }

        if (otherClubUid.HasValue)
        {
            plant.HasClub = true;
            try
            {
                var fabricatedClub = new FM.UI.ClubReference(otherClubUid.Value);
                var wrappedClub = TypedValue.Create<Il2CppSystem.Object>(fabricatedClub.Cast<Il2CppSystem.Object>());
                var path2 = "__bridge.relc." + Interlocked.Increment(ref _counter);
                var clubRootKey = NativeBindings.CreatePath(bindings, path2, Bindings.NodeFlags.Default);
                plant.ClubNameKey = NativeBindings.CreateRooted(bindings, clubRootKey, "Name", Bindings.NodeFlags.RequiresContext);
                plant.ClubNameCb = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Bindings.ValueChangedCallback>(
                    (Action<Bindings.Key, TypedValue>)((k, v) => { try { plant.ClubNameValue = FMBridge.Eyes.TreeWalker.Describe(v); } catch { } }));
                bindings.Bind(ref plant.ClubNameKey, plant.ClubNameCb);
                bindings.Set(ref clubRootKey, wrappedClub, Bindings.SetFlags.UpdateHandler | Bindings.SetFlags.ForceUpdateValue);
            }
            catch { }
        }
        return plant;
    }

    private static int PeekLoanExtras(BindingSubsystem bindings, LoanExtrasPlant plant)
    {
        if (plant.Error != null) return 0;
        var pending = 0;
        if (!plant.LoanContractLanded)
        {
            try
            {
                var d = bindings.Get(ref plant.LoanContractKey);
                if (d != null) { plant.LoanContractValue = d; plant.LoanContractLanded = true; }
            }
            catch { }
            if (!plant.LoanContractLanded) pending++;
        }
        if (plant.EndDateChainStarted && plant.EndDateValue == null)
        {
            try
            {
                var d = bindings.Get(ref plant.EndDateKey);
                if (d != null) plant.EndDateValue = FMBridge.Eyes.TreeWalker.Describe(d);
            }
            catch { }
            if (plant.EndDateValue == null) pending++;
        }
        if (plant.HasClub && plant.ClubNameValue == null)
        {
            try
            {
                var d = bindings.Get(ref plant.ClubNameKey);
                if (d != null) plant.ClubNameValue = FMBridge.Eyes.TreeWalker.Describe(d);
            }
            catch { }
            if (plant.ClubNameValue == null) pending++;
        }
        return pending;
    }

    private static void StartLoanContractChain(BindingSubsystem bindings, LoanExtrasPlant plant)
    {
        if (plant.Error != null || plant.EndDateChainStarted || !plant.LoanContractLanded) return;
        try
        {
            var contractTv = bindings.Get(ref plant.LoanContractKey);
            if (contractTv != null)
            {
                plant.EndDateKey = NativeBindings.CreateRooted(bindings, plant.LoanContractKey, "EndDate", Bindings.NodeFlags.RequiresContext);
                plant.EndDateCb = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Bindings.ValueChangedCallback>(
                    (Action<Bindings.Key, TypedValue>)((k, v) => { try { plant.EndDateValue = FMBridge.Eyes.TreeWalker.Describe(v); } catch { } }));
                bindings.Bind(ref plant.EndDateKey, plant.EndDateCb);
                bindings.Set(ref plant.LoanContractKey, contractTv, Bindings.SetFlags.UpdateHandler | Bindings.SetFlags.ForceUpdateValue);
                plant.EndDateChainStarted = true;
            }
        }
        catch { }
    }

    private static (string clubName, JsonNode until) CollectLoanExtras(BindingSubsystem bindings, LoanExtrasPlant plant)
    {
        PeekLoanExtras(bindings, plant);
        string clubName = plant.HasClub && plant.ClubNameValue != null ? Decode(plant.ClubNameValue)?.ToString() : null;
        JsonNode until = plant.EndDateValue != null ? Decode(plant.EndDateValue) : null;
        try
        {
            var k = plant.LoanContractKey; bindings?.Unbind(ref k, plant.LoanContractCb);
            plant.Interop?.CloseChannel(k);
        }
        catch { }
        if (plant.EndDateChainStarted)
        {
            try
            {
                var k2 = plant.EndDateKey; bindings?.Unbind(ref k2, plant.EndDateCb);
                plant.Interop?.CloseChannel(k2);
            }
            catch { }
        }
        if (plant.HasClub)
        {
            try
            {
                var k3 = plant.ClubNameKey; bindings?.Unbind(ref k3, plant.ClubNameCb);
                plant.Interop?.CloseChannel(k3);
            }
            catch { }
        }
        return (clubName, until);
    }

    /// <summary>
    /// Shared single-uid plant/poll/collect loop, factored out of RunAsync so
    /// other verbs (my_club) can reuse the exact
    /// same fabricate-plant-bind-poll-collect machinery for a curated prop
    /// list without going through the WS request/response shape or
    /// reimplementing any channel plumbing.
    /// </summary>
    public static async Task<JsonObject> ReadOneEntity(FMBridge.Voice.MainThreadQueue queue, string kind, int uid,
        List<string> propNames, long? startedMs = null, long totalDeadlineMs = TotalDeadlineMs)
    {
        var start = startedMs ?? Environment.TickCount64;
        var plant = await OnQueue(queue, b => Plant(b, kind, uid, propNames));
        if (plant.Error != null)
            return new JsonObject { ["found"] = false, ["error"] = plant.Error };

        var pending = await OnQueue(queue, b => Peek(b, plant));
        var deadline = Environment.TickCount64 + PerUidWaitMs;
        while (pending > 0 && Environment.TickCount64 < deadline &&
               Environment.TickCount64 - start <= totalDeadlineMs)
        {
            await Task.Delay(PollIntervalMs);
            pending = await OnQueue(queue, b => Peek(b, plant));
        }
        return await OnQueue(queue, b => Collect(b, plant));
    }

    // ---------------------------------------------------------- main thread

    private static EntityPlant Plant(BindingSubsystem bindings, string kind, int uid, List<string> propNames)
    {
        var plant = new EntityPlant();
        if (!GameSubsystems.TryGet<FM.GamePlugin.GameInteropSubsystem>(out var interop) || interop == null)
        {
            plant.Error = "game-interop-subsystem-not-found";
            return plant;
        }
        plant.Interop = interop;

        SI.Interop.InteropReference fabricated;
        try
        {
            fabricated = kind switch
            {
                "person" => new FM.UI.PersonReference(uid),
                "club" => new FM.UI.ClubReference(uid),
                "nation" => new FM.UI.NationReference(uid),
                "competition" => new FM.UI.CompReference(uid),
                _ => null,
            };
        }
        catch (Exception e) { plant.Error = "fabricate-ctor-failed: " + e.Message; return plant; }
        if (fabricated == null) { plant.Error = "unknown-kind:" + kind; return plant; }

        TypedValue wrapped;
        try
        {
            wrapped = TypedValue.Create<Il2CppSystem.Object>(fabricated.Cast<Il2CppSystem.Object>());
        }
        catch (Exception e) { plant.Error = "typedvalue-create-failed: " + e.Message; return plant; }

        plant.ParentPath = "__bridge.re." + Interlocked.Increment(ref _counter);
        Bindings.Key parentKey;
        try
        {
            parentKey = NativeBindings.CreatePath(bindings, plant.ParentPath, Bindings.NodeFlags.Default);
        }
        catch (Exception e) { plant.Error = "create-parent-failed: " + e.Message; return plant; }
        plant.ParentKey = parentKey;

        // Child nodes + callbacks BEFORE the parent value lands, so the dirty
        // walk after Set sees children needing data targets.
        for (var i = 0; i < propNames.Count; i++)
        {
            var prop = new PropRead { Name = propNames[i] };
            try
            {
                var span = SpanOf(prop.Name);
                var propId = PropertyIdentifierSet.Instance.GetID(span);
                if (propId.ID == 0)
                {
                    prop.Unresolved = true;
                    prop.Note = "unknown-property-name";
                    plant.Props.Add(prop);
                    continue;
                }
                prop.Key = NativeBindings.CreateRooted(bindings, parentKey, prop.Name, Bindings.NodeFlags.RequiresContext);
                if (prop.Key.m_key == parentKey.m_key)
                {
                    prop.Unresolved = true;
                    prop.Note = "child-key-equals-parent (empty span?)";
                    plant.Props.Add(prop);
                    continue;
                }
                prop.Cb = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Bindings.ValueChangedCallback>(
                    (Action<Bindings.Key, TypedValue>)((k, v) =>
                    {
                        DescribeAndResolve(v, out var d, out var refUid, out var refTable,
                            out var resolvedDisplay, out var resolvedSort, out var recordPending);
                        var fv = new FiredValue
                        {
                            Desc = d,
                            RefUid = refUid,
                            RefTable = refTable,
                            ResolvedDisplay = resolvedDisplay,
                            ResolvedSort = resolvedSort,
                            RecordPending = recordPending,
                        };
                        lock (prop.Fired) prop.Fired.Add(fv);
                    }));
                bindings.Bind(ref prop.Key, prop.Cb);
                prop.Bound = true;
            }
            catch (Exception e)
            {
                prop.Unresolved = true;
                prop.Note = e.Message;
            }
            plant.Props.Add(prop);
        }

        try
        {
            bindings.Set(ref parentKey, wrapped,
                Bindings.SetFlags.UpdateHandler | Bindings.SetFlags.ForceUpdateValue);
        }
        catch (Exception e) { plant.Error = "parent-set-failed: " + e.Message; }
        return plant;
    }

    /// <summary>Non-destructive sweep: harvest fired/direct values for props
    /// that have one, count the rest. Safe to call repeatedly while waiting
    /// for frames to tick.</summary>
    private static int Peek(BindingSubsystem bindings, EntityPlant plant)
    {
        var pending = 0;
        for (var i = 0; i < plant.Props.Count; i++)
        {
            var prop = plant.Props[i];
            if (prop.Unresolved) continue;
            if (prop.Value != null && !prop.NeedsRecheck) continue;

            string d = null;
            int? refUid = null;
            string refTable = null;
            string resolvedDisplay = null;
            double? resolvedSort = null;
            bool recordPending = false;

            // On a recheck pass, skip the (stale) Fired cache and re-read
            // the live node directly -- the callback only fires again if the
            // *outer* TypedValue reference changes, which it won't just
            // because the Record's own dictionary finished populating.
            if (!prop.NeedsRecheck)
            {
                lock (prop.Fired)
                {
                    if (prop.Fired.Count > 0)
                    {
                        var last = prop.Fired[prop.Fired.Count - 1];
                        d = last.Desc;
                        refUid = last.RefUid;
                        refTable = last.RefTable;
                        resolvedDisplay = last.ResolvedDisplay;
                        resolvedSort = last.ResolvedSort;
                        recordPending = last.RecordPending;
                    }
                }
            }
            if (d == null && bindings != null)
            {
                try
                {
                    var direct = bindings.Get(ref prop.Key);
                    if (direct != null)
                        DescribeAndResolve(direct, out d, out refUid, out refTable,
                            out resolvedDisplay, out resolvedSort, out recordPending);
                }
                catch { }
            }
            if (d == null) { pending++; continue; }

            prop.Value = d;
            prop.RefUid = refUid;
            prop.RefTable = refTable;
            prop.ResolvedDisplay = resolvedDisplay;
            prop.ResolvedSort = resolvedSort;
            prop.NeedsRecheck = recordPending;
            // Value already has a safe fallback stored (bare $ref shape) --
            // keep polling a bit longer only to try to improve it to a
            // resolved scalar, never blocking on it past the normal per-uid
            // deadline the caller already enforces.
            if (recordPending) pending++;
        }
        return pending;
    }

    private static JsonObject Collect(BindingSubsystem bindings, EntityPlant plant)
    {
        Peek(bindings, plant);
        var data = new JsonObject();
        var missing = new JsonArray();
        for (var i = 0; i < plant.Props.Count; i++)
        {
            var prop = plant.Props[i];
            if (prop.Bound)
            {
                var keyCopy = prop.Key;
                try { bindings?.Unbind(ref keyCopy, prop.Cb); } catch { }
                try { plant.Interop?.CloseChannel(keyCopy); } catch { }
            }
            if (prop.Value != null)
            {
                var decoded = Decode(prop.Value, prop.RefUid, prop.RefTable, prop.ResolvedDisplay, prop.ResolvedSort);
                data[prop.Name] = prop.Name.StartsWith("Attribute", StringComparison.Ordinal)
                    ? StructureAttributeValue(decoded)
                    : decoded;
                if (prop.Name == "Position")
                {
                    // Additive decoded sibling -- raw bitmask stays
                    // under "Position" unchanged, decoded compact label
                    // ("GK"/"D (RLC)"/"AM (RL)"/...) goes in "PositionDecoded".
                    var decodedPosition = PositionDecode.Decode(prop.Value);
                    if (decodedPosition != null) data["PositionDecoded"] = decodedPosition;
                }
            }
            else missing.Add(prop.Note != null ? prop.Name + " (" + prop.Note + ")" : prop.Name);
        }
        return new JsonObject
        {
            ["found"] = data.Count > 0,
            ["data"] = data,
            ["missing"] = missing,
        };
    }

    /// <summary>
    /// Inline DynamicReference resolution.
    /// A property landing as a DynamicReference is, at that instant, a
    /// populated SI.Bindable.DynamicReference : SI.Core.Record : Dictionary
    /// &lt;uint,TypedValue&gt; -- resolve it to a scalar right here
    /// (TryCast&lt;Record&gt;, keyed TryGetValue, never enumerate) instead of
    /// leaving a bare {"$ref":"DynamicReference"} wrapper a caller would
    /// need further chained reads to chase.
    ///
    /// Priority: PropertyValue (plain attribute wrappers, e.g.
    /// AttributeCrossing) -> DisplayValue+SortValue (Wage, FinancialStatus,
    /// TransferStatusDetails, ...) -> unresolved (caller keeps the original
    /// $ref-with-uid fallback, so nothing regresses).
    ///
    /// Genuine entity references (Nation, Club, ...) are detected FIRST via
    /// TryCast&lt;DatabaseRecordReference&gt; and returned immediately -- they are
    /// never flattened, staying chainable $refs exactly as before this
    /// change. Only values that fail that cast AND describe as type
    /// "DynamicReference" specifically (never "NationReference"/
    /// "ClubReference"/etc, which already returned above) are candidates for
    /// Record resolution.
    ///
    /// pending=true means "this landed as a Record but neither known key was
    /// present AND the dictionary was empty" -- the caller should keep
    /// polling (reusing read_entity's existing per-uid wait loop, no new
    /// threading) rather than freeze a possibly-not-yet-populated result.
    /// In practice this is expected to rarely trigger: live experiments
    /// found a landed Wage/FinancialStatus Record
    /// already fully populated (recordCount:2, both keys present) on the
    /// very first sweep with no extra wait -- the deserializer that builds a
    /// Record does so in one shot before the TypedValue is ever handed to a
    /// callback -- but the guard costs nothing and protects against the one
    /// scenario (a race on the very first delivering tick) that would
    /// otherwise silently ship a wrong/empty resolution.
    /// </summary>
    /// <summary>internal (not private): reused by SquadReport.cs for its own
    /// contract-chain second-hop resolution (FullContract.Wage / .EndDate /
    /// .ContractLength / .ContractDaysElapsed) — the exact same DynamicReference
    /// -> scalar collapse, no reimplementation.</summary>
    internal static void DescribeAndResolve(TypedValue v, out string desc, out int? refUid,
        out string refTable, out string resolvedDisplay, out double? resolvedSort, out bool pending)
    {
        refUid = null; refTable = null; resolvedDisplay = null; resolvedSort = null; pending = false;
        try { desc = FMBridge.Eyes.TreeWalker.Describe(v); } catch { desc = "<describe-error>"; }

        // Entity references first, unconditionally preserved as $refs.
        try
        {
            var dbRef = v.Get()?.TryCast<FM.UI.DatabaseRecordReference>();
            if (dbRef != null)
            {
                refUid = dbRef.m_index;
                refTable = dbRef.Type.ToString();
                return;
            }
        }
        catch { }

        if (desc == null || !desc.StartsWith("DynamicReference", StringComparison.Ordinal)) return;

        try
        {
            var record = v.Get()?.TryCast<Record>();
            if (record == null) return;

            var pvId = PropertyValueId;
            if (pvId != 0 && record.TryGetValue(pvId, out var pv) && pv != null)
            {
                try { desc = FMBridge.Eyes.TreeWalker.Describe(pv); } catch { }
                return;
            }
            if (record.TryGetValue(DisplayValueId, out var dv) && dv != null)
            {
                try { resolvedDisplay = FMBridge.Eyes.TreeWalker.Describe(dv); } catch { }
                if (resolvedDisplay != null && record.TryGetValue(SortValueId, out var sv) && sv != null)
                {
                    try
                    {
                        var sd = FMBridge.Eyes.TreeWalker.Describe(sv);
                        var p = sd.IndexOf(':');
                        if (p >= 0 && double.TryParse(sd.Substring(p + 1).TrimEnd('f', 'm', 'd'),
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var n))
                            resolvedSort = n;
                    }
                    catch { }
                }
                return;
            }

            // Neither known key present. Count is a plain Dictionary
            // property getter, not an enumeration -- safe per house rules.
            var count = -1;
            try { count = record.Count; } catch { }
            if (count <= 0) pending = true; // not populated yet -- keep polling
            // else: a Record shape we don't have a resolver for yet -- fall
            // back to the bare $ref (still correct/safe, matches pre-change
            // behavior for this case).
        }
        catch { }
    }

    // ------------------------------------------------------------ decoding

    /// <summary>Splits a Describe-ladder output "Type:payload" into JSON:
    /// DynamicNumber payloads become numbers when parseable, reference types
    /// become {"$ref": TypeName} — plus "uid"/"table" when a
    /// DatabaseRecordReference target was captured at read time, making the
    /// ref directly chainable into another read_entity call — everything
    /// else stays as the decoded text (names arrive markup-free — Describe
    /// runs TextFunctions.SubString(removeEmbeddedData:true)).</summary>
    /// <summary>internal (not private): reused by SquadReport.cs so its contract-chain
    /// fields decode through the exact same ladder as read_entity's own props.</summary>
    internal static JsonNode Decode(string described, int? refUid = null, string refTable = null,
        string resolvedDisplay = null, double? resolvedSort = null)
    {
        // Inline DynamicReference resolution: a
        // DisplayValue+SortValue composite (Wage, FinancialStatus, ...)
        // resolves to an object carrying both the human-readable string and
        // the raw sortable number, instead of a bare $ref wrapper a caller
        // would have to chain further reads to reach.
        if (resolvedDisplay != null)
        {
            var dsep = resolvedDisplay.IndexOf(':');
            var dpayload = dsep < 0 ? resolvedDisplay : resolvedDisplay.Substring(dsep + 1);
            var node = new JsonObject { ["display"] = dpayload };
            if (resolvedSort.HasValue) node["sort"] = resolvedSort.Value;
            return node;
        }

        var sep = described.IndexOf(':');
        var type = sep < 0 ? described : described.Substring(0, sep);
        var payload = sep < 0 ? "" : described.Substring(sep + 1);
        if (type == "DynamicNumber")
        {
            if (double.TryParse(payload.TrimEnd('f', 'm', 'd'),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var num))
                return JsonValue.Create(num);
            return JsonValue.Create(payload);
        }
        if (payload == "<unprintable>" || type.EndsWith("Reference"))
        {
            var refNode = new JsonObject { ["$ref"] = type };
            if (refUid.HasValue)
            {
                refNode["uid"] = refUid.Value;
                refNode["table"] = refTable;
            }
            return refNode;
        }
        return JsonValue.Create(payload);
    }

    // ------------------------------------------------------------- helpers

    internal static async Task<T> OnQueue<T>(FMBridge.Voice.MainThreadQueue queue, Func<BindingSubsystem, T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Enqueue(() =>
        {
            try
            {
                var bindings = FMBridge.Eyes.SpikeHooks.CapturedBindings;
                if (bindings == null)
                {
                    tcs.SetException(new InvalidOperationException("bindings-not-captured"));
                    return;
                }
                tcs.SetResult(func(bindings));
            }
            catch (Exception e)
            {
                try { tcs.SetException(e); } catch { }
            }
        });
        var done = await Task.WhenAny(tcs.Task, Task.Delay(QueueTimeoutMs));
        if (done != tcs.Task) throw new TimeoutException("main thread did not answer in " + QueueTimeoutMs + "ms");
        return tcs.Task.Result;
    }

    private static List<string> StringArray(JsonNode node, string field, out string error)
    {
        error = null;
        if (node == null) return null;
        if (node is not JsonArray arr)
        {
            error = "'" + field + "' must be an array of strings";
            return null;
        }
        var list = new List<string>();
        foreach (var n in arr)
        {
            var s = (string)n;
            if (string.IsNullOrEmpty(s)) continue;
            list.Add(s);
        }
        return list;
    }

    private static void AddUnique(List<string> target, IEnumerable<string> source)
    {
        foreach (var s in source)
        {
            if (target.IndexOf(s) >= 0) continue;
            target.Add(s);
        }
    }

    private static JsonObject Fail(string error, Stopwatch sw)
    {
        sw.Stop();
        return new JsonObject { ["ok"] = false, ["error"] = error, ["latency_ms"] = sw.Elapsed.TotalMilliseconds };
    }

    // Live-proven resolution path: GetID over MemoryExtensions.AsSpan.
    // Do NOT replace with id math — native content ids are opaque FourCC
    // mnemonics, not sequential integers.
    internal static Il2CppSystem.ReadOnlySpan<char> SpanOf(string s)
    {
        return Il2CppSystem.MemoryExtensions.AsSpan(s);
    }
}
