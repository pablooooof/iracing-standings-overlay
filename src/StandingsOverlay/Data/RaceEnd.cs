namespace StandingsOverlay.Data;

/// <summary>How exposed the projected lap count is to changing by one.</summary>
public enum ExtraLapRisk { None, Safe, Borderline, Likely }

/// <summary>
/// Timed-race distance estimate. iRacing ends a timed race when the <b>overall leader</b>
/// crosses the start/finish line after the clock reaches zero — so the race length in laps is
/// set by the leader's pace and track position, not the player's. This projects the leader's
/// finishing lap, the player's own laps-to-go, and how close the result is to gaining or losing
/// one lap (the "do I need fuel for an extra lap?" knife-edge the driver asks on the last stint).
///
/// Pure: everything comes off <see cref="RawTick"/> (leader = CarIdxPosition 1, its LapDistPct
/// and last lap time) plus the player's measured pace. Returns null when there is no usable
/// leader/clock (practice, lap-limited race, warm-up).
/// </summary>
public readonly record struct RaceEndEstimate(
    int TotalLaps,            // leader's projected final lap number
    int LeaderLapsToGo,       // laps the leader still runs, including the current one
    int PlayerLapsToGo,       // laps the player still runs before taking the flag
    double CheckerSec,        // seconds from now until the leader takes the flag (>= time remaining)
    double GainMarginSec,     // leader improvement that would add a lap (small = extra lap likely)
    double DropMarginSec,     // leader slowdown that would drop the last lap
    ExtraLapRisk Risk)
{
    // A lap is "in play" when a small fraction of a lap of pace change flips the count.
    private const double LikelyFrac = 0.20;
    private const double BorderlineFrac = 0.12;

    public static RaceEndEstimate? Estimate(RawTick t, double playerPaceSec)
    {
        double tau = t.SessionTimeRemain;
        if (tau <= 0 || tau > 200 * 3600) return null;

        int leader = -1;
        for (int i = 0; i < t.Position.Length; i++)
            if (t.Position[i] == 1 &&
                (i >= t.TrackSurface.Length || t.TrackSurface[i] != -1)) { leader = i; break; }
        if (leader < 0) return null;

        double lp = leader < t.LastLap.Length && t.LastLap[leader] > 5 ? t.LastLap[leader]
                  : playerPaceSec > 5 ? playerPaceSec : 0;
        if (lp <= 0) return null;

        double leaderPct = leader < t.LapDistPct.Length ? Math.Clamp(t.LapDistPct[leader], 0, 0.999) : 0;
        int leaderLap = leader < t.Lap.Length ? t.Lap[leader] : 0;
        double timeToLine = (1 - leaderPct) * lp;

        // Crossings happen at timeToLine + j·lp; the flag falls at the first crossing on/after the
        // clock hits zero. So the leader runs (j+1) more laps and finishes at CheckerSec.
        int leaderToGo; double checker;
        if (tau <= timeToLine) { leaderToGo = 1; checker = timeToLine; }
        else
        {
            int j = (int)Math.Ceiling((tau - timeToLine) / lp - 1e-9);
            leaderToGo = j + 1;
            checker = timeToLine + j * lp;
        }

        // How much clock the leader has in hand within the current lap bucket. gain small = an
        // extra lap nearly fits (a touch more pace adds it); drop small = the last lap is fragile.
        double frac = tau <= timeToLine ? 0 : ((tau - timeToLine) / lp) % 1.0;
        double gain = tau <= timeToLine ? lp : (1 - frac) * lp;
        double drop = tau <= timeToLine ? tau : frac * lp;
        var risk = tau <= timeToLine ? ExtraLapRisk.None
                 : gain < LikelyFrac * lp ? ExtraLapRisk.Likely
                 : Math.Min(gain, drop) < BorderlineFrac * lp ? ExtraLapRisk.Borderline
                 : ExtraLapRisk.Safe;

        // Player's own laps to the flag: their crossings until the leader finishes, +1 for the lap
        // they are completing when the flag drops.
        double pp = playerPaceSec > 5 ? playerPaceSec : lp;
        int player = t.PlayerCarIdx;
        double playerPct = player >= 0 && player < t.LapDistPct.Length
            ? Math.Clamp(t.LapDistPct[player], 0, 0.999) : 0;
        double playerToLine = (1 - playerPct) * pp;
        int playerToGo = checker <= playerToLine
            ? 1 : (int)Math.Ceiling((checker - playerToLine) / pp - 1e-9) + 1;

        return new RaceEndEstimate(leaderLap + leaderToGo, leaderToGo, playerToGo,
                                   checker, gain, drop, risk);
    }
}
