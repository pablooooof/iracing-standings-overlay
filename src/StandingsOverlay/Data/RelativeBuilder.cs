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
    string StintText,        // laps into the current stint
    bool StintFresh,         // ≤3 laps old rubber — a threat worth highlighting
    string LastLapText,
    string PaceText,         // ▲ ▼ ► vs the player (same convention as the standings)
    int PaceSign,
    string GapText,          // "+2.3" ahead · "-0.8" behind · "—" on the player row
    int TyreSwitch = 0,      // 0 none · +1 just switched to wets · -1 to slicks (inline o→o)
    string PenaltyText = "") // penalty flag chip (DQ/BLK/DMG/WRN); empty in "Text" status style
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

            // Same convention as the traffic alerter so both show the *same* gap: one shared
            // phase delta decides ahead/behind AND the magnitude; a car behind closes at its
            // own pace (its class lap as the ruler), a car ahead you close on at yours.
            float ph = RelativeGap.SignedPhase(t, roster, d.CarIdx, t.PlayerCarIdx);  // + = ahead on track
            float carRef = ph < 0 && d.ClassEstLap > 10 ? d.ClassEstLap : refLap;
            float gap = ph * carRef;
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

        // Shared two-channel status (Data/CarStatus), collapsed per the relative's own style.
        var st = CarStatus.Of(t, stints, idx, cfg.ShowRejoinState, swap.JustSwapped(idx, 60),
                              d.ClassEstLap, outLapStates: true);
        var (statusText, penaltyText) = rc.StatusStyle.Equals("TextAndFlags", StringComparison.OrdinalIgnoreCase)
            ? (st.State, st.Penalty) : (st.Combined, "");

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

        // Laps into the current stint (blank when unknowable — mid-race join, on pit road);
        // green while the tyres are fresh out of a stop.
        string stint = "";
        bool fresh = false;
        if (rc.ShowStintAge)
        {
            if (stints.StintLaps(idx, t.Lap[idx]) is int laps) stint = laps.ToString();
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
            StatusText: statusText,
            PenaltyText: penaltyText,
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

}
