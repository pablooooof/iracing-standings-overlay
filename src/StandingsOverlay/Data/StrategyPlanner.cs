using StandingsOverlay.Config;

namespace StandingsOverlay.Data;

public enum FuelSegKind { Past, Push, Save, Splash }

/// <summary>One horizontal slice of a strategy bar. Frac = share of the whole race (0..1),
/// rounded to 3 decimals so equal-looking bars compare equal and don't repaint.</summary>
public readonly record struct FuelSeg(double Frac, FuelSegKind Kind);

/// <summary>One Pirelli-style strategy row: segments spanning the full race, past dimmed.</summary>
public sealed record FuelStrategyBar(string Label, string DeltaText, IReadOnlyList<FuelSeg> Segs)
{
    public bool VisuallyEquals(FuelStrategyBar o) =>
        Label == o.Label && DeltaText == o.DeltaText && Segs.SequenceEqual(o.Segs);
}

public sealed record FuelSnapshot(
    bool Show,               // false = no fuel data at all (disconnected / no player car)
    string FuelText,         // "43.2L"
    string PerLapText,       // "2.31/lap"
    string LapsText,         // "18.7 laps"
    string TargetText,       // "tgt 2.21" — per-lap number that makes the planned window
    string PlanText,         // "next stop ~L168 · add 74L · 214 laps to go"
    string RaceText,         // timed races: "≈24 laps · you 12 to go · extra lap likely"
    int RaceEmphasis,        // 0 normal · 1 borderline · 2 extra-lap likely (colors RaceText)
    string FinishText,       // "finish on 41.8L · 2.4L in hand" — the exact fuel-to-the-flag number
    int FinishEmphasis,      // 0 ok (green) · 1 over-fuelled (amber) · 2 short (red)
    string SaveText,         // "save 0.09/lap to skip the stop (12s faster)" — the fuel-save fork
    double NowFrac,          // 0..1 position of the now marker on the bars
    IReadOnlyList<FuelStrategyBar> Bars)
{
    public static readonly FuelSnapshot Empty = new(false, "", "", "", "", "", "", 0, "", 0, "", 0, []);

    public bool VisuallyEquals(FuelSnapshot? o)
    {
        if (o is null) return false;
        if (Show != o.Show || FuelText != o.FuelText || PerLapText != o.PerLapText ||
            LapsText != o.LapsText || TargetText != o.TargetText || PlanText != o.PlanText ||
            RaceText != o.RaceText || RaceEmphasis != o.RaceEmphasis ||
            FinishText != o.FinishText || FinishEmphasis != o.FinishEmphasis || SaveText != o.SaveText ||
            Math.Abs(NowFrac - o.NowFrac) > 0.004 || Bars.Count != o.Bars.Count) return false;
        for (int i = 0; i < Bars.Count; i++)
            if (!Bars[i].VisuallyEquals(o.Bars[i])) return false;
        return true;
    }
}

/// <summary>
/// Endurance strategy solver. A strategy = (stops remaining k, fuel-save level s L/lap); for
/// each viable stop count it finds the minimum save level that makes the fuel reach, prices the
/// whole thing (lap-time penalty of saving vs pit stops), and emits the best few as stint bars.
/// The classic fork appears by construction: "push + splash-and-dash" vs "save, one stop fewer".
///
/// Plans are anchored to the player's lap crossing and held for the lap — planning from mid-lap
/// fuel makes the integer stint floors creep tick by tick (the save level walks up until the
/// fork collapses, then snaps back at the line). The live numbers row still updates every tick.
/// Works identically in demo mode. Spec: docs/FUEL-STRATEGY.md.
/// </summary>
public sealed class StrategyPlanner
{
    private const double FallbackPitLoss = 45;   // s, until a real stop is measured
    private const double FallbackFillRate = 2.6; // L/s

    // Plan cache: recompute only when one of these moves.
    private int _planLap = int.MinValue;
    private bool _planOnPit;
    private int _planGreenLaps = -1;
    private OverlayConfig? _planCfg;

    private string _targetText = "", _planText = "", _saveText = "";
    private double _nowFrac;
    private IReadOnlyList<FuelStrategyBar> _bars = [];

    public void Reset()
    {
        _planLap = int.MinValue;
        _planOnPit = false;
        _planGreenLaps = -1;
        _planCfg = null;
        _targetText = _planText = _saveText = "";
        _nowFrac = 0;
        _bars = [];
    }

    public FuelSnapshot Build(RawTick t, FuelModel fuel, OverlayConfig cfg)
    {
        var fc = cfg.Fuel;
        int p = t.PlayerCarIdx;
        if (!fc.Enabled || !t.Has(p) || float.IsNaN(t.PlayerFuelLevel)) return FuelSnapshot.Empty;

        double fuelNow = t.PlayerFuelLevel;
        double perLap = fuel.GreenPerLap;
        double tank = t.TankCapacity;
        double pace = fuel.PaceSec;

        string fuelText = $"{fuelNow:0.0}L";
        string perLapText = perLap > 0 ? $"{perLap:0.00}/lap" : "—/lap";
        string lapsText = perLap > 0 ? $"{fuelNow / perLap:0.0} laps" : "";

        bool isRace = StandingsSnapshot.KindOf(t.SessionType) == SessionKind.Race;
        bool lapMode = t.SessionLapsTotal > 0 && t.SessionLapsRemain >= 0;

        // Race-distance readout: only meaningful (and uncertain) in a timed race, but it does NOT
        // need fuel confidence — it rides on the leader's clock, so compute it every tick and show
        // it even before we trust the per-lap number. This is the live "extra lap or not?" answer.
        string raceText = ""; int raceEmphasis = 0;
        RaceEndEstimate? est = null;
        if (isRace && !lapMode && t.SessionState < 5)
        {
            double pp = pace > 5 ? pace
                      : p < t.LastLap.Length && t.LastLap[p] > 5 ? t.LastLap[p] : 0;
            est = RaceEndEstimate.Estimate(t, pp);
            if (est is { } e) { raceText = RaceLine(e); raceEmphasis = e.Risk == ExtraLapRisk.Likely ? 2
                                                                     : e.Risk == ExtraLapRisk.Borderline ? 1 : 0; }
        }

        // Fuel-to-the-flag: the single most useful number, especially in a sprint where you never
        // stop. Needs only a per-lap figure + laps to go (lap count or the timed-race estimate),
        // NOT full strategy confidence — so it shows the moment we have one clean lap, and tells
        // you exactly how much to carry (and by how much you're over, which costs lap time).
        string finishText = ""; int finishEmphasis = 0;
        if (isRace && perLap > 0.01 && t.SessionState < 5)
        {
            double lapsToGo = lapMode ? t.SessionLapsRemain
                            : est is { } fe ? fe.PlayerLapsToGo
                            : pace > 5 && t.SessionTimeRemain > 0 && t.SessionTimeRemain < 200 * 3600
                                ? Math.Ceiling(t.SessionTimeRemain / pace) : -1;
            if (lapsToGo >= 1)
            {
                double margin = Math.Max(0, fc.MarginLaps);
                double need = (lapsToGo + margin) * perLap;
                double spare = fuelNow - need;
                double overBy = spare / perLap;   // laps of dead weight being carried
                finishText = spare < -0.05
                    ? $"finish needs {need:0.0}L — SHORT {-spare:0.0}L"
                    : overBy > 1.5
                        ? $"finish on {need:0.0}L · carrying {spare:0.0}L extra ({overBy:0.0} laps)"
                        : $"finish on {need:0.0}L · {spare:0.0}L in hand";
                finishEmphasis = spare < -0.05 ? 2 : overBy > 1.5 ? 1 : 0;
            }
        }

        FuelSnapshot Numbers() => new(true, fuelText, perLapText, lapsText, "", "",
                                      raceText, raceEmphasis, finishText, finishEmphasis, "", 0, []);

        // Bars need confidence + a race that is actually running toward an end.
        if (!isRace || t.SessionState >= 5 || fuel.GreenLaps < 3 || perLap <= 0.01 ||
            pace <= 5 || tank <= 1 || t.SessionTime < 0)
            return Numbers();

        double timeRemain = t.SessionTimeRemain;
        if (!lapMode && (timeRemain <= 0 || timeRemain > 200 * 3600)) return Numbers();

        // In a timed race the flag falls when the leader finishes, which is a touch past the raw
        // clock — budget fuel to the leader's checker, not to t=0 (the endurance-correct end).
        double timeBudget = !lapMode && est is { } re ? re.CheckerSec : timeRemain;

        int playerLap = p < t.Lap.Length ? t.Lap[p] : 0;
        bool onPit = p < t.OnPitRoad.Length && t.OnPitRoad[p];
        if (playerLap != _planLap || onPit != _planOnPit || fuel.GreenLaps != _planGreenLaps ||
            !ReferenceEquals(cfg, _planCfg))
        {
            _planLap = playerLap;
            _planOnPit = onPit;
            _planGreenLaps = fuel.GreenLaps;
            _planCfg = cfg;
            Replan(t, fuel, fc, fuelNow, perLap, tank, pace, lapMode, timeBudget, playerLap);
        }

        return new FuelSnapshot(true, fuelText, perLapText, lapsText,
                                _targetText, _planText, raceText, raceEmphasis,
                                finishText, finishEmphasis, _saveText, _nowFrac, _bars);
    }

    /// <summary>One glanceable line: projected race length, the player's laps to go, and whether
    /// the count is about to tip a lap (the fuel-relevant knife-edge).</summary>
    private static string RaceLine(RaceEndEstimate e) => e.Risk switch
    {
        ExtraLapRisk.None => $"final lap · {e.TotalLaps} laps · you {e.PlayerLapsToGo} to go",
        ExtraLapRisk.Likely =>
            $"≈{e.TotalLaps}(+1?) laps · you {e.PlayerLapsToGo} to go · extra lap likely — fuel for {e.PlayerLapsToGo + 1}",
        ExtraLapRisk.Borderline =>
            $"≈{e.TotalLaps} laps · you {e.PlayerLapsToGo} to go · borderline (±1)",
        _ => $"≈{e.TotalLaps} laps · you {e.PlayerLapsToGo} to go",
    };

    private void Replan(RawTick t, FuelModel fuel, FuelConfig fc, double fuelNow, double perLap,
                        double tank, double pace, bool lapMode, double timeRemain, int playerLap)
    {
        double laneLoss = fc.PitLaneLossSec > 0 ? fc.PitLaneLossSec
                        : fuel.MeasuredPitLoss > 0 ? fuel.MeasuredPitLoss : FallbackPitLoss;
        double fillRate = fc.FillRateLps > 0 ? fc.FillRateLps
                        : fuel.MeasuredFillRate > 0 ? fuel.MeasuredFillRate : FallbackFillRate;
        double maxSave = Math.Clamp(fc.MaxSaveLPerLap, 0.01, perLap * 0.5);
        // Rain makes pace and burn uncertain (safety-car interruptions, variable throttle, a
        // possible tyre stop) — carry an extra lap of fuel while the track is wet.
        bool wet = t.DeclaredWet || t.TrackWetness >= 5
                   || (!float.IsNaN(t.Precipitation) && t.Precipitation > 0.15f);
        double margin = Math.Max(0, fc.MarginLaps) + (wet ? 1.0 : 0);

        double Penalty(double s) => s / maxSave * Math.Max(0, fc.MaxSavePenaltySec);

        // Laps the player still runs. Time-limited races: saving slows the laps → fewer of
        // them → less fuel (the endurance freebie); pit time also eats race clock. Fixed point,
        // two rounds is plenty.
        double LapsRemainFor(double s, int k)
        {
            if (lapMode) return t.SessionLapsRemain;
            double lapSec = pace + Penalty(s);
            double laps = timeRemain / lapSec;
            for (int i = 0; i < 2; i++)
            {
                double need = Math.Max(0, laps * (perLap - s) - fuelNow);
                laps = Math.Max(1, (timeRemain - k * laneLoss - need / fillRate) / lapSec);
            }
            return Math.Ceiling(laps);
        }

        // Feasible = every stint fits its tank: first stint on current fuel, k more on fills,
        // with MarginLaps of cushion. Integer per-stint floors, no hand-waving.
        bool Feasible(int k, double s)
        {
            double laps = LapsRemainFor(s, k);
            double eff = perLap - s;
            if (eff <= 0.01) return false;
            int first = (int)Math.Floor(fuelNow / eff);
            int full = (int)Math.Floor(tank / eff);
            if (full < 1) return false;
            return first + (double)k * full >= laps + margin;
        }

        Plan Simulate(int k, double s)
        {
            double eff = perLap - s;
            double pen = Penalty(s);
            double laps = LapsRemainFor(s, k);
            int lapsLeft = (int)laps;

            var stints = new List<Stint>(k + 1);
            double fuelCur = fuelNow, total = 0, firstFill = -1;
            for (int stop = 0; stop <= k; stop++)
            {
                bool last = stop == k;
                int run = last ? lapsLeft
                    : Math.Clamp((int)Math.Floor(fuelCur / eff), 1, Math.Max(1, lapsLeft - (k - stop)));
                lapsLeft -= run;

                // After the *final* fill you push if the last tank can afford it — saving
                // past your last stop buys nothing.
                bool pushed = s <= 0.005 || (last && fuelCur >= run * perLap);
                stints.Add(new Stint(run, run * (pace + (pushed ? 0 : pen)), pushed));
                total += stints[^1].Seconds;

                if (!last)
                {
                    double leftover = Math.Max(0, fuelCur - run * eff);
                    bool moreStops = stop < k - 1;
                    // Intermediate stops brim the tank; the final stop takes only what the
                    // remaining laps need — a small final fill is *visibly* a splash-and-dash.
                    double needAfter = (lapsLeft + margin) * (moreStops ? eff : perLap);
                    double fill = moreStops ? tank - leftover
                        : Math.Min(tank - leftover, Math.Max(0, needAfter - leftover));
                    if (firstFill < 0) firstFill = fill;
                    total += laneLoss + fill / fillRate;
                    fuelCur = leftover + fill;
                }
            }
            return new Plan(k, s, (int)laps, total, firstFill, stints);
        }

        int kMin = -1, kPush = -1;
        for (int k = 0; k <= 300 && (kMin < 0 || kPush < 0); k++)
        {
            if (kMin < 0 && Feasible(k, maxSave)) kMin = k;
            if (kPush < 0 && Feasible(k, 0)) kPush = k;
        }
        if (kMin < 0) { _targetText = _planText = _saveText = ""; _bars = []; return; }
        if (kPush < 0) kPush = kMin;

        var plans = new List<Plan>();
        for (int k = kMin; k <= kPush; k++)
        {
            double sk = 0;
            if (k < kPush)
            {
                double step = Math.Max(0.005, maxSave / 40);
                for (double s = 0; s <= maxSave + 1e-9; s += step)
                    if (Feasible(k, s)) { sk = s; break; }
            }
            plans.Add(Simulate(k, sk));
        }

        // Fuel-save fork: the fewest-stops plan (kMin, highest save, built first) vs pushing
        // (kPush, s=0, built last). When saving skips a stop, surface the exact per-lap save so
        // "lift 0.09/lap and you make it" is an action, not a guess. Captured before truncation.
        _saveText = "";
        if (kMin < kPush && plans.Count >= 2 && plans[0].Save > 0.005)
        {
            var savePlan = plans[0];
            double dt = savePlan.TotalSec - plans[^1].TotalSec;
            string cost = dt <= -1 ? $"{-dt:0}s faster" : dt >= 1 ? $"+{dt:0}s slower" : "~even";
            _saveText = savePlan.Stops == 0
                ? $"save {savePlan.Save:0.00}/lap to skip the stop ({cost})"
                : $"save {savePlan.Save:0.00}/lap → {StopsText(savePlan.Stops)} ({cost})";
        }

        plans.Sort((a, b) => a.TotalSec.CompareTo(b.TotalSec));
        int keep = Math.Clamp(fc.Strategies, 1, 3);
        if (plans.Count > keep) plans.RemoveRange(keep, plans.Count - keep);

        var best = plans[0];
        string wetNote = wet ? " · wet +1 margin" : "";
        // Wall-clock stint end in GMT (endurance teams coordinate in GMT): the plan is anchored
        // at a lap crossing, so an absolute UTC timestamp taken here stays valid all lap.
        string boxAt = best.Stops == 0
            ? $" · flag {DateTime.UtcNow.AddSeconds(best.TotalSec):HH:mm:ss} GMT"
            : $" · box {DateTime.UtcNow.AddSeconds(best.Stints[0].Seconds):HH:mm:ss} GMT";
        _planText = (best.Stops == 0
            ? $"no more stops · {best.Laps} laps to go"
            : $"next stop ~L{playerLap + best.Stints[0].Laps} · add {best.FirstFill:0}L · {best.Laps} laps to go")
            + wetNote + boxAt;
        _targetText = best.Stints[0].Laps > 0
            ? $"tgt {fuelNow / (best.Stints[0].Laps + (best.Stops == 0 ? margin : 0)):0.00}"
            : "";

        // Bars: x-axis is the whole race — elapsed (dimmed, actual stint boundaries) + the
        // plan's projected remainder. Each bar normalizes by its own total, Pirelli style.
        double elapsed = t.SessionTime;
        var bars = new List<FuelStrategyBar>(plans.Count);
        for (int i = 0; i < plans.Count; i++)
        {
            var plan = plans[i];
            double total = elapsed + plan.TotalSec;
            var segs = new List<FuelSeg>(plan.Stints.Count + fuel.StintBounds.Count + 1);
            double prev = 0;
            foreach (var b in fuel.StintBounds)
            {
                if (b <= prev || b > elapsed) continue;
                segs.Add(new FuelSeg(Math.Round((b - prev) / total, 3), FuelSegKind.Past));
                prev = b;
            }
            segs.Add(new FuelSeg(Math.Round((elapsed - prev) / total, 3), FuelSegKind.Past));

            int fullStint = (int)Math.Floor(tank / (perLap - plan.Save));
            for (int j = 0; j < plan.Stints.Count; j++)
            {
                var st = plan.Stints[j];
                bool isFinal = j == plan.Stints.Count - 1;
                var kind = isFinal && plan.Stops > 0 && st.Laps <= Math.Max(2, fullStint / 4)
                    ? FuelSegKind.Splash
                    : st.Pushed ? FuelSegKind.Push : FuelSegKind.Save;
                segs.Add(new FuelSeg(Math.Round(st.Seconds / total, 3), kind));
            }

            string label = plan.Save > 0.005
                ? $"{(char)('A' + i)}  save {plan.Save:0.00}/lap · {StopsText(plan.Stops)}"
                : $"{(char)('A' + i)}  push · {StopsText(plan.Stops)}";
            string delta = i == 0 ? "fastest" : $"+{plan.TotalSec - best.TotalSec:0}s";
            bars.Add(new FuelStrategyBar(label, delta, segs));
        }

        _nowFrac = Math.Round(elapsed / (elapsed + best.TotalSec), 3);
        _bars = bars;
    }

    private static string StopsText(int k) => k == 1 ? "1 stop" : $"{k} stops";

    private readonly record struct Stint(int Laps, double Seconds, bool Pushed);

    private sealed record Plan(int Stops, double Save, int Laps, double TotalSec,
                               double FirstFill, IReadOnlyList<Stint> Stints);
}
