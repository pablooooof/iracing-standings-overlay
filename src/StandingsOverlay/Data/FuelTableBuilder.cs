using StandingsOverlay.Config;

namespace StandingsOverlay.Data;

/// <summary>One row of the consumption table: a label, its L/lap figure and the laps that figure
/// leaves in the current tank. <see cref="PerLap"/> is -1 when there's no data yet.</summary>
public readonly record struct FuelTableRow(string Label, double PerLap, double LapsToEmpty, bool IsTarget)
{
    public bool VisuallyEquals(FuelTableRow o) =>
        Label == o.Label && IsTarget == o.IsTarget &&
        Near(PerLap, o.PerLap) && Near(LapsToEmpty, o.LapsToEmpty);

    private static bool Near(double a, double b) =>
        (a < 0 && b < 0) || Math.Abs(a - b) < 0.005;
}

/// <summary>
/// The fuel consumption table: a header (current/usable fuel + a laps-remaining range), rows for
/// the last lap, last-5/last-10 rolling averages, the current stint and a driver-set target, plus
/// a target-vs-actual "ahead / behind" tracker for the stint in progress. Player-only (iRacing
/// exposes fuel for the player's car only) and works in both practice and race.
/// </summary>
public sealed record FuelTableSnapshot(
    bool Show,
    string TankText,        // "100.5 / 100.9 L"
    double FuelFrac,        // 0..1 for the E—F gauge
    string RemText,         // "28.6 – 29.7" laps left on current fuel (best/worst recent burn)
    IReadOnlyList<FuelTableRow> Rows,
    int TargetLapsGoal,     // derived stint-length goal (laps), -1 if no target set — seeds the spinner
    bool HasStatus,
    string StatusText,      // "Lap 10 / 29 · 0.4L ahead · need 3.58/lap for 19 to go"
    int StatusState)        // 0 neutral · 1 ahead (green) · 2 behind (red)
{
    public static readonly FuelTableSnapshot Empty =
        new(false, "", 0, "", [], -1, false, "", 0);

    public bool VisuallyEquals(FuelTableSnapshot? o)
    {
        if (o is null) return false;
        if (Show != o.Show || TankText != o.TankText || RemText != o.RemText ||
            TargetLapsGoal != o.TargetLapsGoal ||
            HasStatus != o.HasStatus || StatusText != o.StatusText || StatusState != o.StatusState ||
            Math.Abs(FuelFrac - o.FuelFrac) > 0.005 || Rows.Count != o.Rows.Count) return false;
        for (int i = 0; i < Rows.Count; i++)
            if (!Rows[i].VisuallyEquals(o.Rows[i])) return false;
        return true;
    }
}

/// <summary>Builds <see cref="FuelTableSnapshot"/> from the live tick and the player fuel model.
/// Pure — the target interlock (laps ↔ litres/lap, tied by the usable tank) is derived here from
/// the live tank so settings can store just the driver's intent. Spec: docs/FUEL-STRATEGY.md.</summary>
public static class FuelTableBuilder
{
    public static FuelTableSnapshot Build(RawTick t, FuelModel fuel, OverlayConfig cfg)
    {
        var fc = cfg.FuelTable;
        int p = t.PlayerCarIdx;
        if (!fc.Enabled || !t.Has(p) || float.IsNaN(t.PlayerFuelLevel)) return FuelTableSnapshot.Empty;

        double fuelNow = t.PlayerFuelLevel;
        double tank = t.TankCapacity;
        string tankText = tank > 1 ? $"{fuelNow:0.0} / {tank:0.0} L" : $"{fuelNow:0.0} L";
        double fuelFrac = tank > 1 ? Math.Clamp(fuelNow / tank, 0, 1) : 0;

        // Laps-remaining range: fuel over the thirstiest and thriftiest of the recent laps.
        string remText = "";
        var recent = fuel.RecentUsage;
        if (recent.Count > 0 && fuelNow > 0.01)
        {
            int take = Math.Min(10, recent.Count);
            double lo = double.MaxValue, hi = double.MinValue;
            for (int i = recent.Count - take; i < recent.Count; i++)
            {
                lo = Math.Min(lo, recent[i]);
                hi = Math.Max(hi, recent[i]);
            }
            if (hi > 0.01)
            {
                double worst = fuelNow / hi, best = fuelNow / lo;
                remText = Math.Abs(best - worst) < 0.05 ? $"{best:0.0}" : $"{worst:0.0} – {best:0.0}";
            }
        }

        // Target interlock: whichever number the driver set last (TargetMode) is master; the other
        // falls out of the usable tank. Falls back to whichever single value is set.
        double targetPerLap = -1, targetLaps = -1;
        bool perLapMaster = fc.TargetMode.Equals("PerLap", StringComparison.OrdinalIgnoreCase)
                            && fc.TargetPerLap > 0.01;
        if (perLapMaster)
        {
            targetPerLap = fc.TargetPerLap;
            targetLaps = tank > 1 ? tank / targetPerLap : fc.TargetLaps > 0 ? fc.TargetLaps : -1;
        }
        else if (fc.TargetLaps > 0.5)
        {
            targetLaps = fc.TargetLaps;
            targetPerLap = tank > 1 ? tank / targetLaps : fc.TargetPerLap > 0 ? fc.TargetPerLap : -1;
        }
        else if (fc.TargetPerLap > 0.01)   // mode says Laps but only the per-lap value is set
        {
            targetPerLap = fc.TargetPerLap;
            targetLaps = tank > 1 ? tank / targetPerLap : -1;
        }

        var rows = new List<FuelTableRow>(5);
        rows.Add(Row("Last", fuel.LastPerLap, fuelNow));
        if (fc.ShowLast5) rows.Add(Row("Last 5", fuel.RecentAvg(5), fuelNow));
        if (fc.ShowLast10) rows.Add(Row("Last 10", fuel.RecentAvg(10), fuelNow));
        if (fc.ShowStint) rows.Add(Row("Stint", fuel.StintPerLap, fuelNow));
        rows.Add(new FuelTableRow("Target", targetPerLap,
            targetPerLap > 0.01 && fuelNow > 0.01 ? fuelNow / targetPerLap : -1, IsTarget: true));

        int targetLapsGoal = targetLaps >= 1 ? (int)Math.Round(targetLaps) : -1;

        // Target-vs-actual for the stint in progress: are we banking or overspending, and what
        // average burn now makes the target stint length on the fuel we have.
        bool hasStatus = false; string statusText = ""; int statusState = 0;
        int stintLaps = fuel.StintLaps;
        if (fc.ShowStatus && targetPerLap > 0.01 && targetLapsGoal >= 1 && stintLaps >= 1)
        {
            int tgtLaps = targetLapsGoal;
            int lapsLeft = tgtLaps - stintLaps;
            double plannedUsed = stintLaps * targetPerLap;
            double actualUsed = fuel.StintPerLap * stintLaps;
            double delta = plannedUsed - actualUsed;          // + = used less than plan = ahead
            statusState = delta >= -0.0005 ? 1 : 2;
            string word = delta >= 0 ? "ahead" : "behind";
            if (lapsLeft > 0)
            {
                double need = fuelNow / lapsLeft;             // burn this to make the stint on current fuel
                statusText = $"Lap {stintLaps} / {tgtLaps} · {Math.Abs(delta):0.0}L {word} · " +
                             $"need {need:0.00}/lap for {lapsLeft} to go";
            }
            else
            {
                statusText = $"Lap {stintLaps} / {tgtLaps} · target stint done · {Math.Abs(delta):0.0}L {word}";
            }
            hasStatus = true;
        }

        return new FuelTableSnapshot(true, tankText, fuelFrac, remText, rows,
                                     targetLapsGoal, hasStatus, statusText, statusState);
    }

    private static FuelTableRow Row(string label, double perLap, double fuelNow) =>
        new(label, perLap, perLap > 0.01 && fuelNow > 0.01 ? fuelNow / perLap : -1, IsTarget: false);
}
