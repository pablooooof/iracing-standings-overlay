# Roadmap / idea backlog

Working v0.1 exists (live + demo). This is the improvement queue, roughly ordered.

## Near-term fixes & plumbing

- [x] ~~Wire the `Show*` column toggles~~ (v0.3 — via `ColumnVisibility` DataContext)
- [x] ~~Precision settings~~ (v0.3 — `GapPrecision` / `IntervalPrecision` / `LapTimePrecision` / `DeltaPrecision`)
- [x] ~~Fix snapshot dedup~~ (v0.3 — row-by-row value compare in `OverlayWindow.SnapshotsEqual`)
- [x] ~~Multiclass~~ (v0.3 — class grouping, headers, per-class positions/gaps, `OtherClassesDriversAtTop`)
- [x] ~~Per-car flag column~~ (v0.3 — STATUS badge: DQ/BLK/DMG/WRN/PIT from `CarIdxSessionFlags`)
- **Live validation polish** — offline testing shows iRating 1 / license R 0.01 (iRacing reports those values in test sessions); IR/SoF now hidden when ≤1 but verify in an online race.
- **Online race validation** — gaps, laps-down, positions gained, strategy column against a real field.
- [x] ~~TOW state + status audit + fonts~~ (2026-07-10 — TOW is now transition-detected
  (`StintTracker.WasTowedIn`: car materializes in its stall without passing "approaching pits";
  + `PlayerCarTowTime` for the player) — the "stopped >15 s ⇒ TOW" guess Pablo kept seeing wrong
  is gone (parked = SPUN). Status split into two channels (`Data/CarStatus`): penalty flags vs
  physical state, so DMG-in-pit shows both; per-widget `StatusStyle` ("Text + flags" / "All text")
  in Settings for standings and relative independently. Fonts unified: one Segoe UI ramp
  (base/FontSm/FontXs from `FontSize`) across standings + relative, relative's hidden −1.5px
  offset and Consolas cells removed, its default Scale now 1.0.)
- [x] ~~Relative/traffic gap accuracy~~ (2026-07-10 — `RelativeGap` rewritten as a lap-phase
  model: `CarIdxEstTime / CarClassEstLapTime` per car, wrapped delta × chaser's lap, skew-gated
  pct fallback; the old est/dist blend breathed with track section → phantom 0.7 s/s closing
  rates → random traffic pop-ups + 99.9 countdowns, seen live 07-08/07-10. Both boxes now share
  one phase number; first unit-test project added (`src/StandingsOverlay.Tests`, 12 tests) and
  the demo track got a non-uniform speed profile so est≠pct divergence is exercised offline.
  **Validate next live session**: relative should track the sim's F3 box within ~0.1 s for
  same-class cars; traffic rates in the log should read 0.05–0.25 s/s, not 0.7+.)

## ⭐ Strategy inference per car (the "data project" phase)

v0.3 shipped the core (`StintTracker`): stint history from `CarIdxOnPitRoad` transitions, per-car
lap times, positions gained vs grid, **PIT column** (`~34` expected next pit lap · `34!` overdue ·
`0stp` no stop needed · `~34*` = that stop is the final splash; the old `Nstp` stop counts were
dropped 2026-07-11 — useless in endurance), **PACE column** (▲/▼ vs class
median over last 5 laps, `S` = fuel-saving: consistent laps ≥1.5% off own best with <1% spread).

Still open:
- **Pit stop duration tracking** → [x] shipped as `PitInfo` (total / stationary / drive-through)
  and the pit-time columns; still todo: *classify* splash vs full stop vs repair from those times.
- **Stint pace decay** → tire deg estimate per car; who's managing, who's dying at stint end.
- **Confidence display** — strategy guesses from 1 stint are weak; dim until 2+ stints observed.
- **Endurance mode**: auto-widen strategy columns when session length > ~40 min; driver-swap
  detection for team events (roster changes mid-session).
- **Driver consistency tag**: lap time variance percentile within class.

## ⛽ Player fuel calculator & endurance strategy (v0.6, spec: docs/FUEL-STRATEGY.md)

Shipped: `FuelModel` (per-lap fuel sampling with green/yellow/in-out classification, learned
fill rate + pit lane loss), `StrategyPlanner` (stops × min-save enumeration; push-plus-splash
vs save-a-stop with projected Δ), `FuelWindow` (live numbers + Pirelli-style stint bars).

Follow-ups:
- **Auto-set pit fuel** — broadcast `PitCommand_Fuel` so the planned fill is preloaded in the
  black box (read `PitSvFuel` back to confirm); opt-in config.
- **Yellow-aware projection** — during a caution, project the current stint with the yellow
  consumption EWMA instead of waiting for re-plan on green.
- **Unit display** — gallons / kWh label from the session (math already unit-agnostic).
- **Save-level calibration** — learn MaxSave/penalty from the player's own observed saving
  laps instead of config constants.
- [x] **Fuel-saving strategy surfacing** — the fuel widget shows the fork's exact per-lap save as
  an action: "save 0.14/lap → 2 stops (38s faster)" / "save X/lap to skip the stop" in a sprint.

## Sector mini-bar (idea, evaluated 2026-07)

A small horizontal bar per row split by sector, colored improved/personal-best/class-best.
What the SDK gives us: sector *boundaries* are in the session YAML (`SplitTimeInfo.Sectors`,
`SectorStartPct`), but iRacing does **not** broadcast other cars' sector times. They can be
measured ourselves from `CarIdxLapDistPct` crossing a boundary: at 60 Hz sampling that's ±17 ms
accuracy (fine), at our 4 Hz snapshot rate ±125 ms (too coarse). Plan: watch boundary crossings
inside the 60 Hz `OnDataChanged` handler (cheap compares only, no allocation), keep per-car
sector history, render in the existing cells pipeline. Player sectors are exact. Do after the
strategy phase.

## Nice-to-have UX

- **Settings window** (tray → Settings): GUI editor over `config.json` — same file, so hot-reload keeps working and the JSON stays the source of truth.
- **Laps-to-catch display** — already computed in `GapHistory.LapsToCatch`, not yet shown; could alternate with the Δ column or show on the car directly ahead/behind.
- **Driver tagging** (friends/rivals with colors), like iOverlay's module.
- [x] ~~Relative overlay~~ (v0.5 — `RelativeBuilder`/`RelativeWindow`, shared `RelativeGap`
  helper with the traffic alerter; spec in `docs/RELATIVE.md`)

## Lap Lab (practice lap table, phased — design draft 2026-07-12)

Offline-testing/practice tool: every lap a row, official sectors as columns, gaps vs a
reference lap; corner-level "biggest losses" panel later. Spec: `docs/LAP-LAB.md`.

- [x] **Phase 1 (2026-07-13)** — `SectorClock` (60 Hz player sampling, ±2 ms splits, active-reset/
  tow disarm), `LapLabTracker` (session best + optimal refs, clean-lap rules), `LapLabWindow`,
  `LapLab` config + settings section, `--demo lab`, unit tests.
- [ ] **Phase 2** — `.ibt` reference import (embedded YAML → conditions BLOCK/WARN/INFO chips) +
  auto-saved previous-session best per car+track.
- [ ] **Phase 3** — corner segmentation from the reference speed trace (minima = apexes, merge
  <1.5%) + "biggest losses" panel with erratic/repeatable spread badges.
- [ ] **Phase 4** — active-reset run mode: rows = attempts between resets, span timing, delta
  vs best run. (.blap/.olap parsing cut: proprietary format; G61 API pull = v2 candidate.)

## Live-iteration backlog (2026-07)

Ideas and requests captured during rapid iteration so nothing is lost. Roughly ordered.

**Traffic alerter**
- [x] **"You're lapping a slower car" alert @ 5s** — `IsLapping` rows (green), fired on raw gap
  (`Traffic.LapTrafficGapSec`, default 5s) while closing; toggle `Traffic.WarnLapping`.

**Standings / relative**
- [x] **Smooth GAP/INT** (`SmoothGaps`, default on) — gap-to-leader is the cumulative sum of
  adjacent `CarIdxEstTime` intervals (continuous like the relative); laps-down still "NL".
- [x] **Pit-exit badge (~15s)** — the first ~15s out of the pits reads a bright `EXIT`, distinct
  from the steady whole-out-lap `OUT`. (Bright badge, not a per-row blink — an ItemsControl
  restarts row animations on every repaint.)
- [x] **Hide parked / no-driver cars in relative** (`Relative.HideParkedCars`) — drops cars sat
  in the pits >60s (heuristic; iRacing has no "no driver" var).
- [x] **Driver-change alert (endurance teams)** — `DriverSwapTracker` tags a car `SWAP` (purple)
  for 60s when its driver name changes across a YAML reparse.
- [x] **Show top-N when leading** (`MinLeadingCars`, default 10) — near the front, show at least
  the top N instead of a tiny window around you.
- [x] **Stint laps** in the relative — laps into the car's current stint (blank when unknowable:
  mid-race join before its first observed stop). Was briefly a stint *number* (`ST1`/`ST2`);
  reverted 2026-07-11 — lap count is the tyre-age answer the column exists for.
- [x] **Endurance pit hardening (2026-07-11)** — teleport arrivals (tow / team-driver reconnect)
  no longer count as pit stops for stint stats or P·LAP/P·TOT timing; per-car pace resets on
  driver swap; traffic catch rate reads from the standings delta history (median-filtered
  avg of last 5 clean laps); phase wrap-seam guard kills the phantom 0.1 s blue alerts.
- [x] **Pinned tow rows (2026-07-11)** — a towed player-class car shows in the standings at its
  live position even outside the window (`PinTowedCars`); the row removes itself when the car
  drives out of its stall.
- [x] **Relative position = standings position (2026-07-11)** — relative rows rank by the same
  live total-distance ordering as the standings instead of scored `CarIdxPosition` (which only
  updates at timing lines, so the widgets disagreed for most of a lap after an overtake).
- [x] **Driving vs spectate profiles (2026-07-11)** — `config.spectate.json` swaps in while the
  player is out of the car (`IsOnTrack`, 5 s debounce, tow-guarded): every setting including
  widget positions can differ. Cloned from the driving profile on first use (seeded with a
  wider standings view); tune it live while spectating, edits persist to the active profile.
- [x] **Tire-change inference (2026-07-11, `InferTireChanges`)** — no SDK channel exists for
  opponents' tire sets, but under fuel-and-tires-separate rules service is sequential: a tire
  stop sits ~10s+ longer than the car's own fuel-fill baseline (cheapest observed sec/stint-lap).
  Relative shows `ST8+` = last stop took no tires (double-stint tell); fresh-green keys off
  inferred tire age. Swap stops excluded (driver-change overhead looks like tire time);
  unknown ⇒ assume fresh (quiet failure). Known limit: a car whose observed stops ALL took
  tires has a contaminated baseline until its first fuel-only stop.
- [x] **Restart survival (2026-07-11)** — `session-state.json` next to the exe: GapHistory +
  StintTracker durables saved every 30 s, restored when reattaching to the same SubSessionID +
  SessionNum (session-clock guard rejects restarted races). Time-anchored transients
  deliberately not persisted. Follow-up: persist FuelModel learning too.
- [x] **Pit-area status fixes (2026-07-11)** — SPUN/REJOIN/SLOW suppressed in the pit area
  (stall + entry/exit lanes; the pit limiter used to read SLOW); traffic alerts suppressed on
  the pit entry/exit roads, not just between the cones; driver-change blink no longer reads TOW
  nor splits the pit visit; positions gained shows +N/−N; relative
  stint column reads `ST17`.
- [x] **Traffic row UX v2 (2026-07-11)** — headline chip is the class position ("P4"), car
  number rides in the subtext ("GTP · #07"); the s/lap number is replaced by the relative's
  ▲/►/▼ pace arrow (same thresholds/colors, chevrons keep encoding catch intensity); the
  imminent pulse is subtler (0.15→0.6) and typed — blue rows pulse BLUE, so the pulse never
  hides which alert it is. Meatball (repair) stops excluded from tire inference — the jack
  lift itself is not in the SDK, so repair time would read as tires.
- [x] **Fuel plan wall clock (2026-07-11)** — the plan line ends with "box HH:mm:ss GMT"
  (or "flag … GMT" when no stops remain): the projected current-stint end from pace +
  strategy, anchored at the lap-crossing replan.
- [x] **Tyre-age column (2026-07-11, `ShowTyreAge`, race default on)** — laps on the current
  tires next to the compound ring, with a superscript stint count on that rubber: `42²` =
  double-stinting, `42³` = triple (green ≤3 laps, amber when multi-stinted). The relative's
  stint column carries the same superscript (`ST8²`). Backed by `StintTracker.TireInfo`
  (TireStints counter, persisted in session state).
- ~~Class-colour override map~~ — dropped (live sessions use iRacing's own class colours).

**Tyres / weather**
- [x] **Inline `o→o` tyre-switch marker** (both widgets) + `TyreSwitchDisplay` toggle
  (Flash / Inline / Both), gated on `TyreSwitchAlertSec`.
- [x] Tyre-switch flash duration configurable (`TyreSwitchAlertSec`, default 30s).
- [x] Trend arrows latch until the trend reverses (not per-sample blink).

**Fuel**
- [x] **Fuel-to-the-flag number** — `finish on X L · N L in hand / carrying extra` so a sprint
  tells you exactly what to fuel and flags over-fuelling (dead weight). Amber when >1.5 laps over.

**Status states** — [x] TOW (heuristic), [x] REJOIN (experimental toggle), [x] offline dim name.

**Settings window**
- [x] Expose the newer options in the GUI: real clock, name-column width, header font size,
  smooth gaps, rejoin toggle, tyre-switch duration, track-temp decimals, abbreviate-wetness,
  relative tyre ring + hide-parked, traffic lapping alert, and the pit-time columns.

**Pit-time columns** (see also "Pit stop duration tracking" above)
- [x] Toggleable race columns: pit lap + total / drive-through / stationary pit time.

## Status badge legend

| Badge | Meaning |
|---|---|
| `PIT` | on pit road |
| `EXIT` | just left the pits (~15s) — bright, to catch the eye |
| `OUT` | on the out-lap (cold tyres) — the rest of the lap after a pit exit |
| `SWAP` | team driver change — a new driver just took over (60s) |
| `SPUN` | stopped on the racing surface (likely to recover) |
| `TOW` | stopped off-track, or stuck >15s — a tow is coming |
| `REJOIN` | was stopped, moving again (spin recovery); experimental, `ShowRejoinState` |
| `SLOW` | moving but crawling well below class pace (limping/damaged); experimental, `ShowRejoinState` |
| `DQ` / `BLK` | disqualified / black flag |
| `DMG` | meatball — car damaged, must pit for repair |
| `WRN` | furled black flag — a warning (repair/behaviour) before a full black flag |
| *(dim name)* | offline / not in the world (disconnected or retired) |

## Known SDK limits

- **No weather forecast.** iRacing's telemetry and session YAML expose only *current* conditions
  (`Precipitation`, `TrackWetness`, `WeatherDeclaredWet`, temps, wind). There is **no** forward
  forecast (rain chance next 15/60 min) in the SDK — the in-sim forecast panel isn't published.
  Our substitute is the trend arrow (rising/falling from our own sampled history). Confirmed
  against the irsdk telemetry + WeekendInfo references, 2026-07. Do not re-attempt without new SDK.
- **Class colours** come from iRacing's session YAML per class (live); the demo uses convention
  colours (GTP yellow / GT3 pink / GT4 blue) only as stand-ins.
- **No "driver in car" var** — parked/no-driver detection must be heuristic (pit stall + stationary).

## Distribution

- `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true` → one exe, no .NET install needed for teammates.
- GitHub Release + a tiny CI workflow to build on tag.
- Expect SmartScreen "unknown publisher" on first run (unsigned binary); document the More info → Run anyway step. Code signing is overkill for a free tool.
