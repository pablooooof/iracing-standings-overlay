using StandingsOverlay.Config;

namespace StandingsOverlay.Data;

public enum TrafficPhase { Watch, Imminent }

public enum AlongsideDir { None, Left, Right, Both, TwoLeft, TwoRight }

[Flags]
public enum TrafficCues { None = 0, Watch = 1, Imminent = 2, Blue = 4 }

/// <summary>One display-ready alert row. All formatting happens in the detector, not in XAML.</summary>
public sealed record TrafficRow(
    int CarIdx,
    TrafficPhase Phase,
    bool IsBlue,             // being lapped (blue-flag) vs faster-class traffic
    bool IsLapping,          // slower/lapped traffic AHEAD that you're about to lap
    string ClassColor,
    string CarNumber,
    string Name,
    string IRatingText,      // "4.2k" or ""
    string SubText,          // "GTP · P2 in class" / "GT3 · leader · +1 lap"
    string TtaText,          // "6.8"
    string RateText,         // "+7.2s/lap"
    string Chevrons,         // "▾" | "▾▾" | "▾▾▾"
    int TrainCount,          // >1 on the lead car of a same-class train
    double BarPct);          // 0..1 proximity bar fill

public sealed record TrafficSnapshot(
    IReadOnlyList<TrafficRow> Rows,   // sorted by time-to-arrival, capped at MaxRows
    int Overflow,                     // alerting cars beyond MaxRows
    AlongsideDir Alongside,           // active overlap banner (alerted traffic only)
    bool ClearFlash,                  // brief "CLEAR" confirmation after traffic passes
    TrafficCues Cues)                 // audio requests for this tick (already arbitrated)
{
    public static readonly TrafficSnapshot Empty = new([], 0, AlongsideDir.None, false, TrafficCues.None);

    public bool IsEmpty => Rows.Count == 0 && Alongside == AlongsideDir.None && !ClearFlash;

    /// <summary>Value comparison (the record alone compares Rows by reference). Cues excluded —
    /// they are consumed by audio, not rendering.</summary>
    public bool VisuallyEquals(TrafficSnapshot? o)
    {
        if (o is null) return false;
        if (Overflow != o.Overflow || Alongside != o.Alongside || ClearFlash != o.ClearFlash ||
            Rows.Count != o.Rows.Count) return false;
        for (int i = 0; i < Rows.Count; i++)
            if (Rows[i] != o.Rows[i]) return false;
        return true;
    }
}

/// <summary>
/// Detects traffic approaching the player and turns it into display-ready alerts:
/// faster-class cars closing in, and same-class cars about to put the player a lap down.
/// Gap = wrapped track-position delta × the chaser's class lap time; a short ring buffer of
/// gap samples gives the closing rate, and gap/rate is the time-to-arrival countdown that
/// drives the WATCH → IMMINENT → (ALONGSIDE) → CLEAR state machine. Sorted purely by TTA;
/// same-class cars within a couple of seconds of each other merge into a "train" (×N badge,
/// one audio cue). Pure function of the tick otherwise — works identically in demo mode.
/// Full spec: docs/TRAFFIC-ALERTER.md.
/// </summary>
public sealed class TrafficDetector
{
    private const double WindowSec = 30;        // furthest gap we track at all
    private const double RateWindowSec = 3.2;   // ring buffer span for the closing rate
    private const double MinRateSpanSec = 1.5;  // measured rate needs at least this much history
    private const float MaxRateSps = 0.6f;      // faster "closing" than this is an artifact, not a car
    private const float BlueGapSec = 2.5f;      // a lapping leader inside this is blue regardless of rate
    private const double TrainGapSec = 2.5;     // same-class cars closer than this merge
    private const double MinDisplaySec = 2;     // a row lives at least this long
    private const double DismissAfterSec = 3;   // out of range this long before removal
    private const double ReAlertCooldownSec = 15;  // same car can't re-WATCH right after dismissal
    private const double ClearFlashSec = 0.8;
    private const double WatchCueCooldownSec = 5;
    private const double ImminentCueCooldownSec = 3;

    private sealed class CarState
    {
        public readonly List<(double T, float Gap)> Samples = new(16);
        public TrafficPhase Phase;
        public double PhaseSince;
        public double AlertSince;
        public double OutOfRangeSince = -1;
        public double PrevDeltaBehind = -1;
        public double DismissedAt = double.MinValue;
        public float LastTta = float.NaN;   // last finite countdown shown, held when the rate collapses
        public bool Alerting;
    }

    // "You're lapping them" alerts (slower traffic ahead) keep their own light state so the tuned
    // behind-approaching machine stays untouched — a car is only ever in one of the two regimes.
    private sealed class LapState
    {
        public readonly List<(double T, float Gap)> Samples = new(8);
        public bool Alerting;
        public double AlertSince;
        public TrafficPhase Phase;
    }

    private readonly Dictionary<int, CarState> _cars = new();
    private readonly Dictionary<int, LapState> _lapCars = new();
    private readonly HashSet<int> _bluePlayed = new();   // blue cue fires once per lapping car
    private double _lastWatchCue = double.MinValue;
    private double _lastImminentCue = double.MinValue;
    private double _clearFlashUntil = -1;

    public void Reset()
    {
        _cars.Clear();
        _lapCars.Clear();
        _bluePlayed.Clear();
        _lastWatchCue = _lastImminentCue = double.MinValue;
        _clearFlashUntil = -1;
    }

    public TrafficSnapshot Update(RawTick t, Roster roster, GapHistory history, OverlayConfig cfg)
    {
        var tc = cfg.Traffic;
        bool race = StandingsSnapshot.KindOf(t.SessionType) == SessionKind.Race;
        if (!tc.Enabled || (tc.RacesOnly && !race) ||
            !t.Has(t.PlayerCarIdx) || t.SessionTime < 0 ||
            // Lone qualifying: other cars are ghosts from separate runs, never really near you.
            t.SessionType.Contains("Lone", StringComparison.OrdinalIgnoreCase) ||
            // No traffic warnings while the player is anywhere in the pit area (lane, stall,
            // or the entry/exit roads before the cones) — everyone "closes" at full speed on
            // a slow car and none of it is actionable.
            t.OnPitRoad[t.PlayerCarIdx] ||
            (t.PlayerCarIdx < t.TrackSurface.Length && t.TrackSurface[t.PlayerCarIdx] is 1 or 2))
        {
            if (_cars.Count > 0) Reset();
            return TrafficSnapshot.Empty;
        }

        double now = t.SessionTime;
        float playerLapTime = RelativeGap.PlayerRefLap(t, roster);
        double playerTotal = t.Lap[t.PlayerCarIdx] + t.LapDistPct[t.PlayerCarIdx];
        bool lappingOn = !tc.Mode.Equals("FasterClassOnly", StringComparison.OrdinalIgnoreCase);
        bool allClosing = tc.Mode.Equals("AllClosing", StringComparison.OrdinalIgnoreCase);
        int playerClassId = roster.Drivers.TryGetValue(t.PlayerCarIdx, out var me) ? me.CarClassId : -1;

        var cues = TrafficCues.None;
        var active = new List<(TrafficRow Row, float Tta, float Gap, int ClassId)>();

        foreach (var d in roster.Drivers.Values)
        {
            if (d.CarIdx == t.PlayerCarIdx || d.IsPaceCar || d.IsSpectator || !t.Has(d.CarIdx)) continue;

            bool gone = t.OnPitRoad[d.CarIdx] ||
                        (d.CarIdx < t.TrackSurface.Length && t.TrackSurface[d.CarIdx] == -1);
            if (gone) { _cars.Remove(d.CarIdx); continue; }

            double deltaBehind = t.LapDistPct[t.PlayerCarIdx] - t.LapDistPct[d.CarIdx];
            if (deltaBehind < 0) deltaBehind += 1;

            // "You're lapping them": slower/lapped traffic AHEAD on track that you're at least ~half
            // a lap up on overall (a lap down). A separate light state handles it; a car is only
            // ever ahead-being-lapped OR behind-approaching, never both — so skip the rest here.
            double carTot = t.Lap[d.CarIdx] + t.LapDistPct[d.CarIdx];
            if (race && lappingOn && tc.WarnLapping && deltaBehind >= 0.5 && playerTotal - carTot >= 0.5)
            {
                _cars.Remove(d.CarIdx);
                HandleLapTarget(d, t, roster, history, now, playerLapTime, tc, active);
                continue;
            }
            _lapCars.Remove(d.CarIdx);

            // Lap counts only mean "lapping you" in a race; practice/qual has no blue flags.
            bool isBlue = race && lappingOn && d.CarClassId == playerClassId &&
                          (t.Lap[d.CarIdx] + t.LapDistPct[d.CarIdx]) - playerTotal >= 0.9;
            bool isFaster = d.ClassEstLap > 10 && playerLapTime - d.ClassEstLap > 1.0;

            var state = _cars.TryGetValue(d.CarIdx, out var s0) ? s0 : null;

            // A car passing us wraps deltaBehind from ~0 to ~1. If it was an imminent alert,
            // confirm with a brief CLEAR flash; either way its alert is finished.
            if (state is not null && state.PrevDeltaBehind >= 0 &&
                state.PrevDeltaBehind < 0.1 && deltaBehind > 0.9)
            {
                if (state.Alerting)
                {
                    Log.Write($"traffic: PASSED #{d.CarNumber} {d.ClassName}");
                    if (state.Phase == TrafficPhase.Imminent) _clearFlashUntil = now + ClearFlashSec;
                }
                _cars.Remove(d.CarIdx);
                state = null;
            }

            float chaserLap = d.ClassEstLap > 10 ? d.ClassEstLap : playerLapTime;
            // Shared phase gap (docs/RELATIVE.md): negative = the car is behind us, so flip
            // the sign for a chaser gap. Floor at zero — the phase occasionally puts a car with
            // deltaBehind ≈ 0 marginally "ahead", and for TTA purposes alongside is zero gap.
            float signed = RelativeGap.SignedSeconds(t, roster, d.CarIdx, chaserLap);
            float gap = Math.Max(0f, -signed);
            // The zero-floor is only legitimate alongside (deltaBehind ≈ 0). Near track-opposite
            // the time-phase and distance-pct wrap their ±half-lap seam on different ticks, so a
            // +1-lap car sitting ~half a lap away read phase-"ahead" while pct said 0.4x behind →
            // gap=0 → phantom BLUE at "0.1 s" re-firing every cooldown cycle (live 24h,
            // 2026-07-11). Phase-ahead with the car clearly behind in pct is that seam, not a car.
            bool wrapSeam = signed > 0f && deltaBehind > 0.25;
            bool inWindow = !wrapSeam && deltaBehind < 0.5 && gap < WindowSec;

            if (!inWindow)
            {
                if (state is not null)
                {
                    state.PrevDeltaBehind = deltaBehind;
                    state.Samples.Clear();
                    if (state.Alerting) DismissOrKeep(state, now);
                    if (!state.Alerting && deltaBehind >= 0.5) _cars.Remove(d.CarIdx);
                }
                continue;
            }

            state ??= _cars[d.CarIdx] = new CarState();
            state.PrevDeltaBehind = deltaBehind;

            // Ring buffer of (time, gap). A jump of 3+ seconds between ticks is a tow or
            // reset, not driving — flush so the rate doesn't go wild.
            if (state.Samples.Count > 0 && Math.Abs(gap - state.Samples[^1].Gap) > 3)
                state.Samples.Clear();
            state.Samples.Add((now, gap));
            while (state.Samples.Count > 1 && now - state.Samples[0].T > RateWindowSec)
                state.Samples.RemoveAt(0);

            // Closing rate (s/s): least-squares slope over the buffer (noise-tolerant where an
            // endpoint difference spikes on a single bad sample). The steady-state truth is the
            // standings delta history — avg of the last 5 clean per-lap deltas (pit/tow/driver-
            // swap laps excluded) — which beats the class-pace *guess* wherever it exists and
            // gives same-class chasers a real "+N.Ns/lap" too. Capped — anything "closing" above
            // MaxRateSps is an artifact (tow, reset), not driving.
            float classPace = isFaster ? (playerLapTime - d.ClassEstLap) / playerLapTime : 0f;
            float? lapCatch = history.CatchRatePerLap(d.CarIdx);   // s/lap, >0 = closing on us
            float rate = float.MinValue;
            double span = state.Samples.Count > 1 ? now - state.Samples[0].T : 0;
            if (span >= MinRateSpanSec)
            {
                rate = -SlopeOf(state.Samples);
                // A player accelerating out of a corner briefly "holds off" a faster car in
                // the numbers, which used to blow the countdown up to 99 s. They are still
                // coming — never let the rate fall below a third of the lap-measured catch
                // (or, for a faster class with no history yet, a third of class pace; race
                // only — in practice/qual cars run their own programs).
                if (lapCatch is float lm && lm > 0f)
                    rate = Math.Max(rate, lm / playerLapTime * 0.3f);
                else if (isFaster && race)
                    rate = Math.Max(rate, classPace * 0.3f);
            }
            else if (lapCatch is float lc && lc > 0f)
            {
                rate = lc / playerLapTime;
            }
            else if (isFaster && race)
            {
                rate = classPace;
            }
            rate = Math.Min(rate, MaxRateSps);

            float tta = rate > 0.05f ? gap / rate : float.MaxValue;
            double lead = isBlue && !isFaster ? tc.BlueLeadTimeSec : tc.AlertLeadTimeSec;

            // Blue also fires on raw gap: a leader grinding up at 1-3 s/lap has a rate too small
            // for a meaningful countdown (TTA math would only alert with them on the bumper) —
            // if they're within BlueGapSec they're a blue-flag situation, closing fast or not.
            bool blueNear = isBlue && !isFaster && gap <= BlueGapSec;
            bool qualifies = isFaster || isBlue || (allClosing && rate > 0.15f);
            bool inRange = qualifies && (tta <= lead || blueNear);

            if (!state.Alerting)
            {
                // The re-alert cooldown stops boundary flapping, but must never silence a car
                // that is actually arriving — a dismissal at ~3 s followed by a fast re-approach
                // used to fire WATCH only at contact (gap=0.0 in the logs). Urgent = it's here.
                bool urgent = tta <= tc.ImminentSec || gap <= BlueGapSec;
                if (!inRange || (!urgent && now - state.DismissedAt < ReAlertCooldownSec)) continue;
                state.Alerting = true;
                state.Phase = TrafficPhase.Watch;
                state.AlertSince = state.PhaseSince = now;
                state.OutOfRangeSince = -1;
                Log.Write($"traffic: WATCH #{d.CarNumber} {d.ClassName}{(isBlue && !isFaster ? " BLUE" : "")} " +
                          $"gap={gap:0.0}s tta={Math.Min(tta, 999.9f):0.0}s rate={Math.Max(rate, -9.999f):0.000}s/s " +
                          $"catch={(lapCatch is float lg ? lg.ToString("0.0") : "n/a")}s/lap");

                if (isBlue && !isFaster)
                {
                    if (_bluePlayed.Add(d.CarIdx)) cues |= TrafficCues.Blue;
                }
                else if (now - _lastWatchCue > WatchCueCooldownSec)
                {
                    cues |= TrafficCues.Watch;
                    _lastWatchCue = now;
                }
            }
            else if (!inRange && !(qualifies && (tta <= lead * 1.3 ||
                     (isBlue && !isFaster && gap <= BlueGapSec * 1.3))))
            {
                // Hysteresis: only dismiss after being out of range for a while.
                if (!DismissOrKeep(state, now)) continue;
            }
            else
            {
                state.OutOfRangeSince = -1;
            }

            // Blue escalates on TTA only: a leader closing at 2 s/lap lives under 1.5 s of
            // gap for most of a lap, and a calm alert is the whole point of the blue design.
            bool imminentNow = tta <= tc.ImminentSec || (!(isBlue && !isFaster) && gap <= 1.5f);
            if (state.Phase == TrafficPhase.Watch && imminentNow)
            {
                state.Phase = TrafficPhase.Imminent;
                state.PhaseSince = now;
                // Blue alerts never escalate the audio; the visual pulse is enough.
                if (!(isBlue && !isFaster) && now - _lastImminentCue > ImminentCueCooldownSec)
                {
                    cues |= TrafficCues.Imminent;
                    _lastImminentCue = now;
                }
            }

            // Display countdown: hold the last finite value when the rate collapses — a player
            // accelerating out of a corner used to flash "99.9" for the dismissal grace period.
            // Capped at the dismissal band: a countdown above lead×1.3 is already a dying alert.
            float shownTta = float.IsFinite(tta) && tta < 90 ? tta
                : float.IsNaN(state.LastTta) ? (float)lead : state.LastTta;
            shownTta = Math.Min(shownTta, (float)(lead * 1.3));
            state.LastTta = shownTta;

            // Shown catch rate: the lap-measured number when it exists (what the standings delta
            // column would say), else the short-window slope extrapolated to a lap.
            float ratePerLap = lapCatch ?? Math.Max(0, rate) * playerLapTime;
            active.Add((BuildRow(d, t, state.Phase, isBlue && !isFaster, shownTta, gap, ratePerLap,
                                 playerTotal, lead, tc), tta, gap, d.CarClassId));
        }

        // Sweep alerting cars that vanished from the roster loop (e.g. pitted mid-alert).
        // Rows for them simply stop being produced; their state ages out via the pit branch.

        active.Sort((a, b) => a.Tta.CompareTo(b.Tta));

        // Same-class train merging: consecutive cars of one class within TrainGapSec.
        var rows = new List<TrafficRow>(active.Count);
        for (int i = 0; i < active.Count; i++)
        {
            int train = 1;
            while (i + train < active.Count &&
                   active[i + train].ClassId == active[i].ClassId &&
                   active[i + train].Gap - active[i + train - 1].Gap <= TrainGapSec)
                train++;
            rows.Add(active[i].Row with { TrainCount = train });
            for (int k = 1; k < train; k++) rows.Add(active[i + k].Row);
            i += train - 1;
        }

        int overflow = Math.Max(0, rows.Count - Math.Max(1, cfg.Traffic.MaxRows));
        if (overflow > 0) rows.RemoveRange(rows.Count - overflow, overflow);

        // Alongside banner: the sim spotter says a car overlaps us AND we have alerted
        // traffic close enough for it to plausibly be that car (traffic only, per spec).
        var alongside = AlongsideDir.None;
        if (tc.AlongsideBanner && t.CarLeftRight >= 2 &&
            active.Any(a => a.Gap <= 2.0f))
        {
            alongside = t.CarLeftRight switch
            {
                2 => AlongsideDir.Left,
                3 => AlongsideDir.Right,
                4 => AlongsideDir.Both,
                5 => AlongsideDir.TwoLeft,
                6 => AlongsideDir.TwoRight,
                _ => AlongsideDir.None,
            };
        }

        return new TrafficSnapshot(rows, overflow, alongside, now < _clearFlashUntil, cues);
    }

    /// <summary>Returns true if the car should still be shown this tick (grace period).</summary>
    private static bool DismissOrKeep(CarState s, double now)
    {
        if (s.OutOfRangeSince < 0) s.OutOfRangeSince = now;
        if (now - s.OutOfRangeSince > DismissAfterSec && now - s.AlertSince > MinDisplaySec)
        {
            s.Alerting = false;
            s.OutOfRangeSince = -1;
            s.DismissedAt = now;
            return false;
        }
        return true;
    }

    /// <summary>Least-squares slope of gap over time across the ring buffer (s/s). An endpoint
    /// difference lets one noisy sample invent a closing rate; the fit uses every sample.</summary>
    private static float SlopeOf(List<(double T, float Gap)> samples)
    {
        int n = samples.Count;
        double t0 = samples[0].T, st = 0, sg = 0, stt = 0, stg = 0;
        for (int i = 0; i < n; i++)
        {
            double x = samples[i].T - t0, y = samples[i].Gap;
            st += x; sg += y; stt += x * x; stg += x * y;
        }
        double den = n * stt - st * st;
        return den < 1e-6 ? 0f : (float)((n * stg - st * sg) / den);
    }

    /// <summary>Slower/lapped traffic ahead you're catching. Simpler than the behind machine:
    /// fires on raw gap (default 5s) while you're actually closing, with a short dismissal grace.</summary>
    private void HandleLapTarget(DriverEntry d, RawTick t, Roster roster, GapHistory history,
        double now, float playerLapTime,
        TrafficConfig tc, List<(TrafficRow Row, float Tta, float Gap, int ClassId)> active)
    {
        // They are ahead, so the shared phase gap is positive = time for us to reach them.
        float gap = Math.Max(0f, RelativeGap.SignedSeconds(t, roster, d.CarIdx, playerLapTime));
        if (gap <= 0.01f || gap > WindowSec) { _lapCars.Remove(d.CarIdx); return; }

        var s = _lapCars.TryGetValue(d.CarIdx, out var s0) ? s0 : (_lapCars[d.CarIdx] = new LapState());
        if (s.Samples.Count > 0 && Math.Abs(gap - s.Samples[^1].Gap) > 3) s.Samples.Clear();
        s.Samples.Add((now, gap));
        while (s.Samples.Count > 1 && now - s.Samples[0].T > RateWindowSec) s.Samples.RemoveAt(0);

        double span = s.Samples.Count > 1 ? now - s.Samples[0].T : 0;
        float rate = span >= MinRateSpanSec ? (float)((s.Samples[0].Gap - gap) / span) : 0f;
        bool closing = span < MinRateSpanSec || rate > 0.02f;   // benefit of the doubt until known
        bool inRange = closing && gap <= tc.LapTrafficGapSec;

        if (!s.Alerting)
        {
            if (!inRange) { if (gap > tc.LapTrafficGapSec * 1.5) _lapCars.Remove(d.CarIdx); return; }
            s.Alerting = true;
            s.Phase = TrafficPhase.Watch;
            s.AlertSince = now;
            Log.Write($"traffic: LAPPING #{d.CarNumber} {d.ClassName} gap={gap:0.0}s rate={rate:0.000}s/s");
        }
        else if ((!closing || gap > tc.LapTrafficGapSec * 1.4) && now - s.AlertSince > MinDisplaySec)
        {
            _lapCars.Remove(d.CarIdx);
            return;
        }

        if (gap <= 2.0f) s.Phase = TrafficPhase.Imminent;
        // For a car ahead the lap-measured delta is negative when the player gains — flip it.
        float ratePerLap = history.CatchRatePerLap(d.CarIdx) is float lc
            ? -lc : Math.Max(0, rate) * playerLapTime;
        active.Add((BuildLapRow(d, t, s.Phase, gap, ratePerLap, tc), gap, gap, d.CarClassId));
    }

    private static TrafficRow BuildLapRow(DriverEntry d, RawTick t, TrafficPhase phase, float gap,
        float ratePerLap, TrafficConfig tc)
    {
        string chevrons = ratePerLap > 6 ? "▾▾▾" : ratePerLap > 2.5 ? "▾▾" : "▾";
        int cp = d.CarIdx < t.ClassPosition.Length ? t.ClassPosition[d.CarIdx] : 0;
        string sub = cp > 0 ? $"{d.ClassName} · P{cp} · lapping" : $"{d.ClassName} · lapping";
        return new TrafficRow(
            CarIdx: d.CarIdx, Phase: phase, IsBlue: false, IsLapping: true,
            ClassColor: string.IsNullOrEmpty(d.ClassColor) ? "#9DA0AA" : d.ClassColor,
            CarNumber: "#" + d.CarNumber, Name: d.Name,
            IRatingText: tc.ShowIRating && d.IRating > 0 ? $"{d.IRating / 1000.0:0.0}k" : "",
            SubText: sub,
            TtaText: Math.Clamp(gap, 0.1, 99.9).ToString("0.0"),
            RateText: ratePerLap > 0.05 ? $"+{ratePerLap:0.0}s/lap" : "",
            Chevrons: chevrons, TrainCount: 1,
            BarPct: Math.Round(Math.Clamp(1 - gap / tc.LapTrafficGapSec, 0.03, 1), 2));
    }

    private static TrafficRow BuildRow(DriverEntry d, RawTick t, TrafficPhase phase, bool isBlue,
                                       float tta, float gap, float ratePerLap,
                                       double playerTotal, double lead, TrafficConfig tc)
    {
        string chevrons = ratePerLap > 6 ? "▾▾▾" : ratePerLap > 2.5 ? "▾▾" : "▾";

        string sub;
        if (isBlue)
        {
            int lapsUp = (int)Math.Floor((t.Lap[d.CarIdx] + t.LapDistPct[d.CarIdx]) - playerTotal);
            string who = t.ClassPosition[d.CarIdx] == 1 ? "leader" : $"P{t.ClassPosition[d.CarIdx]}";
            sub = $"{d.ClassName} · {who} · +{Math.Max(1, lapsUp)} lap{(lapsUp > 1 ? "s" : "")}";
        }
        else
        {
            int cp = t.ClassPosition[d.CarIdx];
            sub = cp > 0 ? $"{d.ClassName} · P{cp} in class" : d.ClassName;
        }

        // Blue rows show the raw gap, not a countdown — "the leader is N seconds behind you" is
        // how a spotter calls it, and it stays meaningful at grind-it-out closing rates where
        // a TTA would read as noise. Traffic rows keep the countdown.
        double shown = Math.Clamp(isBlue ? gap : tta, 0.1, 99.9);
        double bar = isBlue ? 1 - gap / 10 : 1 - tta / lead;
        return new TrafficRow(
            CarIdx: d.CarIdx,
            Phase: phase,
            IsBlue: isBlue,
            IsLapping: false,
            ClassColor: string.IsNullOrEmpty(d.ClassColor) ? "#9DA0AA" : d.ClassColor,
            // "#72", not "72" — a bare number in an alert reads as a position.
            CarNumber: "#" + d.CarNumber,
            Name: d.Name,
            IRatingText: tc.ShowIRating && d.IRating > 0 ? $"{d.IRating / 1000.0:0.0}k" : "",
            SubText: sub,
            TtaText: shown.ToString("0.0"),
            RateText: ratePerLap > 0.05 ? $"+{ratePerLap:0.0}s/lap" : "",
            Chevrons: chevrons,
            TrainCount: 1,
            BarPct: Math.Round(Math.Clamp(bar, 0.03, 1), 2));
    }
}
