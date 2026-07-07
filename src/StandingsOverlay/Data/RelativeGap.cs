namespace StandingsOverlay.Data;

/// <summary>
/// Signed on-track gap between the player and another car, shared by the relative box and the
/// traffic alerter so both speak the same numbers. Positive = the car is physically ahead of
/// the player. Base value is the wrapped track-position delta × a reference lap time, refined
/// with CarIdxEstTime where it's sane (it maps position to time along the class reference lap,
/// so it knows a hairpin from a straight — same signal the sim's own relative uses).
/// Spec: docs/RELATIVE.md.
/// </summary>
public static class RelativeGap
{
    /// <summary>Wrapped track-position delta in laps, in (-0.5, +0.5]. Positive = car ahead.</summary>
    public static float SignedLaps(RawTick t, int carIdx)
    {
        float d = t.LapDistPct[carIdx] - t.LapDistPct[t.PlayerCarIdx];
        if (d > 0.5f) d -= 1f;
        else if (d <= -0.5f) d += 1f;
        return d;
    }

    /// <summary>
    /// Signed gap in seconds over <paramref name="refLap"/> (the traffic alerter passes the
    /// chaser's class lap, the relative box the player's). The est-time refinement is shifted
    /// by ±refLap to land nearest the distance-based gap (S/F line between the cars) and only
    /// accepted within 0.35 laps of it — est time reads 0 in the pits and can tear mid-crossing.
    /// </summary>
    public static float SignedSeconds(RawTick t, int carIdx, float refLap)
    {
        float gap = SignedLaps(t, carIdx) * refLap;
        if (t.PlayerCarIdx < t.EstTime.Length && carIdx < t.EstTime.Length)
        {
            float pe = t.EstTime[t.PlayerCarIdx], ce = t.EstTime[carIdx];
            if (pe > 0.5f && ce > 0.5f)
            {
                float est = ce - pe;
                if (est - gap > 0.5f * refLap) est -= refLap;
                else if (gap - est > 0.5f * refLap) est += refLap;
                if (Math.Abs(est - gap) < 0.35f * refLap) gap = est;
            }
        }
        return gap;
    }

    /// <summary>The player's reference lap: class est lap, else own best, else a safe default.</summary>
    public static float PlayerRefLap(RawTick t, Roster roster)
    {
        if (roster.Drivers.TryGetValue(t.PlayerCarIdx, out var me) && me.ClassEstLap > 10)
            return me.ClassEstLap;
        if (t.BestLap[t.PlayerCarIdx] > 10) return t.BestLap[t.PlayerCarIdx];
        return 90f;
    }
}
