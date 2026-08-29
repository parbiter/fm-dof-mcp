using System;
using System.Diagnostics;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using FMBridge.Eyes;
using FMBridge.Pump;
using FMBridge.Voice;

namespace FMBridge;

    [BepInPlugin("dev.fmdofmcp.bridge", "FM Bridge", "0.5.0")]
    public class BridgePlugin : BasePlugin
    {
        private ManualLogSource _log;
        private ITickPump _pump;
        private EyesModule _eyes;
        private VoiceServer _voice;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _ticks;
        private double _nextBeat = -1.0;
        private const int BeatSeconds = 5;

        public override void Load()
        {
            _log = Log;
            _pump = MainThreadTickPump.IsSupported
                ? new MainThreadTickPump()
                : WinHarmonyTickPump.TryCreate(Log);

            Log.LogInfo($"[Bridge] loaded, pump={_pump.Name}");
            _pump.Tick += OnTick;

            try
            {
                var voiceEnabled = Config.Bind("Voice", "Enabled", true,
                    "Host a localhost WebSocket server for external control");
                var voicePort = Config.Bind("Voice", "Port", 7777,
                    "TCP port for the WebSocket server (localhost only)");
                var allowCommands = Config.Bind("Voice", "AllowCommands", false,
                    "Permit write commands over the WS protocol (continue)");
                if (voiceEnabled.Value)
                {
                    _voice = new VoiceServer(Log, voicePort.Value, allowCommands.Value);
                    if (!_voice.Start())
                        _voice = null;
                }
                FMBridge.World.Navigator.Init(o => _voice?.Broadcast("on_nav", o));
                FMBridge.World.Navigator._log = Log;
                _eyes = new EyesModule(Log, Config, _voice);
                _eyes.Start();
            }
            catch (Exception e)
            {
                Log.LogError($"[Bridge] init failed: {e}");
            }
        }

        public override bool Unload()
        {
            if (_pump != null)
            {
                _pump.Tick -= OnTick;
                _pump.Dispose();
                _pump = null;
            }
            _voice?.Dispose();
            return base.Unload();
        }

        private void OnTick()
        {
            try
            {
                _ticks++;
                _voice?.Queue.Drain();
                _eyes?.OnTick();
                FMBridge.World.Navigator.Tick();
                FMBridge.World.UiInject.Tick();
                var now = _clock.Elapsed.TotalSeconds;
                if (_nextBeat < 0)
                {
                    _nextBeat = now + BeatSeconds;
                    return;
                }
                if (now < _nextBeat) return;
                _nextBeat = now + BeatSeconds;
                var uptime = TimeSpan.FromSeconds(now).ToString(@"hh\:mm\:ss");
                var init = SpikeHooks.Initialised > 0 ? "Y" : "n";
                Log.LogInfo($"[Bridge] alive uptime={uptime} ticks={_ticks} avg={_ticks / now:F0}/s init={init}");
            }
            catch (Exception e)
            {
                Log.LogError($"[Bridge] tick handler error: {e}");
            }
        }
    }
