using System.Globalization;
using System.IO;
using System.Text.Json;

namespace StandingsOverlay.Data;

/// <summary>Conditions a reference lap was recorded in (from an .ibt's embedded session YAML,
/// or our own session at save time). Everything optional-ish: NaN/-1/"" = unknown.</summary>
public sealed record RefConditions(
    int TrackId,
    string TrackConfig,
    string TrackName,
    string CarPath,
    string CarName,
    float TrackTempC,
    float AirTempC,
    float WindVelMs,
    int TrackWetness,      // irsdk_TrackWetness: 1 dry … 7; -1 unknown
    string RubberState,
    string RecordedUtc);

/// <summary>
/// A reference lap: time (and speed, for the phase-3 corner work) at every 0.1% of the lap,
/// plus the conditions it was set in. Sector times are DERIVED at the live session's
/// boundaries (<see cref="SectorsFor"/>), so a lap recorded under different SplitTimeInfo —
/// or our own grid-based save — always scores against today's official sectors.
/// </summary>
public sealed class LapRef
{
    public const int GridSize = 1000;

    public string Source { get; set; } = "";       // "file" | "prev"
    public string Label { get; set; } = "";        // short display name for logs
    public double LapTime { get; set; }
    public float[] TimeAtPct { get; set; } = [];   // seconds since lap start at pct k/GridSize
    public float[] SpeedAtPct { get; set; } = [];  // m/s; empty when the source had no speed
    public float[] BrakeAtPct { get; set; } = [];  // 0..1 pedal grids — preferred input for
    public float[] ThrottleAtPct { get; set; } = []; // turn-zone segmentation; may be empty
    public RefConditions? Conditions { get; set; }

    /// <summary>Time since lap start at a lap fraction, linearly interpolated on the grid.</summary>
    public double TimeAt(double pct) => TimeAtOf(TimeAtPct, LapTime, pct);

    /// <summary>Sector times at the given boundary pcts (ascending, [0] == 0).</summary>
    public double[] SectorsFor(float[] bounds) => SplitsOf(TimeAtPct, LapTime, bounds);

    /// <summary>Static grid interpolation, shared with own-lap grids (SectorLap.TimeAtPct).</summary>
    public static double TimeAtOf(float[] grid, double lapTime, double pct)
    {
        if (grid.Length == 0) return double.NaN;
        pct = Math.Clamp(pct, 0, 1);
        double x = pct * GridSize;
        int k = (int)x;
        if (k >= grid.Length - 1)
        {
            // Above the last bin: interpolate toward the known lap total at pct = 1.
            double last = grid[^1];
            double span = 1.0 - (double)(grid.Length - 1) / GridSize;
            double frac = span <= 0 ? 0 : (pct - (double)(grid.Length - 1) / GridSize) / span;
            return last + (lapTime - last) * Math.Clamp(frac, 0, 1);
        }
        return grid[k] + (grid[k + 1] - grid[k]) * (x - k);
    }

    /// <summary>Split times of any recorded lap grid at arbitrary boundaries.</summary>
    public static double[] SplitsOf(float[] grid, double lapTime, float[] bounds)
    {
        var s = new double[bounds.Length];
        for (int i = 0; i < bounds.Length; i++)
        {
            double start = TimeAtOf(grid, lapTime, bounds[i]);
            double end = i < bounds.Length - 1 ? TimeAtOf(grid, lapTime, bounds[i + 1]) : lapTime;
            s[i] = end - start;
        }
        return s;
    }

    // NaN is a legitimate "unknown" in conditions (e.g. air temp when saving our own laps) —
    // System.Text.Json refuses it unless told to write the named literal.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    }

    public static LapRef? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var r = JsonSerializer.Deserialize<LapRef>(File.ReadAllText(path), JsonOpts);
            return r is { LapTime: > 0, TimeAtPct.Length: > 0 } ? r : null;
        }
        catch { return null; }
    }
}

/// <summary>The conditions guard: is this reference even valid right now? Severity:
/// 2 = block (deltas vs a different track/car are noise, the ref is refused),
/// 1 = warn (amber chip, deltas still shown), 0 = info (grey chip), -1 = nothing.</summary>
public static class RefGuard
{
    public static (int Severity, string Chip) Diff(RefConditions? r, Roster roster, RawTick t, double warnTempDelta)
    {
        if (r is null) return (-1, "");

        if (r.TrackId > 0 && roster.TrackId > 0 &&
            (r.TrackId != roster.TrackId ||
             !string.Equals(r.TrackConfig, roster.TrackConfig, StringComparison.OrdinalIgnoreCase)))
            return (2, "≠ track");
        if (r.CarPath.Length > 0 && roster.PlayerCarPath.Length > 0 &&
            !string.Equals(r.CarPath, roster.PlayerCarPath, StringComparison.OrdinalIgnoreCase))
            return (2, "≠ car");

        bool refWet = r.TrackWetness > 1;
        bool liveWet = t.TrackWetness > 1;
        if (r.TrackWetness > 0 && t.TrackWetness > 0 && refWet != liveWet)
            return (1, refWet ? "ref was wet" : "ref was dry");
        if (!float.IsNaN(r.TrackTempC) && !float.IsNaN(t.TrackTemp) &&
            Math.Abs(t.TrackTemp - r.TrackTempC) > warnTempDelta)
            return (1, $"track {t.TrackTemp - r.TrackTempC:+0;−0}°C vs ref");

        if (!float.IsNaN(r.WindVelMs) && !float.IsNaN(t.WindVel) &&
            Math.Abs(t.WindVel - r.WindVelMs) > 4)
            return (0, "wind differs");
        if (r.RubberState.Length > 0 && roster.RubberState.Length > 0 &&
            !string.Equals(r.RubberState, roster.RubberState, StringComparison.OrdinalIgnoreCase))
            return (0, "rubber differs");

        return (-1, "");
    }

    /// <summary>"39.79 C" / "1.34 m/s" → 39.79 (iRacing YAML numbers carry a unit suffix).</summary>
    public static float ParseUnit(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return float.NaN;
        var tok = s.Trim().Split(' ')[0];
        return float.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : float.NaN;
    }
}
