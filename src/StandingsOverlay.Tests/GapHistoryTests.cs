using StandingsOverlay.Data;
using Xunit;

namespace StandingsOverlay.Tests;

public class GapHistoryTests
{
    [Fact]
    public void CatchRate_AveragesCleanLaps_ExcludingPitSizedSwings()
    {
        // A same-class car gains ~2.4 s on the player every lap, except one lap where it pits
        // (a ~36 s swing). The catch rate must read the steady ~2.4, not the pit-polluted mean —
        // this is what the traffic alerter shows as "+N.Ns/lap".
        var r = new Rig(2);
        r.AddCar(0, 2, 120f);
        r.AddCar(1, 2, 120f);
        var h = new GapHistory();

        double playerProg = 5.0, carProg = 4.9;   // car ~12 s behind
        for (int lap = 0; lap < 10; lap++)
        {
            r.Place(0, playerProg);
            r.Place(1, carProg);
            r.Tick.SessionTime = 100 + lap * 120;
            h.Update(r.Tick, r.Roster);
            playerProg += 1.0;
            carProg += lap == 4 ? 1.0 - 0.3 : 1.0 + 0.02;   // lap 4: pit stop, loses 0.3 laps
        }

        float? rate = h.CatchRatePerLap(1);
        Assert.NotNull(rate);
        Assert.InRange(rate.Value, 1.0, 4.0);
    }

    [Fact]
    public void CatchRate_KeepsFastClassCatching_ThatAFixedCutWouldDiscard()
    {
        // A GTP catching ~18 s/lap (realistic multiclass) — every delta is "pit-sized" by a
        // naive fixed threshold, but it IS the pace. The median-relative band must keep it.
        var r = new Rig(2);
        r.AddCar(0, 2, 138f);
        r.AddCar(1, 1, 120f, "GTP");
        var h = new GapHistory();

        double playerProg = 5.0, carProg = 4.6;
        for (int lap = 0; lap < 8; lap++)
        {
            r.Place(0, playerProg);
            r.Place(1, carProg);
            r.Tick.SessionTime = 100 + lap * 138;
            h.Update(r.Tick, r.Roster);
            playerProg += 1.0;
            carProg += 1.0 + 18.0 / 138.0;   // gains ~18 s per player lap
        }

        float? rate = h.CatchRatePerLap(1);
        Assert.NotNull(rate);
        Assert.InRange(rate.Value, 14.0, 22.0);
    }
}
