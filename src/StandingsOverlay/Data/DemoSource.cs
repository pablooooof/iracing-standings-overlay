using StandingsOverlay.Config;

namespace StandingsOverlay.Data;

/// <summary>
/// Fake 20-car race so the overlay can be developed and styled without iRacing running.
/// Laps take ~25 s so the multi-lap delta column comes alive within a minute or two.
/// Feeds the exact same SnapshotBuilder/GapHistory pipeline as the live source.
/// </summary>
public sealed class DemoSource : ITelemetrySource
{
    private const int Cars = 20;
    private const int PlayerIdx = 7;
    private const float DemoLapSeconds = 25f;

    private readonly Func<OverlayConfig> _cfg;
    private readonly GapHistory _history = new();
    private readonly Roster _roster = new();
    private readonly RawTick _tick = new();
    private readonly double[] _totalDist = new double[Cars];
    private readonly float[] _pace = new float[Cars];      // seconds per lap
    private readonly Random _rng = new(42);
    private Timer? _timer;
    private double _elapsed;

    public event Action<StandingsSnapshot>? SnapshotReady;

    public DemoSource(Func<OverlayConfig> cfg)
    {
        _cfg = cfg;

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
            _roster.Drivers[i] = new DriverEntry(
                CarIdx: i,
                Name: names[i],
                CarNumber: (i * 7 % 90 + 1).ToString(),
                IRating: 1200 + _rng.Next(4500),
                LicString: lics[i % lics.Length],
                LicColor: licColors[i % licColors.Length],
                CarClassId: 1,
                ClassColor: "#FFDA59",
                ClassEstLap: DemoLapSeconds,
                IsPaceCar: false,
                IsSpectator: false);

            // Grid spread: leader starts furthest; pace differences make the deltas move.
            _totalDist[i] = 1.0 - i * 0.02;
            _pace[i] = DemoLapSeconds + (i - Cars / 2f) * 0.12f + (float)_rng.NextDouble() * 0.1f;
        }
        _roster.ComputeSof();

        _tick.PlayerCarIdx = PlayerIdx;
        _tick.Position = new int[Cars];
        _tick.Lap = new int[Cars];
        _tick.LapDistPct = new float[Cars];
        _tick.LastLap = new float[Cars];
        _tick.BestLap = new float[Cars];
        _tick.F2Time = new float[Cars];
        _tick.OnPitRoad = new bool[Cars];
        _tick.SessionType = "Race";
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
            // Small per-tick pace jitter, plus a slow "battle" oscillation.
            double jitter = 1.0 + 0.02 * Math.Sin(_elapsed / 7.0 + i * 1.7);
            _totalDist[i] += dt / (_pace[i] * jitter);
            _tick.Lap[i] = (int)_totalDist[i];
            _tick.LapDistPct[i] = (float)(_totalDist[i] - _tick.Lap[i]);
            _tick.LastLap[i] = _pace[i] + (float)Math.Sin(_elapsed / 5.0 + i) * 0.15f;
            _tick.BestLap[i] = _pace[i] - 0.2f;
            _tick.OnPitRoad[i] = false;
        }

        // Positions + gaps from total distance, leader first.
        var order = Enumerable.Range(0, Cars).OrderByDescending(i => _totalDist[i]).ToArray();
        double leaderDist = _totalDist[order[0]];
        for (int p = 0; p < order.Length; p++)
        {
            int i = order[p];
            _tick.Position[i] = p + 1;
            _tick.F2Time[i] = (float)((leaderDist - _totalDist[i]) * DemoLapSeconds);
        }

        _tick.SessionLapsRemain = Math.Max(0, 30 - _tick.Lap[order[0]]);
        _tick.SessionTimeRemain = _tick.SessionLapsRemain * DemoLapSeconds;

        var cfg = _cfg();
        _history.Update(_tick, _roster);
        SnapshotReady?.Invoke(SnapshotBuilder.Build(_tick, _roster, _history, cfg));
    }

    public void Dispose() => _timer?.Dispose();
}
