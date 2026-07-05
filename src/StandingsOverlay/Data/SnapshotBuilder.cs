using StandingsOverlay.Config;

namespace StandingsOverlay.Data;

/// <summary>
/// Turns a raw telemetry tick + roster into the display model. Pure function of its inputs,
/// shared by the live iRacing source and the demo source so both exercise the same logic.
/// </summary>
public static class SnapshotBuilder
{
    public static StandingsSnapshot Build(RawTick t, Roster roster, GapHistory history, OverlayConfig cfg)
    {
        bool isRace = t.SessionType.Contains("Race", StringComparison.OrdinalIgnoreCase);

        var cars = roster.Drivers.Values
            .Where(d => !d.IsPaceCar && !d.IsSpectator && t.Has(d.CarIdx))
            .ToList();

        List<DriverEntry> ordered;
        if (isRace)
        {
            ordered = cars.Where(d => t.Position[d.CarIdx] > 0)
                          .OrderBy(d => t.Position[d.CarIdx])
                          .ToList();
        }
        else
        {
            // Practice/qual: iRacing often leaves CarIdxPosition at 0, so order by best lap.
            ordered = cars.Where(d => t.BestLap[d.CarIdx] > 0)
                          .OrderBy(d => t.BestLap[d.CarIdx])
                          .Concat(cars.Where(d => t.BestLap[d.CarIdx] <= 0).OrderBy(d => d.CarNumber, StringComparer.Ordinal))
                          .ToList();
        }

        if (ordered.Count == 0)
            return new StandingsSnapshot(true, Header(t, isRace), Sof(roster), Clock(t, isRace), []);

        int playerPos = ordered.FindIndex(d => d.CarIdx == t.PlayerCarIdx);

        // Compact selection: top N + window around the player, separator where non-contiguous.
        var picked = new SortedSet<int>();
        for (int i = 0; i < Math.Min(cfg.DriversAtTop, ordered.Count); i++) picked.Add(i);
        if (playerPos >= 0)
            for (int i = Math.Max(0, playerPos - cfg.DriversAheadBehind);
                 i <= Math.Min(ordered.Count - 1, playerPos + cfg.DriversAheadBehind); i++)
                picked.Add(i);

        float sessionBest = ordered.Where(d => t.BestLap[d.CarIdx] > 0)
                                   .Select(d => t.BestLap[d.CarIdx])
                                   .DefaultIfEmpty(0).Min();

        var rows = new List<StandingsRow>(picked.Count + 1);
        int prev = -1;
        foreach (int i in picked)
        {
            if (prev >= 0 && i != prev + 1) rows.Add(StandingsRow.Separator);
            prev = i;
            rows.Add(BuildRow(i, ordered, t, history, cfg, isRace, sessionBest));
        }

        return new StandingsSnapshot(true, Header(t, isRace), Sof(roster), Clock(t, isRace), rows);
    }

    private static StandingsRow BuildRow(int i, List<DriverEntry> ordered, RawTick t,
        GapHistory history, OverlayConfig cfg, bool isRace, float sessionBest)
    {
        var d = ordered[i];
        int idx = d.CarIdx;
        var leader = ordered[0];

        string gap = "", interval = "";
        if (isRace)
        {
            if (i > 0)
            {
                int lapsDown = t.Lap[leader.CarIdx] - t.Lap[idx];
                double leaderTotal = t.Lap[leader.CarIdx] + t.LapDistPct[leader.CarIdx];
                double carTotal = t.Lap[idx] + t.LapDistPct[idx];
                if (leaderTotal - carTotal >= 1.0)
                    gap = $"+{lapsDown}L";
                else
                    gap = "+" + FmtGap(Math.Max(0, t.F2Time[idx] - t.F2Time[leader.CarIdx]));

                var ahead = ordered[i - 1];
                double aheadTotal = t.Lap[ahead.CarIdx] + t.LapDistPct[ahead.CarIdx];
                interval = carTotal <= aheadTotal - 1.0
                    ? $"+{(int)(aheadTotal - carTotal)}L"
                    : "+" + FmtGap(Math.Max(0, t.F2Time[idx] - t.F2Time[ahead.CarIdx]));
            }
        }
        else
        {
            float best = t.BestLap[idx];
            if (best > 0 && sessionBest > 0 && i > 0)
                gap = "+" + FmtGap(best - sessionBest);
            if (best > 0 && i > 0 && t.BestLap[ordered[i - 1].CarIdx] > 0)
                interval = "+" + FmtGap(best - t.BestLap[ordered[i - 1].CarIdx]);
        }

        string deltaText = "";
        int deltaSign = 0;
        if (idx != t.PlayerCarIdx)
        {
            var delta = history.DeltaOver(idx, cfg.DeltaLaps);
            if (delta is not null)
            {
                // Negative = player gained on this car over the window = green.
                deltaSign = Math.Abs(delta.Value) < 0.05f ? 0 : Math.Sign(delta.Value);
                deltaText = (delta.Value <= -0.05f ? "▼" : delta.Value >= 0.05f ? "▲" : "") +
                            Math.Abs(delta.Value).ToString("0.0");
            }
        }

        float last = t.LastLap[idx];

        return new StandingsRow(
            Position: i + 1,
            CarNumber: d.CarNumber,
            Name: d.Name,
            IRatingText: FmtIr(d.IRating),
            LicText: d.LicString,
            LicColor: d.LicColor,
            ClassColor: d.ClassColor,
            GapText: gap,
            IntervalText: interval,
            LastLapText: last > 0 ? FmtLap(last) : "",
            DeltaText: deltaText,
            DeltaSign: deltaSign,
            IsPlayer: idx == t.PlayerCarIdx,
            InPit: t.OnPitRoad[idx],
            IsSeparator: false);
    }

    private static string Header(RawTick t, bool isRace) =>
        isRace ? "RACE" : t.SessionType.ToUpperInvariant();

    private static string Sof(Roster roster) =>
        roster.StrengthOfField > 0 ? $"SoF {FmtIr((int)roster.StrengthOfField)}" : "";

    private static string Clock(RawTick t, bool isRace)
    {
        if (isRace && t.SessionLapsRemain >= 0)
            return $"{t.SessionLapsRemain} laps";
        if (t.SessionTimeRemain >= 0 && t.SessionTimeRemain < 172800)
        {
            var ts = TimeSpan.FromSeconds(t.SessionTimeRemain);
            return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
        }
        return "";
    }

    internal static string FmtIr(int ir) =>
        ir >= 1000 ? (ir / 1000.0).ToString("0.0") + "k" : ir > 0 ? ir.ToString() : "";

    internal static string FmtGap(double s) =>
        s >= 60 ? $"{(int)(s / 60)}:{s % 60:00.0}" : s.ToString("0.0");

    internal static string FmtLap(double s) =>
        s >= 60 ? $"{(int)(s / 60)}:{s % 60:00.000}" : s.ToString("0.000");
}
