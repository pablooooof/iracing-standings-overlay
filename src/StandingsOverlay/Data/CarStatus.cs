namespace StandingsOverlay.Data;

/// <summary>
/// THE per-car status model, shared by standings and relative so a car never tells two stories.
/// Two independent channels, because they answer different questions and often overlap:
///
///   Penalty — race-control paperwork from CarIdxSessionFlags, at most one:
///     DQ > BLK (black flag) > DMG (meatball) > WRN (furled warning).
///   State — what the car is physically doing right now, at most one:
///     TOW  — sits in its pit stall without having driven through pit entry (teleported = towed;
///            StintTracker.WasTowedIn), or the player's own PlayerCarTowTime is counting.
///     SPUN — stationary on track ≥4 s, still in the world.
///     REJOIN / SLOW — moving again after a stop / limping well off pace (safety-relevant).
///     SWAP — driver just changed (team endurance, 60 s window).
///     PIT  — on pit road (drove there).
///     EXIT / OUT — just left the pits / on the out-lap (relative only; standings covers the
///            pit cycle in its strategy columns instead).
///
/// Overlaps the split resolves (the old single badge always hid one side): meatball car pitting
/// = DMG chip + PIT text; towed car with damage = DMG chip + TOW text; black-flagged car limping
/// = BLK chip + SLOW text.
///
/// "Text" display mode collapses both channels to ONE badge via Combined: DQ first (terminal),
/// then the physical safety states, then penalties, then the pit cycle —
///   DQ > TOW > SPUN > REJOIN > SLOW > BLK > DMG > WRN > SWAP > PIT > EXIT > OUT.
/// Rationale: in a text-only column the reader wants "what is this car DOING" (it's stopped in
/// front of me / it's towing away) before "what paperwork does it carry"; DQ outranks everything
/// because the car is out of the race.
/// </summary>
public readonly record struct CarStatus(string Penalty, string State)
{
    public string Combined =>
        Penalty == "DQ" ? "DQ"
        : State is "TOW" or "SPUN" or "REJOIN" or "SLOW" ? State
        : Penalty.Length > 0 ? Penalty
        : State;

    public static CarStatus Of(RawTick t, StintTracker stints, int idx, bool showRejoin,
        bool swapped, float refLap, bool outLapStates)
    {
        int flags = idx < t.SessionFlags.Length ? t.SessionFlags[idx] : 0;
        string penalty =
            (flags & CarFlags.Disqualify) != 0 ? "DQ"
            : (flags & CarFlags.Black) != 0 ? "BLK"
            : (flags & CarFlags.Repair) != 0 ? "DMG"
            : (flags & CarFlags.Furled) != 0 ? "WRN"
            : "";

        // A car that dropped offline freezes its telemetry, so the "stationary" timer grows
        // forever — SPUN is only real while the car is still in the world (surface != -1).
        bool inWorld = idx >= t.TrackSurface.Length || t.TrackSurface[idx] != -1;
        bool inPit = idx < t.OnPitRoad.Length && t.OnPitRoad[idx];

        string state =
            (idx == t.PlayerCarIdx && t.PlayerTowTime > 0) || stints.WasTowedIn(t, idx) ? "TOW"
            : inWorld && stints.LooksStopped(idx) ? "SPUN"
            : showRejoin && inWorld && stints.IsRejoining(idx, 6) ? "REJOIN"
            : showRejoin && inWorld && stints.LooksSlow(idx, refLap) ? "SLOW"
            : swapped ? "SWAP"
            : inPit ? "PIT"
            : outLapStates && stints.JustExitedPits(idx, 15) ? "EXIT"
            : outLapStates && stints.OnOutLap(idx, t.Lap[idx]) ? "OUT"
            : "";

        return new CarStatus(penalty, state);
    }
}
