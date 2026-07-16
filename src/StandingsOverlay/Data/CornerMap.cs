namespace StandingsOverlay.Data;

/// <summary>
/// Derives turn-zone boundaries from a reference lap's speed-at-pct grid — iRacing has no
/// corner channel, but every braking zone leaves a speed minimum. Apexes = smoothed local
/// minima with a real drop vs their neighborhood; apexes closer than ~1.5% of a lap merge
/// into one zone (esses/chicanes count as ONE numbered T); boundaries sit at the fastest
/// point between consecutive apexes, so a zone = braking + corner + exit — the span a driver
/// actually works on. Numbering is sequential from S/F, which tracks the official map until
/// complexes merge. Spec: docs/LAP-LAB.md.
/// </summary>
public static class CornerMap
{
    private const int SmoothBins = 15;          // ±1.5% moving average
    private const int MergeBins = 15;           // apexes closer than this become one zone
    private const float MinDropFrac = 0.12f;    // apex must sit ≥12% below its gate maxima
    public const int MinZones = 4;              // outside this range the detection is
    public const int MaxZones = 24;             // untrustworthy → caller falls back to sectors

    /// <summary>Zone start pcts (ascending, [0] == 0), or [] when the trace doesn't segment
    /// cleanly (missing speed, oval-flat, detection out of range).</summary>
    public static float[] FromSpeed(float[] speedAtPct)
    {
        int n = speedAtPct.Length;
        if (n < 100) return [];

        // Circular moving average — the lap wraps, so the window does too.
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float sum = 0;
            for (int w = -SmoothBins; w <= SmoothBins; w++)
                sum += speedAtPct[(i + w + n) % n];
            s[i] = sum / (2 * SmoothBins + 1);
        }

        // Local minima, then keep only those with a genuine drop vs the maxima that gate them.
        var minima = new List<int>();
        for (int i = 0; i < n; i++)
        {
            float prev = s[(i - 1 + n) % n], next = s[(i + 1) % n];
            if (s[i] <= prev && s[i] < next) minima.Add(i);
        }
        if (minima.Count == 0) return [];

        var apexes = new List<int>();
        foreach (int m in minima)
        {
            if (s[m] < Gate(s, m) * (1 - MinDropFrac)) apexes.Add(m);
        }

        // Merge close apexes (keep the slowest of each cluster).
        apexes.Sort();
        var zones = new List<int>();
        foreach (int a in apexes)
        {
            if (zones.Count > 0 && a - zones[^1] < MergeBins)
            {
                if (s[a] < s[zones[^1]]) zones[^1] = a;
            }
            else zones.Add(a);
        }
        // Wrap seam: first and last apex can be the same complex straddling S/F.
        if (zones.Count >= 2 && zones[0] + n - zones[^1] < MergeBins) zones.RemoveAt(zones.Count - 1);

        if (zones.Count < MinZones || zones.Count > MaxZones) return [];

        // Boundaries: S/F, then the fastest bin between each consecutive apex pair.
        var bounds = new List<float> { 0f };
        for (int z = 0; z < zones.Count - 1; z++)
        {
            int from = zones[z], to = zones[z + 1], best = from;
            for (int i = from; i <= to; i++)
                if (s[i] > s[best]) best = i;
            bounds.Add((float)best / n);
        }
        return bounds.Where((b, i) => i == 0 || b > bounds[i - 1] + 0.001f).ToArray();
    }

    /// <summary>Prominence gate of a minimum: walking each way, the highest speed reached
    /// before descending below the minimum again — i.e. what the car accelerates back to.
    /// The LOWER of the two sides is the honest measure of how much of a corner this is.</summary>
    private static float Gate(float[] s, int m)
    {
        int n = s.Length;
        float left = s[m], right = s[m];
        for (int i = 1; i < n; i++)
        {
            float v = s[(m - i + n) % n];
            if (v < s[m]) break;
            if (v > left) left = v;
        }
        for (int i = 1; i < n; i++)
        {
            float v = s[(m + i) % n];
            if (v < s[m]) break;
            if (v > right) right = v;
        }
        return Math.Min(left, right);
    }
}
