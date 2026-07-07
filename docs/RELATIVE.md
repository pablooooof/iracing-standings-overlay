# Relative box — design

*Status: implemented (v0.5). This doc is the spec; keep it in sync with `Data/RelativeBuilder.cs`.*

## Why build one (what iRacing's F3 black box lacks)

The sim's own relative answers exactly one question — "who is physically around me" — and
stops there. Things it does not tell you, which decide how you actually drive the next corner:

| Missing in the sim | Why it matters | Our answer |
|---|---|---|
| **Pace of the car around you** | Is the car ahead catchable? Is the car behind a threat or just holding on? | LAST lap column + ▲/▼/► pace arrow vs the player (reuses `StintTracker.RecentPace`) |
| **Tyre/stint age** | A car behind on 2-lap-old tyres is a different animal than one on a 25-lap stint | Stint-age column (laps since last stop), green while fresh (≤3 laps) |
| **Class position** | "P14" overall is noise in multiclass; P3 *in class* is the battle | Class position, shown in class color |
| **Car brand** | Draft/brake references differ per car | 3-letter brand code (same `Brands.Code` as standings) |
| **Battle relevance** | The sim colors names by lap parity but shows nothing about *racing for position* | Same-class same-lap cars get a `▸` battle marker when within `BattleGapSec` |
| **Stable gaps** | The sim's est-time gap teleports when a car enters the pit stall or crosses S/F | est-time gap sanity-bounded by the distance×lap-time fallback (shared helper, see below) |

Kept from the sim because they work (users already speak this language):

- Row order = physical track order, player centered; ahead above, behind below.
- Lap-parity coloring of driver names, race only: **red** = a lap+ ahead of you (lapping you),
  **blue** = a lap+ down on you (you lap them), white = same lap, dimmed = pit road / towing.
- Fixed slot count (`CarsAhead`/`CarsBehind`, default 3+3): empty slots render blank instead of
  the window resizing, so the player row never jumps on screen.

## Gap math (shared with the traffic alerter)

`Data/RelativeGap.SignedSeconds(t, carIdx, refLap)` — positive = car is physically ahead:

1. `d = LapDistPct[car] − LapDistPct[player]`, wrapped to (−0.5, +0.5]. Base gap = `d × refLap`.
2. Refine with `CarIdxEstTime` (time to reach the current spot on the class reference lap —
   knows a hairpin from a straight): `est = EstTime[car] − EstTime[player]`, shifted by ±refLap
   to land nearest the base gap (S/F wrap), accepted only within `0.35 × refLap` of it
   (est time reads 0 in the pits and can tear mid-crossing).

The traffic detector calls the same helper with the **chaser's** class lap and negates
(its gaps are "behind" positive); the relative uses the **player's** class lap. One implementation,
two sign conventions — a fix in the gap math now improves both widgets.

Mixed-class caveat: `EstTime` is on each car's own class reference lap, so cross-class est
deltas carry a small systematic error; the 0.35-lap acceptance bound keeps it harmless (same
tradeoff the sim itself makes).

## Data flow

`RelativeBuilder.Build(RawTick, Roster, StintTracker, OverlayConfig)` — pure, same demo/live
pipeline as everything else — → `RelativeSnapshot` → `RelativeReady` event → `UI/RelativeWindow`.

- Candidates: everyone in the world (`TrackSurface ≠ −1`), not pace car/spectator. Pit-road cars
  stay listed (dimmed, PIT tag) — you still pass them physically.
- Sort by signed gap descending, take the nearest `CarsAhead` positives and `CarsBehind`
  negatives, pad missing slots with `RelativeRow.Blank`.
- Lap parity (race only): `Math.Round((carLap + carPct) − (playerLap + playerPct))` — a car
  0.95 total laps up that is 2 s behind you on track *is* lapping you; rounding handles that.
- Post-race freeze / disconnect mirror the standings window (`SessionState ≥ 5` handled at the
  source level, sources emit `RelativeSnapshot.Empty` on disconnect).

## Window

`UI/RelativeWindow` — third overlay window, identical plumbing to `TrafficWindow`: click-through
topmost, own `Relative.X/Y` config (default: bottom-right of the work area on first run),
edit-mode drag + persist, repaint only when `VisuallyEquals` says the snapshot changed,
sample rows rendered in edit mode.

Row layout (fixed-width grid, `FontSize` shared with standings):

```
P3 │ #12 │ FER │ Charles LeClerk      ▸ │ 4.2k │ 12 │ 1:42.301 │ ▼ │ -0.8
cls   num  brand  name (parity color)     iR    age    last     pace  gap
```

Gap column: ahead rows positive top-down decreasing, player row shows `—`, behind rows negative.
Gap color repeats the lap-parity language (red/blue/white), except battle-relevant cars where it
switches to the accent color — the actual fight pops; the ▸ battle marker sits after the name.

## Config (`Relative` section)

```jsonc
"Relative": {
  "Enabled": true,
  "CarsAhead": 3, "CarsBehind": 3,
  "ShowClassPos": true, "ShowBrand": true, "ShowIRating": true, "ShowLicense": false,
  "ShowStintAge": true, "ShowLastLap": true, "ShowPace": true,
  "BattleGapSec": 1.5,     // ▸ marker + white gap for same-class same-lap cars within this
  "GapPrecision": 1,
  "X": -1, "Y": -1         // -1 = auto bottom-right on first run; edit mode persists real values
}
```

`RacesOnly` is intentionally absent: a relative is useful in every session type; lap-parity
coloring and stint age simply stay neutral outside races.
