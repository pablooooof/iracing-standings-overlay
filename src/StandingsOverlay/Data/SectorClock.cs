using System.Text.RegularExpressions;

namespace StandingsOverlay.Data;

/// <summary>Why a completed lap doesn't count as clean (worst wins, Tow > Reset > Pit > Off).</summary>
public enum LapDirt { Clean = 0, Off = 1, Pit = 2, Reset = 3, Tow = 4 }

/// <summary>One completed player lap, timed by <see cref="SectorClock"/> from S/F to S/F.</summary>
public sealed record SectorLap(
    int Number,             // our own 1-based count of fully timed laps this session
    double LapTime,
    double[] Sectors,       // seconds per official sector; NaN = boundary never crossed
    bool[] SectorDirty,     // off-track / pit road touched that sector
    LapDirt Dirt,
    int DirtSector);        // first dirty sector (0-based) for the reason chip; -1

/// <summary>
/// Times the player's laps and official sectors from LapDistPct boundary crossings. iRacing
/// broadcasts nobody's sector times, but the player's are exact when sampled at the SDK's full
/// 60 Hz: the crossing instant is linearly interpolated between the two straddling samples
/// (~±2 ms). Feed <see cref="Sample"/> every frame; a lap only counts once it starts AND ends
/// with an S/F crossing, so join laps and teleports (tow, active reset) never produce garbage
/// rows — they just disarm the clock until the next S/F. Lap Lab spec: docs/LAP-LAB.md.
/// </summary>
public sealed class SectorClock
{
    /// <summary>Backward LapDistPct jump (not an S/F wrap) treated as a teleport — iRacing's
    /// active reset or an ESC-to-pits. A spin rolling backward moves far less than 2% of a lap.</summary>
    private const float TeleportPct = 0.02f;

    private float[] _bounds = [];       // sector start pcts, ascending, [0] == 0 (S/F)
    private double[] _crossT = [];      // session time each boundary was crossed this lap
    private bool[] _sectorDirty = [];

    private readonly List<SectorLap> _done = [];
    private bool _armed;                // lap started with a real S/F crossing
    private int _curSector;
    private LapDirt _dirt;
    private int _lapCount;

    private float _prevPct = -1;
    private double _prevTime;

    /// <summary>Teleport-style abandons seen this session (active reset / repositioning).</summary>
    public int ResetCount { get; private set; }
    /// <summary>Laps abandoned because the car left the world (tow, garage).</summary>
    public int TowCount { get; private set; }

    public bool HasBoundaries => _bounds.Length >= 2;
    public int SectorCount => _bounds.Length;

    /// <summary>Install the track's sector boundaries. Returns true when they changed
    /// (which resets all lap state — old crossings mean nothing on new boundaries).</summary>
    public bool SetBoundaries(IReadOnlyList<float> startPcts)
    {
        var b = startPcts.Where(p => p is >= 0 and < 1).Distinct().OrderBy(p => p).ToArray();
        if (b.Length > 0 && b[0] > 0.0001f) b = [0f, .. b];   // S/F is always a boundary
        if (b.Length == _bounds.Length && !b.Where((p, i) => Math.Abs(p - _bounds[i]) > 0.0001f).Any())
            return false;
        _bounds = b;
        _crossT = new double[b.Length];
        _sectorDirty = new bool[b.Length];
        Reset();
        return true;
    }

    public void Reset()
    {
        _done.Clear();
        _armed = false;
        _lapCount = 0;
        _prevPct = -1;
        ResetCount = 0;
        TowCount = 0;
    }

    /// <summary>Append completed laps (oldest first) to <paramref name="into"/>; returns how many.</summary>
    public int DrainInto(List<SectorLap> into)
    {
        if (_done.Count == 0) return 0;
        into.AddRange(_done);
        int n = _done.Count;
        _done.Clear();
        return n;
    }

    /// <summary>One telemetry sample of the player. <paramref name="surface"/> is
    /// irsdk_TrkLoc: -1 not in world · 0 off track · 1 pit stall · 2 approaching pits · 3 on track.</summary>
    public void Sample(float pct, double time, int surface, bool onPitRoad)
    {
        if (_bounds.Length < 2) return;

        if (surface < 0 || pct < 0)     // left the world: tow / garage / spectate
        {
            if (_armed) { _armed = false; TowCount++; }
            _prevPct = -1;
            return;
        }

        if (_prevPct < 0) { _prevPct = pct; _prevTime = time; return; }

        double dt = time - _prevTime;
        if (dt <= 0) { _prevPct = pct; _prevTime = time; return; }   // paused / clock resync

        float d = pct - _prevPct;
        bool wrapped = d < -0.5f;                 // forward across S/F
        if (d > 0.5f || (!wrapped && d < -TeleportPct))
        {
            // Teleported: active reset, reposition, or reversing across S/F. The in-progress
            // lap can never complete honestly — disarm until the next real S/F crossing.
            if (_armed) { _armed = false; ResetCount++; }
            _prevPct = pct;
            _prevTime = time;
            return;
        }

        if (_armed)
        {
            bool off = surface == 0;
            bool pit = onPitRoad || surface is 1 or 2;
            if (off || pit)
            {
                _sectorDirty[_curSector] = true;
                var kind = pit ? LapDirt.Pit : LapDirt.Off;
                if (kind > _dirt) _dirt = kind;
            }
        }

        if (d > 0 || wrapped)
        {
            double span = wrapped ? 1 - _prevPct + pct : d;
            // Boundaries in (prevPct, 1) first, then — if we wrapped — [0, pct] in order.
            for (int bi = 1; bi < _bounds.Length; bi++)
            {
                if (_bounds[bi] <= _prevPct) continue;
                double at = _bounds[bi] - _prevPct;
                if (at > span) break;
                Cross(bi, _prevTime + dt * at / span);
            }
            if (wrapped)
            {
                Cross(0, _prevTime + dt * (1 - _prevPct) / span);
                for (int bi = 1; bi < _bounds.Length; bi++)
                {
                    double at = 1 - _prevPct + _bounds[bi];
                    if (at > span) break;
                    Cross(bi, _prevTime + dt * at / span);
                }
            }
        }

        _prevPct = pct;
        _prevTime = time;
    }

    private void Cross(int boundary, double t)
    {
        if (boundary > 0)
        {
            if (!_armed) return;
            _crossT[boundary] = t;
            _curSector = boundary;
            return;
        }

        // S/F: close the lap in progress, then start the next one at this exact instant.
        if (_armed)
        {
            int n = _bounds.Length;
            var sectors = new double[n];
            bool gap = false;
            for (int i = 0; i < n; i++)
            {
                double start = _crossT[i], end = i < n - 1 ? _crossT[i + 1] : t;
                sectors[i] = start > 0 && end > start ? end - start : double.NaN;
                gap |= double.IsNaN(sectors[i]);
            }
            var dirt = gap && _dirt < LapDirt.Reset ? LapDirt.Reset : _dirt;
            int dirtSector = Array.IndexOf(_sectorDirty, true);
            _done.Add(new SectorLap(++_lapCount, t - _crossT[0], sectors,
                                    (bool[])_sectorDirty.Clone(), dirt, dirtSector));
        }

        _armed = true;
        _curSector = 0;
        _dirt = LapDirt.Clean;
        Array.Clear(_crossT);
        Array.Clear(_sectorDirty);
        _crossT[0] = t;
    }

    /// <summary>Extract SplitTimeInfo sector boundaries from the raw session YAML (irsdkSharp's
    /// session model doesn't map that section). Returns [] when absent/unparsable.</summary>
    public static float[] ParseBoundaries(string yaml)
    {
        var section = Regex.Match(yaml, @"SplitTimeInfo:.*?(?=\r?\n\S|\z)", RegexOptions.Singleline);
        if (!section.Success) return [];
        var pcts = Regex.Matches(section.Value, @"SectorStartPct:\s*([0-9.eE+-]+)")
            .Select(m => float.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : float.NaN)
            .Where(f => !float.IsNaN(f))
            .ToArray();
        return pcts;
    }
}
