namespace StandingsOverlay.Data;

/// <summary>A completed pit visit's timing (seconds), plus the lap it happened on. Stationary =
/// time sat still (≈ in the box); DriveThrough = the rest (pit-lane transit).</summary>
public readonly record struct PitInfo(int Lap, double Total, double Stationary, double DriveThrough);

/// <summary>
/// Per-car race-craft state built from cheap observations: lap times (sampled at each car's own
/// lap crossing), pit road transitions, and the first position seen (the grid). Everything the
/// strategy columns show — expected pit lap, stops to end, splash-and-dash, fuel-save detection,
/// pace vs class — is derived from this.
/// </summary>
public sealed class StintTracker
{
    private sealed class CarState
    {
        public int LastLapSeen = -1;
        public readonly List<float> LapTimes = new(32);   // this car's recent laps, capped; -1 = no time (tow/reset)
        public readonly List<bool> PitLaps = new(32);     // parallel: lap touched the pit lane (in/out lap)
        public bool PitThisLap;
        public bool PendingCrossing;                      // lap completed, time not yet published
        public bool PendingPit;
        public float LastLapValue = -999f;                // last CarIdxLastLapTime we recorded
        public readonly List<int> StintLengths = new(8);  // completed green-lap stints
        public int CurrentStintStartLap;
        public bool WasOnPit;
        public int PitCount;
        public int FirstSeenLap = int.MinValue;           // car's lap when we first saw it in the world
        public int GridPos;                               // first valid race position seen
        public double LastTotalDist = -1;                 // stopped-car detection
        public double LastMoveTime = -1;
        public double RejoinTime = -1;                    // when it started moving again after a stop
        public double RateSampleTime = -1, RateSampleDist;
        public double LapsPerSec = -1;                    // smoothed track progress (laps/sec)
        public double LastPitExitTime = -1;               // when the car last rejoined from pit road
        public int LastCompound = -1;                     // CarIdxTireCompound (0 dry, >=1 wet)
        public double CompoundSwitchTime = -1;            // when it last crossed dry<->wet
        public bool SwitchedToWet;                        // direction of that last switch
        // Track-surface transition latch: the last surface value BEFORE the current one.
        // A towed car materializes InPitStall(1) without ever reading AproachingPits(2).
        public int Surface = int.MinValue;
        public int PrevSurface = int.MinValue;
        // What the car was on when it left the world: a driver change blinks the car out of
        // its stall (1/2) for a second or two; a real tow vanishes from the track (0/3).
        public int SurfaceBeforeVanish = int.MinValue;
        // Pit-visit timing.
        public double PitEntryTime = -1;
        public int PitEntryLap;
        public double PitPrevNow = -1, PitPrevDist;
        public double PitStationaryAccum;
        public PitInfo? LastPit;
        // Tire-change inference (no SDK channel exists for opponents' tire sets): with
        // fuel-and-tires-separate rules a tire stop sits still ~10s+ longer than the same
        // fuel fill alone, so the cheapest observed fill rate (sec per stint lap) becomes
        // the fuel-only baseline and longer stops are classified as "took tires".
        public double MinFillSecPerLap = double.MaxValue;
        public int PendingStintLen = -1;                  // stint length latched at pit entry
        public bool SwapThisVisit;                        // driver changed → stop time is meaningless
        public bool DmgThisVisit;                         // meatball → repairs pad the stop, not tires
        public int TireStartLap;                          // lap the current tires were (likely) fitted
        public int TireStints = 1;                        // stints on this rubber (2 = double, 3 = triple)
    }

    private const int MaxLapTimes = 30;
    private readonly Dictionary<int, CarState> _cars = new();
    private bool _isRace;
    private double _now = -1;                             // last SessionTime seen

    public void Update(RawTick t)
    {
        _isRace = t.SessionType.Contains("Race", StringComparison.OrdinalIgnoreCase);
        _now = t.SessionTime;

        for (int idx = 0; idx < t.Position.Length; idx++)
        {
            if (idx >= t.Lap.Length || idx >= t.OnPitRoad.Length) break;

            if (!_cars.TryGetValue(idx, out var s))
                _cars[idx] = s = new CarState
                {
                    // Don't treat a LastLapTime that predates us as a fresh lap.
                    LastLapValue = idx < t.LastLap.Length ? t.LastLap[idx] : -999f,
                };

            if (_isRace && s.GridPos == 0 && t.Position[idx] > 0)
                s.GridPos = t.Position[idx];

            // First lap we ever saw this car on: ≤1 means we've watched its whole race, so its
            // stint boundaries are trustworthy even before its first stop. A mid-race join
            // (24h team events) leaves the opening stint unknowable.
            if (s.FirstSeenLap == int.MinValue && t.Lap[idx] >= 0)
                s.FirstSeenLap = t.Lap[idx];

            // Lap crossing: note it, but don't read CarIdxLastLapTime yet — iRacing bumps the
            // lap counter a beat before it publishes the time, and at 4 Hz we land in between.
            // The lap is recorded below once the value actually changes.
            if (t.Lap[idx] > s.LastLapSeen)
            {
                if (s.LastLapSeen >= 0)
                {
                    // Crossed again and the previous lap never produced a time → tow/reset.
                    // (Skip if the car has no laps yet: that's just the out lap.)
                    if (s.PendingCrossing && s.LapTimes.Count > 0) AddLap(s, -1f, s.PendingPit);
                    s.PendingCrossing = true;
                    s.PendingPit = s.PitThisLap || t.OnPitRoad[idx];
                }
                s.LastLapSeen = t.Lap[idx];
                s.PitThisLap = t.OnPitRoad[idx];
            }
            if (t.OnPitRoad[idx]) s.PitThisLap = true;

            if (s.PendingCrossing && idx < t.LastLap.Length && t.LastLap[idx] > 5
                && Math.Abs(t.LastLap[idx] - s.LastLapValue) > 0.0005f)
            {
                AddLap(s, t.LastLap[idx], s.PendingPit);
                s.LastLapValue = t.LastLap[idx];
                s.PendingCrossing = false;
            }

            // Track forward progress so a car parked on track (spin/crash) is detectable.
            if (_now >= 0 && idx < t.LapDistPct.Length && t.Lap[idx] >= 0)
            {
                double total = t.Lap[idx] + t.LapDistPct[idx];
                // ~0.0007 laps ≈ a few meters; a reset/tow jumps backwards, treat as movement.
                if (s.LastMoveTime < 0 || total >= s.LastTotalDist + 0.0007 || total < s.LastTotalDist - 0.5)
                {
                    // Moving again after sitting still for a while = rejoining (spin recovery / tow).
                    if (s.LastMoveTime >= 0 && _now - s.LastMoveTime > 4.0 && !s.WasOnPit)
                        s.RejoinTime = _now;
                    s.LastTotalDist = total;
                    s.LastMoveTime = _now;
                }

                // Smoothed progress rate over a 3s window → detect a car limping round well off pace.
                if (s.RateSampleTime < 0) { s.RateSampleTime = _now; s.RateSampleDist = total; }
                else if (_now - s.RateSampleTime >= 3.0)
                {
                    double dl = total - s.RateSampleDist;
                    if (dl >= 0 && dl < 0.6) s.LapsPerSec = dl / (_now - s.RateSampleTime);
                    s.RateSampleTime = _now;
                    s.RateSampleDist = total;
                }
            }

            // Dry<->wet tyre change (compound only changes across a pit stop): latch it so the
            // header can flag "someone committed to slicks/wets" — the practical crossover signal.
            if (idx < t.TireCompound.Length && t.TireCompound[idx] >= 0)
            {
                int comp = t.TireCompound[idx];
                if (s.LastCompound >= 0 && (comp >= 1) != (s.LastCompound >= 1) && _now >= 0)
                {
                    s.CompoundSwitchTime = _now;
                    s.SwitchedToWet = comp >= 1;
                }
                s.LastCompound = comp;
            }

            // Latch surface transitions for the tow rule below.
            if (idx < t.TrackSurface.Length)
            {
                int surf = t.TrackSurface[idx];
                if (s.Surface == int.MinValue) s.Surface = surf;
                else if (surf != s.Surface)
                {
                    if (surf == -1) s.SurfaceBeforeVanish = s.Surface;
                    s.PrevSurface = s.Surface;
                    s.Surface = surf;
                }
            }

            // A car out of the world reads OnPitRoad=false even mid-stop — a driver-change
            // blink used to split the pit visit in two (phantom exit + tow-shaped re-entry).
            // Freeze pit-edge accounting until it's back.
            if (s.Surface == -1) continue;

            bool onPit = t.OnPitRoad[idx];
            if (onPit && !s.WasOnPit)
            {
                // Teleport arrivals (tow / team-driver reconnect: the car materializes in its
                // stall without driving pit entry, surface never reads AproachingPits) and stints
                // with an unknown start (mid-race join) poison the stats — a 24h field's
                // "typical stint" once read as a car's entire lap count, and P·TOT clocked a
                // 20-minute reconnect as a pit stop. Count the visit, skip stint length + timing.
                bool droveIn = s.Surface == 2 || s.PrevSurface == 2;
                bool knownStart = s.PitCount > 0 || s.FirstSeenLap is >= 0 and <= 1;
                int stintLen = t.Lap[idx] - s.CurrentStintStartLap;
                if (droveIn && knownStart && stintLen >= 3) s.StintLengths.Add(stintLen);
                s.PitCount++;
                s.PendingStintLen = droveIn && knownStart && stintLen >= 3 ? stintLen : -1;
                s.SwapThisVisit = false;
                s.DmgThisVisit = false;
                if (droveIn)
                {
                    s.PitEntryTime = _now;
                    s.PitEntryLap = t.Lap[idx];
                    s.PitStationaryAccum = 0;
                    s.PitPrevNow = _now;
                    s.PitPrevDist = t.Lap[idx] + t.LapDistPct[idx];
                }
            }
            else if (!onPit && s.WasOnPit)
            {
                if (_now >= 0) s.LastPitExitTime = _now;
                bool tookTires = true;   // unknown stop ⇒ assume fresh rubber (the quiet failure)
                if (s.PitEntryTime >= 0 && _now > s.PitEntryTime)
                {
                    double total = _now - s.PitEntryTime;
                    double stat = Math.Min(total, s.PitStationaryAccum);
                    s.LastPit = new PitInfo(s.PitEntryLap, total, stat, Math.Max(0, total - stat));
                    s.PitEntryTime = -1;

                    // Tire inference: fuel time scales with the laps burned, tires add a fixed
                    // ~10s+ on top (service is sequential under fuel-and-tires-separate rules).
                    // Swap and repair (meatball) stops are excluded — their overhead looks
                    // exactly like tire time. (The jack lift itself isn't in the SDK.)
                    if (!s.SwapThisVisit && !s.DmgThisVisit && s.PendingStintLen > 0 && stat > 2)
                    {
                        double perLap = stat / s.PendingStintLen;
                        if (s.MinFillSecPerLap < double.MaxValue)
                            tookTires = stat - s.MinFillSecPerLap * s.PendingStintLen > 7;
                        if (perLap < s.MinFillSecPerLap) s.MinFillSecPerLap = perLap;
                    }
                }
                if (tookTires) { s.TireStartLap = t.Lap[idx]; s.TireStints = 1; }
                else s.TireStints++;
                s.PendingStintLen = -1;
                s.CurrentStintStartLap = t.Lap[idx];
            }
            // A meatball at any point during the visit means the stop time includes repairs.
            if (onPit && idx < t.SessionFlags.Length && (t.SessionFlags[idx] & CarFlags.Repair) != 0)
                s.DmgThisVisit = true;
            // Accumulate time sat still in the pit lane (≈ the stop itself) across the visit.
            if (onPit && s.PitEntryTime >= 0 && s.PitPrevNow >= 0 && _now >= 0)
            {
                double dt = _now - s.PitPrevNow;
                double dist = t.Lap[idx] + t.LapDistPct[idx];
                if (dt > 0 && dt < 2 && Math.Abs(dist - s.PitPrevDist) < 0.0004) s.PitStationaryAccum += dt;
                s.PitPrevNow = _now;
                s.PitPrevDist = dist;
            }
            s.WasOnPit = onPit;
        }
    }

    private static void AddLap(CarState s, float time, bool pit)
    {
        s.LapTimes.Add(time);
        s.PitLaps.Add(pit);
        if (s.LapTimes.Count > MaxLapTimes) { s.LapTimes.RemoveAt(0); s.PitLaps.RemoveAt(0); }
    }

    public int PositionsGained(int idx, int currentPos)
    {
        if (!_isRace || currentPos <= 0) return 0;
        return _cars.TryGetValue(idx, out var s) && s.GridPos > 0 ? s.GridPos - currentPos : 0;
    }

    /// <summary>Median completed stint length in laps, or null before the car's first stop.</summary>
    public int? TypicalStintLaps(int idx)
    {
        if (!_cars.TryGetValue(idx, out var s) || s.StintLengths.Count == 0) return null;
        var sorted = s.StintLengths.OrderBy(x => x).ToList();
        return sorted[sorted.Count / 2];
    }

    /// <summary>
    /// Strategy summary for the car: the lap its next pit stop is expected on.
    /// Returns "" when unknowable (no completed stint yet, or not a race).
    /// "~34" next stop around lap 34 · "34!" overdue · "0stp" can make the finish ·
    /// "~34*" that stop is the last one and only a splash. (Used to show "Nstp" stop
    /// counts when 2+ stops remained — in an endurance race that told you nothing
    /// actionable; the lap their window opens is the number you race against.)
    /// </summary>
    public string StrategyText(int idx, int carLap, double lapsRemain)
    {
        if (!_isRace || TypicalStintLaps(idx) is not int stint || !_cars.TryGetValue(idx, out var s))
            return "";

        int lapsIntoStint = carLap - s.CurrentStintStartLap;
        int lapsLeftInTank = stint - lapsIntoStint;

        if (lapsRemain >= 0 && lapsRemain <= lapsLeftInTank) return "0stp";

        int expectedPitLap = s.CurrentStintStartLap + stint;
        if (lapsLeftInTank < 0) return $"{expectedPitLap}!";
        if (lapsRemain < 0) return $"~{expectedPitLap}";

        double afterTank = lapsRemain - Math.Max(0, lapsLeftInTank);
        bool splash = afterTank > 0 && afterTank < stint * 0.2;   // one short final stop left
        return $"~{expectedPitLap}{(splash ? "*" : "")}";
    }

    /// <summary>True for a few seconds after the car rejoined the track from pit road — a cold,
    /// slow out-lap car worth flagging in the relative.</summary>
    public bool JustExitedPits(int idx, double withinSec) =>
        _now >= 0 && _cars.TryGetValue(idx, out var s) && !s.WasOnPit
        && s.LastPitExitTime >= 0 && _now - s.LastPitExitTime <= withinSec;

    /// <summary>True while the car is on its out-lap (rejoined from pit, hasn't completed a green
    /// lap yet) — cold tyres for the whole lap, not just the pit exit.</summary>
    public bool OnOutLap(int idx, int carLap) => LapsSincePit(idx, carLap) is 0;

    /// <summary>The car's last dry↔wet tyre switch as (time, direction: +1 to wet · -1 to dry),
    /// or (-1, 0) if none. Used both inline (the tyre column) and for the header alert.</summary>
    public (double Time, int Dir) LastCompoundSwitch(int idx) =>
        _cars.TryGetValue(idx, out var s) && s.CompoundSwitchTime >= 0
            ? (s.CompoundSwitchTime, s.SwitchedToWet ? 1 : -1) : (-1, 0);

    /// <summary>Laps since the car's last pit stop, or null before its first stop (tyre age
    /// from the race start isn't comparable to a fresh set).</summary>
    public int? LapsSincePit(int idx, int carLap) =>
        _cars.TryGetValue(idx, out var s) && s.PitCount > 0 && !s.WasOnPit
            ? Math.Max(0, carLap - s.CurrentStintStartLap)
            : null;

    /// <summary>Laps the car has done in its current stint, or null when unknowable — on pit
    /// road, or we joined mid-race and haven't seen it stop yet (its stint start is a mystery).
    /// Unlike <see cref="LapsSincePit"/>, laps since the race start count: before the first
    /// stop of a race watched from the start, that IS the current stint.</summary>
    public int? StintLaps(int idx, int carLap) =>
        _cars.TryGetValue(idx, out var s) && !s.WasOnPit
        && (s.PitCount > 0 || s.FirstSeenLap is >= 0 and <= 1)
            ? Math.Max(0, carLap - s.CurrentStintStartLap)
            : null;

    /// <summary>A driver change was detected on this car: drop the old driver's pace history
    /// (their laps say nothing about the new driver), and mark the current pit visit so its
    /// stop time is never read as a tire change — driver-swap overhead looks exactly like
    /// tire time. Stint/pit state survives (the car's strategy timeline continues).</summary>
    public void NoteDriverSwap(int idx)
    {
        if (!_cars.TryGetValue(idx, out var s)) return;
        s.LapTimes.Clear();
        s.PitLaps.Clear();
        if (s.WasOnPit) s.SwapThisVisit = true;
    }

    /// <summary>Laps on the car's current tires plus the stint count on that rubber (1 = fresh
    /// this stint, 2 = double-stinting, 3 = triple), inferred from stop lengths (the pit-exit
    /// classifier), or null when unknowable. Age equals <see cref="StintLaps"/> unless a stop
    /// was classified fuel-only, where it keeps counting across the stop.</summary>
    public (int Age, int Stints)? TireInfo(int idx, int carLap) =>
        _cars.TryGetValue(idx, out var s) && !s.WasOnPit
        && (s.PitCount > 0 || s.FirstSeenLap is >= 0 and <= 1)
            ? (Math.Max(0, carLap - s.TireStartLap), Math.Max(1, s.TireStints))
            : null;

    /// <summary>Laps on the car's current tires (see <see cref="TireInfo"/>).</summary>
    public int? TireAgeLaps(int idx, int carLap) => TireInfo(idx, carLap)?.Age;

    /// <summary>All recorded laps for the car, oldest first (capped at 30). -1 = lap with no time.</summary>
    public IReadOnlyList<float> LapTimesFor(int idx) =>
        _cars.TryGetValue(idx, out var s) ? s.LapTimes : [];

    /// <summary>Completed timed laps for the car.</summary>
    public int LapCount(int idx) =>
        _cars.TryGetValue(idx, out var s) ? s.LapTimes.Count(x => x > 0) : 0;

    /// <summary>Average of the car's last <paramref name="n"/> clean laps (timed, no pit lane), or null.</summary>
    public float? RecentPace(int idx, int n = 5)
    {
        var clean = CleanLaps(idx);
        if (clean.Count < Math.Min(n, 3)) return null;
        return clean.TakeLast(n).Average();
    }

    private List<float> CleanLaps(int idx)
    {
        if (!_cars.TryGetValue(idx, out var s)) return [];
        var result = new List<float>(s.LapTimes.Count);
        for (int i = 0; i < s.LapTimes.Count; i++)
            if (s.LapTimes[i] > 0 && !(i < s.PitLaps.Count && s.PitLaps[i]))
                result.Add(s.LapTimes[i]);
        return result;
    }

    /// <summary>
    /// True while the car sits in its pit stall having never driven through the pit entry —
    /// iRacing teleports towed cars straight into their stall, while a normal stop always
    /// passes through AproachingPits(2) first. Race only by definition: practice/qual resets
    /// teleport the same way but aren't tows. The needs-a-lap guard keeps race joiners
    /// (who also spawn in the stall) out. This replaces the old "stopped a long time = TOW"
    /// guess, which mislabeled every parked car.
    /// </summary>
    public bool WasTowedIn(RawTick t, int idx) =>
        _isRace
        && idx < t.TrackSurface.Length && t.TrackSurface[idx] == 1        // InPitStall
        && _cars.TryGetValue(idx, out var s)
        && s.LastLapSeen >= 1
        && s.PrevSurface is -1 or 0 or 3    // vanished / off-track / on-track — anything but pit entry
        // Driver changes blink the car out of the world for a second or two MID-STOP; it
        // re-materializes in its stall exactly like a tow. Only a car that vanished from the
        // track (or off-track) was actually towed — one that vanished from the pit area wasn't.
        && !(s.PrevSurface == -1 && s.SurfaceBeforeVanish is 1 or 2);

    /// <summary>
    /// True when the car has been stationary on track (not pit road) for a few seconds —
    /// almost always a spin, crash, or a car waiting for a tow.
    /// </summary>
    public bool LooksStopped(int idx) =>
        _now >= 0 && _cars.TryGetValue(idx, out var s)
        && !s.WasOnPit && s.LastLapSeen >= 1 && s.LastMoveTime >= 0
        && _now - s.LastMoveTime > 4.0;

    /// <summary>Completed pit stops for the car (0 before its first stop). Stint number = this + 1.</summary>
    public int PitStops(int idx) => _cars.TryGetValue(idx, out var s) ? s.PitCount : 0;

    /// <summary>The car's last completed pit visit (lap + total/stationary/drive-through seconds),
    /// or null before its first stop.</summary>
    public PitInfo? LastPit(int idx) => _cars.TryGetValue(idx, out var s) ? s.LastPit : null;

    /// <summary>Seconds the car has been sitting still (0 if moving / unknown).</summary>
    public double StoppedSeconds(int idx) =>
        _now >= 0 && _cars.TryGetValue(idx, out var s) && s.LastMoveTime >= 0
            ? _now - s.LastMoveTime : 0;

    /// <summary>True for a few seconds after a stopped car started moving again — a spin recovery
    /// or tow rejoining the track. Experimental; toggle via config if it misfires.</summary>
    public bool IsRejoining(int idx, double withinSec) =>
        _now >= 0 && _cars.TryGetValue(idx, out var s)
        && s.RejoinTime >= 0 && _now - s.RejoinTime <= withinSec && !LooksStopped(idx);

    /// <summary>True when the car is moving but crawling — well below its class pace (a limping,
    /// damaged car). <paramref name="refLap"/> = class ref-lap seconds. Not for stopped cars.</summary>
    public bool LooksSlow(int idx, float refLap) =>
        refLap > 10 && _cars.TryGetValue(idx, out var s) && s.LapsPerSec >= 0 && s.LastLapSeen >= 1
        && s.LapsPerSec * refLap is > 0.05 and < 0.35 && !LooksStopped(idx);

    /// <summary>
    /// True when the car looks like it's fuel-saving: consistent laps well off its own best.
    /// Heuristic — traffic can trigger it too, which is why it's a small tag, not a headline.
    /// </summary>
    public bool LooksLikeFuelSaving(int idx)
    {
        if (!_isRace) return false;
        var timed = CleanLaps(idx);
        if (timed.Count < 5) return false;

        var recent = timed.TakeLast(4).ToList();
        float best = timed.Min();
        float avg = recent.Average();
        if (avg < best * 1.015f) return false;                    // pushing

        double spread = recent.Max() - recent.Min();
        return spread < best * 0.01f;                             // slow AND metronomic = saving
    }

    public void Reset() => _cars.Clear();

    // ---- restart survival (SessionStateStore): only the durable, lap-anchored fields.
    // Time-anchored transients (movement timers, surface latches, pending crossings) re-latch
    // within a tick; restoring them stale would misfire the tow/spin heuristics.

    public Dictionary<int, CarStintDto> Export()
    {
        var result = new Dictionary<int, CarStintDto>(_cars.Count);
        foreach (var (idx, s) in _cars)
            result[idx] = new CarStintDto
            {
                LastLapSeen = s.LastLapSeen,
                LastLapValue = s.LastLapValue,
                LapTimes = [.. s.LapTimes],
                PitLaps = [.. s.PitLaps],
                StintLengths = [.. s.StintLengths],
                CurrentStintStartLap = s.CurrentStintStartLap,
                PitCount = s.PitCount,
                FirstSeenLap = s.FirstSeenLap,
                GridPos = s.GridPos,
                TireStartLap = s.TireStartLap,
                TireStints = s.TireStints,
                MinFillSecPerLap = s.MinFillSecPerLap,
                LastPit = s.LastPit,
            };
        return result;
    }

    public void Import(Dictionary<int, CarStintDto> cars)
    {
        _cars.Clear();
        foreach (var (idx, d) in cars)
        {
            var s = new CarState
            {
                LastLapSeen = d.LastLapSeen,
                LastLapValue = d.LastLapValue,
                CurrentStintStartLap = d.CurrentStintStartLap,
                PitCount = d.PitCount,
                FirstSeenLap = d.FirstSeenLap,
                GridPos = d.GridPos,
                TireStartLap = d.TireStartLap,
                TireStints = Math.Max(1, d.TireStints),
                MinFillSecPerLap = d.MinFillSecPerLap,
                LastPit = d.LastPit,
            };
            s.LapTimes.AddRange(d.LapTimes);
            s.PitLaps.AddRange(d.PitLaps);
            s.StintLengths.AddRange(d.StintLengths);
            _cars[idx] = s;
        }
    }
}
