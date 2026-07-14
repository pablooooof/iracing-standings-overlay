using StandingsOverlay.Config;

namespace StandingsOverlay.Data;

/// <summary>One table cell. Sign: 0 neutral · 1 slower than ref (red) · 2 faster than ref
/// (purple) · 3 dirty (amber) · 4 dim (untimed / not applicable).</summary>
public readonly record struct LapLabCell(string Text, int Sign);

public sealed record LapLabRow(
    string LapText,
    bool IsSessionBest,     // green lap number + time
    IReadOnlyList<LapLabCell> Sectors,
    string TimeText,
    bool TimeDim,           // dirty laps: time shown muted
    LapLabCell Delta)       // lap delta vs ref, or the dirty reason ("off S2" / "pit" / "AR")
{
    public bool VisuallyEquals(LapLabRow o)
    {
        if (LapText != o.LapText || IsSessionBest != o.IsSessionBest || TimeText != o.TimeText ||
            TimeDim != o.TimeDim || Delta != o.Delta || Sectors.Count != o.Sectors.Count) return false;
        for (int i = 0; i < Sectors.Count; i++)
            if (Sectors[i] != o.Sectors[i]) return false;
        return true;
    }
}

public sealed record LapLabSnapshot(
    bool Show,
    string RefText,                       // "ref best 1:59.48" / "no reference yet"
    IReadOnlyList<string> SectorHeaders,  // "S1".."Sn"
    IReadOnlyList<LapLabRow> Rows)        // newest first
{
    public static readonly LapLabSnapshot Empty = new(false, "", [], []);

    public bool VisuallyEquals(LapLabSnapshot? o)
    {
        if (o is null || Show != o.Show || RefText != o.RefText ||
            Rows.Count != o.Rows.Count || !SectorHeaders.SequenceEqual(o.SectorHeaders)) return false;
        for (int i = 0; i < Rows.Count; i++)
            if (!Rows[i].VisuallyEquals(o.Rows[i])) return false;
        return true;
    }
}

/// <summary>
/// Lap Lab's lap history: collects the SectorClock's completed laps, tracks the session-best
/// clean lap and the per-sector optimal composite, and renders the table — every lap a row,
/// every sector a delta against the chosen reference. Practice/qual/testing only by design.
/// Pure function of the tick otherwise — works identically in demo mode. Spec: docs/LAP-LAB.md.
/// </summary>
public sealed class LapLabTracker
{
    private readonly List<SectorLap> _laps = [];
    private int _bestIdx = -1;          // fastest clean lap in _laps
    private double[] _optimal = [];     // best clean time per sector; NaN until seen
    private int _loggedResets;
    private int _loggedTows;

    public void Reset()
    {
        _laps.Clear();
        _bestIdx = -1;
        _optimal = [];
        _loggedResets = 0;
        _loggedTows = 0;
    }

    public LapLabSnapshot Build(RawTick t, SectorClock clock, OverlayConfig cfg)
    {
        int drained = clock.DrainInto(_laps);
        for (int i = _laps.Count - drained; i < _laps.Count; i++) Absorb(i);

        // Teleports/tows are invisible in the table (the lap simply never completes) — log
        // them so a missing row is explainable from overlay.log.
        if (clock.ResetCount != _loggedResets)
        {
            _loggedResets = clock.ResetCount;
            Log.Write($"lap lab: reset/teleport detected — lap abandoned ({_loggedResets} this session)");
        }
        if (clock.TowCount != _loggedTows)
        {
            _loggedTows = clock.TowCount;
            Log.Write("lap lab: car left the world — lap abandoned");
        }

        if (!cfg.LapLab.Enabled || !clock.HasBoundaries ||
            StandingsSnapshot.KindOf(t.SessionType) == SessionKind.Race)
            return LapLabSnapshot.Empty;

        int n = clock.SectorCount;
        int dec = Math.Clamp(cfg.LapLab.Decimals, 1, 3);
        double eps = 0.5 * Math.Pow(10, -dec);

        // Reference: the optimal composite needs every sector seen clean at least once;
        // until then (and always for "SessionBest") the fastest clean lap is the reference.
        double[]? refSecs = null;
        string refName = "";
        bool wantOptimal = cfg.LapLab.Reference.Equals("SessionOptimal", StringComparison.OrdinalIgnoreCase);
        if (wantOptimal && _optimal.Length == n && !_optimal.Any(double.IsNaN))
        {
            refSecs = _optimal;
            refName = "optimal";
        }
        else if (_bestIdx >= 0)
        {
            refSecs = _laps[_bestIdx].Sectors;
            refName = "best";
        }
        double refLap = refSecs?.Sum() ?? 0;

        var headers = new string[n];
        for (int i = 0; i < n; i++) headers[i] = $"S{i + 1}";

        var rows = new List<LapLabRow>();
        int maxRows = Math.Clamp(cfg.LapLab.MaxRows, 1, 30);
        for (int li = _laps.Count - 1; li >= 0 && rows.Count < maxRows; li--)
        {
            var lap = _laps[li];
            var cells = new LapLabCell[n];
            for (int i = 0; i < n; i++)
            {
                double s = lap.Sectors[i];
                if (double.IsNaN(s)) { cells[i] = new LapLabCell("·", 4); continue; }
                bool dirty = lap.SectorDirty[i];
                if (refSecs is null)
                {
                    cells[i] = new LapLabCell(FmtTime(s, dec), dirty ? 3 : 0);
                }
                else
                {
                    double delta = s - refSecs[i];
                    int sign = dirty ? 3 : delta <= -eps ? 2 : delta >= eps ? 1 : 0;
                    cells[i] = new LapLabCell(FmtDelta(delta, dec), sign);
                }
            }

            bool clean = lap.Dirt == LapDirt.Clean;
            LapLabCell deltaCell;
            if (!clean)
            {
                string reason = lap.Dirt switch
                {
                    LapDirt.Off => lap.DirtSector >= 0 ? $"off S{lap.DirtSector + 1}" : "off",
                    LapDirt.Pit => "pit",
                    LapDirt.Reset => "AR",
                    _ => "tow",
                };
                deltaCell = new LapLabCell(reason, 3);
            }
            else if (refSecs is not null)
            {
                double delta = lap.LapTime - refLap;
                deltaCell = new LapLabCell(FmtDelta(delta, dec), delta <= -eps ? 2 : delta >= eps ? 1 : 0);
            }
            else
            {
                deltaCell = new LapLabCell("", 0);
            }

            rows.Add(new LapLabRow(
                LapText: lap.Number.ToString(),
                IsSessionBest: li == _bestIdx,
                Sectors: cells,
                TimeText: FmtTime(lap.LapTime, Math.Max(dec, 2)),
                TimeDim: !clean,
                Delta: deltaCell));
        }

        string refText = refSecs is null
            ? "no reference yet"
            : $"ref {refName} {FmtTime(refLap, Math.Max(dec, 2))}";
        return new LapLabSnapshot(true, refText, headers, rows);
    }

    /// <summary>Fold lap <paramref name="li"/> into best/optimal, and log it — the log line is
    /// the primary verification channel (splits + total + cleanliness, no screenshots needed).</summary>
    private void Absorb(int li)
    {
        var lap = _laps[li];
        bool fullyTimed = !lap.Sectors.Any(double.IsNaN);
        if (lap.Dirt == LapDirt.Clean && fullyTimed)
        {
            if (_bestIdx < 0 || lap.LapTime < _laps[_bestIdx].LapTime) _bestIdx = li;
            if (_optimal.Length != lap.Sectors.Length)
            {
                _optimal = new double[lap.Sectors.Length];
                Array.Fill(_optimal, double.NaN);
            }
            for (int i = 0; i < lap.Sectors.Length; i++)
                if (!lap.SectorDirty[i] && (double.IsNaN(_optimal[i]) || lap.Sectors[i] < _optimal[i]))
                    _optimal[i] = lap.Sectors[i];
        }

        string splits = string.Join(" ", lap.Sectors.Select(s => double.IsNaN(s) ? "·" : s.ToString("0.000")));
        string state = lap.Dirt == LapDirt.Clean ? "clean"
            : lap.Dirt == LapDirt.Off ? $"off-track S{lap.DirtSector + 1}"
            : lap.Dirt.ToString().ToLowerInvariant();
        Log.Write($"lap lab: L{lap.Number} [{splits}] = {lap.LapTime:0.000} ({state})");
    }

    /// <summary>"38.41" under a minute, "2:00.14" above. Rounds before splitting so 119.996
    /// never renders as "1:60.00".</summary>
    internal static string FmtTime(double s, int dec)
    {
        if (double.IsNaN(s)) return "—";
        s = Math.Round(s, dec);
        string fmt = dec > 0 ? "0." + new string('0', dec) : "0";
        if (s < 60) return s.ToString(fmt);
        int min = (int)(s / 60);
        return $"{min}:{(s - min * 60).ToString("0" + fmt)}";
    }

    internal static string FmtDelta(double d, int dec)
    {
        d = Math.Round(d, dec);
        string fmt = "0." + new string('0', Math.Max(dec, 1));
        return (d >= 0 ? "+" : "−") + Math.Abs(d).ToString(fmt);
    }
}
