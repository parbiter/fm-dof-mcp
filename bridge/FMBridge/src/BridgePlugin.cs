using System;
using System.Diagnostics;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using FMBridge.Eyes;
using FMBridge.Pump;
using FMBridge.Voice;
using HarmonyLib;

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
        private bool _safeTickRunning;
        private Harmony _frameHarmony;
        private static BridgePlugin _frameInstance;
        private const int BeatSeconds = 5;

        public override void Load()
        {
            _log = Log;
            _pump = MainThreadTickPump.IsSupported
                ? new MainThreadTickPump()
                : WinHarmonyTickPump.TryCreate(Log);

            Log.LogInfo($"[Bridge] loaded, pump={_pump.Name}");
            // The compatibility-layer tick can arrive from inside an arbitrary
            // IL2CPP runtime_invoke, including generateVisualContent itself.
            // Prefix the outer repaint entry point instead: this is a stable
            // main-thread frame boundary before RenderChain begins rendering.
            _frameInstance = this;
            _frameHarmony = new Harmony("dev.fmdofmcp.bridge.safe-frame");
            var repaint = AccessTools.Method(
                AccessTools.TypeByName("UnityEngine.UIElements.UIElementsRuntimeUtility"),
                "RepaintPanels");
            if (repaint == null) throw new MissingMethodException("UIElementsRuntimeUtility.RepaintPanels");
            _frameHarmony.Patch(repaint,
                prefix: new HarmonyMethod(typeof(BridgePlugin), nameof(BeforeRepaintPanels)));

            try
            {
                var voiceEnabled = Config.Bind("Voice", "Enabled", true,
                    "Host a localhost WebSocket server for external control");
                var voicePort = Config.Bind("Voice", "Port", 7777,
                    "TCP port for the WebSocket server (localhost only)");
                var allowCommands = Config.Bind("Voice", "AllowCommands", true,
                    "Permit write commands over the WS protocol; this build only exposes the advise-only surface (shortlist management)");
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
                ChatServiceLauncher.Start(Log);
            }
            catch (Exception e)
            {
                Log.LogError($"[Bridge] init failed: {e}");
            }
        }

        public override bool Unload()
        {
            try { _frameHarmony?.UnpatchSelf(); } catch { }
            _frameHarmony = null;
            if (ReferenceEquals(_frameInstance, this)) _frameInstance = null;
            if (_pump != null)
            {
                _pump.Dispose();
                _pump = null;
            }
            ChatServiceLauncher.Stop();
            _voice?.Dispose();
            return base.Unload();
        }

        private void RunSafeTick()
        {
            if (_safeTickRunning) return;
            _safeTickRunning = true;
            try
            {
                _ticks++;
                _voice?.Queue.Drain();
                _eyes?.OnTick();
                FMBridge.World.Navigator.Tick();
                FMBridge.World.UiInject.Tick();
                var now = _clock.Elapsed.TotalSeconds;
                if (_nextBeat < 0) _nextBeat = now + BeatSeconds;
                else if (now >= _nextBeat)
                {
                    _nextBeat = now + BeatSeconds;
                    var uptime = TimeSpan.FromSeconds(now).ToString(@"hh\:mm\:ss");
                    var init = SpikeHooks.Initialised > 0 ? "Y" : "n";
                    Log.LogInfo($"[Bridge] alive uptime={uptime} ticks={_ticks} avg={_ticks / now:F0}/s init={init}");
                }
            }
            catch (Exception e)
            {
                Log.LogError($"[Bridge] scheduled tick handler error: {e}");
            }
            finally { _safeTickRunning = false; }
        }

        private static void BeforeRepaintPanels()
        {
            try { _frameInstance?.RunSafeTick(); } catch { }
        }
    }
