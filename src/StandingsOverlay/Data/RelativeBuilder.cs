using StandingsOverlay.Config;

namespace StandingsOverlay.Data;

/// <summary>One display-ready relative row. All formatting happens here, not in XAML.</summary>
public sealed record RelativeRow(
    bool IsPlayer,
    string PosText,          // class position ("P3") or overall, per config
    string ClassColor,
    int Tyre,                // -1 unknown/hidden · 0 dry · >=1 wet (same as the standings ring)
    string CarNumber,
    string CarBrand,
    string Name,
    int LapParity,           // -1 you lap them (blue) · 0 same lap · +1 they lap you (red)
    string StatusText,       // "" | "PIT" | "OUT" | "SPUN"
    bool Battle,             // same class, same lap, within BattleGapSec → ▸ marker
    string IRatingText,
    string LicText,
    string LicColor,
    string StintText,        // laps since last pit stop
    bool StintFresh,         // ≤3 laps old rubber — a threat worth highlighting
    string LastLapText,
    string PaceText,         // ▲ ▼ ► vs the player (same convention as the standings)
    int PaceSign,
    string GapText,          // "+2.3" ahead · "-0.8" behind · "—" on the player row
    int TyreSwitch = 0)      // 0 none · +1 just switched to wets · -1 to slicks (inline o→o)
{
    public static readonly RelativeRow Blank =
        new(false, "", "", -1, "", "", "", 0, "", false, "", "", "", "", false, "", "", 0, "");

    public bool IsBlank => CarNumber.Length == 0 && !IsPlayer;
}

public sealed record RelativeSnapshot(IReadOnlyList<RelativeRow> Rows)
{
    public static readonly RelativeSnapshot Empty = new([]);

    /// <summary>Value comparison (the record alone compares Rows by reference).</summary>
    public bool VisuallyEquals(RelativeSnapshot? o)
    {
        if (o is null || Rows.Count != o.Rows.Count) return false;
        for (int i = 0; i < Rows.Count; i++)
            if (Rows[i] != o.Rows[i]) return false;
        return true;
    }
}

/// <summary>
/// Builds the relative box: the cars physically around the player in track order, nearest the
/// middle, with the context the sim's own F3 box lacks — pace, stint age, class position,
/// battle relevance. Pure function of the tick, shared by demo and live sources.
/// Gap math is the shared RelativeGap helper (also used by the traffic alerter).
/// Spec: docs/RELATIVE.md.
/// </summary>
public static class RelativeBuilder
{
    private const int FreshTyreLaps = 3;

    public static RelativeSnapshot Build(RawTick t, Roster roster, StintTracker stints,
        DriverSwapTracker swap, OverlayConfig cfg)
    {
        var rc = cfg.Relative;
        if (!rc.Enabled || !t.Has(t.PlayerCarIdx)) return RelativeSnapshot.Empty;
        // Lone qualifying: every driver runs alone, so any "cars around you" are ghosts from other
        // drivers' separate runs — the relative (and traffic) are meaningless.
        if (t.SessionType.Contains("Lone", StringComparison.OrdinalIgnoreCase)) return RelativeSnapshot.Empty;
        // In the garage / on the flatbed there is no meaningful "around me".
        if (t.PlayerCarIdx < t.TrackSurface.Length && t.TrackSurface[t.PlayerCarIdx] == -1)
            return RelativeSnapshot.Empty;
        if (!roster.Drivers.TryGetValue(t.PlayerCarIdx, out var me)) return RelativeSnapshot.Empty;

        bool isRace = StandingsSnapshot.KindOf(t.SessionType) == SessionKind.Race;
        float refLap = RelativeGap.PlayerRefLap(t, roster);
        double playerTotal = t.Lap[t.PlayerCarIdx] + t.LapDistPct[t.PlayerCarIdx];
        float? playerPace = stints.RecentPace(t.PlayerCarIdx);

        var ahead = new List<(float Gap, DriverEntry D)>();
        var behind = new List<(float Gap, DriverEntry D)>();
        foreach (var d in roster.Drivers.Values)
        {
            if (d.CarIdx == t.PlayerCarIdx || d.IsPaceCar || d.IsSpectator || !t.Has(d.CarIdx)) continue;
            if (d.CarIdx < t.TrackSurface.Length && t.TrackSurface[d.CarIdx] == -1) continue;
            if (t.Lap[d.CarIdx] < 0) continue;
            // A car sat in the pits for a long time is parked (DNF / no driver) — noise, not traffic.
            if (rc.HideParkedCars && d.CarIdx < t.OnPitRoad.Length && t.OnPitRoad[d.CarIdx]
                && stints.StoppedSeconds(d.CarIdx) > 60) continue;

            float gap = RelativeGap.SignedSeconds(t, d.CarIdx, refLap);
            (gap >= 0 ? ahead : behind).Add((gap, d));
        }
        ahead.Sort((a, b) => a.Gap.CompareTo(b.Gap));    // nearest first
        behind.Sort((a, b) => b.Gap.CompareTo(a.Gap));   // nearest first (closest to zero)

        // Fixed slot count: blanks instead of a resizing window, so the player row never
        // jumps on screen (the window is usually anchored near the bottom edge).
        int nAhead = Math.Max(0, rc.CarsAhead), nBehind = Math.Max(0, rc.CarsBehind);
        var rows = new List<RelativeRow>(nAhead + nBehind + 1);
        int takeAhead = Math.Min(ahead.Count, nAhead);
        for (int i = takeAhead; i < nAhead; i++) rows.Add(RelativeRow.Blank);
        for (int i = takeAhead - 1; i >= 0; i--)   // furthest ahead at the top
            rows.Add(BuildRow(ahead[i].D, ahead[i].Gap, false, t, stints, swap, cfg, isRace,
                              playerTotal, me.CarClassId, playerPace));
        rows.Add(BuildRow(me, 0, true, t, stints, swap, cfg, isRace, playerTotal, me.CarClassId, playerPace));
        for (int i = 0; i < Math.Min(behind.Count, nBehind); i++)
            rows.Add(BuildRow(behind[i].D, behind[i].Gap, false, t, stints, swap, cfg, isRace,
                              playerTotal, me.CarClassId, playerPace));
        while (rows.Count < nAhead + 1 + nBehind) rows.Add(RelativeRow.Blank);

        return new RelativeSnapshot(rows);
    }

    private static RelativeRow BuildRow(DriverEntry d, float gap, bool isPlayer, RawTick t,
        StintTracker stints, DriverSwapTracker swap, OverlayConfig cfg, bool isRace, double playerTotal,
        int playerClassId, float? playerPace)
    {
        var rc = cfg.Relative;
        int idx = d.CarIdx;
        bool inPit = idx < t.OnPitRoad.Length && t.OnPitRoad[idx];

        // Lap parity relative to where the row is DISPLAYED: total-distance delta minus the
        // wrapped on-track delta is a near-integer lap count. A car shown 2 s behind you that
        // is 0.95 total laps up rounds to +1 — it is lapping you, exactly like the sim colors it.
        int parity = 0;
        if (isRace && !isPlayer)
            parity = Math.Sign((int)Math.Round(
                t.Lap[idx] + t.LapDistPct[idx] - playerTotal - RelativeGap.SignedLaps(t, idx)));

        bool battle = isRace && !isPlayer && !inPit && parity == 0 &&
                      d.CarClassId == playerClassId && Math.Abs(gap) <= rc.BattleGapSec;

        // Position is just the number in the relative (the column is self-evidently position).
        string pos = "";
        int cp = idx < t.ClassPosition.Length ? t.ClassPosition[idx] : 0;
        int op = idx < t.Position.Length ? t.Position[idx] : 0;
        if (rc.ShowClassPos && cp > 0) pos = cp.ToString();
        else if (op > 0) pos = op.ToString();

        // Stint number (ST1 = opening stint, ST2 = after one stop, …); still green while the tyres
        // are fresh out of a stop.
        string stint = "";
        bool fresh = false;
        if (rc.ShowStintAge)
        {
            stint = "ST" + (stints.PitStops(idx) + 1);
            if (stints.LapsSincePit(idx, t.Lap[idx]) is int age) fresh = age <= FreshTyreLaps;
        }

        string pace = "";
        int paceSign = 0;
        if (rc.ShowPace && !isPlayer
            && stints.RecentPace(idx) is float rp && playerPace is float pp && pp > 0)
        {
            if (rp < pp * 0.997f) { pace = "▲"; paceSign = 1; }
            else if (rp > pp * 1.003f) { pace = "▼"; paceSign = -1; }
            else { pace = "►"; }
        }

        float last = idx < t.LastLap.Length ? t.LastLap[idx] : 0;

        return new RelativeRow(
            IsPlayer: isPlayer,
            PosText: pos,
            ClassColor: d.ClassColor,
            Tyre: rc.ShowTyre && idx < t.TireCompound.Length ? t.TireCompound[idx] : -1,
            TyreSwitch: rc.ShowTyre ? SnapshotBuilder.InlineTyreSwitch(t, stints, idx, cfg) : 0,
            CarNumber: "#" + d.CarNumber,
            CarBrand: rc.ShowBrand ? d.CarBrand : "",
            Name: d.Name,
            LapParity: parity,
            StatusText: RelativeStatus(t, stints, idx, inPit, cfg.ShowRejoinState, swap.JustSwapped(idx, 60), d.ClassEstLap),
            Battle: battle,
            IRatingText: rc.ShowIRating ? SnapshotBuilder.FmtIr(d.IRating) : "",
            LicText: rc.ShowLicense ? d.LicString : "",
            LicColor: d.LicColor,
            StintText: stint,
            StintFresh: fresh,
            LastLapText: rc.ShowLastLap && last > 0 ? SnapshotBuilder.FmtLap(last, cfg.LapTimePrecision) : "",
            PaceText: pace,
            PaceSign: paceSign,
            // Ahead is unsigned (list order shows it); behind keeps its "-".
            GapText: isPlayer ? "—"
                : (gap < 0 ? "-" : "") + SnapshotBuilder.FmtGap(Math.Abs(gap), rc.GapPrecision));
    }

    /// <summary>Relative status badge, sharing the standings' penalty flags plus the relative-only
    /// PIT / OUT (out-lap) / REJOIN / SPUN states.</summary>
    private static string RelativeStatus(RawTick t, StintTracker stints, int idx, bool inPit,
        bool showRejoin, bool swapped, float refLap)
    {
        int flags = idx < t.SessionFlags.Length ? t.SessionFlags[idx] : 0;
        if ((flags & CarFlags.Disqualify) != 0) return "DQ";
        if ((flags & CarFlags.Black) != 0) return "BLK";
        if ((flags & CarFlags.Repair) != 0) return "DMG";
        if ((flags & CarFlags.Furled) != 0) return "WRN";
        bool inWorld = idx >= t.TrackSurface.Length || t.TrackSurface[idx] != -1;
        if (inWorld && stints.LooksStopped(idx)) return SnapshotBuilder.StoppedBadge(t, stints, idx);
        if (swapped) return "SWAP";
        if (showRejoin && inWorld && stints.IsRejoining(idx, 6)) return "REJOIN";
        if (showRejoin && inWorld && stints.LooksSlow(idx, refLap)) return "SLOW";
        if (inPit) return "PIT";
        // Fresh out of the pits (~15s) reads EXIT — bright, to catch the eye; the rest of the
        // out-lap is a steady OUT (cold tyres).
        if (stints.JustExitedPits(idx, 15)) return "EXIT";
        if (stints.OnOutLap(idx, t.Lap[idx])) return "OUT";
        return "";
    }
}
