using StandingsOverlay.Config;

namespace StandingsOverlay.Data;

/// <summary>
/// Turns a raw telemetry tick + roster into the display model. Pure function of its inputs,
/// shared by the live iRacing source and the demo source so both exercise the same logic.
/// </summary>
public static class SnapshotBuilder
{
    public static StandingsSnapshot Build(RawTick t, Roster roster, GapHistory history,
        StintTracker stints, OverlayConfig cfg)
    {
        bool isRace = t.SessionType.Contains("Race", StringComparison.OrdinalIgnoreCase);

        var cars = roster.Drivers.Values
            .Where(d => !d.IsPaceCar && !d.IsSpectator && t.Has(d.CarIdx))
            .ToList();

        // Group into classes, fastest class first. Single-class fields collapse to one group.
        var classes = cars.GroupBy(d => d.CarClassId)
                          .OrderBy(g => g.Min(d => d.ClassEstLap > 0 ? d.ClassEstLap : float.MaxValue))
                          .ToList();
        bool multiclass = classes.Count > 1;

        int playerClassId = roster.Drivers.TryGetValue(t.PlayerCarIdx, out var me) ? me.CarClassId
                            : classes.Count > 0 ? classes[0].Key : 0;

        double lapsRemain = EstimateLapsRemain(t, roster);

        var rows = new List<StandingsRow>(24);
        foreach (var cls in classes)
        {
            bool isPlayerClass = cls.Key == playerClassId;
            if (multiclass && !isPlayerClass && cfg.OtherClassesDriversAtTop <= 0) continue;

            var ordered = OrderClass(cls.ToList(), t, isRace);
            if (ordered.Count == 0) continue;

            // Which indices of this class to show.
            var picked = new SortedSet<int>();
            if (isPlayerClass)
            {
                for (int i = 0; i < Math.Min(cfg.DriversAtTop, ordered.Count); i++) picked.Add(i);
                int playerPos = ordered.FindIndex(d => d.CarIdx == t.PlayerCarIdx);
                if (playerPos >= 0)
                    for (int i = Math.Max(0, playerPos - cfg.DriversAheadBehind);
                         i <= Math.Min(ordered.Count - 1, playerPos + cfg.DriversAheadBehind); i++)
                        picked.Add(i);
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

            float classBest = ordered.Where(d => t.BestLap[d.CarIdx] > 0)
                                     .Select(d => t.BestLap[d.CarIdx])
                                     .DefaultIfEmpty(0).Min();
            float? classMedianPace = MedianRecentPace(ordered, stints);

            int prev = -1;
            foreach (int i in picked)
            {
                if (prev >= 0 && i != prev + 1) rows.Add(StandingsRow.Separator);
                prev = i;
                rows.Add(BuildRow(i, ordered, t, history, stints, cfg, isRace, classBest, classMedianPace, lapsRemain));
            }
        }

        return new StandingsSnapshot(true, isRace ? "RACE" : t.SessionType.ToUpperInvariant(),
            HeaderMid(t, roster, cfg), HeaderRight(t, isRace, cfg), rows);
    }

    private static List<DriverEntry> OrderClass(List<DriverEntry> cars, RawTick t, bool isRace)
    {
        if (isRace)
            return cars.Where(d => t.Position[d.CarIdx] > 0)
                       .OrderBy(d => t.Position[d.CarIdx])
                       .ToList();

        // Practice/qual: iRacing often leaves CarIdxPosition at 0, so order by best lap.
        return cars.Where(d => t.BestLap[d.CarIdx] > 0)
                   .OrderBy(d => t.BestLap[d.CarIdx])
                   .Concat(cars.Where(d => t.BestLap[d.CarIdx] <= 0)
                               .OrderBy(d => d.CarNumber, StringComparer.Ordinal))
                   .ToList();
    }

    private static StandingsRow BuildRow(int i, List<DriverEntry> ordered, RawTick t,
        GapHistory history, StintTracker stints, OverlayConfig cfg, bool isRace,
        float classBest, float? classMedianPace, double lapsRemain)
    {
        var d = ordered[i];
        int idx = d.CarIdx;
        var leader = ordered[0];

        string gap = "", interval = "";
        if (isRace)
        {
            if (i > 0)
            {
                gap = GapToCar(t, idx, leader.CarIdx, cfg.GapPrecision);
                interval = GapToCar(t, idx, ordered[i - 1].CarIdx, cfg.IntervalPrecision);
            }
        }
        else
        {
            float best = t.BestLap[idx];
            if (best > 0 && classBest > 0 && i > 0)
                gap = "+" + FmtGap(best - classBest, cfg.GapPrecision);
            if (best > 0 && i > 0 && t.BestLap[ordered[i - 1].CarIdx] > 0)
                interval = "+" + FmtGap(best - t.BestLap[ordered[i - 1].CarIdx], cfg.IntervalPrecision);
        }

        var deltaCells = new DeltaCell[cfg.DeltaLaps];
        if (idx != t.PlayerCarIdx)
        {
            var perLap = history.PerLapDeltas(idx, cfg.DeltaLaps);
            for (int k = 0; k < perLap.Length; k++)
            {
                if (perLap[k] is not float dl) { deltaCells[k] = new DeltaCell("", 0); continue; }
                // A swing this big means someone pitted (or crashed) that lap — mark it instead
                // of letting a 35s number swamp the column.
                if (Math.Abs(dl) >= 10f) { deltaCells[k] = new DeltaCell("P", 0); continue; }
                // Color carries the sign: green = player gained on this car during that lap.
                int sign = Math.Abs(dl) < 0.05f ? 0 : Math.Sign(dl);
                deltaCells[k] = new DeltaCell(Math.Abs(dl).ToString("F" + cfg.DeltaPrecision), sign);
            }
        }
        else
        {
            Array.Fill(deltaCells, new DeltaCell("", 0));
        }

        int gained = stints.PositionsGained(idx, t.Position.Length > idx ? t.Position[idx] : 0);

        string pace = "";
        int paceSign = 0;
        if (isRace && stints.RecentPace(idx) is float rp && classMedianPace is float med && med > 0)
        {
            if (rp < med * 0.997f) { pace = "▲"; paceSign = -1; }
            else if (rp > med * 1.005f) { pace = "▼"; paceSign = 1; }
            if (stints.LooksLikeFuelSaving(idx)) pace += "S";
        }

        float last = t.LastLap[idx];
        float bestLap = t.BestLap[idx];

        return new StandingsRow(
            Kind: RowKind.Normal,
            PosText: (i + 1).ToString(),
            PosGainedText: gained == 0 ? "" : gained > 0 ? $"+{gained}" : gained.ToString(),
            PosGainedSign: gained > 0 ? -1 : gained < 0 ? 1 : 0,
            CarNumber: "#" + d.CarNumber,
            Name: d.Name,
            IRatingText: FmtIr(d.IRating),
            LicText: d.LicString,
            LicColor: d.LicColor,
            ClassColor: d.ClassColor,
            GapText: gap,
            IntervalText: interval,
            BestLapText: bestLap > 0 ? FmtLap(bestLap, cfg.LapTimePrecision) : "",
            LastLapText: last > 0 ? FmtLap(last, cfg.LapTimePrecision) : "",
            DeltaCells: deltaCells,
            StatusText: Status(t, idx),
            StratText: stints.StrategyText(idx, t.Lap[idx], lapsRemain),
            PaceText: pace,
            PaceSign: paceSign,
            IsPlayer: idx == t.PlayerCarIdx);
    }

    /// <summary>Race gap between two cars: laps-down when a lap+ apart, else F2Time difference.</summary>
    private static string GapToCar(RawTick t, int idx, int refIdx, int precision)
    {
        double refTotal = t.Lap[refIdx] + t.LapDistPct[refIdx];
        double carTotal = t.Lap[idx] + t.LapDistPct[idx];
        if (refTotal - carTotal >= 1.0)
            return $"+{(int)(refTotal - carTotal)}L";
        return "+" + FmtGap(Math.Max(0, t.F2Time[idx] - t.F2Time[refIdx]), precision);
    }

    /// <summary>Highest-priority per-car status badge.</summary>
    private static string Status(RawTick t, int idx)
    {
        int flags = idx < t.SessionFlags.Length ? t.SessionFlags[idx] : 0;
        if ((flags & CarFlags.Disqualify) != 0) return "DQ";
        if ((flags & CarFlags.Black) != 0) return "BLK";
        if ((flags & CarFlags.Repair) != 0) return "DMG";
        if ((flags & CarFlags.Furled) != 0) return "WRN";
        if (idx < t.OnPitRoad.Length && t.OnPitRoad[idx]) return "PIT";
        return "";
    }

    private static float? MedianRecentPace(List<DriverEntry> ordered, StintTracker stints)
    {
        var paces = ordered.Select(d => stints.RecentPace(d.CarIdx))
                           .Where(p => p is not null).Select(p => p!.Value)
                           .OrderBy(p => p).ToList();
        return paces.Count >= 3 ? paces[paces.Count / 2] : null;
    }

    private static double EstimateLapsRemain(RawTick t, Roster roster)
    {
        if (t.SessionLapsRemain >= 0) return t.SessionLapsRemain;
        if (t.SessionTimeRemain > 0 && t.SessionTimeRemain < 172800 &&
            roster.Drivers.TryGetValue(t.PlayerCarIdx, out var me) && me.ClassEstLap > 10)
            return t.SessionTimeRemain / me.ClassEstLap;
        return -1;
    }

    private static string HeaderMid(RawTick t, Roster roster, OverlayConfig cfg)
    {
        var parts = new List<string>(2);
        if (cfg.ShowSof && roster.StrengthOfField > 25)
            parts.Add($"SoF {FmtIr((int)roster.StrengthOfField)}");
        if (cfg.ShowTrackTemp && !float.IsNaN(t.TrackTemp))
            parts.Add($"{t.TrackTemp:0}°C");
        return string.Join("  ·  ", parts);
    }

    private static string HeaderRight(RawTick t, bool isRace, OverlayConfig cfg)
    {
        var parts = new List<string>(2);
        if (isRace && t.SessionLapsRemain >= 0)
            parts.Add($"{t.SessionLapsRemain} laps");
        else if (t.SessionTimeRemain >= 0 && t.SessionTimeRemain < 172800)
        {
            var ts = TimeSpan.FromSeconds(t.SessionTimeRemain);
            parts.Add(ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss"));
        }
        if (cfg.ShowIncidents && t.PlayerIncidents >= 0)
            parts.Add($"{t.PlayerIncidents}x");
        return string.Join("  ·  ", parts);
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
