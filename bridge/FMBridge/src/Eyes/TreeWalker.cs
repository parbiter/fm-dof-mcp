using System;
using System.Collections.Generic;
using BepInEx.Logging;
using SI.Bindable;
using SI.Core;

namespace FMBridge.Eyes;

internal sealed class TreeWalker
{
    private const int MaxDepth = 7;
    private const int MaxNodesPerWalk = 20000;
    private const int MaxLogLinesPerWalk = 250;

    private readonly ManualLogSource _log;
    private readonly HashSet<string> _reported = new HashSet<string>();
    private readonly StatusPoller _poller;
    private int _walks;

    public TreeWalker(ManualLogSource log, StatusPoller poller)
    {
        _log = log;
        _poller = poller;
    }

    public void MaybeWalk(BindingSubsystem bindings, long tick, bool force)
    {
        if (bindings == null) return;
        if (_walks >= 40) return;
        if (!force && tick % 1800 != 0) return;
        _walks++;
        try
        {
            Walk(bindings);
        }
        catch (Exception e)
        {
            _log.LogWarning($"[Eyes] treewalk failed: {e.Message}");
        }
    }

    public IEnumerable<string> KnownLeafNames()
    {
        var set = new HashSet<string>();
        foreach (var entry in _reported)
        {
            var path = entry.Split('=')[0];
            var segs = path.Split('.');
            if (segs.Length > 0 && segs[^1].Length > 0)
                set.Add(segs[^1]);
        }
        return set;
    }

    private void Walk(BindingSubsystem bindings)
    {
        var root = bindings.RootNode;
        if (root == null)
        {
            _log.LogInfo("[Eyes] treewalk: RootNode null");
            return;
        }
        var visited = 0;
        var newPaths = new List<string>();
        WalkNode(root, "", 0, newPaths, ref visited);
        _log.LogInfo($"[Eyes] treewalk #{_walks}: visited={visited} newValuedPaths={newPaths.Count} totalKnown={_reported.Count}");
        var shown = 0;
        foreach (var line in newPaths)
        {
            if (shown++ >= MaxLogLinesPerWalk)
            {
                _log.LogInfo($"[Eyes]   ...{newPaths.Count - shown + 1} more suppressed");
                break;
            }
            _log.LogInfo(line);
        }
    }

    private void WalkNode(IReadOnlyNode node, string prefix, int depth, List<string> outLines, ref int visited)
    {
        if (node == null || depth > MaxDepth || visited >= MaxNodesPerWalk) return;
        visited++;

        string path = prefix.Length == 0 ? SafeName(node) : prefix + "." + SafeName(node);

        TypedValue v = null;
        try { v = node.Value; } catch { }

        if (v != null)
        {
            var decoded = Decode(v);
            var key = path + "=" + decoded;
            if (_reported.Add(key))
            {
                var marker = "";
                if (IsInterestingName(path) || decoded.StartsWith("GameDate"))
                {
                    marker = "  <<< INTERESTING";
                    _poller.AddRealNode(path, node);
                }
                outLines.Add($"[Eyes] val '{path}' = {decoded}{marker}");
            }
        }

        if (node.FirstChild == null) return;
        foreach (var child in node.Children)
            WalkNode(child, path, depth + 1, outLines, ref visited);
    }

    internal static IReadOnlyNode FindNode(BindingSubsystem bindings, string path)
    {
        var segments = path.Trim('.').Split('.');
        if (segments.Length == 0 || string.IsNullOrEmpty(segments[0])) return null;
        var root = bindings.RootNode;
        if (root == null) return null;
        foreach (var child in Enumerate(root))
        {
            if (SafeName(child) != segments[0]) continue;
            IReadOnlyNode current = child;
            for (var i = 1; i < segments.Length && current != null; i++)
            {
                IReadOnlyNode next = null;
                foreach (var grand in Enumerate(current))
                {
                    if (SafeName(grand) == segments[i]) { next = grand; break; }
                }
                current = next;
            }
            if (current != null) return current;
        }
        return null;
    }

    internal static TypedValue FindValueTyped(BindingSubsystem bindings, string path)
    {
        try
        {
            var n = FindNode(bindings, path);
            if (n == null) return null;
            return n.Value;
        }
        catch { return null; }
    }

    internal static string FindValueText(BindingSubsystem bindings, string path)
    {
        var segments = path.Trim('.').Split('.');
        if (segments.Length == 0 || string.IsNullOrEmpty(segments[0])) return null;
        var root = bindings.RootNode;
        if (root == null) return null;
        IReadOnlyNode current = null;
        foreach (var child in Enumerate(root))
        {
            if (SafeName(child) != segments[0]) continue;
            current = child;
            for (var i = 1; i < segments.Length && current != null; i++)
            {
                IReadOnlyNode next = null;
                foreach (var grand in Enumerate(current))
                {
                    if (SafeName(grand) == segments[i]) { next = grand; break; }
                }
                current = next;
            }
            break;
        }
        if (current == null) return null;
        TypedValue v = null;
        try { v = current.Value; } catch { }
        return v == null ? "" : Describe(v);
    }

    private static IEnumerable<IReadOnlyNode> Enumerate(IReadOnlyNode node)
    {
        if (node.FirstChild == null) yield break;
        foreach (var child in node.Children)
            yield return child;
    }

    internal static string Describe(TypedValue value)
    {
        var type = "?";
        try { type = value.DataType?.Name ?? "?"; } catch { }
        // Live translation beats any offline catalog: the native translator
        // also composes entity names (person first+last etc.) embedded via
        // ICppTranslatable before the string reaches managed code.
        // Main-thread only, like all verbs.
        if (type == "TranslationID")
        {
            try
            {
                var id = value.Get<SI.Translation.TranslationID>();
                var live = SI.Bindable.BindableWidgetUtils.Translate(id);
                if (!string.IsNullOrEmpty(live)) return $"{type}:{Trunc(live)}";
            }
            catch { }
        }
        // AUTHORITATIVE DECODE: GameDate is a single packed uint
        // whose bit layout is native-only —
        // there is no managed field to hand-decode. Before this fix, falling
        // through to AsString() just printed the raw struct
        // ("GameDate { Data1 = N, Type = FmdateSiDate }"), and a *manual*
        // reverse-engineered decode of Data1 (treating it as a linear count
        // of 15-minute quarters)
        // disagreed with the FullContract.ContractLength/
        // ContractDaysElapsed day-count cross-check by ~5 years. The actual
        // native decoder is `FM.GamePlugin.GameDate.ToDateTime(GameDate)` —
        // already proven correct in EyesModule.BuildSnapshot, where its output
        // is displayed as game_status's date_iso and matches the real game
        // clock exactly every tick. Route every GameDate through that same
        // native call instead of hand-decoding Data1, so any bound GameDate
        // field (EndDate, etc.) decodes consistently with game_status.
        if (type == "GameDate")
        {
            try
            {
                var gd = FM.UI.VisualFunctions.GameDateFunctions.GetGameDate(value);
                var dt = FM.GamePlugin.GameDate.ToDateTime(gd);
                // NOTE: an interpolated-string format specifier ($"{dt:...}")
                // was tried first and silently produced the default
                // M/d/yyyy h:mm:ss tt culture format instead of the requested
                // pattern (live-observed, IL2CPP interop quirk) — use the
                // explicit .ToString("...") call style EyesModule already
                // uses successfully for the same DateTime type.
                return $"{type}:" + dt.ToString("yyyy-MM-dd HH:mm");
            }
            catch { /* fall through to raw struct dump below if native call fails */ }
        }
        try
        {
            var s = value.AsString();
            // Channel-delivered strings arrive as FM's raw translation markup
            // (a -prefixed wire format with embedded serialized args —
            // e.g. a person's Name). TranslationManager.Format parses the
            // markup and composes the display text via the native translator
            // (string in / Translation class out — no structs, interop-safe).
            // Main-thread only, like all verbs.
            if (!string.IsNullOrEmpty(s) && s.IndexOf('\u0001') >= 0)
            {
                // First: FM.UI's own embedded-data-aware substring (global
                // namespace TextFunctions, FM.UI.dll). StringLength counts
                // visible chars; SubString(removeEmbeddedData:true) strips
                // the \u0001-framed serialized args, leaving display text.
                try
                {
                    var len = TextFunctions.StringLength(s);
                    if (len > 0)
                    {
                        bool did = false;
                        var plain = TextFunctions.SubString(s, 0, len, true, out did);
                        if (!string.IsNullOrEmpty(plain) && plain.IndexOf('\u0001') < 0)
                            return $"{type}:{Trunc(plain)}";
                    }
                }
                catch { }
                try
                {
                    var t = SI.Translation.TranslationManager.Format(s);
                    if (t != null)
                    {
                        string composed = t; // implicit Translation -> string runs the composer
                        if (!string.IsNullOrEmpty(composed)) return $"{type}:{Trunc(composed)}";
                    }
                }
                catch { }
            }
            var dec = Translate.TryDecode(s);
            return $"{type}:{Trunc(dec ?? s)}";
        }
        catch
        {
            return $"{type}:<unprintable>";
        }
    }

    private static bool IsInterestingName(string path)
    {
        return path.EndsWith(".GameProcessing") || path.EndsWith(".CurrentDate") ||
               path.EndsWith(".InGameDate") || path.EndsWith(".GameWaiting") ||
               path.EndsWith(".HumanStatusAction") || path.EndsWith(".IsOnHoliday") ||
               path.EndsWith(".continue.State") || path.EndsWith(".ContinueButtonMainString");
    }

    private static string SafeName(IReadOnlyNode n)
    {
        try { return n.Name ?? "?"; } catch { return "?"; }
    }

    private static string Decode(TypedValue value)
    {
        var type = "?";
        try { type = value.DataType?.Name ?? "?"; } catch { }
        try
        {
            var s = value.AsString();
            return $"{type}:{Trunc(s)}";
        }
        catch
        {
            return $"{type}:<unprintable>";
        }
    }

    private static string Trunc(string s)
    {
        if (s == null) return "null";
        return s.Length <= 80 ? s : s.Substring(0, 77) + "...";
    }
}
