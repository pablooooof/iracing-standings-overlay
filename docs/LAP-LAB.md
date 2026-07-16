# Lap Lab — practice lap table

A practice/offline-testing tool for learning tracks fast: every lap is a row, the track's
official sectors are columns, and every gap is measured against a reference lap — so "where am
I losing time, and is it a technique problem or a consistency problem" is answerable at a
glance, live, without a Garage 61 / VRS round-trip. Design draft + full phase plan live in the
Claude artifact (2026-07-12); decisions recorded here as they ship.

Never shows in races by design. Testing/practice/qualifying only (`LapLabTracker` gates on
`StandingsSnapshot.KindOf`).

## Timing model (phase 1, shipped)

- Sector boundaries come from the session YAML `SplitTimeInfo.Sectors → SectorStartPct` — the
  same splits the sim scores. irsdkSharp's session model doesn't map that section, so
  `SectorClock.ParseBoundaries` regexes the raw YAML.
- iRacing broadcasts nobody's sector times. The player's are measured by `Data/SectorClock`:
  it samples `LapDistPct` + `SessionTime` (+ `PlayerTrackSurface`, `OnPitRoad`) on **every
  60 Hz frame** inside `IRacingSource.OnDataChanged`, *before* the UpdateHz stride gate —
  three scalar reads, races gated out via `_lapLabRace`. Crossing instants are linearly
  interpolated between the straddling samples: ~±2 ms, comfortably clean for 2-decimal gaps
  (at the 4 Hz snapshot rate it would be ±125 ms — don't move this sampling).
- A lap only counts when it starts AND ends with an S/F crossing while the clock is armed.
  Everything weird — join lap, tow, ESC to garage, **active reset** — simply disarms the
  clock until the next S/F, so garbage rows are structurally impossible.
- Active reset detection: a backward `LapDistPct` jump > 2% that is not an S/F wrap while the
  car stays in the world. A tow/garage exit goes through `TrackSurface == -1` instead — the
  two are never confused. Reset/tow abandons are logged (`lap lab: reset/teleport detected`).
- Clean-lap rules: off-track (`TrackSurface == 0`) or pit road (`OnPitRoad` / surface 1-2)
  during a lap marks the touched sector dirty and the lap `Off`/`Pit`; dirty laps still show
  (amber, with a reason chip in the Δ column: "off S2" / "pit") but are excluded from the
  session best and the optimal composite.

## References (phases 1–2)

- **Session best** (default): fastest clean lap; improving re-bases every visible row.
- **Session optimal**: best clean time per sector composited; used once every sector has been
  seen clean at least once (falls back to session best until then).
- **Previous best** (`Reference: "PreviousBest"`): your best clean lap from an earlier session
  at this car+track, auto-saved as `laps/prev_{car}_{trackid}_{config}.json` next to the exe
  (`SaveSessionBest`, on by default; only ever overwritten by a *faster* lap — the store
  re-checks disk before writing). In-memory it always means "before this session".
- **File** (`Reference: "File"` + `ReferenceFile`): any `.ibt` — yours from
  `Documents\iRacing\telemetry`, a teammate's, a Garage 61/VRS download. `Data/IbtLap` is a
  self-written ~250-line reader (header → var headers → embedded session YAML → row stream);
  it picks the fastest complete lap (clean preferred, teleports discarded) in two passes, so
  even a 200 MB endurance file parses in ~a second on the thread pool, and the result is
  cached as JSON (`laps/cache_*.json`, keyed by name+mtime) so re-selection is instant.

External references are stored as a **time-at-pct grid** (0.1%, `LapRef`, plus speed-at-pct
for phase 3) and sector times are derived at the LIVE session's boundaries (`SectorsFor`), so
sector definitions can differ between recordings without corrupting the columns. The
SectorClock records the same grids for every live lap (doubles internally — absolute session
time exceeds float precision late in long sessions).

## Conditions guard (phase 2)

An `.ibt` embeds the full session YAML; our own saves snapshot live conditions. `RefGuard.Diff`
compares reference vs live on every build: **BLOCK** (red chip, ref refused, falls back to
session best) on track/config or car mismatch; **WARN** (amber) on dry↔wet mismatch or track
temp Δ > `WarnTrackTempDelta` (default 4 °C); **INFO** (grey) on wind Δ > 4 m/s or rubber
state difference. Unknown fields never fire. Session best/optimal never warn by construction.

Phase 3: corner segmentation from the reference lap's speed trace + "biggest losses" panel.
Phase 4: active-reset run mode (rows = attempts between resets).

## Turn view (phase 3a)

`LapLab.View: "Turns"` swaps the sector columns for auto-detected turn zones. iRacing has no
corner channel — `Data/CornerMap` segments the **reference lap's speed trace** (smoothed
minima = apexes, prominence-gated ≥12% drop; apexes closer than 1.5% of a lap merge, so a
chicane/esses complex is ONE numbered T; boundaries at the fastest point between apexes).
Zone splits come from the recorded time-at-pct grids (`LapRef.SplitsOf`) — no extra 60 Hz
work. Segmentation source: the external ref's trace, else the session best's own; cached per
source so zones stay stable. Detection outside 4–24 zones (ovals, missing speed) falls back
to sectors with a log line. T-numbers are sequential per braking zone — close to the track
map until complexes merge. Off-track/pit attribution maps from the official sectors
(conservative amber). `--demo lab` feeds a synthetic 8-apex profile (`DemoSource.SynthSpeed`,
one mergeable pair → 7 zones).

## Display

`UI/LapLabWindow`, same plumbing as the fuel widget (click-through, own position, repaint on
`VisuallyEquals` change only). Newest lap on top. Colors: red = slower than ref, purple =
beat the ref sector, amber = dirty, green lap number + time = session best. Before any clean
lap exists, cells show absolute sector times instead of deltas.

Config: `LapLab` section — `Enabled`, `Decimals` (1-3, default 2), `MaxRows`, `Reference`
("SessionBest" | "SessionOptimal" | "PreviousBest" | "File"), `ReferenceFile`,
`SaveSessionBest`, `WarnTrackTempDelta`, `Scale`, `X/Y`. Settings window: "Lap Lab" section
(4-way reference picker + .ibt browse row).

## Demo & verification

`--demo lab` runs an "Offline Testing" session: scripted per-lap S2 variance (the middle
third is the deliberately weak, erratic sector — pattern in `DemoSource.LabLoss`), an
off-track every 8th lap (lap%8==4, pairs with the biggest S2 loss), and one active reset at
~112 s. The demo feeds the clock in 8 sub-steps per 4 Hz tick (motion is linear in time
within a step) to recover most of the 60 Hz interpolation accuracy.

Verify from `overlay.log`, not screenshots: every completed lap logs
`lap lab: L<n> [s1 s2 s3] = total (clean|off-track S2|pit|reset)` — splits must sum to the
total exactly (telescoping by construction); scripted lap times are 25 s ± the lab loss
pattern. Unit tests: `LapLabTests` (60 Hz exactness on the non-uniform TrackPct profile,
dirty-lap attribution, teleport/tow abandons, re-base, optimal composition, YAML parse).
