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

## ⭐ Strategy inference per car (the "data project" phase)

v0.3 shipped the core (`StintTracker`): stint history from `CarIdxOnPitRoad` transitions, per-car
lap times, positions gained vs grid, **PIT column** (`~34` expected pit lap · `34!` overdue ·
`0stp` no stop needed · `2stp*` = final stop is a splash-and-dash), **PACE column** (▲/▼ vs class
median over last 5 laps, `S` = fuel-saving: consistent laps ≥1.5% off own best with <1% spread).

Still open:
- **Pit stop duration tracking** → distinguish splash vs full stop vs repair from time stationary.
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

## Live-iteration backlog (2026-07)

Ideas and requests captured during rapid iteration so nothing is lost. Roughly ordered.

**Traffic alerter**
- [ ] **"You're lapping a slower car" alert @ 5s** — mirror of the blue-flag branch: warn when
  the player is closing on slower/lapped traffic *ahead* they're about to put a lap down. New
  `IsLapping` row type + rendering. (Chosen semantics: you lapping them, not you un-lapping.)

**Standings / relative**
- [ ] **Smooth GAP/INT** — option to compute standings gap/interval from the relative's
  continuous `CarIdxEstTime` method (`RelativeGap`) instead of `CarIdxF2Time`, config-toggled.
- [ ] **Pit-exit flash (~15s)** — flash the out-lap badge for the first ~15s after pit exit,
  distinct from the steady whole-out-lap `OUT`.
- [ ] **Hide parked / no-driver cars in relative** — config to drop cars parked in the pit stall
  (stationary, likely DNF / no driver) — cuts noise in practice and endurance. iRacing has no
  clean "no driver" var; heuristic = in pit stall + stationary a long time.
- [ ] **Driver-change alert (endurance teams)** — detect a car's current driver changing on YAML
  reparse (name / UserID) → a 60s tag/alert. Roster already reparses on `SessionInfoUpdate`.
- [ ] **Class-colour override map** — optional user map (GT3=pink, LMP2=blue, GTP=yellow…) that
  overrides iRacing's own class colours when you prefer a convention.

**Tyres / weather**
- [ ] **Inline `o→o` tyre-switch marker** in the row (both widgets) + Settings toggle
  (flash / inline / both) + configurable lifetime (until next stop / one lap / N sec).
- [x] Tyre-switch flash duration configurable (`TyreSwitchAlertSec`, default 30s).
- [x] Trend arrows latch until the trend reverses (not per-sample blink).

**Fuel**
- [x] **Fuel-to-the-flag number** — `finish on X L · N L in hand / carrying extra` so a sprint
  tells you exactly what to fuel and flags over-fuelling (dead weight). Amber when >1.5 laps over.

**Status states** — [x] TOW (heuristic), [x] REJOIN (experimental toggle), [x] offline dim name.

**Settings window**
- [ ] Expose the newer options in the GUI: real clock, name-column width, header font size,
  rejoin toggle, tyre-switch duration, abbreviate-wetness, relative tyre ring, etc.

**Pit-time columns** (see also "Pit stop duration tracking" above)
- [ ] Toggleable race columns: pit lap + total / drive-through / stationary pit time.

## Status badge legend

| Badge | Meaning |
|---|---|
| `PIT` | on pit road |
| `OUT` | on the out-lap (cold tyres) — the whole lap after a pit exit |
| `SPUN` | stopped on the racing surface (likely to recover) |
| `TOW` | stopped off-track, or stuck >15s — a tow is coming |
| `REJOIN` | was stopped, moving again (spin recovery); experimental, `ShowRejoinState` |
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
