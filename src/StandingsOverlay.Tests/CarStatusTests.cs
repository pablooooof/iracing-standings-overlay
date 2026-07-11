using StandingsOverlay.Data;
using Xunit;

namespace StandingsOverlay.Tests;

public class CarStatusTests
{
    /// <summary>Drive a car's surface history through the StintTracker tick by tick.</summary>
    private static StintTracker Track(Rig r, int idx, params (double T, int Surface, bool Pit, double Progress)[] steps)
    {
        var stints = new StintTracker();
        foreach (var (time, surface, pit, progress) in steps)
        {
            r.Tick.SessionTime = time;
            r.Tick.TrackSurface[idx] = surface;
            r.Tick.OnPitRoad[idx] = pit;
            r.Place(idx, progress);
            stints.Update(r.Tick);
        }
        return stints;
    }

    private static CarStatus StatusOf(Rig r, StintTracker s, int idx) =>
        CarStatus.Of(r.Tick, s, idx, showRejoin: true, swapped: false, refLap: 120f, outLapStates: false);

    [Fact]
    public void TeleportedIntoStall_IsTow_DrivingIn_IsPit()
    {
        var r = new Rig(2, sessionType: "Race");
        r.AddCar(0, 2, 120f);
        r.AddCar(1, 2, 120f);

        // Car 1 tows: on track → straight into the pit stall, never "approaching pits".
        var towed = Track(r, 1,
            (100, 3, false, 10.40), (101, 3, false, 10.41), (102, 1, true, 10.0), (103, 1, true, 10.0));
        Assert.Equal("TOW", StatusOf(r, towed, 1).State);

        // Same car drives in normally: 3 → 2 (approaching) → 1 (stall) — that's a pit stop.
        var r2 = new Rig(2, sessionType: "Race");
        r2.AddCar(0, 2, 120f);
        r2.AddCar(1, 2, 120f);
        var pitted = Track(r2, 1,
            (100, 3, false, 10.95), (102, 2, true, 10.99), (104, 1, true, 11.0), (106, 1, true, 11.0));
        Assert.Equal("PIT", StatusOf(r2, pitted, 1).State);
    }

    [Fact]
    public void PracticeReset_IsNotTow()
    {
        // ESC in practice teleports to the stall the same way, but it is not a tow.
        var r = new Rig(2, sessionType: "Practice");
        r.AddCar(0, 2, 120f);
        r.AddCar(1, 2, 120f);
        var s = Track(r, 1,
            (100, 3, false, 10.40), (102, 1, true, 10.0), (104, 1, true, 10.0));
        Assert.Equal("PIT", StatusOf(r, s, 1).State);
    }

    [Fact]
    public void PlayerTowTime_IsAuthoritative()
    {
        var r = new Rig(2, sessionType: "Race");
        r.AddCar(0, 2, 120f);
        r.AddCar(1, 2, 120f);
        var s = Track(r, 0, (100, 3, false, 10.40));
        r.Tick.PlayerTowTime = 27.5f;
        Assert.Equal("TOW", StatusOf(r, s, 0).State);
    }

    [Fact]
    public void LongStoppedCarOnTrack_IsSpun_NeverTow()
    {
        // The old heuristic flipped SPUN → TOW after 15 s parked; every parked car got mislabeled.
        var r = new Rig(2, sessionType: "Race");
        r.AddCar(0, 2, 120f);
        r.AddCar(1, 2, 120f);
        var steps = new List<(double, int, bool, double)> { (100, 3, false, 10.40), (101, 3, false, 10.41) };
        for (double t = 102; t <= 140; t += 1)   // 38 s parked on track
            steps.Add((t, 3, false, 10.41));
        var s = Track(r, 1, steps.ToArray());
        Assert.Equal("SPUN", StatusOf(r, s, 1).State);
    }

    [Fact]
    public void ChannelsAreIndependent_AndCombinedFollowsPrecedence()
    {
        // Meatball car in the pit lane: DMG chip + PIT text simultaneously; the old single
        // badge showed DMG and hid that the car was pitting.
        var r = new Rig(2, sessionType: "Race");
        r.AddCar(0, 2, 120f);
        r.AddCar(1, 2, 120f);
        var s = Track(r, 1,
            (100, 3, false, 10.95), (102, 2, true, 10.99), (104, 1, true, 11.0));
        r.Tick.SessionFlags[1] = CarFlags.Repair;

        var status = StatusOf(r, s, 1);
        Assert.Equal("DMG", status.Penalty);
        Assert.Equal("PIT", status.State);
        // Text mode: paperwork (DMG) outranks the pit cycle…
        Assert.Equal("DMG", status.Combined);
        // …but never a physical safety state, and DQ outranks everything.
        Assert.Equal("TOW", new CarStatus("DMG", "TOW").Combined);
        Assert.Equal("SPUN", new CarStatus("BLK", "SPUN").Combined);
        Assert.Equal("DQ", new CarStatus("DQ", "TOW").Combined);
        Assert.Equal("PIT", new CarStatus("", "PIT").Combined);
    }
}
