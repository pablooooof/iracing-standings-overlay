namespace StandingsOverlay.Data;

/// <summary>
/// Watches for driver swaps in team/endurance events: the roster is reparsed from session YAML
/// whenever it changes, and a car whose driver name changes has just done a swap. Records the
/// time so the standings/relative can tag the car for a while ("new driver in the car"). Cheap —
/// only does work on the rare tick where a name actually differs.
/// </summary>
public sealed class DriverSwapTracker
{
    private readonly Dictionary<int, string> _lastDriver = new();
    private readonly Dictionary<int, double> _swapAt = new();
    private double _now = -1;

    public void Update(RawTick t, Roster roster)
    {
        _now = t.SessionTime;
        if (_now < 0) return;
        foreach (var d in roster.Drivers.Values)
        {
            if (d.IsPaceCar || d.IsSpectator) continue;
            string name = d.Name ?? "";
            if (_lastDriver.TryGetValue(d.CarIdx, out var prev) && prev != name
                && prev.Length > 0 && name.Length > 0)
                _swapAt[d.CarIdx] = _now;
            _lastDriver[d.CarIdx] = name;
        }
    }

    /// <summary>True for <paramref name="withinSec"/> after this car's driver last changed.</summary>
    public bool JustSwapped(int idx, double withinSec) =>
        _now >= 0 && _swapAt.TryGetValue(idx, out var at) && _now - at <= withinSec;

    public void Reset()
    {
        _lastDriver.Clear();
        _swapAt.Clear();
        _now = -1;
    }
}
