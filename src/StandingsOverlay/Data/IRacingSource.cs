using irsdkSharp;
using irsdkSharp.Serialization.Models.Session;
using StandingsOverlay.Config;

namespace StandingsOverlay.Data;

/// <summary>
/// Live telemetry source. irsdkSharp raises OnDataChanged at 60 Hz (it waits on iRacing's
/// data-valid event, no busy polling); we down-sample to cfg.UpdateHz and re-parse the session
/// YAML only when the header says it changed.
/// </summary>
public sealed class IRacingSource : ITelemetrySource
{
    private readonly IRacingSDK _sdk = new();
    private readonly Func<OverlayConfig> _cfg;
    private readonly GapHistory _history = new();
    private readonly StintTracker _stints = new();
    private readonly Roster _roster = new();

    private int _frameCount;
    private int _lastSessionInfoUpdate = -1;
    private int _lastSessionNum = -1;
    private bool _running;

    public event Action<StandingsSnapshot>? SnapshotReady;

    public IRacingSource(Func<OverlayConfig> cfg) => _cfg = cfg;

    public void Start()
    {
        _running = true;
        _sdk.OnDataChanged += OnDataChanged;
        _sdk.OnDisconnected += () =>
        {
            _lastSessionInfoUpdate = -1;
            _lastSessionNum = -1;
            _history.Reset();
            _stints.Reset();
            lock (_roster) _roster.Drivers.Clear();
            SnapshotReady?.Invoke(StandingsSnapshot.Disconnected);
        };
        SnapshotReady?.Invoke(StandingsSnapshot.Disconnected);
    }

    private void OnDataChanged()
    {
        if (!_running) return;

        var cfg = _cfg();
        int stride = Math.Max(1, 60 / Math.Clamp(cfg.UpdateHz, 1, 10));
        if (++_frameCount % stride != 0) return;

        try
        {
            RefreshRosterIfStale();

            var t = ReadTick();
            if (t is null) return;

            _history.Update(t, _roster);
            _stints.Update(t);
            SnapshotReady?.Invoke(SnapshotBuilder.Build(t, _roster, _history, _stints, cfg));
        }
        catch (Exception ex)
        {
            // A torn read or YAML quirk on one tick is harmless; the next tick recovers.
            // Log it (throttled) so silent failures are diagnosable from overlay.log.
            if ((DateTime.UtcNow - _lastErrorLog).TotalSeconds > 10)
            {
                _lastErrorLog = DateTime.UtcNow;
                Log.Error("telemetry-tick", ex);
            }
        }
    }

    private DateTime _lastErrorLog = DateTime.MinValue;

    private void RefreshRosterIfStale()
    {
        var header = _sdk.Header;
        if (header is null || header.SessionInfoUpdate == _lastSessionInfoUpdate) return;

        var yaml = _sdk.GetSessionInfo();
        if (yaml is null) return;

        var model = IRacingSessionModel.Serialize(yaml);
        if (model?.DriverInfo?.Drivers is null)
        {
            Log.Write($"session YAML parse failed (update {header.SessionInfoUpdate}, {yaml.Length} chars)");
            return;
        }

        _lastSessionInfoUpdate = header.SessionInfoUpdate;

        int sessionNum = _sdk.GetData("SessionNum") is int sn ? sn : -1;
        var session = model.SessionInfo?.Sessions?.FirstOrDefault(x => x.SessionNum == sessionNum)
                      ?? model.SessionInfo?.Sessions?.LastOrDefault();
        _currentSessionType = session?.SessionType ?? "Race";
        _sessionLapsTotal = int.TryParse(session?.SessionLaps, out var sl) && sl > 0 ? sl : -1;

        // New session (practice → race, race restart, …): lap/gap/stint history is stale.
        if (sessionNum != _lastSessionNum)
        {
            _lastSessionNum = sessionNum;
            _history.Reset();
            _stints.Reset();
        }

        lock (_roster)
        {
            _roster.Drivers.Clear();
            foreach (var d in model.DriverInfo.Drivers)
            {
                _roster.Drivers[d.CarIdx] = new DriverEntry(
                    CarIdx: d.CarIdx,
                    Name: d.UserName ?? "",
                    CarNumber: d.CarNumber ?? "",
                    IRating: d.IRating,
                    LicString: d.LicString ?? "",
                    LicColor: HexColor(d.LicColor),
                    CarBrand: BrandOf(d.CarScreenNameShort),
                    CarClassId: d.CarClassID,
                    ClassName: d.CarClassShortName ?? "",
                    ClassColor: HexColor(d.CarClassColor),
                    ClassEstLap: d.CarClassEstLapTime,
                    IsPaceCar: d.CarIsPaceCar == "1",
                    IsSpectator: d.IsSpectator == 1);
            }

            // Session results: the only source of laps run before we joined (or by cars
            // that have since left the world) — telemetry arrays are blank for those.
            _roster.Results.Clear();
            if (session?.ResultsPositions is not null)
                foreach (var r in session.ResultsPositions)
                    _roster.Results[r.CarIdx] = new SessionResult(r.FastestTime, r.LastTime, r.LapsComplete);

            _roster.TrackName = model.WeekendInfo?.TrackDisplayShortName ?? "";
            _roster.ComputeSof();
        }
        // The YAML updates constantly during a race (results churn); only log real changes.
        var summary = $"roster: {_roster.Drivers.Count} drivers, session '{_currentSessionType}', " +
                      $"unnamed: {_roster.Drivers.Values.Count(d => string.IsNullOrEmpty(d.Name))}";
        if (summary != _lastRosterSummary)
        {
            _lastRosterSummary = summary;
            Log.Write(summary);
        }
    }

    private string _lastRosterSummary = "";

    private string _currentSessionType = "Race";
    private int _sessionLapsTotal = -1;

    /// <summary>"Ferrari 296 GT3" → "Ferrari"; "Mercedes-AMG GT3 2020" → "Mercedes".</summary>
    private static string BrandOf(string? screenNameShort)
    {
        if (string.IsNullOrWhiteSpace(screenNameShort)) return "";
        var tok = screenNameShort.Trim().Split(' ', '-')[0];
        return tok.Length > 9 ? tok[..9] : tok;
    }

    private RawTick? ReadTick()
    {
        if (_sdk.GetData("PlayerCarIdx") is not int playerIdx) return null;
        if (_sdk.GetData("CarIdxPosition") is not int[] pos) return null;

        var t = new RawTick
        {
            PlayerCarIdx = playerIdx,
            Position = pos,
            ClassPosition = _sdk.GetData("CarIdxClassPosition") as int[] ?? [],
            Lap = _sdk.GetData("CarIdxLap") as int[] ?? [],
            LapDistPct = _sdk.GetData("CarIdxLapDistPct") as float[] ?? [],
            LastLap = _sdk.GetData("CarIdxLastLapTime") as float[] ?? [],
            BestLap = _sdk.GetData("CarIdxBestLapTime") as float[] ?? [],
            F2Time = _sdk.GetData("CarIdxF2Time") as float[] ?? [],
            OnPitRoad = _sdk.GetData("CarIdxOnPitRoad") as bool[] ?? [],
            SessionFlags = _sdk.GetData("CarIdxSessionFlags") as int[] ?? [],
            TireCompound = _sdk.GetData("CarIdxTireCompound") as int[] ?? [],
            SessionType = _currentSessionType,
        };

        if (t.Lap.Length == 0 || t.LapDistPct.Length == 0) return null;

        if (_sdk.GetData("SessionTimeRemain") is double timeRemain) t.SessionTimeRemain = timeRemain;
        if (_sdk.GetData("SessionTime") is double sessionTime) t.SessionTime = sessionTime;
        if (_sdk.GetData("SessionTimeTotal") is double timeTotal) t.SessionTimeTotal = timeTotal;
        if (_sdk.GetData("SessionLapsRemainEx") is int lapsRemain && lapsRemain >= 0 && lapsRemain < 32000)
            t.SessionLapsRemain = lapsRemain;
        t.SessionLapsTotal = _sessionLapsTotal;
        if (_sdk.GetData("TrackTempCrew") is float trackTemp) t.TrackTemp = trackTemp;
        if (_sdk.GetData("PlayerCarMyIncidentCount") is int incs) t.PlayerIncidents = incs;
        if (_sdk.GetData("Precipitation") is float precip) t.Precipitation = precip;
        if (_sdk.GetData("WeatherDeclaredWet") is bool wet) t.DeclaredWet = wet;
        if (_sdk.GetData("TrackWetness") is int wetness) t.TrackWetness = wetness;

        return t;
    }

    /// <summary>iRacing YAML colors look like "0xffcc00"; normalize to "#FFCC00".</summary>
    private static string HexColor(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var s = raw.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return s.Length == 6 ? "#" + s.ToUpperInvariant() : "";
    }

    public void Dispose()
    {
        _running = false;
        _sdk.OnDataChanged -= OnDataChanged;
    }
}
