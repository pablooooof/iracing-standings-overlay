using StandingsOverlay.Config;
using StandingsOverlay.Data;
using Xunit;

namespace StandingsOverlay.Tests;

/// <summary>
/// Turn-zone detection (CornerMap) and the Lap Lab turn view: apex finding + merging on the
/// demo's synthetic speed trace, boundary sanity, the flat-trace fallback, and the tracker
/// producing T-columns whose splits telescope to the lap time.
/// </summary>
public class CornerMapTests
{
    private static float[] SynthGrid()
    {
        var g = new float[LapRef.GridSize];
        for (int k = 0; k < g.Length; k++) g[k] = DemoSource.SynthSpeed((float)k / g.Length);
        return g;
    }

    [Fact]
    public void DetectsZones_MergesTheCloseComplex()
    {
        var bounds = CornerMap.FromSpeed(SynthGrid());

        // Eight scripted apexes, two of which (0.475/0.487) form one complex → seven zones.
        Assert.Equal(7, bounds.Length);
        Assert.Equal(0f, bounds[0]);
        for (int i = 1; i < bounds.Length; i++)
            Assert.True(bounds[i] > bounds[i - 1], "boundaries must ascend");

        // Every scripted apex must fall inside exactly one zone; the merged pair shares one.
        float[] apexes = [0.06f, 0.19f, 0.335f, 0.475f, 0.487f, 0.60f, 0.74f, 0.88f];
        int ZoneOf(float pct)
        {
            int z = 0;
            while (z < bounds.Length - 1 && pct >= bounds[z + 1]) z++;
            return z;
        }
        Assert.Equal(ZoneOf(0.475f), ZoneOf(0.487f));
        Assert.Equal(7, apexes.Select(ZoneOf).Distinct().Count());
    }

    [Fact]
    public void PedalDetector_FindsRegions_MergesChicane()
    {
        var brake = new float[LapRef.GridSize];
        var thr = new float[LapRef.GridSize];
        for (int k = 0; k < brake.Length; k++)
            (brake[k], thr[k]) = DemoSource.SynthPedals((float)k / brake.Length);

        var bounds = CornerMap.FromPedals(brake, thr);
        Assert.Equal(8, bounds.Length);   // [0] + 7 braking onsets (0.475/0.487 stay one zone)
        Assert.Equal(0f, bounds[0]);
        for (int i = 1; i < bounds.Length; i++)
            Assert.True(bounds[i] > bounds[i - 1], "boundaries must ascend");

        int ZoneOf(float pct) { int z = 0; while (z < bounds.Length - 1 && pct >= bounds[z + 1]) z++; return z; }
        Assert.Equal(ZoneOf(0.475f), ZoneOf(0.487f));    // chicane holds together
        Assert.NotEqual(ZoneOf(0.487f), ZoneOf(0.60f));  // next corner is its own zone

        // Flat-out pedals (never braking, always full throttle) don't segment.
        Assert.Empty(CornerMap.FromPedals(new float[LapRef.GridSize],
                                          Enumerable.Repeat(1f, LapRef.GridSize).ToArray()));
    }

    [Fact]
    public void ZoneLabels_UseOfficialNumbers()
    {
        var names = new CornerMap.CornerNames
        {
            Corners =
            [
                new(0.056f, 1, "La Source"),
                new(0.100f, 2, "Eau Rouge"),
                new(0.115f, 3, "Raidillon"),
                new(0.345f, 5, "Les Combes"),
            ],
        };
        float[] bounds = [0f, 0.035f, 0.321f, 0.5f];
        Assert.Equal("·", names.ZoneLabel(bounds, 0));      // S/F straight fragment: no corner
        Assert.Equal("T1-3", names.ZoneLabel(bounds, 1));   // complex spans official T1..T3
        Assert.Equal("T5", names.ZoneLabel(bounds, 2));
        Assert.Equal("La Source", names.ZoneName(bounds, 1));
    }

    [Fact]
    public void FlatOrEmptyTraces_FallBack()
    {
        var flat = new float[LapRef.GridSize];
        Array.Fill(flat, 55f);
        Assert.Empty(CornerMap.FromSpeed(flat));
        Assert.Empty(CornerMap.FromSpeed([]));

        // One wide sine dip (the demo track's real motion profile) is not a corner map.
        var sine = new float[LapRef.GridSize];
        for (int k = 0; k < sine.Length; k++)
            sine[k] = 50f + 20f * MathF.Sin(2 * MathF.PI * k / sine.Length);
        Assert.True(CornerMap.FromSpeed(sine).Length < CornerMap.MinZones);
    }

    [Fact]
    public void TurnView_TColumns_SplitsTelescopeToLapTime()
    {
        var rig = new Rig(1, "Offline Testing");
        rig.AddCar(0, 2, 25f);
        rig.Cfg.LapLab.View = "Turns";

        var clock = new SectorClock();
        clock.SetBoundaries([0f, 1f / 3f, 2f / 3f]);
        // Drive with the synthetic corner-profile speed so the session best carries a trace.
        double time = 100;
        foreach (double lapTime in new[] { 25.0, 25.0, 25.3 })
        {
            int steps = (int)Math.Round(lapTime * 60);
            for (int k = 0; k < steps; k++)
            {
                float pct = DemoSource.TrackPct((float)k / steps);
                clock.Sample(pct, time, 3, false, DemoSource.SynthSpeed(pct));
                time += 1.0 / 60;
            }
        }
        clock.Sample(0.0001f, time, 3, false, 60f);

        var tracker = new LapLabTracker();
        var snap = tracker.Build(rig.Tick, clock, rig.Roster, new LapRefStore(), rig.Cfg);

        Assert.True(snap.Show);
        Assert.Equal(7, snap.SectorHeaders.Count);
        Assert.Equal("T1", snap.SectorHeaders[0]);
        Assert.Equal("T7", snap.SectorHeaders[6]);
        Assert.Equal(7, snap.Rows[0].Sectors.Count);

        // Sanity via the underlying math: any lap's zone splits telescope to its lap time.
        var bounds = CornerMap.FromSpeed(SynthGrid());
        var grid = new float[LapRef.GridSize];
        for (int k = 0; k < grid.Length; k++) grid[k] = k * 25f / LapRef.GridSize;
        Assert.Equal(25.0, LapRef.SplitsOf(grid, 25.0, bounds).Sum(), 6);
    }

    [Fact]
    public void TurnView_NoSpeedTrace_FallsBackToSectors()
    {
        var rig = new Rig(1, "Offline Testing");
        rig.AddCar(0, 2, 25f);
        rig.Cfg.LapLab.View = "Turns";

        var clock = new SectorClock();
        clock.SetBoundaries([0f, 1f / 3f, 2f / 3f]);
        double time = 100;
        for (int lap = 0; lap < 3; lap++)                     // no speed passed at all
            for (int k = 0; k < 1500; k++)
            {
                clock.Sample(DemoSource.TrackPct(k / 1500f), time, 3, false);
                time += 1.0 / 60;
            }
        clock.Sample(0.0001f, time, 3, false);

        var snap = new LapLabTracker().Build(rig.Tick, clock, rig.Roster, new LapRefStore(), rig.Cfg);
        Assert.Equal(["S1", "S2", "S3"], snap.SectorHeaders);
    }
}
