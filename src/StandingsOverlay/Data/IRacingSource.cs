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
    private readonly Roster _roster = new();

    private int _frameCount;
    private int _lastSessionInfoUpdate = -1;
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
            _history.Reset();
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
            SnapshotReady?.Invoke(SnapshotBuilder.Build(t, _roster, _history, cfg));
        }
        catch (Exception)
        {
            // A torn read or YAML quirk on one tick is harmless; the next tick recovers.
        }
    }

    private void RefreshRosterIfStale()
    {
        var header = _sdk.Header;
        if (header is null || header.SessionInfoUpdate == _lastSessionInfoUpdate) return;

        var yaml = _sdk.GetSessionInfo();
        if (yaml is null) return;

        var model = IRacingSessionModel.Serialize(yaml);
        if (model?.DriverInfo?.Drivers is null) return;

        _lastSessionInfoUpdate = header.SessionInfoUpdate;
        _currentSessionType = FindSessionType(model);

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
                    CarClassId: d.CarClassID,
                    ClassColor: HexColor(d.CarClassColor),
                    ClassEstLap: d.CarClassEstLapTime,
                    IsPaceCar: d.CarIsPaceCar == "1",
                    IsSpectator: d.IsSpectator == 1);
            }
            _roster.TrackName = model.WeekendInfo?.TrackDisplayShortName ?? "";
            _roster.ComputeSof();
        }
    }

    private string _currentSessionType = "Race";

    private string FindSessionType(IRacingSessionModel model)
    {
        int sessionNum = _sdk.GetData("SessionNum") is int n ? n : -1;
        var s = model.SessionInfo?.Sessions?.FirstOrDefault(x => x.SessionNum == sessionNum)
                ?? model.SessionInfo?.Sessions?.LastOrDefault();
        return s?.SessionType ?? "Race";
    }

    private RawTick? ReadTick()
    {
        if (_sdk.GetData("PlayerCarIdx") is not int playerIdx) return null;
        if (_sdk.GetData("CarIdxPosition") is not int[] pos) return null;

        var t = new RawTick
        {
            PlayerCarIdx = playerIdx,
            Position = pos,
            Lap = _sdk.GetData("CarIdxLap") as int[] ?? [],
            LapDistPct = _sdk.GetData("CarIdxLapDistPct") as float[] ?? [],
            LastLap = _sdk.GetData("CarIdxLastLapTime") as float[] ?? [],
            BestLap = _sdk.GetData("CarIdxBestLapTime") as float[] ?? [],
            F2Time = _sdk.GetData("CarIdxF2Time") as float[] ?? [],
            OnPitRoad = _sdk.GetData("CarIdxOnPitRoad") as bool[] ?? [],
            SessionType = _currentSessionType,
        };

        if (t.Lap.Length == 0 || t.LapDistPct.Length == 0) return null;

        if (_sdk.GetData("SessionTimeRemain") is double timeRemain) t.SessionTimeRemain = timeRemain;
        if (_sdk.GetData("SessionLapsRemainEx") is int lapsRemain && lapsRemain >= 0 && lapsRemain < 32000)
            t.SessionLapsRemain = lapsRemain;

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
