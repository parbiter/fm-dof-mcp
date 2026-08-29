using System;
using System.Diagnostics;
using BepInEx.Configuration;
using BepInEx.Logging;
using FMBridge.Voice;
using SI.Bindable;
using SI.Core;

namespace FMBridge.Eyes;

internal sealed class EyesModule
{
    private const int StatusUpdateIntervalTicks = 10;
    private const int DisplayLeadMinutes = 15;

    private readonly ManualLogSource _log;
    private readonly StatusPoller _poller;
    private readonly TreeWalker _treeWalker;
    private readonly BindingWatcher _watcher;
    private readonly VoiceServer _voice;
    private readonly ConfigEntry<float> _idleSeconds;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _ticks;
    private double _lastBusySec = -1.0;
    private bool _idleAnnounced;
    private ulong? _lastDateRaw;
    private bool _wasBusy;
    private volatile bool _walkNow;

    public ConfigEntry<bool> SpikeEnabled { get; }

    public static EyesModule Instance { get; private set; }

    public EyesModule(ManualLogSource log, ConfigFile config, VoiceServer voice)
    {
        Instance = this;
        _log = log;
        _voice = voice;
        _poller = new StatusPoller(log);
        _treeWalker = new TreeWalker(log, _poller);
        _watcher = new BindingWatcher(log);
        _watcher.Changed += OnBindingChanged;
        SpikeEnabled = config.Bind("Eyes", "SpikeEnabled", true,
            "Harmony hook on BindingSubsystem.Initialise to capture the live Bindings instance");
        _idleSeconds = config.Bind("Voice", "IdleSeconds", 3f,
            "Seconds the game must stay unbusy before on_idle is broadcast");
    }

    public void Start()
    {
            if (SpikeEnabled.Value)
            {
                SpikeHooks.CareerActivated += () => _walkNow = true;
                SpikeHooks.Apply(_log);
            }
    }

    public void OnTick()
    {
        var bindings = SpikeHooks.CapturedBindings;
        _ticks++;
        if (_voice != null && _ticks % StatusUpdateIntervalTicks == 0)
            UpdateStatus();
        _poller.Tick(bindings);
        if (_walkNow)
        {
            _walkNow = false;
            _treeWalker.MaybeWalk(bindings, _ticks, true);
        }
        _treeWalker.MaybeWalk(bindings, _ticks, false);
    }

    private void UpdateStatus()
    {
        var bindings = SpikeHooks.CapturedBindings;
        _watcher.EnsureBound(bindings, "date", _poller.DateNode);
        _watcher.EnsureBound(bindings, "processing", _poller.ProcessingNode);
        _watcher.EnsureBound(bindings, "waiting", _poller.WaitingNode);
        _watcher.EnsureBound(bindings, "holiday", _poller.HolidayNode);
        var snapshot = BuildSnapshot();
        _voice.Snapshots.Publish(snapshot);
        DetectEvents(snapshot);
    }

    private void OnBindingChanged()
    {
        if (_voice == null) return;
        var snapshot = BuildSnapshot();
        _voice.Snapshots.Publish(snapshot);
        DetectEvents(snapshot);
    }

    private GameStatusSnapshot BuildSnapshot()
    {
        var snap = new GameStatusSnapshot
        {
            Tick = _ticks,
            WallClockSec = _clock.Elapsed.TotalSeconds,
        };
        var dateNode = _poller.DateNode;
        if (dateNode == null) return snap;
        snap.InCareer = true;
        snap.Processing = ReadBool(_poller.ProcessingNode);
        snap.Waiting = ReadBool(_poller.WaitingNode);
        snap.Holiday = ReadBool(_poller.HolidayNode);
        snap.ContinueState = ReadString(_poller.ContinueStateNode);
        snap.ContinueLabel = ReadString(_poller.ContinueLabelNode);
        try
        {
            TypedValue v = null;
            try { v = dateNode.Value; } catch { }
            if (v != null)
            {
                var gd = FM.UI.VisualFunctions.GameDateFunctions.GetGameDate(v);
                snap.DateRaw = (ulong)gd.Data1;
                var dt = FM.GamePlugin.GameDate.ToDateTime(gd);
                snap.DateIso = dt.AddMinutes(-DisplayLeadMinutes).ToString("yyyy-MM-dd HH:mm");
            }
        }
        catch (Exception e)
        {
            _log.LogDebug($"[Eyes] date decode failed: {e.Message}");
        }
        return snap;
    }

    private static bool ReadBool(IReadOnlyNode node)
    {
        if (node == null) return false;
        try
        {
            TypedValue v = null;
            try { v = node.Value; } catch { }
            return v != null && v.AsString() == "True";
        }
        catch
        {
            return false;
        }
    }

    private static string ReadString(IReadOnlyNode node)
    {
        if (node == null) return null;
        try
        {
            TypedValue v = null;
            try { v = node.Value; } catch { }
            return v?.AsString();
        }
        catch
        {
            return null;
        }
    }

    private void DetectEvents(GameStatusSnapshot snap)
    {
        if (!snap.InCareer)
        {
            ResetIdleTracking(snap.WallClockSec);
            return;
        }
        var busy = snap.Processing || snap.Waiting;
        if (busy != _wasBusy)
        {
            BroadcastBusyEdge(busy, snap);
            _wasBusy = busy;
        }
        if (busy)
        {
            _idleAnnounced = false;
            _lastBusySec = snap.WallClockSec;
        }
        else if (_lastBusySec < 0)
        {
            _lastBusySec = snap.WallClockSec;
        }
        else if (!_idleAnnounced && snap.WallClockSec - _lastBusySec >= _idleSeconds.Value)
        {
            _idleAnnounced = true;
            BroadcastIdle(snap);
        }
        if (_lastDateRaw.HasValue && snap.DateRaw != _lastDateRaw.Value)
        {
            BroadcastDateChange(_lastDateRaw.Value, snap.DateRaw);
        }
        if (snap.DateRaw != 0 || snap.DateIso != null) _lastDateRaw = snap.DateRaw;
    }

    private void ResetIdleTracking(double now)
    {
        _idleAnnounced = false;
        _lastBusySec = -1.0;
        _lastDateRaw = null;
        _wasBusy = false;
    }

    private void BroadcastBusyEdge(bool busy, GameStatusSnapshot snap)
    {
        var name = busy ? "on_busy_start" : "on_busy_end";
        _log.LogInfo($"[Voice] event {name} tick={snap.Tick}");
        _voice.Broadcast(name, new System.Text.Json.Nodes.JsonObject
        {
            ["processing"] = snap.Processing,
            ["waiting"] = snap.Waiting,
            ["date_iso"] = snap.DateIso,
            ["tick"] = snap.Tick,
        });
    }

    private void BroadcastIdle(GameStatusSnapshot snap)
    {
        _log.LogInfo($"[Voice] event on_idle date={snap.DateIso ?? "?"} tick={snap.Tick}");
        _voice.Broadcast("on_idle", new System.Text.Json.Nodes.JsonObject
        {
            ["date_iso"] = snap.DateIso,
            ["date_raw"] = snap.DateRaw,
            ["tick"] = snap.Tick,
            ["uptime_sec"] = Math.Round(snap.WallClockSec, 1),
        });
    }

    private void BroadcastDateChange(ulong oldRaw, ulong newRaw)
    {
        _log.LogInfo($"[Voice] event on_date_change {oldRaw} -> {newRaw}");
        _voice.Broadcast("on_date_change", new System.Text.Json.Nodes.JsonObject
        {
            ["old_raw"] = oldRaw,
            ["new_raw"] = newRaw,
        });
    }
}
