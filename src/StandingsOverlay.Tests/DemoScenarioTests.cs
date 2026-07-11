using StandingsOverlay.Config;
using StandingsOverlay.Data;
using Xunit;

namespace StandingsOverlay.Tests;

/// <summary>Drives the demo source's clock directly (no timer) and asserts the scripted
/// scenarios produce the right statuses through the real pipeline — the same code path the
/// live source uses.</summary>
public class DemoScenarioTests
{
    [Fact]
    public void DemoTowScript_ProducesTow_AndNormalPitsNever()
    {
        var cfg = new OverlayConfig();
        var demo = new DemoSource(() => cfg);
        var stints = new StintTracker();

        // The demo builds its roster in the constructor; drive 240 s at 4 Hz. Car idx 9 (#64)
        // spins every ~90 s and its third incident (t≈188–208) is a tow; everyone else makes
        // normal stops that pass through "approaching pits".
        bool sawSpun = false, sawTow = false, sawPit = false;
        var falseTows = new List<string>();
        for (double t = 0.25; t <= 240; t += 0.25)
        {
            demo.StepForTest(0.25);
            var tick = demo.TickForTest;
            stints.Update(tick);

            for (int i = 0; i < tick.Position.Length; i++)
            {
                var st = CarStatus.Of(tick, stints, i, showRejoin: true, swapped: false,
                                      refLap: 25f, outLapStates: false);
                if (i == 9)
                {
                    if (st.State == "SPUN") sawSpun = true;
                    if (st.State == "TOW" && t is > 185 and < 212) sawTow = true;
                }
                else
                {
                    if (st.State == "PIT") sawPit = true;
                    if (st.State == "TOW")
                        falseTows.Add($"car {i} at t={t:0.0}");
                }
            }
        }

        Assert.True(sawSpun, "car 9 never read SPUN during its scripted spins");
        Assert.True(sawTow, "car 9 never read TOW during its scripted tow window");
        Assert.True(sawPit, "no car ever read PIT — pit script broken");
        Assert.Empty(falseTows);   // the Pablo bug: normal stops/parked cars mislabeled TOW
    }

    [Fact]
    public void TowedClassRival_PinsIntoTheStandings_OnlyWhileInItsStall()
    {
        // Window of zero cars around the player: normally the player is the ONLY normal row in
        // the class, so #64 (car idx 9, GT3 like the player) can only appear via the tow pin —
        // and must vanish again once its scripted tow window (t≈188-208) ends.
        var cfg = new OverlayConfig
        {
            DriversAtTop = 0, DriversAhead = 0, DriversBehind = 0, MinLeadingCars = 0,
        };
        var demo = new DemoSource(() => cfg);
        var stints = new StintTracker();
        var history = new GapHistory();
        var weather = new WeatherTracker();
        var swap = new DriverSwapTracker();

        bool pinnedDuringTow = false;
        var pinnedOutsideTow = new List<string>();
        for (double t = 0.25; t <= 240; t += 0.25)
        {
            demo.StepForTest(0.25);
            var tick = demo.TickForTest;
            stints.Update(tick);
            history.Update(tick, demo.RosterForTest);
            var snap = SnapshotBuilder.Build(tick, demo.RosterForTest, history, stints, weather, swap, cfg);

            var tow = snap.Rows.FirstOrDefault(r => r.Kind == RowKind.Normal && r.CarNumber == "#64");
            if (tow is null) continue;
            if (t is > 185 and < 212 && tow.StatusText == "TOW") pinnedDuringTow = true;
            else if (t is < 185 or > 218) pinnedOutsideTow.Add($"t={t:0.0} status={tow.StatusText}");
        }

        Assert.True(pinnedDuringTow, "#64 never pinned into the standings during its tow");
        Assert.Empty(pinnedOutsideTow);
    }
}
