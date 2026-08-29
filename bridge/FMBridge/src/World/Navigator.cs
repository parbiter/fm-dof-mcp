using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SI.Bindable;
using SI.Core;

namespace FMBridge.World;

/// <summary>
/// Low-level UI Toolkit primitives (element lookup, synthetic pointer
/// clicks) that the release verbs (query_players, shortlist) use internally
/// to drive the game's player-search and shortlist screens. All calls
/// main-thread.
/// </summary>
internal static partial class Navigator
{
    internal static PanelManager Pm()
    {
        try
        {
            var pm = SI.Core.ManualSingleton<PanelManager>.Instance;
            if (pm != null) return pm;
        }
        catch { }
        try { return UnityEngine.Object.FindObjectOfType<PanelManager>(); }
        catch { return null; }
    }

    /// <summary>Synthesizes a pointer click on the named UI Toolkit element.</summary>
    public static JsonObject UiClick(string elementName, int index)
    {
        try
        {
            var pm = Pm();
            var root = pm != null ? pm.RootVisualElement : null;
            if (root == null) return new JsonObject { ["ok"] = false, ["reason"] = "no-root-visualelement" };
            var matches = new List<UnityEngine.UIElements.VisualElement>();
            Collect(root, elementName, matches);
            if (matches.Count == 0) return new JsonObject { ["ok"] = false, ["error"] = "element-not-found:" + elementName };
            if (index < 0 || index >= matches.Count) index = 0;
            var el = matches[index];
            var rect = el.worldBound;
            if (rect.width <= 0 || rect.height <= 0)
                return new JsonObject { ["ok"] = false, ["error"] = "zero-bounds", ["found"] = matches.Count };
            var pos = rect.center;

            var down = UnityEngine.UIElements.PointerDownEvent.GetPooled();
            SetDown(down, pos);
            root.SendEvent(down);

            var up = UnityEngine.UIElements.PointerUpEvent.GetPooled();
            SetUp(up, pos);
            root.SendEvent(up);

            return new JsonObject { ["ok"] = true, ["clicked"] = el.name, ["matches"] = matches.Count,
                                    ["childCount"] = el.childCount, ["x"] = (int)pos.x, ["y"] = (int)pos.y };
        }
        catch (Exception e)
        {
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
    }

    private static void SetDown(UnityEngine.UIElements.PointerDownEvent e, UnityEngine.Vector2 pos)
    {
        e.position = pos;
        e.localPosition = pos;
        e.button = 0;
        e.pressedButtons = 1;
        e.clickCount = 1;
    }

    private static void SetUp(UnityEngine.UIElements.PointerUpEvent e, UnityEngine.Vector2 pos)
    {
        e.position = pos;
        e.localPosition = pos;
        e.button = 0;
        e.pressedButtons = 0;
        e.clickCount = 1;
    }

    private static void Collect(UnityEngine.UIElements.VisualElement node, string name,
        List<UnityEngine.UIElements.VisualElement> acc, int depth = 0)
    {
        if (node == null || depth > 64 || acc.Count > 200) return;
        try { if (node.name == name) acc.Add(node); } catch { }
        for (int i = 0; i < node.childCount; i++)
            Collect(node[i], name, acc, depth + 1);
    }

    /// <summary>
    /// Lists visual elements whose name contains the substring, filtered by a
    /// screen-space region (worldBound intersection); reports USS classes,
    /// tree depth, and pickability per row. A region of (0,0,0,0) disables
    /// the region filter (matches everything).
    /// </summary>
    public static JsonObject UiFind2(string substr, int max, int minX, int minY, int maxX, int maxY, bool deepText = false)
    {
        try
        {
            var pm = Pm();
            var root = pm != null ? pm.RootVisualElement : null;
            if (root == null) return new JsonObject { ["ok"] = false, ["reason"] = "no-root-visualelement" };
            var rows = new JsonArray();
            var n = 0;
            bool useRegion = !(minX == 0 && minY == 0 && maxX == 0 && maxY == 0);
            CollectBySubstring2(root, (substr ?? "").ToLowerInvariant(), rows, ref n,
                Math.Clamp(max <= 0 ? 40 : max, 1, 2000), 0, useRegion, minX, minY, maxX, maxY, deepText);
            return new JsonObject { ["ok"] = true, ["count"] = n, ["rows"] = rows };
        }
        catch (Exception e) { return new JsonObject { ["ok"] = false, ["error"] = e.Message }; }
    }

    private static readonly Regex RichTextTagRe = new Regex(@"</?[a-zA-Z][^<>]{0,150}>", RegexOptions.Compiled);

    /// <summary>Strips TextMeshPro rich-text markup (&lt;link=...&gt;, &lt;color=...&gt;,
    /// &lt;style=...&gt;, &lt;b&gt;, &lt;sprite ...&gt;, &lt;size=...&gt;, etc.) so it doesn't
    /// bloat the string or get sheared mid-tag by the length cap. Only removes well-formed
    /// tags; a bare stray '&lt;' is left alone. Returns input unchanged on failure.</summary>
    private static string StripRichTextTags(string s)
    {
        try { return string.IsNullOrEmpty(s) ? s : RichTextTagRe.Replace(s, ""); }
        catch { return s; }
    }

    /// <summary>Reads the rendered text of a VisualElement if it is a TextElement
    /// (buttons, labels, etc. — this is the localized string UI Toolkit actually
    /// paints, as opposed to the undecoded TranslationID blobs in the binding tree).
    /// Trims, collapses internal newlines, caps length. Returns null when empty/absent.</summary>
    private static string ExtractOwnText(UnityEngine.UIElements.VisualElement node)
    {
        try
        {
            var te = node.TryCast<UnityEngine.UIElements.TextElement>();
            if (te == null) return null;
            var raw = te.text;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var cleaned = StripRichTextTags(raw).Trim().Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", " / ");
            if (cleaned.Length > 240) cleaned = cleaned.Substring(0, 240);
            return string.IsNullOrEmpty(cleaned) ? null : cleaned;
        }
        catch { return null; }
    }

    /// <summary>Walks a matched element's descendants (bounded depth/hit-count) collecting
    /// TextElement texts, for containers (e.g. button remappers) whose own text is empty
    /// but whose child label carries the rendered caption. Joins hits with " | ", capped
    /// at 240 chars total; returns null when nothing found.</summary>
    private static string CollectDeepText(UnityEngine.UIElements.VisualElement node)
    {
        var hits = new List<string>();
        WalkDeepText(node, 0, hits);
        if (hits.Count == 0) return null;
        var joined = StripRichTextTags(string.Join(" | ", hits));
        if (joined.Length > 240) joined = joined.Substring(0, 240);
        return joined;
    }

    private static void WalkDeepText(UnityEngine.UIElements.VisualElement node, int depth, List<string> hits)
    {
        if (node == null || depth > 6 || hits.Count >= 12) return;
        for (int i = 0; i < node.childCount && hits.Count < 12; i++)
        {
            UnityEngine.UIElements.VisualElement child;
            try { child = node[i]; } catch { continue; }
            try
            {
                var t = ExtractOwnText(child);
                if (!string.IsNullOrEmpty(t)) hits.Add(t);
            }
            catch { }
            try { WalkDeepText(child, depth + 1, hits); }
            catch { }
        }
    }

    private static void CollectBySubstring2(UnityEngine.UIElements.VisualElement node, string needle,
        JsonArray rows, ref int n, int max, int depth,
        bool useRegion, int minX, int minY, int maxX, int maxY, bool deepText)
    {
        if (node == null || depth > 64 || n >= max) return;
        try
        {
            var nm = node.name;
            if (!string.IsNullOrEmpty(nm) && nm.ToLowerInvariant().Contains(needle))
            {
                var r = node.worldBound;
                bool inRegion = !useRegion ||
                    (r.x < maxX && r.x + r.width > minX && r.y < maxY && r.y + r.height > minY);
                if (inRegion)
                {
                    string classes = "";
                    try
                    {
                        // Il2CppInterop's generic IEnumerator<T> only exposes Current; MoveNext
                        // lives on the non-generic Il2CppSystem.Collections.IEnumerator, so cast
                        // to that to drive iteration while reading Current off the typed handle.
                        var genEnum = node.GetClasses().GetEnumerator();
                        var driver = genEnum.Cast<Il2CppSystem.Collections.IEnumerator>();
                        var sb = new System.Text.StringBuilder();
                        var cn = 0;
                        while (cn < 8 && driver.MoveNext())
                        {
                            if (cn > 0) sb.Append(' ');
                            sb.Append(genEnum.Current);
                            cn++;
                        }
                        classes = sb.ToString();
                    }
                    catch { classes = ""; }

                    bool pickable;
                    try { pickable = node.pickingMode == UnityEngine.UIElements.PickingMode.Position; }
                    catch { pickable = false; }

                    string text = null;
                    try { text = ExtractOwnText(node); } catch { }
                    if (string.IsNullOrEmpty(text) && deepText)
                    {
                        try { text = CollectDeepText(node); } catch { }
                    }

                    var row = new JsonObject { ["name"] = nm, ["x"] = (int)r.x, ["y"] = (int)r.y,
                                              ["w"] = (int)r.width, ["h"] = (int)r.height,
                                              ["classes"] = classes, ["depth"] = depth, ["pickable"] = pickable };
                    if (!string.IsNullOrEmpty(text)) row["text"] = text;
                    rows.Add(row);
                    n++;
                }
            }
        }
        catch { }
        // Recurse into children even when the parent fails the region test: many parents
        // have degenerate/zero worldBounds while their children sit inside the region.
        for (int i = 0; i < node.childCount; i++)
            CollectBySubstring2(node[i], needle, rows, ref n, max, depth + 1, useRegion, minX, minY, maxX, maxY, deepText);
    }
}
