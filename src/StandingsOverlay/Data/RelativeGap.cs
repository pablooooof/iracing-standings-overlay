namespace StandingsOverlay.Data;

/// <summary>
/// THE on-track gap model, shared by the relative box, the traffic alerter and the smoothed
/// standings gaps so every widget speaks the same numbers.
///
/// Each car is reduced to a lap PHASE in [0,1): how far around the track it is in TIME terms,
/// CarIdxEstTime / CarClassEstLapTime. EstTime is the sim's own position→time lookup along the
/// class reference lap (it knows a hairpin from a straight); dividing by the class lap time
/// removes the class scale so a GTP and a GT4 at the same physical spot get the same phase —
/// the same normalization irdashies uses, and what iRon's raw est delta implicitly assumes for
/// a single class. Gap seconds = wrapped(phaseA − phaseB) × refLap, where refLap is the pace of
/// whichever car is doing the closing (chaser's class lap for a car behind, the player's for a
/// car ahead) — callers choose the ruler, the phase delta is the single source of truth.
///
/// LapDistPct is only a per-car fallback (est reads 0 in a tow and can misbehave in the pits),
/// gated by how far the est phase strays from the distance phase — broken est values show up as
/// a huge skew. The two domains agree exactly at the S/F line, so a fallback never tears the gap
/// there; if either car of a pair falls back, both use pct so the pair stays in one domain.
/// Predecessor design (est/dist blend with an acceptance band) breathed with track section —
/// the band rejected the correct est gap whenever a slow section sat between the cars, and the
/// domain flip-flop produced phantom 0.7+ s/s closing rates (live, 2026-07-08/10).
/// Spec: docs/RELATIVE.md.
/// </summary>
public static class RelativeGap
{
    /// <summary>Max legitimate divergence between time-phase and distance-pct, in laps — the
    /// track's speed-profile shape (straights vs hairpins) accounts for up to ~0.08 on road
    /// courses; anything beyond is a broken est value (pit zero, tear) and pct wins.</summary>
    private const float MaxShapeSkew = 0.12f;

    /// <summary>Wrapped track-position delta in laps, in (-0.5, +0.5], distance domain
    /// (LapDistPct). Positive = car ahead of the player. Use for lap parity and coarse
    /// windowing — it stays consistent with Lap + LapDistPct totals; use the phase methods
    /// below for anything shown as seconds.</summary>
    public static float SignedLaps(RawTick t, int carIdx) => SignedLaps(t, carIdx, t.PlayerCarIdx);

    /// <summary>Wrapped track-position delta in laps between any two cars, in (-0.5, +0.5].
    /// Positive = <paramref name="aIdx"/> is ahead of <paramref name="bIdx"/>.</summary>
    public static float SignedLaps(RawTick t, int aIdx, int bIdx)
    {
        float d = t.LapDistPct[aIdx] - t.LapDistPct[bIdx];
        if (d > 0.5f) d -= 1f;
        else if (d <= -0.5f) d += 1f;
        return d;
    }

    /// <summary>
    /// Wrapped lap-phase delta between two cars in (-0.5, +0.5], time domain. Positive =
    /// <paramref name="aIdx"/> is physically ahead. Multiply by a reference lap to get seconds;
    /// the sign is the definitive "ahead/behind on track" answer, shared by all widgets.
    /// </summary>
    public static float SignedPhase(RawTick t, Roster roster, int aIdx, int bIdx)
    {
        float pa = Phase(t, roster, aIdx, out bool ea);
        float pb = Phase(t, roster, bIdx, out bool eb);
        if (ea != eb)
        {
            // One side fell back to distance — keep the pair in a single domain, otherwise
            // the mixed error shows up as a phantom gap of the track-shape skew.
            pa = Frac(t.LapDistPct[aIdx]);
            pb = Frac(t.LapDistPct[bIdx]);
        }
        float d = pa - pb;
        if (d > 0.5f) d -= 1f;
        else if (d <= -0.5f) d += 1f;
        return d;
    }

    /// <summary>Signed gap in seconds between the player and <paramref name="carIdx"/> over
    /// <paramref name="refLap"/> — pass the pace of whichever car is closing (the chaser's
    /// class lap for a car behind, the player's for a car ahead). Positive = car ahead.</summary>
    public static float SignedSeconds(RawTick t, Roster roster, int carIdx, float refLap)
        => SignedPhase(t, roster, carIdx, t.PlayerCarIdx) * refLap;

    /// <summary>On-track gap in seconds between two arbitrary cars. Positive =
    /// <paramref name="aIdx"/> is ahead. Only valid within a lap — the caller handles
    /// laps-down separately.</summary>
    public static float SignedSecondsBetween(RawTick t, Roster roster, int aIdx, int bIdx, float refLap)
        => SignedPhase(t, roster, aIdx, bIdx) * refLap;

    /// <summary>The player's reference lap: class est lap, else own best, else a safe default.</summary>
    public static float PlayerRefLap(RawTick t, Roster roster)
    {
        if (roster.Drivers.TryGetValue(t.PlayerCarIdx, out var me) && me.ClassEstLap > 10)
            return me.ClassEstLap;
        if (t.BestLap[t.PlayerCarIdx] > 10) return t.BestLap[t.PlayerCarIdx];
        return 90f;
    }

    /// <summary>One car's lap phase in [0,1): est-time based when sane, LapDistPct otherwise.
    /// <paramref name="estValid"/> reports which domain was used.</summary>
    private static float Phase(RawTick t, Roster roster, int idx, out bool estValid)
    {
        estValid = false;
        float pct = Frac(idx < t.LapDistPct.Length ? t.LapDistPct[idx] : 0f);
        if (idx >= t.EstTime.Length) return pct;
        float e = t.EstTime[idx];
        if (e < 0f) return pct;
        if (!roster.Drivers.TryGetValue(idx, out var d) || d.ClassEstLap <= 10) return pct;
        float ep = e / d.ClassEstLap;
        if (ep >= 1.5f) return pct;   // garbage — est is positional, it tops out around one lap
        ep = Frac(ep);

        // No special-casing of est == 0: at the S/F line a zero est IS the correct value (and
        // must stay in the est domain — demoting one car of a pair to pct for a tick makes the
        // gap jump by the other car's shape skew). A pit/tow zero mid-lap shows up here as a
        // huge skew instead, and pct wins.
        float skew = ep - pct;
        if (skew > 0.5f) skew -= 1f;
        else if (skew <= -0.5f) skew += 1f;
        if (Math.Abs(skew) > MaxShapeSkew) return pct;

        estValid = true;
        return ep;
    }

    private static float Frac(float x) => x - MathF.Floor(x);
}
