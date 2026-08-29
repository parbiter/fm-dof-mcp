using System;
using System.Collections.Concurrent;
using BepInEx.Logging;

namespace FMBridge.Voice;

internal sealed class MainThreadQueue
{
    private readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();
    private readonly ManualLogSource _log;

    public MainThreadQueue(ManualLogSource log)
    {
        _log = log;
    }

    public void Enqueue(Action action)
    {
        if (action != null) _queue.Enqueue(action);
    }

    public void Drain()
    {
        while (_queue.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                _log.LogError($"[Voice] main-thread job failed: {e}");
            }
        }
    }
}
