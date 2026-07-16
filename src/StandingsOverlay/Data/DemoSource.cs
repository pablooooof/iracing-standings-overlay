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

    // Player fuel: 30 L tank at 2.4 L/lap ≈ 12-lap stints, sized so the strategy planner's
    // "push + splash" vs "save a tenth, one stop fewer" fork shows up mid-demo.
    private const float PlayerTank = 30f;
    private const float PlayerBurnPerLap = 2.4f;
    private const float DemoFillRate = 2.5f;  // L/s while stopped

    private readonly Func<OverlayConfig> _cfg;
    private readonly GapHistory _history = new();
    private readonly StintTracker _stints = new();
    private readonly TrafficDetector _traffic = new();
    private readonly FuelModel _fuel = new();
    private readonly StrategyPlanner _planner = new();
    private readonly WeatherTracker _weather = new();
    private readonly DriverSwapTracker _driverSwap = new();
    private readonly SectorClock _sectorClock = new();
    private readonly LapLabTracker _lapLab = new();
    private readonly LapRefStore _refStore = new();
    private readonly Roster _roster = new();
    private bool _swapDone;
    private double _playerFuel = PlayerTank;
    private readonly RawTick _tick = new();
    // Laps completed + time-fraction of the current lap (time domain; TrackPct maps to distance).
    private readonly double[] _progress = new double[Cars];
    private readonly float[] _pace = new float[Cars];       // seconds per lap
    private readonly int[] _stintLaps = new int[Cars];      // laps between stops
    private readonly int[] _lastPitLap = new int[Cars];
    private readonly double[] _pitStart = new double[Cars]; // elapsed time when the visit began
    private readonly double[] _pitUntil = new double[Cars]; // elapsed time when the stop ends
    private bool _towing9;                                   // car 9's scripted tow is active
    private readonly Random _rng = new(42);
    private Timer? _timer;
    private double _elapsed;

    private readonly bool _isRace;
    // Timed sprint: join the last ~2.6 laps of a time-limited race so the race-end / extra-lap
    // estimator is exercised immediately instead of after a 40-minute wait.
    private readonly bool _timed;
    private static readonly double TimedTailSeconds = 2.6 * Gt3LapSeconds;
    // "--demo rain": a scripted dry→wet arc so the weather trend arrows and the dry→wet flash are
    // exercisable offline (rain starts ramping ~25 s in, track goes damp→wet over the next minute).
    private readonly bool _rain;
    // "--demo lab": offline-testing session with scripted player sector variance (S2 is the
    // erratic corner complex), an off-track every 8th lap, and one active reset at ~112 s —
    // everything Lap Lab's table needs to show its colors without iRacing.
    private readonly bool _lab;
    private double _prevPlayerProgress;
    private double _prevElapsed;
    private bool _labResetDone;

    public event Action<StandingsSnapshot>? SnapshotReady;
    public event Action<TrafficSnapshot>? TrafficReady;
    public event Action<RelativeSnapshot>? RelativeReady;
    public event Action<FuelSnapshot>? FuelReady;
    public event Action<LapLabSnapshot>? LapLabReady;

    public DemoSource(Func<OverlayConfig> cfg, string sessionType = "Race", bool timed = false, bool rain = false)
    {
        _cfg = cfg;
        _isRace = sessionType.Contains("Race", StringComparison.OrdinalIgnoreCase);
        _timed = timed && _isRace;
        _rain = rain;
        _lab = sessionType.Contains("Testing", StringComparison.OrdinalIgnoreCase);
        // Three equal-distance sectors; with the non-uniform TrackPct speed profile the middle
        // sector is the slow hairpin third, so sector TIMES come out realistically uneven.
        _sectorClock.SetBoundaries([0f, 1f / 3f, 2f / 3f]);
        // Identity for Lap Lab's previous-best key + conditions guard: a real Spa/GT3 .ibt
        // picked as File ref in demo mode exercises the "≠ track" BLOCK chip by design.
        _roster.TrackName = "Demo Ring";
        _roster.TrackId = 990001;
        _roster.TrackConfig = "Demo";
        _roster.PlayerCarPath = "demo_gt3";
        _roster.PlayerCarName = "Demo GT3";
        _roster.RubberState = "moderately low usage";

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
            // Colours follow the common iRacing convention: GTP/prototype yellow, GT3 pink,
            // the slower class blue (like LMP2). Live sessions use iRacing's own class colours.
            var (classId, className, classColor, classLap) =
                i < GtpCars ? (1, "GTP", "#FFD24D", GtpLapSeconds)
                : i < Gt3End ? (2, "GT3", "#FF5FA8", Gt3LapSeconds)
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
            _progress[i] = 1.0 - classSlot * 0.02 + (classId == 1 ? 0.5 : classId == 3 ? -0.5 : 0);
            // GTP paces spread wider so they sometimes arrive as a train, sometimes solo.
            _pace[i] = classId == 1
                ? classLap + classSlot * 0.35f
                : classLap + (classSlot - 5f) * 0.12f + (float)_rng.NextDouble() * 0.1f;
            if (i == RabbitIdx) _pace[i] = 23.0f;   // gains ~2.3 s/lap on the player
            _stintLaps[i] = 7 + _rng.Next(3);   // pit every 7-9 laps
        }
        // Car 9 (#64) never makes scheduled stops — its pit visits are the scripted spin/tow
        // incidents, so the TOW badge can't be masked by a regular PIT overlapping the window.
        _stintLaps[9] = 999;
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
        _tick.EstTime = new float[Cars];
        _tick.TrackTemp = 31.2f;
        _tick.WindVel = 3.3f;              // ~12 km/h
        _tick.WindDir = 2.36f;             // ~SW, so the header arrow points NE
        _tick.PlayerIncidents = 3;
        _tick.SessionType = sessionType;
        _tick.SessionTimeTotal = _timed ? TimedTailSeconds : 40 * Gt3LapSeconds;
        _tick.SessionLapsTotal = _timed ? -1
            : _isRace ? 40 : sessionType.Contains("Qual", StringComparison.OrdinalIgnoreCase) ? 4 : -1;
        _tick.Precipitation = 0.0f;
        _tick.TrackWetness = 1; // dry
        _tick.SessionState = 4; // racing (never freezes the demo)
        _tick.TankCapacity = PlayerTank;

        // A little chaos for the status column: every penalty flag kind gets one car.
        _tick.SessionFlags[10] = CarFlags.Repair;     // meatball
        _tick.SessionFlags[16] = CarFlags.Black;
        _tick.SessionFlags[4] = CarFlags.Furled;      // warning
        _tick.SessionFlags[11] = CarFlags.Disqualify;

        // Two cars gambling on wets so the tyre column has variety.
        _tick.TireCompound[5] = 1;
        _tick.TireCompound[14] = 1;
    }

    /// <summary>Demo track speed profile: distance fraction covered as a function of the lap's
    /// time-fraction. Fast down the S/F straight, a slow mid-lap hairpin complex (speed swings
    /// 0.55×–1.45× of average), so LapDistPct and EstTime diverge like on a real road course —
    /// a regression toward distance-based gap math shows up in the demo as breathing gaps and
    /// phantom closing rates instead of hiding behind a uniform-speed track.</summary>
    internal static float TrackPct(float tf)
        => tf + 0.45f / (2 * MathF.PI) * MathF.Sin(2 * MathF.PI * tf);

    public void Start()
    {
        var cfg = _cfg();
        int periodMs = 1000 / Math.Clamp(cfg.UpdateHz, 1, 10);
        _timer = new Timer(_ => Step(periodMs / 1000.0), null, 0, periodMs);
    }

    // Test hooks: drive the demo clock without the timer, so the scripted scenarios
    // (spin, tow, pit sequences) are assertable deterministically end to end.
    internal RawTick TickForTest => _tick;
    internal Roster RosterForTest => _roster;
    internal void StepForTest(double dt) => Step(dt);

    private void Step(double dt)
    {
        _elapsed += dt;

        for (int i = 0; i < Cars; i++)
        {
            // In the pits: stationary at the start/finish area until the stop ends.
            if (_elapsed < _pitUntil[i])
            {
                _tick.OnPitRoad[i] = true;
                if (i == PlayerIdx) _playerFuel = Math.Min(PlayerTank, _playerFuel + DemoFillRate * dt);
                continue;
            }
            _tick.OnPitRoad[i] = false;

            // Car 9 alternates incidents: an ~8 s stationary spin every ~90 s (SPUN badge), and
            // every third cycle the spin ends in a tow — the car teleports back to its pit stall
            // WITHOUT ever reading "approaching pits", the exact transition the TOW rule keys on.
            if (i == 9 && _tick.Lap[i] >= 1)
            {
                double inCycle = _elapsed % 90;
                bool towCycle = (int)(_elapsed / 90) % 3 == 2;
                if (towCycle && inCycle >= 8 && inCycle < 28)
                {
                    if (!_towing9) { _towing9 = true; _progress[9] = Math.Floor(_progress[9]) + 0.0002; }
                    _tick.OnPitRoad[9] = true;
                    continue;
                }
                _towing9 = false;
                if (inCycle < 8) continue;   // spinning: stationary on track
            }

            // Pit when the stint is up (crossing the line on the pit lap). Races only.
            if (_isRace && _tick.Lap[i] - _lastPitLap[i] >= _stintLaps[i] && _tick.LapDistPct[i] < 0.05f)
            {
                _lastPitLap[i] = _tick.Lap[i];
                _pitStart[i] = _elapsed;
                _pitUntil[i] = _elapsed + PitDuration + _rng.Next(6);
                _tick.OnPitRoad[i] = true;
                // Wet enough to warrant a tyre switch → they come out on wets (fires the alert).
                if (_rain && _tick.TrackWetness >= 4) _tick.TireCompound[i] = 1;
                continue;
            }

            // Small per-tick pace jitter, plus a slow "battle" oscillation. Two GT4 cars
            // fuel-save (steady but slow) so the pace tags have something to find.
            double jitter = 1.0 + 0.02 * Math.Sin(_elapsed / 7.0 + i * 1.7);
            if (i is 14 or 17) jitter = 1.022; // fuel saving: consistent +2.2%
            if (_lab && i == PlayerIdx)
                jitter = 1.0 + LabLoss(_tick.Lap[i], _progress[i] - Math.Floor(_progress[i]));
            _progress[i] += dt / (_pace[i] * jitter);
            if (i == PlayerIdx)
                _playerFuel = Math.Max(0, _playerFuel - PlayerBurnPerLap * dt / (_pace[i] * jitter));
            int newLap = (int)_progress[i];
            bool crossed = newLap != _tick.Lap[i];
            _tick.Lap[i] = newLap;
            float tf = (float)(_progress[i] - newLap);          // time-fraction of the lap
            _tick.LapDistPct[i] = TrackPct(tf);
            // The sim's positional est curve: time-fraction × class lap, exact by construction.
            _tick.EstTime[i] = tf * _roster.Drivers[i].ClassEstLap;
            _tick.LastLap[i] = _pace[i] * (float)jitter + (i is 14 or 17 ? 0f : (float)Math.Sin(_elapsed / 5.0 + i) * 0.15f);
            // Best = fastest recorded lap, latched at the crossing like iRacing does.
            if (crossed && (_tick.BestLap[i] <= 0 || _tick.LastLap[i] < _tick.BestLap[i]))
                _tick.BestLap[i] = _tick.LastLap[i];
        }

        // Positions + gaps from total lap progress (time domain): overall and per class.
        var order = Enumerable.Range(0, Cars).OrderByDescending(i => _progress[i]).ToArray();
        double leaderDist = _progress[order[0]];
        var classCounters = new Dictionary<int, int>();
        for (int p = 0; p < order.Length; p++)
        {
            int i = order[p];
            _tick.Position[i] = p + 1;
            _tick.F2Time[i] = (float)((leaderDist - _progress[i]) * Gt3LapSeconds);
            int clsId = _roster.Drivers[i].CarClassId;
            classCounters[clsId] = classCounters.GetValueOrDefault(clsId) + 1;
            _tick.ClassPosition[i] = classCounters[clsId];
        }

        if (_timed)
        {
            // Time-limited: the clock, not a lap target, ends it; laps remaining is unknown
            // (that's exactly what the estimator infers from the leader's pace).
            _tick.SessionTimeRemain = Math.Max(0, TimedTailSeconds - _elapsed);
            _tick.SessionLapsRemain = -1;
        }
        else
        {
            _tick.SessionLapsRemain = Math.Max(0, 40 - _tick.Lap[order[0]]);
            _tick.SessionTimeRemain = _tick.SessionLapsRemain * Gt3LapSeconds;
        }
        _tick.SessionTime = _elapsed;
        _tick.TimeOfDay = 14 * 3600 + 30 * 60 + _elapsed;   // starts 14:30, clock ticks with the session

        // Weather: the track cools slowly through the session (temp trend arrow → falling). With
        // "--demo rain" a shower moves in ~25 s in and the track goes dry→damp→wet (precip arrow
        // rising + the dry→wet header flash).
        _tick.TrackTemp = 31.2f - (float)(_elapsed * 0.015);
        if (_rain)
        {
            float ramp = (float)Math.Clamp((_elapsed - 25) / 45.0, 0, 1);
            _tick.Precipitation = ramp * 0.55f;
            _tick.TrackWetness = ramp <= 0 ? 1 : ramp < 0.35f ? 2 : ramp < 0.7f ? 4 : 6;
            _tick.DeclaredWet = ramp >= 0.7f;
        }

        // Traffic-alerter inputs: surfaces mirror iRacing's pit sequence — a normal stop reads
        // "approaching pits"(2) briefly before the stall(1); car 9's tow lands straight on 1.
        // The spotter squawks "car left" whenever another car overlaps the player's position.
        _tick.CarLeftRight = 1; // clear
        for (int i = 0; i < Cars; i++)
        {
            _tick.TrackSurface[i] =
                i == 9 && _towing9 ? 1
                : _tick.OnPitRoad[i] ? (_elapsed - _pitStart[i] < 2.5 ? 2 : 1)
                : 3;
            if (i == PlayerIdx || _tick.OnPitRoad[i]) continue;
            double d = Math.Abs(_progress[i] - _progress[PlayerIdx]) % 1.0;
            if (Math.Min(d, 1.0 - d) < 0.006) _tick.CarLeftRight = 2; // car left
        }

        _tick.PlayerFuelLevel = (float)_playerFuel;
        _tick.PlayerInPitStall = _tick.OnPitRoad[PlayerIdx];

        // Lap Lab: one scripted active reset — the player teleports a quarter lap back exactly
        // like iRacing's AR Run button, so the abandon-and-rearm path is exercisable offline.
        if (_lab && !_labResetDone && _elapsed > 112 &&
            _progress[PlayerIdx] - Math.Floor(_progress[PlayerIdx]) > 0.45)
        {
            _labResetDone = true;
            _progress[PlayerIdx] -= 0.25;
        }

        // Feed the player's motion to the sector clock in sub-steps: the demo ticks at the
        // snapshot rate (4 Hz), far below the 60 Hz the live clock samples at, and progress is
        // linear in time within a step — so interpolated sub-samples recover most of the
        // crossing accuracy (TrackPct is evaluated per sub-sample, not linearized).
        {
            double playerTf = _progress[PlayerIdx] - Math.Floor(_progress[PlayerIdx]);
            int playerSurf = _lab && _tick.Lap[PlayerIdx] % 8 == 4 && playerTf is > 0.45 and < 0.55 ? 0 : 3;
            double prog = _progress[PlayerIdx];
            if (_prevElapsed > 0 && prog > _prevPlayerProgress && prog - _prevPlayerProgress < 0.5)
            {
                const int Sub = 8;
                const float TrackLenM = 1000;   // nominal, only feeds the reference speed grid
                float prevPct2 = TrackPct((float)(_prevPlayerProgress - Math.Floor(_prevPlayerProgress)));
                for (int k = 1; k <= Sub; k++)
                {
                    double p = _prevPlayerProgress + (prog - _prevPlayerProgress) * k / Sub;
                    double tm = _prevElapsed + (_elapsed - _prevElapsed) * (double)k / Sub;
                    float pct = TrackPct((float)(p - Math.Floor(p)));
                    double dtSub = (_elapsed - _prevElapsed) / Sub;
                    float dp = pct - prevPct2;
                    if (dp < -0.5f) dp += 1;
                    float speed = dtSub > 0 ? (float)(dp * TrackLenM / dtSub) : float.NaN;
                    _sectorClock.Sample(pct, tm, playerSurf, _tick.OnPitRoad[PlayerIdx], speed);
                    prevPct2 = pct;
                }
            }
            else if (_prevElapsed > 0)   // teleport (scripted AR): one raw sample, no interpolation
            {
                _sectorClock.Sample(TrackPct((float)playerTf), _elapsed,
                                    playerSurf, _tick.OnPitRoad[PlayerIdx]);
            }
            _prevPlayerProgress = prog;
            _prevElapsed = _elapsed;
        }

        var cfg = _cfg();
        // ~35s in, one team swaps drivers (roster entry gets a new name) so the SWAP tag is
        // exercisable offline.
        if (!_swapDone && _elapsed > 35)
        {
            _swapDone = true;
            var old = _roster.Drivers[6];
            _roster.Drivers[6] = old with { Name = "F. Alesi" };
        }
        // ~18s in, one car switches to wets so the inline o→o tyre-switch marker is exercisable.
        if (_elapsed > 18 && _tick.TireCompound[8] == 0) _tick.TireCompound[8] = 1;

        _history.Update(_tick, _roster);
        _stints.Update(_tick);
        _fuel.Update(_tick);
        _weather.Update(_tick);
        // A swapped car's recorded pace belongs to the outgoing driver — drop it (and
        // flag the pit visit so the swap overhead isn't read as a tire change).
        foreach (int idx in _driverSwap.Update(_tick, _roster)) _stints.NoteDriverSwap(idx);
        SnapshotReady?.Invoke(SnapshotBuilder.Build(_tick, _roster, _history, _stints, _weather, _driverSwap, cfg));
        TrafficReady?.Invoke(_traffic.Update(_tick, _roster, _history, _stints, cfg));
        RelativeReady?.Invoke(RelativeBuilder.Build(_tick, _roster, _stints, _driverSwap, cfg));
        FuelReady?.Invoke(_planner.Build(_tick, _fuel, cfg));
        LapLabReady?.Invoke(_lapLab.Build(_tick, _sectorClock, _roster, _refStore, cfg));
    }

    /// <summary>Scripted lab-mode player pace: a repeating per-lap loss factor over the middle
    /// third (S2, the hairpin complex — deliberately the weak, erratic sector) plus light drift
    /// elsewhere. Index 4 is the big excursion that pairs with the scripted off-track.</summary>
    private static double LabLoss(int lap, double tf)
    {
        ReadOnlySpan<double> s2 = [0.030, 0.012, 0.045, 0.020, 0.140, 0.006, 0.028, 0.016];
        double mid = tf is > 0.31 and < 0.69 ? s2[lap % s2.Length] : 0;
        return mid + 0.004 * Math.Sin(lap * 2.1 + tf * 6.283);
    }

    public void Dispose() => _timer?.Dispose();
}
