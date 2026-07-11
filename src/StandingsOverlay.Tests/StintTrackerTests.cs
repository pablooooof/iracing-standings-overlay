using StandingsOverlay.Data;
using Xunit;

namespace StandingsOverlay.Tests;

public class StintTrackerTests
{
    /// <summary>Drives one car (idx 1) through scripted laps and pit stops at 1 Hz-ish ticks.</summary>
    private sealed class Sim
    {
        public readonly Rig R = new(2, "Race");
        public readonly StintTracker St = new();
        public double Now = 100;
        public double Prog = 1.05;

        public Sim()
        {
            R.AddCar(0, 2, 100f);
            R.AddCar(1, 2, 100f);
            R.Place(0, 5.5);
            Tick(0.1, Prog, 3, false);   // first sight on lap 1 → stint boundaries trustworthy
        }

        public void Tick(double dt, double prog, int surf, bool pit)
        {
            Now += dt;
            Prog = prog;
            R.Place(1, prog);
            R.Tick.TrackSurface[1] = surf;
            R.Tick.OnPitRoad[1] = pit;
            R.Tick.SessionTime = Now;
            St.Update(R.Tick);
        }

        public void Laps(int n)
        {
            for (int i = 0; i < n; i++) Tick(100, Prog + 1, 3, false);
        }

        /// <summary>A driven pit visit sitting still for ~<paramref name="stationarySec"/>.</summary>
        public void Stop(int stationarySec)
        {
            double lap = Math.Floor(Prog);
            Tick(5, lap + 0.98, 2, true);                                    // through the cones
            for (int i = 0; i < stationarySec; i++) Tick(1, lap + 0.985, 1, true);
            Tick(1, lap + 0.99, 2, false);                                   // rolling out
            Tick(2, lap + 1.02, 3, false);                                   // back on track
        }

        public int Lap => (int)Math.Floor(Prog);
    }

    [Fact]
    public void DriverChangeBlink_MidStop_IsNotATow()
    {
        // Live 24h: during a driver change the car blinks out of the world (-1) for a second
        // or two and re-materializes in its stall — exactly the tow signature. It vanished
        // FROM the pit area though, so it must not read TOW (and the pit visit stays one visit).
        var sim = new Sim();
        sim.Laps(4);
        double lap = Math.Floor(sim.Prog);
        sim.Tick(5, lap + 0.98, 2, true);                                    // drives into pit
        for (int i = 0; i < 5; i++) sim.Tick(1, lap + 0.985, 1, true);       // stall
        sim.Tick(1, lap + 0.985, -1, false);                                 // blink out
        sim.Tick(1, lap + 0.985, -1, false);
        sim.Tick(1, lap + 0.985, 1, true);                                   // back in stall
        Assert.False(sim.St.WasTowedIn(sim.R.Tick, 1), "driver-change blink read as TOW");

        // And the blink didn't split the visit into a second pit stop.
        Assert.Equal(1, sim.St.PitStops(1));
    }

    [Fact]
    public void RealTow_FromTheTrack_StillReadsTow()
    {
        var sim = new Sim();
        sim.Laps(4);
        double p = sim.Prog;
        for (int i = 0; i < 4; i++) sim.Tick(1, p, 3, false);                // dead on track
        for (int i = 0; i < 3; i++) sim.Tick(1, p, -1, false);               // on the flatbed
        sim.Tick(1, Math.Floor(p) + 0.985, 1, true);                         // dropped in stall
        Assert.True(sim.St.WasTowedIn(sim.R.Tick, 1), "a real tow stopped reading TOW");
    }

    [Fact]
    public void TireInference_FuelOnlyStop_KeepsTireAgeCounting()
    {
        // Fuel-and-tires-separate rules: fuel time scales with laps burned, tires add ~12s.
        // Stop A (20s) sets the fuel baseline, stop B (32s) reads as tires, stop C (21s) as
        // fuel-only — so tire age keeps counting across C while stint laps reset.
        var sim = new Sim();
        sim.Laps(10);
        sim.Stop(20);            // A: no baseline yet → assumed tires; baseline learned
        sim.Laps(10);
        sim.Stop(32);            // B: +12s over the same fill → took tires
        sim.Laps(10);
        sim.Stop(21);            // C: ≈ fill time alone → NO tires
        sim.Laps(5);

        int lap = sim.Lap;
        int? stint = sim.St.StintLaps(1, lap);
        int? age = sim.St.TireAgeLaps(1, lap);
        Assert.NotNull(stint);
        Assert.NotNull(age);
        Assert.InRange(stint.Value, 5, 7);
        Assert.InRange(age.Value, 16, 18);                    // laps since stop B, across stop C
        Assert.True(age > stint + 2, "double-stint not flagged");   // the ST8+ condition
    }
}
