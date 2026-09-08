using System.Globalization;
using System.Linq;
using StandingsOverlay.Data;
using Xunit;

namespace StandingsOverlay.Tests;

public class RelativeBuilderTests
{
    /// <summary>A same-class car ~3 s behind that gains ~0.4 s/lap on the player must show the
    /// closing-rate cue as "a car behind is catching you" (amber, kind 1) with the right rate —
    /// the triage signal for a 60-car pack.</summary>
    [Fact]
    public void ClosingRate_CarBehindCatchingYou_ShowsAmberKindAndRate()
    {
        var r = new Rig(2);                 // player 0, car 1, same class, Race
        r.AddCar(0, 2, 120f);
        r.AddCar(1, 2, 120f);
        var h = new GapHistory();
        var stints = new StintTracker();
        var swap = new DriverSwapTracker();

        // Car 1 starts ~3 s behind and gains ~0.4 s/lap on the player.
        double playerProg = 5.0, carProg = 5.0 - 3.0 / 120;
        for (int lap = 0; lap < 7; lap++)
        {
            r.Place(0, playerProg);
            r.Place(1, carProg);
            r.Tick.SessionTime = 100 + lap * 120;
            h.Update(r.Tick, r.Roster);
            stints.Update(r.Tick);
            playerProg += 1.0;
            carProg += 1.0 + 0.4 / 120;     // car gains 0.4 s of the 120 s lap each time
        }

        var snap = RelativeBuilder.Build(r.Tick, r.Roster, stints, swap, h, r.Cfg);
        var row = snap.Rows.First(x => x.CarNumber == "#2");

        Assert.Equal(1, row.ClosingKind);   // behind + catching → amber
        Assert.InRange(double.Parse(row.ClosingText, CultureInfo.CurrentCulture), 0.2, 0.8);
    }

    /// <summary>A car holding station (matching pace) must NOT show a closing cue — otherwise the
    /// column would be noise in a pack. Blank means "not converging".</summary>
    [Fact]
    public void ClosingRate_CarHoldingStation_ShowsNothing()
    {
        var r = new Rig(2);
        r.AddCar(0, 2, 120f);
        r.AddCar(1, 2, 120f);
        var h = new GapHistory();
        var stints = new StintTracker();
        var swap = new DriverSwapTracker();

        double playerProg = 5.0, carProg = 5.0 - 3.0 / 120;
        for (int lap = 0; lap < 7; lap++)
        {
            r.Place(0, playerProg);
            r.Place(1, carProg);
            r.Tick.SessionTime = 100 + lap * 120;
            h.Update(r.Tick, r.Roster);
            stints.Update(r.Tick);
            playerProg += 1.0;
            carProg += 1.0;                 // exact same pace → gap constant
        }

        var snap = RelativeBuilder.Build(r.Tick, r.Roster, stints, swap, h, r.Cfg);
        var row = snap.Rows.First(x => x.CarNumber == "#2");

        Assert.Equal(0, row.ClosingKind);
        Assert.Equal("", row.ClosingText);
    }
}
