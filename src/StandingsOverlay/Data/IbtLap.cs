using System.IO;
using System.Text;
using irsdkSharp.Serialization.Models.Session;

namespace StandingsOverlay.Data;

/// <summary>
/// Minimal reader for iRacing .ibt telemetry files — the SDK's disk layout (112-byte header,
/// 32-byte disk sub-header, var headers, embedded session YAML, then fixed-size telemetry
/// rows). We only need five channels; rows are streamed once to segment laps and once more
/// to grid the chosen lap, so even a multi-hundred-MB endurance file stays cheap. Self-written
/// on purpose: no new dependency, MIT-clean. Spec: docs/LAP-LAB.md.
/// </summary>
public static class IbtLap
{
    private const int HeaderSize = 112;
    private const int VarHeaderSize = 144;

    private sealed record Var(int Type, int Offset);

    private sealed class LapSpan
    {
        public long FirstRow;      // row index the lap's S/F crossing falls between (row-1, row)
        public long LastRow;       // row index just past the closing S/F crossing
        public double LapTime;
        public bool Dirty;
    }

    /// <summary>Parse the file and return its fastest complete lap (clean preferred) as a
    /// LapRef with grid + conditions, or null with a reason in <paramref name="error"/>.</summary>
    public static LapRef? ReadBestLap(string path, out string error)
    {
        error = "";
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                          1 << 16, FileOptions.SequentialScan);
            using var br = new BinaryReader(fs);

            Span<int> h = stackalloc int[HeaderSize / 4];
            for (int i = 0; i < h.Length; i++) h[i] = br.ReadInt32();
            int sessionInfoLen = h[4], sessionInfoOffset = h[5];
            int numVars = h[6], varHeaderOffset = h[7];
            int bufLen = h[9];
            long bufOffset = h[13];   // varBuf[0].bufOffset

            fs.Position = HeaderSize;                     // irsdk_diskSubHeader
            long sessionStartDate = br.ReadInt64();
            br.ReadDouble(); br.ReadDouble();
            br.ReadInt32();
            long recordCount = br.ReadInt32();

            if (bufLen <= 0 || bufOffset <= 0 || numVars <= 0) { error = "not an .ibt file"; return null; }
            if (recordCount <= 0) recordCount = (fs.Length - bufOffset) / bufLen;
            if (recordCount < 100) { error = "file too short"; return null; }

            // Var headers → the channels we need.
            var vars = new Dictionary<string, Var>(numVars, StringComparer.OrdinalIgnoreCase);
            fs.Position = varHeaderOffset;
            var vh = new byte[VarHeaderSize];
            for (int i = 0; i < numVars; i++)
            {
                fs.ReadExactly(vh);
                int type = BitConverter.ToInt32(vh, 0);
                int offset = BitConverter.ToInt32(vh, 4);
                string name = CStr(vh, 16, 32);
                vars[name] = new Var(type, offset);
            }
            if (!vars.TryGetValue("SessionTime", out var vTime) ||
                !vars.TryGetValue("LapDistPct", out var vPct))
            { error = "missing SessionTime/LapDistPct channels"; return null; }
            vars.TryGetValue("Speed", out var vSpeed);
            vars.TryGetValue("Brake", out var vBrake);
            vars.TryGetValue("Throttle", out var vThrottle);
            bool pedals = vBrake is not null && vThrottle is not null;
            vars.TryGetValue("PlayerTrackSurface", out var vSurf);
            vars.TryGetValue("OnPitRoad", out var vPit);
            vars.TryGetValue("TrackWetness", out var vWet);
            vars.TryGetValue("SessionNum", out var vSessNum);

            // Embedded session YAML → conditions.
            fs.Position = sessionInfoOffset;
            var yamlBytes = new byte[sessionInfoLen];
            fs.ReadExactly(yamlBytes);
            string yaml = Encoding.UTF8.GetString(yamlBytes).TrimEnd('\0');

            // ---- pass 1: segment laps ------------------------------------
            var row = new byte[bufLen];
            var laps = new List<LapSpan>();
            LapSpan? cur = null;
            double lapStart = 0;
            float prevPct = -1; double prevT = 0;
            int wetness = -1, sessNum = -1;

            fs.Position = bufOffset;
            for (long r = 0; r < recordCount; r++)
            {
                if (fs.Read(row, 0, bufLen) != bufLen) break;
                double t = BitConverter.ToDouble(row, vTime.Offset);
                float pct = BitConverter.ToSingle(row, vPct.Offset);
                if (r == 0)
                {
                    if (vWet is not null) wetness = BitConverter.ToInt32(row, vWet.Offset);
                    if (vSessNum is not null) sessNum = BitConverter.ToInt32(row, vSessNum.Offset);
                }
                if (pct < 0) { prevPct = -1; cur = null; continue; }
                if (prevPct < 0) { prevPct = pct; prevT = t; continue; }

                float d = pct - prevPct;
                bool wrapped = d < -0.5f;
                if (d > 0.5f || (!wrapped && d < -0.02f)) { cur = null; }   // teleport / reset
                else if (wrapped && t > prevT)
                {
                    double tCross = prevT + (t - prevT) * (1 - prevPct) / (1 - prevPct + pct);
                    if (cur is not null)
                    {
                        cur.LapTime = tCross - lapStart;
                        cur.LastRow = r;
                        if (cur.LapTime > 10) laps.Add(cur);   // sanity: ignore garage blips
                    }
                    cur = new LapSpan { FirstRow = r == 0 ? 0 : r - 1 };
                    lapStart = tCross;
                }
                if (cur is not null)
                {
                    bool off = vSurf is not null && BitConverter.ToInt32(row, vSurf.Offset) == 0;
                    bool pit = (vPit is not null && row[vPit.Offset] != 0) ||
                               (vSurf is not null && BitConverter.ToInt32(row, vSurf.Offset) is 1 or 2);
                    if (off || pit) cur.Dirty = true;
                }
                prevPct = pct; prevT = t;
            }
            if (laps.Count == 0) { error = "no complete laps in file"; return null; }

            var clean = laps.Where(l => !l.Dirty).ToList();
            var best = (clean.Count > 0 ? clean : laps).MinBy(l => l.LapTime)!;

            // ---- pass 2: grid the chosen lap ------------------------------
            var gridT = new float[LapRef.GridSize];
            var gridV = vSpeed is not null ? new float[LapRef.GridSize] : [];
            var gridB = pedals ? new float[LapRef.GridSize] : [];
            var gridTh = pedals ? new float[LapRef.GridSize] : [];
            Array.Fill(gridT, float.NaN);
            if (gridV.Length > 0) Array.Fill(gridV, float.NaN);
            if (pedals) { Array.Fill(gridB, float.NaN); Array.Fill(gridTh, float.NaN); }

            double t0 = -1; prevPct = -1; prevT = 0;
            Span<float> prev = stackalloc float[3];   // speed, brake, throttle
            Span<float> curr = stackalloc float[3];
            double lapTime = best.LapTime;
            fs.Position = bufOffset + best.FirstRow * bufLen;
            for (long r = best.FirstRow; r <= Math.Min(best.LastRow, recordCount - 1); r++)
            {
                if (fs.Read(row, 0, bufLen) != bufLen) break;
                double t = BitConverter.ToDouble(row, vTime.Offset);
                float pct = BitConverter.ToSingle(row, vPct.Offset);
                curr[0] = vSpeed is not null ? BitConverter.ToSingle(row, vSpeed.Offset) : 0;
                curr[1] = pedals ? BitConverter.ToSingle(row, vBrake!.Offset) : 0;
                curr[2] = pedals ? BitConverter.ToSingle(row, vThrottle!.Offset) : 0;
                if (prevPct < 0) { prevPct = pct; prevT = t; curr.CopyTo(prev); continue; }

                float d = pct - prevPct;
                if (d < -0.5f)   // the S/F crossing that starts (or ends) the lap
                {
                    double tCross = prevT + (t - prevT) * (1 - prevPct) / (1 - prevPct + pct);
                    if (t0 < 0)
                    {
                        t0 = tCross;
                        FillBins(gridT, gridV, gridB, gridTh, 0, pct, tCross, t, prev, curr, t0);
                    }
                    else
                    {
                        lapTime = tCross - t0;   // exact close; stop gridding
                        break;
                    }
                }
                else if (t0 >= 0 && d > 0)
                {
                    FillBins(gridT, gridV, gridB, gridTh, prevPct, pct, prevT, t, prev, curr, t0);
                }
                prevPct = pct; prevT = t; curr.CopyTo(prev);
            }
            gridT[0] = 0;
            PatchGaps(gridT, (float)lapTime);
            if (gridV.Length > 0) PatchGaps(gridV, gridV.LastOrDefault(x => !float.IsNaN(x), 0));
            if (pedals) { PatchGaps(gridB, 0); PatchGaps(gridTh, 1); }

            // ---- conditions from the embedded YAML ------------------------
            RefConditions? cond = null;
            try
            {
                var model = IRacingSessionModel.Serialize(yaml);
                var wi = model?.WeekendInfo;
                var di = model?.DriverInfo;
                var me = di?.Drivers?.FirstOrDefault(x => x.CarIdx == di.DriverCarIdx);
                var sess = model?.SessionInfo?.Sessions?
                    .FirstOrDefault(s => s.SessionNum == sessNum && !string.IsNullOrEmpty(s.SessionTrackRubberState))
                    ?? model?.SessionInfo?.Sessions?
                    .LastOrDefault(s => !string.IsNullOrEmpty(s.SessionTrackRubberState));
                cond = new RefConditions(
                    TrackId: wi?.TrackID ?? 0,
                    TrackConfig: wi?.TrackConfigName ?? "",
                    TrackName: wi?.TrackDisplayShortName ?? "",
                    CarPath: me?.CarPath ?? "",
                    CarName: me?.CarScreenName ?? "",
                    TrackTempC: RefGuard.ParseUnit(wi?.TrackSurfaceTemp),
                    AirTempC: RefGuard.ParseUnit(wi?.TrackAirTemp),
                    WindVelMs: RefGuard.ParseUnit(wi?.TrackWindVel),
                    TrackWetness: wetness,
                    RubberState: sess?.SessionTrackRubberState ?? "",
                    RecordedUtc: sessionStartDate > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(sessionStartDate).ToString("yyyy-MM-dd")
                        : "");
            }
            catch { /* conditions stay unknown; the lap itself is still usable */ }

            return new LapRef
            {
                Source = "file",
                Label = Path.GetFileNameWithoutExtension(path),
                LapTime = lapTime,
                TimeAtPct = gridT,
                SpeedAtPct = gridV,
                BrakeAtPct = gridB,
                ThrottleAtPct = gridTh,
                Conditions = cond,
            };
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Fill grid bins covered by one sample step, linearly interpolating time (as
    /// an offset from lap start <paramref name="t0"/>), speed and the pedals.</summary>
    private static void FillBins(float[] gridT, float[] gridV, float[] gridB, float[] gridTh,
                                 float pctA, float pctB, double tA, double tB,
                                 ReadOnlySpan<float> a, ReadOnlySpan<float> b, double t0)
    {
        int from = Math.Max(0, (int)(pctA * LapRef.GridSize) + (pctA <= 0 ? 0 : 1));
        int to = Math.Min(LapRef.GridSize - 1, (int)(pctB * LapRef.GridSize));
        double span = pctB - pctA;
        for (int k = from; k <= to; k++)
        {
            float frac = span <= 0 ? 0 : (float)(((double)k / LapRef.GridSize - pctA) / span);
            gridT[k] = (float)(tA + (tB - tA) * frac - t0);
            if (gridV.Length > 0) gridV[k] = a[0] + (b[0] - a[0]) * frac;
            if (gridB.Length > 0) gridB[k] = a[1] + (b[1] - a[1]) * frac;
            if (gridTh.Length > 0) gridTh[k] = a[2] + (b[2] - a[2]) * frac;
        }
    }

    /// <summary>Forward-fill NaN bins (sampling gaps) so interpolation never sees holes.</summary>
    private static void PatchGaps(float[] grid, float tail)
    {
        float last = 0;
        for (int k = 0; k < grid.Length; k++)
        {
            if (float.IsNaN(grid[k])) grid[k] = last;
            else last = grid[k];
        }
        if (float.IsNaN(grid[^1])) grid[^1] = tail;
    }

    private static string CStr(byte[] buf, int offset, int len)
    {
        int end = Array.IndexOf(buf, (byte)0, offset, len);
        return Encoding.ASCII.GetString(buf, offset, (end < 0 ? offset + len : end) - offset);
    }
}
