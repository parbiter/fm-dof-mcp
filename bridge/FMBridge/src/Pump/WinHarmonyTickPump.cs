using System;
using BepInEx.Logging;

namespace FMBridge.Pump;

internal sealed class WinHarmonyTickPump : ITickPump
{
    public string Name => "win-x64 Harmony pump";

    public event Action Tick
    {
        add { }
        remove { }
    }

    private WinHarmonyTickPump()
    {
    }

    public static WinHarmonyTickPump TryCreate(ManualLogSource log)
    {
        log.LogWarning("[Bridge] win-x64 pump not implemented yet; bridge will idle without ticks");
        return new WinHarmonyTickPump();
    }

    public void Dispose()
    {
    }
}
