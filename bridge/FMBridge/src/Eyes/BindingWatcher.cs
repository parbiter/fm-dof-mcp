using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using SI.Bindable;
using SI.Core;

namespace FMBridge.Eyes;

internal sealed class BindingWatcher
{
    private sealed class Sub
    {
        public string Label;
        public Bindings.Key Key;
        public IReadOnlyNode Node;
        public Bindings.ValueChangedCallback Cb;
    }

    private readonly ManualLogSource _log;
    private readonly List<object> _anchors = new List<object>();
    private readonly Dictionary<string, Sub> _subs = new Dictionary<string, Sub>();
    private BindingSubsystem _bindings;

    public event Action Changed;

    public BindingWatcher(ManualLogSource log)
    {
        _log = log;
    }

    public int Count => _subs.Count;

    public void EnsureBound(BindingSubsystem bindings, string label, IReadOnlyNode node)
    {
        if (bindings == null || node == null) return;
        _bindings = bindings;
        if (_subs.TryGetValue(label, out var sub))
        {
            if (ReferenceEquals(sub.Node, node)) return;
            if (KeysEqual(sub.Key, node.Key))
            {
                sub.Node = node;
                return;
            }
            Unbind(sub);
        }
        try
        {
            var key = node.Key;
            var cb = DelegateSupport.ConvertDelegate<Bindings.ValueChangedCallback>(
                (Action<Bindings.Key, TypedValue>)((k, v) => Fire(label)));
            _anchors.Add(cb);
            var bindKey = key;
            bindings.Bind(ref bindKey, cb);
            _subs[label] = new Sub { Label = label, Key = key, Node = node, Cb = cb };
            _log.LogInfo($"[Eyes] watcher bound '{label}' (callbacks={node.CallbackCount})");
        }
        catch (Exception e)
        {
            _log.LogWarning($"[Eyes] watcher bind failed '{label}': {e.Message}");
        }
    }

    public void UnbindAll()
    {
        if (_bindings == null) return;
        foreach (var s in _subs.Values) Unbind(s);
        _subs.Clear();
    }

    private void Unbind(Sub s)
    {
        try
        {
            var key = s.Key;
            _bindings.Unbind(ref key, s.Cb);
            _log.LogInfo($"[Eyes] watcher unbound '{s.Label}'");
        }
        catch (Exception e)
        {
            _log.LogWarning($"[Eyes] watcher unbind failed '{s.Label}': {e.Message}");
        }
    }

    private void Fire(string label)
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception e)
        {
            _log.LogError($"[Eyes] watcher handler error ({label}): {e}");
        }
    }

    private static bool KeysEqual(Bindings.Key a, Bindings.Key b)
    {
        try { return a.Equals(b); }
        catch { return false; }
    }
}
