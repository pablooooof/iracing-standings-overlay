using StandingsOverlay.Data;
using Xunit;

namespace StandingsOverlay.Tests;

public class RelativeGapTests
{
    [Fact]
    public void SameClass_GapIsExact_AtEverySectionOfTheTrack()
    {
        var r = new Rig(2);
        r.AddCar(0, 2, 120f);
        r.AddCar(1, 2, 120f);

        // 3 s behind in real (time) terms — the reported gap must read 3 s whether the pair is
        // on the straight, in the hairpin, or straddling it. The old distance/est blend breathed
        // between ~1.7 s and ~4.3 s here, inventing 0.7 s/s closing rates on a constant gap.
        foreach (double tf in new[] { 0.01, 0.05, 0.25, 0.5, 0.75, 0.95 })
        {
            r.Place(0, 10 + tf);
            r.Place(1, 10 + tf - 3.0 / 120);
            float gap = RelativeGap.SignedSeconds(r.Tick, r.Roster, 1, 120f);
            Assert.InRange(gap, -3.05, -2.95);
        }
    }

    [Fact]
    public void CrossClass_PhaseNormalizationRemovesTheClassScaleError()
    {
        var r = new Rig(2);
        r.AddCar(0, 3, 130f, "GT4");   // player
        r.AddCar(1, 1, 100f, "GTP");   // chaser, faster class

        // GTP 0.3 s behind the GT4 at mid-lap. Raw est delta reads ~-15 s here (est times live
        // on different class curves: 0.5×130=65 vs ~0.497×100=49.7) — the bug that once made a
        // 0.2 s gap show as 2 s. Phase normalization must read ~0.3 s.
        r.Place(0, 10.5);
        r.Place(1, 10.5 - 0.3 / 100);
        float behind = -RelativeGap.SignedSeconds(r.Tick, r.Roster, 1, 100f);
        Assert.InRange(behind, 0.25, 0.35);
    }

    [Fact]
    public void WrapAcrossStartFinish_StaysSmallAndSigned()
    {
        var r = new Rig(2);
        r.AddCar(0, 2, 120f);
        r.AddCar(1, 2, 120f);

        r.Place(0, 11.005);   // player just past the line
        r.Place(1, 10.980);   // car 3 s short of it
        float gap = RelativeGap.SignedSeconds(r.Tick, r.Roster, 1, 120f);
        Assert.InRange(gap, -3.5, -2.5);   // behind, not +117 s
    }

    [Fact]
    public void PitTowEstZero_FallsBackToDistanceForBothCars()
    {
        var r = new Rig(2);
        r.AddCar(0, 2, 120f);
        r.AddCar(1, 2, 120f);

        r.Place(0, 10.30);
        r.Place(1, 10.28);
        r.Tick.EstTime[1] = 0f;   // tow / no data

        float gap = RelativeGap.SignedSeconds(r.Tick, r.Roster, 1, 120f);
        float expected = (r.Tick.LapDistPct[1] - r.Tick.LapDistPct[0]) * 120f;
        Assert.Equal(expected, gap, 2);
    }

    [Fact]
    public void BrokenEst_BeyondTrackShapeSkew_IsRejected()
    {
        var r = new Rig(2);
        r.AddCar(0, 2, 120f);
        r.AddCar(1, 2, 120f);

        r.Place(0, 10.30);
        r.Place(1, 10.29);
        r.Tick.EstTime[1] = 0.7f * 120f;   // est claims the far side of the track

        float gap = RelativeGap.SignedSeconds(r.Tick, r.Roster, 1, 120f);
        float expected = (r.Tick.LapDistPct[1] - r.Tick.LapDistPct[0]) * 120f;
        Assert.Equal(expected, gap, 2);
    }

    [Fact]
    public void RelativeAndTrafficConventionsAgree_OneSharedPhase()
    {
        var r = new Rig(2);
        r.AddCar(0, 2, 120f);
        r.AddCar(1, 2, 120f);
        r.Place(0, 10.5);
        r.Place(1, 10.5 - 2.0 / 120);

        // The relative box shows the signed gap; the traffic detector negates the same call.
        float relative = RelativeGap.SignedSeconds(r.Tick, r.Roster, 1, 120f);
        float chaser = -RelativeGap.SignedSeconds(r.Tick, r.Roster, 1, 120f);
        Assert.Equal(-relative, chaser, 5);
        Assert.InRange(chaser, 1.95, 2.05);
    }
}

public class DemoTrackTests
{
    [Fact]
    public void TrackPct_IsMonotonic_AndBoundedByTheSkewGate()
    {
        float prev = -1e-3f;
        for (int i = 0; i <= 1000; i++)
        {
            float tf = i / 1000f;
            float pct = DemoSource.TrackPct(tf);
            Assert.True(pct > prev, $"not monotonic at tf={tf}");
            // The divergence the demo produces must stay inside RelativeGap's acceptance gate,
            // or the demo would silently exercise the fallback path instead of the est path.
            Assert.True(Math.Abs(pct - tf) < 0.12f, $"skew {pct - tf} too large at tf={tf}");
            prev = pct;
        }
        Assert.Equal(0f, DemoSource.TrackPct(0f), 4);
        Assert.Equal(1f, DemoSource.TrackPct(1f), 4);
    }
}
