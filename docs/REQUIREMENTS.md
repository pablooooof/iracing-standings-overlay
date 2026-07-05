# Requirements — derived from Pablo's actual iOverlay usage

*Source: live iOverlay `settings.dat` (2026-07-04). iOverlay stores plain JSON in `%APPDATA%\iOverlay\settings.dat`; its Chromium profile folders confirm it's an Electron app.*

## Usage profile

Only two widgets are enabled: **standings** (top-left, x=7 y=6) and **relative** (bottom-right). Fuel, spotter, inputs, track map, delta bar, Twitch: all off. So a standings-first tool replaces the main thing actually used. Relative is a natural phase-2, not phase-1.

Notably: `usehardwareacceleration: false` in the global settings — the Chromium GPU conflict is real enough that it's turned off on this machine. Validates the native-rendering direction.

## Current standings configuration (to match as defaults)

**Layout**
- Compact view: top **3** drivers + **3** ahead/behind the player (`driversattop: 3`, `driversaheadbehind: 3`), other classes collapsed (`otherclassesdriversattop: 0`)
- Live positions on (`uselivepositions: true`) — sort by track position during the race, not official results
- Column header row shown; player row highlighted with background
- No country flags

**Style**
- Background `#212129` at 0.75 opacity, accent `#00FFD0`, highlight color `#FF8800` (also used for tagged friends)
- Font size 14, weight 500; metric units

**Data shown** (race session has a wider column set than practice/qual — session-type-specific column sets are a real need)
- Position, car number (class-colored), driver name, iRating, license, positions gained, gap, interval, last lap, best lap, pit status, lap delta
- Header row: session info (time/laps, SoF-style items)
- Precision settings per field: lap times 3 decimals, gaps/intervals/lap delta 1 decimal
- Driver tagging exists and is used (1 friend tagged, category "Friend" in orange) — worth keeping as a small feature

## ⭐ Headline feature: multi-lap delta (iOverlay pro-gates this)

iOverlay's free tier fixes `lapdeltaamount: 1` — the standings "delta" column shows how much the gap to each car changed **over the last lap only**. The pro tier unlocks more laps. This is the #1 feature to build, unrestricted:

- **Delta over last N laps, user-configurable** (e.g. 1 / 3 / 5 / custom), shown per car: how much you gained/lost on them over that window.
- Implementation sketch: maintain a per-car ring buffer of gap-to-player (or gap-to-leader) sampled **at each lap crossing** (`CarIdxLap` increment), not per-tick. Delta over N laps = `gap_now − gap_N_laps_ago`; also derivable: **per-lap catch rate** and **laps-to-catch** projection (`interval / catch_rate`) — the number you actually want when hunting someone down.
- Cheap: 64 cars × a few floats per lap. Zero measurable cost.
- Possible extras once the buffer exists: trend arrows, tiny sparkline, avg pace over 5/10 laps (also iOverlay pro features — `avg5laps`/`avg10laps` columns).

## Non-functional (unchanged)

Windowed-borderless iRacing, click-through transparent window, repaint only on model change, single lightweight process. Default position top-left.
