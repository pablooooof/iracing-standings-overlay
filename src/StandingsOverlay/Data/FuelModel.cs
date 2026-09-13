namespace StandingsOverlay.Data;

/// <summary>
/// Player-side fuel measurement (iRacing exposes fuel only for the player's car). Samples
/// FuelLevel at the player's lap crossings, classifies each lap (green / yellow / pit-touching)
/// so consumption stats stay honest, and observes real pit stops to learn the fill rate and the
/// pit lane time loss the strategy planner charges per stop. Spec: docs/FUEL-STRATEGY.md.
/// </summary>
public sealed class FuelModel
{
    // irsdk_Flags: yellow, yellowWaving, caution, cautionWaving.
    private const int YellowBits = 0x8 | 0x100 | 0x4000 | 0x8000;
    private const double Alpha = 0.25;              // EWMA weight: last ~10 laps dominate
    private const double FallbackFillRate = 2.6;    // L/s, roughly GT3; used only pre-measurement

    // Lap sampling. Fuel is valid the instant the lap counter bumps; the lap *time* publishes
    // a beat later (same dance as StintTracker).
    private int _lastLap = int.MinValue;
    private float _lapStartFuel = float.NaN;
    private bool _primedMidLap;             // first observed crossing closes a *partial* lap — discard it
    private bool _touchedPit, _sawYellow;
    private bool _pendingTime, _pendingInOut;
    private float _lastLapValue = -999f;
    private bool _primedLapValue;

    // Consumption stats (liters per lap).
    private double _greenEwma = -1;
    private double _yellowEwma = -1;

    // Rolling per-lap usage of the last non-pit laps (green or yellow), newest last — feeds the
    // consumption table's Last / Last-5 / Last-10 rows and the "laps remaining" range. Kept small.
    private readonly List<double> _recent = new(24);
    // Current stint (since the last pit exit / session start): completed racing laps and the fuel
    // they burned — the honest, lap-quantised basis for the target-vs-actual tracker.
    private double _stintUsed;
    private int _stintLaps;

    // Pace (seconds, green laps only — the planner's lap-time base).
    private double _paceEwma = -1;

    // Pit stop observation.
    private float _prevFuel = float.NaN;
    private bool _wasInStall, _wasOnPitRoad;
    private double _fillStartTime = -1, _fillEndTime;
    private float _fillStartFuel, _fillEndFuel;
    private double _fillRate = -1;      // measured L/s
    private double _recentFill = -1;    // liters added at the last stop, pending pit-loss math
    private double _pitLoss = -1;       // measured (in+out lap) − 2×pace − fill time
    private bool _prevLapInOut;
    private double _prevInOutTime;

    // Completed stint boundaries (session time at pit exit) — the "already driven" bar segments.
    private readonly List<double> _stintBounds = new(64);

    /// <summary>Green-lap consumption EWMA (L/lap), -1 until the first clean lap.</summary>
    public double GreenPerLap => _greenEwma;
    /// <summary>Clean green laps observed — the planner gates on this for confidence.</summary>
    public int GreenLaps { get; private set; }
    /// <summary>Yellow-lap consumption EWMA, or 0.55 × green until two yellows are seen.</summary>
    public double YellowPerLap => _yellowEwma > 0 ? _yellowEwma : _greenEwma > 0 ? _greenEwma * 0.55 : -1;
    /// <summary>Player green-lap pace EWMA (s), -1 unknown.</summary>
    public double PaceSec => _paceEwma;
    /// <summary>Measured refuel rate (L/s), -1 until a ≥3 L fill has been watched.</summary>
    public double MeasuredFillRate => _fillRate;
    /// <summary>Measured time lost per pit cycle excluding fill time (s), -1 until a full stop is observed.</summary>
    public double MeasuredPitLoss => _pitLoss;
    /// <summary>Session times at which completed player stints started (pit exits).</summary>
    public IReadOnlyList<double> StintBounds => _stintBounds;

    /// <summary>Fuel used on the last completed non-pit lap (L), -1 until one is seen.</summary>
    public double LastPerLap => _recent.Count > 0 ? _recent[^1] : -1;
    /// <summary>Average burn over the last <paramref name="n"/> non-pit laps (L/lap), -1 if none yet.
    /// Uses fewer if fewer are available.</summary>
    public double RecentAvg(int n)
    {
        if (_recent.Count == 0 || n <= 0) return -1;
        int take = Math.Min(n, _recent.Count);
        double sum = 0;
        for (int i = _recent.Count - take; i < _recent.Count; i++) sum += _recent[i];
        return sum / take;
    }
    /// <summary>Average burn over the current stint (L/lap), -1 until a full racing lap completes.</summary>
    public double StintPerLap => _stintLaps > 0 ? _stintUsed / _stintLaps : -1;
    /// <summary>Completed racing laps in the current stint (since the last pit exit / session start).</summary>
    public int StintLaps => _stintLaps;
    /// <summary>The last non-pit lap usages, newest last — for the "laps remaining" best/worst range.</summary>
    public IReadOnlyList<double> RecentUsage => _recent;

    public void Update(RawTick t)
    {
        int p = t.PlayerCarIdx;
        if (!t.Has(p) || float.IsNaN(t.PlayerFuelLevel)) return;

        float fuel = t.PlayerFuelLevel;
        bool onPit = p < t.OnPitRoad.Length && t.OnPitRoad[p];
        int lap = p < t.Lap.Length ? t.Lap[p] : -1;

        if ((t.GlobalFlags & YellowBits) != 0) _sawYellow = true;
        if (onPit) _touchedPit = true;

        WatchRefuel(t, fuel);

        // Pit exit = a stint boundary for the past part of the strategy bars, and the start of a
        // fresh stint for the target tracker (laps/fuel counted from here).
        if (!onPit && _wasOnPitRoad && t.SessionTime >= 0)
        {
            _stintBounds.Add(t.SessionTime);
            _stintUsed = 0;
            _stintLaps = 0;
        }
        _wasOnPitRoad = onPit;

        // Lap crossing: sample fuel now, classify with the flags gathered over the lap.
        if (lap > _lastLap)
        {
            if (_lastLap == int.MinValue)
            {
                _primedMidLap = true;   // we joined somewhere mid-lap; the next crossing is partial
            }
            else if (lap > _lastLap + 1)
            {
                // Missed crossings (telemetry stall, PC hitch): the span since the last sample
                // is multiple laps, and a 2-lap burn slips under the per-lap sanity cap —
                // discard it and re-prime like a fresh join. Disconnects reset the whole model
                // at the source, so this only guards in-session gaps.
                _primedMidLap = true;
                _pendingTime = false;
            }
            else if (_primedMidLap)
            {
                _primedMidLap = false;  // partial lap closed — resample, record nothing
            }
            else if (!float.IsNaN(_lapStartFuel))
            {
                float used = _lapStartFuel - fuel;
                if (used > 0.05f && used < 60f)   // negative = refueled mid-lap; huge = tow/reset
                {
                    if (_touchedPit) { }          // in/out laps pollute the per-lap number
                    else
                    {
                        if (_sawYellow) _yellowEwma = Ewma(_yellowEwma, used);
                        else { _greenEwma = Ewma(_greenEwma, used); GreenLaps++; }
                        // Rolling table stats count every racing lap (green or yellow); only
                        // pit in/out laps are excluded above.
                        _recent.Add(used);
                        if (_recent.Count > 20) _recent.RemoveAt(0);
                        _stintUsed += used;
                        _stintLaps++;
                    }
                }
                _pendingTime = true;
                _pendingInOut = _touchedPit;
            }
            _lastLap = lap;
            _lapStartFuel = fuel;
            _touchedPit = onPit;
            _sawYellow = (t.GlobalFlags & YellowBits) != 0;
        }

        // The lap time lands late; it feeds pace and the pit-loss measurement.
        float lastLap = p < t.LastLap.Length ? t.LastLap[p] : -1;
        if (!_primedLapValue) { _lastLapValue = lastLap; _primedLapValue = true; }
        if (_pendingTime && lastLap > 5 && Math.Abs(lastLap - _lastLapValue) > 0.0005f)
        {
            _lastLapValue = lastLap;
            _pendingTime = false;
            OnTimedLap(lastLap, _pendingInOut);
        }

        _prevFuel = fuel;
    }

    /// <summary>Fuel rising in the pit stall → learn the fill rate; remember the fill size.</summary>
    private void WatchRefuel(RawTick t, float fuel)
    {
        if (t.PlayerInPitStall && !float.IsNaN(_prevFuel) && fuel > _prevFuel + 0.02f)
        {
            if (_fillStartTime < 0) { _fillStartTime = t.SessionTime; _fillStartFuel = _prevFuel; }
            _fillEndTime = t.SessionTime;
            _fillEndFuel = fuel;
        }
        if (!t.PlayerInPitStall && _wasInStall && _fillStartTime >= 0)
        {
            double added = _fillEndFuel - _fillStartFuel;
            double secs = _fillEndTime - _fillStartTime;
            if (added >= 3 && secs > 1)
                _fillRate = _fillRate < 0 ? added / secs : 0.5 * _fillRate + 0.5 * added / secs;
            if (added > 0.5) _recentFill = added;
            _fillStartTime = -1;
        }
        _wasInStall = t.PlayerInPitStall;
    }

    private void OnTimedLap(double sec, bool inOut)
    {
        if (!inOut)
        {
            _paceEwma = Ewma(_paceEwma, sec);
        }
        else if (_prevLapInOut && _recentFill > 0 && _paceEwma > 0)
        {
            // in-lap + out-lap vs two clean laps, minus the time the fill itself took:
            // what's left is the pit lane transit + stall service overhead.
            double fillRate = _fillRate > 0 ? _fillRate : FallbackFillRate;
            double loss = _prevInOutTime + sec - 2 * _paceEwma - _recentFill / fillRate;
            if (loss is > 3 and < 180)
                _pitLoss = _pitLoss < 0 ? loss : 0.5 * _pitLoss + 0.5 * loss;
            _recentFill = -1;
        }
        _prevLapInOut = inOut;
        if (inOut) _prevInOutTime = sec;
    }

    private static double Ewma(double cur, double sample) =>
        cur < 0 ? sample : cur + Alpha * (sample - cur);

    public void Reset()
    {
        _lastLap = int.MinValue;
        _lapStartFuel = float.NaN;
        _primedMidLap = false;
        _touchedPit = _sawYellow = _pendingTime = _pendingInOut = false;
        _lastLapValue = -999f;
        _primedLapValue = false;
        _greenEwma = _yellowEwma = _paceEwma = -1;
        GreenLaps = 0;
        _prevFuel = float.NaN;
        _wasInStall = _wasOnPitRoad = _prevLapInOut = false;
        _fillStartTime = -1;
        _fillRate = _recentFill = _pitLoss = -1;
        _prevInOutTime = 0;
        _stintBounds.Clear();
        _recent.Clear();
        _stintUsed = 0;
        _stintLaps = 0;
    }
}
