# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

A lightweight, standings-only overlay for iRacing (MIT, single WPF exe). Headline feature: gap delta over a configurable N laps (iOverlay pro-gates this at 1 lap). Efficiency is a hard requirement: the overlay must never affect sim frame times — repaint only on change, no busy polling, no browser engine.

**Working style**: this is a fast do-try-do-try project. Commit and test freely — small, frequent commits with a quick `--demo` verification are expected and encouraged; don't agonize over a change before shipping it, and don't fear breaking things (everything is reversible, the overlay never touches the sim). Experimental features land behind a config toggle (e.g. `ShowRejoinState`) so they can be switched off and iterated. Capture new ideas/decisions in `docs/ROADMAP.md` (live-iteration backlog + status-badge legend + known SDK limits) so nothing is lost between sessions.

## Commands

```powershell
dotnet build src/StandingsOverlay -c Release
# run without iRacing (fake 20-car, 3-class race; ~25s laps, delta column populates after
# ~4 laps; GTP traffic alerts fire within the first minute, blue flags after ~4 minutes):
src/StandingsOverlay/bin/Release/net10.0-windows/StandingsOverlay.exe --demo
# run against live iRacing (waits for the sim; iRacing must be windowed borderless):
src/StandingsOverlay/bin/Release/net10.0-windows/StandingsOverlay.exe
# open the settings window on launch (also reachable from the tray / tray double-click):
src/StandingsOverlay/bin/Release/net10.0-windows/StandingsOverlay.exe --demo --settings
# time-limited race joined in its final ~2.6 laps, to exercise the race-end / extra-lap
# estimator (fuel widget "≈N laps · you M to go") without a 40-minute wait:
src/StandingsOverlay/bin/Release/net10.0-windows/StandingsOverlay.exe --demo timed
# scripted dry→wet arc (rain ramps in ~25s) to exercise the weather trend arrows and the
# dry→wet header flash (iRacing exposes NO forecast in the SDK — only current conditions):
src/StandingsOverlay/bin/Release/net10.0-windows/StandingsOverlay.exe --demo rain
# offline-testing session for the Lap Lab practice table: scripted S2 variance, an off-track
# every 8th lap, one active reset at ~112s; verify via "lap lab:" lines in overlay.log:
src/StandingsOverlay/bin/Release/net10.0-windows/StandingsOverlay.exe --demo lab
```

Unit tests exist for the gap model, traffic detector and Lap Lab sector clock (`dotnet test src/StandingsOverlay.Tests -c Release` — gap exactness across track sections, cross-class normalization, alert onset/99.9/blue regressions, 60 Hz sector-split exactness + clean-lap rules; the demo track's non-uniform speed profile in `DemoSource.TrackPct` is what makes them meaningful). Everything else is verified visually: run `--demo`, screenshot the top-left of the screen (the overlay defaults to x=7, y=6), and check rows/gaps/delta signs. Kill with `Stop-Process -Name StandingsOverlay` (the window is click-through; interactive exit is via the tray icon).

**Verification token budget** (tokens are limited — a past session burned most of a day's quota on screenshot loops): read at most 2-3 screenshots per change, never image-poll on an interval. Prefer `overlay.log` (the traffic detector logs every WATCH/PASSED transition with gap/tta/rate) and scripted pixel scans (`System.Drawing.GetPixel` for a marker color, print only matches) over eyeballing captures. For slow demo events (blue flag ≈ every 5 min), arm a background PowerShell job that greps the log and screenshots only when the event fires, then read just those frames.

Note for fresh shells on this machine: git/dotnet/gh need `$env:Path = [System.Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [System.Environment]::GetEnvironmentVariable('Path','User')` first.

## Git workflow & releases

- **Always commit completed work** — small, frequent commits directly on `master` (the only long-lived branch), each verified with a quick `--demo` run first. Push `master` to origin when a change set is done; pushing master never publishes anything.
- **Versions come from git tags via MinVer** (`MinVerTagPrefix=v`: tag `v0.4.0` → version 0.4.0; untagged commits build as the next patch `-alpha`). Never hand-edit a version into the csproj. Display/log version is `UpdateCheck.CurrentDisplay` (informational version — MinVer leaves `AssemblyVersion` at major-only, so `GetName().Version` is wrong for display).
- **Releasing is user-gated: NEVER create or push a `v*` tag without asking first.** When a meaningful set of user-visible changes has accumulated, propose a release with a suggested version (0.x semver: minor = features, patch = fixes only) and wait for the go-ahead.
- To release after the go-ahead: `git tag vX.Y.Z; git push origin vX.Y.Z`. That triggers `.github/workflows/release.yml` (tests → self-contained single-file win-x64 publish → zip → GitHub Release with auto-generated notes). Watch it with `gh run watch`, confirm with `gh release view vX.Y.Z`.
- The app itself checks GitHub's latest release once at launch (`UpdateCheck.cs`, toggle `CheckForUpdates`): tray menu item + About-page link when newer, notify-only — it never downloads. The check logs `update check:` to `overlay.log` either way, which is also how you verify it.

## Architecture

Data flows one way: **source → snapshot → render**.

- `Data/IRacingSource` (live) and `Data/DemoSource` (`--demo`) both implement `ITelemetrySource` and feed the **same** `SnapshotBuilder` + `GapHistory` pipeline — any standings logic change is testable in demo mode. Sources raise `SnapshotReady` on background threads; `OverlayWindow.OnSnapshot` dispatches to the UI thread.
- `Data/SnapshotBuilder.Build(RawTick, Roster, GapHistory, OverlayConfig)` is a pure function producing display-ready `StandingsRow` records (all formatting happens here, not in XAML). `RawTick` = unboxed per-car telemetry arrays; `Roster` = driver info parsed from session YAML.
- `Data/GapHistory` samples each car's gap to the player **once per player lap crossing** (distance-based: `(carTotalDist − playerTotalDist) × refLapTime`). Delta-over-N-laps and laps-to-catch are lookups into that ring buffer. Sign convention: negative rel-change = player gained (shown green ▼).
- `IRacingSource` down-samples irsdkSharp's 60 Hz `OnDataChanged` to `UpdateHz` (default 4) and re-parses session YAML **only** when `Header.SessionInfoUpdate` changes. Keep it that way — reparsing the YAML per tick is the classic overlay CPU sink.
- `OverlayWindow` is transparent/click-through/topmost via Win32 extended styles (`Interop/Win32.ApplyOverlayStyle`); "edit mode" (tray menu) drops `WS_EX_TRANSPARENT` so the window can be dragged, then persists position.
- The relative box is another parallel branch: `Data/RelativeBuilder.Build(RawTick, Roster, StintTracker, OverlayConfig)` → `RelativeReady` → `UI/RelativeWindow` (own window, default bottom-right, fixed slot count so the player row never jumps). Gap math lives in `Data/RelativeGap` (lap-phase model: `CarIdxEstTime / CarClassEstLapTime` per car, wrapped delta × the chaser's lap; LapDistPct only as a skew-gated fallback) and is **shared with the traffic detector and the smoothed standings gaps** — fix gap bugs there, not in the widgets, and never reintroduce distance/est blending (it breathes with track section; see docs/RELATIVE.md). Spec: `docs/RELATIVE.md`.
- The traffic alerter is a parallel branch of the same tick: `Data/TrafficDetector.Update(RawTick, Roster, OverlayConfig)` → `TrafficReady` → `UI/TrafficWindow` (own window/position, two styles: `Row`/`Beacon`) + `UI/TrafficAudio` (synthesized WAV cues, arbitration already done in the detector). Faster-class alerts fire in every session type (`Traffic.RacesOnly` restores race-only); blue-flag alerts are race-only by definition; suppressed while the player is on pit road. Spec: `docs/TRAFFIC-ALERTER.md`.
- The fuel calculator is another parallel branch: `Data/FuelModel.Update(RawTick)` (player-only measurement — iRacing exposes no fuel for other cars) + `Data/StrategyPlanner.Build(RawTick, FuelModel, OverlayConfig)` → `FuelReady` → `UI/FuelWindow` (live numbers + Pirelli-style strategy stint bars). The planner enumerates (stops remaining × min fuel-save level) and prices pit loss vs save penalty, so "push + splash-and-dash" vs "save, one stop fewer" fall out with an honest Δ. Each re-plan is logged to `overlay.log` (`fuel plan:`). Spec: `docs/FUEL-STRATEGY.md`.
- Lap Lab (practice lap table) is another parallel branch, player-only: `Data/SectorClock` samples `LapDistPct`/`SessionTime` on **every 60 Hz frame** (before the UpdateHz stride — crossing interpolation needs the full rate; races gated out) → `Data/LapLabTracker.Build` at snapshot rate → `LapLabReady` → `UI/LapLabWindow`. Sector boundaries are regexed from the raw session YAML (`SplitTimeInfo` — irsdkSharp's model lacks it). Laps only count S/F-to-S/F while armed; teleports (active reset, tow) disarm the clock, so garbage rows are impossible. Testing/practice/qual only. Spec: `docs/LAP-LAB.md`.
- `Config/ConfigService` owns `config.json` (created next to the exe), hot-reloads on external edits via `FileSystemWatcher`, and debounces its own saves. It holds **two profiles**: `config.json` (driving) and `config.spectate.json`, active while the player is out of the car (`IsOnTrack` false — teammate stint/garage/spectating; `IRacingSource.DrivingChanged`, 5 s debounce, tows count as driving). The spectate file is cloned from the driving profile on first use; `Current` is always the active profile, so positions/columns/counts all differ per profile with no per-setting plumbing.
- `UI/SettingsWindow` is the app's only real chrome: a normal (activatable, dark-title-bar via `Win32.UseDarkTitleBar`) window opened from the tray "Settings…" item (or `--settings`). It edits `ConfigService.Current` live and persists through **`ConfigService.SaveAndNotify()`** — plain `Save()` suppresses the watcher echo, so in-app edits MUST use `SaveAndNotify` to reach the widgets; external file edits still flow via the watcher. Rows are built from descriptor helpers (`Toggle`/`Slider`/`Segmented`/`ColorRow`/`Master`), so adding a setting is one line — do not hand-roll bound XAML per control. Every widget's `Enabled` flag and the "Move overlays" edit-mode toggle route here; edit mode has one owner (`App.SetEditMode`) that mirrors state back to both the tray checkbox and the settings switch. Autostart lives in `Config/AutoStart` (shared by tray + settings).

## Constraints & gotchas

- **Licensing**: uses irsdkSharp (MIT). Do NOT switch to IRSDKSharper — it is GPL-3.0 and conflicts with this repo's MIT license.
- The csproj removes implicit `System.Drawing`/`System.Windows.Forms` usings (WPF type collisions); WinForms is only for the tray `NotifyIcon` — import those namespaces explicitly and only in `UI/TrayIcon.cs`.
- iRacing session-type quirks live in `SnapshotBuilder`: race ordering uses `CarIdxPosition`, practice/qual falls back to best-lap sort (positions are often 0); race gaps use `CarIdxF2Time` with laps-down handling, practice/qual gaps are best-lap deltas.
- Car status is `Data/CarStatus` — two channels (penalty flags DQ/BLK/DMG/WRN vs physical state TOW/SPUN/REJOIN/SLOW/SWAP/PIT/EXIT/OUT) shared by standings and relative; display style (`StatusStyle`: "TextAndFlags" | "Text") is per widget. TOW is transition-detected (car materializes in its pit stall without passing "approaching pits" — `StintTracker.WasTowedIn`) plus `PlayerCarTowTime` for the player; never infer TOW from how long a car sat still. Driver changes blink the car out of the world for a second mid-stop and re-materialize it in the stall — the pre-vanish surface distinguishes that from a tow, and the blink must not split the pit visit. SPUN/REJOIN/SLOW never fire in the pit area (stall + entry/exit lanes). Opponent tire changes have NO SDK channel — `StintTracker` infers them from stationary stop time vs the car's own fuel-fill baseline (valid under fuel-and-tires-separate rules; `InferTireChanges`).
- Column toggles are wired through `ColumnVisibility` (the Window's DataContext); per-column `Visibility` bindings resolve via `RelativeSource AncestorType=Window`. Snapshot dedup is `OverlayWindow.SnapshotsEqual` (record `Equals` alone compares row lists by reference).
- Tyre column renders `CarIdxTireCompound >= 1` as a wet (blue) ring — correct for rain-enabled series, but series with an alternate dry compound also report 1.

## Docs

`docs/RESEARCH.md` — survey of existing overlay apps, SDK bindings, window techniques. `docs/REQUIREMENTS.md` — feature set derived from the user's real iOverlay config (defaults must match it). `docs/ROADMAP.md` — ordered backlog including the strategy-inference phase.
