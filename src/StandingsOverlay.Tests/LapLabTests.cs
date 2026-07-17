using StandingsOverlay.Data;
using Xunit;

namespace StandingsOverlay.Tests;

/// <summary>
/// SectorClock + LapLabTracker: split exactness over the demo track's non-uniform speed
/// profile, clean-lap rules (off-track, pit, teleport/active-reset), optimal composition,
/// and the session-best re-base. The clock is fed synthetic 60 Hz motion — the same
/// TrackPct curve the demo uses, so pct and time legitimately diverge mid-lap.
/// </summary>
public class LapLabTests
{
    private const double Hz = 60;

    private static SectorClock NewClock()
    {
        var clock = new SectorClock();
        clock.SetBoundaries([0f, 1f / 3f, 2f / 3f]);
        return clock;
    }

    /// <summary>Drive laps of the given durations at 60 Hz. Motion is linear in the time
    /// domain; LapDistPct follows DemoSource.TrackPct like the sim on a real road course.
    /// <paramref name="mutate"/> can inject surface/pit/teleport per sample.</summary>
    private static void Drive(SectorClock clock, double[] lapSeconds,
        Func<int, double, (int surf, bool pit, double jump)>? mutate = null)
    {
        double time = 100;                       // arbitrary session-time origin
        foreach (var (lapTime, lapIdx) in lapSeconds.Select((s, i) => (s, i)))
        {
            int steps = (int)Math.Round(lapTime * Hz);
            for (int k = 0; k < steps; k++)
            {
                double tf = (double)k / steps;
                var (surf, pit, jump) = mutate?.Invoke(lapIdx, tf) ?? (3, false, 0);
                if (jump != 0) tf = Math.Max(0, tf + jump);
                clock.Sample(DemoSource.TrackPct((float)tf), time, surf, pit);
                time += 1 / Hz;
            }
        }
        clock.Sample(0.0001f, time, 3, false);   // final S/F crossing closes the last lap
    }

    private static List<SectorLap> Laps(SectorClock clock)
    {
        var laps = new List<SectorLap>();
        clock.DrainInto(laps);
        return laps;
    }

    [Fact]
    public void SectorsSumToLapTime_AndMatchScriptedPace()
    {
        var clock = NewClock();
        Drive(clock, [25.0, 25.5, 24.8, 25.2]);
        var laps = Laps(clock);

        // First S/F crossing arms the clock, so lap 1 of the script is the arming lap.
        Assert.Equal(3, laps.Count);
        double[] expected = [25.5, 24.8, 25.2];
        for (int i = 0; i < laps.Count; i++)
        {
            Assert.Equal(expected[i], laps[i].LapTime, 2);            // ±10 ms at 60 Hz interp
            Assert.Equal(laps[i].LapTime, laps[i].Sectors.Sum(), 6);  // exact by construction
            Assert.Equal(LapDirt.Clean, laps[i].Dirt);
            Assert.Equal(3, laps[i].Sectors.Length);
            Assert.All(laps[i].Sectors, s => Assert.False(double.IsNaN(s)));
        }
    }

    [Fact]
    public void OffTrackFlagsTheSectorItHappenedIn()
    {
        var clock = NewClock();
        // Off-track mid-lap on script lap 2 (= timed lap 1): tf 0.45–0.55 → distance ≈ 0.5 → S2.
        Drive(clock, [25, 25, 25],
            (lap, tf) => (lap == 1 && tf is > 0.45 and < 0.55 ? 0 : 3, false, 0));
        var laps = Laps(clock);

        Assert.Equal(2, laps.Count);
        Assert.Equal(LapDirt.Off, laps[0].Dirt);
        Assert.Equal(1, laps[0].DirtSector);
        Assert.True(laps[0].SectorDirty[1]);
        Assert.False(laps[0].SectorDirty[0]);
        Assert.Equal(LapDirt.Clean, laps[1].Dirt);
    }

    [Fact]
    public void PitRoadDirtiesTheLap()
    {
        var clock = NewClock();
        Drive(clock, [25, 25, 25],
            (lap, tf) => (3, lap == 1 && tf > 0.9, 0));
        var laps = Laps(clock);

        Assert.Equal(LapDirt.Pit, laps[0].Dirt);
        Assert.Equal(LapDirt.Clean, laps[1].Dirt);
    }

    [Fact]
    public void TeleportAbandonsTheLap_NextFullLapIsClean()
    {
        var clock = NewClock();
        // Mid script-lap 2: jump a quarter lap back once — iRacing's active reset.
        bool jumped = false;
        Drive(clock, [25, 25, 25, 25], (lap, tf) =>
        {
            if (lap == 1 && tf > 0.6 && !jumped) { jumped = true; return (3, false, -0.25); }
            return (3, false, 0);
        });
        var laps = Laps(clock);

        // Script: lap 0 arms · lap 1 abandoned by the jump (the jump also rewinds the motion,
        // so the S/F that follows re-arms) · remaining full laps are timed clean.
        Assert.Equal(1, clock.ResetCount);
        Assert.True(laps.Count >= 2);
        Assert.All(laps, l => Assert.Equal(LapDirt.Clean, l.Dirt));
        Assert.All(laps, l => Assert.Equal(l.LapTime, l.Sectors.Sum(), 6));
    }

    [Fact]
    public void LeavingTheWorldAbandonsTheLap()
    {
        var clock = NewClock();
        Drive(clock, [25, 25, 25],
            (lap, tf) => (lap == 1 && tf is > 0.5 and < 0.6 ? -1 : 3, false, 0));
        var laps = Laps(clock);

        Assert.Equal(1, clock.TowCount);
        // Lap 1 vanished mid-lap → abandoned; the lap after its next S/F is timed clean.
        Assert.Single(laps);
        Assert.Equal(LapDirt.Clean, laps[0].Dirt);
    }

    [Fact]
    public void TrackerBuildsRows_SessionBestRebasesOlderLaps()
    {
        var rig = new Rig(1, "Offline Testing");
        rig.AddCar(0, 2, 25f);
        rig.Cfg.LapLab.HideSlowLaps = false;   // the 26.5 lap is 6% off — keep its full row
        var clock = NewClock();
        var tracker = new LapLabTracker();

        // Laps: 26.0 (best so far), 26.5, then 25.0 (new best → older rows re-base).
        Drive(clock, [25, 26.0, 26.5, 25.0]);
        var snap = tracker.Build(rig.Tick, clock, rig.Roster, new LapRefStore(), rig.Cfg);

        Assert.True(snap.Show);
        Assert.Equal(["S1", "S2", "S3"], snap.SectorHeaders);
        Assert.Equal(3, snap.Rows.Count);

        // Newest first; the 25.0 lap is the reference → its delta ≈ 0, best-marked.
        Assert.True(snap.Rows[0].IsSessionBest);
        Assert.StartsWith("ref best", snap.RefText);
        Assert.Equal(LapLabTracker.FmtDelta(0, 2), snap.Rows[0].Delta.Text);
        // The 26.5 lap re-based against 25.0 → +1.50; at 6% off that's in the ignore band (dim).
        Assert.Equal(LapLabTracker.FmtDelta(1.5, 2), snap.Rows[1].Delta.Text);
        Assert.Equal(4, snap.Rows[1].Delta.Sign);
        // The 26.0 lap (+1.00 = 4%) sits inside the heat range → red.
        Assert.Equal(LapLabTracker.FmtDelta(1.0, 2), snap.Rows[2].Delta.Text);
        Assert.Equal(1, snap.Rows[2].Delta.Sign);
    }

    [Fact]
    public void OptimalReferenceCombinesBestSectors()
    {
        var rig = new Rig(1, "Offline Testing");
        rig.AddCar(0, 2, 25f);
        rig.Cfg.LapLab.Reference = "SessionOptimal";
        var clock = NewClock();
        var tracker = new LapLabTracker();

        // Two clean laps with different sector strengths: optimal beats both totals.
        Drive(clock, [25, 25.0, 25.0], (lap, tf) => (3, false, 0));
        var snap = tracker.Build(rig.Tick, clock, rig.Roster, new LapRefStore(), rig.Cfg);

        Assert.True(snap.Show);
        Assert.StartsWith("ref optimal", snap.RefText);
        // Every sector delta vs the optimal must be >= 0 (within display rounding).
        foreach (var row in snap.Rows)
            foreach (var cell in row.Sectors)
                Assert.False(cell.Text.StartsWith('−') && cell.Sign == 2,
                    $"sector faster than optimal: {cell.Text}");
    }

    [Fact]
    public void OptimalUsesCleanSectorsFromDirtyLaps()
    {
        // iRacing's optimal composites best sectors from ANY valid lap — a fast S1 on a lap
        // that went off in S2 must count. Lap 3 is faster everywhere but dirty in S2.
        var rig = new Rig(1, "Offline Testing");
        rig.AddCar(0, 2, 25f);
        rig.Cfg.LapLab.Reference = "SessionOptimal";
        var clock = NewClock();
        Drive(clock, [25, 25.0, 24.0],
            (lap, tf) => (lap == 2 && tf is > 0.45 and < 0.55 ? 0 : 3, false, 0));

        var snap = new LapLabTracker().Build(rig.Tick, clock, rig.Roster, new LapRefStore(), rig.Cfg);
        Assert.StartsWith("ref optimal", snap.RefText);
        // The clean 25.0 lap (older row) must read RED in S1 — the optimal's S1 came from
        // the faster dirty lap. Under clean-laps-only semantics this cell was neutral.
        Assert.Equal(1, snap.Rows[1].Sectors[0].Sign);
    }

    [Fact]
    public void HeatBandsFollowPercentOfReference()
    {
        var c = new StandingsOverlay.Config.LapLabConfig();   // Good 1% · Full 2% · Ignore 4.5%
        Assert.Equal(0, LapLabTracker.HeatOf(0.4, c));         // on pace: quiet
        Assert.Equal(0, LapLabTracker.HeatOf(1.0, c));
        Assert.Equal(0.5f, LapLabTracker.HeatOf(1.5, c), 2);   // focus band ramps
        Assert.Equal(1f, LapLabTracker.HeatOf(2.0, c), 2);
        Assert.Equal(1f, LapLabTracker.HeatOf(3.5, c), 2);     // stays full red until Ignore
        Assert.True(float.IsNaN(LapLabTracker.HeatOf(4.5, c)));// mistake territory: dim, not red
        Assert.Equal(-0.5f, LapLabTracker.HeatOf(-0.5, c), 2); // gains ramp green
        Assert.Equal(-1f, LapLabTracker.HeatOf(-2.0, c), 2);
    }

    [Fact]
    public void SlowLapsCollapseToOneQuietLine()
    {
        var rig = new Rig(1, "Offline Testing");
        rig.AddCar(0, 2, 25f);
        var clock = NewClock();
        Drive(clock, [25, 25.0, 28.0]);   // 28.0 > 107% of 25.0

        var snap = new LapLabTracker().Build(rig.Tick, clock, rig.Roster, new LapRefStore(), rig.Cfg);
        var slow = snap.Rows[0];
        Assert.Equal("slow", slow.Status.Text);
        Assert.All(slow.Sectors, c => Assert.Equal("", c.Text));
        Assert.Equal("", slow.Delta.Text);

        rig.Cfg.LapLab.HideSlowLaps = false;
        var clock2 = NewClock();
        Drive(clock2, [25, 25.0, 28.0]);
        var snap2 = new LapLabTracker().Build(rig.Tick, clock2, rig.Roster, new LapRefStore(), rig.Cfg);
        Assert.NotEqual("", snap2.Rows[0].Sectors[0].Text);   // full row when the filter is off
    }

    [Fact]
    public void DirtyLap_DeltaAndStatusAreSeparateColumns()
    {
        var rig = new Rig(1, "Offline Testing");
        rig.AddCar(0, 2, 25f);
        var clock = NewClock();
        Drive(clock, [25, 25.0, 25.0],
            (lap, tf) => (lap == 2 && tf is > 0.45 and < 0.55 ? 0 : 3, false, 0));

        var snap = new LapLabTracker().Build(rig.Tick, clock, rig.Roster, new LapRefStore(), rig.Cfg);
        var dirty = snap.Rows[0];
        Assert.Equal("off S2", dirty.Status.Text);
        Assert.StartsWith("+", dirty.Delta.Text);   // the number survives alongside the reason
        Assert.True(dirty.TimeDim);
        Assert.Equal("", snap.Rows[1].Status.Text); // clean rows keep the column empty
    }

    [Fact]
    public void HiddenInRaces_EmptyBeforeBoundaries()
    {
        var rig = new Rig(1, "Race");
        rig.AddCar(0, 2, 25f);
        var clock = NewClock();
        var tracker = new LapLabTracker();
        Drive(clock, [25, 25]);

        Assert.False(tracker.Build(rig.Tick, clock, rig.Roster, new LapRefStore(), rig.Cfg).Show);

        var bare = new SectorClock();   // no boundaries yet (YAML not parsed)
        Assert.False(new LapLabTracker()
            .Build(new Rig(1, "Practice").Tick, bare, rig.Roster, new LapRefStore(), rig.Cfg).Show);
    }

    [Fact]
    public void YamlBoundariesParse()
    {
        const string yaml = """
            WeekendInfo:
             TrackName: suzuka gp
            SplitTimeInfo:
             Sectors:
             - SectorNum: 0
               SectorStartPct: 0.000000
             - SectorNum: 1
               SectorStartPct: 0.276145
             - SectorNum: 2
               SectorStartPct: 0.585417
            CarSetup:
             UpdateCount: 2
            """;
        var bounds = SectorClock.ParseBoundaries(yaml);
        Assert.Equal(3, bounds.Length);
        Assert.Equal(0f, bounds[0], 5);
        Assert.Equal(0.276145f, bounds[1], 5);
        Assert.Equal(0.585417f, bounds[2], 5);

        Assert.Empty(SectorClock.ParseBoundaries("WeekendInfo:\n TrackName: x\n"));
    }
}
