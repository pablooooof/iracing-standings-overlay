namespace StandingsOverlay.Data;

/// <summary>
/// Samples track temperature and precipitation over time so the header can show a trend arrow
/// (rising / falling / steady). iRacing exposes only current conditions — there is no forecast in
/// the SDK telemetry or session YAML — so "is it getting wetter?" is inferred from our own
/// history. Also latches the dry→wet transition for a brief header flash. Owned by the source and
/// fed every tick; the actual comparison runs on a slow interval (weather moves slowly).
/// </summary>
public sealed class WeatherTracker
{
    private const double SampleSec = 20;    // temp/precip drift slowly; compare against ~20 s ago
    private const float TempEps = 0.15f;    // °C move below this reads as "steady"
    private const float PrecipEps = 0.01f;  // 1 %-point move below this reads as "steady"
    private const int WetThreshold = 3;     // TrackWetness >= 3 (damp) counts as "wet"

    private double _lastSample = -1;
    private float _tempPrev = float.NaN, _precipPrev = float.NaN;
    private int _prevWetness = -1;
    private double _now = -1;

    public int TempTrend { get; private set; }      // -1 falling · 0 steady · +1 rising
    public int PrecipTrend { get; private set; }
    /// <summary>Session time of the last dry↔wet transition (-1 = none). The alert duration is the
    /// caller's config, so it can outlive the tracker's own sampling.</summary>
    public double TransitionTime { get; private set; } = -1;
    /// <summary>Direction of that last transition: true = went wet, false = dried out.</summary>
    public bool TransitionToWet { get; private set; }

    public void Update(RawTick t)
    {
        double now = t.SessionTime;
        if (now < 0) return;
        _now = now;

        // Dry↔wet edge: wetness crossing the "damp" threshold either way (or the marshal declaring
        // wet forces the wet side). Record the time + direction; the header decides how long to show it.
        int wetness = t.DeclaredWet ? Math.Max(WetThreshold, t.TrackWetness) : t.TrackWetness;
        if (_prevWetness >= 0 && wetness >= 0)
        {
            bool wasWet = _prevWetness >= WetThreshold, isWet = wetness >= WetThreshold;
            if (isWet != wasWet) { TransitionTime = now; TransitionToWet = isWet; }
        }
        if (wetness >= 0) _prevWetness = wetness;

        if (_lastSample < 0)
        {
            _lastSample = now;
            _tempPrev = t.TrackTemp;
            _precipPrev = t.Precipitation;
            return;
        }
        if (now - _lastSample < SampleSec) return;

        // Latch the direction: an arrow persists until the value actually moves the *other* way,
        // rather than blinking off whenever a sample happens to be flat.
        if (!float.IsNaN(t.TrackTemp) && !float.IsNaN(_tempPrev))
        {
            if (t.TrackTemp > _tempPrev + TempEps) TempTrend = 1;
            else if (t.TrackTemp < _tempPrev - TempEps) TempTrend = -1;
        }
        if (!float.IsNaN(t.Precipitation) && !float.IsNaN(_precipPrev))
        {
            if (t.Precipitation > _precipPrev + PrecipEps) PrecipTrend = 1;
            else if (t.Precipitation < _precipPrev - PrecipEps) PrecipTrend = -1;
        }

        _tempPrev = t.TrackTemp;
        _precipPrev = t.Precipitation;
        _lastSample = now;
    }

    public void Reset()
    {
        _lastSample = _now = TransitionTime = -1;
        _tempPrev = _precipPrev = float.NaN;
        _prevWetness = -1;
        TempTrend = PrecipTrend = 0;
        TransitionToWet = false;
    }
}
