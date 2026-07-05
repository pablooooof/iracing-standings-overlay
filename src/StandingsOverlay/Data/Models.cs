namespace StandingsOverlay.Data;

/// <summary>One frame of raw per-car telemetry, already unboxed. Produced by a real or demo source.</summary>
public sealed class RawTick
{
    public int PlayerCarIdx;
    public int[] Position = [];
    public int[] ClassPosition = [];
    public int[] Lap = [];
    public float[] LapDistPct = [];
    public float[] LastLap = [];
    public float[] BestLap = [];
    public float[] F2Time = [];
    public bool[] OnPitRoad = [];
    public int[] SessionFlags = [];      // CarIdxSessionFlags bitfield
    public int[] TireCompound = [];      // CarIdxTireCompound: 0 = dry, >=1 = wet (rain-enabled)
    public int[] TrackSurface = [];      // CarIdxTrackSurface (irsdk_TrkLoc): -1 = not in world
    public int CarLeftRight;             // irsdk_CarLeftRight: 0 off · 1 clear · 2 left · 3 right · 4 both · 5 two left · 6 two right
    public double SessionTimeRemain = -1;
    public double SessionTime = -1;      // elapsed
    public double SessionTimeTotal = -1;
    public int SessionLapsRemain = -1;
    public float TrackTemp = float.NaN;  // °C
    public float Precipitation = float.NaN; // 0..1
    public bool DeclaredWet;
    public int TrackWetness = -1;        // irsdk_TrackWetness: 1 dry … 7 extremely wet
    public int PlayerIncidents = -1;
    public string SessionType = "Race";

    public bool Has(int idx) => idx >= 0 && idx < Position.Length;
}

/// <summary>Per-car flag bits from CarIdxSessionFlags (irsdk_Flags).</summary>
public static class CarFlags
{
    public const int Black = 0x010000;
    public const int Disqualify = 0x020000;
    public const int Furled = 0x080000;      // warning (furled black)
    public const int Repair = 0x100000;      // meatball
}

public sealed record DriverEntry(
    int CarIdx,
    string Name,
    string CarNumber,
    int IRating,
    string LicString,
    string LicColor,
    int CarClassId,
    string ClassName,
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
        var irs = Drivers.Values.Where(d => !d.IsPaceCar && !d.IsSpectator && d.IRating > 1)
                                .Select(d => (double)d.IRating).ToList();
        if (irs.Count == 0) { StrengthOfField = 0; return; }
        const double ln2 = 0.6931471805599453;
        var sum = irs.Sum(ir => Math.Exp(-ir * ln2 / 1600.0));
        StrengthOfField = 1600.0 / ln2 * Math.Log(irs.Count / sum);
    }
}

/// <summary>One per-lap delta cell. Sign: -1 the player gained that lap (green), +1 lost (red).</summary>
public readonly record struct DeltaCell(string Text, int Sign);

public enum RowKind { Normal, Separator, ClassHeader }

public sealed record StandingsRow(
    RowKind Kind,
    string LapsText,
    string PosText,
    string PosGainedText,
    int PosGainedSign,       // -1 gained places (green), +1 lost (red)
    string CarNumber,
    string Name,             // also carries the class name for ClassHeader rows
    string IRatingText,
    string LicText,
    string LicColor,
    string ClassColor,
    int Tyre,                // -1 unknown/hidden, 0 dry, >=1 wet
    string GapText,
    string IntervalText,
    string BestLapText,
    int BestLapSign,         // 2 = class-best lap (purple)
    string LastLapText,
    IReadOnlyList<DeltaCell> DeltaCells,   // oldest lap first
    string StatusText,       // "" | "PIT" | "WRN" | "BLK" | "DMG" | "DQ"
    string StratText,        // expected pit lap / stops to end
    string PaceText,         // ▲ ▼ plus "S" when fuel-saving
    int PaceSign,
    bool IsPlayer)
{
    public static readonly StandingsRow Separator = Empty(RowKind.Separator) with { Name = "···" };

    public static StandingsRow ClassHeader(string className, string classColor) =>
        Empty(RowKind.ClassHeader) with { Name = className, ClassColor = classColor };

    public static StandingsRow Empty(RowKind kind) =>
        new(kind, "", "", "", 0, "", "", "", "", "", "", -1, "", "", "", 0, "", [], "", "", "", 0, false);
}

public enum SessionKind { Practice, Qualify, Race }

public sealed record StandingsSnapshot(
    bool Connected,
    SessionKind Kind,
    string HeaderLeft,      // e.g. "RACE"
    string HeaderMid,       // e.g. "SoF 2.4k · 31°C"
    string HeaderRight,     // e.g. "12 laps · 3x"
    IReadOnlyList<string> CellHeaders,   // "Δ-5"…"Δ-1" in race, "L1"…"L4" in quali
    IReadOnlyList<StandingsRow> Rows)
{
    public static readonly StandingsSnapshot Disconnected =
        new(false, SessionKind.Practice, "STANDINGS", "", "waiting for iRacing…", [], []);

    public static SessionKind KindOf(string sessionType) =>
        sessionType.Contains("Race", StringComparison.OrdinalIgnoreCase) ? SessionKind.Race
        : sessionType.Contains("Qual", StringComparison.OrdinalIgnoreCase) ? SessionKind.Qualify
        : SessionKind.Practice;
}
