# Suikoden 2 — collision findings, 2026-09-17

> **Historical document, superseded.** Kept for the reasoning, not as
> instructions. For the current state, start with
> [`HANDOVER.md`](../../HANDOVER.md) in the repository root. Version numbers,
> next steps and file locations below are out of date.

Three play sessions on one map (`a0v0m1`, the barracks), plus static reads.
Plugin **v58**. Everything below was measured, not inferred; where something is
still an inference it says so.

Ground truth in hand: **156 walked cells, 46 blocked observations, 230 observed
steps**, all on `a0v0m1`. Preserved outside the game folder, because the dumps
are overwritten per map.

---

## 1. The oracle: `EVENTCON.wallStopDir`

**At the player's current position, this field holds the direction the engine
refused.** A pure field read: no call, no patch, nothing perturbed.

Evidence:

* 433 of 1550 frames had `wallStopDir != 255`, and every one had
  `speed=0, dx=0, dy=0`. Every frame the player was moving read 255.
* Not merely a "stationary" flag: 913 frames of idle standing also read 255.
* Pressing west (`currentMoveDir=2`) and not moving gave `wallStopDir=2`;
  pressing north (`1`) gave `1`.
* Independently, `EventMapAtariCheck(0, dir)` returned 1 for exactly the
  direction `wallStopDir` held, at four positions across three maps.

**It LATCHES** — it keeps its value after the player stops pushing. Samples must
be edge-triggered or a stale direction gets stamped onto every later position.

## 2. Direction encoding, and 8-way movement

Measured from frames where the player was moving, pairing `currentMoveDir`
against the sub-cell offsets `dx`/`dy`. Each cardinal produced rows with one axis
exactly zero, which pins it; the diagonals never did.

```
0 = S  (0,+1)     11 = SW (-1,+1)
1 = N  (0,-1)     12 = SE (+1,+1)
2 = W  (-1, 0)    13 = NW (-1,-1)
3 = E  (+1, 0)    14 = NE (+1,-1)      255 = none
```

The gap from 3 to 11 is the engine's. The pattern under it is
`11 + 2*(north) + (east)`.

**8-way movement is confirmed by observation**, not by the existence of
`EVENT_HUMAN.isDiagonalMove`: 63 of 230 distinct steps changed both axes,
covering all four diagonal deltas, and the engine refuses diagonals
*individually* — `blockedDir` came back 11, 12, 13 and 14. A diagonal is a
first-class direction here, **not** a composition of two cardinals, so A\* must
query it separately rather than infer it from the two cardinals.

## 3. Coordinates

* `px`/`py` are EVENT_POS/8 — **half-tile** resolution. A tile is 16 EVENT_POS.
* `dx`/`dy` are the sub-cell offset within the current `px`/`py`, range 0..±7.
* `TATEMONO.map_x` / `map_y` are in the **same px/py space** as the player.
* `MAP.maxw`/`maxh` are EVENT_POS. Tiles = `maxw/16 * maxh/16`.

## 4. SOLVED: furniture is `TATEMONO`

**Of 156 cells the player stood on, ZERO are a TATEMONO position.** With 21
entries inside a 513-cell bounding box, chance would put about six there.

All 57 entries match a screenshot of the room one for one: `anime_no` 8 and 9 are
the four beds in two halves, 10 the two wall candles, 11 the central pole, 2 the
three stacked barrels, 7 the table, 1 the rug, 0 the crates.

The footprint chain is in **GSDShare**, not GSD2:

```
TATEMONO.anime_no
  -> machi_opd.anime_table[anime_no].timer_table[i].pattern_no
    -> machi_opd.pattern_table[pattern_no]
       { flag, center_x, center_y, size_x, size_y, atari_data, obj_table }
```

`size_x`/`size_y` are EVENT_POS; divide by 8 for px. Sprites are anchored at
their **base** (pattern 0's `obj[0]` has `ofs=(0,-48)` against `size=(16,48)`).

Best scoring model so far: object occupies its width, and only its lowest rows
block — **zero walked-cell violations, 20 of 42 refused cells covered**, at
anchor `(+1,0)`. A whole-rectangle model reaches 25/42 but over-blocks badly
(28 violations), which is expected: the drawn sprite is taller than the part you
cannot walk into.

**Not yet decoded:** `PatternTable.atari_data`, 4 ints per pattern
(e.g. pattern 7 = `-1 -1 0 0`, pattern 9 = `-1 15 -1 0`, the animated ones all
zero). Every bit-layout I tried — cell sizes 4/8/16 EVENT_POS, row- and
column-major, both bit orders, both polarities, anchor swept — scored *worse*
than the plain rectangle. It is a collision field on a collision structure and it
almost certainly is the exact mask; the layout is simply not yet known.

## 5. NOT SOLVED: the terrain layer

With furniture modelled, **22 of the 42 refused cells remain unexplained, and
they are all on the room's edges** — the tent walls. Furniture is one layer;
walls are another. This is Suikoden 1 repeating exactly: collision was two
layers and only one was being read.

## 6. Routes measured DEAD — do not re-open without new evidence

| Route | How it died |
|---|---|
| `BGCON.EventMapAtariCheckF1(typ,mno,px,py)` | 1536 combinations — all 256 selectors x 6 coordinate scales — **zero variation**. 1024 constant, 512 out of range. |
| `MAP.atari_data` via `index` chip ids | Dumped raw and scored over every quadrant assignment, every single-value and bitmask rule, every layer subset: best 3 of 46, i.e. chance. Drawn out, it blocks almost nothing. |
| Direct plane readings | 165,000 combinations — `index`, `index_new`, `han_new`, strides 4..240, four coordinate scales, any single-value rule. Best 7 of 46. Chance. |
| `SRN_TYPE` as a footprint table | Right shape (`ofx, ofy, w, h`); all five entries read `ofs=(0,0) size=(960,240)` — the whole map. It is screen/scroll regions. |

**`EventMapAtariCheck(pno, dir)` is NOT side-effect free.** The before/after
snapshot caught it writing `currentMoveDir`, `wallMoveDir`, `wallStopDir`, `sdir`
and `isMoveChanged`. Value-parameters-only was the reason to believe it safe and
that reason was wrong. A run of 16 calls is also not 16 independent samples.
Keypress-only, and `wallStopDir` is the better oracle anyway.

## 6b. Second map, 2026-09-19: the static-data route is EXHAUSTED

Ground truth collected on `a0v0m0`, the outdoor camp above the barracks:
**558 walked cells, 90 blocked observations (83 distinct refused cells), 729
observed steps.** This is the map whose planes DO fit its geometry - 960x960,
3600 tiles, `index_new` and `han_new` both 6 x 3600, `index` 6 x 7200.

Every static reading was re-run against it. Results:

* **`han_new` is not the terrain layer.** Every candidate with zero walked-cell
  violations covered **zero** refused cells. Its content is far too sparse to be
  collision: L0, L1, L4 are uniformly 4; L2 has 186 twos in 3600; L3 has *two*;
  L5 has 271 ones.
* **Nothing else works either.** A sweep over `index`, `index_new`, `han_new`,
  fifteen strides, four coordinate scales, every value, plus chip -> atari_data
  with quadrant-by-parity and nibble rules: **best coverage was 2 of 83.** With
  558 walked cells as the constraint, that is noise.
* **The TATEMONO finding does NOT generalise.** The barracks gave 0 of 156
  walked cells on a tatemono anchor. The camp gives **4 of 558** - still below
  chance (about 13 expected) but no longer zero. Worse, proximity barely
  discriminates: median Chebyshev distance from a refused cell to the nearest
  anchor is 2, and from a walked cell it is 3. And `htyp` is **4 for all 79
  entries** here, so that filter is dead too.

**But the object rule does partially replicate, and that matters.** The
per-pattern fitted-footprint model, anchored at `(+1, 0)`, gives:

* barracks: **0 walked violations, 20 of 42 refused covered**
* camp: **0 walked violations, 29 of 83 refused covered**

Same anchor on both maps, no over-blocking on either. So objects are real and
account for roughly a third to a half of refusals - a genuine bounded partial
result. The other half to two thirds is terrain, and terrain is not readable.

So the barracks rule was *found*, never *bounded*, exactly as caution 2 warns.
Objects are certainly part of collision - the barracks match to the screenshot
was too clean to be chance - but they are not most of it, and the terrain
component is not in any readable plane.

**Conclusion, stated plainly so it is not circled again: `MAP`'s static data
does not encode the walkable grid in any form recoverable by reading it.**
Tested on two maps, with 714 walked cells and 125 blocked observations between
them. The remaining static tables have all been read and none of them is it.

### What is still alive

* **`EVENTCON.wallStopDir`** - exact, free, per-frame, and validated. Gives
  (position, refused direction) as the player walks.
* **`EventMapAtariCheck(pno, dir)`** - returns 1 for blocked, agrees with
  wallStopDir four times out of four. Entity-local, and side-effecting.

Both answer *about where an entity currently is*. Neither reads an arbitrary
cell. That is almost certainly because this engine computes collision through
the `ref RECT` path (`EventMapAtariCheckFunc1`) from structures built at load,
rather than keeping a flat grid anyone can index - which is consistent with
every negative above.

## 7. The thing to do next

**`index_new` and `han_new` are exactly the tile count on three of four maps:**

| | size | tiles | `index_new` / `han_new` |
|---|---|---|---|
| `map[0]` | 960x960 | 3600 | 6 x **3600** |
| `map[2]` | 576x544 | 1224 | 6 x **1224** |
| `map[3]` | 640x640 | 1600 | 6 x **1600** |
| **`map[1]`** | 960x240 | 900 | 5 x **1530** |

`map[1]` — the barracks, **the only map any ground truth has ever been collected
on** — is the single entry whose plane length does not fit its own geometry, and
the only one with 5 layers instead of 6. Every plane-scoring run so far has been
against that anomaly. `index` is consistently 2x the tile count with the upper
half `0xCDCD` buffer fill, on all four.

So: **collect walked and blocked ground truth on `map[0]`, `map[2]` or `map[3]`,
and score `han_new` there.** `han_new` carries values `{1,2,4}` and is the
obvious terrain candidate; it has never been tested on a map where its length
makes sense.

## 8. Standing constraints

* `EventMapAtariCheckFunc1` and `EventAtariCheckFunc1` take `ref RECT` and are
  **off limits to Harmony**. A postfix reading `object[] __args` on the first
  corrupted live collision in play.
* Reading state is preferred over writing it. Writing to an unused `ehuman` slot
  to turn the entity-local oracle into an arbitrary-point query is a **possibility
  held in reserve**, not the plan, at the project owner's direction.
* Quality over speed. The waypoint-graph fallback remains the worst case, not the
  plan.
