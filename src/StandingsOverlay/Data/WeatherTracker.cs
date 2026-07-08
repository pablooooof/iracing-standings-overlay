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
    private const int WetThreshold = 3;     // TrackWetness >= 3 (damp) counts as "wet enough to flag"
    private const double FlashSec = 10;     // dry→wet flash stays lit this long

    private double _lastSample = -1;
    private float _tempPrev = float.NaN, _precipPrev = float.NaN;
    private int _prevWetness = -1;
    private double _turnedWetAt = -1;
    private double _now = -1;

    public int TempTrend { get; private set; }      // -1 falling · 0 steady · +1 rising
    public int PrecipTrend { get; private set; }
    /// <summary>True for a short window right after the track crossed dry→wet (or was declared wet).</summary>
    public bool JustTurnedWet => _turnedWetAt >= 0 && _now >= 0 && _now - _turnedWetAt <= FlashSec;

    public void Update(RawTick t)
    {
        double now = t.SessionTime;
        if (now < 0) return;
        _now = now;

        // Dry→wet edge: wetness crossing the damp threshold upward, or the marshal declaring wet.
        int wetness = t.DeclaredWet ? Math.Max(WetThreshold, t.TrackWetness) : t.TrackWetness;
        if (_prevWetness >= 0 && wetness >= WetThreshold && _prevWetness < WetThreshold)
            _turnedWetAt = now;
        if (wetness >= 0) _prevWetness = wetness;

        if (_lastSample < 0)
        {
            _lastSample = now;
            _tempPrev = t.TrackTemp;
            _precipPrev = t.Precipitation;
            return;
        }
        if (now - _lastSample < SampleSec) return;

        if (!float.IsNaN(t.TrackTemp) && !float.IsNaN(_tempPrev))
            TempTrend = t.TrackTemp > _tempPrev + TempEps ? 1 : t.TrackTemp < _tempPrev - TempEps ? -1 : 0;
        if (!float.IsNaN(t.Precipitation) && !float.IsNaN(_precipPrev))
            PrecipTrend = t.Precipitation > _precipPrev + PrecipEps ? 1
                        : t.Precipitation < _precipPrev - PrecipEps ? -1 : 0;

        _tempPrev = t.TrackTemp;
        _precipPrev = t.Precipitation;
        _lastSample = now;
    }

    public void Reset()
    {
        _lastSample = _turnedWetAt = _now = -1;
        _tempPrev = _precipPrev = float.NaN;
        _prevWetness = -1;
        TempTrend = PrecipTrend = 0;
    }
}
