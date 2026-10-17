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

## Race-end model (`Data/RaceEnd.cs` → `RaceEndModel`)

- **Lap-limited**: `SessionLapsRemain` is authoritative.
- **Time-limited** (the endurance case) — the rule, as observed in iRacing (the sporting code
  doesn't spell it out; confirmed from community telemetry logs, and the model logs the sim's own
  white/checkered flags to `overlay.log` so the next real race re-confirms it): the clock reaching
  0:00 does **not** end the race. The **overall leader's first crossing after 0:00 is the white
  flag; the checkered falls one lap later.** So
  `N = floor(leader laps completed at 0:00) + 2`. The leader crossing the line just *before* 0:00
  means a whole extra lap. (v0.x before this had the checkered at the first crossing — one lap
  short; the 1-lap fuel margin was hiding it.)

  **Projection** — for each of the top 3 overall cars (whoever is P1 at 0:00 decides, and leaders
  swap during pit cycles; the max wins):
  `X = laps completed now + (clock left − expected remaining stop time) / pace`
  - *laps completed now*: integer from `CarIdxLap`, fraction from the **time-domain** lap phase
    (`RelativeGap.LapPhase`) — distance pct would make the projection breathe with track section.
  - *pace*: `StintTracker.PaceStats` — trimmed mean of the last 8 clean laps (drop slowest 2,
    fastest 1). Not a median: over an odd window the median flips between lap clusters from one
    lap to the next and the projection alternated ±0.1 lap with it (seen in the demo).
  - *remaining stops*: soft. P(needs another stop) = Φ((laps needed − laps left in tank + 0.5) /
    0.6) per stop, from the car's typical stint (own → class median → player's tank converted to
    the car's lap time; borrowed stints widen σ). Priced at the car's **measured stop loss**: the
    drop of its "laps completed − elapsed / pace" invariant from pit entry to its first crossing
    after the exit (in-lap slowdown + lane + service + cold out-lap in one number; works without a
    track map, same in demo and live). Fallbacks: class median → player's → 60 s.
  - *on pit road*: the projection made on the way in is **held** (it already priced the stop);
    the implied remaining stop time feeds the checkered time. Re-projected at pit exit — the
    only jump left is the real stop's deviation from the measured ones.
  - *σ*: √(laps · lap-σ² + (0.25 % · clock)² + stop-count variance · loss² + stops · 6 s²), the stop
    terms capped by the clock actually left. P(+1 lap) = 1 − Φ((N−1−X)/σ), P(−1) = Φ((N−2−X)/σ).
  - *hysteresis*: the shown N (and the player's final lap) is kept while it's still ≥ 35 % likely,
    so a projection sitting on a lap boundary doesn't flip the count every few seconds; the bar
    pins full/empty and the % carries the rest.
  - *at 0:00*: the white-flag lap is latched (a late join reads the sim's white flag), phase
    becomes "white flag next crossing" → "final lap", P = 0.

  **Player**: laps to go = the player's line crossings until the leader's checkered (+ the one
  they're on), with their own remaining stops from the tank (stops = what the tank can't cover,
  lane loss + fill / fill rate), capped at the leader's lap count. The checkered *time* uses the
  most likely leader stop count, not the mean (a 5 % "maybe a splash" moved the player's knife-
  edge for an outcome that almost never happens). P(player runs one more) sums the leader's
  −1/0/+1 outcomes × the chance the player's own crossing lands past the line — in multiclass or
  laps down the player's crossing can tip a lap on its own. Held through the player's pit visit.

  The planner prices every candidate plan with the same count (`PlayerLapsFor(lapSec, stopSec)`,
  including the hysteresis hold) — saving fuel slows you → fewer laps → less fuel, the endurance
  freebie — so the plan's "N laps to go" and the race line never disagree.
  `Fuel.MarginLaps` (default 1.0) of extra fuel is always budgeted on top — a calculator that runs
  you dry once is deleted forever.

  **Why a % that doesn't jump** (the "4 % on lap 80, 80 % on lap 81" complaint about naive
  calculators): `time left ÷ last lap` moves with every lap-time wobble, with the leader's in-lap,
  and by a whole pit stop when the leader pits. Here the only inputs that move are a robust pace
  and stops that were already priced in, so the % can only move when the race actually changes
  (a caution, the leader losing time, a stop slower than their others). The headline is the
  bar and the seconds; the % is calibrated by the same σ. Verified in `RaceEndTests` on the
  endurance demo: second-half worst lap-to-lap change 6 %, final lap called 100 % of the last third.

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
finish needs 62.4L — SHORT 19.2L · +1 lap: 64.7L      ← fuel to the flag (+ the extra-lap case)
RACE 84 laps ▕██████████████▌░░░░░▏ 85  14%           ← timed race: bar = leader's progress through
+1 lap if leader 26.4s quicker (0.95s/lap) · leader 1 stop left · you 26 to go   the lap at 0:00
last stop ~L58 · add 38L (40L if +1 lap) · 26 laps to go   ← plan summary
black box 35L — 3.0L short, set 38L                   ← PitSvFuel vs the planned fill
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
push-plus-splash vs save-two-stints fork within the first minute of `--demo`. `--demo endurance`
is a 10-minute timed race where the GTP leaders pit every 7-9 laps and the player pits on fuel
(skipping a stop its tank already covers): the race-end model has to price the leaders' stops,
the last-stop line and black-box check show up before the player's final stop, and the race ends
like iRacing's (clock negative after 0:00, white at the leader's next crossing, checkered → state 5).
`RaceEndTests` steps it headless and checks the projection against the actual finish. Verify via
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

## Fuel consumption table (second widget)

A separate, simpler widget (`Data/FuelTableBuilder` → `FuelTableReady` → `UI/FuelTableWindow`)
built from the **same** `FuelModel`, for drivers who want the plain consumption numbers rather
than the Pirelli strategy bars. Works in **both practice and race** (no session gating, no
strategy-confidence gate — rows fill in as laps complete). Independent enable/position/scale.

Layout (matches the classic fuel-overlay idiom):

```
FUEL  E [====green====] F   72.4 / 100.9 L        Rem (Laps) 20.9 – 21.4
                 Usage (L/Lap)        Laps to Empty
Last                3.39                 21.36
Last 5              3.42                 21.17     ← optional row, off by default
Last 10             3.44                 21.05
Stint               3.47                 20.86
Target              3.46                 20.92     ← accent-coloured, clickable
┌───────────────────────────────────────────────────────────┐
│ Lap 8 / 29 · 0.6L ahead · need 3.44/lap for 21 to go        │  green = ahead, red = behind
└───────────────────────────────────────────────────────────┘
```

- **Rows** (`FuelModel` rolling stats, all scoped to the current tank — the Last-N window and the
  stint accumulator both reset at pit exit): `Last` = last non-pit lap; `Last 5`/`Last 10` =
  averages of the last N non-pit laps this stint, shown only once ≥2 laps exist and growing 2→N
  (a single lap never masquerades as an N-lap average); `Stint` = average over the whole stint.
  Laps-to-Empty = `fuelNow / usage`. The `Rem (Laps)` header is `fuelNow` over the
  thriftiest…thirstiest of the last 10 laps. In/out laps are excluded from all of these.
- **Target** = two interlocked numbers tied by the **usable tank**: `TargetLaps` (laps you want
  from a full tank) and `TargetPerLap` (L/lap). Set one and the other follows
  (`perLap = tank / laps`); `TargetMode` records which the driver set last, and that one is
  master. E.g. Suzuka 1000 km, 100.88 L tank: 29 laps → 3.48 L/lap, 30 laps → 3.36 L/lap.
- **Target tracker** (the actionable line): with a target set and a stint underway, compares
  `stintLaps × targetPerLap` (planned) against the fuel actually burned this stint. Positive =
  banked fuel (**green, "ahead"**), negative = overspent (**red, "behind"**). `need X/lap` =
  `fuelNow / lapsLeftInStint` — the average burn that still completes the target stint on the fuel
  you have (rises when ahead → you can push; falls when behind → save now).
- **Clickable target**: editable in the settings window (Fuel page → "Show fuel table"), and —
  when the per-widget `Interactive` toggle is on, or while overlays are unlocked (edit mode) — via
  ▲/▼ (and the mouse wheel) on the Target row itself. `Interactive` drops click-through for this
  one widget; it's **off by default** so it never steals clicks from the sim, matching every other
  overlay. Both paths write the same config, two decimals.

Config (`FuelTable` section): `Enabled`, `ShowLast5` (default off), `ShowLast10`, `ShowStint`,
`ShowStatus`, `TargetLaps`, `TargetPerLap`, `TargetMode` ("Laps"|"PerLap"), `Interactive`,
`Scale`, `X`, `Y`.

**Shared target across profiles**: the target (`TargetLaps` / `TargetPerLap` / `TargetMode`) is a
race-wide strategy decision, so `ConfigService.MirrorSharedTarget` keeps it identical in the
driving and spectate profiles — set it in the car and it applies while watching a teammate's stint,
and vice versa. Everything else about the widget (position, enabled, rows) stays per-profile.

## Gotchas & non-goals

- `FuelLevel` on EV/hybrid content reads kWh; unit label follows `PitSvFuel`'s unit. v1
  treats it as an opaque "unit" — math is identical.
- BoP: never read `DriverCarFuelMaxLtr` alone; the usable tank is `× DriverCarMaxFuelPct`.
- Driver swaps (team endurance) are free: telemetry follows the *car*, and consumption EWMA
  re-converges within ~4 laps of a new driver.
- Non-goals for v1: auto-setting `PitSvFuel` via broadcast messages (easy follow-up, listed
  in ROADMAP), tire-change time modeling (iRacing fills and changes concurrently for most
  road content; fill time dominates in endurance), multi-car strategy comparison (no data).
