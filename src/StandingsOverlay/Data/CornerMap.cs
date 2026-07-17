using System.IO;

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

    private const float BrakeOn = 0.08f;
    private const float ThrottleFull = 0.85f;
    private const int GapCloseBins = 8;     // throttle blips inside a chicane don't split it
    private const int MinRegionBins = 3;    // shorter "cornering" bursts are pedal noise

    /// <summary>Zone boundaries from the pedal traces — the preferred detector. A corner is
    /// what a driver says it is: braking onset → back to full throttle. Flat-out kinks that
    /// never make a speed minimum (Eau Rouge) correctly stay part of their straight, and
    /// chicanes hold together through internal throttle blips (gap closing). Boundaries sit
    /// just before each braking onset, so a zone = braking + corner + full exit straight —
    /// a bad exit bills its whole straight to the zone that caused it.</summary>
    public static float[] FromPedals(float[] brakeAtPct, float[] throttleAtPct)
    {
        int n = Math.Min(brakeAtPct.Length, throttleAtPct.Length);
        if (n < 100) return [];

        var cornering = new bool[n];
        bool any = false, all = true;
        for (int i = 0; i < n; i++)
        {
            cornering[i] = brakeAtPct[i] > BrakeOn || throttleAtPct[i] < ThrottleFull;
            any |= cornering[i];
            all &= cornering[i];
        }
        if (!any || all) return [];

        CloseGaps(cornering, GapCloseBins);

        // Braking onsets (false→true edges, circular), regions under the noise floor dropped.
        var onsets = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (!cornering[i] || cornering[(i - 1 + n) % n]) continue;
            int len = 0;
            while (len < n && cornering[(i + len) % n]) len++;
            if (len >= MinRegionBins) onsets.Add(i);
        }
        if (onsets.Count < MinZones || onsets.Count > MaxZones) return [];

        var bounds = new List<float> { 0f };
        foreach (int o in onsets)
        {
            float b = (float)((o - 2 + n) % n) / n;
            if (b > 0.003f) bounds.Add(b);
        }
        bounds.Sort();
        return bounds.Where((b, i) => i == 0 || b > bounds[i - 1] + 0.003f).ToArray();
    }

    /// <summary>Fill non-cornering gaps of at most <paramref name="maxGap"/> bins (circular).</summary>
    private static void CloseGaps(bool[] c, int maxGap)
    {
        int n = c.Length;
        for (int i = 0; i < n; i++)
        {
            if (c[i] || !c[(i - 1 + n) % n]) continue;   // only gap starts
            int len = 0;
            while (len < n && !c[(i + len) % n]) len++;
            if (len <= maxGap && len < n)
                for (int k = 0; k < len; k++) c[(i + k) % n] = true;
            i += len - 1;
        }
    }

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

    /// <summary>Official corner numbers/names for a track, loaded once per combo from
    /// <c>corners/{trackId}_{config}.json</c> next to the exe. Zone headers then read the
    /// official numbering ("T10", "T18-19" for a merged chicane) instead of sequential
    /// indices that drift once complexes merge. Entirely optional — no file, no change.</summary>
    public sealed class CornerNames
    {
        public sealed record Entry(float Pct, int Num, string Name);
        public List<Entry> Corners { get; set; } = [];

        private static string _key = "";
        private static CornerNames? _cached;

        public static CornerNames? For(Roster roster)
        {
            string key = $"{roster.TrackId}_{roster.TrackConfig}";
            if (key == _key) return _cached;
            _key = key;
            _cached = null;
            try
            {
                string cfg = roster.TrackConfig.Replace(' ', '-').ToLowerInvariant();
                foreach (var bad in Path.GetInvalidFileNameChars()) cfg = cfg.Replace(bad.ToString(), "");
                string path = Path.Combine(AppContext.BaseDirectory, "corners", $"{roster.TrackId}_{cfg}.json");
                if (File.Exists(path))
                {
                    _cached = System.Text.Json.JsonSerializer.Deserialize<CornerNames>(File.ReadAllText(path));
                    if (_cached is not { Corners.Count: > 0 }) _cached = null;
                    else Log.Write($"lap lab: {_cached.Corners.Count} corner names loaded (track {key})");
                }
            }
            catch { _cached = null; }
            return _cached;
        }

        /// <summary>Header for zone i: the official numbers whose pcts fall inside it —
        /// "T10", "T18-19", or "·" for a zone with no corner (straight fragment at S/F).</summary>
        public string ZoneLabel(float[] bounds, int i)
        {
            float from = bounds[i], to = i < bounds.Length - 1 ? bounds[i + 1] : 1f;
            var nums = Corners.Where(c => c.Pct >= from && c.Pct < to)
                              .Select(c => c.Num).OrderBy(x => x).ToList();
            return nums.Count == 0 ? "·"
                 : nums.Count == 1 ? $"T{nums[0]}"
                 : $"T{nums[0]}-{nums[^1]}";
        }

        /// <summary>First corner name inside zone i, for the log map ("T10 Pouhon").</summary>
        public string ZoneName(float[] bounds, int i)
        {
            float from = bounds[i], to = i < bounds.Length - 1 ? bounds[i + 1] : 1f;
            return Corners.Where(c => c.Pct >= from && c.Pct < to)
                          .OrderBy(c => c.Pct).FirstOrDefault()?.Name ?? "";
        }
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
