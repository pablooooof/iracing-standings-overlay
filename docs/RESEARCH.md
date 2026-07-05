# Research: how iRacing overlay apps work

*Last updated: 2026-07-04*

## 1. The data source — there is only one

Every overlay app (commercial or open source) gets its data the same way: **iRacing's official SDK mechanism**, a memory-mapped file in shared memory.

- Shared memory name: `Local\IRSDKMemMapFileName`, with a `Local\IRSDKDataValidEvent` event you can wait on instead of polling.
- **Telemetry variables** (car positions, lap times, gaps, pit road status, etc.) update at **60 Hz**. iRacing writes into a ring of 4 buffers and bumps an index in the header, so readers always get a consistent snapshot without locks.
- **Session info** (driver roster, iRating, licenses, class info, session results/positions) is a **YAML string** that updates infrequently (roster changes, session transitions). This is where most standings data actually comes from; telemetry fills in the live bits (gaps, on-track state).
- Multiple readers can attach simultaneously — running our overlay next to SimHub/JRT/etc. is fine.
- **This is read-only and out-of-process.** Nothing is injected into the game. An overlay bug can crash the overlay, never the sim. The only "interaction" with the game is compositing a window on top of it.

Practical notes learned from existing apps' FAQs:
- iRacing must run **windowed borderless** (fullscreen exclusive gives the GPU the whole screen; nothing can composite on top).
- iRacing setting `max cars` (in app.ini / graphics options) controls how many cars appear in telemetry — users should set 63 for full fields.
- Overlay stutter reports in iOverlay/Kapps are almost always Chromium hardware-acceleration conflicts (their FAQs literally recommend turning HW acceleration off) — an argument against browser-based rendering for us.

## 2. Survey of existing apps

| App | Stack (observed/inferred) | Notes |
|---|---|---|
| [iOverlay](https://ioverlay.app/) | Closed source; Chromium-based rendering (FAQ mentions hardware-acceleration toggles, VC++ redistributable) | Free, popular. Standings + relative + fuel + inputs. Stutter fixes = disable HW accel → browser engine under the hood. |
| [RaceLab](https://racelab.app/) | Closed source; Electron-style app | Big suite, streaming focus, "zero performance impact" marketing but known to be heavy in practice. |
| [Kapps](https://kapps.kutu.ru/) | Closed source; same author as **pyirsdk** (kutu). Chromium rendering (HW-accel toggle in settings) | Interesting lineage: the most-used Python SDK binding comes from this author. |
| [irDashies](https://github.com/tariknz/irdashies) | **Electron + React + TypeScript**, native C++ node module for the SDK. MIT, actively maintained | Best modern open-source reference. Nice trick: overlay windows are sized to fit each widget (not full-screen transparent windows) to cut GPU compositing cost. Very feature-rich standings widget (multiclass, flags, iRating, deltas, column config). |
| [iRon](https://github.com/lespalt/iRon) | **C++ / Direct2D**, zero dependencies, single exe. MIT, maintenance mode since 2023 | The efficiency benchmark. Includes a standings overlay. `config.json` hot-reloaded. Closest in spirit to this project. |
| [iFL03](https://github.com/SemSodermans31/iFL03) | C++ (iRon lineage), modernized visuals | Lightweight, maintained. |
| [SharpOverlay](https://github.com/TiberiuC39/SharpOverlay) | **C# / .NET + WPF** | Middle ground: managed language, native window transparency. |
| [rah-iracing-overlay](https://github.com/RaulArcos/rah-iracing-overlay) | Python backend + web frontend (OBS-source style) | The "data person" pattern: Python reads SDK, serves HTML; render in browser/OBS. |
| SimHub (bo2 overlays) | C# app, overlays as its own rendering + web | General sim telemetry swiss army knife, not iRacing-specific. |

## 3. SDK bindings by language

| Language | Library | Notes |
|---|---|---|
| Python | [pyirsdk](https://github.com/kutu/pyirsdk) | De-facto standard, simple API (`irsdk.IRSDK()`, `ir['CarIdxPosition']`, `ir['DriverInfo']`). Battle-tested (Kapps author). |
| C# | [IRSDKSharper](https://github.com/mherbold/IRSDKSharper) | Modern, fast rewrite; fixes known iRacing SDK quirks (broken YAML, wrong var types). Active. |
| Node.js | [node-irsdk](https://github.com/apihlaja/node-irsdk) / irsdk-node forks | Used by irdashies (they vendor their own native binding). |
| C++ | Official headers (`irsdk_defines.h`) | What iRon uses directly. |
| Rust | [iracing](https://docs.rs/crate/iracing) crate | Exists; less mature ecosystem. |

Official SDK docs (community-mirrored): https://sajax.github.io/irsdkdocs/

## 4. The overlay window itself

Two rendering philosophies:

**A. Native transparent window** (iRon, SharpOverlay, iFL03)
- `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_NOACTIVATE` window, per-pixel alpha, click-through.
- Render with Direct2D/DirectWrite (C++) or WPF `AllowsTransparency` (C#).
- Cost: a few MB RAM, ~0% GPU for a widget that repaints a small region a few times per second.

**B. Browser-engine window** (iOverlay, RaceLab, Kapps, irdashies)
- Electron/CEF transparent frameless window, HTML/CSS rendering.
- Pro: trivial styling/iteration, web skills transfer directly.
- Con: 150–400 MB RAM, a Chromium compositor fighting the sim for GPU; this is the source of every "overlay stutters my stream" FAQ entry. Mitigations exist (small windows per widget à la irdashies, disable HW accel) but it's inherently the heavy path.

Key efficiency lessons regardless of path:
- **Don't repaint at 60 Hz.** Standings meaningfully change a few times per second at most (gaps) or per lap (positions). Repaint only when the rendered model actually changed; 2–4 Hz is plenty, and diff-checking makes idle cost ~zero.
- **Size the window to the widget**, not the screen.
- **Wait on the SDK's data-valid event** instead of busy-polling.
- Parse session-info YAML only when its update counter changes (it's a big string; reparsing it at 60 Hz is the classic rookie CPU sink).

## 5. Stack options for this project

| Option | Efficiency | Dev speed | Fit |
|---|---|---|---|
| **C# + IRSDKSharper + WPF (or Win2D)** | ★★★★ (~30–60 MB, negligible CPU/GPU) | ★★★★ | **Recommended.** Native transparency is first-class, DirectWrite text quality, one modern language, easy JSON config, ships as one exe. |
| C++ + Direct2D (iRon model) | ★★★★★ | ★★ | Maximum efficiency, but slow iteration on layout/styling for a fun project. Great reference code to steal patterns from (MIT). |
| Python (pyirsdk) + native rendering | ★★★ | ★★★ | pyirsdk is lovely for the data side, but Python has no good native transparent-overlay rendering story (tkinter/pygame overlays are hacky). |
| Python backend + browser/OBS source | ★★★ | ★★★★ | Fine if the overlay lives in OBS for streaming; wrong shape for an on-screen-while-driving overlay. |
| Electron + React (irdashies model) | ★★ | ★★★★★ | Best styling ergonomics, heaviest runtime. Overkill for a single widget. |

### Recommendation

> **Update (2026-07-04):** implemented with **irsdkSharp** instead of IRSDKSharper — IRSDKSharper turned out to be GPL-3.0, which conflicts with this repo's MIT license. irsdkSharp is MIT and its performance is more than sufficient at our snapshot rate.

**C# (.NET 8) + IRSDKSharper + a transparent WPF window**, borrowing behavioral patterns from iRon (config hot-reload, edit-mode for dragging position) and feature ideas from irdashies' standings widget (multiclass colors, iRating, gaps, pit status). Single ~few-MB exe, no runtime bloat, and C# is close enough to "programming and data" skills to stay fun.

Fallback/alternative if we want web-style iteration later: keep the data layer separate (a small standings-model library) so the renderer can be swapped.

## 6. Standings widget — data checklist

From telemetry (60 Hz): `CarIdxPosition`, `CarIdxClassPosition`, `CarIdxLap`, `CarIdxLapDistPct`, `CarIdxF2Time` (gap/interval), `CarIdxOnPitRoad`, `CarIdxLastLapTime`, `CarIdxBestLapTime`, `SessionState`, `SessionFlags`.
From session YAML: `DriverInfo.Drivers[]` (name, car number, iRating, license, class, car), `SessionInfo.Sessions[].ResultsPositions[]` (authoritative order + gaps in race sessions), `WeekendInfo`.

Known gotchas (documented across all these projects):
- Gaps: race sessions use `ResultsPositions` time/laps-behind; practice/qual gaps come from best-lap deltas. `CarIdxF2Time` semantics differ by session type.
- Pace car is `CarIdx` with `CarIsPaceCar=1` — filter it.
- Disconnected/DNF drivers linger in the roster.
- Multiclass: sort within class, color by `CarClassID`.

## Sources

- [iOverlay FAQ](https://ioverlay.app/help/) · [iOverlay](https://ioverlay.app/)
- [iRacing SDK docs (community)](https://sajax.github.io/irsdkdocs/)
- [irDashies](https://github.com/tariknz/irdashies) · [iRon](https://github.com/lespalt/iRon) · [iFL03](https://github.com/SemSodermans31/iFL03) · [SharpOverlay](https://github.com/TiberiuC39/SharpOverlay) · [rah-iracing-overlay](https://github.com/RaulArcos/rah-iracing-overlay)
- [pyirsdk](https://github.com/kutu/pyirsdk) · [IRSDKSharper](https://github.com/mherbold/IRSDKSharper) · [node-irsdk](https://github.com/apihlaja/node-irsdk)
- [Kapps FAQ](https://kapps.kutu.ru/faq/) · [RaceLab](https://racelab.app/)
