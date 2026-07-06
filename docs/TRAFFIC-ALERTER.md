# Traffic Alerter — design spec

Warns the player when a faster-class car (or a car about to lap them) is closing in,
with a time-to-arrival countdown. Visual mockups (three styles, live demos, audio):
https://claude.ai/code/artifact/e9be3f72-a1eb-4912-983b-4aa112c288c2

Decisions (locked with Pablo, 2026-07-05):
- Default mode: **faster class + being lapped**, visually distinct (see below).
- ALONGSIDE banner: **traffic only**, never same-class battles.
- **Audio cues on by default**, master + per-cue toggles.
- Visual style is a user choice: `Style: "Row" | "Beacon"`. Halo was designed but parked
  ("might be too much"); the artifact keeps the design if it's ever wanted.

Implementation status (2026-07-05): **Row + Beacon + audio implemented** — `Data/TrafficDetector`,
`UI/TrafficWindow`, `UI/TrafficAudio`, wired through both sources' `TrafficReady` event.
Additional behaviors discovered/locked during implementation: alerts are suppressed while the
player is on pit road (everyone "closes" on a stationary car); demo mode fields 3 GTP + a fast
GT3 rabbit so faster-class, train, and blue-flag paths all fire within ~4 minutes.
Faster-class alerts run in **all session types** (Pablo got swamped by GTPs in practice with
race-only gating, 2026-07-05); blue-flag qualification stays race-only — lap counts don't mean
"lapping you" in practice. `Traffic.RacesOnly` restores the old gating.

Feasibility: fully local. Paid equivalents (RaceLab "Overtake Alert", iOverlay "traffic
indicator") read the same iRacing shared-memory telemetry irsdkSharp exposes. No new
packages, MIT-clean.

## Data

Already in `RawTick`/`Roster`: `CarIdxLapDistPct`, `CarIdxLap`, `CarIdxOnPitRoad`,
`CarIdxSessionFlags`, `DriverEntry.ClassEstLap` / `ClassColor` / name / number / iRating.

Add to `ReadTick()` (same 4 Hz tick, three extra `GetData` calls):

| Var | Type | Purpose |
|---|---|---|
| `CarIdxEstTime` | `float[]` | sim's time-to-current-position; better gap accuracy through slow sectors than pct × laptime |
| `CarIdxTrackSurface` | `int[]` | filter `NotInWorld` (-1) so towed/reset cars don't ghost-trigger |
| `CarLeftRight` | `int` | sim spotter state for the ALONGSIDE stage (off/clear/left/right/2-left/2-right) |

## Detection (pure function, testable in `--demo`)

1. **Gap behind (seconds).** `deltaPct = (playerPct − carPct + 1) % 1`; candidate when
   `deltaPct < 0.5`. Primary gap = `CarIdxEstTime` delta (wrap: `+chaserLap` when negative;
   trusted only when > 0, both ests > 0.5 s, and within 35 % of a lap of the distance
   estimate — est reads 0 in pits and tears mid-crossing). Fallback/bound:
   `deltaPct × car.ClassEstLap`. Distance-only math made the TTA blow up to 99 s on corner
   exit (Pablo, live, 2026-07-05) because pct-gap stops closing when the player accelerates
   first; est time knows a hairpin from a straight. Faster-class closing rate is also floored
   at 30 % of the class-pace difference for the same reason. Window: `gapSec < 30`.
2. **Qualification.**
   - *Faster class:* `car.ClassEstLap < player.ClassEstLap − 1.0s` — alert regardless of laps.
   - *Being lapped (blue):* same class, `carTotal − playerTotal ≥ ~0.9` laps
     (`total = Lap + LapDistPct`); the player's flag bit `0x0020` (blue) is a cross-check,
     not the trigger (fires too late).
   - Skip pace car, spectators, `OnPitRoad`, `TrackSurface == NotInWorld`.
3. **Closing rate / TTA.** Per candidate, ring buffer of `(sessionTime, gapSec)` — ~12
   samples ≈ 3 s at 4 Hz. `rate = (oldest − newest) / windowSec`; `tta = gapSec / rate`
   when `rate > 0.2`. Gap jump > 3 s between ticks ⇒ tow/reset ⇒ flush that car's buffer.
4. **State machine (per car), with hysteresis.**
   - HIDDEN → **WATCH** when `tta ≤ AlertLeadTimeSec` (12)
   - → **IMMINENT** when `tta ≤ ImminentSec` (4) or `gapSec ≤ 1.5`. The gap shortcut is
     for traffic only — a blue car closing at 2 s/lap *lives* under 1.5 s of gap, so blue
     escalates on TTA alone (found live in demo: blue went red instantly otherwise)
   - → **ALONGSIDE** when `CarLeftRight` reports a car (banner: "◀ CAR LEFT" etc.)
   - → **CLEAR** once the car is 0.5 s *ahead*: green flash 0.8 s, then remove
   - Min display 2 s; dismiss only after 3 s out of range (no flicker at thresholds).
5. **Multi-car stacking.**
   - Stack sorted **purely by TTA** — nearest threat first/headline, regardless of type;
     blue styling travels with the car wherever it sits.
   - **Trains:** same-class cars within ~2.5 s of each other merge into a train — the
     lead car's row/headline gets a `×N` badge, one audio cue for the group. Different
     classes never merge.
   - Row style: max `MaxRows` (3) rows + "+N more in window" chip. Beacon style:
     headline = lowest TTA, others as class-colored queue dots with TTA (max 2 dots
     + "+N"). Halo: glow = nearest car's class color, count in the pill.

## UI — three styles (config `Style`)

Separate widget window/element, own draggable position via existing edit mode
(default top-center, near the mirror sightline). Shared visual grammar across styles:
**class color = what's coming · catch-rate chevrons = how fast · red pulse = it's here.**
Chevron scale from the closing-rate buffer: `▾` < 2.5 s/lap · `▾▾` 2.5–6 · `▾▾▾` > 6.

### Faster class vs. being lapped (must never be confused)

|  | Faster class (traffic) | Being lapped (blue) |
|---|---|---|
| Color | car's iRacing class color | always blue-flag blue + striped blue/yellow `BLUE` tag |
| Chevrons | usually ▾▾/▾▾▾ | usually ▾ (leader grinds, doesn't fly) |
| Lead time | WATCH at TTA ≤ 12 s | WATCH at TTA ≤ 20 s (planning, not reflexes) |
| Audio | rising chirp; urgent double-beep imminent | single calm two-tone, never escalates |

### Style A — "Row" (data driver)
Standings-row sibling: 300×36 px, `#212129` @94 %, 4 px radius, Segoe UI 12.5 px.
Class stripe + number chip, name + iRating, sub-line (`GTP · P2 in class`), TTA
countdown (mono, amber→red) with chevrons + `+N.Ns/lap`, 2 px proximity bar along the
bottom. IMMINENT pulses the border at ~0.6 s. ALONGSIDE swaps to a full-width banner.

### Style B — "Beacon" (peripheral vision)
~150 px missile-lock panel: class name bar, giant mono TTA (~40 px), chevrons that
*animate downward* toward a class-colored bar labeled YOU — fall speed proportional to
closing rate. Minimal text; readable from the corner of the eye. ALONGSIDE: panel
becomes `◀ CAR LEFT / HOLD LINE`.

### Style C — "Halo" (immersion)
Full-screen click-through window; the *screen edge glows* in the approaching class
color (inset vignette), breathing faster/brighter as TTA falls; IMMINENT strobes the
edge (~5 Hz). Tiny top-center pill carries `GTP · #07 · 5.2s ▾▾▾`. ALONGSIDE lights
only the side the car is on. Night-race hero style.

Repaint rules: Row/Beacon redraw only when state or displayed tenths change; Halo's
breathing/strobe runs as a WPF `DoubleAnimation` on the composition thread (GPU), not
per-tick redraws, and the animation object exists only while an alert is live. All
styles are a collapsed element doing zero work when no traffic (the common case).

## Audio

Tiny WAVs synthesized once at startup (sine/square envelopes, ~0.5 s), played via
`System.Media.SoundPlayer` — base library, no new packages, MIT-clean.

- **Watch (traffic):** two soft rising chirps (780→1170 Hz sine).
- **Imminent (traffic):** triple 1560 Hz square beep.
- **Blue (lapped):** calm descending two-tone (660→495 Hz sine). Never escalates.

Anti-spam arbitration: WATCH fires on window empty→occupied (a train = one chirp);
additional cars entering within 5 s ride the same cue. IMMINENT at most once per 3 s
regardless of how many cars cross the threshold. BLUE once per lapping car, never
repeated. No cue ever loops.

Config: master `Audio.Enabled`, per-cue booleans, `Volume` 0–100 (scale sample
amplitude at generation; SoundPlayer has no volume API). Cue prototypes are auditionable
in the artifact (WebAudio, same envelopes).

## Config (`config.json`, hot-reloaded)

```json
"Traffic": {
  "Enabled": true,
  "Style": "Beacon",                 // Row | Beacon | Halo
  "Mode": "FasterClassAndLapping",   // FasterClassOnly | AllClosing
  "AlertLeadTimeSec": 12,
  "BlueLeadTimeSec": 20,
  "ImminentSec": 4,
  "MaxRows": 3,
  "ShowIRating": true,
  "AlongsideBanner": true,           // traffic only
  "Audio": {
    "Enabled": true, "Volume": 70,
    "WatchCue": true, "ImminentCue": true, "BlueCue": true
  },
  "PositionX": 810, "PositionY": 6
}
```

## Demo-mode notes

`DemoSource` already fabricates two classes (GT3 `#FFDA59`, prototype `#57C1FF`).
Ensure at least one faster-class car starts behind the player and closes at a realistic
rate (~3–5 s/lap) so all five states are reachable in a demo run.
