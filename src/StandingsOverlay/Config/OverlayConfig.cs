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
    // When you're running near the front (position <= this), show at least this many from the top
    // instead of a tiny window — you want to see the whole leading group. 0 = off.
    public int MinLeadingCars { get; set; } = 10;
    // Pin a towed player-class car into the standings at its live position (with a TOW badge)
    // even outside the window — it disappears again the moment it drives out of its stall.
    public bool PinTowedCars { get; set; } = true;
    // Infer opponents' tire changes from stop lengths (no SDK channel exists for their tire
    // sets): under fuel-and-tires-separate rules a tire stop sits ~10s+ longer than the same
    // fuel fill alone. Shows as "ST8+" (rubber older than the stint) in the relative.
    public bool InferTireChanges { get; set; } = true;
    public bool ShowColumnHeader { get; set; } = true;
    public bool ShowRejoinState { get; set; } = true;   // "REJOIN" badge when a stopped car moves again (experimental)
    // Status column style: "TextAndFlags" = penalty flag chip + physical-state text side by side;
    // "Text" = one text badge picked by the unified precedence (Data/CarStatus).
    public string StatusStyle { get; set; } = "TextAndFlags";
    // Driver-name column: fixed width in DIPs so long names don't resize the whole table
    // (names past this length ellipsize). Tune to taste.
    public double NameColumnWidth { get; set; } = 150;

    // ⭐ Per-lap gap delta columns for the last N laps, oldest left (the reason this project exists).
    public int DeltaLaps { get; set; } = 5;

    // Smooth GAP/INT: compute standings gaps from CarIdxEstTime (continuous, like the relative)
    // instead of CarIdxF2Time (steps at timing lines). Laps-down is still shown as "NL".
    public bool SmoothGaps { get; set; } = true;

    // Data refresh rate (snapshots per second, 1-10). Rendering only happens on change.
    public int UpdateHz { get; set; } = 4;

    // One GET to GitHub's latest-release endpoint at launch (UpdateCheck); tray + About link
    // when a newer version exists. Never polls, never downloads.
    public bool CheckForUpdates { get; set; } = true;

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
    public int WeatherAlertSec { get; set; } = 180;     // how long the dry↔wet track-state banner stays
    public int TyreSwitchAlertSec { get; set; } = 30;   // how long a dry↔wet tyre switch is shown
    public string TyreSwitchDisplay { get; set; } = "Both";   // Flash (header) | Inline (o→o in the row) | Both
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

    // Lap Lab: practice lap table, sectors vs a reference lap. Spec: docs/LAP-LAB.md.
    public LapLabConfig LapLab { get; set; } = new();

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

    /// <summary>Deep copy via a JSON round-trip — used to seed the spectate profile.</summary>
    public OverlayConfig Clone() =>
        JsonSerializer.Deserialize<OverlayConfig>(JsonSerializer.Serialize(this, JsonOpts), JsonOpts)
        ?? new OverlayConfig();
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
    public double Scale { get; set; } = 1.0;    // fonts now match the standings ramp exactly at 1.0

    public bool ShowClassPos { get; set; } = true;
    public bool ShowTyre { get; set; } = true;       // wet/dry compound ring, like the standings
    public bool HideParkedCars { get; set; }         // drop cars sat in the pits >~60s (DNF / no driver)
    public bool ShowBrand { get; set; } = true;
    public bool ShowIRating { get; set; } = true;
    public bool ShowLicense { get; set; }
    public bool ShowStintAge { get; set; } = true;   // laps since last pit stop; green while fresh
    public bool ShowLastLap { get; set; } = true;
    public bool ShowPace { get; set; } = true;       // ▲/▼/► recent pace vs the player

    // Same-class same-lap cars within this many seconds get the ▸ battle marker + white gap.
    public double BattleGapSec { get; set; } = 1.5;
    public int GapPrecision { get; set; } = 1;

    // Status style, independent of the standings: "Text" (default, denser) or "TextAndFlags".
    public string StatusStyle { get; set; } = "Text";

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

/// <summary>Lap Lab: the practice/testing lap table — every lap a row, official sectors as
/// columns, gaps vs a reference lap. Never shows in races by design (Data/LapLabTracker).</summary>
public sealed class LapLabConfig
{
    public bool Enabled { get; set; } = true;
    public int Decimals { get; set; } = 2;        // sector/lap delta decimals (1-3)
    public int MaxRows { get; set; } = 8;         // laps shown, newest on top
    // SessionBest | SessionOptimal | PreviousBest | File
    public string Reference { get; set; } = "SessionBest";
    public string ReferenceFile { get; set; } = "";       // .ibt path used when Reference = File
    public bool SaveSessionBest { get; set; } = true;     // auto-save best clean lap per car+track
    public double WarnTrackTempDelta { get; set; } = 4.0; // °C difference vs ref that warns
    public double Scale { get; set; } = 1.0;      // widget size multiplier (LayoutTransform)

    // Widget position (DIPs), draggable in edit mode like every widget.
    public double X { get; set; } = 7;
    public double Y { get; set; } = 330;
}

/// <summary>Column toggles for one session type. Not every column is meaningful everywhere —
/// strategy/pace/positions-gained only ever render in races regardless of the flag.</summary>
public sealed class SessionColumns
{
    public bool ShowPositionsGained { get; set; }
    public bool ShowTyre { get; set; } = true;       // dry/wet compound ring next to position
    // Laps on current tires next to the ring ("42²" = double-stint). Renders in races only,
    // so true is safe as the property default (pre-existing configs lack the key).
    public bool ShowTyreAge { get; set; } = true;
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
    // Last pit stop, race-only. Lap + total on by default; the drive-through/box split is opt-in.
    public bool ShowPitLap { get; set; }
    public bool ShowPitTotal { get; set; }
    public bool ShowPitDrive { get; set; }
    public bool ShowPitStall { get; set; }

    public static SessionColumns RaceDefaults() => new()
    {
        ShowPositionsGained = true,
        ShowTyreAge = true,       // endurance: who's double-stinting is a glance, not a guess
        ShowBestLap = false,      // LAST + deltas matter in a race; BEST is qual/practice info
        ShowPaceRank = true,
        ShowStrategy = true,
        ShowPace = true,
        ShowPitLap = true,
        ShowPitTotal = true,
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
/// Owns the config instances, persists them next to the exe, and hot-reloads on external edits.
/// Two profiles: config.json (driving) and config.spectate.json, active while the player is out
/// of the car (teammate stint, garage, spectating — see IRacingSource.DrivingChanged). The
/// spectate file is cloned from the driving profile on first use, seeded with a wider standings
/// view, so EVERY setting — widget positions, columns, row counts — can differ per profile.
/// <see cref="Current"/> is always the active profile; swapping raises <see cref="Changed"/> so
/// all widgets re-apply, exactly like a settings edit.
/// </summary>
public sealed class ConfigService : IDisposable
{
    private readonly string _path;
    private readonly string _spectatePath;
    private readonly FileSystemWatcher? _watcher;
    private DateTime _lastSelfWrite = DateTime.MinValue;
    private OverlayConfig _driving;
    private OverlayConfig? _spectate;

    public OverlayConfig Current { get; private set; }
    public bool Spectating { get; private set; }
    public event Action<OverlayConfig>? Changed;

    public ConfigService(string path)
    {
        _path = path;
        _spectatePath = Path.Combine(Path.GetDirectoryName(path) ?? "", "config.spectate.json");
        _driving = OverlayConfig.Load(path);
        if (File.Exists(_spectatePath)) _spectate = OverlayConfig.Load(_spectatePath);
        Current = _driving;
        if (!File.Exists(path))
            Current.Save(path);

        var dir = Path.GetDirectoryName(path);
        if (dir is not null)
        {
            _watcher = new FileSystemWatcher(dir, "config*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
        }
    }

    /// <summary>Switch between the driving and spectate profiles. Clones the driving profile
    /// into config.spectate.json on first use (nothing jumps, then the user tunes it live).</summary>
    public void SetSpectating(bool spectating)
    {
        if (Spectating == spectating) return;
        Spectating = spectating;
        if (spectating && _spectate is null)
        {
            _spectate = _driving.Clone();
            // Out of the car you're a crew chief: default to the field, not a window.
            _spectate.DriversAhead = Math.Max(_spectate.DriversAhead, 8);
            _spectate.DriversBehind = Math.Max(_spectate.DriversBehind, 8);
            _spectate.OtherClassesDriversAtTop = Math.Max(_spectate.OtherClassesDriversAtTop, 2);
            _lastSelfWrite = DateTime.UtcNow;
            _spectate.Save(_spectatePath);
        }
        Current = spectating ? _spectate! : _driving;
        Log.Write($"config profile: {(spectating ? "spectate" : "driving")}");
        Changed?.Invoke(Current);
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // Ignore the echo of our own Save(); editors also fire multiple events, so debounce.
        if ((DateTime.UtcNow - _lastSelfWrite).TotalMilliseconds < 500) return;
        Thread.Sleep(100); // let the editor finish writing
        bool spectateFile = string.Equals(e.Name, Path.GetFileName(_spectatePath),
                                          StringComparison.OrdinalIgnoreCase);
        if (spectateFile)
        {
            if (!File.Exists(_spectatePath)) return;
            _spectate = OverlayConfig.Load(_spectatePath);
        }
        else
        {
            _driving = OverlayConfig.Load(_path);
        }
        if (spectateFile != Spectating) return;   // inactive profile edited: reloaded silently
        Current = spectateFile ? _spectate! : _driving;
        Changed?.Invoke(Current);
    }

    public void Save()
    {
        _lastSelfWrite = DateTime.UtcNow;
        Current.Save(Spectating ? _spectatePath : _path);
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
