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

## Nice-to-have UX

- **Settings window** (tray → Settings): GUI editor over `config.json` — same file, so hot-reload keeps working and the JSON stays the source of truth.
- **Laps-to-catch display** — already computed in `GapHistory.LapsToCatch`, not yet shown; could alternate with the Δ column or show on the car directly ahead/behind.
- **Driver tagging** (friends/rivals with colors), like iOverlay's module.
- **Relative overlay** — phase 2 widget, reusing the same data layer.

## Distribution

- `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true` → one exe, no .NET install needed for teammates.
- GitHub Release + a tiny CI workflow to build on tag.
- Expect SmartScreen "unknown publisher" on first run (unsigned binary); document the More info → Run anyway step. Code signing is overkill for a free tool.
