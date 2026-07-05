namespace StandingsOverlay.Data;

/// <summary>One frame of raw per-car telemetry, already unboxed. Produced by a real or demo source.</summary>
public sealed class RawTick
{
    public int PlayerCarIdx;
    public int[] Position = [];
    public int[] Lap = [];
    public float[] LapDistPct = [];
    public float[] LastLap = [];
    public float[] BestLap = [];
    public float[] F2Time = [];
    public bool[] OnPitRoad = [];
    public double SessionTimeRemain = -1;
    public int SessionLapsRemain = -1;
    public string SessionType = "Race";

    public bool Has(int idx) => idx >= 0 && idx < Position.Length;
}

public sealed record DriverEntry(
    int CarIdx,
    string Name,
    string CarNumber,
    int IRating,
    string LicString,
    string LicColor,
    int CarClassId,
    string ClassColor,
    float ClassEstLap,
    bool IsPaceCar,
    bool IsSpectator);

/// <summary>Session-scoped info parsed from the session YAML (or fabricated by the demo source).</summary>
public sealed class Roster
{
    public Dictionary<int, DriverEntry> Drivers { get; } = new();
    public string TrackName = "";
    public double StrengthOfField;

    /// <summary>iRacing SoF formula over the given field.</summary>
    public void ComputeSof()
    {
        var irs = Drivers.Values.Where(d => !d.IsPaceCar && !d.IsSpectator && d.IRating > 0)
                                .Select(d => (double)d.IRating).ToList();
        if (irs.Count == 0) { StrengthOfField = 0; return; }
        const double ln2 = 0.6931471805599453;
        var sum = irs.Sum(ir => Math.Exp(-ir * ln2 / 1600.0));
        StrengthOfField = 1600.0 / ln2 * Math.Log(irs.Count / sum);
    }
}

public sealed record StandingsRow(
    int Position,
    string CarNumber,
    string Name,
    string IRatingText,
    string LicText,
    string LicColor,
    string ClassColor,
    string GapText,
    string IntervalText,
    string LastLapText,
    string DeltaText,
    int DeltaSign,          // -1 you gained (green), +1 you lost (red), 0 neutral
    bool IsPlayer,
    bool InPit,
    bool IsSeparator)
{
    public static readonly StandingsRow Separator =
        new(0, "", "···", "", "", "", "", "", "", "", "", 0, false, false, true);
}

public sealed record StandingsSnapshot(
    bool Connected,
    string HeaderLeft,      // e.g. "RACE"
    string HeaderMid,       // e.g. "SoF 2.4k"
    string HeaderRight,     // e.g. "12 laps" / "23:41"
    IReadOnlyList<StandingsRow> Rows)
{
    public static readonly StandingsSnapshot Disconnected =
        new(false, "STANDINGS", "", "waiting for iRacing…", []);
}
