using StandingsOverlay.Config;

namespace StandingsOverlay.Data;

/// <summary>
/// Fake three-class field (3 GTP + 11 GT3 + 6 GT4) so the overlay can be developed without
/// iRacing. Laps take ~25 s and cars pit every ~8 laps (~35 s stops), so multiclass grouping,
/// the per-lap delta columns AND the strategy predictions all come alive within a few minutes.
/// The GTPs lap the GT3 player every ~2 minutes and one rabbit GT3 laps them every ~4, so the
/// traffic alerter's faster-class and blue-flag paths both fire regularly.
/// Feeds the exact same SnapshotBuilder/GapHistory/StintTracker pipeline as the live source.
/// </summary>
public sealed class DemoSource : ITelemetrySource
{
    private const int Cars = 20;
    private const int GtpCars = 3;           // idx 0-2 = GTP
    private const int Gt3End = 14;           // idx 3-13 = GT3, 14-19 = GT4
    private const int PlayerIdx = 7;
    private const int RabbitIdx = 3;         // GT3 rabbit that laps the player (blue flags)
    private const float GtpLapSeconds = 20.5f;
    private const float Gt3LapSeconds = 25f;
    private const float Gt4LapSeconds = 29f;
    private const float PitDuration = 35f;   // seconds stationary per stop

    private readonly Func<OverlayConfig> _cfg;
    private readonly GapHistory _history = new();
    private readonly StintTracker _stints = new();
    private readonly TrafficDetector _traffic = new();
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
    public event Action<TrafficSnapshot>? TrafficReady;

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
        string[] brands = ["Ferrari", "BMW", "Porsche", "Audi", "McLaren", "Mercedes", "Lambo", "Acura"];

        for (int i = 0; i < Cars; i++)
        {
            // Class layout: GTP (fast prototypes) / GT3 (player's class) / GT4.
            var (classId, className, classColor, classLap) =
                i < GtpCars ? (1, "GTP", "#E33241", GtpLapSeconds)
                : i < Gt3End ? (2, "GT3", "#FFDA59", Gt3LapSeconds)
                : (3, "GT4", "#57C1FF", Gt4LapSeconds);

            _roster.Drivers[i] = new DriverEntry(
                CarIdx: i,
                Name: names[i],
                CarNumber: (i * 7 % 90 + 1).ToString(),
                IRating: 1200 + _rng.Next(4500),
                LicString: lics[i % lics.Length],
                LicColor: licColors[i % licColors.Length],
                CarBrand: Brands.Code(brands[i % brands.Length]),
                CarClassId: classId,
                ClassName: className,
                ClassColor: classColor,
                ClassEstLap: classLap,
                IsPaceCar: false,
                IsSpectator: false);

            int classSlot = i < GtpCars ? i : i < Gt3End ? i - GtpCars : i - Gt3End;
            // Staggered rolling start: GTP half a lap up the road, GT4 half a lap back.
            _totalDist[i] = 1.0 - classSlot * 0.02 + (classId == 1 ? 0.5 : classId == 3 ? -0.5 : 0);
            // GTP paces spread wider so they sometimes arrive as a train, sometimes solo.
            _pace[i] = classId == 1
                ? classLap + classSlot * 0.35f
                : classLap + (classSlot - 5f) * 0.12f + (float)_rng.NextDouble() * 0.1f;
            if (i == RabbitIdx) _pace[i] = 23.0f;   // gains ~2.3 s/lap on the player
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
        _tick.TireCompound = new int[Cars];
        _tick.TrackSurface = new int[Cars];
        _tick.TrackTemp = 31.2f;
        _tick.PlayerIncidents = 3;
        _tick.SessionType = sessionType;
        _tick.SessionTimeTotal = 40 * Gt3LapSeconds;
        _tick.SessionLapsTotal = _isRace ? 40 : sessionType.Contains("Qual", StringComparison.OrdinalIgnoreCase) ? 4 : -1;
        _tick.Precipitation = 0.0f;
        _tick.TrackWetness = 1; // dry
        _tick.SessionState = 4; // racing (never freezes the demo)

        // A little chaos for the status column: every penalty flag kind gets one car.
        _tick.SessionFlags[10] = CarFlags.Repair;     // meatball
        _tick.SessionFlags[16] = CarFlags.Black;
        _tick.SessionFlags[4] = CarFlags.Furled;      // warning
        _tick.SessionFlags[11] = CarFlags.Disqualify;

        // Two cars gambling on wets so the tyre column has variety.
        _tick.TireCompound[5] = 1;
        _tick.TireCompound[14] = 1;
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

            // Car 9 spins and sits stationary for ~8 s every ~90 s so the SPUN badge shows up.
            if (i == 9 && _tick.Lap[i] >= 1 && _elapsed % 90 < 8) continue;

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
        _tick.SessionTime = _elapsed;

        // Traffic-alerter inputs: everyone is on track (pit stall while stopped), and the
        // spotter squawks "car left" whenever another car overlaps the player's position.
        _tick.CarLeftRight = 1; // clear
        for (int i = 0; i < Cars; i++)
        {
            _tick.TrackSurface[i] = _tick.OnPitRoad[i] ? 1 : 3;
            if (i == PlayerIdx || _tick.OnPitRoad[i]) continue;
            double d = Math.Abs(_totalDist[i] - _totalDist[PlayerIdx]) % 1.0;
            if (Math.Min(d, 1.0 - d) < 0.006) _tick.CarLeftRight = 2; // car left
        }

        var cfg = _cfg();
        _history.Update(_tick, _roster);
        _stints.Update(_tick);
        SnapshotReady?.Invoke(SnapshotBuilder.Build(_tick, _roster, _history, _stints, cfg));
        TrafficReady?.Invoke(_traffic.Update(_tick, _roster, cfg));
    }

    public void Dispose() => _timer?.Dispose();
}
