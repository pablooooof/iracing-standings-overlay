using StandingsOverlay.Config;

namespace StandingsOverlay.Data;

/// <summary>One table cell. Sign: 0 neutral · 1 slower than ref (red) · 2 faster than ref
/// (purple) · 3 dirty (amber) · 4 dim (untimed / not applicable). Heat is the signed
/// magnitude (-1..+1, delta / HeatScale) that drives the cell's background — the at-a-glance
/// "where am I losing it" channel while driving.</summary>
public readonly record struct LapLabCell(string Text, int Sign, float Heat = 0);

public sealed record LapLabRow(
    string LapText,
    bool IsSessionBest,     // green lap number + time
    IReadOnlyList<LapLabCell> Sectors,
    string TimeText,
    bool TimeDim,           // dirty laps: time shown muted
    LapLabCell Delta,       // lap delta vs ref — always the number, even on dirty laps
    LapLabCell Status)      // "off S2" / "pit" / "AR" / "slow"; empty when clean
{
    public bool VisuallyEquals(LapLabRow o)
    {
        if (LapText != o.LapText || IsSessionBest != o.IsSessionBest || TimeText != o.TimeText ||
            TimeDim != o.TimeDim || Delta != o.Delta || Status != o.Status ||
            Sectors.Count != o.Sectors.Count) return false;
        for (int i = 0; i < Sectors.Count; i++)
            if (Sectors[i] != o.Sectors[i]) return false;
        return true;
    }
}

public sealed record LapLabSnapshot(
    bool Show,
    string RefText,                       // "ref best 1:59.48" / "no reference yet"
    IReadOnlyList<string> SectorHeaders,  // "S1".."Sn"
    IReadOnlyList<LapLabRow> Rows,        // newest first
    string WarnText = "",                 // conditions chip ("track +5°C vs ref", "ref blocked: ≠ car")
    int WarnSeverity = -1)                // 2 block (red) · 1 warn (amber) · 0 info (grey)
{
    public static readonly LapLabSnapshot Empty = new(false, "", [], []);

    public bool VisuallyEquals(LapLabSnapshot? o)
    {
        if (o is null || Show != o.Show || RefText != o.RefText ||
            WarnText != o.WarnText || WarnSeverity != o.WarnSeverity ||
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
        _turnBounds = [];
        _turnSrcRef = null;
        _turnSrcLap = -1;
    }

    public LapLabSnapshot Build(RawTick t, SectorClock clock, Roster roster, LapRefStore store, OverlayConfig cfg)
    {
        int prevBest = _bestIdx;
        int drained = clock.DrainInto(_laps);
        for (int i = _laps.Count - drained; i < _laps.Count; i++) Absorb(i);

        // New session best → persist as this combo's previous-best benchmark (the store only
        // writes when it actually beats what's on disk).
        if (_bestIdx != prevBest && _bestIdx >= 0 && cfg.LapLab.SaveSessionBest)
        {
            string saveKey = LapRefStore.Key(roster);
            var b = _laps[_bestIdx];
            if (saveKey.Length > 0 && b.TimeAtPct.Length > 0)
                store.SavePrev(saveKey, new LapRef
                {
                    Source = "prev",
                    Label = "previous best",
                    LapTime = b.LapTime,
                    TimeAtPct = b.TimeAtPct,
                    SpeedAtPct = b.SpeedAtPct,
                    Conditions = new RefConditions(
                        roster.TrackId, roster.TrackConfig, roster.TrackName,
                        roster.PlayerCarPath, roster.PlayerCarName,
                        t.TrackTemp, float.NaN, t.WindVel, t.TrackWetness,
                        roster.RubberState, DateTime.UtcNow.ToString("yyyy-MM-dd")),
                });
        }

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

        int dec = Math.Clamp(cfg.LapLab.Decimals, 1, 3);
        double eps = 0.5 * Math.Pow(10, -dec);

        // Reference: File / PreviousBest are external LapRefs, scored at TODAY's boundaries
        // (SectorsFor) and run through the conditions guard; anything unavailable or blocked
        // falls back to the session best so the table never goes delta-less mid-session.
        // Resolved BEFORE the view: turn segmentation prefers the reference's speed trace.
        double[]? refSecs = null;
        double refLap = 0;
        string refName = "";
        int warnSev = -1;
        string warnChip = "";
        string mode = cfg.LapLab.Reference;
        LapRef? ext = null;
        if (mode.Equals("File", StringComparison.OrdinalIgnoreCase))
        {
            store.EnsureFile(cfg.LapLab.ReferenceFile);
            ext = store.FileRef;
            if (ext is null)
            {
                warnSev = 0;
                warnChip = store.FileLoading ? "loading ref…"
                    : store.FileError.Length > 0 ? "file: " + store.FileError
                    : string.IsNullOrWhiteSpace(cfg.LapLab.ReferenceFile) ? "no file picked" : "";
            }
        }
        else if (mode.Equals("PreviousBest", StringComparison.OrdinalIgnoreCase))
        {
            string key = LapRefStore.Key(roster);
            store.EnsurePrev(key);
            ext = store.PrevRef;
            if (ext is null && key.Length > 0) { warnSev = 0; warnChip = "no previous best yet"; }
        }
        if (ext is not null)
        {
            var (sev, chip) = RefGuard.Diff(ext.Conditions, roster, t, cfg.LapLab.WarnTrackTempDelta);
            if (sev == 2) { warnSev = 2; warnChip = "ref blocked: " + chip; ext = null; }
            else if (sev >= 0) { warnSev = sev; warnChip = chip; }
        }
        // View: official sectors, or turn zones segmented from the reference's speed trace
        // (the session best's own trace when the ref carries none). No usable trace → sectors.
        bool turnView = cfg.LapLab.View.Equals("Turns", StringComparison.OrdinalIgnoreCase);
        float[] bounds = clock.Bounds;
        if (turnView)
        {
            var tb = TurnBounds(ext);
            if (tb.Length >= CornerMap.MinZones) bounds = tb;
            else turnView = false;
        }
        int n = bounds.Length;

        if (ext is not null)
        {
            refSecs = ext.SectorsFor(bounds);
            refLap = ext.LapTime;
            refName = ext.Source;
        }
        else
        {
            // The optimal composite needs every split seen clean at least once; until then
            // (and always for "SessionBest") the fastest clean lap is the reference.
            bool wantOptimal = mode.Equals("SessionOptimal", StringComparison.OrdinalIgnoreCase);
            if (wantOptimal)
            {
                refSecs = turnView ? OptimalFor(bounds, clock.Bounds)
                    : _optimal.Length == n && !_optimal.Any(double.IsNaN) ? _optimal : null;
                if (refSecs is not null) refName = "optimal";
            }
            if (refSecs is null && _bestIdx >= 0)
            {
                var b = _laps[_bestIdx];
                refSecs = turnView ? LapRef.SplitsOf(b.TimeAtPct, b.LapTime, bounds) : b.Sectors;
                refName = "best";
            }
            refLap = refSecs?.Sum() ?? 0;
        }

        var headers = new string[n];
        for (int i = 0; i < n; i++) headers[i] = turnView ? $"T{i + 1}" : $"S{i + 1}";

        var rows = new List<LapLabRow>();
        int maxRows = Math.Clamp(cfg.LapLab.MaxRows, 1, 30);
        float heatScale = (float)Math.Max(0.05, cfg.LapLab.HeatScale);
        // 107% rule: a lap this far off the pace is traffic or a trip through the gravel —
        // it collapses to one quiet line instead of a row of screaming red.
        double slowAt = 1.07 * (_bestIdx >= 0 ? _laps[_bestIdx].LapTime : refSecs is not null ? refLap : double.MaxValue);
        for (int li = _laps.Count - 1; li >= 0 && rows.Count < maxRows; li--)
        {
            var lap = _laps[li];
            bool clean = lap.Dirt == LapDirt.Clean;
            string reason = lap.Dirt switch
            {
                LapDirt.Clean => "",
                LapDirt.Off => lap.DirtSector >= 0 ? $"off S{lap.DirtSector + 1}" : "off",
                LapDirt.Pit => "pit",
                LapDirt.Reset => "AR",
                _ => "tow",
            };
            string timeText = FmtTime(lap.LapTime, Math.Max(dec, 2));

            if (cfg.LapLab.HideSlowLaps && lap.LapTime > slowAt)
            {
                var quiet = new LapLabCell("", 0);
                rows.Add(new LapLabRow(lap.Number.ToString(), false,
                    Enumerable.Repeat(quiet, n).ToArray(), timeText, true, quiet,
                    new LapLabCell(reason.Length > 0 ? reason : "slow", 4)));
                continue;
            }

            double[] splits = turnView ? LapRef.SplitsOf(lap.TimeAtPct, lap.LapTime, bounds) : lap.Sectors;
            var cells = new LapLabCell[n];
            for (int i = 0; i < n; i++)
            {
                double s = splits[i];
                if (double.IsNaN(s)) { cells[i] = new LapLabCell("·", 4); continue; }
                bool dirty = turnView ? TurnDirty(lap, bounds, i, clock.Bounds) : lap.SectorDirty[i];
                if (refSecs is null)
                {
                    cells[i] = new LapLabCell(FmtTime(s, dec), dirty ? 3 : 0);
                }
                else
                {
                    double delta = s - refSecs[i];
                    int sign = dirty ? 3 : delta <= -eps ? 2 : delta >= eps ? 1 : 0;
                    float heat = dirty ? 0 : (float)Math.Clamp(delta / heatScale, -1, 1);
                    cells[i] = new LapLabCell(FmtDelta(delta, dec), sign, heat);
                }
            }

            LapLabCell deltaCell = new("", 0);
            if (refSecs is not null)
            {
                double delta = lap.LapTime - refLap;
                deltaCell = new LapLabCell(FmtDelta(delta, dec), delta <= -eps ? 2 : delta >= eps ? 1 : 0);
            }

            rows.Add(new LapLabRow(
                LapText: lap.Number.ToString(),
                IsSessionBest: li == _bestIdx,
                Sectors: cells,
                TimeText: timeText,
                TimeDim: !clean,
                Delta: deltaCell,
                Status: clean ? new LapLabCell("", 0) : new LapLabCell(reason, 3)));
        }

        string refText = refSecs is null
            ? "no reference yet"
            : $"ref {refName} {FmtTime(refLap, Math.Max(dec, 2))}";
        return new LapLabSnapshot(true, refText, headers, rows, warnChip, warnChip.Length > 0 ? warnSev : -1);
    }

    private float[] _turnBounds = [];
    private LapRef? _turnSrcRef;
    private int _turnSrcLap = -1;

    /// <summary>Turn-zone boundaries from the active reference's speed trace, falling back to
    /// the session best's own. Cached per source lap, so zones stay stable across the session
    /// and segmentation only reruns when the source actually changes.</summary>
    private float[] TurnBounds(LapRef? ext)
    {
        float[] speed = [];
        LapRef? srcRef = null;
        int srcLap = -1;
        if (ext is { SpeedAtPct.Length: > 0 }) { speed = ext.SpeedAtPct; srcRef = ext; }
        else if (_bestIdx >= 0 && _laps[_bestIdx].SpeedAtPct.Length > 0)
        { speed = _laps[_bestIdx].SpeedAtPct; srcLap = _bestIdx; }
        if (speed.Length == 0) return [];

        if (ReferenceEquals(srcRef, _turnSrcRef) && srcLap == _turnSrcLap) return _turnBounds;
        _turnSrcRef = srcRef;
        _turnSrcLap = srcLap;
        _turnBounds = CornerMap.FromSpeed(speed);
        Log.Write(_turnBounds.Length >= CornerMap.MinZones
            ? $"lap lab: {_turnBounds.Length} turn zones [{string.Join(" ", _turnBounds.Select(b => b.ToString("0.###")))}]"
            : "lap lab: turn detection found no usable zones — falling back to sectors");
        return _turnBounds;
    }

    /// <summary>Per-zone best — the turn-view optimal. Same semantics as the sector optimal:
    /// non-dirty zones from any honestly timed lap; null until every zone has a sample.</summary>
    private double[]? OptimalFor(float[] bounds, float[] secBounds)
    {
        double[]? opt = null;
        foreach (var lap in _laps)
        {
            if (lap.Dirt >= LapDirt.Reset || lap.TimeAtPct.Length == 0) continue;
            var s = LapRef.SplitsOf(lap.TimeAtPct, lap.LapTime, bounds);
            opt ??= Enumerable.Repeat(double.NaN, bounds.Length).ToArray();
            for (int i = 0; i < opt.Length; i++)
                if (!TurnDirty(lap, bounds, i, secBounds) &&
                    (double.IsNaN(opt[i]) || s[i] < opt[i]))
                    opt[i] = s[i];
        }
        return opt is not null && !opt.Any(double.IsNaN) ? opt : null;
    }

    /// <summary>Off-track/pit attribution is recorded per official sector — a turn zone is
    /// dirty when its span overlaps any dirty sector (conservative amber).</summary>
    private static bool TurnDirty(SectorLap lap, float[] bounds, int i, float[] secBounds)
    {
        float from = bounds[i], to = i < bounds.Length - 1 ? bounds[i + 1] : 1f;
        for (int s = 0; s < secBounds.Length && s < lap.SectorDirty.Length; s++)
        {
            if (!lap.SectorDirty[s]) continue;
            float sFrom = secBounds[s], sTo = s < secBounds.Length - 1 ? secBounds[s + 1] : 1f;
            if (from < sTo && to > sFrom) return true;
        }
        return false;
    }

    /// <summary>Fold lap <paramref name="li"/> into best/optimal, and log it — the log line is
    /// the primary verification channel (splits + total + cleanliness, no screenshots needed).</summary>
    private void Absorb(int li)
    {
        var lap = _laps[li];
        bool fullyTimed = !lap.Sectors.Any(double.IsNaN);
        if (lap.Dirt == LapDirt.Clean && fullyTimed &&
            (_bestIdx < 0 || lap.LapTime < _laps[_bestIdx].LapTime))
            _bestIdx = li;

        // Optimal composites the best NON-DIRTY sectors from any honestly timed lap — same
        // semantics as iRacing's own optimal, so a great S1 on a lap that ended in the gravel
        // still counts. Only teleport-broken laps (Reset/Tow gaps) are excluded outright.
        if (lap.Dirt < LapDirt.Reset)
        {
            if (_optimal.Length != lap.Sectors.Length)
            {
                _optimal = new double[lap.Sectors.Length];
                Array.Fill(_optimal, double.NaN);
            }
            for (int i = 0; i < lap.Sectors.Length; i++)
                if (!lap.SectorDirty[i] && !double.IsNaN(lap.Sectors[i]) &&
                    (double.IsNaN(_optimal[i]) || lap.Sectors[i] < _optimal[i]))
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
