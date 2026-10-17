using StandingsOverlay.Config;

namespace StandingsOverlay.Data;

public enum RaceEndPhase
{
    Projecting,   // clock still running: lap count is a projection
    WhiteNext,    // clock hit 0:00 — the leader's next crossing is the white flag
    FinalLap,     // the leader is on the white-flag lap
}

/// <summary>
/// Timed-race distance projection. See <see cref="RaceEndModel"/> for the rule and the model.
/// </summary>
public readonly record struct RaceEndEstimate(
    RaceEndPhase Phase,
    int TotalLaps,            // N: the leader's final (checkered) lap number
    double ExtraLapProgress,  // f ∈ [0,1]: how far round the leader is when the clock hits 0:00 — 1.0 = extra lap
    double PExtra,            // chance the race gets one MORE lap than TotalLaps
    double PFewer,            // chance it gets one fewer
    double GainSec,           // leader time gain that adds a lap (negative = already projected past it: count held)
    double DropSec,           // leader time loss that drops one (negative = already projected under it)
    double LeaderLapsToZero,  // leader laps until 0:00 — divides GainSec into a per-lap figure
    double LeaderStopsLeft,   // expected remaining pit stops of the projected leader (soft, may be fractional)
    int LeaderIdx,
    double LeaderLapSec,
    double CheckerSec,        // seconds from now until the leader takes the checkered flag
    int PlayerLapsToGo,       // laps the player still runs, including the current one
    int PlayerLapsIfExtra,    // one more than that — the "fuel for an extra lap" figure
    double PlayerPExtra,      // chance the PLAYER runs at least one lap more than PlayerLapsToGo
    double PlayerCompleted,   // player's laps completed right now, continuous (time phase)
    int PlayerLap,            // player's CarIdxLap (laps started) at the time of the estimate
    int PlayerBias)           // shown final lap − raw projection (hysteresis hold), applied to plan counts
{
    /// <summary>Laps the player still runs (including the current one) if they lap in
    /// <paramref name="lapSec"/> and spend <paramref name="stopSec"/> in remaining stops —
    /// the strategy planner prices its candidate plans with this so every plan counts the
    /// flag the same way the race line does (including its hysteresis hold, so the plan's
    /// "N laps to go" and the race line's "you N to go" never disagree by the knife-edge lap).
    /// No car completes more laps than the leader.</summary>
    public int PlayerLapsFor(double lapSec, double stopSec)
    {
        if (lapSec <= 5) return Math.Max(1, PlayerLapsToGo);
        double y = PlayerCompleted + Math.Max(0, CheckerSec - stopSec) / lapSec;
        int final = Math.Min((int)Math.Floor(y) + 1 + PlayerBias, TotalLaps);
        return Math.Max(1, final - PlayerLap + 1);
    }
}

/// <summary>
/// How many laps a timed race will run, and how close it is to running one more.
///
/// <b>The rule</b> (iRacing, observed — the sporting code doesn't spell it out): the clock
/// counting down to 0:00 does not end the race. The <b>overall leader's</b> first crossing of
/// the line after 0:00 is the <b>white flag</b>; the checkered falls one lap later. So the race
/// length is set by where the leader is when the clock expires: N = (leader laps completed at
/// 0:00, floored) + 2. If the leader crosses the line just BEFORE 0:00, the white waits for
/// the next crossing — a whole extra lap. That knife-edge is <see cref="RaceEndEstimate.ExtraLapProgress"/>:
/// the leader's projected progress through their lap at 0:00. Near 1.0 an extra lap is close.
///
/// <b>The projection</b> (why it doesn't jump from 4% to 80% in a lap): a naive
/// "time left ÷ last lap" estimate moves with every lap-time wobble, with the leader's in-lap,
/// and by a whole pit stop's worth when the leader pits. Instead, for each of the top few cars
/// (whoever is P1 at 0:00 decides, and leaders swap during pit cycles):
///   X = laps completed now (time-domain phase, no track-section breathing)
///     + (time left − expected remaining pit-stop time) ÷ median clean-lap pace.
/// The remaining stops are soft: P(needs another stop) from its typical stint length vs the
/// laps it has left in the tank, priced at the stop loss we measured for that car (its
/// projection's drop across each pit visit). While a car is on pit road its projection is held
/// (the stop was already priced in) instead of chasing a stationary car.
/// The leader is the car with the largest X. Uncertainty σ grows with √laps × the leader's lap
/// scatter, plus pace drift over the time left, plus the stop-count ambiguity — so the chance
/// of an extra lap is a smooth function of a slowly moving number, not of the last lap time.
///
/// The player's laps to go follow from the leader's checkered time and the player's own pace
/// and stops (including the cap: nobody does more laps than the leader). Stateful only for the
/// per-car stop-loss measurements, the pit holds and the white-flag lap latched at 0:00.
/// </summary>
public sealed class RaceEndModel
{
    private const int Candidates = 3;              // top overall positions that could take the flag
    private const double DriftFrac = 0.0025;       // pace drift over the time left (deg, traffic, driver swaps)
    private const double DefaultLapSigma = 0.5;    // s, lap-to-lap scatter before we have data
    private const double PitSigma = 6;             // s, stop-to-stop variation (splash vs full fill, tires)
    private const double StopDecisionSigma = 0.6;  // laps — how fuzzy "do they need another stop?" is
    private const double FallbackLoss = 60;        // s per stop when nothing was measured
    private const double KeepShown = 0.35;         // a shown lap count stays while it's still this likely
    private const int YellowBits = 0x8 | 0x100 | 0x4000 | 0x8000;
    private const int WhiteFlag = 0x2, CheckeredFlag = 0x1;

    private sealed class Car
    {
        public bool WasOnPit;
        public double EntryRaw = double.NaN;   // completed − now/pace at pit entry
        public double EntryPace;
        public int ExitLap = -1;               // waiting for the first crossing after pit exit
        public readonly List<double> Losses = new(4);
        public double HeldX = double.NaN;      // stop-modeled projection, held through a pit visit
        public double HeldSigma;
        public double HeldStops;
        public double LastX = double.NaN;
        public double LastSigma, LastStops, LastXAt = -1;
    }

    private readonly Dictionary<int, Car> _cars = new();
    private int _finalLap = -1;                // the checkered lap, latched when the clock expires
    private int _prevFlags;
    private int _loggedLap = int.MinValue;
    private RaceEndEstimate? _lastProjection;
    // Hysteresis memory: the race count and the player's final lap currently on screen.
    private int _shownN = -1;
    private int _shownFinal = -1;
    private double _shownPlayerPExtra;
    private int _playerBias;

    public void Reset()
    {
        _cars.Clear();
        _finalLap = -1;
        _prevFlags = 0;
        _loggedLap = int.MinValue;
        _lastProjection = null;
        _shownN = _shownFinal = -1;
        _shownPlayerPExtra = 0;
        _playerBias = 0;
    }

    /// <summary>Feed every tick of a timed race (it measures pit losses as they happen);
    /// returns null when there is no usable clock or leader.</summary>
    public RaceEndEstimate? Update(RawTick t, Roster roster, StintTracker stints, FuelModel fuel, FuelConfig fc)
    {
        double now = t.SessionTime;
        if (now >= 0) MeasureStops(t, roster, stints, now);

        // A missing/unlimited clock must never read as "expired" — that latches the final lap.
        double tau = t.SessionTimeRemain;
        if (tau > 200 * 3600 || tau < -3600 || t.SessionTimeTotal <= 0) return null;

        // ---- leader side --------------------------------------------------------------------
        int leader = -1;
        double x = 0, sigmaX = 0, lpL = 0, stopsL = 0, stopSecL = 0, completedL = 0;
        RaceEndPhase phase;
        int n;

        if (tau > 0)
        {
            double bestX = double.NegativeInfinity;
            for (int i = 0; i < t.Position.Length; i++)
            {
                if (t.Position[i] < 1 || t.Position[i] > Candidates || !InWorld(t, i)) continue;
                if (Project(t, roster, stints, fuel, i, tau) is not { } pr) continue;
                if (pr.X > bestX)
                {
                    bestX = pr.X; leader = i;
                    (x, sigmaX, lpL, stopsL, stopSecL, completedL) = (pr.X, pr.SigmaX, pr.Lap, pr.Stops, pr.StopSec, pr.Completed);
                }
            }
            if (leader < 0) return null;
            // Hysteresis: a projection sitting right on a lap boundary would flip the count (and
            // empty/fill the bar) every few seconds. Keep the count on screen while it is still a
            // live outcome; the bar pins full/empty and the chance says the rest.
            n = (int)Math.Floor(x) + 2;
            double sx = sigmaX, xx = x;
            double PRace(int k) => Phi((k - 1 - xx) / sx) - Phi((k - 2 - xx) / sx);
            if (_shownN > 0 && Math.Abs(n - _shownN) == 1 && PRace(_shownN) >= KeepShown) n = _shownN;
            _shownN = n;
            _finalLap = -1;
            phase = RaceEndPhase.Projecting;
        }
        else
        {
            // Clock expired: the white flag lap is now a fact, not a projection. Latch it once.
            for (int i = 0; i < t.Position.Length; i++)
                if (t.Position[i] == 1 && InWorld(t, i)) { leader = i; break; }
            if (leader < 0) return null;
            if (_finalLap < 0)
            {
                // A late join (app started after 0:00) can't know whether the leader already took
                // the white; the sim's own white flag being out says they did.
                _finalLap = t.Lap[leader] + ((t.GlobalFlags & WhiteFlag) != 0 ? 0 : 1);
                LogExpiry(t, leader);
            }
            n = _finalLap;
            lpL = PaceOf(t, roster, stints, leader, out _);
            completedL = Completed(t, roster, leader);
            phase = t.Lap[leader] >= n ? RaceEndPhase.FinalLap : RaceEndPhase.WhiteNext;
        }
        LogFlags(t, leader, n);

        bool projecting = phase == RaceEndPhase.Projecting;
        // Relative to the count shown (n may be held one off the raw floor): progress toward the
        // extra lap, its chance, and the seconds either way (signed — negative = already past).
        double f = projecting ? x - (n - 2) : 0;
        double pExtra = projecting ? 1 - Phi((n - 1 - x) / sigmaX) : 0;
        double pFewer = projecting ? Phi((n - 2 - x) / sigmaX) : 0;
        double checker = Math.Max(0, (n - completedL) * lpL + stopSecL);
        double toZero = projecting ? Math.Max(0, x - completedL) : 0;
        // Leader time σ to the flag, for the player's own knife-edge below.
        double sigmaTL = projecting ? sigmaX * lpL : DefaultLapSigma * Math.Sqrt(Math.Max(0, n - completedL));

        // ---- player side --------------------------------------------------------------------
        int p = t.PlayerCarIdx;
        int playerLap = t.Has(p) && p < t.Lap.Length ? t.Lap[p] : 0;
        double completedP = t.Has(p) ? Completed(t, roster, p) : 0;
        double pp = fuel.PaceSec > 5 ? fuel.PaceSec : t.Has(p) ? PaceOf(t, roster, stints, p, out _) : lpL;
        pp = Math.Max(5, pp);
        double sigmaLapP = t.Has(p) && stints.PaceStats(p) is { } ps ? Math.Clamp(ps.Sigma, 0.15, 2.0) : DefaultLapSigma;

        var est = new RaceEndEstimate(phase, n, Math.Round(Math.Clamp(f, 0, 1), 4), pExtra, pFewer,
            projecting ? (n - 1 - x) * lpL : 0, projecting ? (x - (n - 2)) * lpL : 0, toZero, stopsL, leader, lpL, checker,
            0, 0, 0, completedP, playerLap, 0);

        bool playerOnPit = t.Has(p) && p < t.OnPitRoad.Length && t.OnPitRoad[p];
        int final;
        double playerPExtra;
        if (playerOnPit && _shownFinal > 0)
        {
            // In the pit lane the player's position stalls and the fill changes by the second;
            // the count was priced (with this stop) on the way in — hold it until the exit.
            final = _shownFinal;
            playerPExtra = _shownPlayerPExtra;
        }
        else
        {
            double stopSecP = PlayerStopSec(t, fuel, fc, est, pp, out int stopsP);
            int rawFinal = playerLap + est.PlayerLapsFor(pp, stopSecP) - 1;

            // P(player's final lap ≥ F): over the leader's −1/0/+1 lap outcomes, the chance the
            // player's own crossing lands past lap F−1 before the flag, under the leader cap.
            double lapsP = checker / pp;
            double sigmaTP = Math.Sqrt(lapsP * sigmaLapP * sigmaLapP + Sq(DriftFrac * checker)) + stopsP * PitSigma * 0.5;
            double sigmaY = Math.Max(0.02, Math.Sqrt(Sq(sigmaTL) + Sq(sigmaTP)) / pp);
            double AtLeast(int lapF)
            {
                double s = 0;
                for (int j = -1; j <= 1; j++)
                {
                    double pj = j == 1 ? pExtra : j == -1 ? pFewer : 1 - pExtra - pFewer;
                    if (pj <= 0 || n + j < lapF) continue;
                    double y = completedP + Math.Max(0, checker + j * lpL - stopSecP) / pp;
                    s += pj * (1 - Phi((lapF - 1 - y) / sigmaY));
                }
                return Math.Clamp(s, 0, 1);
            }
            double PFinal(int lapF) => AtLeast(lapF) - AtLeast(lapF + 1);

            final = rawFinal;
            for (int c = rawFinal - 1; c <= rawFinal + 1; c++)
                if (c >= playerLap && PFinal(c) > PFinal(final) + 1e-9) final = c;
            // Same hysteresis as the race count: on a knife-edge the player's crossing lands a
            // second either side of the leader's flag for laps on end — hold what's shown.
            if (_shownFinal >= playerLap && Math.Abs(_shownFinal - final) == 1 && PFinal(_shownFinal) >= KeepShown)
                final = _shownFinal;
            final = Math.Max(final, playerLap);
            playerPExtra = AtLeast(final + 1);
            _shownFinal = final;
            _shownPlayerPExtra = playerPExtra;
            _playerBias = final - rawFinal;
        }

        int lapsToGo = Math.Max(1, final - playerLap + 1);
        est = est with { PlayerLapsToGo = lapsToGo, PlayerLapsIfExtra = lapsToGo + 1, PlayerPExtra = playerPExtra,
                         PlayerBias = _playerBias };
        if (phase == RaceEndPhase.Projecting)
        {
            _lastProjection = est;
            LogProjection(t, est, sigmaX);
        }
        return est;
    }

    private readonly record struct Proj(double X, double SigmaX, double Lap, double Stops, double StopSec, double Completed);

    /// <summary>One candidate's projected laps completed at 0:00, with its σ (laps).</summary>
    private Proj? Project(RawTick t, Roster roster, StintTracker stints, FuelModel fuel, int i, double tau)
    {
        if (i >= t.Lap.Length || t.Lap[i] < 0) return null;
        double lp = PaceOf(t, roster, stints, i, out double sigLap);
        if (lp <= 5) return null;
        double completed = Completed(t, roster, i);
        var car = CarFor(i);

        bool onPit = i < t.OnPitRoad.Length && t.OnPitRoad[i];
        if (onPit && !double.IsNaN(car.HeldX))
        {
            // The held X implies how much stop time is still to come (it shrinks while the car
            // sits there) — the checkered time needs it.
            double implied = Math.Max(0, tau - (car.HeldX - completed) * lp);
            return new Proj(car.HeldX, car.HeldSigma, lp, car.HeldStops, implied, completed);
        }

        double loss = StopLoss(t, roster, stints, fuel, i);
        double stint = StintPrior(t, roster, stints, fuel, i, lp, out int stintSource);
        double? left = stints.StintLaps(i, t.Lap[i]) is int into && stint > 0 ? stint - into : null;

        // Fixed point: stops eat clock → fewer laps → maybe fewer stops. Two rounds is plenty.
        double stops = 0, var = 0;
        double laps = tau / lp;
        for (int k = 0; k < 2; k++)
        {
            (stops, var) = SoftStops(laps + 1.5, left, stint);
            laps = Math.Max(0, tau - stops * loss) / lp;
        }
        // A stint length borrowed from class-mates or guessed from the player's tank is itself
        // uncertain: widen the stop count with it, so the early-race projection doesn't sound sure.
        var += stintSource switch { 2 => Sq(0.5 * stops), 1 => Sq(0.2 * stops), _ => 0 };
        // A car already in the pit lane with no held projection (we started mid-stop): assume
        // half its stop is still to come.
        double stopSec = stops * loss + (onPit ? 0.5 * loss : 0);
        double x = completed + Math.Max(0, tau - stopSec) / lp;

        // Stops can only cost the clock that's left: with 10 s to go, a "maybe one more stop"
        // can't move the count by a 40 s stop's worth (that's what kept σ wide at the very end).
        double effLoss = Math.Min(loss, tau), effPit = Math.Min(PitSigma, tau);
        double sigT = Math.Sqrt(laps * sigLap * sigLap + Sq(DriftFrac * tau) + var * effLoss * effLoss + stops * effPit * effPit);
        double sigmaX = Math.Max(0.004, sigT / lp);

        if (!onPit) { car.LastX = x; car.LastSigma = sigmaX; car.LastStops = stops; car.LastXAt = t.SessionTime; }
        // The checkered TIME takes the most likely stop count, not the mean: a 5% "maybe one more
        // splash" is 95% no stop, and adding 5% of a stop to the flag time moved the player's
        // knife-edge crossing for an outcome that almost never happens. (The lap count X keeps
        // the mean + its σ — that's where the ambiguity is priced.)
        double checkerStopSec = Math.Round(stops) * loss + (onPit ? 0.5 * loss : 0);
        return new Proj(x, sigmaX, lp, stops, checkerStopSec, completed);
    }

    /// <summary>Expected remaining stops (and their variance) for a car needing
    /// <paramref name="need"/> more laps, with <paramref name="left"/> laps left in its tank
    /// (null = unknown phase: average over the stint) on a <paramref name="stint"/>-lap tank.
    /// Soft at each threshold so a car right on the edge of "one more splash?" moves the
    /// projection by half a stop, not a whole one.</summary>
    internal static (double E, double Var) SoftStops(double need, double? left, double stint)
    {
        if (stint < 2) return (0, 0);
        if (left is not double l) return (Math.Max(0, need / stint), 0.1);
        double e = 0, v = 0;
        for (int m = 1; m <= 8; m++)
        {
            // +0.5: teams stop with a little fuel margin rather than coast across the line on fumes.
            double pr = Phi((need - l - (m - 1) * stint + 0.5) / StopDecisionSigma);
            if (pr < 1e-4) break;
            e += pr;
            v += pr * (1 - pr);
        }
        return (e, v);
    }

    /// <summary>Player's remaining stop time from their own fuel: stops = what the tank can't
    /// cover, priced at the lane loss + the fill at the measured rate. Lane loss: configured,
    /// else learned from the in/out lap times; before that, the player's own measured stop cost
    /// (whole cycle, fill included) beats a blind fallback.</summary>
    private double PlayerStopSec(RawTick t, FuelModel fuel, FuelConfig fc, RaceEndEstimate est, double pp, out int stops)
    {
        stops = 0;
        double perLap = fuel.GreenPerLap, tank = t.TankCapacity, fuelNow = t.PlayerFuelLevel;
        if (perLap <= 0.01 || tank <= 1 || float.IsNaN(t.PlayerFuelLevel)) return 0;
        double fillRate = fc.FillRateLps > 0 ? fc.FillRateLps
                        : fuel.MeasuredFillRate > 0 ? fuel.MeasuredFillRate : 2.6;
        double laneLoss = fc.PitLaneLossSec > 0 ? fc.PitLaneLossSec
                        : fuel.MeasuredPitLoss > 0 ? fuel.MeasuredPitLoss : -1;
        double cycle = laneLoss < 0 && _cars.TryGetValue(t.PlayerCarIdx, out var me) && me.Losses.Count > 0
            ? Median(me.Losses) : -1;
        if (laneLoss < 0 && cycle < 0) laneLoss = 45;
        double margin = Math.Max(0, fc.MarginLaps);
        int p = t.PlayerCarIdx;
        double distPhase = p < t.LapDistPct.Length ? Math.Clamp(t.LapDistPct[p], 0, 1) : 0;

        double stopSec = 0;
        for (int k = 0; k < 2; k++)
        {
            int laps = est.PlayerLapsFor(pp, stopSec);
            double needFuel = (laps - distPhase + margin) * perLap - fuelNow;
            if (needFuel <= 0) { stops = 0; stopSec = 0; break; }
            stops = (int)Math.Ceiling(needFuel / tank);
            stopSec = cycle > 0 ? stops * cycle : stops * laneLoss + needFuel / fillRate;
        }
        return stopSec;
    }

    // ---- per-car stop loss measurement --------------------------------------------------------

    /// <summary>The time a pit visit really cost a car = how far its "laps completed − elapsed ÷
    /// pace" invariant dropped between pit entry and its first line crossing after the exit
    /// (in-lap slowdown, lane, stop and the cold out-lap, all in one number). Constant at
    /// constant pace, so it needs no track map and works the same in demo and live.</summary>
    private void MeasureStops(RawTick t, Roster roster, StintTracker stints, double now)
    {
        for (int i = 0; i < t.Position.Length && i < t.OnPitRoad.Length && i < t.Lap.Length; i++)
        {
            if (!InWorld(t, i) || t.Lap[i] < 1) continue;
            var car = CarFor(i);
            bool onPit = t.OnPitRoad[i];
            if (onPit && !car.WasOnPit)
            {
                double lp = PaceOf(t, roster, stints, i, out _);
                car.EntryPace = lp;
                car.EntryRaw = lp > 5 ? Completed(t, roster, i) - now / lp : double.NaN;
                car.ExitLap = -1;
                // Hold the projection made on the way in — it already priced this stop. Only a
                // fresh one: a car that wasn't a candidate lately has a stale LastX.
                car.HeldX = now - car.LastXAt < 2 ? car.LastX : double.NaN;
                car.HeldSigma = car.LastSigma;
                car.HeldStops = Math.Max(0, car.LastStops - 1);
            }
            else if (!onPit && car.WasOnPit)
            {
                car.ExitLap = t.Lap[i];
                car.HeldX = double.NaN;
            }
            else if (!onPit && car.ExitLap >= 0 && t.Lap[i] > car.ExitLap)
            {
                if (!double.IsNaN(car.EntryRaw))
                {
                    double loss = (car.EntryRaw - (Completed(t, roster, i) - now / car.EntryPace)) * car.EntryPace;
                    if (loss is > 5 and < 400)
                    {
                        car.Losses.Add(loss);
                        if (car.Losses.Count > 3) car.Losses.RemoveAt(0);
                    }
                }
                car.ExitLap = -1;
                car.EntryRaw = double.NaN;
            }
            car.WasOnPit = onPit;
        }
    }

    /// <summary>Stop loss for a car: its own measured median, else its class's, else the
    /// player's measured lane loss + a typical fill, else a fallback.</summary>
    private double StopLoss(RawTick t, Roster roster, StintTracker stints, FuelModel fuel, int i)
    {
        if (_cars.TryGetValue(i, out var c) && c.Losses.Count > 0) return Median(c.Losses);
        int cls = roster.Drivers.TryGetValue(i, out var d) ? d.CarClassId : -1;
        var classLosses = new List<double>();
        foreach (var (idx, car) in _cars)
            if (car.Losses.Count > 0 && roster.Drivers.TryGetValue(idx, out var od) && od.CarClassId == cls)
                classLosses.Add(Median(car.Losses));
        if (classLosses.Count > 0) return Median(classLosses);
        if (fuel.MeasuredPitLoss > 0 && fuel.GreenPerLap > 0 && t.TankCapacity > 1)
            return fuel.MeasuredPitLoss + t.TankCapacity * 0.8 / Math.Max(0.5, fuel.MeasuredFillRate > 0 ? fuel.MeasuredFillRate : 2.6);
        return FallbackLoss;
    }

    /// <summary>A car's stint length in laps: its own median, else its class's median, else
    /// the player's tank converted to the car's lap time (series BoP stints to similar
    /// durations), else 0 = unknown (no stop modelling).</summary>
    private static double StintPrior(RawTick t, Roster roster, StintTracker stints, FuelModel fuel, int i, double lp,
                                     out int source)
    {
        source = 0;
        if (stints.TypicalStintLaps(i) is int own) return own;
        source = 1;
        int cls = roster.Drivers.TryGetValue(i, out var d) ? d.CarClassId : -1;
        var mates = new List<double>();
        foreach (var (idx, od) in roster.Drivers)
            if (od.CarClassId == cls && stints.TypicalStintLaps(idx) is int s) mates.Add(s);
        if (mates.Count > 0) return Median(mates);
        source = 2;
        if (fuel.GreenPerLap > 0.01 && t.TankCapacity > 1 && fuel.PaceSec > 5)
            return t.TankCapacity / fuel.GreenPerLap * fuel.PaceSec / lp;
        return 0;
    }

    /// <summary>Test hook: the stop losses measured for a car (seconds, newest last).</summary>
    internal IReadOnlyList<double> LossesOf(int idx) => _cars.TryGetValue(idx, out var c) ? c.Losses : [];

    // ---- helpers ------------------------------------------------------------------------------

    /// <summary>Median clean-lap pace (σ out), else its last lap, else its class estimate.</summary>
    private static double PaceOf(RawTick t, Roster roster, StintTracker stints, int i, out double sigma)
    {
        if (stints.PaceStats(i) is { } ps && ps.Pace > 5)
        {
            sigma = Math.Clamp(ps.Sigma, 0.15, 2.0);
            return ps.Pace;
        }
        sigma = DefaultLapSigma * 2;
        if (i < t.LastLap.Length && t.LastLap[i] > 5) return t.LastLap[i];
        return roster.Drivers.TryGetValue(i, out var d) && d.ClassEstLap > 5 ? d.ClassEstLap : 0;
    }

    /// <summary>Laps completed right now, continuous: CarIdxLap carries the integer (consistent
    /// with LapDistPct at the line), the time-domain phase the fraction.</summary>
    private static double Completed(RawTick t, Roster roster, int i)
    {
        double pct = i < t.LapDistPct.Length ? Math.Clamp(t.LapDistPct[i], 0, 0.9999) : 0;
        double ph = RelativeGap.LapPhase(t, roster, i);
        double skew = ph - pct;
        if (skew > 0.5) skew -= 1; else if (skew <= -0.5) skew += 1;
        return t.Lap[i] - 1 + pct + skew;
    }

    private static bool InWorld(RawTick t, int i) => i >= t.TrackSurface.Length || t.TrackSurface[i] != -1;

    private Car CarFor(int i)
    {
        if (!_cars.TryGetValue(i, out var c)) _cars[i] = c = new Car();
        return c;
    }

    private static double Median(List<double> xs)
    {
        var s = xs.OrderBy(v => v).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : 0.5 * (s[s.Count / 2 - 1] + s[s.Count / 2]);
    }

    private static double Sq(double v) => v * v;

    /// <summary>Standard normal CDF (Abramowitz–Stegun 7.1.26, |error| &lt; 1.5e-7).</summary>
    internal static double Phi(double z)
    {
        if (double.IsNaN(z)) return 0.5;
        double x = Math.Abs(z) / Math.Sqrt(2);
        double k = 1 / (1 + 0.3275911 * x);
        double erf = 1 - ((((1.061405429 * k - 1.453152027) * k + 1.421413741) * k - 0.284496736) * k + 0.254829592) * k * Math.Exp(-x * x);
        return z >= 0 ? 0.5 * (1 + erf) : 0.5 * (1 - erf);
    }

    // ---- overlay.log (rare events only) -------------------------------------------------------

    /// <summary>One line per projected-leader lap: enough to audit the projection against the
    /// real finish after a race without screenshots.</summary>
    private void LogProjection(RawTick t, RaceEndEstimate e, double sigmaX)
    {
        int lap = t.Lap[e.LeaderIdx];
        if (lap == _loggedLap) return;
        _loggedLap = lap;
        bool yellow = (t.GlobalFlags & YellowBits) != 0;
        Log.Write($"race est: clock {t.SessionTimeRemain:0}s · leader idx {e.LeaderIdx} lap {lap} pace {e.LeaderLapSec:0.00} " +
                  $"stops {e.LeaderStopsLeft:0.00} → {e.TotalLaps} laps, f={e.ExtraLapProgress:0.000} σ={sigmaX:0.000} " +
                  $"P+1 {e.PExtra:P0} P-1 {e.PFewer:P0} · you {e.PlayerLapsToGo} to go (P+1 {e.PlayerPExtra:P0})" +
                  (yellow ? " · YELLOW" : ""));
    }

    private void LogExpiry(RawTick t, int leader)
    {
        string pred = _lastProjection is { } lp
            ? $"projected {lp.TotalLaps} (f={lp.ExtraLapProgress:0.000}, P+1 {lp.PExtra:P0})" : "no projection";
        Log.Write($"race end: clock 0:00 · leader idx {leader} on lap {t.Lap[leader]} at {t.LapDistPct[leader]:0.000} " +
                  $"→ white at next crossing, checkered after lap {_finalLap} · {pred}");
    }

    /// <summary>Log the sim's own white/checkered flags as they appear — the ground truth that
    /// confirms (or corrects) the white-flag rule above on the next real race.</summary>
    private void LogFlags(RawTick t, int leader, int n)
    {
        int rising = t.GlobalFlags & ~_prevFlags;
        _prevFlags = t.GlobalFlags;
        if ((rising & (WhiteFlag | CheckeredFlag)) == 0 || leader < 0) return;
        string which = (rising & CheckeredFlag) != 0 ? "checkered" : "white";
        Log.Write($"race end: sim shows {which} flag · clock {t.SessionTimeRemain:0.0}s · leader idx {leader} " +
                  $"lap {t.Lap[leader]} · model final lap {n}");
    }
}
