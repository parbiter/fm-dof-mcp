using System.Text.Json.Nodes;

namespace FMBridge.Voice;

internal sealed class GameStatusSnapshot
{
    public bool InCareer;
    public bool Processing;
    public bool Waiting;
    public bool Holiday;
    public string ContinueState;
    public string ContinueLabel;
    public ulong DateRaw;
    public string DateIso;
    public long Tick;
    public double WallClockSec;

    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["in_career"] = InCareer,
            ["processing"] = Processing,
            ["waiting"] = Waiting,
            ["holiday"] = Holiday,
            ["tick"] = Tick,
        };
        if (InCareer)
        {
            o["date_raw"] = DateRaw;
            o["date_iso"] = DateIso ?? $"raw:{DateRaw}";
            if (ContinueState != null) o["continue_state"] = ContinueState;
            if (ContinueLabel != null) o["continue_label"] = ContinueLabel;
        }
        return o;
    }
}

internal sealed class SnapshotStore
{
    private volatile GameStatusSnapshot _current;

    public GameStatusSnapshot Current => _current;

    public void Publish(GameStatusSnapshot snapshot)
    {
        _current = snapshot;
    }
}
