using StandingsOverlay.Config;

namespace StandingsOverlay.Data;

/// <summary>
/// Fake two-class field (12 GT3 + 8 GT4) so the overlay can be developed without iRacing.
/// Laps take ~25 s and cars pit every ~8 laps (~35 s stops), so multiclass grouping, the
/// per-lap delta columns AND the strategy predictions all come alive within a few minutes.
/// Feeds the exact same SnapshotBuilder/GapHistory/StintTracker pipeline as the live source.
/// </summary>
public sealed class DemoSource : ITelemetrySource
{
    private const int Cars = 20;
    private const int Gt3Cars = 12;          // idx 0-11 = GT3, 12-19 = GT4
    private const int PlayerIdx = 7;
    private const float Gt3LapSeconds = 25f;
    private const float Gt4LapSeconds = 29f;
    private const float PitDuration = 35f;   // seconds stationary per stop

    private readonly Func<OverlayConfig> _cfg;
    private readonly GapHistory _history = new();
    private readonly StintTracker _stints = new();
    private readonly Roster _roster = new();
    private readonly RawTick _tick = new();
    private readonly double[] _totalDist = new double[Cars];
    private readonly float[] _pace = new float[Cars];       // seconds per lap
    private readonly int[] _stintLaps = new int[Cars];      // laps between stops
    private readonly int[] _lastPitLap = new int[Cars];
    private readonly double[] _pitUntil = new double[Cars]; // elapsed time when the stop ends
    private readonly Random _rng = new(42);
    private Timer? _timer;
    private double _elapsed;

    private readonly bool _isRace;

    public event Action<StandingsSnapshot>? SnapshotReady;

    public DemoSource(Func<OverlayConfig> cfg, string sessionType = "Race")
    {
        _cfg = cfg;
        _isRace = sessionType.Contains("Race", StringComparison.OrdinalIgnoreCase);

        string[] names =
        [
            "Max Verschtappen", "Lando Norrise", "Charles LeClerk", "Oscar Piastry",
            "Lewis Hamiltone", "George Russel", "Fernando Alonzo", "Pablo Pizarro",
            "Carlos Sainzz", "Sergio Perezz", "Esteban Ocone", "Pierre Gaslee",
            "Yuki Tsunodaa", "Alex Albone", "Nico Hulkenberg", "Kevin Magnusen",
            "Valtteri Bottass", "Zhou Guanyou", "Logan Sargent", "Daniel Ricciardoo",
        ];
        string[] lics = ["A 4.99", "A 3.51", "B 3.20", "A 4.35", "B 2.87", "A 4.12"];
        string[] licColors = ["#0153DB", "#0153DB", "#00C702", "#0153DB", "#00C702", "#0153DB"];

        for (int i = 0; i < Cars; i++)
        {
            bool gt3 = i < Gt3Cars;
            float classLap = gt3 ? Gt3LapSeconds : Gt4LapSeconds;

            _roster.Drivers[i] = new DriverEntry(
                CarIdx: i,
                Name: names[i],
                CarNumber: (i * 7 % 90 + 1).ToString(),
                IRating: 1200 + _rng.Next(4500),
                LicString: lics[i % lics.Length],
                LicColor: licColors[i % licColors.Length],
                CarClassId: gt3 ? 1 : 2,
                ClassName: gt3 ? "GT3" : "GT4",
                ClassColor: gt3 ? "#FFDA59" : "#57C1FF",
                ClassEstLap: classLap,
                IsPaceCar: false,
                IsSpectator: false);

            int classSlot = gt3 ? i : i - Gt3Cars;
            _totalDist[i] = 1.0 - classSlot * 0.02 - (gt3 ? 0 : 0.5);
            _pace[i] = classLap + (classSlot - 5f) * 0.12f + (float)_rng.NextDouble() * 0.1f;
            _stintLaps[i] = 7 + _rng.Next(3);   // pit every 7-9 laps
        }
        _roster.ComputeSof();

        _tick.PlayerCarIdx = PlayerIdx;
        _tick.Position = new int[Cars];
        _tick.ClassPosition = new int[Cars];
        _tick.Lap = new int[Cars];
        _tick.LapDistPct = new float[Cars];
        _tick.LastLap = new float[Cars];
        _tick.BestLap = new float[Cars];
        _tick.F2Time = new float[Cars];
        _tick.OnPitRoad = new bool[Cars];
        _tick.SessionFlags = new int[Cars];
        _tick.TrackTemp = 31.2f;
        _tick.PlayerIncidents = 3;
        _tick.SessionType = sessionType;

        // A little chaos for the status column: one car with a meatball, one black-flagged.
        _tick.SessionFlags[10] = CarFlags.Repair;
        _tick.SessionFlags[16] = CarFlags.Black;
    }

    public void Start()
    {
        var cfg = _cfg();
        int periodMs = 1000 / Math.Clamp(cfg.UpdateHz, 1, 10);
        _timer = new Timer(_ => Step(periodMs / 1000.0), null, 0, periodMs);
    }

    private void Step(double dt)
    {
        _elapsed += dt;

        for (int i = 0; i < Cars; i++)
        {
            // In the pits: stationary at the start/finish area until the stop ends.
            if (_elapsed < _pitUntil[i]) { _tick.OnPitRoad[i] = true; continue; }
            _tick.OnPitRoad[i] = false;

            // Pit when the stint is up (crossing the line on the pit lap). Races only.
            if (_isRace && _tick.Lap[i] - _lastPitLap[i] >= _stintLaps[i] && _tick.LapDistPct[i] < 0.05f)
            {
                _lastPitLap[i] = _tick.Lap[i];
                _pitUntil[i] = _elapsed + PitDuration + _rng.Next(6);
                _tick.OnPitRoad[i] = true;
                continue;
            }

            // Small per-tick pace jitter, plus a slow "battle" oscillation. Two GT4 cars
            // fuel-save (steady but slow) so the pace tags have something to find.
            double jitter = 1.0 + 0.02 * Math.Sin(_elapsed / 7.0 + i * 1.7);
            if (i is 14 or 17) jitter = 1.022; // fuel saving: consistent +2.2%
            _totalDist[i] += dt / (_pace[i] * jitter);
            int newLap = (int)_totalDist[i];
            bool crossed = newLap != _tick.Lap[i];
            _tick.Lap[i] = newLap;
            _tick.LapDistPct[i] = (float)(_totalDist[i] - newLap);
            _tick.LastLap[i] = _pace[i] * (float)jitter + (i is 14 or 17 ? 0f : (float)Math.Sin(_elapsed / 5.0 + i) * 0.15f);
            // Best = fastest recorded lap, latched at the crossing like iRacing does.
            if (crossed && (_tick.BestLap[i] <= 0 || _tick.LastLap[i] < _tick.BestLap[i]))
                _tick.BestLap[i] = _tick.LastLap[i];
        }

        // Positions + gaps from total distance: overall and per class.
        var order = Enumerable.Range(0, Cars).OrderByDescending(i => _totalDist[i]).ToArray();
        double leaderDist = _totalDist[order[0]];
        var classCounters = new Dictionary<int, int>();
        for (int p = 0; p < order.Length; p++)
        {
            int i = order[p];
            _tick.Position[i] = p + 1;
            _tick.F2Time[i] = (float)((leaderDist - _totalDist[i]) * Gt3LapSeconds);
            int clsId = _roster.Drivers[i].CarClassId;
            classCounters[clsId] = classCounters.GetValueOrDefault(clsId) + 1;
            _tick.ClassPosition[i] = classCounters[clsId];
        }

        _tick.SessionLapsRemain = Math.Max(0, 40 - _tick.Lap[order[0]]);
        _tick.SessionTimeRemain = _tick.SessionLapsRemain * Gt3LapSeconds;

        var cfg = _cfg();
        _history.Update(_tick, _roster);
        _stints.Update(_tick);
        SnapshotReady?.Invoke(SnapshotBuilder.Build(_tick, _roster, _history, _stints, cfg));
    }

    public void Dispose() => _timer?.Dispose();
}
