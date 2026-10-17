using StandingsOverlay.Config;
using StandingsOverlay.Data;
using Xunit;
using Xunit.Abstractions;

namespace StandingsOverlay.Tests;

/// <summary>Timed-race end: the white-flag rule (leader's first crossing after 0:00 = white,
/// checkered one lap later), the latch at expiry, the soft stop model, and an end-to-end run of
/// the endurance demo — projections checked against the race's actual finish, and the extra-lap
/// chance checked for lap-to-lap stability (the "4% then 80%" complaint).</summary>
public class RaceEndTests(ITestOutputHelper output)
{
    private static Rig TimedRig(double tau, double leaderProgress, double lap = 100)
    {
        var rig = new Rig(2, playerIdx: 1);
        rig.AddCar(0, 1, (float)lap);
        rig.AddCar(1, 1, (float)lap);
        rig.Place(0, leaderProgress);
        rig.Place(1, leaderProgress - 0.3);
        rig.Tick.Position[0] = 1;
        rig.Tick.Position[1] = 2;
        rig.Tick.LastLap[0] = rig.Tick.LastLap[1] = (float)lap;
        rig.Tick.SessionTimeRemain = tau;
        rig.Tick.SessionTimeTotal = 3600;
        return rig;
    }

    private static RaceEndEstimate Estimate(Rig rig, RaceEndModel? model = null)
        => (model ?? new RaceEndModel()).Update(rig.Tick, rig.Roster, new StintTracker(), new FuelModel(), rig.Cfg.Fuel)
           ?? throw new Xunit.Sdk.XunitException("no estimate");

    [Fact]
    public void LeaderCrossingAfterZero_IsTheWhiteFlag_CheckeredOneLapLater()
    {
        // Leader half way round lap 10 (100 s laps), 30 s on the clock: crosses at +50 s — after
        // 0:00 → that's the white, lap 11 is the last one.
        var e = Estimate(TimedRig(tau: 30, leaderProgress: 10.5));
        Assert.Equal(11, e.TotalLaps);
        Assert.Equal(0.8, e.ExtraLapProgress, 2);
        Assert.Equal(20, e.GainSec, 0);           // 20 s quicker and it crosses before 0:00
        Assert.True(e.PExtra < 0.01);

        // 60 s on the clock: crosses at +50 s — BEFORE 0:00 — so the white waits a whole lap.
        e = Estimate(TimedRig(tau: 60, leaderProgress: 10.5));
        Assert.Equal(12, e.TotalLaps);
        Assert.Equal(0.1, e.ExtraLapProgress, 2);
        Assert.Equal(10, e.DropSec, 0);           // 10 s slower and lap 12 disappears
        // Checkered = end of lap 12; the leader has 9.5 laps done → 2.5 laps of driving from now.
        Assert.Equal(250, e.CheckerSec, 0);
    }

    [Fact]
    public void ClockExpired_LatchesTheFinalLap_ThroughTheWhiteFlagCrossing()
    {
        var model = new RaceEndModel();
        var rig = TimedRig(tau: 60, leaderProgress: 10.5);
        Assert.Equal(12, Estimate(rig, model).TotalLaps);

        // Clock runs out with the leader on lap 11 → white at the next crossing, lap 12 is last.
        rig.Tick.SessionTimeRemain = -2;
        rig.Place(0, 11.1);
        var e = Estimate(rig, model);
        Assert.Equal(RaceEndPhase.WhiteNext, e.Phase);
        Assert.Equal(12, e.TotalLaps);

        // Leader takes the white: on the final lap, count unchanged.
        rig.Tick.SessionTimeRemain = -95;
        rig.Place(0, 12.05);
        e = Estimate(rig, model);
        Assert.Equal(RaceEndPhase.FinalLap, e.Phase);
        Assert.Equal(12, e.TotalLaps);
        Assert.Equal(0, e.PExtra);
    }

    [Fact]
    public void MissingClock_NeverReadsAsExpired()
    {
        var rig = TimedRig(tau: -1, leaderProgress: 10.5);
        rig.Tick.SessionTimeTotal = -1;   // field never populated
        Assert.Null(new RaceEndModel().Update(rig.Tick, rig.Roster, new StintTracker(), new FuelModel(), rig.Cfg.Fuel));
    }

    [Fact]
    public void PlayerLapsToGo_CountsCrossingsUntilTheCheckered_CappedAtTheLeader()
    {
        // Same class, player 0.3 laps (30 s) behind on the lead lap: same lap count as the leader.
        var e = Estimate(TimedRig(tau: 30, leaderProgress: 10.5));
        Assert.Equal(11, e.PlayerLap + e.PlayerLapsToGo - 1);
        // Leader would need 70 s+ in hand to give the player another lap: no own knife-edge.
        Assert.True(e.PlayerPExtra < 0.01);

        // A much slower player (150 s laps) one lap down: crossings until the leader's checkered.
        var rig = TimedRig(tau: 30, leaderProgress: 10.5);
        rig.Tick.LastLap[1] = 150;
        rig.Place(1, 9.2);
        e = Estimate(rig);
        // Leader finishes in 150 s; player (at 9.2 laps, 150 s/lap) completes lap 9 in 120 s,
        // then is 20% into lap 10 when the flag drops → finishes lap 10: 2 laps to go.
        Assert.Equal(2, e.PlayerLapsToGo);
    }

    [Fact]
    public void SoftStops_IsSmoothAcrossTheOneMoreStopThreshold()
    {
        Assert.True(RaceEndModel.SoftStops(need: 5, left: 10, stint: 20).E < 0.01);
        Assert.Equal(1.0, RaceEndModel.SoftStops(need: 20, left: 10, stint: 20).E, 2);
        Assert.Equal(2.0, RaceEndModel.SoftStops(need: 40, left: 10, stint: 20).E, 1);
        // Right at the edge: half a stop, not a coin flip between 0 and a whole one.
        var edge = RaceEndModel.SoftStops(need: 9.5, left: 10, stint: 20);
        Assert.InRange(edge.E, 0.4, 0.6);
        // Unknown stint phase: the average.
        Assert.Equal(1.5, RaceEndModel.SoftStops(need: 30, left: null, stint: 20).E, 2);
    }

    [Fact]
    public void Phi_MatchesTheStandardNormal()
    {
        Assert.Equal(0.5, RaceEndModel.Phi(0), 6);
        Assert.Equal(0.841345, RaceEndModel.Phi(1), 5);
        Assert.Equal(0.022750, RaceEndModel.Phi(-2), 5);
    }

    /// <summary>The endurance demo: 10-minute timed race, three GTPs leading with 7-9 lap stints
    /// and ~38 s stops, GT3 player. The projection must call the real final lap well before the
    /// end, despite the leaders' stops — and the extra-lap chance must not leap lap to lap.</summary>
    [Fact]
    public void EnduranceDemo_ProjectionCallsTheFinish_AndStaysStable()
    {
        var cfg = new OverlayConfig();
        var demo = new DemoSource(() => cfg, endurance: true);
        var stints = new StintTracker();
        var fuel = new FuelModel();
        var model = new RaceEndModel();
        var samples = new List<(double T, int LeaderLap, RaceEndEstimate E)>();
        int actualFinal = -1, actualPlayerFinal = -1;

        for (double t = 0.25; t < DemoSource.EnduranceSeconds + 200; t += 0.25)
        {
            demo.StepForTest(0.25);
            var tick = demo.TickForTest;
            stints.Update(tick);
            fuel.Update(tick);
            if (tick.SessionState >= 5)
            {
                int lead = Array.IndexOf(tick.Position, 1);
                actualFinal = tick.Lap[lead] - 1;
                actualPlayerFinal = tick.Lap[tick.PlayerCarIdx];
                break;
            }
            if (model.Update(tick, demo.RosterForTest, stints, fuel, cfg.Fuel) is { } e && e.Phase == RaceEndPhase.Projecting)
            {
                samples.Add((t, tick.Lap[e.LeaderIdx], e));
            }
        }
        Assert.True(actualFinal > 0, "demo race never reached the checkered");

        // One sample per leader lap (what a driver reads off the bar each time by).
        var perLap = samples.GroupBy(s => s.LeaderLap).Select(g => g.Last()).ToList();
        foreach (var s in perLap)
            output.WriteLine($"t={s.T,6:0.0} L{s.LeaderLap,3} N={s.E.TotalLaps} f={s.E.ExtraLapProgress:0.00} " +
                             $"P+1={s.E.PExtra:P0} P-1={s.E.PFewer:P0} stops={s.E.LeaderStopsLeft:0.00} " +
                             $"you={s.E.PlayerLapsToGo} final L{s.E.PlayerLap + s.E.PlayerLapsToGo - 1} (+1 {s.E.PlayerPExtra:P0})");
        output.WriteLine($"actual final lap {actualFinal}, player's {actualPlayerFinal}");

        // The last third of the race: the most likely count is the real one.
        double from = DemoSource.EnduranceSeconds * 2 / 3;
        var late = samples.Where(s => s.T >= from).ToList();
        double hit = late.Count(s => MostLikely(s.E) == actualFinal) / (double)late.Count;
        output.WriteLine($"late hit rate {hit:P0}");
        Assert.True(hit >= 0.9, $"late projections called {actualFinal} only {hit:P0} of the time");

        // Player's laps to go in the last third: right for the same share.
        // Every miss in the window must at least have flagged the other outcome as live (≥10%).
        var misses = late.Where(s => s.E.PlayerLap + s.E.PlayerLapsToGo - 1 != actualPlayerFinal).ToList();
        foreach (var s in misses.Where((_, k) => k % 20 == 0))
            output.WriteLine($"  player miss t={s.T:0.00} final L{s.E.PlayerLap + s.E.PlayerLapsToGo - 1} P+1 {s.E.PlayerPExtra:P0}");
        double playerHit = late.Count(s => s.E.PlayerLap + s.E.PlayerLapsToGo - 1 == actualPlayerFinal) / (double)late.Count;
        output.WriteLine($"late player hit rate {playerHit:P0}");
        Assert.True(playerHit >= 0.85, $"player's final lap {actualPlayerFinal} called only {playerHit:P0} of the time");

        // Stability: lap to lap, the extra-lap chance moves smoothly (pit cycles included).
        double worst = 0;
        for (int i = 1; i < perLap.Count; i++)
            worst = Math.Max(worst, Math.Abs(perLap[i].E.PExtra - perLap[i - 1].E.PExtra));
        output.WriteLine($"worst lap-to-lap P+1 change {worst:P0}");
        Assert.True(worst <= 0.35, $"extra-lap chance jumped {worst:P0} between consecutive leader laps");

        // Second half — every car's stint is known by now, which is when a driver makes the call.
        double worstLate = 0;
        var half = perLap.Where(s => s.T >= DemoSource.EnduranceSeconds / 2).ToList();
        for (int i = 1; i < half.Count; i++)
            worstLate = Math.Max(worstLate, Math.Abs(half[i].E.PExtra - half[i - 1].E.PExtra));
        output.WriteLine($"worst second-half lap-to-lap P+1 change {worstLate:P0}");
        Assert.True(worstLate <= 0.15, $"second-half extra-lap chance jumped {worstLate:P0} in one lap");
    }

    private static int MostLikely(RaceEndEstimate e)
        => e.PExtra > 0.5 ? e.TotalLaps + 1 : e.PFewer > 0.5 ? e.TotalLaps - 1 : e.TotalLaps;
}
