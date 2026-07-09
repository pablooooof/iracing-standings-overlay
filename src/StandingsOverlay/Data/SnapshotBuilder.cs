using StandingsOverlay.Config;

namespace StandingsOverlay.Data;

/// <summary>
/// Turns a raw telemetry tick + roster into the display model. Pure function of its inputs,
/// shared by the live iRacing source and the demo source so both exercise the same logic.
/// </summary>
public static class SnapshotBuilder
{
    public static StandingsSnapshot Build(RawTick t, Roster roster, GapHistory history,
        StintTracker stints, WeatherTracker weather, DriverSwapTracker swap, OverlayConfig cfg)
    {
        var kind = StandingsSnapshot.KindOf(t.SessionType);
        bool isRace = kind == SessionKind.Race;

        // Quali: one cell per lap. Sessions with a lap limit tell us the count up front;
        // timed sessions grow with the laps actually run (min 2, max 4 columns).
        int cellCount = kind switch
        {
            SessionKind.Race => cfg.DeltaLaps,
            SessionKind.Qualify => t.SessionLapsTotal is > 0 and <= 6
                ? t.SessionLapsTotal
                : Math.Clamp(
                    roster.Drivers.Keys.Select(i => stints.LapTimesFor(i).Count).DefaultIfEmpty(0).Max(), 2, 4),
            _ => 0,
        };
        var cellHeaders = kind switch
        {
            SessionKind.Race => Enumerable.Range(0, cellCount).Select(k => $"Δ-{cellCount - k}").ToList(),
            SessionKind.Qualify => Enumerable.Range(1, cellCount).Select(k => $"L{k}").ToList(),
            _ => new List<string>(),
        };

        var cars = roster.Drivers.Values
            .Where(d => !d.IsPaceCar && !d.IsSpectator && t.Has(d.CarIdx))
            .ToList();

        // Practice/qual: hide cars that never turned a lap and aren't currently on track —
        // otherwise the list is a wall of empty garage rows. The player always shows.
        if (!isRace)
            cars = cars.Where(d => d.CarIdx == t.PlayerCarIdx
                                || EffBest(t, roster, d.CarIdx) > 0
                                || (d.CarIdx < t.Lap.Length && t.Lap[d.CarIdx] >= 0)).ToList();

        // Group into classes, fastest class first. Single-class fields collapse to one group.
        var classes = cars.GroupBy(d => d.CarClassId)
                          .OrderBy(g => g.Min(d => d.ClassEstLap > 0 ? d.ClassEstLap : float.MaxValue))
                          .ToList();
        bool multiclass = classes.Count > 1;

        int playerClassId = roster.Drivers.TryGetValue(t.PlayerCarIdx, out var me) ? me.CarClassId
                            : classes.Count > 0 ? classes[0].Key : 0;

        double lapsRemain = EstimateLapsRemain(t, roster);
        float? playerPace = stints.RecentPace(t.PlayerCarIdx);

        var rows = new List<StandingsRow>(24);
        foreach (var cls in classes)
        {
            bool isPlayerClass = cls.Key == playerClassId;
            if (multiclass && !isPlayerClass && cfg.OtherClassesDriversAtTop <= 0) continue;

            var ordered = OrderClass(cls.ToList(), t, roster, isRace);
            if (ordered.Count == 0) continue;

            // Which indices of this class to show.
            var picked = new SortedSet<int>();
            if (isPlayerClass)
            {
                // Quali fields are small and you want the full picture once your run is done.
                if (kind == SessionKind.Qualify && cfg.QualifyShowFullClass)
                    for (int i = 0; i < ordered.Count; i++) picked.Add(i);
                else
                {
                    for (int i = 0; i < Math.Min(cfg.DriversAtTop, ordered.Count); i++) picked.Add(i);
                    int playerPos = ordered.FindIndex(d => d.CarIdx == t.PlayerCarIdx);
                    if (playerPos >= 0)
                        for (int i = Math.Max(0, playerPos - cfg.DriversAhead);
                             i <= Math.Min(ordered.Count - 1, playerPos + cfg.DriversBehind); i++)
                            picked.Add(i);
                }
            }
            else
            {
                for (int i = 0; i < Math.Min(cfg.OtherClassesDriversAtTop, ordered.Count); i++) picked.Add(i);
            }
            if (picked.Count == 0) continue;

            if (multiclass)
            {
                var first = ordered[0];
                rows.Add(StandingsRow.ClassHeader(
                    string.IsNullOrEmpty(first.ClassName) ? $"CLASS {cls.Key}" : first.ClassName.ToUpperInvariant(),
                    first.ClassColor));
            }

            float classBest = ordered.Select(d => EffBest(t, roster, d.CarIdx))
                                     .Where(b => b > 0)
                                     .DefaultIfEmpty(0).Min();

            // Smooth gaps (config): cumulative est-time interval down the running order, so every
            // car's gap-to-leader is continuous like the relative rather than stepping at the line.
            double[]? smoothCum = null;
            if (isRace && cfg.SmoothGaps)
            {
                float rl = ordered[0].ClassEstLap > 10 ? ordered[0].ClassEstLap : 90f;
                smoothCum = new double[ordered.Count];
                for (int j = 1; j < ordered.Count; j++)
                    smoothCum[j] = smoothCum[j - 1]
                        + Math.Max(0, RelativeGap.SignedSecondsBetween(t, ordered[j - 1].CarIdx, ordered[j].CarIdx, rl));
            }

            // Who's actually fastest ON TRACK right now: rank the class by average of the
            // last 5 clean laps (timed, no pit lane). Rank 1 = fastest.
            var paceRank = new Dictionary<int, int>();
            var byPace = ordered.Select(d => (d.CarIdx, Pace: stints.RecentPace(d.CarIdx)))
                                .Where(x => x.Pace is not null)
                                .OrderBy(x => x.Pace!.Value).ToList();
            for (int r = 0; r < byPace.Count; r++) paceRank[byPace[r].CarIdx] = r + 1;

            int prev = -1;
            foreach (int i in picked)
            {
                if (prev >= 0 && i != prev + 1) rows.Add(StandingsRow.Separator);
                prev = i;
                rows.Add(BuildRow(i, ordered, t, roster, history, stints, swap, cfg, kind, cellCount,
                                  classBest, playerPace, paceRank, lapsRemain, smoothCum));
            }
        }

        return new StandingsSnapshot(true, kind, isRace ? "RACE" : t.SessionType.ToUpperInvariant(),
            HeaderGroups(t, roster, weather, cfg, playerClassId), cellHeaders, rows,
            HeaderAlert: HeaderAlert(t, roster, stints, weather, cfg));
    }

    private static List<DriverEntry> OrderClass(List<DriverEntry> cars, RawTick t, Roster roster, bool isRace)
    {
        if (isRace)
        {
            // Live track-position ordering, like a relative: an overtake swaps the rows
            // immediately instead of waiting for iRacing to re-score at the next timing line.
            var moving = cars.Where(d => t.Lap[d.CarIdx] >= 0
                                      && t.Lap[d.CarIdx] + t.LapDistPct[d.CarIdx] > 0.001)
                             .OrderByDescending(d => t.Lap[d.CarIdx] + t.LapDistPct[d.CarIdx])
                             .ToList();
            if (moving.Count > 0)
            {
                // Cars no longer in the world (retired/towed) sit below, in scored order.
                var rest = cars.Except(moving)
                               .OrderBy(d => t.Position[d.CarIdx] > 0 ? t.Position[d.CarIdx] : int.MaxValue)
                               .ThenBy(d => OfficialPos(roster, d.CarIdx))
                               .ToList();
                return moving.Concat(rest).ToList();
            }
            // Pre-grid nobody has telemetry yet — fall through to the official order below
            // (the source substitutes quali results, so the grid matches qualifying).
        }

        // Practice/qual/pre-grid: official results position first (matches the sim's own
        // standings, including invalid-lap handling), then live best lap, then car number.
        return cars.OrderBy(d => OfficialPos(roster, d.CarIdx))
                   .ThenBy(d => EffBest(t, roster, d.CarIdx) is var b and > 0 ? b : float.MaxValue)
                   .ThenBy(d => d.CarNumber, StringComparer.Ordinal)
                   .ToList();
    }

    /// <summary>Official position from the session results (class position when scored).</summary>
    private static int OfficialPos(Roster roster, int idx) =>
        roster.Results.TryGetValue(idx, out var r)
            ? r.ClassPosition > 0 ? r.ClassPosition : r.Position > 0 ? r.Position : int.MaxValue
            : int.MaxValue;

    /// <summary>Best lap from live telemetry, falling back to the session-YAML results —
    /// telemetry arrays go blank for cars that aren't currently in the world.</summary>
    private static float EffBest(RawTick t, Roster roster, int idx) =>
        idx < t.BestLap.Length && t.BestLap[idx] > 0 ? t.BestLap[idx]
        : roster.Results.TryGetValue(idx, out var r) && r.BestLap > 0 ? r.BestLap : 0;

    private static float EffLast(RawTick t, Roster roster, int idx) =>
        idx < t.LastLap.Length && t.LastLap[idx] > 0 ? t.LastLap[idx]
        : roster.ResultsFromCurrentSession
          && roster.Results.TryGetValue(idx, out var r) && r.LastLap > 0 ? r.LastLap : 0;

    private static StandingsRow BuildRow(int i, List<DriverEntry> ordered, RawTick t, Roster roster,
        GapHistory history, StintTracker stints, DriverSwapTracker swap, OverlayConfig cfg,
        SessionKind kind, int cellCount,
        float classBest, float? playerPace, Dictionary<int, int> paceRank, double lapsRemain,
        double[]? smoothCum)
    {
        bool isRace = kind == SessionKind.Race;
        var d = ordered[i];
        int idx = d.CarIdx;
        var leader = ordered[0];

        string gap = "", interval = "";
        if (isRace)
        {
            if (i > 0 && smoothCum != null)
            {
                // Smooth: gap-to-leader is the cumulative sum of adjacent est-time intervals
                // (each short, so the ±0.5-lap est-time method stays valid); laps-down still "NL".
                gap = LapsDownText(t, leader.CarIdx, idx) ?? FmtGap(smoothCum[i], cfg.GapPrecision);
                interval = LapsDownText(t, ordered[i - 1].CarIdx, idx)
                           ?? FmtGap(Math.Max(0, smoothCum[i] - smoothCum[i - 1]), cfg.IntervalPrecision);
            }
            else if (i > 0)
            {
                gap = GapToCar(t, idx, leader.CarIdx, cfg.GapPrecision);
                interval = GapToCar(t, idx, ordered[i - 1].CarIdx, cfg.IntervalPrecision);
            }
        }
        else
        {
            // Quali gaps are best-lap deltas where hundredths decide positions.
            int gapPrec = kind == SessionKind.Qualify ? cfg.QualifyGapPrecision : cfg.GapPrecision;
            int intPrec = kind == SessionKind.Qualify ? cfg.QualifyGapPrecision : cfg.IntervalPrecision;
            float best = EffBest(t, roster, idx);
            if (best > 0 && classBest > 0 && i > 0)
                gap = FmtGap(best - classBest, gapPrec);
            if (best > 0 && i > 0 && EffBest(t, roster, ordered[i - 1].CarIdx) > 0)
                interval = FmtGap(best - EffBest(t, roster, ordered[i - 1].CarIdx), intPrec);
        }

        var deltaCells = new DeltaCell[cellCount];
        Array.Fill(deltaCells, new DeltaCell("", 0));   // default structs have a null Text
        if (kind == SessionKind.Race && idx != t.PlayerCarIdx)
        {
            var perLap = history.PerLapDeltas(idx, cellCount);
            for (int k = 0; k < perLap.Length; k++)
            {
                if (perLap[k] is not float dl) { deltaCells[k] = new DeltaCell("", 0); continue; }
                // A swing this big means someone pitted (or crashed) that lap. Show the real
                // number ("you gained 38s") but dimmed — it says nothing about raw pace.
                if (Math.Abs(dl) >= 10f)
                {
                    deltaCells[k] = new DeltaCell(FmtGap(Math.Abs(dl), 0), 0);
                    continue;
                }
                // Color carries the sign: green = player gained on this car during that lap.
                int sign = Math.Abs(dl) < 0.05f ? 0 : Math.Sign(dl);
                deltaCells[k] = new DeltaCell(Math.Abs(dl).ToString("F" + cfg.DeltaPrecision), sign);
            }
        }
        else if (kind == SessionKind.Qualify)
        {
            // One cell per quali lap: purple = class best, green = this car's best, ✕ = no time.
            var lapTimes = stints.LapTimesFor(idx);
            float carBest = t.BestLap[idx];
            for (int k = 0; k < cellCount; k++)
            {
                if (k >= lapTimes.Count) { deltaCells[k] = new DeltaCell("", 0); continue; }
                float lt = lapTimes[k];
                if (lt <= 0) { deltaCells[k] = new DeltaCell("✕", 0); continue; }
                int sign = classBest > 0 && Math.Abs(lt - classBest) < 0.0005f ? 2
                         : carBest > 0 && Math.Abs(lt - carBest) < 0.0005f ? -1
                         : 0;
                deltaCells[k] = new DeltaCell(FmtLap(lt, cfg.LapTimePrecision), sign);
            }
        }

        int gained = stints.PositionsGained(idx, t.Position.Length > idx ? t.Position[idx] : 0);

        // Pace is relative to the PLAYER: ▲ red they're pulling away, ▼ green we're catching,
        // ► yellow matched pace. "S" = looks like fuel saving.
        string pace = "";
        int paceSign = 0;
        if (isRace && idx != t.PlayerCarIdx
            && stints.RecentPace(idx) is float rp && playerPace is float pp && pp > 0)
        {
            if (rp < pp * 0.997f) { pace = "▲"; paceSign = 1; }
            else if (rp > pp * 1.003f) { pace = "▼"; paceSign = -1; }
            else { pace = "►"; paceSign = 0; }
            if (stints.LooksLikeFuelSaving(idx)) pace += "S";
        }

        float last = EffLast(t, roster, idx);
        float bestLap = EffBest(t, roster, idx);
        int lapsDone = Math.Max(stints.LapCount(idx),
            roster.ResultsFromCurrentSession
            && roster.Results.TryGetValue(idx, out var res) ? res.LapsComplete : 0);

        return new StandingsRow(
            Kind: RowKind.Normal,
            LapsText: lapsDone > 0 ? lapsDone.ToString() : "",
            PosText: (i + 1).ToString(),
            // Green (gained) / red (lost) already carries the direction — show just the count.
            PosGainedText: gained == 0 ? "" : Math.Abs(gained).ToString(),
            PosGainedSign: gained > 0 ? -1 : gained < 0 ? 1 : 0,
            CarNumber: "#" + d.CarNumber,
            Name: d.Name,
            IRatingText: FmtIr(d.IRating),
            LicText: d.LicString,
            LicColor: d.LicColor,
            CarBrand: d.CarBrand,
            ClassColor: d.ClassColor,
            // NOTE: some dry series use compound 1 for an alternate dry tyre; we render >=1 as wet.
            Tyre: idx < t.TireCompound.Length ? t.TireCompound[idx] : -1,
            GapText: gap,
            IntervalText: interval,
            BestLapText: bestLap > 0 ? FmtLap(bestLap, cfg.LapTimePrecision) : "",
            BestLapSign: bestLap > 0 && classBest > 0 && Math.Abs(bestLap - classBest) < 0.0005f ? 2 : 0,
            LastLapText: last > 0 ? FmtLap(last, cfg.LapTimePrecision) : "",
            DeltaCells: deltaCells,
            StatusText: Status(t, stints, idx, cfg.ShowRejoinState, swap.JustSwapped(idx, 60)),
            RankText: paceRank.TryGetValue(idx, out var rank) ? rank.ToString() : "",
            RankSign: paceRank.TryGetValue(idx, out var rk) ? (rk == 1 ? 2 : rk <= 3 ? -1 : 0) : 0,
            StratText: stints.StrategyText(idx, t.Lap[idx], lapsRemain),
            PaceText: pace,
            PaceSign: paceSign,
            IsPlayer: idx == t.PlayerCarIdx,
            Offline: idx < t.TrackSurface.Length && t.TrackSurface[idx] == -1 && idx != t.PlayerCarIdx,
            PitLapText: isRace && stints.LastPit(idx) is { } pl ? pl.Lap.ToString() : "",
            PitTotalText: isRace && stints.LastPit(idx) is { } pt ? pt.Total.ToString("0.0") : "",
            PitDriveText: isRace && stints.LastPit(idx) is { } pd ? pd.DriveThrough.ToString("0.0") : "",
            PitStallText: isRace && stints.LastPit(idx) is { } ps ? ps.Stationary.ToString("0.0") : "");
    }

    /// <summary>"NL" when the car is a lap or more down on the reference, else null.</summary>
    private static string? LapsDownText(RawTick t, int refIdx, int idx)
    {
        double d = (t.Lap[refIdx] + t.LapDistPct[refIdx]) - (t.Lap[idx] + t.LapDistPct[idx]);
        return d >= 1.0 ? $"{(int)d}L" : null;
    }

    /// <summary>Race gap between two cars via CarIdxF2Time (iRacing's own behind-leader time),
    /// laps-down aware. Positive by construction, so no leading "+".</summary>
    private static string GapToCar(RawTick t, int idx, int refIdx, int precision) =>
        LapsDownText(t, refIdx, idx) ?? FmtGap(Math.Max(0, t.F2Time[idx] - t.F2Time[refIdx]), precision);

    /// <summary>Highest-priority per-car status badge.</summary>
    private static string Status(RawTick t, StintTracker stints, int idx, bool showRejoin, bool swapped)
    {
        int flags = idx < t.SessionFlags.Length ? t.SessionFlags[idx] : 0;
        if ((flags & CarFlags.Disqualify) != 0) return "DQ";
        if ((flags & CarFlags.Black) != 0) return "BLK";
        if ((flags & CarFlags.Repair) != 0) return "DMG";
        if ((flags & CarFlags.Furled) != 0) return "WRN";
        // A car that dropped offline freezes its telemetry, so the "stationary" timer grows
        // forever — SPUN is only real while the car is still in the world (surface != -1).
        bool inWorld = idx >= t.TrackSurface.Length || t.TrackSurface[idx] != -1;
        if (inWorld && stints.LooksStopped(idx)) return StoppedBadge(t, stints, idx);
        if (swapped) return "SWAP";   // new driver just took over (team endurance)
        if (showRejoin && inWorld && stints.IsRejoining(idx, 6)) return "REJOIN";
        if (idx < t.OnPitRoad.Length && t.OnPitRoad[idx]) return "PIT";
        return "";
    }

    /// <summary>A stationary car is SPUN (on track, likely to recover) or TOW (off the racing
    /// surface, or stuck long enough that a tow is coming).</summary>
    internal static string StoppedBadge(RawTick t, StintTracker stints, int idx)
    {
        bool offTrack = idx < t.TrackSurface.Length && t.TrackSurface[idx] == 0;
        return offTrack || stints.StoppedSeconds(idx) > 15 ? "TOW" : "SPUN";
    }

    private static double EstimateLapsRemain(RawTick t, Roster roster)
    {
        if (t.SessionLapsRemain >= 0) return t.SessionLapsRemain;
        if (t.SessionTimeRemain > 0 && t.SessionTimeRemain < 172800 &&
            roster.Drivers.TryGetValue(t.PlayerCarIdx, out var me) && me.ClassEstLap > 10)
            return t.SessionTimeRemain / me.ClassEstLap;
        return -1;
    }

    /// <summary>Header metrics grouped into chips by type: [time] · [track/field] · [weather].
    /// Each chip is rendered as a rounded pill so related numbers read as a cluster.</summary>
    private static List<string> HeaderGroups(RawTick t, Roster roster, WeatherTracker weather,
        OverlayConfig cfg, int playerClassId)
    {
        var groups = new List<string>(3);

        // Time: real-life clock · in-sim track clock · lap counter · session clock.
        var time = new List<string>(4);
        if (cfg.ShowRealClock) time.Add(DateTime.Now.ToString("H:mm"));
        if (cfg.ShowTimeOfDay && t.TimeOfDay >= 0) time.Add("☀ " + FmtTimeOfDay(t.TimeOfDay));
        if (LapCounter(t, roster) is { Length: > 0 } laps) time.Add(laps);
        if (SessionClock(t) is { Length: > 0 } clock) time.Add(clock);
        if (time.Count > 0) groups.Add(string.Join(" · ", time));

        // Track / field: SoF (player's class) · track temp · incidents.
        var track = new List<string>(3);
        if (cfg.ShowSof)
        {
            double sof = roster.SofByClass.TryGetValue(playerClassId, out var s) && s > 25
                ? s : roster.StrengthOfField;
            if (sof > 25) track.Add($"SoF {FmtIr((int)sof)}");
        }
        if (cfg.ShowTrackTemp && !float.IsNaN(t.TrackTemp))
            track.Add($"{t.TrackTemp.ToString("F" + Math.Clamp(cfg.ShowTrackTempDecimals, 0, 2))}°C{TrendArrow(weather.TempTrend)}");
        if (cfg.ShowIncidents && t.PlayerIncidents >= 0) track.Add($"{t.PlayerIncidents}x");
        if (track.Count > 0) groups.Add(string.Join(" · ", track));

        // Weather: wetness · rain % · wind.
        var sky = new List<string>(3);
        if (cfg.ShowWeather)
        {
            var wet = t.DeclaredWet ? "WET declared" : WetnessText(t.TrackWetness, cfg.AbbreviateWetness);
            if (!string.IsNullOrEmpty(wet)) sky.Add(wet);
            if (!float.IsNaN(t.Precipitation)) sky.Add($"☂ {t.Precipitation * 100:0}%{TrendArrow(weather.PrecipTrend)}");
        }
        if (cfg.ShowWind && WindText(t.WindVel, t.WindDir) is { Length: > 0 } wind) sky.Add(wind);
        if (sky.Count > 0) groups.Add(string.Join(" · ", sky));

        return groups;
    }

    /// <summary>Trend suffix for a header metric: rising ↑, falling ↓, steady (nothing).</summary>
    private static string TrendArrow(int trend) => trend > 0 ? " ↑" : trend < 0 ? " ↓" : "";

    /// <summary>The flashing header banner text (empty = no flash). Dry→wet takes priority, then
    /// the most recent dry↔wet tyre switch by any car (the crossover "someone committed" signal).</summary>
    private static string HeaderAlert(RawTick t, Roster roster, StintTracker stints,
        WeatherTracker weather, OverlayConfig cfg)
    {
        if (cfg.ShowWeather && weather.JustTurnedWet) return "⚠ TRACK WENT WET";

        double bestT = -1; int bestDir = 0; string bestNum = "";
        foreach (var d in roster.Drivers.Values)
        {
            if (d.IsPaceCar || d.IsSpectator) continue;
            var (ct, dir) = stints.LastCompoundSwitch(d.CarIdx);
            if (dir != 0 && ct >= 0 && t.SessionTime - ct <= cfg.TyreSwitchAlertSec && ct > bestT)
                (bestT, bestDir, bestNum) = (ct, dir, d.CarNumber);
        }
        return bestDir > 0 ? $"⚑ #{bestNum} → WETS"
             : bestDir < 0 ? $"⚑ #{bestNum} → SLICKS"
             : "";
    }

    /// <summary>"L 3/40" — player lap over the known or estimated total.</summary>
    private static string LapCounter(RawTick t, Roster roster)
    {
        if (t.PlayerCarIdx < 0 || t.PlayerCarIdx >= t.Lap.Length) return "";
        int cur = Math.Max(0, t.Lap[t.PlayerCarIdx]);
        double remain = EstimateLapsRemain(t, roster);
        if (remain < 0) return "";
        return t.SessionLapsRemain >= 0 ? $"L {cur}/{cur + t.SessionLapsRemain}"
                                        : $"L {cur}/{cur + remain:0.#}";
    }

    /// <summary>"15:05/40m" elapsed/total, or the remaining clock when total is unknown.</summary>
    private static string SessionClock(RawTick t)
    {
        if (t.SessionTime >= 0 && t.SessionTimeTotal > 0 && t.SessionTimeTotal < 172800)
            return $"{FmtClock(t.SessionTime)}/{FmtTotal(t.SessionTimeTotal)}";
        if (t.SessionTimeRemain >= 0 && t.SessionTimeRemain < 172800)
            return FmtClock(t.SessionTimeRemain);
        return "";
    }

    /// <summary>In-sim local time of day, "H:mm" (24h), like iOverlay's clock.</summary>
    private static string FmtTimeOfDay(double s)
    {
        var ts = TimeSpan.FromSeconds(((s % 86400) + 86400) % 86400);
        return $"{ts.Hours}:{ts.Minutes:00}";
    }

    private static readonly string[] WindArrows = ["↑", "↗", "→", "↘", "↓", "↙", "←", "↖"];

    /// <summary>Compass arrow (pointing where the wind blows TO) + speed in km/h. WindDir is the
    /// bearing the wind comes FROM, so we add π to get the travel direction.</summary>
    private static string WindText(float velMs, float dirRad)
    {
        if (float.IsNaN(velMs) || float.IsNaN(dirRad)) return "";
        int idx = (int)Math.Round((dirRad + Math.PI) / (Math.PI / 4)) & 7;
        return $"{WindArrows[idx]} {velMs * 3.6:0} km/h";
    }

    private static string WetnessText(int w, bool abbrev) => w switch
    {
        1 => "Dry",
        2 => abbrev ? "M.Dry" : "Mostly Dry",
        3 or 4 => "Damp",
        5 => "Wet",
        6 or 7 => abbrev ? "V.Wet" : "Very Wet",
        _ => "",
    };

    private static string FmtClock(double s)
    {
        var ts = TimeSpan.FromSeconds(s);
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }

    /// <summary>Compact session length: "2h", "1:30", "45m".</summary>
    private static string FmtTotal(double s)
    {
        var ts = TimeSpan.FromSeconds(s);
        if (ts.TotalHours >= 1)
            return ts.Minutes == 0 && ts.Seconds == 0 ? $"{(int)ts.TotalHours}h" : ts.ToString(@"h\:mm");
        return ts.Seconds == 0 ? $"{ts.Minutes}m" : ts.ToString(@"m\:ss");
    }

    internal static string FmtIr(int ir) =>
        ir >= 1000 ? (ir / 1000.0).ToString("0.0") + "k" : ir > 1 ? ir.ToString() : "";

    internal static string FmtGap(double s, int precision)
    {
        if (s >= 60)
        {
            var secFmt = "00" + (precision > 0 ? "." + new string('0', precision) : "");
            return $"{(int)(s / 60)}:{(s % 60).ToString(secFmt)}";
        }
        return s.ToString("F" + precision);
    }

    internal static string FmtLap(double s, int precision)
    {
        if (s >= 60)
        {
            var secFmt = "00" + (precision > 0 ? "." + new string('0', precision) : "");
            return $"{(int)(s / 60)}:{(s % 60).ToString(secFmt)}";
        }
        return s.ToString("F" + precision);
    }
}
