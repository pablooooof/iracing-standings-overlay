using StandingsOverlay.Config;
using StandingsOverlay.Data;

namespace StandingsOverlay.Tests;

/// <summary>
/// A minimal synthetic session. Cars are placed by lap progress in the TIME domain
/// (laps completed + time-fraction of the current lap); LapDistPct follows the demo track's
/// non-uniform speed profile and EstTime follows the class positional curve — exactly how the
/// sim reports them, so distance-pct and time-phase legitimately diverge mid-lap.
/// </summary>
internal sealed class Rig
{
    public readonly RawTick Tick = new();
    public readonly Roster Roster = new();
    public readonly OverlayConfig Cfg = new();

    public Rig(int cars, string sessionType = "Race", int playerIdx = 0)
    {
        Tick.PlayerCarIdx = playerIdx;
        Tick.SessionType = sessionType;
        Tick.SessionTime = 100;
        Tick.SessionState = 4;
        Tick.Position = new int[cars];
        Tick.ClassPosition = new int[cars];
        Tick.Lap = new int[cars];
        Tick.LapDistPct = new float[cars];
        Tick.LastLap = new float[cars];
        Tick.BestLap = new float[cars];
        Tick.F2Time = new float[cars];
        Tick.OnPitRoad = new bool[cars];
        Tick.SessionFlags = new int[cars];
        Tick.TireCompound = new int[cars];
        Tick.TrackSurface = new int[cars];
        Tick.EstTime = new float[cars];
        Array.Fill(Tick.TrackSurface, 3);   // on track
    }

    public void AddCar(int idx, int classId, float classLap, string className = "GT3")
        => Roster.Drivers[idx] = new DriverEntry(
            CarIdx: idx, Name: $"Driver {idx}", CarNumber: (idx + 1).ToString(), IRating: 2000,
            LicString: "A 4.00", LicColor: "#0153DB", CarBrand: "FER",
            CarClassId: classId, ClassName: className, ClassColor: "#FF5FA8",
            ClassEstLap: classLap, IsPaceCar: false, IsSpectator: false);

    /// <summary>Place a car at a time-domain lap progress (e.g. 10.5 = lap 10, half the LAP TIME
    /// elapsed). Distance pct and est time derive from it like the sim's own signals.</summary>
    public void Place(int idx, double progress)
    {
        int lap = (int)Math.Floor(progress);
        float tf = (float)(progress - lap);
        Tick.Lap[idx] = lap;
        Tick.LapDistPct[idx] = DemoSource.TrackPct(tf);
        Tick.EstTime[idx] = tf * Roster.Drivers[idx].ClassEstLap;
    }
}
