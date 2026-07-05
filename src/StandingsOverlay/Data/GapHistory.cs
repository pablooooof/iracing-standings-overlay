namespace StandingsOverlay.Data;

/// <summary>
/// Per-car history of the time gap between the player and every other car, sampled once per
/// player lap crossing. This is what powers the multi-lap delta column: delta over N laps is
/// one subtraction between two samples.
///
/// The gap is expressed as relative seconds, rel = (carTotalDist - playerTotalDist) * refLapTime,
/// where totalDist = lap + lapDistPct. Negative rel = car is behind the player. Distance-based
/// gaps are smooth across pit stops and don't depend on session-type-specific F2Time semantics.
/// A decrease in rel always means the player gained on that car, so sign conventions stay simple.
/// </summary>
public sealed class GapHistory
{
    private const int Capacity = 24; // laps of history per car; plenty for any sane DeltaLaps

    private readonly Dictionary<int, List<(int Lap, float Rel)>> _byCar = new();
    private int _lastPlayerLap = -1;

    /// <summary>Call once per tick; records a sample for every car when the player crosses the line.</summary>
    public void Update(RawTick t, Roster roster)
    {
        if (!t.Has(t.PlayerCarIdx)) return;

        int playerLap = t.Lap[t.PlayerCarIdx];
        if (playerLap <= _lastPlayerLap) return;

        // First observation: just latch the lap so we don't record a bogus mid-lap sample set.
        bool record = _lastPlayerLap >= 0;
        _lastPlayerLap = playerLap;
        if (!record) return;

        float refLap = RefLapTime(t, roster);
        double playerTotal = t.Lap[t.PlayerCarIdx] + t.LapDistPct[t.PlayerCarIdx];

        foreach (var d in roster.Drivers.Values)
        {
            if (d.IsPaceCar || d.IsSpectator || !t.Has(d.CarIdx) || d.CarIdx == t.PlayerCarIdx) continue;

            double carTotal = t.Lap[d.CarIdx] + t.LapDistPct[d.CarIdx];
            float rel = (float)((carTotal - playerTotal) * refLap);

            if (!_byCar.TryGetValue(d.CarIdx, out var list))
                _byCar[d.CarIdx] = list = new List<(int, float)>(Capacity);
            list.Add((playerLap, rel));
            if (list.Count > Capacity) list.RemoveAt(0);
        }
    }

    /// <summary>
    /// Gap change over the last <paramref name="laps"/> player laps for this car.
    /// Negative = the player gained that many seconds on the car. Null until enough history exists.
    /// </summary>
    public float? DeltaOver(int carIdx, int laps)
    {
        if (laps < 1 || !_byCar.TryGetValue(carIdx, out var list) || list.Count < 2) return null;

        var (nowLap, nowRel) = list[^1];
        int wantLap = nowLap - laps;

        // Exact lap preferred; tolerate one missing sample (e.g. sim hiccup) by taking the closest older one.
        for (int i = list.Count - 2; i >= 0; i--)
        {
            if (list[i].Lap == wantLap) return nowRel - list[i].Rel;
            if (list[i].Lap < wantLap)
                return list[i].Lap >= wantLap - 1 ? nowRel - list[i].Rel : null;
        }
        return null;
    }

    /// <summary>
    /// Gap change during each of the last <paramref name="laps"/> laps, oldest first.
    /// Element k covers lap (currentLap - laps + 1 + k); null where history is missing.
    /// Negative = the player gained on the car during that lap.
    /// </summary>
    public float?[] PerLapDeltas(int carIdx, int laps)
    {
        var result = new float?[laps];
        if (laps < 1 || !_byCar.TryGetValue(carIdx, out var list) || list.Count < 2) return result;

        int nowLap = list[^1].Lap;
        float? RelAt(int lap)
        {
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i].Lap == lap) return list[i].Rel;
            return null;
        }

        for (int k = 0; k < laps; k++)
        {
            int lap = nowLap - laps + 1 + k;
            var end = RelAt(lap);
            var start = RelAt(lap - 1);
            if (end is not null && start is not null) result[k] = end - start;
        }
        return result;
    }

    /// <summary>Estimated laps until the player catches this car (positive, capped), or null.</summary>
    public float? LapsToCatch(int carIdx, int laps)
    {
        var delta = DeltaOver(carIdx, laps);
        if (delta is null || delta >= -0.05f) return null; // not catching
        if (!_byCar.TryGetValue(carIdx, out var list) || list.Count == 0) return null;

        float rel = list[^1].Rel;
        if (rel <= 0) return null; // car is behind us

        float perLap = -delta.Value / laps;
        float lapsNeeded = rel / perLap;
        return lapsNeeded is > 0 and < 100 ? lapsNeeded : null;
    }

    public void Reset()
    {
        _byCar.Clear();
        _lastPlayerLap = -1;
    }

    private static float RefLapTime(RawTick t, Roster roster)
    {
        if (roster.Drivers.TryGetValue(t.PlayerCarIdx, out var me) && me.ClassEstLap > 10)
            return me.ClassEstLap;
        if (t.Has(t.PlayerCarIdx) && t.BestLap[t.PlayerCarIdx] > 10)
            return t.BestLap[t.PlayerCarIdx];
        return 90f;
    }
}
