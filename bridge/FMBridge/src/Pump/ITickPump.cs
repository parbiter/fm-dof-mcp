using System;

namespace FMBridge.Pump;

internal interface ITickPump : IDisposable
{
    string Name { get; }

    event Action Tick;
}
