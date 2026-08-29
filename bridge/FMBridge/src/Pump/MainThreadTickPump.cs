using System;
using System.Runtime.InteropServices;
using BepInEx.Unity.IL2CPP;

namespace FMBridge.Pump;

internal sealed class MainThreadTickPump : ITickPump
{
    public string Name => "MainThreadTick (DadMych fork)";

    public event Action Tick;

    public static bool IsSupported =>
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    public MainThreadTickPump()
    {
        IL2CPPChainloader.MainThreadTick += OnMainThreadTick;
    }

    private void OnMainThreadTick()
    {
        Tick?.Invoke();
    }

    public void Dispose()
    {
        IL2CPPChainloader.MainThreadTick -= OnMainThreadTick;
    }
}
