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

`Data/RelativeGap` — one phase model feeds the relative box, the traffic alerter and the
smoothed standings gaps, so they agree by construction (rewritten 2026-07-10 after the live
est/dist blend "breathed" with track section and fired phantom traffic alerts):

1. Each car reduces to a **lap phase** in [0,1): `CarIdxEstTime / CarClassEstLapTime` — how far
   around the lap it is in *time* terms. EstTime is the sim's own position→time curve for the
   class (knows a hairpin from a straight); dividing by the class lap removes the class scale so
   phases compare across classes. Same normalization irdashies uses; iRon's raw est delta is the
   single-class special case.
2. `SignedPhase(t, roster, a, b)` = wrapped phase delta in (−0.5, +0.5], positive = `a` ahead.
   This one signed number decides ahead/behind AND the magnitude for every widget.
3. Gap seconds = phase delta × **refLap of whichever car is closing**: the chaser's class lap
   for a car behind, the player's for a car ahead (both widgets use this same ruler rule).
4. `LapDistPct` is the per-car fallback, gated by *skew*: if a car's est phase strays more than
   0.12 laps from its distance pct, the est value is broken (pit/tow zeros read as ~half-lap
   skews) and pct wins; if either car of a pair falls back, both do, so a pair always lives in
   one domain. The two domains agree exactly at S/F, so fallbacks never tear the gap there —
   and a zero est *at the line* is correct data, deliberately not special-cased (demoting it
   to pct for a tick made the gap jump by the other car's skew and poisoned the closing rate).

Why not blend est with the distance estimate (the previous design)? The acceptance band
rejected the correct est gap whenever a slow section sat between the cars (est legitimately
exceeds the distance gap there), so the reported gap flip-flopped between domains as the pair
moved through the lap — breathing by ±2s and inventing 0.7+ s/s closing rates on stable gaps
(live, 2026-07-08/10). `SignedLaps` (pct domain) survives only for lap parity and coarse
windowing, where consistency with `Lap + LapDistPct` totals matters more than time accuracy.

Unit tests: `src/StandingsOverlay.Tests/RelativeGapTests.cs` (exactness across track sections,
cross-class normalization, S/F wrap, pit-zero fallback, skew gate).

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

## Status badges (shared model: `Data/CarStatus`)

Two channels, same data as the standings so a car never tells two stories:

- **Penalty** (race-control flags): DQ > BLK > DMG > WRN — rendered as the standings' drawn
  flag chip in "Text + flags" style, or as text in "Text" style.
- **State** (what the car is physically doing): TOW > SPUN > REJOIN > SLOW > SWAP > PIT >
  EXIT > OUT. TOW is transition-detected (`StintTracker.WasTowedIn`: the car materializes in
  its pit stall without ever reading "approaching pits" — towed cars teleport; race only) plus
  `PlayerCarTowTime` for the player's own row. The old "stopped >15 s ⇒ TOW" guess is gone —
  a long-parked car is SPUN, full stop.

`Relative.StatusStyle` ("Text" default here, denser) and the standings' root `StatusStyle`
("TextAndFlags" default) are independent. "Text" collapses both channels to one badge:
DQ > TOW > SPUN > REJOIN > SLOW > BLK > DMG > WRN > SWAP > PIT > EXIT > OUT (physical safety
states beat paperwork; DQ beats everything).

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
