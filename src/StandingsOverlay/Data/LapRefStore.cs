using System.IO;

namespace StandingsOverlay.Data;

/// <summary>
/// Owns Lap Lab's external references. Lives next to the exe in <c>laps/</c>:
/// previous-session bests as <c>prev_{car}_{track}.json</c> (auto-saved on every new session
/// best), and a parse cache per imported .ibt so re-selecting a big file is instant.
/// .ibt parsing runs on the thread pool (a 200 MB endurance file takes ~a second — never on
/// the telemetry tick); results land in volatile fields the tracker reads each build.
/// </summary>
public sealed class LapRefStore
{
    private readonly string _dir = Path.Combine(AppContext.BaseDirectory, "laps");

    private volatile LapRef? _fileRef;
    private volatile string _fileError = "";
    private string _filePath = "";
    private volatile bool _fileLoading;

    private LapRef? _prevRef;
    private string _prevKey = "";
    private double _lastSavedPrev = double.MaxValue;   // best lap time already on disk

    public LapRef? FileRef => _fileRef;
    public string FileError => _fileError;
    public bool FileLoading => _fileLoading;
    public LapRef? PrevRef => _prevRef;

    /// <summary>Kick off (or re-kick) the .ibt load when the configured path changes.</summary>
    public void EnsureFile(string path)
    {
        if (path == _filePath) return;
        _filePath = path;
        _fileRef = null;
        _fileError = "";
        if (string.IsNullOrWhiteSpace(path)) return;

        _fileLoading = true;
        Task.Run(() =>
        {
            try
            {
                if (!File.Exists(path)) { _fileError = "file not found"; return; }

                // Parse cache: keyed by name + mtime so a re-exported file re-parses.
                // Prefix bumps when the LapRef payload grows (cache2: + pedal grids).
                string cacheName = $"cache2_{Path.GetFileNameWithoutExtension(path)}_{File.GetLastWriteTimeUtc(path).Ticks}.json";
                string cachePath = Path.Combine(_dir, cacheName);
                var cached = LapRef.Load(cachePath);
                if (cached is not null)
                {
                    _fileRef = cached;
                    Log.Write($"lap lab: ref file (cached) {cached.Label} {cached.LapTime:0.000}");
                    return;
                }

                var lap = IbtLap.ReadBestLap(path, out var err);
                if (lap is null)
                {
                    _fileError = err;
                    Log.Write($"lap lab: ref file failed — {err} ({Path.GetFileName(path)})");
                    return;
                }
                _fileRef = lap;
                lap.Save(cachePath);
                Log.Write($"lap lab: ref file loaded {lap.Label} {lap.LapTime:0.000} " +
                          $"({lap.Conditions?.TrackName} {lap.Conditions?.TrackTempC:0}°C, {lap.Conditions?.RecordedUtc})");
            }
            catch (Exception ex)
            {
                _fileError = ex.Message;
                Log.Error("lap lab ref load", ex);
            }
            finally { _fileLoading = false; }
        });
    }

    /// <summary>Load the saved previous-session best for this car+track combo (tiny JSON,
    /// synchronous on purpose — happens once per session).</summary>
    public void EnsurePrev(string key)
    {
        if (key.Length == 0 || key == _prevKey) return;
        _prevKey = key;
        _prevRef = LapRef.Load(PrevPath(key));
        _lastSavedPrev = _prevRef?.LapTime ?? double.MaxValue;
        if (_prevRef is not null)
            Log.Write($"lap lab: previous best loaded {_prevRef.LapTime:0.000} ({_prevRef.Conditions?.RecordedUtc})");
    }

    /// <summary>Persist a new personal best for the combo. Only writes when it actually beats
    /// what's on disk — today's slower session must never overwrite yesterday's benchmark.
    /// The in-memory PrevRef deliberately stays as loaded: mid-session the "prev" reference
    /// should keep meaning "before this session".</summary>
    public void SavePrev(string key, LapRef lap)
    {
        if (key.Length == 0 || lap.LapTime <= 0) return;
        EnsurePrev(key);   // learn what's on disk before deciding to overwrite it
        if (lap.LapTime >= _lastSavedPrev) return;
        _lastSavedPrev = lap.LapTime;
        lap.Source = "prev";
        try
        {
            lap.Save(PrevPath(key));
            Log.Write($"lap lab: previous best saved {lap.LapTime:0.000} ({key})");
        }
        catch (Exception ex) { Log.Error("lap lab prev save", ex); }
    }

    public static string Key(Roster roster) =>
        roster.TrackId <= 0 || roster.PlayerCarPath.Length == 0
            ? ""
            : Sanitize($"{roster.PlayerCarPath}_{roster.TrackId}_{roster.TrackConfig}");

    private string PrevPath(string key) => Path.Combine(_dir, $"prev_{key}.json");

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '-');
        return s.Replace(' ', '-').ToLowerInvariant();
    }
}
