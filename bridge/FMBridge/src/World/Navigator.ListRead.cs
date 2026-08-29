using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json.Nodes;

namespace FMBridge.World;

/// <summary>
/// Reads ALL rows of a virtualized list/table widget (squad table, inbox
/// list) directly from the widget object, without scrolling.
///
/// Core insight: the binding-tree walkers (TreeWalker et al.) only see rows
/// whose VisualElement has actually been materialized/bound (OnBindItem) —
/// i.e. only the currently-scrolled-into-view window. But the UI Toolkit
/// visual tree (the same tree Navigator's Collect/CollectBySubstring family
/// already walks from PanelManager.RootVisualElement) holds the list/table
/// widget objects themselves, and those objects carry the FULL un-windowed
/// row list as a plain IList field/property — independent of what's
/// materialized.
///
/// Rank #1: VirtualisedList descendant walk -> .Items IList -> index loop ->
/// TryCast&lt;TypedValue&gt; -> TreeWalker.Describe decode ladder. Live-observed
/// lesson: the widget's public ItemCount getter reports the true total, but
/// reading Count off the resolved IList can reflect &lt;=0 (interface-proxy
/// GetType() has no usable Count). So Count is NEVER trusted here: the
/// authoritative total is StreamedListView.ItemCount (public, live-proven)
/// and the IList is iterated by index with a per-row try/catch that stops on
/// the first out-of-range read.
///
/// Rank #2 fallback: StreamedListView.GetItemFromSource(int) (protected;
/// republished public by Il2CppInterop) invoked via ordinary .NET reflection
/// per index — plain reflection over a real il2cpp-backed method, no
/// interop-struct construction.
/// </summary>
internal static partial class Navigator
{
    private sealed class ListWidgetMatch
    {
        public string Name;
        public string TypeName;
        public int ItemCount;
        public UnityEngine.UIElements.VisualElement Node;
        public bool IsVirtualisedList;
    }

    /// <summary>
    /// query: case-insensitive substring matched against the list/table
    /// element names AND widget type names (empty/omitted matches all) —
    /// both pools merged, since FM's real tables are often unnamed and a
    /// literal-named sibling would otherwise shadow them. When several
    /// widgets match, the one with the largest ItemCount wins (the "main"
    /// list); every match is reported under "matches". matchIndex >= 0
    /// selects that index within "matches" deterministically instead.
    /// max: row cap (default 500).
    /// </summary>
    public static JsonObject ListRead(string query, int max, int matchIndex = -1)
    {
        try
        {
            var pm = Pm();
            var root = pm != null ? pm.RootVisualElement : null;
            if (root == null) return new JsonObject { ["ok"] = false, ["reason"] = "no-root-visualelement" };

            var widgets = new List<ListWidgetMatch>();
            CollectListWidgets(root, widgets, 0);

            var needle = (query ?? "").Trim().ToLowerInvariant();
            var matches = new List<ListWidgetMatch>();
            bool byName = false, byType = false;
            foreach (var w in widgets)
            {
                var nm = (w.Name ?? "").ToLowerInvariant();
                var tn = (w.TypeName ?? "").ToLowerInvariant();
                if (needle.Length == 0 || nm.Contains(needle)) { matches.Add(w); byName = true; }
                else if (needle.Length > 0 && tn.Contains(needle)) { matches.Add(w); byType = true; }
            }
            string how = needle.Length == 0
                ? "all-widgets:max-itemcount"
                : byName && byType ? "name+type:max-itemcount"
                : byType ? "type-substring:max-itemcount"
                : "name-substring:max-itemcount";

            if (matchIndex >= 0 && matchIndex < matches.Count)
            {
                how = "explicit-match-index";
            }

            if (matches.Count == 0)
            {
                var avail = new JsonArray();
                foreach (var w in widgets)
                    avail.Add(new JsonObject { ["name"] = w.Name, ["type"] = w.TypeName, ["itemCount"] = w.ItemCount });
                return new JsonObject
                {
                    ["ok"] = false,
                    ["error"] = "no-list-widget-matches:" + query,
                    ["candidates"] = avail,
                };
            }

            // Deterministic pick: explicit match_index if given, else largest
            // ItemCount (the screen's main list), ties broken by discovery order.
            var chosen = matches[0];
            if (matchIndex >= 0 && matchIndex < matches.Count)
            {
                chosen = matches[matchIndex];
                how = "explicit-match-index";
            }
            else
            {
                foreach (var m in matches)
                    if (m.ItemCount > chosen.ItemCount) chosen = m;
            }

            var matchesJson = new JsonArray();
            foreach (var m in matches)
                matchesJson.Add(new JsonObject { ["name"] = m.Name, ["type"] = m.TypeName, ["itemCount"] = m.ItemCount });

            int cap = Math.Clamp(max <= 0 ? 500 : max, 1, 2000);
            int total = chosen.ItemCount; // public StreamedListView/Widget getter — live-proven authoritative

            var rows = new JsonArray();
            int returned = 0;
            string note;
            string sourceKind;

            Il2CppSystem.Collections.IList items = null;
            try { items = ResolveItems(chosen, out sourceKind); } catch { items = null; sourceKind = null; }

            int read = -1;
            if (items != null) { try { read = ReadRows(items, cap, rows, ref returned); } catch { read = -1; } }

            if (returned > 0)
            {
                note = "items:" + (sourceKind ?? "?")
                    + (total > 0 && read >= 0 && read != total ? $" count-mismatch:itemcount={total},ilist={read}" : "");
            }
            else if (!chosen.IsVirtualisedList)
            {
                // Rank #2 fallback per notes §3/§6.2: .Items null/stale —
                // reflection-invoke the protected GetItemFromSource(int).
                note = ReadViaGetItemFromSource(chosen, cap, rows, ref returned);
            }
            else
            {
                note = "counts-only: virtualisedlist-items-null";
            }

            var result = new JsonObject
            {
                ["ok"] = true,
                ["element"] = chosen.Name,
                ["type"] = chosen.TypeName,
                ["count"] = total,
                ["returned"] = returned,
                ["how"] = how,
                ["matches"] = matchesJson,
                ["note"] = note,
                ["rows"] = rows,
            };
            var cols = ReadColumnIds(chosen.Node);
            if (cols != null) result["columns"] = cols;
            var win = ReadVisibleWindow(chosen.Node);
            if (win != null) result["visibleWindow"] = win;
            return result;
        }
        catch (Exception e)
        {
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
    }

    /// <summary>
    /// Index loop over the resolved IList (never foreach an interop
    /// collection — project crash rule). Stops early on an out-of-range read
    /// or three consecutive row failures, so an overstated/unknown Count can
    /// never wedge the verb. Returns the number of indexes actually readable
    /// (-1 when unknown), appends one JSON row per successful read.
    /// </summary>
    /// <summary>internal (not private): reused by SquadReport.cs to decode a
    /// channel-delivered `List&lt;TypedValue&gt;` (e.g. Team.Players) the exact
    /// same safe index-loop way a UI widget's backing IList is read here —
    /// same crash-safety guarantees (Count+indexer only, never foreach), no new pattern.</summary>
    internal static int ReadRows(Il2CppSystem.Collections.IList items, int cap, JsonArray rows, ref int returned)
    {
        int known = TryListCount(items);
        int n = known >= 0 ? Math.Min(known, cap) : cap;
        int readable = 0;
        int consecutiveErrors = 0;
        for (int i = 0; i < n; i++)
        {
            object boxed;
            try { boxed = items[i]; }
            catch
            {
                // Past the end (or dead list): stop; report what we proved.
                readable = readable > 0 || i > 0 ? readable : 0;
                return readable;
            }
            try
            {
                rows.Add(BuildRow(i, boxed));
                returned++;
                readable = i + 1;
                consecutiveErrors = 0;
            }
            catch (Exception e)
            {
                // Never let one bad row kill the whole read.
                rows.Add(new JsonObject { ["index"] = i, ["error"] = e.Message });
                if (++consecutiveErrors >= 3) return readable;
            }
        }
        return known >= 0 ? known : readable;
    }

    /// <summary>Decodes one source row into the wire shape: uid (+ refType)
    /// when the row's TypedValue wraps a DatabaseRecordReference (Person/
    /// Team/ClubReference — m_index + Type), text via the unchanged
    /// TreeWalker.Describe ladder, cells left empty (per-row column values
    /// only exist for materialized rows).</summary>
    private static JsonObject BuildRow(int index, object boxed)
    {
        var row = new JsonObject { ["index"] = index, ["cells"] = new JsonObject() };
        var iob = boxed as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
        var tv = iob?.TryCast<SI.Core.TypedValue>();
        if (tv == null)
        {
            row["text"] = "<non-typedvalue:" + SafeType(boxed) + ">";
            return row;
        }
        try
        {
            var dbRef = tv.Get()?.TryCast<FM.UI.DatabaseRecordReference>();
            if (dbRef != null)
            {
                row["uid"] = dbRef.m_index;
                try { row["refType"] = dbRef.Type.ToString(); } catch { }
            }
        }
        catch { }
        try { row["text"] = FMBridge.Eyes.TreeWalker.Describe(tv); }
        catch (Exception e) { row["text"] = "<describe-error:" + e.Message + ">"; }
        return row;
    }

    private static string SafeType(object o)
    {
        try { return o.GetType().Name; } catch { return "?"; }
    }

    /// <summary>
    /// Row count of an interop IList WITHOUT trusting reflection-by-name
    /// alone (live lesson: interface-proxy handles reflect as
    /// Il2CppObjectBase and hide Count). Direct Il2CppSystem.Collections.
    /// ICollection cast first (plain property read, no construction), then
    /// the runtime-type property, else -1 (= unknown; callers probe).
    /// </summary>
    internal static int TryListCount(Il2CppSystem.Collections.IList items)
    {
        if (items == null) return -1;
        try
        {
            var col = items.TryCast<Il2CppSystem.Collections.ICollection>();
            if (col != null) return col.Count;
        }
        catch { }
        try
        {
            var pi = items.GetType().GetProperty("Count");
            if (pi != null) return (int)pi.GetValue(items);
        }
        catch { }
        return -1;
    }

    private static string ReadViaGetItemFromSource(ListWidgetMatch chosen, int cap, JsonArray rows, ref int returned)
    {
        try
        {
            var sl = chosen.Node.TryCast<SI.Bindable.StreamedListView>();
            if (sl == null) return "counts-only: not-a-streamedlistview";
            // Il2CppInterop republishes the game's protected members as public
            // on the wrapper (NativeMethodInfoPtr_GetItemFromSource_Protected_...
            // in SI.Bindable.dll), so search public AND non-public.
            var mi = sl.GetType().GetMethod("GetItemFromSource",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (mi == null) return "counts-only: getitemfromsource-not-found:" + sl.GetType().FullName;

            int total = chosen.ItemCount;
            int n = total < 0 ? 0 : Math.Min(total, cap);
            int consecutiveErrors = 0;
            for (int i = 0; i < n; i++)
            {
                try
                {
                    var boxed = mi.Invoke(sl, new object[] { i });
                    rows.Add(BuildRow(i, boxed));
                    returned++;
                    consecutiveErrors = 0;
                }
                catch (Exception e)
                {
                    rows.Add(new JsonObject { ["index"] = i, ["error"] = e.Message });
                    if (++consecutiveErrors >= 3) break;
                }
            }
            return "reflection-fallback:GetItemFromSource";
        }
        catch (Exception e)
        {
            return "counts-only: reflection-failed:" + e.Message;
        }
    }

    /// <summary>
    /// Resolves the full, un-windowed backing IList for a matched widget:
    /// StreamedListView.SourceData first (the exact list handed to SetList),
    /// then its VirtualisedList child's public Items, then
    /// — when the match itself IS a VirtualisedList — its own Items.
    /// Protected members are reached by reflection because Il2CppInterop
    /// republishes them public on the wrapper (search both visibilities).
    /// </summary>
    private static Il2CppSystem.Collections.IList ResolveItems(ListWidgetMatch chosen, out string sourceKind)
    {
        sourceKind = null;
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        if (!chosen.IsVirtualisedList)
        {
            var sl = chosen.Node.TryCast<SI.Bindable.StreamedListView>();
            if (sl != null)
            {
                try
                {
                    var pd = sl.GetType().GetProperty("SourceData", Any);
                    var srcBoxed = pd?.GetValue(sl) as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
                    var src = srcBoxed?.TryCast<Il2CppSystem.Collections.IList>();
                    if (src != null) { sourceKind = "SourceData"; return src; }
                }
                catch { }
            }
        }
        try
        {
            SI.UI.VirtualisedList vl = null;
            if (chosen.IsVirtualisedList)
                vl = chosen.Node.TryCast<SI.UI.VirtualisedList>();
            else
            {
                var sl2 = chosen.Node.TryCast<SI.Bindable.StreamedListView>();
                var pi = sl2?.GetType().GetProperty("ListElement", Any);
                var elBoxed = pi?.GetValue(sl2) as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
                vl = elBoxed?.TryCast<SI.UI.VirtualisedList>();
            }
            var items = vl?.Items;
            if (items != null) { sourceKind = "ListElement.Items"; return items; }
        }
        catch { }
        return null;
    }

    /// <summary>Best-effort column schema for StreamedTable widgets: ordered
    /// ColumnIDs off the private m_columns list via pure reflection
    /// reads. Null when the widget isn't a table or anything throws.</summary>
    private static JsonArray ReadColumnIds(UnityEngine.UIElements.VisualElement node)
    {
        try
        {
            var st = node.TryCast<SI.Bindable.StreamedTable>();
            if (st == null) return null;
            const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var colsObj = st.GetType().GetField("m_columns", Any)?.GetValue(st)
                          ?? (object)(st.GetType().GetProperty("m_columns", Any)?.GetValue(st));
            if (colsObj == null) return null;
            var t = colsObj.GetType();
            var countPi = t.GetProperty("Count");
            var itemPi = t.GetProperty("Item");
            if (countPi == null || itemPi == null) return null;
            int cnt = (int)countPi.GetValue(colsObj);
            var arr = new JsonArray();
            for (int i = 0; i < cnt && i < 64; i++)
            {
                var col = itemPi.GetValue(colsObj, new object[] { i }) as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
                if (col == null) continue;
                var id = col.GetType().GetProperty("ColumnID")?.GetValue(col) as string;
                arr.Add(id ?? ("#" + i));
            }
            return arr.Count > 0 ? arr : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Resolves a single "main" list/table widget by the same case-
    /// insensitive name-substring rule ListRead uses (largest ItemCount wins
    /// among matches; empty query matches every widget). Factored out for
    /// ScanUidChunk/ScrollToIndex/GetVisibleWindow, which only need the one
    /// widget, not ListRead's full multi-match diagnostics.
    /// </summary>
    private static ListWidgetMatch ResolveSingleListWidget(string query)
    {
        var pm = Pm();
        var root = pm != null ? pm.RootVisualElement : null;
        if (root == null) return null;

        var widgets = new List<ListWidgetMatch>();
        CollectListWidgets(root, widgets, 0);

        var needle = (query ?? "").Trim().ToLowerInvariant();
        ListWidgetMatch chosen = null;
        foreach (var w in widgets)
        {
            var nm = (w.Name ?? "").ToLowerInvariant();
            var tn = (w.TypeName ?? "").ToLowerInvariant();
            if (needle.Length == 0 || nm.Contains(needle) || tn.Contains(needle))
            {
                if (chosen == null || w.ItemCount > chosen.ItemCount) chosen = w;
            }
        }
        return chosen;
    }

    /// <summary>
    /// Resolves the actual SI.UI.VirtualisedList node behind a matched
    /// widget -- either the match itself (when it IS a VirtualisedList) or,
    /// for a StreamedListView match, its internal ListElement child (same
    /// reflection lookup ResolveItems already uses to reach .Items, just
    /// returning the node instead of its backing list so callers can invoke
    /// the node's own public methods, e.g. ScrollTo).
    /// </summary>
    private static SI.UI.VirtualisedList ResolveVirtualisedListNode(ListWidgetMatch chosen)
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        try
        {
            if (chosen.IsVirtualisedList)
                return chosen.Node.TryCast<SI.UI.VirtualisedList>();
            var sl = chosen.Node.TryCast<SI.Bindable.StreamedListView>();
            var pi = sl?.GetType().GetProperty("ListElement", Any);
            var elBoxed = pi?.GetValue(sl) as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
            return elBoxed?.TryCast<SI.UI.VirtualisedList>();
        }
        catch { return null; }
    }

    /// <summary>
    /// Shortlist add/remove's out-of-window locate fix. Live recon confirmed
    /// that typing a player's name into the Player Database search box to
    /// materialize the row has no target to type into: a scoped filter-bar
    /// sweep and a full-screen text-field scan both came back empty -- the
    /// only text-adjacent control is "Edit Search", which opens a native
    /// (non-UITK-tree, non-drivable) condition picker, and the contract/value
    /// range filters have no coverage either. The top-bar global "Search" HUD
    /// button was also probed live -- it neither produced a text field nor
    /// changed navigation state, so it is not a hidden alternative either.
    /// Search-assist therefore locates a uid by SCROLLING it into the
    /// rendered window instead of narrowing the table by typed text --
    /// SI.UI.VirtualisedList exposes a public ScrollTo(int index) that does
    /// exactly this.
    ///
    /// CRITICAL SAFETY NOTE (do not "simplify" this back to one call): an
    /// earlier attempt at this same fix located a uid by scanning the whole
    /// ~10,000-row backing list in ONE main-thread queue callback and it
    /// WEDGED THE GAME (see Shortlist.cs class doc). Whatever the exact
    /// mechanism (Il2Cpp interop indexer cost, or just a many-hundred-ms-to-
    /// multi-second synchronous callback blocking Unity's single-threaded
    /// Update loop for that whole span), a single big scan is not safe here.
    /// This method therefore does NOT scan by itself: it reads only
    /// `[startIndex, startIndex+count)` -- capped to ListRead's own already-
    /// live-proven-safe per-call magnitude (2000 rows; ListRead does a FULL
    /// TreeWalker.Describe decode of up to 2000 rows in one call today with
    /// no reported issue, and this does strictly less work per row: no
    /// Describe call at all, only the cheap DatabaseRecordReference
    /// m_index/.Type fields). Callers cover the full table by chaining
    /// several bounded calls with a real frame gap (await Task.Delay)
    /// between them -- see Shortlist.cs's LocateAndOpenActionsMenuOnce.
    /// </summary>
    public static JsonObject ScanUidChunk(string query, int startIndex, int count)
    {
        try
        {
            var chosen = ResolveSingleListWidget(query);
            if (chosen == null)
                return new JsonObject { ["ok"] = false, ["error"] = "no-list-widget-matches:" + query };

            Il2CppSystem.Collections.IList items;
            try { items = ResolveItems(chosen, out _); } catch { items = null; }
            if (items == null)
                return new JsonObject { ["ok"] = false, ["error"] = "could-not-resolve-backing-list:" + query };

            int start = Math.Max(0, startIndex);
            int cap = Math.Clamp(count, 1, 2000); // matches ListRead's own proven-safe per-call magnitude
            int total = chosen.ItemCount;

            var rows = new JsonArray();
            int scanned = 0;
            for (int i = start; i < start + cap; i++)
            {
                object boxed;
                try { boxed = items[i]; }
                catch { break; } // past the end (or dead list)
                scanned++;
                try
                {
                    var iob = boxed as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
                    var tv = iob?.TryCast<SI.Core.TypedValue>();
                    var dbRef = tv?.Get()?.TryCast<FM.UI.DatabaseRecordReference>();
                    if (dbRef == null) continue;
                    string rt = null;
                    try { rt = dbRef.Type.ToString(); } catch { }
                    rows.Add(new JsonObject { ["index"] = i, ["uid"] = dbRef.m_index, ["refType"] = rt });
                }
                catch { }
            }
            return new JsonObject { ["ok"] = true, ["total"] = total, ["start"] = start, ["scanned"] = scanned, ["rows"] = rows };
        }
        catch (Exception e)
        {
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
    }

    /// <summary>
    /// Bare index scroll: a single, cheap,
    /// bounded call into SI.UI.VirtualisedList's own public ScrollTo(int) --
    /// fire-and-forget on the widget's own animation, never a loop. Callers
    /// poll GetVisibleWindow afterward to know when the scroll has actually
    /// settled (same pattern as any other animated UI transition in this
    /// codebase).
    /// </summary>
    public static JsonObject ScrollToIndex(string query, int index)
    {
        try
        {
            var chosen = ResolveSingleListWidget(query);
            if (chosen == null)
                return new JsonObject { ["ok"] = false, ["error"] = "no-list-widget-matches:" + query };

            var vl = ResolveVirtualisedListNode(chosen);
            if (vl == null)
                return new JsonObject { ["ok"] = false, ["error"] = "could-not-resolve-virtualisedlist-node:" + query };

            try { vl.ScrollTo(index); }
            catch (Exception e)
            {
                return new JsonObject { ["ok"] = false, ["error"] = "scrollto-threw:" + e.Message };
            }
            return new JsonObject { ["ok"] = true, ["index"] = index };
        }
        catch (Exception e)
        {
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
    }

    /// <summary>
    /// Cheap sibling of ListRead for polling after ScrollToIndex: resolves
    /// the same widget and reports ONLY its total ItemCount + current
    /// VisibleView -- no row decode at all -- so a caller can poll on a
    /// tight cadence (checking whether a scroll has settled yet) without
    /// paying ListRead's per-row Describe cost on every tick.
    /// </summary>
    public static JsonObject GetVisibleWindow(string query)
    {
        try
        {
            var chosen = ResolveSingleListWidget(query);
            if (chosen == null)
                return new JsonObject { ["ok"] = false, ["error"] = "no-list-widget-matches:" + query };

            var win = ReadVisibleWindow(chosen.Node);
            return new JsonObject
            {
                ["ok"] = true,
                ["element"] = chosen.Name,
                ["type"] = chosen.TypeName,
                ["count"] = chosen.ItemCount,
                ["visibleWindow"] = win,
            };
        }
        catch (Exception e)
        {
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
    }

    /// <summary>Which slice of rows is currently materialized (diagnostic only
    /// — the read itself covers ALL rows). VirtualisedList.ActiveView public
    /// getter; View is a 2-field struct we only ever READ.</summary>
    private static JsonObject ReadVisibleWindow(UnityEngine.UIElements.VisualElement node)
    {
        try
        {
            SI.UI.VirtualisedList vl;
            var direct = node.TryCast<SI.UI.VirtualisedList>();
            if (direct != null) vl = direct;
            else
            {
                var sl = node.TryCast<SI.Bindable.StreamedListView>();
                if (sl == null) return null;
                const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var pi = sl.GetType().GetProperty("ListElement", Any);
                var elBoxed = pi?.GetValue(sl) as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
                vl = elBoxed?.TryCast<SI.UI.VirtualisedList>();
            }
            if (vl == null) return null;
            var view = vl.VisibleView;
            return new JsonObject { ["start"] = view.Start, ["end"] = view.End, ["count"] = view.Count };
        }
        catch { return null; }
    }

    /// <summary>
    /// Type-based collector mirroring Collect/CollectBySubstring's exact walk
    /// shape (index-loop over childCount/this[i], depth+count bounded) but
    /// matching by TryCast&lt;T&gt;() instead of by node.name substring.
    /// Stops descending once a
    /// StreamedListView or VirtualisedList is matched: a StreamedListView's
    /// internal VirtualisedList (StreamedListView.ListElement) lives inside
    /// its own subtree, so not descending avoids double-reporting the same
    /// widget as two separate rows.
    /// </summary>
    private static void CollectListWidgets(UnityEngine.UIElements.VisualElement node, List<ListWidgetMatch> acc, int depth)
    {
        if (node == null || depth > 64 || acc.Count > 100) return;
        try
        {
            var sl = node.TryCast<SI.Bindable.StreamedListView>();
            if (sl != null)
            {
                int cnt = -1;
                try { cnt = sl.ItemCount; } catch { }
                string nm = null;
                try { nm = node.name; } catch { }
                string tn = "StreamedListView";
                try { tn = sl.GetIl2CppType()?.Name ?? tn; } catch { }
                acc.Add(new ListWidgetMatch { Name = nm, TypeName = tn, ItemCount = cnt, Node = node, IsVirtualisedList = false });
                return;
            }
            var vl = node.TryCast<SI.UI.VirtualisedList>();
            if (vl != null)
            {
                string nm = null;
                try { nm = node.name; } catch { }
                int cnt = -1;
                try { cnt = TryListCount(vl.Items); } catch { }
                acc.Add(new ListWidgetMatch { Name = nm, TypeName = "VirtualisedList", ItemCount = cnt, Node = node, IsVirtualisedList = true });
                return;
            }
        }
        catch { }
        for (int i = 0; i < node.childCount; i++)
        {
            UnityEngine.UIElements.VisualElement child;
            try { child = node[i]; } catch { continue; }
            CollectListWidgets(child, acc, depth + 1);
        }
    }
}
