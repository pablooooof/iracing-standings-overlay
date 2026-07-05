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
    public string BackgroundColor { get; set; } = "#212129";
    public string AccentColor { get; set; } = "#00FFD0";
    public string HighlightColor { get; set; } = "#FF8800";

    // Layout: top N drivers + a window around the player.
    public int DriversAtTop { get; set; } = 3;
    public int DriversAheadBehind { get; set; } = 3;
    public bool ShowColumnHeader { get; set; } = true;

    // ⭐ Per-lap gap delta columns for the last N laps, oldest left (the reason this project exists).
    public int DeltaLaps { get; set; } = 5;

    // Data refresh rate (snapshots per second, 1-10). Rendering only happens on change.
    public int UpdateHz { get; set; } = 4;

    // Multiclass: how many drivers of each other class to show at the top of their group.
    public int OtherClassesDriversAtTop { get; set; } = 0;

    // Columns (all wired to the UI)
    public bool ShowPositionsGained { get; set; } = true;
    public bool ShowIRating { get; set; } = true;
    public bool ShowLicense { get; set; } = true;
    public bool ShowGap { get; set; } = true;
    public bool ShowInterval { get; set; } = true;
    public bool ShowBestLap { get; set; } = true;
    public bool ShowLastLap { get; set; } = true;
    public bool ShowDelta { get; set; } = true;
    public bool ShowStatus { get; set; } = true;     // PIT / black flag / meatball / DQ badge
    public bool ShowStrategy { get; set; } = true;   // expected pit lap, stops to end (race only)
    public bool ShowPace { get; set; } = true;       // fast/slow vs class + fuel-save tag (race only)

    // Header extras
    public bool ShowSof { get; set; } = true;
    public bool ShowTrackTemp { get; set; } = true;
    public bool ShowIncidents { get; set; } = true;

    // Decimal places
    public int GapPrecision { get; set; } = 1;
    public int IntervalPrecision { get; set; } = 1;
    public int LapTimePrecision { get; set; } = 3;
    public int DeltaPrecision { get; set; } = 1;

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

    public void Dispose() => _watcher?.Dispose();
}
