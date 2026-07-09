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
    /// Signed gap in seconds over <paramref name="refLap"/> — callers pass the pace of whichever
    /// car is closing (the chaser's class lap for a car behind, the player's for a car ahead), so
    /// the relative box and the traffic alerter agree. The est-time refinement is shifted
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
                gap = Refine(ce - pe, gap, refLap);
        }
        return gap;
    }

    /// <summary>Wrapped track-position delta in laps between any two cars, in (-0.5, +0.5].
    /// Positive = <paramref name="aIdx"/> is ahead of <paramref name="bIdx"/>.</summary>
    public static float SignedLaps(RawTick t, int aIdx, int bIdx)
    {
        float d = t.LapDistPct[aIdx] - t.LapDistPct[bIdx];
        if (d > 0.5f) d -= 1f;
        else if (d <= -0.5f) d += 1f;
        return d;
    }

    /// <summary>On-track gap in seconds between two arbitrary cars (same est-time refinement as the
    /// player version). Positive = <paramref name="aIdx"/> is ahead. Only valid within a lap — the
    /// caller handles laps-down separately.</summary>
    public static float SignedSecondsBetween(RawTick t, int aIdx, int bIdx, float refLap)
    {
        float gap = SignedLaps(t, aIdx, bIdx) * refLap;
        if (aIdx < t.EstTime.Length && bIdx < t.EstTime.Length)
        {
            float ae = t.EstTime[aIdx], be = t.EstTime[bIdx];
            if (ae > 0.5f && be > 0.5f)
                gap = Refine(ae - be, gap, refLap);
        }
        return gap;
    }

    /// <summary>
    /// Blend the CarIdxEstTime difference (<paramref name="est"/>) with the uniform-speed distance
    /// estimate (<paramref name="dist"/>). Est-time knows a straight from a hairpin, so it should
    /// *shrink* the distance estimate (which over-reads on the straights) — but it must never
    /// *inflate* a close gap: iRacing's CarIdxEstTime can be scaled by a car's own (slower) lap,
    /// which would otherwise turn a 0.2s gap into 2s. So accept est only on the same side and when
    /// it doesn't grow the gap by more than a hair. Away from that, the distance estimate wins.
    /// </summary>
    private static float Refine(float est, float dist, float refLap)
    {
        // Unwrap the est difference across the start/finish line toward the distance estimate.
        if (est - dist > 0.5f * refLap) est -= refLap;
        else if (dist - est > 0.5f * refLap) est += refLap;

        if (Math.Sign(est) == Math.Sign(dist) &&
            Math.Abs(est) < 1.3f * Math.Abs(dist) + 0.3f)
            return est;
        return dist;
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
