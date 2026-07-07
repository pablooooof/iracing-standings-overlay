# Fuel calculator & endurance strategy — design

*Status: design. Keep in sync with `Data/FuelModel.cs` / `Data/StrategyPlanner.cs` once implemented.*

## Goal

A player fuel calculator focused on **endurance**: live consumption numbers, plus a strategy
planner that projects the rest of the race as **horizontal stint bars** (the F1/Pirelli
strategy-graphic idiom) and shows **two competing strategies side by side** — e.g. at hour 18
of the Spa 24h: *"push and take a splash-and-dash at the end"* vs *"save 0.11 L/lap for the
next two stints and skip that stop"* — with the projected time delta between them.

Everything is player-only by necessity: iRacing exposes fuel telemetry **only for the player's
car** (other cars' strategy stays heuristic — that's `StintTracker`'s job).

## Telemetry inputs

Live vars (all already readable through irsdkSharp `GetData`):

| Var | Unit | Use |
|---|---|---|
| `FuelLevel` | L | the master signal; sampled at lap crossings, watched during stops |
| `FuelLevelPct` | % | sanity check only |
| `SessionFlags` | bitfield | global caution bits → classify yellow laps |
| `OnPitRoad[player]`, `PlayerCarInPitStall` | bool | in/out-lap classification, refuel detection |
| `PitSvFuel`, `PitSvFlags` | L, bitfield | what the pit crew is *about* to add (compare vs plan) |
| `SessionTimeRemain` / `SessionLapsRemain` | s / laps | race-end model |

Session YAML (`DriverInfo`, verified present in irsdkSharp 0.9.0's model):

- `DriverCarFuelMaxLtr` × `DriverCarMaxFuelPct` → **usable tank capacity** (BoP can cap the
  physical tank; always multiply).
- `DriverCarEstLapTime` → pace prior before we have clean laps.

## FuelModel (measurement)

`Data/FuelModel.Update(RawTick)` — player-side twin of `StintTracker`, fed every tick, does
real work only on transitions:

- **Per-lap sampling**: record `FuelLevel` at each player lap crossing (same
  crossed-then-wait-for-the-value dance as `StintTracker`; fuel is valid immediately, the lap
  *time* is what lags). `usedThisLap = prevLevel − level` (ignore laps where fuel rose — that
  was a stop or a tow reset).
- **Lap classification**: `Green` (clean), `Yellow` (any caution flag seen during the lap),
  `InLap`/`OutLap` (touched pit road), `Junk` (tow / no time). Consumption stats are kept
  **per class**: green uses an EWMA over the last ~10 clean laps (α≈0.25, recent stints
  dominate — track evolves, driving changes) plus min/max; yellow keeps its own average and
  falls back to `0.55 × green` until two yellow laps are observed.
- **Refuel observation**: while `PlayerCarInPitStall` and `FuelLevel` rising, measure the
  **fill rate** (L/s) — that's the per-liter cost of every planned stop. Fallback until
  observed: `Fuel.FillRateLps` config (default 2.6, roughly GT3).
- **Pit-loss observation**: `(inLap + outLap) − 2 × greenPace` from the player's own completed
  stops → measured **pit lane loss** excluding fill time. Fallback: `Fuel.PitLaneLossSec`
  (default 45; set per track if you care before the first stop).
- **Stint history**: completed player stints (laps, fuel used, save level guess) — feeds the
  dimmed "already driven" part of the strategy bars.

All state resets with the session (same reset points as `StintTracker`).

## Race-end model

- **Lap-limited**: `SessionLapsRemain` is authoritative.
- **Time-limited** (the endurance case): laps remaining ≈ `timeRemain / paceLap`, computed
  with the *strategy's own* lap time (saving fuel slows you down → fewer laps → less fuel —
  the endurance freebie; the solver iterates this fixed point, it converges in 2-3 rounds).
  The flag falls when the **leader** finishes: if the player is laps down, use the leader's
  recent pace (`StintTracker.RecentPace`) to bound total race laps, then count how many the
  player completes in that window. `Fuel.MarginLaps` (default 1.0) of extra fuel is always
  budgeted on top — a calculator that runs you dry once is deleted forever.

## StrategyPlanner (the solver)

`Data/StrategyPlanner.Plan(FuelState, OverlayConfig)` — pure; runs on lap crossings and pit
transitions, not per tick.

Model: a **strategy = (stops remaining k, save level s)** where `s` L/lap is saved on every
lap **before the final fill** (after the last stop you always push — saving there buys
nothing). Lap-time cost of saving is linear: `penalty(s) = s / MaxSaveLPerLap ×
MaxSavePenaltySec` (defaults 0.20 L/lap ↔ 0.55 s/lap, both configurable per car/track).

For each feasible stop count `k` from `kMin` (fewest stops physics allows at max save) to
`kMin + 2`:

1. Find the **minimum** save level `s_k` (0 if pushing already works) such that
   `fuelNow + k × usableTank ≥ lapsRemain(s_k) × (greenPerLap − s_k) + MarginLaps × greenPerLap`.
2. Projected time = `lapsRemain × (paceLap + penalty(s_k)) + Σ stops(pitLaneLoss + fill/fillRate)`
   — the fill amounts fall out of a lap-by-lap simulation that also emits the stint list:
   each stint's laps, its planned pit lap, the fill (a final stop only takes what the
   remaining laps need → a small fill is *visibly* a splash-and-dash).
3. Emit as `StrategyPlan { Label, Stints[], Stops, SaveLevel, TotalSeconds }`.

Rank by projected time; show the best `Fuel.Strategies` (default 2). The classic endurance
fork appears by construction: `k` stops with `s=0` (push + splash) vs `k−1` stops with
`s=s_min` (save, no splash), with an honest Δ between them. When the Δ is inside the noise
(< ~5 s), both bars matter; when one dominates, the number says so.

Also computed for the header row (the number drivers actually drive to):
**target consumption** = `fuelAvailableThisStint / lapsToPlannedStop` — "keep it under 2.21
and you make the window".

Degrade gracefully: before 3 clean laps exist, show live fuel + laps-in-tank from
`DriverCarEstLapTime` priors and *no* strategy bars (a guess from lap 1 is worse than nothing
— same philosophy as StintTracker's confidence gating).

## FuelWindow (the widget)

Fourth overlay window, identical plumbing to `TrafficWindow` (click-through topmost, own
config X/Y, edit-mode drag + persist, repaint only on change, sample content in edit mode).
Default position: top-right area, under the traffic widget.

```
FUEL 43.2L ▏ 2.31/lap ▏ 18.7 laps ▏ tgt 2.21          ← live numbers (big)
next stop ~L168 · add 74L · 214 laps to go            ← plan summary
┌──────────────────────────────────────────────┬─────┐
│▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│██████│██████│██████│█▍   │ +0s │  A · push · 3 stops
│▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│▓▓▓▓▓▓▓│▓▓▓▓▓▓▓│▓▓▓▓▓▓▓ │ +14s│  B · save 0.11/lap · 2 stops
└───────────────────────▲──────────────────────┴─────┘
                       now
```

- Bar x-axis = the **whole race** (elapsed + remaining), Pirelli style: the past is one dimmed
  gray segment (actual stint boundaries ticked), the future is colored stints separated by
  white stop ticks; a labeled ▲ marks *now*.
- Stint segment color: **green** = push, **amber** = fuel save (label carries the level),
  **red + narrow** = splash-and-dash — the whole point is that strat A's final red sliver vs
  strat B's amber stints reads at a glance.
- Right column: Δ vs the best strategy (`+0s` / `+14s`). Row label: strategy letter, save
  level, stop count. Hover/labels stay static — this is glanceable race info, not a dashboard.
- Yellow-flag laps compress reality vs the projection; the planner re-runs every lap so the
  bars just re-scale — no special casing in the view.
- Practice/qual: bars hidden, live numbers stay (that's where you *learn* your per-lap number).

## Demo mode

`DemoSource` fabricates player fuel so the whole pipeline is testable offline: 100 L tank,
~2.4 L/lap with noise, scripted stops refuel at 2.5 L/s, one caution phase (lower burn), and
the remaining-race fuel need deliberately lands at ~2.15 tanks → the planner must produce the
push-plus-splash vs save-two-stints fork within the first minute of `--demo`. Verify via
`overlay.log` (planner logs each re-plan: inputs + chosen strategies) per the token-budget
rules; screenshot only the final look.

## Config (`Fuel` section)

```jsonc
"Fuel": {
  "Enabled": true,
  "Strategies": 2,            // bars shown (1-3)
  "MarginLaps": 1.0,          // safety fuel, in laps
  "MaxSaveLPerLap": 0.20,     // car's realistic ceiling for lift-and-coast saving
  "MaxSavePenaltySec": 0.55,  // lap-time cost at that ceiling (linear in between)
  "PitLaneLossSec": -1,       // -1 = auto-measure from own stops (fallback 45)
  "FillRateLps": -1,          // -1 = auto-measure while refueling (fallback 2.6)
  "BarWidth": 420,            // strategy bar length, DIPs
  "X": 810, "Y": 120          // edit-mode draggable like every widget
}
```

## Gotchas & non-goals

- `FuelLevel` on EV/hybrid content reads kWh; unit label follows `PitSvFuel`'s unit. v1
  treats it as an opaque "unit" — math is identical.
- BoP: never read `DriverCarFuelMaxLtr` alone; the usable tank is `× DriverCarMaxFuelPct`.
- Driver swaps (team endurance) are free: telemetry follows the *car*, and consumption EWMA
  re-converges within ~4 laps of a new driver.
- Non-goals for v1: auto-setting `PitSvFuel` via broadcast messages (easy follow-up, listed
  in ROADMAP), tire-change time modeling (iRacing fills and changes concurrently for most
  road content; fill time dominates in endurance), multi-car strategy comparison (no data).
