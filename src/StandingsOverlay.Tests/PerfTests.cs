using System.Diagnostics;
using StandingsOverlay.Data;
using Xunit;
using Xunit.Abstractions;

namespace StandingsOverlay.Tests;

public class PerfTests
{
    private readonly ITestOutputHelper _out;
    public PerfTests(ITestOutputHelper o) => _out = o;

    /// <summary>The overlay's one hard rule: never affect sim frame times. The per-tick BUILD
    /// (standings + relative + traffic) is the only part that scans the whole field — rendering is
    /// windowed, so it barely grows with grid size. This measures the full build at a 60-car grid
    /// with populated history/pace and guards it well under a frame budget (real is tens of µs).</summary>
    [Fact]
    public void FullBuildPipeline_At60Cars_IsWellUnderAFrameBudget()
    {
        const int N = 60;
        var r = new Rig(N, sessionType: "Race");
        for (int i = 0; i < N; i++) r.AddCar(i, classId: 2, classLap: 118f);

        var h = new GapHistory();
        var stints = new StintTracker();
        var swap = new DriverSwapTracker();
        var weather = new WeatherTracker();
        var traffic = new TrafficDetector();

        // A few laps so catch-rate and clean-lap pace are populated (the costlier path).
        for (int lap = 0; lap < 6; lap++)
        {
            for (int i = 0; i < N; i++) r.Place(i, lap + 5.0 - i * 0.015);   // strung out ~0.9 lap
            r.Tick.SessionTime = 100 + lap * 118;
            h.Update(r.Tick, r.Roster);
            stints.Update(r.Tick);
        }
        r.Tick.PlayerCarIdx = 30;   // mid-pack

        var cfg = r.Cfg;
        void Tick()
        {
            SnapshotBuilder.Build(r.Tick, r.Roster, h, stints, weather, swap, cfg);
            RelativeBuilder.Build(r.Tick, r.Roster, stints, swap, h, cfg);
            traffic.Update(r.Tick, r.Roster, h, stints, cfg);
        }

        for (int k = 0; k < 300; k++) Tick();   // warm up JIT

        const int iters = 5000;
        var sw = Stopwatch.StartNew();
        for (int k = 0; k < iters; k++) Tick();
        sw.Stop();

        double perTickMs = sw.Elapsed.TotalMilliseconds / iters;
        _out.WriteLine($"60-car full build pipeline: {perTickMs * 1000:0.0} µs/tick " +
                       $"({perTickMs:0.000} ms). At 10 Hz that's {perTickMs * 10:0.00}% of one second.");

        // Generous guard: even a 1 ms build at 10 Hz is 1% of a second and never touches the sim's
        // render thread. Real is far lower; this only trips on an accidental O(n^2) regression.
        Assert.True(perTickMs < 2.0, $"build too slow at 60 cars: {perTickMs:0.000} ms/tick");
    }
}
