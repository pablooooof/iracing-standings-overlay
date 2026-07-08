using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StandingsOverlay.Config;

public sealed class OverlayConfig
{
    // Window position (DIPs). Defaults mirror the classic top-left placement.
    public double X { get; set; } = 7;
    public double Y { get; set; } = 6;

    // Style
    public double Opacity { get; set; } = 0.75;
    public double FontSize { get; set; } = 14;
    public double Scale { get; set; } = 1.0;   // standings size multiplier (LayoutTransform)
    public string BackgroundColor { get; set; } = "#212129";
    public string AccentColor { get; set; } = "#00FFD0";
    public string HighlightColor { get; set; } = "#FF8800";

    // Layout: top N drivers + a window around the player (ahead = better positions).
    public int DriversAtTop { get; set; } = 3;
    public int DriversAhead { get; set; } = 5;
    public int DriversBehind { get; set; } = 3;
    public bool ShowColumnHeader { get; set; } = true;
    public bool ShowRejoinState { get; set; } = true;   // "REJOIN" badge when a stopped car moves again (experimental)
    // Driver-name column: fixed width in DIPs so long names don't resize the whole table
    // (names past this length ellipsize). Tune to taste.
    public double NameColumnWidth { get; set; } = 150;

    // ⭐ Per-lap gap delta columns for the last N laps, oldest left (the reason this project exists).
    public int DeltaLaps { get; set; } = 5;

    // Data refresh rate (snapshots per second, 1-10). Rendering only happens on change.
    public int UpdateHz { get; set; } = 4;

    // Multiclass: how many drivers of each other class to show at the top of their group.
    public int OtherClassesDriversAtTop { get; set; } = 0;

    // Per-session-type column sets. The Cells column is per-lap deltas in a race
    // and one column per completed lap in qualifying.
    public SessionColumns Race { get; set; } = SessionColumns.RaceDefaults();
    public SessionColumns Qualify { get; set; } = SessionColumns.QualifyDefaults();
    public SessionColumns Practice { get; set; } = SessionColumns.PracticeDefaults();

    // Header extras
    public bool ShowSof { get; set; } = true;
    public bool ShowRealClock { get; set; } = true; // real-life wall clock alongside the in-sim clock
    public bool ShowTimeOfDay { get; set; } = true; // in-sim local time of day (iOverlay-style clock)
    public bool ShowTrackTemp { get; set; } = true;
    public int ShowTrackTempDecimals { get; set; } = 1;   // decimals on the track-temp readout (0-2)
    public bool ShowIncidents { get; set; } = true;
    public bool ShowWeather { get; set; } = true;   // track state (Dry/Damp/Wet) + precipitation %
    public bool AbbreviateWetness { get; set; }     // false = full "Mostly Dry"/"Very Wet" names
    public int TyreSwitchAlertSec { get; set; } = 30;   // how long the dry↔wet tyre-switch flash lasts
    public bool ShowWind { get; set; } = true;      // wind direction arrow + speed
    public double HeaderFontSize { get; set; } = 13; // standings header pill text size

    // Decimal places
    public int GapPrecision { get; set; } = 1;
    public int IntervalPrecision { get; set; } = 1;
    public int LapTimePrecision { get; set; } = 3;
    public int DeltaPrecision { get; set; } = 1;
    public int QualifyGapPrecision { get; set; } = 2;   // quali gaps/intervals: hundredths matter

    // Quali: list the whole class instead of the top-N + window layout.
    public bool QualifyShowFullClass { get; set; } = true;

    // Traffic alerter (multiclass "faster class approaching" / blue-flag warnings).
    public TrafficConfig Traffic { get; set; } = new();

    // Relative box (cars physically around the player). Spec: docs/RELATIVE.md.
    public RelativeConfig Relative { get; set; } = new();

    // Fuel calculator + endurance strategy bars. Spec: docs/FUEL-STRATEGY.md.
    public FuelConfig Fuel { get; set; } = new();

    public SessionColumns ColumnsFor(Data.SessionKind kind) => kind switch
    {
        Data.SessionKind.Race => Race,
        Data.SessionKind.Qualify => Qualify,
        _ => Practice,
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static OverlayConfig Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<OverlayConfig>(File.ReadAllText(path), JsonOpts) ?? new OverlayConfig();
        }
        catch (Exception)
        {
            // Malformed config: fall back to defaults rather than dying.
        }
        return new OverlayConfig();
    }

    public void Save(string path)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    }
}

/// <summary>Traffic alerter settings. Detection details in docs/TRAFFIC-ALERTER.md.</summary>
public sealed class TrafficConfig
{
    public bool Enabled { get; set; } = true;
    public bool RacesOnly { get; set; }                         // default: also alert in practice/qual (blue flags stay race-only)
    public string Style { get; set; } = "Row";                  // Row | Beacon
    public string Mode { get; set; } = "FasterClassAndLapping"; // FasterClassOnly | FasterClassAndLapping | AllClosing
    public double AlertLeadTimeSec { get; set; } = 12;  // WATCH threshold (time to arrival) for traffic
    public double BlueLeadTimeSec { get; set; } = 20;   // WATCH threshold when being lapped (planning, not reflexes)
    public double ImminentSec { get; set; } = 4;
    public bool WarnLapping { get; set; } = true;       // alert on slower/lapped traffic AHEAD you're about to lap
    public double LapTrafficGapSec { get; set; } = 5;   // gap at which the "lapping" alert fires
    public int MaxRows { get; set; } = 3;
    public double Scale { get; set; } = 1.0;            // widget size multiplier (LayoutTransform)
    public bool ShowIRating { get; set; } = true;
    public bool AlongsideBanner { get; set; } = true;   // CarLeftRight banner; fires for alerted traffic only
    public TrafficAudioConfig Audio { get; set; } = new();

    // Widget position (DIPs), independent of the standings table; draggable in edit mode.
    public double X { get; set; } = 810;
    public double Y { get; set; } = 6;

    [JsonIgnore]
    public bool BeaconStyle => Style.Equals("Beacon", StringComparison.OrdinalIgnoreCase);
}

public sealed class TrafficAudioConfig
{
    public bool Enabled { get; set; } = true;
    public int Volume { get; set; } = 70;         // 0-100, baked into the generated WAVs
    public bool WatchCue { get; set; } = true;    // rising chirp when traffic enters the window
    public bool ImminentCue { get; set; } = true; // urgent triple beep
    public bool BlueCue { get; set; } = true;     // calm two-tone when being lapped; never escalates
}

/// <summary>Relative box settings. Useful in every session type by design (no RacesOnly:
/// lap-parity coloring and stint age simply stay neutral outside races). docs/RELATIVE.md.</summary>
public sealed class RelativeConfig
{
    public bool Enabled { get; set; } = true;
    public int CarsAhead { get; set; } = 5;
    public int CarsBehind { get; set; } = 5;
    public double Scale { get; set; } = 1.15;   // reads bigger than the dense standings by default

    public bool ShowClassPos { get; set; } = true;
    public bool ShowTyre { get; set; } = true;       // wet/dry compound ring, like the standings
    public bool ShowBrand { get; set; } = true;
    public bool ShowIRating { get; set; } = true;
    public bool ShowLicense { get; set; }
    public bool ShowStintAge { get; set; } = true;   // laps since last pit stop; green while fresh
    public bool ShowLastLap { get; set; } = true;
    public bool ShowPace { get; set; } = true;       // ▲/▼/► recent pace vs the player

    // Same-class same-lap cars within this many seconds get the ▸ battle marker + white gap.
    public double BattleGapSec { get; set; } = 1.5;
    public int GapPrecision { get; set; } = 1;

    // Widget position (DIPs); -1 = auto bottom-right corner until dragged in edit mode.
    public double X { get; set; } = -1;
    public double Y { get; set; } = -1;
}

/// <summary>Fuel calculator + strategy bars. Live numbers show in every session (practice is
/// where you learn your per-lap burn); strategy bars are race-only by definition.</summary>
public sealed class FuelConfig
{
    public bool Enabled { get; set; } = true;
    public int Strategies { get; set; } = 2;            // bars shown (1-3)
    public double MarginLaps { get; set; } = 1.0;       // safety fuel budgeted at the end, in laps

    // Fuel-save model: the car's realistic lift-and-coast ceiling, and what it costs.
    // Linear in between (Simracing-PC-style); tune per car once you know your numbers.
    public double MaxSaveLPerLap { get; set; } = 0.20;
    public double MaxSavePenaltySec { get; set; } = 0.55;

    public double PitLaneLossSec { get; set; } = -1;    // -1 = learn from own stops (fallback 45)
    public double FillRateLps { get; set; } = -1;       // -1 = learn while refueling (fallback 2.6)

    public double BarWidth { get; set; } = 420;         // strategy bar length, DIPs
    public double Scale { get; set; } = 1.0;            // widget size multiplier (LayoutTransform)

    // Widget position (DIPs), draggable in edit mode like every widget.
    public double X { get; set; } = 810;
    public double Y { get; set; } = 130;
}

/// <summary>Column toggles for one session type. Not every column is meaningful everywhere —
/// strategy/pace/positions-gained only ever render in races regardless of the flag.</summary>
public sealed class SessionColumns
{
    public bool ShowPositionsGained { get; set; }
    public bool ShowTyre { get; set; } = true;       // dry/wet compound ring next to position
    public bool ShowIRating { get; set; } = true;
    public bool ShowLicense { get; set; }            // off by default in favor of the car brand
    public bool ShowCarBrand { get; set; } = true;
    public bool ShowLapsCount { get; set; }
    public bool ShowGap { get; set; } = true;
    public bool ShowInterval { get; set; } = true;
    public bool ShowBestLap { get; set; } = true;
    public bool ShowLastLap { get; set; } = true;
    public bool ShowCells { get; set; } = true;      // race: per-lap deltas · quali: per-lap times
    public bool ShowPaceRank { get; set; }           // # fastest in class over last 5 clean laps
    public bool ShowStatus { get; set; } = true;
    public bool ShowStrategy { get; set; }
    public bool ShowPace { get; set; }

    public static SessionColumns RaceDefaults() => new()
    {
        ShowPositionsGained = true,
        ShowBestLap = false,      // LAST + deltas matter in a race; BEST is qual/practice info
        ShowPaceRank = true,
        ShowStrategy = true,
        ShowPace = true,
    };

    public static SessionColumns QualifyDefaults() => new()
    {
        ShowInterval = true,
        ShowLastLap = false,      // the per-lap cells already show every lap
    };

    public static SessionColumns PracticeDefaults() => new()
    {
        ShowLapsCount = true,
        ShowCells = false,
        ShowInterval = false,
        ShowPaceRank = true,
    };
}

/// <summary>
/// Owns the config instance, persists it next to the exe, and hot-reloads on external edits.
/// </summary>
public sealed class ConfigService : IDisposable
{
    private readonly string _path;
    private readonly FileSystemWatcher? _watcher;
    private DateTime _lastSelfWrite = DateTime.MinValue;

    public OverlayConfig Current { get; private set; }
    public event Action<OverlayConfig>? Changed;

    public ConfigService(string path)
    {
        _path = path;
        Current = OverlayConfig.Load(path);
        if (!File.Exists(path))
            Current.Save(path);

        var dir = Path.GetDirectoryName(path);
        if (dir is not null)
        {
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // Ignore the echo of our own Save(); editors also fire multiple events, so debounce.
        if ((DateTime.UtcNow - _lastSelfWrite).TotalMilliseconds < 500) return;
        Thread.Sleep(100); // let the editor finish writing
        Current = OverlayConfig.Load(_path);
        Changed?.Invoke(Current);
    }

    public void Save()
    {
        _lastSelfWrite = DateTime.UtcNow;
        Current.Save(_path);
    }

    /// <summary>Persist an in-app edit (the settings window) and push it to every widget.
    /// <see cref="Save"/> alone suppresses the watcher echo, so it would NOT re-apply live —
    /// this raises <see cref="Changed"/> directly. External file edits still flow via the watcher.</summary>
    public void SaveAndNotify()
    {
        Save();
        Changed?.Invoke(Current);
    }

    public void Dispose() => _watcher?.Dispose();
}
