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
    string RaceHead,         // timed races: "84 laps" / "84 laps · final lap" ("" = no race row)
    double RaceProgress,     // 0..1 bar: leader's progress through their lap at 0:00 (1 = extra lap); -1 = no bar
    string RaceNext,         // "85" — the lap count the bar is filling toward
    string RaceChance,       // "8%" — chance of that extra lap
    string RaceText,         // "+1 lap if leader 15.8s quicker (0.57s/lap) · you 26 to go"
    int RaceEmphasis,        // 0 normal · 1 extra lap possible (≥10%) · 2 likely (≥50%)
    string FinishText,       // "finish on 41.8L · 2.4L in hand" — the exact fuel-to-the-flag number
    int FinishEmphasis,      // 0 ok (green) · 1 over-fuelled (amber) · 2 short (red)
    string SaveText,         // "save 0.09/lap to skip the stop (12s faster)" — the fuel-save fork
    string BoxText,          // "black box 35L — set 38L" — pit-service fuel vs the planned fill
    int BoxEmphasis,         // 0 matches (green) · 1 off (amber)
    double NowFrac,          // 0..1 position of the now marker on the bars
    IReadOnlyList<FuelStrategyBar> Bars)
{
    public static readonly FuelSnapshot Empty =
        new(false, "", "", "", "", "", "", -1, "", "", "", 0, "", 0, "", "", 0, 0, []);

    public bool VisuallyEquals(FuelSnapshot? o)
    {
        if (o is null) return false;
        if (Show != o.Show || FuelText != o.FuelText || PerLapText != o.PerLapText ||
            LapsText != o.LapsText || TargetText != o.TargetText || PlanText != o.PlanText ||
            RaceHead != o.RaceHead || Math.Abs(RaceProgress - o.RaceProgress) > 0.004 ||
            RaceNext != o.RaceNext || RaceChance != o.RaceChance ||
            RaceText != o.RaceText || RaceEmphasis != o.RaceEmphasis ||
            FinishText != o.FinishText || FinishEmphasis != o.FinishEmphasis || SaveText != o.SaveText ||
            BoxText != o.BoxText || BoxEmphasis != o.BoxEmphasis ||
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
    private double _nextFill = -1;      // planned fill at the next stop (L), -1 = no stop planned
    private bool _nextIsLast;

    private readonly RaceEndModel _raceEnd = new();

    public void Reset()
    {
        _planLap = int.MinValue;
        _planOnPit = false;
        _planGreenLaps = -1;
        _planCfg = null;
        _targetText = _planText = _saveText = "";
        _nowFrac = 0;
        _bars = [];
        _nextFill = -1;
        _nextIsLast = false;
        _raceEnd.Reset();
    }

    public FuelSnapshot Build(RawTick t, FuelModel fuel, StintTracker stints, Roster roster, OverlayConfig cfg)
    {
        var fc = cfg.Fuel;
        int p = t.PlayerCarIdx;
        if (!fc.Enabled.In(StandingsSnapshot.KindOf(t.SessionType)) || !t.Has(p) || float.IsNaN(t.PlayerFuelLevel)) return FuelSnapshot.Empty;

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
        RaceEndEstimate? est = null;
        if (isRace && !lapMode && t.SessionState < 5)
            est = _raceEnd.Update(t, roster, stints, fuel, fc);
        var race = RaceRow(est);

        // Fuel-to-the-flag: the single most useful number, especially in a sprint where you never
        // stop. Needs only a per-lap figure + laps to go (lap count or the timed-race estimate),
        // NOT full strategy confidence — so it shows the moment we have one clean lap, and tells
        // you exactly how much to carry (and by how much you're over, which costs lap time).
        // Laps are fractional (minus the part of this lap already driven) so the number falls
        // with the fuel through the lap instead of sawtoothing at the line.
        string finishText = ""; int finishEmphasis = 0;
        if (isRace && perLap > 0.01 && t.SessionState < 5)
        {
            double done = p < t.LapDistPct.Length ? Math.Clamp(t.LapDistPct[p], 0, 1) : 0;
            double lapsToGo = lapMode ? t.SessionLapsRemain
                            : est is { } fe ? fe.PlayerLapsToGo - done
                            : pace > 5 && t.SessionTimeRemain > 0 && t.SessionTimeRemain < 200 * 3600
                                ? Math.Ceiling(t.SessionTimeRemain / pace) : -1;
            if (lapsToGo > 0)
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

                // The extra-lap scenario, priced: what the tank needs if the race runs one more lap.
                if (est is { } xe && xe.PlayerPExtra >= 0.02 && xe.PlayerLapsIfExtra > xe.PlayerLapsToGo)
                {
                    double needX = need + (xe.PlayerLapsIfExtra - xe.PlayerLapsToGo) * perLap;
                    finishText += fuelNow >= needX ? " · +1 lap ✓" : $" · +1 lap: {needX:0.0}L";
                }
            }
        }

        var (boxText, boxEmphasis) = BoxCheck(t, perLap);

        FuelSnapshot Numbers() => new(true, fuelText, perLapText, lapsText, "", "",
                                      race.Head, race.Progress, race.Next, race.Chance, race.Text, race.Emphasis,
                                      finishText, finishEmphasis, "", "", 0, 0, []);

        // Bars need confidence + a race that is actually running toward an end.
        if (!isRace || t.SessionState >= 5 || fuel.GreenLaps < 3 || perLap <= 0.01 ||
            pace <= 5 || tank <= 1 || t.SessionTime < 0)
            return Numbers();

        double timeRemain = t.SessionTimeRemain;
        if (!lapMode && est is null && (timeRemain <= 0 || timeRemain > 200 * 3600)) return Numbers();

        int playerLap = p < t.Lap.Length ? t.Lap[p] : 0;
        bool onPit = p < t.OnPitRoad.Length && t.OnPitRoad[p];
        if (playerLap != _planLap || onPit != _planOnPit || fuel.GreenLaps != _planGreenLaps ||
            !ReferenceEquals(cfg, _planCfg))
        {
            _planLap = playerLap;
            _planOnPit = onPit;
            _planGreenLaps = fuel.GreenLaps;
            _planCfg = cfg;
            Replan(t, fuel, fc, fuelNow, perLap, tank, pace, lapMode, timeRemain, est, playerLap);
        }

        return new FuelSnapshot(true, fuelText, perLapText, lapsText,
                                _targetText, _planText,
                                race.Head, race.Progress, race.Next, race.Chance, race.Text, race.Emphasis,
                                finishText, finishEmphasis, _saveText, boxText, boxEmphasis, _nowFrac, _bars);
    }

    private readonly record struct RaceRowText(string Head, double Progress, string Next, string Chance,
                                               string Text, int Emphasis);

    /// <summary>The timed-race row: "84 laps ▕████▌░░▏ 85  8%" plus one detail line. The bar is
    /// the leader's projected progress through their lap when the clock hits 0:00 — it creeps,
    /// it doesn't jump, and when it reaches the end the race has gained a lap. The detail says
    /// what it would take in seconds (the way race engineers call it) and the player's count.</summary>
    private static RaceRowText RaceRow(RaceEndEstimate? est)
    {
        if (est is not { } e) return new("", -1, "", "", "", 0);
        string you = $"you {e.PlayerLapsToGo} to go";
        if (e.Phase == RaceEndPhase.FinalLap)
            return new($"{e.TotalLaps} laps · final lap", -1, "", "", you, 0);
        if (e.Phase == RaceEndPhase.WhiteNext)
            return new($"{e.TotalLaps} laps · white flag next time the leader crosses", -1, "", "", you, 0);

        // Seconds either way, the way an engineer calls it. A held count (the projection already
        // sits past a boundary but the shown count is still live) reads as "unless".
        string detail = e.GainSec < 0
            ? $"+1 lap unless leader {-e.GainSec:0.0}s slower"
            : e.DropSec < 0
                ? $"−1 lap unless leader {-e.DropSec:0.0}s quicker"
                : e.ExtraLapProgress >= 0.2
                    ? $"+1 lap if leader {e.GainSec:0.0}s quicker" +
                      (e.LeaderLapsToZero >= 2 ? $" ({e.GainSec / e.LeaderLapsToZero:0.00}s/lap)" : "")
                    : $"−1 lap if leader {e.DropSec:0.0}s slower";
        double stops = e.LeaderStopsLeft;
        double frac = stops - Math.Floor(stops);
        if (stops >= 0.2)
            detail += frac is >= 0.2 and <= 0.8
                ? $" · leader {Math.Floor(stops):0}–{Math.Floor(stops) + 1:0} stops left"
                : Math.Round(stops) == 1 ? " · leader 1 stop left" : $" · leader {Math.Round(stops):0} stops left";
        detail += " · " + you;
        // In multiclass / laps down, the player's own line-crossing can tip a lap independently.
        if (Math.Abs(e.PlayerPExtra - e.PExtra) >= 0.03)
            detail += $" (+1 for you: {Pct(e.PlayerPExtra)})";

        double risk = Math.Max(e.PExtra, e.PlayerPExtra);
        int emphasis = risk >= 0.5 ? 2 : risk >= 0.1 ? 1 : 0;
        return new($"{e.TotalLaps} laps", Math.Round(e.ExtraLapProgress, 2), $"{e.TotalLaps + 1}",
                   Pct(e.PExtra), detail, emphasis);
    }

    private static string Pct(double p) => p < 0.005 ? "<1%" : p > 0.995 ? ">99%" : $"{p * 100:0}%";

    /// <summary>The pit black box vs the plan: iRacing adds exactly PitSvFuel at the next stop,
    /// so the number the driver sets is THE last-stop decision. Shown only with a stop planned
    /// and while not already in the stall (the fill is underway there).</summary>
    private (string Text, int Emphasis) BoxCheck(RawTick t, double perLap)
    {
        if (_nextFill < 0 || t.PlayerInPitStall || float.IsNaN(t.PitSvFuel) || t.PitSvFuel < 0) return ("", 0);
        if ((t.PitSvFlags & PitSvFuelFill) == 0)
            return ($"black box: fuel OFF — set {_nextFill:0}L", 1);
        double box = t.PitSvFuel, slack = Math.Max(1.0, perLap * 0.5);
        if (box < _nextFill - 0.5)
            return ($"black box {box:0}L — {_nextFill - box:0.0}L short, set {_nextFill:0}L", 1);
        // Intermediate stops brim the tank: anything above the need just fills to the top.
        if (_nextIsLast && box > _nextFill + slack + Math.Max(0, perLap))
            return ($"black box {box:0}L — {box - _nextFill:0}L more than the finish needs", 1);
        return ($"black box {box:0}L ✓", 0);
    }

    private const int PitSvFuelFill = 0x10;   // irsdk_PitSvFlags.FuelFill

    private void Replan(RawTick t, FuelModel fuel, FuelConfig fc, double fuelNow, double perLap,
                        double tank, double pace, bool lapMode, double timeRemain, RaceEndEstimate? est,
                        int playerLap)
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
        // two rounds is plenty. With a race-end estimate the count is the real flag rule (the
        // player's line crossings until the leader's checkered, capped at the leader's laps).
        double LapsRemainFor(double s, int k)
        {
            if (lapMode) return t.SessionLapsRemain;
            double lapSec = pace + Penalty(s);
            if (est is { } e)
            {
                int n = e.PlayerLapsFor(lapSec, k * laneLoss);
                for (int i = 0; i < 2; i++)
                {
                    double need = Math.Max(0, (n + margin) * (perLap - s) - fuelNow);
                    n = e.PlayerLapsFor(lapSec, k * laneLoss + need / fillRate);
                }
                return n;
            }
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
        if (kMin < 0) { _targetText = _planText = _saveText = ""; _bars = []; _nextFill = -1; return; }
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
        // The last stop is the one that decides the finish: price the extra-lap scenario too, so
        // the black-box number for "and if the race runs one more lap" is on screen, not mental math.
        string fillText = $"add {best.FirstFill:0}L";
        if (best.Stops == 1 && est is not null && !lapMode && best.FirstFill + perLap <= tank + 0.05)
            fillText += $" ({best.FirstFill + perLap:0}L if +1 lap)";   // you push after the last stop
        _nextFill = best.Stops == 0 ? -1 : best.FirstFill;
        _nextIsLast = best.Stops == 1;
        _planText = (best.Stops == 0
            ? $"no more stops · {LapsWord(best.Laps)} to go"
            : $"{(best.Stops == 1 ? "last" : "next")} stop ~L{playerLap + best.Stints[0].Laps} · {fillText} · {LapsWord(best.Laps)} to go")
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
    private static string LapsWord(int n) => n == 1 ? "1 lap" : $"{n} laps";

    private readonly record struct Stint(int Laps, double Seconds, bool Pushed);

    private sealed record Plan(int Stops, double Save, int Laps, double TotalSec,
                               double FirstFill, IReadOnlyList<Stint> Stints);
}
