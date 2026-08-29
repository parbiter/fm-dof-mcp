using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SI.Bindable;
using SI.Core;

namespace FMBridge.World;

/// <summary>
/// Inbox domain verbs: inbox_list / inbox_read / inbox_respond. Dumb
/// packaging of live-proven mechanisms — no triage logic, just faithful
/// list/read/fire.
///
/// ID STRATEGY (live-verified): every list row exposes
/// `.BindingRemapper1.NewsItem.UniqueId` (DynamicNumber). Values stayed
/// identical across panel close/reopen cycles and the selected-message
/// remap (`_remap2.HumanPortalLastSelectedMessage`) carried the same number
/// as the clicked row — these are the engine's own news-item unique ids
/// (NewsItemReference.m_uniqueID). No composite fallback needed.
///
/// LIST SOURCE: there is no headless news collection in the bindings (checked
/// live: only `Human.NumberUnreadMessages` exists outside the portal tool),
/// so the list is read from the binding tree under the open portal messages
/// tool: ...MessagesBlock*.internationalRemapper*.ShowInternationalComp*
/// .BindingSetter*.MessagesStreamedObjectListA.items0.{slot}
/// .BindingRemapper1.NewsItem.* with fields UniqueId / NewsTitleString /
/// NewsFirstBubbleSpeechString (preview) / NewsItemTimeSent / Date (GameDate,
/// day separators only) / CanRespond / NewsHasResponded / NewsItemHasBeenRead
/// / NewsItemIsPriority, plus sibling .Person.Name (sender). If the list is
/// not materialized the verb opens PortalMessagesTool via Navigator.NavOpenEx
/// and polls across frames (the queue drains per tick; the main thread is
/// never blocked), matching the game's own routing when NeedAction fires.
///
/// READ: selects the message by synthesizing a pointer click on its
/// NewsItemButton row (visual order == binding order, title-cross-checked),
/// waits for the reader pane to render, then harvests rendered body text +
/// MessagesActionButtonRemapper labels in one further main-thread slice
/// (deepText ladder — TMP rich-text stripped, the same recipe
/// Navigator.UiFind2's deepText option uses).
///
/// RESPOND: after selecting the message, resolve `choice` (number = action
/// index; a negative index counts from the end, so -1 is the last button;
/// string = case-insensitive label match against the button's rendered
/// text) and fire the same synthesized pointer click on
/// MessagesActionButtonRemapper that was live-proven to flip
/// ContinueFlowState. Effect is asynchronous. Gated behind
/// Voice.AllowCommands since responses are irreversible.
///
/// WIRE NOTES: the WS envelope owns top-level "id" for correlation, so the
/// message id is read from "message_id" (preferred) or alias "mid" — strings
/// or numbers both accepted; "choice" may be a string label or numeric index.
/// Arg parsing lives here so VoiceServer only needs three thin case lines.
/// </summary>
internal static class Inbox
{
    private const int WalkMaxDepth = 32;
    private const int WalkMaxNodes = 200000;
    private const int MaxRows = 500;
    private const int VisualMaxDepth = 64;
    private const int VisualMaxElements = 400;
    private const string PanelName = "PortalMessagesTool";
    private const string ActionButtonName = "MessagesActionButtonRemapper";
    private const string RowButtonName = "NewsItemButton";
    private const string ReaderContainerName = "MessagesDynamicRemapper";
    private const int OpenPollMs = 400;
    private const int OpenPollAttempts = 20;
    private const int RenderSettleMs = 900;
    private const int CmdTimeoutMs = 30000;

    // ------------------------------------------------------------------ //
    // Public entry points — one per WS case line, mirroring TypeText      //
    // ------------------------------------------------------------------ //

    public static Task<JsonObject> ListAsync(FMBridge.Voice.MainThreadQueue queue)
        => RunSafeAsync(async () =>
        {
            var scan = await CmdValAsync(queue, ScanInbox);
            if (!scan.Materialized)
            {
                var opened = await OpenAndWaitAsync(queue, () => CmdValAsync(queue, ScanInbox));
                if (opened == null) return NotMaterialized();
                scan = opened;
            }
            return ListReply(scan);
        });

    public static Task<JsonObject> ReadAsync(FMBridge.Voice.MainThreadQueue queue, string requestJson)
        => RunSafeAsync(async () =>
        {
            var (mid, _) = ParseArgs(requestJson);
            var located = await LocateAsync(queue, mid);
            if (located.reply != null) return located.reply;

            var clicked = await CmdAsync(queue, b => ClickRow(b, located.row));
            if (!clicked.HasOk()) return clicked;

            await Task.Delay(RenderSettleMs); // reader pane renders across frames

            return await CmdAsync(queue, b => FinalizeRead(b, located.row));
        });

    public static Task<JsonObject> RespondAsync(FMBridge.Voice.MainThreadQueue queue, string requestJson)
        => RunSafeAsync(async () =>
        {
            var (mid, choice) = ParseArgs(requestJson);
            if (string.IsNullOrEmpty(choice))
                return new JsonObject { ["ok"] = false, ["error"] = "inbox_respond requires 'choice' (label or index)" };
            var located = await LocateAsync(queue, mid);
            if (located.reply != null) return located.reply;

            var clicked = await CmdAsync(queue, b => ClickRow(b, located.row));
            if (!clicked.HasOk()) return clicked;

            await Task.Delay(RenderSettleMs);

            return await CmdAsync(queue, b => FinalizeRespond(b, located.row, choice));
        });

    // ------------------------------------------------------------------ //
    // Shared plumbing                                                     //
    // ------------------------------------------------------------------ //

    private static bool HasOk(this JsonObject o)
        => o?["ok"] is JsonValue bv && bv.TryGetValue<bool>(out var b) && b;

    private static async Task<JsonObject> RunSafeAsync(Func<Task<JsonObject>> flow)
    {
        try { return await flow(); }
        catch (Exception e) { return new JsonObject { ["ok"] = false, ["error"] = e.Message }; }
    }

    /// <summary>Main-thread slice returning a typed result (null bindings ->
    /// standard bindings-not-captured reply, TapCmdOffThread discipline).</summary>
    private static async Task<JsonObject> CmdAsync(
        FMBridge.Voice.MainThreadQueue queue, Func<BindingSubsystem, JsonObject> cmd)
    {
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Enqueue(() =>
        {
            try
            {
                var bindings = FMBridge.Eyes.SpikeHooks.CapturedBindings;
                tcs.SetResult(bindings == null
                    ? new JsonObject { ["ok"] = false, ["reason"] = "bindings-not-captured" }
                    : cmd(bindings));
            }
            catch (Exception e)
            {
                try { tcs.SetResult(new JsonObject { ["ok"] = false, ["error"] = e.Message }); } catch { }
            }
        });
        var done = await Task.WhenAny(tcs.Task, Task.Delay(CmdTimeoutMs));
        if (done != tcs.Task) return new JsonObject { ["ok"] = false, ["error"] = "inbox timeout" };
        return tcs.Task.Result;
    }

    private static async Task<InboxScan> CmdValAsync<T>(
        FMBridge.Voice.MainThreadQueue queue, Func<BindingSubsystem, T> cmd)
    {
        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Enqueue(() =>
        {
            try
            {
                var bindings = FMBridge.Eyes.SpikeHooks.CapturedBindings;
                tcs.SetResult(bindings == null
                    ? (object)new JsonObject { ["ok"] = false, ["reason"] = "bindings-not-captured" }
                    : cmd(bindings));
            }
            catch (Exception e)
            {
                try { tcs.SetResult(new JsonObject { ["ok"] = false, ["error"] = e.Message }); } catch { }
            }
        });
        var done = await Task.WhenAny(tcs.Task, Task.Delay(CmdTimeoutMs));
        if (done != tcs.Task)
            return InboxScan.Failed(new JsonObject { ["ok"] = false, ["error"] = "inbox timeout" });
        return tcs.Task.Result is JsonObject err ? InboxScan.Failed(err) : (InboxScan)tcs.Task.Result;
    }

    /// <summary>Request-arg extraction (see type doc for the wire shape).</summary>
    private static (string mid, string choice) ParseArgs(string requestJson)
    {
        string mid = null, choice = null;
        try
        {
            var root = JsonNode.Parse(requestJson ?? "");
            foreach (var key in new[] { "message_id", "mid" })
            {
                var n = root?[key];
                if (n == null) continue;
                mid = n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n.ToJsonString();
                if (!string.IsNullOrEmpty(mid)) break;
            }
            var c = root?["choice"];
            if (c != null)
                choice = c is JsonValue cv && cv.TryGetValue<string>(out var cs) ? cs : c.ToJsonString();
        }
        catch { }
        return (mid, choice);
    }

    /// <summary>Finds the message row by id, auto-opening the inbox when the
    /// list is not materialized. reply != null means "done, send this".</summary>
    private static async Task<(InboxScan scan, MessageRow row, JsonObject reply)> LocateAsync(
        FMBridge.Voice.MainThreadQueue queue, string mid)
    {
        if (!long.TryParse(mid, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            return (null, null, new JsonObject { ["ok"] = false, ["error"] = "bad-message-id:" + (mid ?? "<null>") });

        var scan = await CmdValAsync(queue, b => FindRow(b, id));
        if (scan.Error != null) return (scan, null, scan.Error);
        if (scan.Row != null) return (scan, scan.Row, null);

        if (!scan.Materialized)
        {
            var reopened = await OpenAndWaitAsync(queue, () => CmdValAsync(queue, b => FindRow(b, id)));
            if (reopened == null) return (scan, null, NotMaterialized());
            scan = reopened;
            if (scan.Row != null) return (scan, scan.Row, null);
        }

        var known = new JsonArray();
        foreach (var r in scan.Rows) known.Add(r.Id);
        return (scan, null, new JsonObject
        {
            ["ok"] = false,
            ["error"] = "message-not-found:" + id,
            ["known_ids"] = known,
        });
    }

    /// <summary>Kicks NavOpen(PortalMessagesTool) then polls across frames.
    /// Returns the first materialized scan, or null on failure.</summary>
    private static async Task<InboxScan> OpenAndWaitAsync(
        FMBridge.Voice.MainThreadQueue queue, Func<Task<InboxScan>> probe)
    {
        var kick = await CmdAsync(queue, _ => Navigator.NavOpenEx(PanelName, null, null));
        if (!kick.HasOk()) return null; // includes load-in-progress from an overlapping nav
        for (int i = 0; i < OpenPollAttempts; i++)
        {
            await Task.Delay(OpenPollMs);
            var scan = await probe();
            if (scan.Materialized) return scan;
        }
        return null;
    }

    private static JsonObject NotMaterialized()
        => new JsonObject
        {
            ["ok"] = false,
            ["reason"] = "inbox-not-materialized",
            ["hint"] = "could not open " + PanelName + "; try nav_open " + PanelName + " manually",
        };

    // ------------------------------------------------------------------ //
    // Binding-tree harvest                                                //
    // ------------------------------------------------------------------ //

    private sealed class MessageRow
    {
        public string Slot;
        public long Id;
        public string Title, Preview, TimeSent, Sender;
        public long? DateRaw;
        public bool? CanRespond, HasResponded, HasBeenRead, IsPriority;
    }

    private sealed class InboxScan
    {
        public readonly List<MessageRow> Rows = new List<MessageRow>();
        public long? SelectedId;
        public string ScopePath;
        public MessageRow Row;   // set by FindRow
        public JsonObject Error; // set when the scan itself failed
        public bool Materialized => Error == null && Rows.Count > 0;

        public static InboxScan Failed(JsonObject error) => new InboxScan { Error = error };
    }

    private static InboxScan ScanInbox(BindingSubsystem bindings)
    {
        var h = new InboxScan();
        WalkNodes = 0; // single main-thread scan at a time (queue discipline)
        try
        {
            var root = bindings.RootNode;
            if (root != null) WalkBindings(root, "", 0, h);
        }
        catch (Exception e)
        {
            return InboxScan.Failed(new JsonObject { ["ok"] = false, ["error"] = e.Message });
        }
        return h;
    }

    private static InboxScan FindRow(BindingSubsystem bindings, long id)
    {
        var h = ScanInbox(bindings);
        if (h.Error != null) return h;
        foreach (var r in h.Rows)
            if (r.Id == id) { h.Row = r; break; }
        return h;
    }

    private static JsonObject ListReply(InboxScan scan)
    {
        // Binding siblings enumerate in creation (hash) order, not display
        // order — sort by slot index so messages[] matches what the human
        // sees top-to-bottom (live: slots 0..E == y-order).
        scan.Rows.Sort((a, b) =>
        {
            var ka = TrySlotIndex(a.Slot, out var ia) ? ia : int.MaxValue;
            var kb = TrySlotIndex(b.Slot, out var ib) ? ib : int.MaxValue;
            return ka.CompareTo(kb);
        });
        var msgs = new JsonArray();
        int cap = Math.Min(scan.Rows.Count, MaxRows);
        for (int i = 0; i < cap; i++)
        {
            var r = scan.Rows[i];
            var m = new JsonObject
            {
                ["id"] = r.Id,
                ["title"] = r.Title,
            };
            if (r.TimeSent != null) m["date"] = r.TimeSent;
            if (r.DateRaw.HasValue) m["date_raw"] = r.DateRaw.Value;
            if (r.Sender != null) m["sender"] = r.Sender;
            if (r.Preview != null) m["preview"] = r.Preview;
            if (r.CanRespond.HasValue) m["can_respond"] = r.CanRespond.Value;
            if (r.HasBeenRead.HasValue) m["read"] = !r.HasBeenRead.Value;
            if (r.HasResponded.HasValue) m["responded"] = r.HasResponded.Value;
            m["slot"] = r.Slot;
            msgs.Add(m);
        }
        return new JsonObject
        {
            ["ok"] = true,
            ["count"] = scan.Rows.Count,
            ["messages"] = msgs,
            ["source"] = "bindings:MessagesStreamedObjectList",
            ["scope"] = scan.ScopePath,
            ["note"] = "ids = engine news-item unique ids (NewsItemReference.m_uniqueID); "
                     + "stable across panel close/reopen. Per-message actions are only "
                     + "enumerable once selected — use inbox_read.",
        };
    }

    private static void WalkBindings(IReadOnlyNode node, string path, int depth, InboxScan h)
    {
        if (node == null || depth > WalkMaxDepth || h.Rows.Count > MaxRows || WalkNodes >= WalkMaxNodes) return;
        WalkNodes++;
        path = path.Length == 0 ? SafeName(node) : path + "." + SafeName(node);

        var name = SafeName(node);
        TypedValue v = null;
        try { v = node.Value; } catch { }

        if (v != null
            && path.Contains("MessagesStreamedObjectList", StringComparison.Ordinal)
            && path.Contains(".BindingRemapper1.", StringComparison.Ordinal))
        {
            var pfx = PrefixBefore(path, ".BindingRemapper1.");
            if (pfx != null)
            {
                switch (name)
                {
                    case "UniqueId":
                        // Sibling subtrees (Human.Team / Person.Team) carry other
                        // UniqueIds (team uid 1099) that can enumerate after the
                        // news item's own — accept only the engine news-item id.
                        if (!path.EndsWith(".NewsItem.UniqueId", StringComparison.Ordinal)) break;
                        var id = NumAfter(v);
                        if (id.HasValue)
                        {
                            var row = RowFor(h, pfx);
                            row.Id = id.Value;
                        }
                        break;
                    case "NewsTitleString" when path.Contains(".NewsItem.", StringComparison.Ordinal):
                        RowFor(h, pfx).Title = TextAfter(v); break;
                    case "NewsFirstBubbleSpeechString" when path.Contains(".NewsItem.", StringComparison.Ordinal):
                        RowFor(h, pfx).Preview = TextAfter(v); break;
                    case "NewsItemTimeSent" when path.Contains(".NewsItem.", StringComparison.Ordinal):
                        RowFor(h, pfx).TimeSent = TextAfter(v); break;
                    case "CanRespond" when path.Contains(".NewsItem.", StringComparison.Ordinal):
                        RowFor(h, pfx).CanRespond = BoolAfter(v); break;
                    case "NewsHasResponded" when path.Contains(".NewsItem.", StringComparison.Ordinal):
                        RowFor(h, pfx).HasResponded = BoolAfter(v); break;
                    case "NewsItemHasBeenRead" when path.Contains(".NewsItem.", StringComparison.Ordinal):
                        RowFor(h, pfx).HasBeenRead = BoolAfter(v); break;
                    case "NewsItemIsPriority" when path.Contains(".NewsItem.", StringComparison.Ordinal):
                        RowFor(h, pfx).IsPriority = BoolAfter(v); break;
                    case "Date" when path.EndsWith(".NewsItem.Date", StringComparison.Ordinal):
                        RowFor(h, pfx).DateRaw = GameDateRaw(v); break;
                    case "HumanPortalLastSelectedMessage":
                        h.SelectedId ??= NumAfter(v);
                        break;
                    default:
                        if ((name == "Name" || name == "Initials") && path.Contains(".Person.", StringComparison.Ordinal))
                        {
                            var spfx = PrefixBefore(path, ".Person.");
                            if (spfx != null && name == "Name") RowFor(h, spfx).Sender ??= TextAfter(v);
                        }
                        break;
                }
            }
        }
        if (h.ScopePath == null && path.Contains("MessagesStreamedObjectList", StringComparison.Ordinal))
            h.ScopePath = path;

        if (node.FirstChild == null) return;
        foreach (var child in node.Children)
            WalkBindings(child, path, depth + 1, h);
    }

    private static int WalkNodes;

    private static MessageRow RowFor(InboxScan h, string itemPrefix)
    {
        var slot = SlotOf(itemPrefix);
        foreach (var r in h.Rows)
            if (r.Slot == slot) return r;
        var row = new MessageRow { Slot = slot };
        h.Rows.Add(row);
        return row;
    }

    /// <summary>"...items0.7" -> "7" (the streamed-list slot segment).</summary>
    private static string SlotOf(string itemPrefix)
    {
        var idx = itemPrefix.LastIndexOf('.');
        return idx < 0 ? itemPrefix : itemPrefix.Substring(idx + 1);
    }

    /// <summary>Everything before the last occurrence of marker (null if absent).</summary>
    private static string PrefixBefore(string path, string marker)
    {
        var idx = path.LastIndexOf(marker, StringComparison.Ordinal);
        return idx < 0 ? null : path.Substring(0, idx);
    }

    private static string SafeName(IReadOnlyNode n)
    {
        try { return n.Name ?? "?"; } catch { return "?"; }
    }

    // -- TypedValue decoders: route through TreeWalker.Describe's proven
    //    translation/markup ladder, then strip its "TypeName:" prefix. ------

    private static string Decoded(TypedValue v)
    {
        try
        {
            var s = FMBridge.Eyes.TreeWalker.Describe(v);
            var colon = s.IndexOf(':');
            return colon < 0 ? s : s.Substring(colon + 1);
        }
        catch { return null; }
    }

    private static string TextAfter(TypedValue v)
    {
        var s = Decoded(v);
        if (string.IsNullOrEmpty(s) || s == "<unprintable>") return null;
        return s.Length > 480 ? s.Substring(0, 480) : s;
    }

    private static long? NumAfter(TypedValue v)
    {
        var s = Decoded(v);
        if (string.IsNullOrEmpty(s)) return null;
        var token = s;
        var eq = token.LastIndexOf("= ", StringComparison.Ordinal);
        if (eq >= 0) token = token.Substring(eq + 2).TrimEnd(' ', '}');
        if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
        var colon = token.IndexOf(':');
        if (colon >= 0 && long.TryParse(token.Substring(colon + 1).Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var l2)) return l2;
        return null;
    }

    private static bool? BoolAfter(TypedValue v)
    {
        var s = Decoded(v);
        if (string.IsNullOrEmpty(s)) return null;
        if (s.StartsWith("True", StringComparison.Ordinal)) return true;
        if (s.StartsWith("False", StringComparison.Ordinal)) return false;
        return null;
    }

    /// <summary>Pulls Data1 out of Describe's "GameDate { Data1 = N, ... }".</summary>
    private static long? GameDateRaw(TypedValue v)
    {
        var s = Decoded(v);
        if (string.IsNullOrEmpty(s)) return null;
        var key = s.IndexOf("Data1 = ", StringComparison.Ordinal);
        if (key < 0) return null;
        var start = key + "Data1 = ".Length;
        var end = start;
        while (end < s.Length && char.IsDigit(s[end])) end++;
        return long.TryParse(s.Substring(start, end - start), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var l) ? l : null;
    }

    // ------------------------------------------------------------------ //
    // Visual-tree side (row selection, action buttons, rendered body)     //
    // ------------------------------------------------------------------ //

    private static JsonObject ClickRow(BindingSubsystem bindings, MessageRow row)
    {
        var root = RootVisualElement();
        if (root == null) return new JsonObject { ["ok"] = false, ["reason"] = "no-root-visualelement" };
        var buttons = CollectByName(root, RowButtonName);
        if (buttons.Count == 0)
            return new JsonObject { ["ok"] = false, ["error"] = "no-" + RowButtonName + "-rendered" };

        // Binding order == visual order (live-verified); title cross-checked.
        int index = IndexOfRow(row, buttons);
        if (!FireClick(buttons[index]))
            return new JsonObject { ["ok"] = false, ["error"] = "zero-bounds", ["index"] = index };
        return new JsonObject { ["ok"] = true, ["clicked_row"] = index, ["matches"] = buttons.Count };
    }

    /// <summary>Prefers the button whose rendered headline equals the binding
    /// title; falls back to positional order (slot index) otherwise.</summary>
    private static int IndexOfRow(MessageRow row, List<UnityEngine.UIElements.VisualElement> buttons)
    {
        if (!string.IsNullOrEmpty(row.Title))
        {
            for (int i = 0; i < buttons.Count; i++)
            {
                foreach (var t in DeepTexts(buttons[i], 12, 300))
                    if (t.Equals(row.Title, StringComparison.OrdinalIgnoreCase))
                        return i;
            }
        }
        return TrySlotIndex(row.Slot, out var pos) && pos < buttons.Count ? pos : 0;
    }

    /// <summary>Slots are single hex chars in creation order (0..9,A..E live).</summary>
    private static bool TrySlotIndex(string slot, out int pos)
    {
        pos = 0;
        if (string.IsNullOrEmpty(slot)) return false;
        if (int.TryParse(slot, NumberStyles.Integer, CultureInfo.InvariantCulture, out pos)) return true;
        foreach (var c in slot)
        {
            if (c >= '0' && c <= '9') { pos = pos * 16 + (c - '0'); continue; }
            if (c >= 'A' && c <= 'F') { pos = pos * 16 + (c - 'A' + 10); continue; }
            return false;
        }
        return true;
    }

    private sealed class ActionButton
    {
        public UnityEngine.UIElements.VisualElement Element;
        public string Label;
    }

    /// <summary>Visible MessagesActionButtonRemapper elements under the portal
    /// messages tool, sorted top-to-bottom/left-to-right (index order == visual
    /// order — the m6 convention).</summary>
    private static List<ActionButton> CollectActionButtons()
    {
        var result = new List<(ActionButton btn, int x, int y)>();
        var root = RootVisualElement();
        if (root == null) return new List<ActionButton>();
        var scopes = CollectByName(root, PanelName);
        if (scopes.Count == 0) scopes.Add(root);
        foreach (var tool in scopes)
            foreach (var el in CollectByName(tool, ActionButtonName))
            {
                var r = el.worldBound;
                if (r.width <= 0 || r.height <= 0) continue;
                var texts = DeepTexts(el, 6, 240);
                result.Add((new ActionButton
                {
                    Element = el,
                    Label = texts.Count > 0 ? string.Join(" | ", texts) : null,
                }, (int)r.x, (int)r.y));
            }
        result.Sort((a, b) => a.y != b.y ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x));
        var ordered = new List<ActionButton>();
        foreach (var (btn, _, _) in result) ordered.Add(btn);
        return ordered;
    }

    private static JsonArray LabelsOf(List<ActionButton> buttons)
    {
        var arr = new JsonArray();
        for (int i = 0; i < buttons.Count; i++)
            arr.Add(new JsonObject { ["index"] = i, ["label"] = buttons[i].Label });
        return arr;
    }

    private static JsonObject FinalizeRead(BindingSubsystem bindings, MessageRow row)
    {
        var scan = ScanInbox(bindings);
        if (scan.Error != null) return scan.Error;
        var mismatch = CheckSelection(scan, row);
        if (mismatch != null) return mismatch;

        var buttons = CollectActionButtons();
        var actions = new JsonArray();
        var labelSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < buttons.Count; i++)
        {
            actions.Add(new JsonObject { ["label"] = buttons[i].Label, ["index"] = i });
            if (!string.IsNullOrEmpty(buttons[i].Label)) labelSet.Add(buttons[i].Label);
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["message"] = new JsonObject
            {
                ["id"] = row.Id,
                ["title"] = row.Title,
                ["body_text"] = ReaderBody(labelSet),
                ["actions"] = actions,
                ["meta"] = MetaOf(scan, row),
            },
        };
    }

    private static JsonObject FinalizeRespond(BindingSubsystem bindings, MessageRow row, string choice)
    {
        var scan = ScanInbox(bindings);
        if (scan.Error != null) return scan.Error;
        var mismatch = CheckSelection(scan, row);
        if (mismatch != null) return mismatch;

        var buttons = CollectActionButtons();
        if (buttons.Count == 0)
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = row.CanRespond == false
                    ? "message-not-respondable"
                    : "no-action-buttons-rendered",
                ["can_respond"] = row.CanRespond,
                ["hint"] = "re-run inbox_read to inspect the rendered action row",
            };

        int index;
        string how;
        if (int.TryParse(choice, NumberStyles.Integer, CultureInfo.InvariantCulture, out var num))
        {
            if (num < 0) num = buttons.Count + num; // m6: "click LAST" == count-1
            if (num < 0 || num >= buttons.Count)
                return new JsonObject
                {
                    ["ok"] = false,
                    ["error"] = $"choice-index-out-of-range:{num}",
                    ["actions"] = LabelsOf(buttons),
                };
            index = num;
            how = "index";
        }
        else
        {
            index = MatchLabel(buttons, choice);
            if (index < 0)
                return new JsonObject
                {
                    ["ok"] = false,
                    ["error"] = "no-action-label-matches:" + choice,
                    ["actions"] = LabelsOf(buttons),
                };
            how = "label";
        }

        var btn = buttons[index];
        var fired = FireClick(btn.Element);
        var reply = new JsonObject
        {
            ["ok"] = fired,
            ["fired"] = $"ui-click {ActionButtonName}[{index}] '{btn.Label}' ({how})",
            ["message_id"] = row.Id,
            ["choice"] = choice,
            ["note"] = "effect is asynchronous; click delivered via the same "
                     + "synthesized-pointer recipe the bridge's other UI verbs use.",
        };
        if (!fired) reply["error"] = "zero-bounds";
        return reply;
    }

    private static JsonObject CheckSelection(InboxScan scan, MessageRow row)
    {
        if (scan.SelectedId.HasValue && scan.SelectedId.Value != row.Id)
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = "select-mismatch",
                ["wanted"] = row.Id,
                ["selected"] = scan.SelectedId.Value,
            };
        return null;
    }

    private static JsonObject MetaOf(InboxScan scan, MessageRow row)
    {
        var meta = new JsonObject
        {
            ["slot"] = row.Slot,
            ["sender"] = row.Sender,
            ["time_sent"] = row.TimeSent,
            ["can_respond"] = row.CanRespond,
            ["has_responded"] = row.HasResponded,
            ["unread"] = row.HasBeenRead.HasValue ? !row.HasBeenRead.Value : null,
            ["is_priority"] = row.IsPriority,
        };
        if (row.DateRaw.HasValue) meta["date_raw"] = row.DateRaw.Value;
        if (row.Preview != null) meta["preview"] = row.Preview;
        if (scan.ScopePath != null) meta["scope"] = scan.ScopePath;
        return meta;
    }

    private static int MatchLabel(List<ActionButton> buttons, string choice)
    {
        if (string.IsNullOrEmpty(choice)) return -1;
        for (int i = 0; i < buttons.Count; i++)
            if (!string.IsNullOrEmpty(buttons[i].Label) &&
                buttons[i].Label.Equals(choice, StringComparison.OrdinalIgnoreCase))
                return i;
        for (int i = 0; i < buttons.Count; i++)
            if (!string.IsNullOrEmpty(buttons[i].Label) &&
                buttons[i].Label.IndexOf(choice, StringComparison.OrdinalIgnoreCase) >= 0)
                return i;
        return -1;
    }

    /// <summary>Rendered reader-pane text: walks the MessagesDynamicRemapper
    /// card container's TextElements (the localized strings the human sees,
    /// bypassing TranslationID blobs), skipping the action labels collected
    /// separately. Falls back to the whole tool subtree.</summary>
    private static string ReaderBody(HashSet<string> skipLabels)
    {
        var root = RootVisualElement();
        if (root == null) return null;
        var containers = CollectByName(root, ReaderContainerName);
        var sb = new System.Text.StringBuilder();
        void Append(IEnumerable<string> texts)
        {
            foreach (var t in texts)
            {
                if (skipLabels.Contains(t)) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(t);
                if (sb.Length > 2400) return;
            }
        }
        if (containers.Count > 0)
        {
            foreach (var c in containers) Append(DeepTexts(c, 80, 2400));
        }
        else
        {
            foreach (var tool in CollectByName(root, PanelName)) Append(DeepTexts(tool, 120, 2400));
        }
        var body = sb.ToString().Trim();
        return body.Length == 0 ? null : body;
    }

    // -- primitives mirroring Navigator.UiClick / ui_find2's proven shapes --

    private static UnityEngine.UIElements.VisualElement RootVisualElement()
    {
        try
        {
            var pm = SI.Core.ManualSingleton<PanelManager>.Instance;
            if (pm != null)
            {
                var r = pm.RootVisualElement;
                if (r != null) return r;
            }
        }
        catch { }
        try { return UnityEngine.Object.FindObjectOfType<PanelManager>()?.RootVisualElement; }
        catch { return null; }
    }

    private static List<UnityEngine.UIElements.VisualElement> CollectByName(
        UnityEngine.UIElements.VisualElement node, string name)
    {
        var acc = new List<UnityEngine.UIElements.VisualElement>();
        Collect(node, name, acc, 0);
        return acc;
    }

    private static void Collect(UnityEngine.UIElements.VisualElement node, string name,
        List<UnityEngine.UIElements.VisualElement> acc, int depth)
    {
        if (node == null || depth > VisualMaxDepth || acc.Count > VisualMaxElements) return;
        try { if (node.name == name) acc.Add(node); } catch { }
        for (int i = 0; i < node.childCount; i++)
        {
            UnityEngine.UIElements.VisualElement child;
            try { child = node[i]; } catch { continue; }
            Collect(child, name, acc, depth + 1);
        }
    }

    /// <summary>Synthesized PointerDown+PointerUp at the element center via
    /// root.SendEvent — the exact event pair Navigator.UiClick has fired live
    /// since M4 (list-row selection, action buttons included).</summary>
    private static bool FireClick(UnityEngine.UIElements.VisualElement el)
    {
        try
        {
            var rect = el.worldBound;
            if (rect.width <= 0 || rect.height <= 0) return false;
            var root = RootVisualElement();
            if (root == null) return false;
            var pos = rect.center;

            var down = UnityEngine.UIElements.PointerDownEvent.GetPooled();
            down.position = pos;
            down.localPosition = pos;
            down.button = 0;
            down.pressedButtons = 1;
            down.clickCount = 1;
            root.SendEvent(down);
            down.Dispose();

            var up = UnityEngine.UIElements.PointerUpEvent.GetPooled();
            up.position = pos;
            up.localPosition = pos;
            up.button = 0;
            up.pressedButtons = 0;
            up.clickCount = 1;
            root.SendEvent(up);
            up.Dispose();
            return true;
        }
        catch { return false; }
    }

    private static readonly Regex RichTextTagRe = new Regex(@"</?[a-zA-Z][^<>]{0,150}>", RegexOptions.Compiled);

    /// <summary>Bounded TextElement harvest under a container (deeper than
    /// ui_find2's deepText, whose depth-6 cap misses card bodies living at
    /// depth 40+). Strips TMP rich-text markup, trims, dedupes, caps total.</summary>
    private static List<string> DeepTexts(UnityEngine.UIElements.VisualElement node, int maxHits, int maxChars)
    {
        var hits = new List<string>();
        var seen = new HashSet<string>();
        var budget = maxChars * 2;
        Walk(node, 0);
        return hits;

        void Walk(UnityEngine.UIElements.VisualElement n, int depth)
        {
            if (n == null || depth > 24 || hits.Count >= maxHits || budget <= 0) return;
            try
            {
                var te = n.TryCast<UnityEngine.UIElements.TextElement>();
                if (te != null)
                {
                    var raw = te.text;
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        var cleaned = RichTextTagRe.Replace(raw, "").Trim();
                        cleaned = cleaned.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", " ");
                        if (cleaned.Length > 300) cleaned = cleaned.Substring(0, 300);
                        if (cleaned.Length > 0 && seen.Add(cleaned))
                        {
                            hits.Add(cleaned);
                            budget -= cleaned.Length;
                        }
                    }
                }
            }
            catch { }
            for (int i = 0; i < n.childCount; i++)
            {
                UnityEngine.UIElements.VisualElement child;
                try { child = n[i]; } catch { continue; }
                Walk(child, depth + 1);
            }
        }
    }
}
