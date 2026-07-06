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
        public readonly List<float> LapTimes = new(32);   // this car's recent laps, capped; -1 = no time (tow/reset)
        public readonly List<bool> PitLaps = new(32);     // parallel: lap touched the pit lane (in/out lap)
        public bool PitThisLap;
        public bool PendingCrossing;                      // lap completed, time not yet published
        public bool PendingPit;
        public float LastLapValue = -999f;                // last CarIdxLastLapTime we recorded
        public readonly List<int> StintLengths = new(8);  // completed green-lap stints
        public int CurrentStintStartLap;
        public bool WasOnPit;
        public int PitCount;
        public int GridPos;                               // first valid race position seen
        public double LastTotalDist = -1;                 // stopped-car detection
        public double LastMoveTime = -1;
    }

    private const int MaxLapTimes = 30;
    private readonly Dictionary<int, CarState> _cars = new();
    private bool _isRace;
    private double _now = -1;                             // last SessionTime seen

    public void Update(RawTick t)
    {
        _isRace = t.SessionType.Contains("Race", StringComparison.OrdinalIgnoreCase);
        _now = t.SessionTime;

        for (int idx = 0; idx < t.Position.Length; idx++)
        {
            if (idx >= t.Lap.Length || idx >= t.OnPitRoad.Length) break;

            if (!_cars.TryGetValue(idx, out var s))
                _cars[idx] = s = new CarState
                {
                    // Don't treat a LastLapTime that predates us as a fresh lap.
                    LastLapValue = idx < t.LastLap.Length ? t.LastLap[idx] : -999f,
                };

            if (_isRace && s.GridPos == 0 && t.Position[idx] > 0)
                s.GridPos = t.Position[idx];

            // Lap crossing: note it, but don't read CarIdxLastLapTime yet — iRacing bumps the
            // lap counter a beat before it publishes the time, and at 4 Hz we land in between.
            // The lap is recorded below once the value actually changes.
            if (t.Lap[idx] > s.LastLapSeen)
            {
                if (s.LastLapSeen >= 0)
                {
                    // Crossed again and the previous lap never produced a time → tow/reset.
                    // (Skip if the car has no laps yet: that's just the out lap.)
                    if (s.PendingCrossing && s.LapTimes.Count > 0) AddLap(s, -1f, s.PendingPit);
                    s.PendingCrossing = true;
                    s.PendingPit = s.PitThisLap || t.OnPitRoad[idx];
                }
                s.LastLapSeen = t.Lap[idx];
                s.PitThisLap = t.OnPitRoad[idx];
            }
            if (t.OnPitRoad[idx]) s.PitThisLap = true;

            if (s.PendingCrossing && idx < t.LastLap.Length && t.LastLap[idx] > 5
                && Math.Abs(t.LastLap[idx] - s.LastLapValue) > 0.0005f)
            {
                AddLap(s, t.LastLap[idx], s.PendingPit);
                s.LastLapValue = t.LastLap[idx];
                s.PendingCrossing = false;
            }

            // Track forward progress so a car parked on track (spin/crash) is detectable.
            if (_now >= 0 && idx < t.LapDistPct.Length && t.Lap[idx] >= 0)
            {
                double total = t.Lap[idx] + t.LapDistPct[idx];
                // ~0.0007 laps ≈ a few meters; a reset/tow jumps backwards, treat as movement.
                if (s.LastMoveTime < 0 || total >= s.LastTotalDist + 0.0007 || total < s.LastTotalDist - 0.5)
                {
                    s.LastTotalDist = total;
                    s.LastMoveTime = _now;
                }
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

    private static void AddLap(CarState s, float time, bool pit)
    {
        s.LapTimes.Add(time);
        s.PitLaps.Add(pit);
        if (s.LapTimes.Count > MaxLapTimes) { s.LapTimes.RemoveAt(0); s.PitLaps.RemoveAt(0); }
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

    /// <summary>All recorded laps for the car, oldest first (capped at 30). -1 = lap with no time.</summary>
    public IReadOnlyList<float> LapTimesFor(int idx) =>
        _cars.TryGetValue(idx, out var s) ? s.LapTimes : [];

    /// <summary>Completed timed laps for the car.</summary>
    public int LapCount(int idx) =>
        _cars.TryGetValue(idx, out var s) ? s.LapTimes.Count(x => x > 0) : 0;

    /// <summary>Average of the car's last <paramref name="n"/> clean laps (timed, no pit lane), or null.</summary>
    public float? RecentPace(int idx, int n = 5)
    {
        var clean = CleanLaps(idx);
        if (clean.Count < Math.Min(n, 3)) return null;
        return clean.TakeLast(n).Average();
    }

    private List<float> CleanLaps(int idx)
    {
        if (!_cars.TryGetValue(idx, out var s)) return [];
        var result = new List<float>(s.LapTimes.Count);
        for (int i = 0; i < s.LapTimes.Count; i++)
            if (s.LapTimes[i] > 0 && !(i < s.PitLaps.Count && s.PitLaps[i]))
                result.Add(s.LapTimes[i]);
        return result;
    }

    /// <summary>
    /// True when the car has been stationary on track (not pit road) for a few seconds —
    /// almost always a spin, crash, or a car waiting for a tow.
    /// </summary>
    public bool LooksStopped(int idx) =>
        _now >= 0 && _cars.TryGetValue(idx, out var s)
        && !s.WasOnPit && s.LastLapSeen >= 1 && s.LastMoveTime >= 0
        && _now - s.LastMoveTime > 4.0;

    /// <summary>
    /// True when the car looks like it's fuel-saving: consistent laps well off its own best.
    /// Heuristic — traffic can trigger it too, which is why it's a small tag, not a headline.
    /// </summary>
    public bool LooksLikeFuelSaving(int idx)
    {
        if (!_isRace) return false;
        var timed = CleanLaps(idx);
        if (timed.Count < 5) return false;

        var recent = timed.TakeLast(4).ToList();
        float best = timed.Min();
        float avg = recent.Average();
        if (avg < best * 1.015f) return false;                    // pushing

        double spread = recent.Max() - recent.Min();
        return spread < best * 0.01f;                             // slow AND metronomic = saving
    }

    public void Reset() => _cars.Clear();
}
