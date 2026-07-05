namespace StandingsOverlay.Data;

/// <summary>
/// Per-car race-craft state built from cheap observations: lap times (sampled at each car's own
/// lap crossing), pit road transitions, and the first position seen (the grid). Everything the
/// strategy columns show — expected pit lap, stops to end, splash-and-dash, fuel-save detection,
/// pace vs class — is derived from this.
/// </summary>
public sealed class StintTracker
{
    private sealed class CarState
    {
        public int LastLapSeen = -1;
        public readonly List<float> LapTimes = new(32);   // this car's recent laps, capped
        public readonly List<int> StintLengths = new(8);  // completed green-lap stints
        public int CurrentStintStartLap;
        public bool WasOnPit;
        public int PitCount;
        public int GridPos;                               // first valid race position seen
    }

    private const int MaxLapTimes = 30;
    private readonly Dictionary<int, CarState> _cars = new();
    private bool _isRace;

    public void Update(RawTick t)
    {
        _isRace = t.SessionType.Contains("Race", StringComparison.OrdinalIgnoreCase);

        for (int idx = 0; idx < t.Position.Length; idx++)
        {
            if (idx >= t.Lap.Length || idx >= t.OnPitRoad.Length) break;

            if (!_cars.TryGetValue(idx, out var s))
                _cars[idx] = s = new CarState();

            if (_isRace && s.GridPos == 0 && t.Position[idx] > 0)
                s.GridPos = t.Position[idx];

            // Lap crossing for THIS car (not the player) → record its lap time.
            if (t.Lap[idx] > s.LastLapSeen)
            {
                if (s.LastLapSeen >= 0 && idx < t.LastLap.Length && t.LastLap[idx] > 5)
                {
                    s.LapTimes.Add(t.LastLap[idx]);
                    if (s.LapTimes.Count > MaxLapTimes) s.LapTimes.RemoveAt(0);
                }
                s.LastLapSeen = t.Lap[idx];
            }

            bool onPit = t.OnPitRoad[idx];
            if (onPit && !s.WasOnPit)
            {
                int stintLen = t.Lap[idx] - s.CurrentStintStartLap;
                if (stintLen >= 3) s.StintLengths.Add(stintLen);   // ignore drive-throughs/early tows
                s.PitCount++;
            }
            else if (!onPit && s.WasOnPit)
            {
                s.CurrentStintStartLap = t.Lap[idx];
            }
            s.WasOnPit = onPit;
        }
    }

    public int PositionsGained(int idx, int currentPos)
    {
        if (!_isRace || currentPos <= 0) return 0;
        return _cars.TryGetValue(idx, out var s) && s.GridPos > 0 ? s.GridPos - currentPos : 0;
    }

    /// <summary>Median completed stint length in laps, or null before the car's first stop.</summary>
    public int? TypicalStintLaps(int idx)
    {
        if (!_cars.TryGetValue(idx, out var s) || s.StintLengths.Count == 0) return null;
        var sorted = s.StintLengths.OrderBy(x => x).ToList();
        return sorted[sorted.Count / 2];
    }

    /// <summary>
    /// Strategy summary for the car: expected next pit lap, or stops remaining to the end.
    /// Returns "" when unknowable (no completed stint yet, or not a race).
    /// "~34" next stop around lap 34 · "34!" overdue · "0stp" can make it · "2stp*" = last stop is a splash.
    /// </summary>
    public string StrategyText(int idx, int carLap, double lapsRemain)
    {
        if (!_isRace || TypicalStintLaps(idx) is not int stint || !_cars.TryGetValue(idx, out var s))
            return "";

        int lapsIntoStint = carLap - s.CurrentStintStartLap;
        int lapsLeftInTank = stint - lapsIntoStint;

        if (lapsRemain >= 0 && lapsRemain <= lapsLeftInTank) return "0stp";

        int expectedPitLap = s.CurrentStintStartLap + stint;
        if (lapsRemain < 0) return lapsLeftInTank < 0 ? $"{expectedPitLap}!" : $"~{expectedPitLap}";

        // Full stops needed to cover the remaining distance after the current tank.
        double afterTank = lapsRemain - Math.Max(0, lapsLeftInTank);
        int stops = (int)Math.Ceiling(afterTank / stint);
        double lastStintFraction = afterTank % stint / stint;
        bool splash = stops >= 1 && lastStintFraction > 0 && lastStintFraction < 0.2;

        return stops <= 1
            ? $"~{expectedPitLap}{(splash ? "*" : "")}"
            : $"{stops}stp{(splash ? "*" : "")}";
    }

    /// <summary>All recorded lap times for the car, oldest first (session order; capped at 30).</summary>
    public IReadOnlyList<float> LapTimesFor(int idx) =>
        _cars.TryGetValue(idx, out var s) ? s.LapTimes : [];

    /// <summary>Completed timed laps for the car.</summary>
    public int LapCount(int idx) => _cars.TryGetValue(idx, out var s) ? s.LapTimes.Count : 0;

    /// <summary>Average of the car's last <paramref name="n"/> lap times, or null.</summary>
    public float? RecentPace(int idx, int n = 5)
    {
        if (!_cars.TryGetValue(idx, out var s) || s.LapTimes.Count < Math.Min(n, 3)) return null;
        return s.LapTimes.TakeLast(n).Average();
    }

    /// <summary>
    /// True when the car looks like it's fuel-saving: consistent laps well off its own best.
    /// Heuristic — traffic can trigger it too, which is why it's a small tag, not a headline.
    /// </summary>
    public bool LooksLikeFuelSaving(int idx)
    {
        if (!_isRace || !_cars.TryGetValue(idx, out var s) || s.LapTimes.Count < 5) return false;

        var recent = s.LapTimes.TakeLast(4).ToList();
        float best = s.LapTimes.Min();
        float avg = recent.Average();
        if (avg < best * 1.015f) return false;                    // pushing

        double spread = recent.Max() - recent.Min();
        return spread < best * 0.01f;                             // slow AND metronomic = saving
    }

    public void Reset() => _cars.Clear();
}
