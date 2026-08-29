using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using SI.Bindable;

namespace FMBridge.World;

/// <summary>
/// Writes text into an on-screen text-entry field resolved by exact element
/// name (index-th match), through the public value setter so the game's own
/// ChangeEvent handlers fire. FM never binds logic to raw UITK fields:
/// SI.Bindable.BindableTextEditBox / BindableIntEditBox wrap an inner UITK
/// field and react to the ChangeEvent bubbling up from it (filtering,
/// InputFilter sanitising, binding pushes all happen inside those handlers),
/// so writing the inner field's value IS the way the game notices.
/// submit=true additionally sends a NavigationSubmitEvent — the same event a
/// real Enter keypress delivers.
///
/// UnityEngine.UIElements.TextField : TextInputBaseField&lt;string&gt;, virtual
/// get/set_value raising ChangeEvent&lt;string&gt;. SI.Bindable.BindableTextEditBox
/// : VisualElement, private TextField m_textField, reacts via
/// BindableTextEditBox_OnValueChanged(ChangeEvent&lt;string&gt;) and
/// OnSubmit(NavigationSubmitEvent); SearchBox : BindableTextEditBox. There is
/// no TMP_InputField anywhere — FM26 text entry is pure UI Toolkit.
/// SI.Bindable.BindableIntEditBox : VisualElement, private IntegerField
/// m_textField, OnValueChanged(ChangeEvent&lt;int&gt;) — the amount-entry widget
/// (negotiation figures). UnityEngine.UIElements.NavigationSubmitEvent :
/// NavigationEventBase&lt;T&gt;.GetPooled(EventModifiers); UITK TextInputBase
/// declares EventInterest in NavigationSubmitEvent, so a direct SendEvent
/// commits delayed fields exactly like pressing Enter.
/// </summary>
internal static class TypeText
{
    public static JsonObject Run(string name, string text, int index, bool submit)
    {
        try
        {
            if (string.IsNullOrEmpty(name))
                return new JsonObject { ["ok"] = false, ["error"] = "type_text requires 'name'" };
            if (text == null) text = "";
            var root = RootVisualElement();
            if (root == null) return new JsonObject { ["ok"] = false, ["reason"] = "no-root-visualelement" };

            var matches = new List<UnityEngine.UIElements.VisualElement>();
            Collect(root, name, matches);
            if (matches.Count == 0)
                return new JsonObject { ["ok"] = false, ["error"] = "element-not-found:" + name };
            if (index < 0 || index >= matches.Count) index = 0;
            var el = matches[index];

            var target = ResolveField(el, out var sendTarget);
            if (target == null)
                return new JsonObject
                {
                    ["ok"] = false,
                    ["error"] = $"not-an-editable-field:{name} ({SafeTypeName(el)})",
                    ["found"] = matches.Count,
                };

            string after;
            if (target.Inner == FieldKind.Int)
            {
                if (!int.TryParse(text.Trim(), System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var n))
                    return new JsonObject
                    {
                        ["ok"] = false,
                        ["error"] = $"not-an-integer:{text}",
                        ["name"] = SafeName(el),
                    };
                target.Int.value = n;
                after = target.Int.value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                target.Text.value = text;
                after = target.Text.value;
            }

            bool submitted = false;
            string submitError = null;
            if (submit && sendTarget != null)
            {
                var evt = NewSubmitEvent();
                if (evt != null)
                {
                    try
                    {
                        sendTarget.SendEvent(evt);
                        submitted = true;
                    }
                    finally
                    {
                        evt.Dispose();
                    }
                }
                else submitError = "submit-event-unavailable";
            }

            var reply = new JsonObject
            {
                ["ok"] = true,
                ["name"] = SafeName(el),
                ["value"] = after,
                ["fieldType"] = SafeTypeName(target.Field),
                ["matches"] = matches.Count,
                ["submitted"] = submitted,
            };
            if (submitError != null) reply["submitError"] = submitError;
            return reply;
        }
        catch (Exception e)
        {
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
    }

    private enum FieldKind { Text, Int }

    /// <summary>Builds a pooled NavigationSubmitEvent (the event a real Enter key
    /// delivers) via reflection: its GetPooled signature names EventModifiers,
    /// an enum Il2CppInterop placed in UnityEngine.IMGUIModule, which the csproj
    /// deliberately doesn't reference. Runtime name-resolved lookup needs no
    /// compile-time dependency.</summary>
    private static UnityEngine.UIElements.EventBase NewSubmitEvent()
    {
        try
        {
            foreach (var m in typeof(UnityEngine.UIElements.NavigationSubmitEvent).GetMethods())
            {
                if (!m.IsStatic || m.Name != "GetPooled") continue;
                var ps = m.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType.Name == "EventModifiers")
                    return (UnityEngine.UIElements.EventBase)m.Invoke(null,
                        new object[] { Activator.CreateInstance(ps[0].ParameterType) });
            }
        }
        catch { }
        return null;
    }

    private sealed class FieldRef
    {
        public UnityEngine.UIElements.TextField Text;
        public UnityEngine.UIElements.IntegerField Int;
        public UnityEngine.UIElements.VisualElement Field => (UnityEngine.UIElements.VisualElement)Text ?? Int;
        public FieldKind Inner => Int != null ? FieldKind.Int : FieldKind.Text;
    }

    /// <summary>Finds the real editable field behind a matched element: direct
    /// UITK text field, FM's wrapped boxes (typed m_textField accessor), or as a
    /// last resort the first text/int field among the descendants.</summary>
    private static FieldRef ResolveField(UnityEngine.UIElements.VisualElement el,
        out UnityEngine.UIElements.VisualElement sendTarget)
    {
        sendTarget = el;
        var direct = el.TryCast<UnityEngine.UIElements.TextField>();
        if (direct != null) return new FieldRef { Text = direct };
        var directInt = el.TryCast<UnityEngine.UIElements.IntegerField>();
        if (directInt != null) return new FieldRef { Int = directInt };

        try
        {
            var box = el.TryCast<SI.Bindable.BindableTextEditBox>();
            if (box != null && box.m_textField != null)
            {
                var tf = box.m_textField.TryCast<UnityEngine.UIElements.TextField>();
                if (tf != null) return new FieldRef { Text = tf };
            }
        }
        catch { }

        try
        {
            var ibox = el.TryCast<SI.Bindable.BindableIntEditBox>();
            if (ibox != null && ibox.m_textField != null)
            {
                var iv = ibox.m_textField.TryCast<UnityEngine.UIElements.IntegerField>();
                if (iv != null) return new FieldRef { Int = iv };
            }
        }
        catch { }

        var descendant = FindDescendantField(el);
        if (descendant != null) return descendant;

        // Upward fallback: UITK's TextField wires a real ChangeEvent-raising
        // TextInputBase whose visualInput is a bare TextElement literally
        // named "unity-text-input" (Unity's own internal constant name --
        // e.g. the raw ModalDialog's CreateShortlistDialog input). Matching
        // that name (there is no other name to click on -- the enclosing
        // TextField itself is typically unnamed in FM's own UXML) lands
        // Collect() on this inner visual node, which is NOT itself a
        // TextField/IntegerField and has no editable descendants of its
        // own -- the real value lives on its ANCESTOR TextField. Walk up a
        // few levels (bounded, since `parent` on a detached/root element is
        // null and this must never loop) trying a direct cast at each hop.
        return FindAncestorField(el);
    }

    private static FieldRef FindAncestorField(UnityEngine.UIElements.VisualElement node)
    {
        var cur = node;
        for (int i = 0; i < 8 && cur != null; i++)
        {
            UnityEngine.UIElements.VisualElement parent;
            try { parent = cur.parent; } catch { break; }
            if (parent == null) break;

            var direct = parent.TryCast<UnityEngine.UIElements.TextField>();
            if (direct != null) return new FieldRef { Text = direct };
            var directInt = parent.TryCast<UnityEngine.UIElements.IntegerField>();
            if (directInt != null) return new FieldRef { Int = directInt };

            try
            {
                var box = parent.TryCast<SI.Bindable.BindableTextEditBox>();
                if (box != null && box.m_textField != null)
                {
                    var tf = box.m_textField.TryCast<UnityEngine.UIElements.TextField>();
                    if (tf != null) return new FieldRef { Text = tf };
                }
            }
            catch { }

            try
            {
                var ibox = parent.TryCast<SI.Bindable.BindableIntEditBox>();
                if (ibox != null && ibox.m_textField != null)
                {
                    var iv = ibox.m_textField.TryCast<UnityEngine.UIElements.IntegerField>();
                    if (iv != null) return new FieldRef { Int = iv };
                }
            }
            catch { }

            cur = parent;
        }
        return null;
    }

    private static FieldRef FindDescendantField(UnityEngine.UIElements.VisualElement node)
    {
        if (node == null) return null;
        var direct = node.TryCast<UnityEngine.UIElements.TextField>();
        if (direct != null) return new FieldRef { Text = direct };
        var directInt = node.TryCast<UnityEngine.UIElements.IntegerField>();
        if (directInt != null) return new FieldRef { Int = directInt };
        for (int i = 0; i < node.childCount; i++)
        {
            UnityEngine.UIElements.VisualElement child;
            try { child = node[i]; } catch { continue; }
            var found = FindDescendantField(child);
            if (found != null) return found;
        }
        return null;
    }

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

    private static void Collect(UnityEngine.UIElements.VisualElement node, string name,
        List<UnityEngine.UIElements.VisualElement> acc, int depth = 0)
    {
        if (node == null || depth > 64 || acc.Count > 200) return;
        try { if (node.name == name) acc.Add(node); } catch { }
        for (int i = 0; i < node.childCount; i++)
            Collect(node[i], name, acc, depth + 1);
    }

    private static string SafeName(UnityEngine.UIElements.VisualElement el)
    {
        try { return el.name; } catch { return "?"; }
    }

    private static string SafeTypeName(object o)
    {
        try { return o?.GetType().Name ?? "?"; } catch { return "?"; }
    }
}
