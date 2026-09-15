using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    // Consumption table (Last / Last-5 / Last-10 / Stint / Target) with a clickable target.
    public FuelTableConfig FuelTable { get; set; } = new();

    // Lap Lab: practice lap table, sectors vs a reference lap. Spec: docs/LAP-LAB.md.
    public LapLabConfig LapLab { get; set; } = new();

    // Auto-hide: keep the widgets off screen unless iRacing is the focused app + a manual hotkey.
    public AutoHideConfig AutoHide { get; set; } = new();

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

/// <summary>Auto-hide the overlays when you tab away from iRacing (data collection keeps running),
/// plus a system-wide hotkey to force show/hide. Data flows regardless — this only affects whether
/// the widgets are painted. Ignored in --demo mode so testing never blanks the screen.</summary>
public sealed class AutoHideConfig
{
    // Hide every widget unless the iRacing sim (or this app's own settings window) is the foreground
    // app. On by default: the whole point is to get the overlay out of the way when you alt-tab.
    public bool Enabled { get; set; } = true;

    // System-wide toggle to force the overlays on/off regardless of what's focused — force-show
    // while you're in another app, or force-hide even inside the sim. The override clears itself
    // once auto-detect agrees with it again.
    public bool HotkeyEnabled { get; set; } = true;

    // Combo for that hotkey: "Ctrl"/"Alt"/"Shift"/"Win" modifiers + one key ("H", "F8", "1").
    public string Hotkey { get; set; } = "Ctrl+Alt+H";
}

/// <summary>Traffic alerter settings. Detection details in docs/TRAFFIC-ALERTER.md.</summary>
public sealed class TrafficConfig
{
    public bool Enabled { get; set; } = true;
    public bool RacesOnly { get; set; }                         // default: also alert in practice/qual (blue flags stay race-only)
    public bool WhileSpectating { get; set; }                   // default: no alerts while out of the car
    public string Style { get; set; } = "Row";                  // Row | Beacon
    public string Mode { get; set; } = "FasterClassAndLapping"; // FasterClassOnly | FasterClassAndLapping | AllClosing
    public double AlertLeadTimeSec { get; set; } = 8;   // WATCH threshold — gap-seconds (Gap basis) or arrival-seconds (Countdown)
    public double BlueLeadTimeSec { get; set; } = 12;   // WATCH threshold when being lapped (planning, not reflexes)
    public double ImminentSec { get; set; } = 4;
    public bool GroupByDirection { get; set; } = true;  // split the list "me in the middle": cars you're catching AHEAD above a fixed YOU line, cars closing from BEHIND below it
    public int SlotsAhead { get; set; } = 3;            // fixed slots above the YOU line (split layout); YOU never moves
    public int SlotsBehind { get; set; } = 3;           // fixed slots below the YOU line (split layout)
    public bool ShowPanel { get; set; }                 // false (default) = frameless/transparent with text shadow; true = one solid rounded panel behind the widget
    public bool WarnLapping { get; set; } = true;       // alert on slower/lapped traffic AHEAD you're about to lap
    public double LapTrafficGapSec { get; set; } = 5;   // gap at which the "lapping" alert fires
    public bool WarnSameClassClosing { get; set; } = true;  // heads-up when a same-class car BEHIND is genuinely charging (single-class packs have no "faster class")
    public double SameClassClosingRate { get; set; } = 0.3; // …show it if it's catching faster than this (s/lap) OR (PinNearbySameClass) it's simply within SameClassPinSec
    public bool PinNearbySameClass { get; set; } = true;    // keep a same-class car on the widget while it's within SameClassPinSec behind, catching or not (wheel-to-wheel awareness)
    public double SameClassPinSec { get; set; } = 2.0;      // …that "nearby" gap
    public int MaxRows { get; set; } = 3;               // total cap in the flat (grouping-off) list
    public double Scale { get; set; } = 1.0;            // widget size multiplier (LayoutTransform)
    public bool ShowIRating { get; set; } = true;
    public bool ShowTimeToArrival { get; set; }         // false (default) = Gap basis: appear/escalate/sort AND the number all key off the on-track gap (matches the relative box; lead/imminent read as gap-seconds). true = Countdown basis: all keyed off time-to-arrival.
    public bool AlongsideBanner { get; set; } = true;   // CarLeftRight: mark the car overlapping you left/right (on its own row, not blanking the widget)
    public bool AlongsideAnyCar { get; set; }           // fire it for ANY spotter-reported overlap (a same-class pack has no "alert" beside you), not just alerted traffic
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
    public bool ShowClosing { get; set; } = true;    // per-car closing rate to YOU (gap trend): who's actually catching / being caught, s/lap

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

/// <summary>The fuel consumption table: rows for the last lap, last-5/last-10 averages, the
/// current stint, and a driver-set target. Works in both practice and race. The target is two
/// interlocked numbers — laps per stint and litres per lap — tied together by the usable tank;
/// whichever the driver set last (<see cref="TargetMode"/>) drives the other. Editable here and,
/// when <see cref="Interactive"/> is on, clickable on the widget itself.</summary>
public sealed class FuelTableConfig
{
    public bool Enabled { get; set; } = true;
    public bool ShowLast5 { get; set; } = false;   // off by default (per request); toggle on in settings
    public bool ShowLast10 { get; set; } = true;
    public bool ShowStint { get; set; } = true;
    public bool ShowStatus { get; set; } = true;    // the target-vs-actual "ahead/behind" line

    // The target, as raw driver intent. The builder derives the partner value from the live tank.
    public double TargetLaps { get; set; } = 0;     // 0 = no target set
    public double TargetPerLap { get; set; } = 0;   // 0 = no target set
    public string TargetMode { get; set; } = "Laps"; // "Laps" | "PerLap" — which one the driver set last

    // On = the widget drops click-through so the target's ▲/▼ (and scroll) work during a session.
    // Off (default) keeps it fully click-through like every other overlay; the target is still
    // editable in settings and in edit mode.
    public bool Interactive { get; set; } = false;

    public double Scale { get; set; } = 1.0;

    public double X { get; set; } = 810;
    public double Y { get; set; } = 300;
}

/// <summary>Lap Lab: the practice/testing lap table — every lap a row, official sectors as
/// columns, gaps vs a reference lap. Never shows in races by design (Data/LapLabTracker).</summary>
public sealed class LapLabConfig
{
    public bool Enabled { get; set; } = true;
    public int Decimals { get; set; } = 2;        // sector/lap delta decimals (1-3)
    public int MaxRows { get; set; } = 8;         // laps shown, newest on top
    // Columns: official sectors, or turn zones auto-detected from the reference's speed
    // trace (falls back to sectors when no trace segments cleanly).
    public string View { get; set; } = "Sectors";             // Sectors | Turns
    // Heatmap bands, in % of the reference split — pace thinking, not absolute seconds.
    // Under Good: on pace (quiet; telemetry-at-the-desk territory). Good→Full: ramps to full
    // red — the "findable on track" focus band. Above Ignore: mistake/traffic, shown dim.
    public double HeatGoodPct { get; set; } = 1.0;
    public double HeatFullPct { get; set; } = 2.0;
    public double HeatIgnorePct { get; set; } = 4.5;
    public bool HideSlowLaps { get; set; } = true;    // slow laps → one quiet line (traffic/spin)
    public int SlowLapPct { get; set; } = 105;        // % of session best that counts as slow
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
/// Owns the config, persists it next to the exe, and hot-reloads on external edits.
///
/// There are two "states": <b>In car</b> and <b>Spectating</b> (out of the car — teammate stint,
/// garage, spectating; see IRacingSource.DrivingChanged). Rather than two full, independently
/// drifting copies, this holds ONE base config (<c>config.json</c>) plus a <b>sparse override
/// layer</b> for spectating (<c>config.spectate.json</c>) that stores ONLY the settings you've
/// deliberately changed while out of the car. The effective spectate config is the base with those
/// overrides patched in, so anything you HAVEN'T overridden follows the in-car value live — no more
/// silent divergence when a race drops you out of the car.
///
/// Inheritance is by value-diff: on save while spectating, the effective config is diffed against
/// the base and only the differing keys are stored. A spectate value equal to the base isn't
/// stored (it inherits). <see cref="Current"/> is always the active effective config; the settings
/// window can edit either state directly via <see cref="Base"/> / <see cref="SpectateEffective"/>
/// and <see cref="SaveProfileAndNotify"/>.
/// </summary>
public sealed class ConfigService : IDisposable
{
    private readonly string _path;
    private readonly string _spectatePath;
    private readonly FileSystemWatcher? _watcher;
    private DateTime _lastSelfWrite = DateTime.MinValue;

    private OverlayConfig _base;
    private JsonObject _overrides;              // sparse: only the spectate keys that differ from base
    private OverlayConfig _effectiveSpectate;   // = base ⊕ overrides

    /// <summary>The active effective config (what the overlays render): base while in the car,
    /// base-patched-with-overrides while spectating.</summary>
    public OverlayConfig Current => Spectating ? _effectiveSpectate : _base;

    /// <summary>The in-car profile — the base every setting falls back to.</summary>
    public OverlayConfig Base => _base;

    /// <summary>The spectate profile as effective values (base + overrides), editable in place.
    /// Saving diffs it back against the base to keep only the deliberate overrides.</summary>
    public OverlayConfig SpectateEffective => _effectiveSpectate;

    public bool Spectating { get; private set; }

    /// <summary>How many individual settings the spectate profile currently overrides.</summary>
    public int SpectateOverrideCount => CountLeaves(_overrides);

    public event Action<OverlayConfig>? Changed;

    public ConfigService(string path)
    {
        _path = path;
        _spectatePath = Path.Combine(Path.GetDirectoryName(path) ?? "", "config.spectate.json");
        _base = OverlayConfig.Load(path);
        _overrides = LoadOverrides();          // normalizes a pre-inheritance full clone to sparse
        _effectiveSpectate = BuildEffective();
        if (!File.Exists(path)) _base.Save(path);

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

    /// <summary>Switch the active state. Both profiles are always ready (inheritance means the
    /// spectate profile needs no lazy seeding), so this just swaps which one is active and re-applies.</summary>
    public void SetSpectating(bool spectating)
    {
        if (Spectating == spectating) return;
        Spectating = spectating;
        Log.Write($"config profile: {(spectating ? "spectate" : "driving")}");
        Changed?.Invoke(Current);
    }

    // ---- inheritance engine ---------------------------------------------

    private OverlayConfig BuildEffective() => FromObject(Merge(ToObject(_base), _overrides));

    /// <summary>Load the spectate override layer, normalizing any older full-clone spectate file
    /// (pre-inheritance) into the sparse format so inheritance takes effect immediately.</summary>
    private JsonObject LoadOverrides()
    {
        if (!File.Exists(_spectatePath)) return new JsonObject();
        try
        {
            var raw = JsonNode.Parse(File.ReadAllText(_spectatePath)) as JsonObject ?? new JsonObject();
            var baseObj = ToObject(_base);
            var sparse = Diff(Merge(baseObj, raw), baseObj);
            StripSharedTargetKeys(sparse);     // the fuel target is global; never a spectate override
            if (!JsonNode.DeepEquals(sparse, raw))
            {
                // Converting a legacy full clone: keep the original once so nothing is lost.
                try { if (!File.Exists(_spectatePath + ".bak")) File.Copy(_spectatePath, _spectatePath + ".bak"); }
                catch { /* best effort */ }
                _lastSelfWrite = DateTime.UtcNow;
                WriteOverridesFile(sparse);
            }
            return sparse;
        }
        catch { return new JsonObject(); }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // Ignore the echo of our own writes; editors also fire multiple events, so debounce.
        if ((DateTime.UtcNow - _lastSelfWrite).TotalMilliseconds < 500) return;
        Thread.Sleep(100); // let the editor finish writing
        bool spectateFile = string.Equals(e.Name, Path.GetFileName(_spectatePath),
                                          StringComparison.OrdinalIgnoreCase);
        if (spectateFile)
        {
            if (!File.Exists(_spectatePath)) return;
            _overrides = LoadOverrides();
        }
        else
        {
            _base = OverlayConfig.Load(_path);
        }
        _effectiveSpectate = BuildEffective();   // base change must ripple through inherited keys
        Changed?.Invoke(Current);
    }

    /// <summary>Persist the ACTIVE profile (used by widget drags in edit mode): base while in the
    /// car, the spectate overrides while spectating.</summary>
    public void Save()
    {
        if (Spectating) SaveSpectate();
        else SaveBase();
    }

    public void SaveAndNotify()
    {
        Save();
        Changed?.Invoke(Current);
    }

    /// <summary>Persist one specific profile — the settings window edits either state regardless of
    /// which is live. Raises <see cref="Changed"/> so the (active) overlays re-apply.</summary>
    public void SaveProfileAndNotify(bool spectate)
    {
        if (spectate) SaveSpectate();
        else SaveBase();
        Changed?.Invoke(Current);
    }

    private void SaveBase()
    {
        _lastSelfWrite = DateTime.UtcNow;
        _base.Save(_path);
        _effectiveSpectate = BuildEffective();   // inherited spectate keys follow the new base
    }

    private void SaveSpectate()
    {
        // The fuel target (laps / L-per-lap / which drives which) is a race-wide strategy decision,
        // not a per-view preference: keep it in the base so it's shared across both states. Push any
        // spectate-side change down into the base, then never store it as an override.
        if (SyncSharedTargetToBase())
        {
            _lastSelfWrite = DateTime.UtcNow;
            _base.Save(_path);
        }
        var sparse = Diff(ToObject(_effectiveSpectate), ToObject(_base));
        StripSharedTargetKeys(sparse);
        _overrides = sparse;
        _lastSelfWrite = DateTime.UtcNow;
        WriteOverridesFile(sparse);
    }

    private bool SyncSharedTargetToBase()
    {
        var a = _effectiveSpectate.FuelTable;
        var b = _base.FuelTable;
        if (b.TargetLaps == a.TargetLaps && b.TargetPerLap == a.TargetPerLap && b.TargetMode == a.TargetMode)
            return false;
        b.TargetLaps = a.TargetLaps;
        b.TargetPerLap = a.TargetPerLap;
        b.TargetMode = a.TargetMode;
        return true;
    }

    private void WriteOverridesFile(JsonObject sparse) =>
        File.WriteAllText(_spectatePath, sparse.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    private static void StripSharedTargetKeys(JsonObject sparse)
    {
        if (sparse["FuelTable"] is not JsonObject ft) return;
        ft.Remove(nameof(FuelTableConfig.TargetLaps));
        ft.Remove(nameof(FuelTableConfig.TargetPerLap));
        ft.Remove(nameof(FuelTableConfig.TargetMode));
        if (ft.Count == 0) sparse.Remove("FuelTable");
    }

    // ---- JSON tree helpers ----------------------------------------------

    private static JsonObject ToObject(OverlayConfig c) =>
        JsonNode.Parse(JsonSerializer.Serialize(c))!.AsObject();

    private static OverlayConfig FromObject(JsonObject o) =>
        JsonSerializer.Deserialize<OverlayConfig>(o.ToJsonString(), NodeOpts) ?? new OverlayConfig();

    /// <summary>Deep-merge <paramref name="over"/> onto a clone of <paramref name="baseObj"/>:
    /// nested objects recurse, leaves replace.</summary>
    private static JsonObject Merge(JsonObject baseObj, JsonObject over)
    {
        var result = baseObj.DeepClone().AsObject();
        foreach (var (key, value) in over)
        {
            if (value is JsonObject oo && result[key] is JsonObject bo)
                result[key] = Merge(bo, oo);
            else
                result[key] = value?.DeepClone();
        }
        return result;
    }

    /// <summary>Sparse diff: the keys of <paramref name="eff"/> whose values differ from
    /// <paramref name="baseObj"/> (recursing into nested objects). Equal values are omitted, so
    /// they inherit.</summary>
    private static JsonObject Diff(JsonObject eff, JsonObject baseObj)
    {
        var result = new JsonObject();
        foreach (var (key, value) in eff)
        {
            var b = baseObj[key];
            if (value is JsonObject eo && b is JsonObject bo)
            {
                var child = Diff(eo, bo);
                if (child.Count > 0) result[key] = child;
            }
            else if (b is null || !JsonNode.DeepEquals(value, b))
            {
                result[key] = value?.DeepClone();
            }
        }
        return result;
    }

    private static int CountLeaves(JsonObject o)
    {
        int n = 0;
        foreach (var (_, value) in o)
            n += value is JsonObject child ? CountLeaves(child) : 1;
        return n;
    }

    private static readonly JsonSerializerOptions NodeOpts = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public void Dispose() => _watcher?.Dispose();
}
