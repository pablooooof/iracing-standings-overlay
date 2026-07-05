# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

A lightweight, standings-only overlay for iRacing (MIT, single WPF exe). Headline feature: gap delta over a configurable N laps (iOverlay pro-gates this at 1 lap). Efficiency is a hard requirement: the overlay must never affect sim frame times — repaint only on change, no busy polling, no browser engine.

## Commands

```powershell
dotnet build src/StandingsOverlay -c Release
# run without iRacing (fake 20-car race; ~25s laps, delta column populates after ~4 laps):
src/StandingsOverlay/bin/Release/net10.0-windows/StandingsOverlay.exe --demo
# run against live iRacing (waits for the sim; iRacing must be windowed borderless):
src/StandingsOverlay/bin/Release/net10.0-windows/StandingsOverlay.exe
```

There are no tests yet. Verification is visual: run `--demo`, screenshot the top-left of the screen (the overlay defaults to x=7, y=6), and check rows/gaps/delta signs. Kill with `Stop-Process -Name StandingsOverlay` (the window is click-through; interactive exit is via the tray icon).

Note for fresh shells on this machine: git/dotnet/gh need `$env:Path = [System.Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [System.Environment]::GetEnvironmentVariable('Path','User')` first.

## Architecture

Data flows one way: **source → snapshot → render**.

- `Data/IRacingSource` (live) and `Data/DemoSource` (`--demo`) both implement `ITelemetrySource` and feed the **same** `SnapshotBuilder` + `GapHistory` pipeline — any standings logic change is testable in demo mode. Sources raise `SnapshotReady` on background threads; `OverlayWindow.OnSnapshot` dispatches to the UI thread.
- `Data/SnapshotBuilder.Build(RawTick, Roster, GapHistory, OverlayConfig)` is a pure function producing display-ready `StandingsRow` records (all formatting happens here, not in XAML). `RawTick` = unboxed per-car telemetry arrays; `Roster` = driver info parsed from session YAML.
- `Data/GapHistory` samples each car's gap to the player **once per player lap crossing** (distance-based: `(carTotalDist − playerTotalDist) × refLapTime`). Delta-over-N-laps and laps-to-catch are lookups into that ring buffer. Sign convention: negative rel-change = player gained (shown green ▼).
- `IRacingSource` down-samples irsdkSharp's 60 Hz `OnDataChanged` to `UpdateHz` (default 4) and re-parses session YAML **only** when `Header.SessionInfoUpdate` changes. Keep it that way — reparsing the YAML per tick is the classic overlay CPU sink.
- `OverlayWindow` is transparent/click-through/topmost via Win32 extended styles (`Interop/Win32.ApplyOverlayStyle`); "edit mode" (tray menu) drops `WS_EX_TRANSPARENT` so the window can be dragged, then persists position.
- `Config/ConfigService` owns `config.json` (created next to the exe), hot-reloads on external edits via `FileSystemWatcher`, and debounces its own saves.

## Constraints & gotchas

- **Licensing**: uses irsdkSharp (MIT). Do NOT switch to IRSDKSharper — it is GPL-3.0 and conflicts with this repo's MIT license.
- The csproj removes implicit `System.Drawing`/`System.Windows.Forms` usings (WPF type collisions); WinForms is only for the tray `NotifyIcon` — import those namespaces explicitly and only in `UI/TrayIcon.cs`.
- iRacing session-type quirks live in `SnapshotBuilder`: race ordering uses `CarIdxPosition`, practice/qual falls back to best-lap sort (positions are often 0); race gaps use `CarIdxF2Time` with laps-down handling, practice/qual gaps are best-lap deltas.
- Column toggles are wired through `ColumnVisibility` (the Window's DataContext); per-column `Visibility` bindings resolve via `RelativeSource AncestorType=Window`. Snapshot dedup is `OverlayWindow.SnapshotsEqual` (record `Equals` alone compares row lists by reference).
- Tyre column renders `CarIdxTireCompound >= 1` as a wet (blue) ring — correct for rain-enabled series, but series with an alternate dry compound also report 1.

## Docs

`docs/RESEARCH.md` — survey of existing overlay apps, SDK bindings, window techniques. `docs/REQUIREMENTS.md` — feature set derived from the user's real iOverlay config (defaults must match it). `docs/ROADMAP.md` — ordered backlog including the strategy-inference phase.
