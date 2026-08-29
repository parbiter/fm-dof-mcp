using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace FMBridge.World;

/// <summary>
/// Scoped, TEXT-matched click helper added for the shortlist verb rewrite.
/// Live investigation proved the raw UiClick(name, index) global name+index
/// contract is fragile for FM's context-menu/dialog UITK templates: several
/// unrelated on-screen elements share the exact same node name (e.g.
/// "button-secondary-label" is used BOTH by CreateShortlistDialog's Cancel
/// button AND an unrelated "Clear" filter button elsewhere on the same
/// screen -- a global name+index click landed on the wrong one live), and
/// the ActionsDropdown/ContentBaseElement context-menu family reuses one
/// generic element name for every row of a growing, order-shifting menu (a
/// player name header, "Add To Shortlist", per-shortlist submenu names,
/// duration options, or a dynamic "Remove From Shortlist (X)" -- all
/// literally named "ContentBaseElement"). Global positional indices proved
/// unsafe for the same reason indexing into interop collections is
/// treated as unsafe everywhere else in this codebase: order/count is not
/// guaranteed stable between
/// the read that picked an index and the click that uses it.
///
/// Fix: click by CONTENT instead of position. One single DFS pass (so the
/// text-read and the click use the identical, not-independently-re-walked,
/// tree state) collects every node with the given exact name, extracts its
/// rendered text (own TextElement text, else a bounded deep-descendant walk
/// -- same ladder UiFind2's deepText option uses), and clicks the first one
/// whose text contains the requested substring (case-insensitive). This
/// gives the structurally-scoped search the live findings called for,
/// without needing to hardcode a catalog of per-dialog container names.
/// </summary>
internal static partial class Navigator
{
    /// <summary>
    /// Finds every VisualElement named exactly <paramref name="elementName"/>
    /// (same exact-match walk as UiClick's Collect), in DFS order, and
    /// clicks the first whose extracted text contains
    /// <paramref name="textNeedle"/> (case-insensitive substring; empty/null
    /// textNeedle matches the first candidate found, same as index 0).
    /// Returns match diagnostics (all extracted texts) on failure so a
    /// caller can see exactly what WAS on screen instead of a bare
    /// "not found".
    /// </summary>
    public static JsonObject ClickByText(string elementName, string textNeedle)
    {
        try
        {
            var pm = Pm();
            var root = pm != null ? pm.RootVisualElement : null;
            if (root == null) return new JsonObject { ["ok"] = false, ["reason"] = "no-root-visualelement" };

            var candidates = new List<UnityEngine.UIElements.VisualElement>();
            Collect(root, elementName, candidates);
            if (candidates.Count == 0)
                return new JsonObject { ["ok"] = false, ["error"] = "element-not-found:" + elementName };

            var needle = (textNeedle ?? "").Trim().ToLowerInvariant();
            var texts = new JsonArray();
            int chosenIndex = -1;
            for (int i = 0; i < candidates.Count; i++)
            {
                string t = null;
                try { t = ExtractOwnText(candidates[i]); } catch { }
                if (string.IsNullOrEmpty(t)) { try { t = CollectDeepText(candidates[i]); } catch { } }
                texts.Add(t ?? "");
                if (chosenIndex < 0 && (needle.Length == 0 || (!string.IsNullOrEmpty(t) && t.ToLowerInvariant().Contains(needle))))
                    chosenIndex = i;
            }
            if (chosenIndex < 0)
                return new JsonObject { ["ok"] = false, ["error"] = "text-not-found:" + textNeedle, ["candidates"] = candidates.Count, ["texts"] = texts };

            var el = candidates[chosenIndex];
            var rect = el.worldBound;
            if (rect.width <= 0 || rect.height <= 0)
                return new JsonObject { ["ok"] = false, ["error"] = "zero-bounds", ["matchedIndex"] = chosenIndex, ["texts"] = texts };
            var pos = rect.center;

            var down = UnityEngine.UIElements.PointerDownEvent.GetPooled();
            SetDown(down, pos);
            root.SendEvent(down);

            var up = UnityEngine.UIElements.PointerUpEvent.GetPooled();
            SetUp(up, pos);
            root.SendEvent(up);

            return new JsonObject
            {
                ["ok"] = true,
                ["clicked"] = elementName,
                ["matchedIndex"] = chosenIndex,
                ["matchedText"] = texts[chosenIndex]?.ToString(),
                ["candidates"] = candidates.Count,
                ["x"] = (int)pos.x,
                ["y"] = (int)pos.y,
            };
        }
        catch (Exception e)
        {
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
    }

    /// <summary>
    /// Clicks at an EXACT screen coordinate rather than resolving a target by
    /// name/index. Added for the shortlist verb's row-checkbox targeting:
    /// live testing found "unity-checkmark" name+index is unsafe even
    /// region-scoped math, because the global element list includes a
    /// "select-all" header checkbox positioned ABOVE the table's own
    /// worldBound (and, live-observed, several more checkmarks belonging to
    /// other off-screen/stacked panels) -- an index computed purely from
    /// Navigator.ListRead's visibleWindow offset landed one row off (target
    /// uid 8293 at window index 4 actually clicked uid 6716's row, because
    /// the header checkbox occupies global index 0). The fix: resolve the
    /// row's checkbox position via a REGION-FILTERED UiFind2 call (scoped to
    /// the table widget's own worldBound, which naturally excludes the
    /// header above it and any unrelated panel below it), then click that
    /// literal (x, y) directly -- no name/index resolution at click time at
    /// all, so there is nothing left to be off-by-one about.
    /// </summary>
    public static JsonObject ClickPoint(int x, int y)
    {
        try
        {
            var pm = Pm();
            var root = pm != null ? pm.RootVisualElement : null;
            if (root == null) return new JsonObject { ["ok"] = false, ["reason"] = "no-root-visualelement" };

            var pos = new UnityEngine.Vector2(x, y);
            var down = UnityEngine.UIElements.PointerDownEvent.GetPooled();
            SetDown(down, pos);
            root.SendEvent(down);

            var up = UnityEngine.UIElements.PointerUpEvent.GetPooled();
            SetUp(up, pos);
            root.SendEvent(up);

            return new JsonObject { ["ok"] = true, ["x"] = x, ["y"] = y };
        }
        catch (Exception e)
        {
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
    }

    /// <summary>Read-only sibling of <see cref="ClickByText"/>: same exact-name
    /// collection + text extraction, no click. Used to poll for a menu/dialog
    /// reaching a stable, expected state before committing a click.</summary>
    public static JsonObject FindByNameTexts(string elementName)
    {
        try
        {
            var pm = Pm();
            var root = pm != null ? pm.RootVisualElement : null;
            if (root == null) return new JsonObject { ["ok"] = false, ["reason"] = "no-root-visualelement" };

            var candidates = new List<UnityEngine.UIElements.VisualElement>();
            Collect(root, elementName, candidates);
            var texts = new JsonArray();
            foreach (var c in candidates)
            {
                string t = null;
                try { t = ExtractOwnText(c); } catch { }
                if (string.IsNullOrEmpty(t)) { try { t = CollectDeepText(c); } catch { } }
                texts.Add(t ?? "");
            }
            return new JsonObject { ["ok"] = true, ["count"] = candidates.Count, ["texts"] = texts };
        }
        catch (Exception e)
        {
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
    }
}
