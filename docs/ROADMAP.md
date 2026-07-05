# Roadmap / idea backlog

Working v0.1 exists (live + demo). This is the improvement queue, roughly ordered.

## Near-term fixes & plumbing

- **Wire the `Show*` column toggles** — they exist in `config.json` but the XAML doesn't respect them yet.
- **Precision settings** — `GapPrecision`, `LapTimePrecision`, `DeltaPrecision` in config (currently hardcoded: gaps 1 decimal, laps 3, delta 1).
- **Fix snapshot dedup** — `StandingsSnapshot.Equals` compares the rows list by reference, so the "skip render when nothing changed" path never triggers. Compare row-by-row (records compare by value) to make idle cost truly ~zero.
- **Multiclass** — sort/position within class, class headers, `OtherClassesDriversAtTop` like iOverlay.
- **Per-car flag column** — telemetry `CarIdxSessionFlags` is a per-car bitfield: black flag (0x010000), DQ (0x020000), servicible, furled (0x080000), repair/meatball (0x100000). Show a colored dot/letter per car under penalty or with damage.
- **Live validation polish** — offline testing shows iRating 1 / license R 0.01 (iRacing reports those values in test sessions); hide IR/SoF when the field is all placeholder values.

## ⭐ Strategy inference per car (the "data project" phase)

All derivable from what we already sample — no extra telemetry cost:

- **Stint tracker**: detect pit-in/pit-out via `CarIdxOnPitRoad` transitions → per-car stint history (laps per stint, stint pace, pit stop duration, in/out laps).
- **Expected pit lap**: from their historical stint length, project the lap of their next stop; show "PIT ~L34".
- **Fuel-save vs push detection**: compare current stint's rolling pace to their own earlier stint pace / best. Consistently 1%+ slower mid-stint with clean air ≈ saving; tag 💧 or 🔥.
- **Stints-to-end / splash-and-dash**: laps remaining ÷ their typical stint length; if the final segment is a fraction of a stint (< ~15%), tag "splash" — they'll need a short stop at the end.
- **Driver pace tags**: pace percentile within class over last N laps vs their qualifying/iRating expectation → "fast"/"slow"/"inconsistent" tags (configurable thresholds).
- Endurance framing (Spa 24h etc.): all of the above becomes most valuable in long races; consider a wider "strategy" column set that only appears when session length > ~40 min.

## Nice-to-have UX

- **Settings window** (tray → Settings): GUI editor over `config.json` — same file, so hot-reload keeps working and the JSON stays the source of truth.
- **Laps-to-catch display** — already computed in `GapHistory.LapsToCatch`, not yet shown; could alternate with the Δ column or show on the car directly ahead/behind.
- **Driver tagging** (friends/rivals with colors), like iOverlay's module.
- **Relative overlay** — phase 2 widget, reusing the same data layer.

## Distribution

- `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true` → one exe, no .NET install needed for teammates.
- GitHub Release + a tiny CI workflow to build on tag.
- Expect SmartScreen "unknown publisher" on first run (unsigned binary); document the More info → Run anyway step. Code signing is overkill for a free tool.
