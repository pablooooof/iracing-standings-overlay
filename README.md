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

## Status

🔬 Research phase. See [`docs/RESEARCH.md`](docs/RESEARCH.md) for the survey of existing overlay apps (iOverlay, RaceLab, irDashies, iRon, …), the tech-stack options considered, and the architecture direction.

## License

[MIT](LICENSE)
