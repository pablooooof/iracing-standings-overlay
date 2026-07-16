using System.IO;
using System.Text;
using StandingsOverlay.Data;
using Xunit;

namespace StandingsOverlay.Tests;

/// <summary>
/// Phase 2 of Lap Lab: the reference model (grid interpolation, sector derivation), the
/// conditions guard severities, previous-best persistence rules, the .ibt reader against a
/// synthetic file, and the tracker's external-reference selection with block fallback.
/// </summary>
public class LapRefTests
{
    // ---- LapRef grid ------------------------------------------------------

    [Fact]
    public void SectorsForInterpolatesTheGrid()
    {
        // Linear time-at-pct (100 s lap at constant speed) → equal thirds.
        var grid = new float[LapRef.GridSize];
        for (int k = 0; k < grid.Length; k++) grid[k] = k * 100f / LapRef.GridSize;
        var lapRef = new LapRef { LapTime = 100, TimeAtPct = grid };

        var s = lapRef.SectorsFor([0f, 1f / 3f, 2f / 3f]);
        Assert.Equal(3, s.Length);
        Assert.Equal(100.0 / 3, s[0], 2);
        Assert.Equal(100.0 / 3, s[1], 2);
        Assert.Equal(100.0 / 3, s[2], 2);
        Assert.Equal(100.0, s.Sum(), 3);   // sectors always telescope to the lap total
    }

    // ---- conditions guard -------------------------------------------------

    private static RefConditions Cond(int trackId = 5, string config = "Grand Prix",
        string car = "porsche992cup", float trackTemp = 30f, int wetness = 1,
        float wind = 2f, string rubber = "high usage") =>
        new(trackId, config, "Testville", car, "Porsche Cup", trackTemp, 25f, wind, wetness, rubber, "2026-07-12");

    private static (Roster roster, RawTick tick) Live()
    {
        var rig = new Rig(1, "Offline Testing");
        rig.AddCar(0, 2, 25f);
        rig.Roster.TrackId = 5;
        rig.Roster.TrackConfig = "Grand Prix";
        rig.Roster.PlayerCarPath = "porsche992cup";
        rig.Roster.RubberState = "high usage";
        rig.Tick.TrackTemp = 30f;
        rig.Tick.TrackWetness = 1;
        rig.Tick.WindVel = 2f;
        return (rig.Roster, rig.Tick);
    }

    [Fact]
    public void GuardSeverities()
    {
        var (roster, tick) = Live();

        Assert.Equal(-1, RefGuard.Diff(Cond(), roster, tick, 4).Severity);
        Assert.Equal(2, RefGuard.Diff(Cond(trackId: 9), roster, tick, 4).Severity);
        Assert.Equal(2, RefGuard.Diff(Cond(config: "Endurance"), roster, tick, 4).Severity);
        Assert.Equal(2, RefGuard.Diff(Cond(car: "ferrari296"), roster, tick, 4).Severity);
        Assert.Equal(1, RefGuard.Diff(Cond(wetness: 4), roster, tick, 4).Severity);       // ref was wet
        Assert.Equal(1, RefGuard.Diff(Cond(trackTemp: 24.5f), roster, tick, 4).Severity); // Δ5.5 °C
        Assert.Equal(0, RefGuard.Diff(Cond(wind: 8f), roster, tick, 4).Severity);
        Assert.Equal(0, RefGuard.Diff(Cond(rubber: "low usage"), roster, tick, 4).Severity);

        // Unknown reference values never fire (imports with sparse YAML stay usable).
        Assert.Equal(-1, RefGuard.Diff(Cond(trackTemp: float.NaN, wind: float.NaN, wetness: -1),
                                       roster, tick, 4).Severity);
    }

    [Fact]
    public void UnitSuffixParses()
    {
        Assert.Equal(39.79f, RefGuard.ParseUnit("39.79 C"), 2);
        Assert.Equal(1.34f, RefGuard.ParseUnit("1.34 m/s"), 2);
        Assert.True(float.IsNaN(RefGuard.ParseUnit(null)));
        Assert.True(float.IsNaN(RefGuard.ParseUnit("wet")));
    }

    // ---- previous-best persistence ----------------------------------------

    private static LapRef MakeRef(double lapTime, RefConditions? cond = null)
    {
        var grid = new float[LapRef.GridSize];
        for (int k = 0; k < grid.Length; k++) grid[k] = (float)(k * lapTime / LapRef.GridSize);
        return new LapRef { Source = "prev", LapTime = lapTime, TimeAtPct = grid, Conditions = cond };
    }

    [Fact]
    public void PrevBestOnlyImprovesOnDisk()
    {
        string key = "test-" + Guid.NewGuid().ToString("N");

        // NaN condition fields (unknown air temp on own saves) must survive the JSON round trip.
        var store = new LapRefStore();
        store.SavePrev(key, MakeRef(25.0, Cond(trackTemp: float.NaN) with { AirTempC = float.NaN }));

        var reload = new LapRefStore();
        reload.EnsurePrev(key);
        Assert.NotNull(reload.PrevRef);
        Assert.Equal(25.0, reload.PrevRef!.LapTime, 3);
        Assert.True(float.IsNaN(reload.PrevRef.Conditions!.AirTempC));

        // A slower "best" from a later session must never clobber the benchmark —
        // including from a fresh store that has not called EnsurePrev itself.
        new LapRefStore().SavePrev(key, MakeRef(26.0));
        var reload2 = new LapRefStore();
        reload2.EnsurePrev(key);
        Assert.Equal(25.0, reload2.PrevRef!.LapTime, 3);

        // A faster one does.
        new LapRefStore().SavePrev(key, MakeRef(24.2));
        var reload3 = new LapRefStore();
        reload3.EnsurePrev(key);
        Assert.Equal(24.2, reload3.PrevRef!.LapTime, 3);
    }

    // ---- tracker integration: external refs --------------------------------

    private static SectorClock NewClock()
    {
        var clock = new SectorClock();
        clock.SetBoundaries([0f, 1f / 3f, 2f / 3f]);
        return clock;
    }

    private static void Drive(SectorClock clock, double[] lapSeconds)
    {
        double time = 100;
        foreach (var lapTime in lapSeconds)
        {
            int steps = (int)Math.Round(lapTime * 60);
            for (int k = 0; k < steps; k++)
            {
                clock.Sample(DemoSource.TrackPct((float)k / steps), time, 3, false);
                time += 1.0 / 60;
            }
        }
        clock.Sample(0.0001f, time, 3, false);
    }

    [Fact]
    public void PreviousBestReference_UsedAndRebased()
    {
        var (roster, tick) = Live();
        // Unique combo per run: the laps/ folder persists in the test bin dir, and SavePrev
        // only-improves — a stale file from an earlier run must not leak into this test.
        roster.TrackId = Math.Abs(Environment.TickCount % 1_000_000) + 1000;
        string key = LapRefStore.Key(roster);
        new LapRefStore().SavePrev(key, MakeRef(24.0, Cond(trackId: roster.TrackId)));   // yesterday: 24.0
        var store = new LapRefStore();   // today's session = a fresh store (PrevRef means "before this session")

        var cfg = new StandingsOverlay.Config.OverlayConfig();
        cfg.LapLab.Reference = "PreviousBest";
        var clock = NewClock();
        var tracker = new LapLabTracker();
        Drive(clock, [25, 25.0, 25.0]);

        var snap = tracker.Build(tick, clock, roster, store, cfg);
        Assert.True(snap.Show);
        Assert.StartsWith("ref prev", snap.RefText);
        Assert.True(snap.WarnSeverity <= 0, $"unexpected warn: {snap.WarnText}");
        // Driving ~25.0 vs a 24.0 reference → lap delta ≈ +1.0, red.
        Assert.Equal(1, snap.Rows[0].Delta.Sign);
    }

    [Fact]
    public void BlockedReferenceFallsBackToSessionBest()
    {
        var (roster, tick) = Live();
        roster.TrackId = Math.Abs(Environment.TickCount % 1_000_000) + 2_000_000;   // own run-unique combo
        string key = LapRefStore.Key(roster);
        new LapRefStore().SavePrev(key, MakeRef(24.0, Cond(trackId: roster.TrackId, car: "ferrari296")));
        var store = new LapRefStore();

        var cfg = new StandingsOverlay.Config.OverlayConfig();
        cfg.LapLab.Reference = "PreviousBest";
        cfg.LapLab.SaveSessionBest = false;   // keep the prepared file untouched
        var clock = NewClock();
        var tracker = new LapLabTracker();
        Drive(clock, [25, 25.0, 25.0]);

        var snap = tracker.Build(tick, clock, roster, store, cfg);
        Assert.Equal(2, snap.WarnSeverity);
        Assert.StartsWith("ref blocked", snap.WarnText);
        Assert.StartsWith("ref best", snap.RefText);   // fell back to the session best
    }

    // ---- .ibt reader --------------------------------------------------------

    [Fact]
    public void IbtReaderFindsBestLapAndConditions()
    {
        string path = Path.Combine(Path.GetTempPath(), $"laplab-test-{Guid.NewGuid():N}.ibt");
        try
        {
            WriteSyntheticIbt(path, lapSeconds: [25.0, 24.5, 25.2]);
            var lap = IbtLap.ReadBestLap(path, out var error);

            Assert.True(lap is not null, $"reader failed: {error}");
            // First S/F crossing arms the segmentation, so laps 2 and 3 are timed; best = 24.5.
            Assert.Equal(24.5, lap!.LapTime, 2);
            Assert.Equal(LapRef.GridSize, lap.TimeAtPct.Length);
            Assert.Equal(LapRef.GridSize, lap.SpeedAtPct.Length);

            var sectors = lap.SectorsFor([0f, 1f / 3f, 2f / 3f]);
            Assert.Equal(lap.LapTime, sectors.Sum(), 3);
            Assert.All(sectors, s => Assert.InRange(s, 3, 15));

            Assert.NotNull(lap.Conditions);
            Assert.Equal(123, lap.Conditions!.TrackId);
            Assert.Equal("Grand Prix", lap.Conditions.TrackConfig);
            Assert.Equal("porsche992cup", lap.Conditions.CarPath);
            Assert.Equal(39.5f, lap.Conditions.TrackTempC, 1);
        }
        finally { File.Delete(path); }
    }

    /// <summary>A minimal but structurally faithful .ibt: header, disk sub-header, four var
    /// headers (SessionTime/LapDistPct/Speed/Lap), embedded session YAML, then 60 Hz rows
    /// following the demo track's non-uniform TrackPct profile.</summary>
    private static void WriteSyntheticIbt(string path, double[] lapSeconds)
    {
        const string yaml = """
            ---
            WeekendInfo:
             TrackName: testville gp
             TrackID: 123
             TrackDisplayShortName: Testville
             TrackConfigName: Grand Prix
             TrackSurfaceTemp: 39.50 C
             TrackAirTemp: 26.00 C
             TrackWindVel: 1.30 m/s
             SubSessionID: 0
            SessionInfo:
             Sessions:
             - SessionNum: 0
               SessionType: Offline Testing
               SessionTrackRubberState: moderately high usage
            DriverInfo:
             DriverCarIdx: 0
             Drivers:
             - CarIdx: 0
               UserName: Test Driver
               CarPath: porsche992cup
               CarScreenName: Porsche 911 GT3 Cup (992)
               CarScreenNameShort: Porsche Cup
               CarClassID: 1
            ...
            """;
        byte[] yamlBytes = Encoding.UTF8.GetBytes(yaml);

        const int bufLen = 20;   // SessionTime d@0 · LapDistPct f@8 · Speed f@12 · Lap i@16
        int varHeaderOffset = 144;
        int sessionInfoOffset = varHeaderOffset + 4 * 144;
        int bufOffset = sessionInfoOffset + yamlBytes.Length;

        // Rows: 60 Hz motion, linear in time within each lap, pct via TrackPct.
        var rows = new List<(double t, float pct, float v, int lap)>();
        double time = 50;
        int lapNum = 0;
        foreach (var lapTime in lapSeconds)
        {
            int steps = (int)Math.Round(lapTime * 60);
            for (int k = 0; k < steps; k++)
            {
                rows.Add((time, DemoSource.TrackPct((float)k / steps), 50f, lapNum));
                time += 1.0 / 60;
            }
            lapNum++;
        }
        rows.Add((time, 0.0001f, 50f, lapNum));   // closing S/F crossing

        using var bw = new BinaryWriter(File.Create(path));
        // irsdk_header: 28 ints.
        int[] h = new int[28];
        h[0] = 2; h[2] = 60;
        h[4] = yamlBytes.Length; h[5] = sessionInfoOffset;
        h[6] = 4; h[7] = varHeaderOffset;
        h[8] = 1; h[9] = bufLen;
        h[12] = rows.Count; h[13] = bufOffset;
        foreach (int v in h) bw.Write(v);
        // irsdk_diskSubHeader.
        bw.Write((long)1770000000);          // sessionStartDate (unix)
        bw.Write(0.0); bw.Write(time);       // start/end session time
        bw.Write(lapSeconds.Length);         // lap count
        bw.Write(rows.Count);                // record count

        WriteVarHeader(bw, 5, 0, "SessionTime");
        WriteVarHeader(bw, 4, 8, "LapDistPct");
        WriteVarHeader(bw, 4, 12, "Speed");
        WriteVarHeader(bw, 2, 16, "Lap");

        bw.Write(yamlBytes);

        foreach (var (t, pct, v, lap) in rows)
        {
            bw.Write(t);
            bw.Write(pct);
            bw.Write(v);
            bw.Write(lap);
        }
    }

    private static void WriteVarHeader(BinaryWriter bw, int type, int offset, string name)
    {
        bw.Write(type);
        bw.Write(offset);
        bw.Write(1);            // count
        bw.Write(0);            // countAsTime + pad
        var buf = new byte[32 + 64 + 32];
        Encoding.ASCII.GetBytes(name).CopyTo(buf, 0);
        bw.Write(buf);
    }
}
