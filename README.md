# iRacing Standings Overlay

A minimal, efficient **standings overlay** for [iRacing](https://www.iracing.com/) — the classic top-left leaderboard showing positions, gaps, iRating, and pit status. Local-only, open source, no accounts, no telemetry leaving your machine.

## Goals

- **One widget, done well** — standings only. No relative, no fuel calculator, no dashboard suite.
- **Lightweight** — negligible CPU/GPU impact, tiny memory footprint. It must never affect frame times in the sim.
- **Safe by design** — reads iRacing's public shared-memory telemetry (the official SDK mechanism). It is a separate process that never touches the game, so it *cannot* crash iRacing.
- **Local & free** — no server, no login, no monetization.

## How it works (high level)

iRacing publishes live telemetry to a memory-mapped file (`Local\IRSDKMemMapFileName`) at 60 Hz, plus session info (driver list, class info, results) as a YAML blob. The overlay:

1. Attaches to that shared memory (read-only).
2. Builds the standings model (positions, gaps, laps, iRating, pit status) each tick.
3. Renders it in a borderless, transparent, click-through, always-on-top window over the sim.

iRacing must run in **windowed borderless** mode (standard requirement for every overlay app).

## Headline feature: multi-lap delta

Beyond the usual columns (position, car number, iRating, license, gap, interval, last lap), the standings show **Δ over the last N laps** for every car — how much you gained (▼ green) or lost (▲ red) on them over a configurable window, not just the last lap. Gap history is sampled once per lap crossing, so the column costs nothing.

## Tech stack

- **.NET 10 / C# / WPF** — native transparent, click-through, always-on-top window. No Electron, no browser engine competing with the sim for GPU.
- **[irsdkSharp](https://github.com/SlevinthHeaven/irsdkSharp)** (MIT) — reads the shared-memory telemetry; waits on iRacing's data-valid event instead of polling.
- Snapshots are built at a configurable rate (default 4 Hz — standings don't change at 60 Hz) and the UI only repaints on change.

## Build & run

```powershell
dotnet build src/StandingsOverlay -c Release
# demo mode — fake 20-car race, no iRacing needed:
src/StandingsOverlay/bin/Release/net10.0-windows/StandingsOverlay.exe --demo
# real mode — just run it, it waits for iRacing:
src/StandingsOverlay/bin/Release/net10.0-windows/StandingsOverlay.exe
```

A `config.json` is created next to the exe on first run and **hot-reloads** on save: position, colors, opacity, font size, `DeltaLaps` (the Δ window), `DriversAtTop` / `DriversAheadBehind` (compact layout), `UpdateHz`, and per-column toggles.

Use the tray icon → **Edit mode** to drag the overlay into place (position is saved), and → **Exit** to quit.

## Status

✅ Working v0.1: live + demo telemetry sources, compact standings (top N + window around you), multi-lap delta, config hot-reload, tray control. See [`docs/RESEARCH.md`](docs/RESEARCH.md) for the survey of existing overlay apps and [`docs/REQUIREMENTS.md`](docs/REQUIREMENTS.md) for the feature rationale. Not yet validated in a live iRacing session — that's the next step.

## License

[MIT](LICENSE)
