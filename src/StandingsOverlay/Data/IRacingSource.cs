using System.IO;
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
    private readonly TrafficDetector _traffic = new();
    private readonly FuelModel _fuel = new();
    private readonly StrategyPlanner _planner = new();
    private readonly WeatherTracker _weather = new();
    private readonly DriverSwapTracker _driverSwap = new();
    private readonly Roster _roster = new();

    private int _frameCount;
    private int _lastSessionInfoUpdate = -1;
    private int _lastSessionNum = -1;
    private bool _running;

    // Restart survival: delta/stint history is snapshotted next to the exe and restored when
    // the overlay reattaches to the same subsession (see SessionStateStore).
    private readonly string _statePath = Path.Combine(AppContext.BaseDirectory, "session-state.json");
    private string _subSessionId = "";
    private bool _stateRestored;
    private double _lastStateSaveAt = double.MinValue;

    public event Action<StandingsSnapshot>? SnapshotReady;
    public event Action<TrafficSnapshot>? TrafficReady;
    public event Action<RelativeSnapshot>? RelativeReady;
    public event Action<FuelSnapshot>? FuelReady;

    public IRacingSource(Func<OverlayConfig> cfg) => _cfg = cfg;

    public void Start()
    {
        _running = true;
        _sdk.OnDataChanged += OnDataChanged;
        _sdk.OnDisconnected += () =>
        {
            _lastSessionInfoUpdate = -1;
            _lastSessionNum = -1;
            _emitted = false;
            _subSessionId = "";
            _stateRestored = false;
            _lastStateSaveAt = double.MinValue;
            _history.Reset();
            _stints.Reset();
            _traffic.Reset();
            _fuel.Reset();
            _planner.Reset();
            _weather.Reset();
            _driverSwap.Reset();
            lock (_roster) _roster.Drivers.Clear();
            SnapshotReady?.Invoke(StandingsSnapshot.Disconnected);
            TrafficReady?.Invoke(TrafficSnapshot.Empty);
            RelativeReady?.Invoke(RelativeSnapshot.Empty);
            FuelReady?.Invoke(FuelSnapshot.Empty);
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

            UpdateDrivingState(t);

            // After the checkered/cooldown, telemetry goes weird (cars warp to pit, times
            // churn) — freeze the last standings rather than showing garbage.
            if (t.SessionState >= 5 && _emitted) return;

            // Restart survival: reattached to the same subsession → restore delta/stint
            // history saved by the previous run (once, before the trackers see a tick).
            if (!_stateRestored && _subSessionId.Length > 0 && _lastSessionNum >= 0 && t.SessionTime > 0)
            {
                _stateRestored = true;
                SessionStateStore.TryRestore(_statePath, _subSessionId, _lastSessionNum,
                                             t.SessionTime, _history, _stints);
            }

            _history.Update(t, _roster);
            _stints.Update(t);
            _fuel.Update(t);
            _weather.Update(t);
            // A swapped car's recorded pace belongs to the outgoing driver — drop it (and
            // flag the pit visit so the swap overhead isn't read as a tire change).
            foreach (int idx in _driverSwap.Update(t, _roster)) _stints.NoteDriverSwap(idx);
            SnapshotReady?.Invoke(SnapshotBuilder.Build(t, _roster, _history, _stints, _weather, _driverSwap, cfg));
            TrafficReady?.Invoke(_traffic.Update(t, _roster, _history, cfg));
            RelativeReady?.Invoke(RelativeBuilder.Build(t, _roster, _stints, _driverSwap, cfg));
            FuelReady?.Invoke(_planner.Build(t, _fuel, cfg));
            _emitted = true;

            if (_stateRestored && t.SessionTime - _lastStateSaveAt >= 30)
            {
                _lastStateSaveAt = t.SessionTime;
                SessionStateStore.Save(_statePath, _subSessionId, _lastSessionNum,
                                       t.SessionTime, _history, _stints);
            }
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

    private bool _driving = true;
    private bool _pendingDriving = true;
    private double _pendingSince = -1;

    /// <summary>Fires when the player gets in or out of the car (true = driving), debounced.</summary>
    public event Action<bool>? DrivingChanged;

    /// <summary>Driving vs spectating: IsOnTrack drops the moment the player leaves the car
    /// (teammate stint, garage, spectating) but also blips during tows and resets — and a
    /// profile flip re-layouts every widget, so commit only after 5 s of stable signal.</summary>
    private void UpdateDrivingState(RawTick t)
    {
        bool driving = t.IsOnTrack || t.PlayerTowTime > 0;
        if (driving == _driving) { _pendingSince = -1; return; }
        if (_pendingSince < 0 || driving != _pendingDriving)
        {
            _pendingDriving = driving;
            _pendingSince = t.SessionTime;
            return;
        }
        if (t.SessionTime < _pendingSince) _pendingSince = t.SessionTime;   // session clock reset
        if (t.SessionTime - _pendingSince >= 5)
        {
            _driving = driving;
            _pendingSince = -1;
            Log.Write($"player: {(driving ? "in the car" : "spectating")}");
            DrivingChanged?.Invoke(driving);
        }
    }

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
        _subSessionId = model.WeekendInfo?.SubSessionID.ToString() ?? "";

        int sessionNum = _sdk.GetData("SessionNum") is int sn ? sn : -1;
        var session = model.SessionInfo?.Sessions?.FirstOrDefault(x => x.SessionNum == sessionNum)
                      ?? model.SessionInfo?.Sessions?.LastOrDefault();
        _currentSessionType = session?.SessionType ?? "Race";
        _sessionLapsTotal = int.TryParse(session?.SessionLaps, out var sl) && sl > 0 ? sl : -1;

        // Usable tank: BoP can cap the physical tank, so the max-pct multiplier is not optional.
        var di = model.DriverInfo;
        _tankCapacity = di.DriverCarFuelMaxLtr > 0
            ? di.DriverCarFuelMaxLtr * (di.DriverCarMaxFuelPct > 0 ? di.DriverCarMaxFuelPct : 1f)
            : -1f;

        // New session (practice → race, race restart, …): lap/gap/stint history is stale.
        if (sessionNum != _lastSessionNum)
        {
            _lastSessionNum = sessionNum;
            _emitted = false;
            _stateRestored = false;
            _lastStateSaveAt = double.MinValue;
            _history.Reset();
            _stints.Reset();
            _traffic.Reset();
            _fuel.Reset();
            _planner.Reset();
            _weather.Reset();
            _driverSwap.Reset();
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
                    CarBrand: Brands.Code(d.CarScreenNameShort),
                    CarClassId: d.CarClassID,
                    ClassName: d.CarClassShortName ?? "",
                    ClassColor: HexColor(d.CarClassColor),
                    ClassEstLap: d.CarClassEstLapTime,
                    IsPaceCar: d.CarIsPaceCar == "1",
                    IsSpectator: d.IsSpectator == 1);
            }

            // Session results: laps run before we joined, official quali/practice positions,
            // and — when the current session has no results yet (race pre-grid) — the most
            // recent earlier session's results, so the grid matches qualifying.
            _roster.Results.Clear();
            var resultsSession = session?.ResultsPositions is { Count: > 0 } ? session
                : model.SessionInfo?.Sessions?
                    .Where(x => x.SessionNum <= sessionNum && x.ResultsPositions is { Count: > 0 })
                    .OrderBy(x => x.SessionNum).LastOrDefault();
            _roster.ResultsFromCurrentSession = resultsSession == session;
            if (resultsSession?.ResultsPositions is not null)
                foreach (var r in resultsSession.ResultsPositions)
                    _roster.Results[r.CarIdx] = new SessionResult(
                        r.FastestTime, r.LastTime, r.LapsComplete, r.Position, r.ClassPosition);

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
    private float _tankCapacity = -1;
    private bool _emitted;

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
            TrackSurface = _sdk.GetData("CarIdxTrackSurface") as int[] ?? [],
            EstTime = _sdk.GetData("CarIdxEstTime") as float[] ?? [],
            SessionType = _currentSessionType,
        };
        if (_sdk.GetData("CarLeftRight") is int clr) t.CarLeftRight = clr;

        if (t.Lap.Length == 0 || t.LapDistPct.Length == 0) return null;

        if (_sdk.GetData("SessionTimeRemain") is double timeRemain) t.SessionTimeRemain = timeRemain;
        if (_sdk.GetData("SessionTime") is double sessionTime) t.SessionTime = sessionTime;
        if (_sdk.GetData("SessionTimeTotal") is double timeTotal) t.SessionTimeTotal = timeTotal;
        if (_sdk.GetData("SessionLapsRemainEx") is int lapsRemain && lapsRemain >= 0 && lapsRemain < 32000)
            t.SessionLapsRemain = lapsRemain;
        t.SessionLapsTotal = _sessionLapsTotal;
        if (_sdk.GetData("TrackTempCrew") is float trackTemp) t.TrackTemp = trackTemp;
        if (_sdk.GetData("SessionTimeOfDay") is float tod) t.TimeOfDay = tod;
        if (_sdk.GetData("WindVel") is float windVel) t.WindVel = windVel;
        if (_sdk.GetData("WindDir") is float windDir) t.WindDir = windDir;
        if (_sdk.GetData("PlayerCarMyIncidentCount") is int incs) t.PlayerIncidents = incs;
        if (_sdk.GetData("PlayerCarTowTime") is float towTime) t.PlayerTowTime = towTime;
        if (_sdk.GetData("IsOnTrack") is bool onTrack) t.IsOnTrack = onTrack;
        if (_sdk.GetData("Precipitation") is float precip) t.Precipitation = precip;
        if (_sdk.GetData("WeatherDeclaredWet") is bool wet) t.DeclaredWet = wet;
        if (_sdk.GetData("TrackWetness") is int wetness) t.TrackWetness = wetness;
        if (_sdk.GetData("SessionState") is int state) t.SessionState = state;

        if (_sdk.GetData("FuelLevel") is float fuelLevel) t.PlayerFuelLevel = fuelLevel;
        if (_sdk.GetData("PlayerCarInPitStall") is bool stall) t.PlayerInPitStall = stall;
        if (_sdk.GetData("SessionFlags") is int flags) t.GlobalFlags = flags;
        t.TankCapacity = _tankCapacity;

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
