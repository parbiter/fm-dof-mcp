using System;
using System.Collections.Generic;
using BepInEx.Logging;
using FM.UI;
using SI.Bindable;
using SI.Core;

namespace FMBridge.Eyes;

internal sealed class StatusPoller
{
    private const int PollIntervalTicks = 30;
    private const int DiscoverRetryTicks = 300;

    private static readonly string[] DatePathCandidates =
    {
        "CurrentDate", "GameDate", "InGameDate", "Date",
        "game.CurrentDate", "game.GameDate", "game.InGameDate", "game.Date"
    };

    private readonly ManualLogSource _log;
    private readonly Dictionary<string, Bindings.Key> _keys = new Dictionary<string, Bindings.Key>();
    private readonly List<string> _watched = new List<string>();
    private readonly Dictionary<string, string> _lastValues = new Dictionary<string, string>();
    private readonly List<KeyValuePair<string, IReadOnlyNode>> _liveNodes = new List<KeyValuePair<string, IReadOnlyNode>>();
    private long _ticksSinceAttempt;
    private bool _discovered;
    private bool _hasLiveNodes;
    private string _datePath;

    public IReadOnlyNode ProcessingNode { get; private set; }
    public IReadOnlyNode WaitingNode { get; private set; }
    public IReadOnlyNode HolidayNode { get; private set; }
    public IReadOnlyNode DateNode { get; private set; }
    public IReadOnlyNode ContinueStateNode { get; private set; }
    public IReadOnlyNode ContinueLabelNode { get; private set; }

    public void AddRealNode(string label, IReadOnlyNode node)
    {
        foreach (var kv in _liveNodes)
            if (kv.Key == label) return;
        _liveNodes.Add(new KeyValuePair<string, IReadOnlyNode>(label, node));
        _hasLiveNodes = true;
        if (label.EndsWith(".GameProcessing")) ProcessingNode = node;
        else if (label.EndsWith(".GameWaiting")) WaitingNode = node;
        else if (label.EndsWith("IsOnHoliday")) HolidayNode = node;
        else if (label.EndsWith(".CurrentGameDate") || label.EndsWith(".CurrentDate")) DateNode = DateNode ?? node;
        else if (label.EndsWith(".continue.State")) ContinueStateNode = node;
        else if (label.EndsWith(".ContinueButtonMainString")) ContinueLabelNode = node;
        _log.LogInfo($"[Eyes] poller: tracking node '{label}'");
    }

    public StatusPoller(ManualLogSource log)
    {
        _log = log;
    }

    public void Tick(BindingSubsystem bindings)
    {
        if (bindings == null) return;
        if (_hasLiveNodes)
        {
            if (_ticksSinceAttempt++ % PollIntervalTicks != 0) return;
            PollLiveNodes();
            return;
        }
        if (!_discovered)
        {
            if (++_ticksSinceAttempt % DiscoverRetryTicks != 0) return;
            Discover();
            if (!_discovered) return;
            _lastValues.Clear();
        }
        if (_ticksSinceAttempt++ % PollIntervalTicks != 0) return;
        PollAll(bindings);
    }

    private void PollLiveNodes()
    {
        for (var i = _liveNodes.Count - 1; i >= 0; i--)
        {
            var kv = _liveNodes[i];
            string now;
            try
            {
                TypedValue v = null;
                try { v = kv.Value.Value; } catch { }
                now = Decode(v);
            }
            catch (Exception e)
            {
                now = "<err:" + e.GetType().Name + ">";
            }
            ReportChange(kv.Key, now);
        }
    }

    private void ReportChange(string path, string now)
    {
        if (now != "<null>" && _datePath == null && DatePathCandidatesContains(path))
        {
            _datePath = path;
            _log.LogInfo($"[Eyes] poller: DATE PATH RESOLVED -> '{path}'");
        }
        string last;
        if (!_lastValues.TryGetValue(path, out last) || last != now)
        {
            _lastValues[path] = now;
            _log.LogInfo($"[Eyes] status '{path}' = {now}");
        }
    }

    private void Discover()
    {
        try
        {
            var busyPath = ContinueManagerModule.s_gameProcessingBindingString;
            if (string.IsNullOrEmpty(busyPath))
            {
                if ((_ticksSinceAttempt / DiscoverRetryTicks) % 4 == 0)
                    _log.LogInfo("[Eyes] poller: waiting for ContinueManagerModule static init");
                return;
            }
            AddWatched(busyPath);
            foreach (var c in DatePathCandidates) AddWatched(c);
            _discovered = true;
            _log.LogInfo($"[Eyes] poller: keys made for [{string.Join(", ", _watched)}]");
        }
        catch (Exception e)
        {
            _log.LogWarning($"[Eyes] poller discovery failed: {e.Message}");
        }
    }

    public void AddRealPath(string path)
    {
        if (_discovered && !_keys.ContainsKey(path))
        {
            try
            {
                var span = SpanOf(path);
                var key = Bindings.MakeKey(ref span);
                _keys[path] = key;
                _watched.Add(path);
                _log.LogInfo($"[Eyes] poller: now tracking real path '{path}'");
            }
            catch (Exception e)
            {
                _log.LogWarning($"[Eyes] poller: MakeKey('{path}') failed: {e.Message}");
            }
        }
    }

    private void AddWatched(string path)
    {
        if (_keys.ContainsKey(path)) return;
        try
        {
            var span = SpanOf(path);
            var key = Bindings.MakeKey(ref span);
            _keys[path] = key;
            _watched.Add(path);
        }
        catch (Exception e)
        {
            _log.LogWarning($"[Eyes] poller: MakeKey('{path}') failed: {e.Message}");
        }
    }

    private void PollAll(BindingSubsystem bindings)
    {
        foreach (var path in _watched)
        {
            string now;
            try
            {
                var key = _keys[path];
                var value = bindings.Get(ref key);
                now = Decode(value);
            }
            catch (Exception e)
            {
                now = "<err:" + e.GetType().Name + ">";
            }
            ReportChange(path, now);
        }
    }

    private bool DatePathCandidatesContains(string p)
    {
        foreach (var c in DatePathCandidates) if (c == p) return true;
        return false;
    }

    private static Il2CppSystem.ReadOnlySpan<char> SpanOf(string s)
    {
        var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<char>(s.Length);
        for (var i = 0; i < s.Length; i++) arr[i] = s[i];
        return new Il2CppSystem.ReadOnlySpan<char>(arr, 0, s.Length);
    }

    private static string Decode(TypedValue value)
    {
        if (value == null) return "<null>";
        var type = "?";
        try { type = value.DataType?.Name ?? "?"; } catch { }
        try
        {
            var s = value.AsString();
            return $"{type}:{s}";
        }
        catch
        {
            return $"{type}:<unprintable>";
        }
    }
}
