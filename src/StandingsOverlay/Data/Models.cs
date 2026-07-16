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
    public float[] EstTime = [];         // CarIdxEstTime: time to reach current position on the class reference lap
    public int CarLeftRight;             // irsdk_CarLeftRight: 0 off · 1 clear · 2 left · 3 right · 4 both · 5 two left · 6 two right
    public double SessionTimeRemain = -1;
    public double SessionTime = -1;      // elapsed
    public double SessionTimeTotal = -1;
    public int SessionLapsRemain = -1;
    public int SessionLapsTotal = -1;    // from YAML SessionLaps; -1 = unlimited/unknown
    public float TrackTemp = float.NaN;  // °C
    public double TimeOfDay = -1;        // SessionTimeOfDay: in-sim local time, seconds since midnight
    public float WindVel = float.NaN;    // m/s
    public float WindDir = float.NaN;    // radians, compass bearing the wind blows FROM (0 = North)
    public float Precipitation = float.NaN; // 0..1
    public bool DeclaredWet;
    public int TrackWetness = -1;        // irsdk_TrackWetness: 1 dry … 7 extremely wet
    public int PlayerIncidents = -1;
    public float PlayerTowTime;          // PlayerCarTowTime: seconds of tow remaining (player only)
    public bool IsOnTrack = true;        // player physically in the car (false = teammate stint / garage / spectating)
    public int SessionState;             // irsdk_SessionState: 4 racing, 5 checkered, 6 cool down
    public string SessionType = "Race";

    // Player-only fuel telemetry (iRacing exposes no fuel data for other cars).
    public float PlayerFuelLevel = float.NaN;  // liters (kWh on electric content; math is identical)
    public bool PlayerInPitStall;              // refueling only happens here, not anywhere on pit road
    public int GlobalFlags;                    // SessionFlags bitfield (global green/yellow/caution…)
    public float TankCapacity = -1;            // usable liters: DriverCarFuelMaxLtr × DriverCarMaxFuelPct (BoP)

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
    string CarBrand,
    int CarClassId,
    string ClassName,
    string ClassColor,
    float ClassEstLap,
    bool IsPaceCar,
    bool IsSpectator);

/// <summary>Per-car line from the session YAML results — the only place laps completed before
/// the player joined (or after a car left the world) are recorded, and the official positions.
/// CAUTION: ClassPosition is kept raw and the YAML encodes it 0-BASED (class leader = 0),
/// unlike Position and the live CarIdxClassPosition (both 1-based). Prefer Position.</summary>
public readonly record struct SessionResult(
    float BestLap, float LastLap, int LapsComplete, int Position, int ClassPosition);

/// <summary>Maps a car screen name to a compact 3-letter brand code ("Ferrari 296 GT3" → "FER").</summary>
public static class Brands
{
    public static string Code(string? screenName)
    {
        if (string.IsNullOrWhiteSpace(screenName)) return "";
        var tok = screenName.Trim().Split(' ', '-')[0];
        return (tok.Length <= 3 ? tok : tok[..3]).ToUpperInvariant();
    }
}

/// <summary>Session-scoped info parsed from the session YAML (or fabricated by the demo source).</summary>
public sealed class Roster
{
    public Dictionary<int, DriverEntry> Drivers { get; } = new();
    public Dictionary<int, SessionResult> Results { get; } = new();
    /// <summary>False when Results were borrowed from an earlier session (e.g. quali grid
    /// shown before a race has results) — lap counts/last times don't apply then.</summary>
    public bool ResultsFromCurrentSession = true;
    public string TrackName = "";
    // Track/car identity + rubber, for Lap Lab's conditions guard and previous-best keys.
    public int TrackId;
    public string TrackConfig = "";
    public string PlayerCarPath = "";
    public string PlayerCarName = "";
    public string RubberState = "";
    public double StrengthOfField;                       // whole field
    public Dictionary<int, double> SofByClass { get; } = new();   // per CarClassId

    /// <summary>iRacing SoF formula, computed for the whole field and per class (multiclass
    /// SoF is per class/split, so the header shows the player's own class number).</summary>
    public void ComputeSof()
    {
        const double ln2 = 0.6931471805599453;
        static double Sof(List<double> irs)
        {
            if (irs.Count == 0) return 0;
            var sum = irs.Sum(ir => Math.Exp(-ir * ln2 / 1600.0));
            return 1600.0 / ln2 * Math.Log(irs.Count / sum);
        }
        var field = Drivers.Values.Where(d => !d.IsPaceCar && !d.IsSpectator && d.IRating > 1).ToList();
        StrengthOfField = Sof(field.Select(d => (double)d.IRating).ToList());
        SofByClass.Clear();
        foreach (var g in field.GroupBy(d => d.CarClassId))
            SofByClass[g.Key] = Sof(g.Select(d => (double)d.IRating).ToList());
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
    string CarBrand,
    string ClassColor,
    int Tyre,                // -1 unknown/hidden, 0 dry, >=1 wet
    int TyreSwitch,          // 0 none · +1 just switched to wets · -1 just switched to slicks (inline o→o)
    string GapText,
    string IntervalText,
    string BestLapText,
    int BestLapSign,         // 2 = class-best lap (purple)
    string LastLapText,
    IReadOnlyList<DeltaCell> DeltaCells,   // oldest lap first
    string StatusText,       // physical-state badge (TOW/SPUN/…/PIT), or the Combined pick in "Text" style
    string RankText,         // # fastest on track in class over the last 5 clean laps
    int RankSign,            // 2 = fastest (purple), -1 = top 3 (green), 0 = rest
    string StratText,        // expected pit lap / stops to end
    string PaceText,         // ▲ ▼ ► vs the player, plus "S" when fuel-saving
    int PaceSign,
    bool IsPlayer,
    bool Offline = false,    // not in the world (disconnected / retired) → dimmed name
    string PitLapText = "",   // last pit stop: lap number
    string PitTotalText = "", // last pit: total time on pit road (s)
    string PitDriveText = "", // last pit: pit-lane transit time (s)
    string PitStallText = "", // last pit: time sat stationary in the box (s)
    string PenaltyText = "",  // penalty flag chip (DQ/BLK/DMG/WRN); empty in "Text" status style
    string TyreAgeText = "",  // laps on the current tires: "42" · "42²" double-stint · "42³" triple
    int TyreAgeSign = 0)      // 1 fresh (green) · 2 multi-stinted rubber (amber) · 0 rest
{
    public static readonly StandingsRow Separator = Empty(RowKind.Separator) with { Name = "···" };

    public static StandingsRow ClassHeader(string className, string classColor) =>
        Empty(RowKind.ClassHeader) with { Name = className, ClassColor = classColor };

    public static StandingsRow Empty(RowKind kind) =>
        new(kind, "", "", "", 0, "", "", "", "", "", "", "", -1, 0, "", "", "", 0, "", [], "", "", 0, "", "", 0, false);
}

public enum SessionKind { Practice, Qualify, Race }

public sealed record StandingsSnapshot(
    bool Connected,
    SessionKind Kind,
    string HeaderLeft,      // e.g. "RACE"
    IReadOnlyList<string> HeaderGroups,  // grouped metric chips: time · track/field · weather
    IReadOnlyList<string> CellHeaders,   // "Δ-5"…"Δ-1" in race, "L1"…"L4" in quali
    IReadOnlyList<StandingsRow> Rows,
    string HeaderAlert = "",             // non-empty → flashing banner (dry→wet, tyre switch)
    bool WetDeclared = false)            // marshal declared wet (wet tyres allowed) → box indicator
{
    public static readonly StandingsSnapshot Disconnected =
        new(false, SessionKind.Practice, "STANDINGS", ["waiting for iRacing…"], [], []);

    public static SessionKind KindOf(string sessionType) =>
        sessionType.Contains("Race", StringComparison.OrdinalIgnoreCase) ? SessionKind.Race
        : sessionType.Contains("Qual", StringComparison.OrdinalIgnoreCase) ? SessionKind.Qualify
        : SessionKind.Practice;
}
