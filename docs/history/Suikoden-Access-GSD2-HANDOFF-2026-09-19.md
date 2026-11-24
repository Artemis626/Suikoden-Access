# Suikoden Access — Suikoden 2 navigation handoff, 2026-09-19

> **Historical document, superseded.** Kept for the reasoning, not as
> instructions. For the current state, start with
> [`HANDOVER.md`](../../HANDOVER.md) in the repository root. Version numbers,
> next steps and file locations below are out of date.

**For a fresh chat asked to examine the remaining leads and the proposed design
path.** Plugin version at time of writing: **v58**.

This document is the current state. Two companions matter and should be read
alongside it:

* `Suikoden-Access-PATHFINDING-HANDOFF-2026-09-16.md` — **§0 of that document is
  the method and it is not optional.** Read it before forming any opinion here.
* `Suikoden-Access-GSD2-COLLISION-2026-09-17.md` — the measurement log, with the
  numbers behind every claim below.

`Suikoden-Access-NAV-STATUS.md` is the deeper reference; its §2 is Suikoden 2
and is now partly superseded by the two documents above.

---

## 0. The one-paragraph version

Suikoden 2's collision **cannot be read out of static map data.** That is now a
measured result across two maps with 714 walked cells and 125 blocked
observations, not a failure to find the right table. What *does* work is
`EVENTCON.wallStopDir`, a free per-frame field that reports the direction the
engine just refused at the player's current position — an exact oracle about
*where the player is*, and nothing else. The open question this chat is asked to
examine is what to build on top of that: the proposal is a learned occupancy
grid feeding real 8-way A\*, and the alternative is a route that requires writing
to game state, which the project owner has deliberately held in reserve.

---

## 1. What is CONFIRMED, and how

All measured in play, never inferred.

### 1.1 The oracle — `EVENTCON.wallStopDir`

At the player's current position this field holds **the direction the engine
refused**. Pure field read: no call, no patch, nothing perturbed.

* 433 of 1550 frames had `wallStopDir != 255`, and every one had
  `speed=0, dx=0, dy=0`. Every frame the player was moving read 255.
* Not merely "stationary": 913 frames of idle standing also read 255.
* Pressing west (`currentMoveDir=2`) and not moving gave `wallStopDir=2`;
  pressing north (`1`) gave `1`.
* Independently, `EventMapAtariCheck(0, dir)` returned 1 for exactly the
  direction `wallStopDir` held, at four positions across three maps.

**It LATCHES** — it keeps its value after the player stops pushing. Samples are
edge-triggered in `Gsd2Walk.NoteBlocked` for that reason. Do not undo that.

### 1.2 Direction encoding — 8-way, measured

```
0 = S  (0,+1)     11 = SW (-1,+1)
1 = N  (0,-1)     12 = SE (+1,+1)
2 = W  (-1, 0)    13 = NW (-1,-1)
3 = E  (+1, 0)    14 = NE (+1,-1)      255 = none
```

The gap from 3 to 11 is the engine's. The pattern under it is
`11 + 2*(north) + (east)`.

**8-way is confirmed by observation**: 63 of 230 distinct steps on one map
changed both axes, all four diagonal deltas present, and the engine refuses
diagonals *individually* (`blockedDir` came back 11, 12, 13, 14). **A diagonal is
a first-class direction, not two cardinals composed.** A\* must query it
separately rather than infer it — this is the opposite of Suikoden 1, where 8-way
had to be forcibly disabled.

### 1.3 Coordinates

* `px`/`py` are EVENT_POS/8 — **half-tile** resolution. A tile is 16 EVENT_POS.
* `dx`/`dy` are the sub-cell offset within the current `px`/`py`, range 0..±7.
* `TATEMONO.map_x`/`map_y` are in the **same px/py space** as the player.
* `MAP.maxw`/`maxh` are EVENT_POS; tiles = `maxw/16 * maxh/16`.
* `EVENT_POS` is a 16.16 fixed-point struct — read `all / 65536`.
  `Convert.ToInt32` on it throws.

### 1.4 Objects — a real but PARTIAL result

`MAP.tatemono_data` is the furniture. On the barracks, all 57 entries matched a
screenshot one for one (beds, candles, central pole, barrels, table, rug,
crates), and **zero of 156 walked cells sat on a tatemono anchor**.

The footprint chain lives in **GSDShare**, not GSD2:

```
TATEMONO.anime_no
  -> machi_opd.anime_table[anime_no].timer_table[i].pattern_no
    -> machi_opd.pattern_table[pattern_no]
       { flag, center_x, center_y, size_x, size_y, atari_data, obj_table }
```

Sprites anchor at their **base**. A per-pattern fitted-footprint model at anchor
`(+1, 0)` replicates across both maps with no over-blocking:

* barracks: **0 walked violations, 20 of 42 refused covered**
* camp: **0 walked violations, 29 of 83 refused covered**

So objects are real and bounded at roughly **a third to a half** of refusals.
They are not most of it.

**Caveats that killed the stronger version of this claim.** The camp gives 4 of
558 walked cells on an anchor (barracks was 0 of 156), proximity barely
discriminates (median Chebyshev distance 2 for refused vs 3 for walked), and
`htyp` is `4` for all 79 camp entries so that filter is dead. The barracks rule
was *found*, never *bounded* — caution 2 exactly.

---

## 2. What is DEAD — measured, do not re-open without new evidence

| Route | How it died |
|---|---|
| `BGCON.EventMapAtariCheckF1(typ,mno,px,py)` | 1536 combinations — all 256 selectors × 6 coordinate scales — **zero variation**. 1024 constant, 512 out of range. |
| `MAP.atari_data` via `index` chip ids | Dumped raw, scored over every quadrant assignment, every single-value and bitmask rule, every layer subset. Best 3 of 46. Drawn out, it blocks almost nothing. |
| Direct plane readings (`index`, `index_new`, `han_new`) | 165,000 combinations on the barracks (strides 4..240, four scales, any value). Re-run on the camp with 558 walked cells: **best 2 of 83.** Noise. |
| `han_new` as terrain | Every zero-violation candidate covered **zero** refused cells. Too sparse to be collision anyway: three of six layers uniformly `4`, one with *two* non-default entries in 3600. |
| `SRN_TYPE` as a footprint table | Right shape (`ofx, ofy, w, h`); all five entries read `ofs=(0,0) size=(960,240)` — the whole map. Screen/scroll regions. |

**Stated plainly so it is not circled again: `MAP`'s static data does not encode
the walkable grid in any form recoverable by reading it.** Two maps, 714 walked
cells, 125 blocked observations.

**`EventMapAtariCheck(pno, dir)` is NOT side-effect free.** A before/after
snapshot caught it writing `currentMoveDir`, `wallMoveDir`, `wallStopDir`, `sdir`
and `isMoveChanged`. "Value parameters only" was the reason to believe it safe
and that reason was wrong. A run of 16 calls is also not 16 independent samples.
It works (returns 1 = blocked) but it is keypress-only, and `wallStopDir` is the
better oracle anyway.

### Why the negatives are probably coherent

Every method that answers correctly (`wallStopDir`, `EventMapAtariCheck`) answers
only about **where an entity currently is**. This is consistent with the engine
computing collision through the `ref RECT` path (`EventMapAtariCheckFunc1`) from
structures built at load, rather than keeping a flat grid anyone can index. If
that is right, no amount of further table-reading will produce a grid, and the
negatives above are not bad luck.

**That inference is labelled as one.** It is the best explanation of five
independent negatives, and it has not itself been tested.

---

## 3. REMAINING LEADS — for this chat to weigh

Listed with honest odds. None has been tried.

1. **`BGCON.mapobj` / `mapobj_sorted`** — `Il2CppReferenceArray<MAPOBJ>`, the
   *runtime* object list. `MAPOBJ { disp, anime_no, pri, htyp, map_x, map_y,
   anime, priorityPosOffset }`. **Low odds**: it looks like a runtime mirror of
   `TATEMONO`, which is already understood and already only explains a third to a
   half. Cheap to dump.

2. **`BGCON.kakikae` (`KAKIKAECON`)** — tile-rewrite machinery.
   `KAKIKAE_HEAD { index, typ, w, h, ptnkazu, ptn }` has `w`/`h`. **Low odds**:
   "kakikae" is animated tile replacement, not collision. Cheap.

3. **`OBJ_AT { px, py, px2, py2, ptn, ptn2 }`** — a genuine collision
   *rectangle*, used by `EventNObjTObjAtariCheck(OBJ_AT, pno)` and
   `EventTObjTObjAtariCheck(OBJ_AT, pno)`. **Interesting**: `OBJ_AT` is a
   reference type, not a `ref` struct, so constructing one and calling those
   methods is *not* barred by the by-ref rule. Both still take `pno`, so they
   may remain entity-local — and both are untested for side effects. Medium odds,
   medium risk, needs the side-effect snapshot treatment before trust.

4. **Decode `PatternTable.atari_data`** — 4 ints per pattern (pattern 7 =
   `-1 -1 0 0`, pattern 9 = `-1 15 -1 0`, animated ones all zero). It is a
   collision field on a collision structure and is very likely the exact object
   mask. Every layout tried — cell sizes 4/8/16 EVENT_POS, row- and column-major,
   both bit orders, both polarities, anchor swept — scored **worse** than a plain
   rectangle. **Would improve objects from ~40% to maybe ~50%. Does not touch
   terrain**, so it cannot finish the job alone.

5. **The `ref RECT` path itself** — `EventMapAtariCheckFunc1(int, int, ref RECT)`
   is **off limits to Harmony** (a postfix reading `object[] __args` on it
   corrupted live collision in play). Whether *calling* it with our own `RECT`
   is safe is a separate and untested question. **High value if it takes an
   arbitrary rectangle; high risk; rule-adjacent.** Do not touch without an
   explicit decision.

---

## 4. THE PROPOSED DESIGN PATH — the main thing to examine

### The proposal

Build a **learned occupancy grid** from `wallStopDir`, persisted per map, and run
**8-way A\*** over it.

* The mod already records exactly this: walked cells and (position, refused
  direction) pairs, per map, in `Gsd2Walk`.
* Persist per map key the way `Gsd1Exits` already persists learned data.
* Unexplored cells are planned through **optimistically**; when the engine
  refuses a step, mark that cell solid and replan. Standard optimistic-A\* with
  replanning, the D\* Lite family.
* Rate observed: **558 walked cells and 83 distinct walls in about six minutes**
  of ordinary walking.

### Why this is not the waypoint fallback

The project owner's standing instruction is that the FF Pixel Remaster
waypoint-graph approach is the worst case, not the plan. **This is not that.**
It is full-resolution grid pathfinding at `px` granularity, true 8-way, real
costs, over a grid whose cells are *measured* rather than guessed. The waypoint
fallback throws away resolution by construction; this does not.

### The argument I find decisive

**The A\* layer is identical whichever way the grid is obtained.** If lead 3 or 5
or the reserved write-route later yields arbitrary-point queries, the same
pathfinder runs over a complete grid instead of a learned one. So this is the
consumer any grid source feeds, and building it is not wasted work under any
outcome.

### The objections a fresh chat should press on

* **You cannot path somewhere you have never been.** How much does that actually
  cost for a screen-reader user? Exits are separately targetable via `MAPINFO`
  (`f_data.g2_minfo_tbl`, 227 entries across 11 areas, read successfully), and
  landmarks are already targetable. Is "path to places you have been, plus known
  exits" enough for the mod's purpose, or is it a material regression against
  Suikoden 1, which has a complete grid?
* **Is optimistic replanning acceptable in an audio-only interface?** A route
  that turns out to be blocked means the player walks into a wall and the mod
  re-routes. In Suikoden 1 that never happens. Is that acceptable, mitigable, or
  disqualifying?
* **Does the partial object model help or hurt?** Objects explain a third to a
  half of refusals with zero over-blocking on two maps. Seeding the learned grid
  with them would give a head start on unexplored cells. But a seeded cell is an
  *inference*, and the project's whole method is that inferences get scored, not
  trusted. Should the seed be used for planning but never recorded as observed?
* **Is the "no flat grid exists" inference (§2) actually right?** It is the load-
  bearing claim under this whole proposal and it has not been tested directly.
  What would test it?

---

## 5. HELD IN RESERVE — the write route

`ehuman` has unused slots (`otyp = 255`). Setting one's position and then asking
`EventMapAtariCheck` about *that* entity would turn the entity-local oracle into
an **arbitrary-point query**, which would produce a complete grid immediately.

**The project owner has explicitly held this in reserve**: reading state first,
because reading is what worked in game 1. It is not rejected, it is deferred.
Anything proposing it should say what changed to justify it.

Risk category: this is **writing** to game state, unlike everything else done so
far. The slot is unused, but if the engine iterates all slots, a modified one
could glitch or crash.

---

## 6. DATA ASSETS — preserve these before they are overwritten

Ground truth currently lives **only** in the game folder, and a fresh `O` or
`F10` press on the same map **overwrites it**:

```
BepInEx\plugins\suikoden2_walk_a0v0m0.txt        camp,     558 walked, 90 blocked
BepInEx\plugins\suikoden2_collision_a0v0m0.txt   camp,     static tables
BepInEx\plugins\suikoden2_walk_a0v0m1.txt        barracks, 156 walked, 46 blocked
BepInEx\plugins\suikoden2_collision_a0v0m1.txt   barracks, static tables
```

**Copy these four files somewhere outside the game folder before the next play
session.** They are ~2 MB total and they are the only ground truth this project
has for Suikoden 2. They must NOT go in the repo — `.gitignore` excludes all game
data and that stays.

Screenshots exist for both maps and were decisive twice (the barracks furniture
match, and sanity-checking the camp). Ask for one for any new map.

---

## 7. Rules paid for in bugs — do not relearn these

* **Never Harmony-patch a hot-path method taking a by-ref struct.** Paid for in
  Suikoden 2 specifically: a postfix reading `object[] __args` on
  `EventMapAtariCheckFunc1(int, int, ref RECT)` corrupted live collision — walls
  stopped blocking, the player slid off the map edge. `EventMapAtariCheckFunc1`
  and `EventAtariCheckFunc1` are off limits.
* **Never patch a base virtual AND its overrides.** Three trampolines in one
  vtable chain crashed the game natively at startup.
* **A hand-patched class must NOT carry a `[HarmonyPatch]` attribute** — it makes
  `PatchAll` abort `Plugin.Load()` and disable the whole mod.
* **Never cap a measurement, and log the zero.** "Found none" and "never looked"
  must never look the same in a log. This bit *this* work: the BGCON sweep once
  `return`ed on the first exception, so scale 16 silently ended it; and I once
  narrowed the oracle sweep from dir 0..15 to 0..7 on a misreading, cutting out
  exactly the four diagonals.
* **`CountOf` must check `Length` as well as `Count`** — Il2Cpp arrays expose
  `Length`, lists expose `Count`.
* **Il2Cpp lists are not `System.Collections.IEnumerable`** — index them.
* **This game runs the new Input System exclusively.** Use `Keyboard.current`.
* **Type names collide between the games** (`MAP`, `GAME_WORK`, `WindowManager`).
  Use `Gsd.FindIn(dll, name)` for anything belonging to one game's field code.
* **Controller note, not a mod bug:** a controller in **SXS** mode crashes the
  game natively on first input. **XInput works fine**, including switching
  between controller and keyboard mid-session.

---

## 8. Environment, build, keys

Game path: `E:\SteamLibrary\steamapps\common\Suikoden I and II HD Remaster`

```
dotnet build
```

The post-build step copies the dll into `BepInEx\plugins`. **`TitleProbe.Version`
prints at startup — confirm it before trusting any test result.** A stale build
has masked several sessions. Currently **v58**.

Read `BepInEx\LogOutput.log` directly. Tags: `[Nav]` Suikoden 2, `[Nav1]`
Suikoden 1.

`BepInEx\_decompiled\GSD1.cs` is valid ASCII. The others, including the 145 MB
`GSD2.cs` and `GSDShare.cs`, are partly UTF-16 — **pipe through `tr -d '\000'`
before grepping or matches vanish silently.** The interop decompiles carry
signatures and parameter names but **never method bodies**.

### Suikoden 2 keys

| Key | Action |
|---|---|
| `O` | write the walk dump (walked, blocked, edges, state histogram) |
| `Shift+O` | one `EventMapAtariCheck` reading — side-effecting, use sparingly |
| `F9` | ehuman dump |
| `F10` | collision dump — all MAPs, planes raw, `atari_data` raw, tatemono, OPD tables, MAPINFO |

`F5`–`F12` are all taken; Suikoden 1 owns `F5`–`F8`, `F11`, `F12`.

### Files

| File | Purpose |
|---|---|
| `Gsd2Walk.cs` | **Suikoden 2 ground-truth recorder** — walked, blocked, edges, state; `O` / `Shift+O` |
| `NavProbe.cs` | Suikoden 2 static dumps — `F9` / `F10` |
| `Gsd1Path.cs` | Suikoden 1 A\*, targets, collision — the worked example to copy the *shape* of |
| `Gsd1Nav.cs` | Suikoden 1 `Village_c` hook, walked-cell recording, `F6` |
| `TitleProbe.cs` | `Gsd` type lookup helpers, `UiText`, version |

**Suikoden 1 is in daily use and must not be touched except to read it as a
worked example.**

---

## 9. What this chat is being asked

1. Examine the remaining leads in §3. Is the ordering right? Is anything
   dismissed too quickly? Is there a lead nobody has listed?
2. Examine the design path in §4, especially the four objections. Is the learned
   occupancy grid the right thing to build now, or is it premature?
3. Pressure-test the §2 inference that no flat grid exists. It is load-bearing
   and untested.

**Quality over speed.** When something is genuinely ruled out, say so plainly and
stop circling it. And per §0 of the pathfinding handoff: reasoning generates
candidates, only an observation chooses between them.
