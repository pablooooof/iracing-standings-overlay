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
}
