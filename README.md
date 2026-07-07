# iRacing Standings Overlay

A minimal, efficient **race-craft overlay** for [iRacing](https://www.iracing.com/) — a standings leaderboard plus a small set of focused widgets (relative box, traffic alerter, fuel strategy). Local-only, open source, no accounts, no telemetry leaving your machine.

## Goals

- **Few widgets, done well** — standings, relative, traffic alerts, fuel strategy. No dashboard suite, no browser skins.
- **Lightweight** — negligible CPU/GPU impact, tiny memory footprint. It must never affect frame times in the sim.
- **Safe by design** — reads iRacing's public shared-memory telemetry (the official SDK mechanism). It is a separate process that never touches the game, so it *cannot* crash iRacing.
- **Local & free** — no server, no login, no monetization.

## How it works (high level)

iRacing publishes live telemetry to a memory-mapped file (`Local\IRSDKMemMapFileName`) at 60 Hz, plus session info (driver list, class info, results) as a YAML blob. The overlay:

1. Attaches to that shared memory (read-only).
2. Builds display-ready snapshots (standings, relative, traffic, fuel) each tick.
3. Renders them in borderless, transparent, click-through, always-on-top windows over the sim — each widget independently positionable, repainting only on change.

iRacing must run in **windowed borderless** mode (standard requirement for every overlay app).

## The widgets

### Standings — headline feature: multi-lap delta

Beyond the usual columns (position, car number, iRating, gap, interval, last lap), the standings show **Δ over the last N laps** for every car — how much you gained (▼ green) or lost (▲ red) on them over a configurable window, not just the last lap. Gap history is sampled once per lap crossing, so the column costs nothing.

Each session type has its own column set (configurable per type in `config.json`):

- **Race** — position, ± vs grid, gap/interval, last lap, **Δ per-lap gap columns**, **PIT** strategy per car (`~34` expected pit lap · `34!` overdue · `0stp` no stop needed · `2stp*` splash-and-dash at the end), **PACE** (▲/▼ vs class, `S` = fuel-saving), status badges (`PIT`/`DMG`/`BLK`/`WRN`/`DQ`/`SPUN`).
- **Qualifying** — best lap, gap/interval to class pole, one column per quali lap (purple = class best, green = personal best).
- **Practice** — laps count, best, last, gap to session best.

Multiclass fields group by class with colored headers.

### Relative box

Track-order window around you (3+3 fixed slots, the sim's F3 box language), plus what the sim doesn't tell you: last lap + pace vs you, stint/tyre age, class position, car brand, and a ▸ battle marker for cars actually racing you for position. Spec: [`docs/RELATIVE.md`](docs/RELATIVE.md).

### Traffic alerter

Multiclass situational awareness: faster-class cars closing on you (WATCH → IMMINENT countdown with time-to-arrival and closing rate, ×N trains) and blue-flag warnings when you're about to be lapped — with synthesized audio cues and an ALONGSIDE banner. Two styles: stacked rows or a compact beacon. Spec: [`docs/TRAFFIC-ALERTER.md`](docs/TRAFFIC-ALERTER.md).

### Fuel calculator & endurance strategy

Live fuel numbers (level, per-lap burn, laps in tank, target consumption) measured from your own lap crossings, plus an endurance **strategy planner** that projects the rest of the race as Pirelli-style stint bars — up to three competing strategies side by side, e.g. *"push + splash-and-dash at the end"* vs *"save 0.14 L/lap, one stop fewer"*, with the projected time delta between them. Learns your pit lane loss and refuel rate from real stops. Spec: [`docs/FUEL-STRATEGY.md`](docs/FUEL-STRATEGY.md).

## Tech stack

- **.NET 10 / C# / WPF** — native transparent, click-through, always-on-top windows. No Electron, no browser engine competing with the sim for GPU.
- **[irsdkSharp](https://github.com/SlevinthHeaven/irsdkSharp)** (MIT) — reads the shared-memory telemetry; waits on iRacing's data-valid event instead of polling.
- Snapshots are built at a configurable rate (default 4 Hz — standings don't change at 60 Hz) and the UI only repaints on change.

## Build & run

```powershell
dotnet build src/StandingsOverlay -c Release
# demo mode — fake three-class 20-car field, no iRacing needed:
src/StandingsOverlay/bin/Release/net10.0-windows/StandingsOverlay.exe --demo          # race
src/StandingsOverlay/bin/Release/net10.0-windows/StandingsOverlay.exe --demo qual     # qualifying
src/StandingsOverlay/bin/Release/net10.0-windows/StandingsOverlay.exe --demo practice
# real mode — just run it, it waits for iRacing:
src/StandingsOverlay/bin/Release/net10.0-windows/StandingsOverlay.exe
```

## Configuration

A `config.json` is created next to the exe on first run and **hot-reloads** on save: positions, colors, opacity, font size, precisions, `DeltaLaps`, compact-layout counts, `UpdateHz`, per-session column toggles, and the `Relative` / `Traffic` / `Fuel` sections.

The tray icon is the control surface: **Edit mode** (drag every widget into place, positions saved), **Start with Windows**, **Exit**. Its tooltip shows whether the app is connected to iRacing or waiting.

## Status

✅ Working: standings (multiclass, per-session columns, multi-lap delta, per-car strategy inference), relative box, traffic alerter with audio, fuel strategy planner — all testable offline via `--demo`. Validated live in offline sessions; broader online-race validation ongoing. See [`docs/RESEARCH.md`](docs/RESEARCH.md) for the survey of existing overlay apps, [`docs/REQUIREMENTS.md`](docs/REQUIREMENTS.md) for the feature rationale, and [`docs/ROADMAP.md`](docs/ROADMAP.md) for what's next.

## License

[MIT](LICENSE)
