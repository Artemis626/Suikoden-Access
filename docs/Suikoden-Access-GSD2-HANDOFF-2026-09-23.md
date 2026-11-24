# Suikoden Access — Suikoden 2 navigation handoff, 2026-09-23

> **Current reference.** The project is shelved at v72 and unmaintained; start
> with [`HANDOVER.md`](../HANDOVER.md) in the repository root. File names below
> are as they were when this was written: the docs now live in `docs/`, and
> `PROJECT-STATUS.md` became `HANDOVER.md`.

**Supersedes `Suikoden-Access-GSD2-HANDOFF-2026-09-19.md`.** Plugin version:
**v64** (written at v63; §10 records what changed the same day). Companions
worth reading, in this order:

> **CURRENT STATE — READ §13 ONWARD (v71; §18 is the 2026-10-06 sanity pass).** Collision is solved
> (§13), the pathfinder works in play (§14–§15), naming is in (§16), and §17 has
> the NPC-data research plan and the v70 cleanup. The history below starts here:
>
> **§11 (2026-09-28).** The v64 session settled the
> `otyp=4` rectangle: **dead** as a collision source. It also turned up the
> passive entity/terrain label §2 said did not exist: **`wallOrg=255` at a
> cardinal refusal is the entity channel**, measured clean across four sessions.
> And the §4 frontier moved for the first time (7% → 18% known solid).
>
> Earlier the same week, §10: the v61 slide repair was refuted and the slide route
> closed.

* `Suikoden-Access-PATHFINDING-HANDOFF-2026-09-16.md` — **§0 is the method and it
  is not optional.** It was violated again on 2026-09-19 and cost a whole
  candidate oracle; §2.1 below is the post-mortem.
* `Suikoden-Access-GSD2-REVIEW-2026-09-21.md` — the fresh-eyes review, with §7
  carrying the results that overturned two of its own claims.
* `Suikoden-Access-GSD2-COLLISION-2026-09-17.md` — the older measurement log.

Suikoden 1's navigation is finished and in daily use. **Do not touch it** except
to read `Gsd1Path.cs` as a worked example.

---

## 0. The one-paragraph version

Suikoden 2's collision cannot be read out of static map data — measured, and not
re-opened. The working oracle is `EVENTCON.wallStopDir`, which is exact but only
about where the player is standing *and has stopped*. Three things changed since
the last handoff, all measured. **The learned occupancy grid is not viable**:
across three sessions on one map, 806 walked cells, the frontier did not close —
7% known, down from 8% after one session. **The referee is mildly contaminated**:
about 4% of blocked observations are entities rather than terrain, and they are
static props rather than moving NPCs, which is the benign case. And **there is a
new collision source**: `EVENT_HUMAN.w`/`h` carries a real rectangle, populated
only for `otyp=4`, which are static and never move. Scoring that rectangle is the
next job.

---

## 1. CONFIRMED — measured in play, never inferred

### 1.1 The oracle — `EVENTCON.wallStopDir`

Holds the direction the engine just refused, at the player's current position.
Pure field read: no call, no patch, nothing perturbed.

* 433 of 1550 frames non-255, every one with `speed=0, dx=0, dy=0`.
* 913 frames of idle standing read 255, so it is not a "stationary" flag.
* **It only ever fires when the player has fully STOPPED.** Across two maps,
  `wallStopDir != 255 && speed != 0` is **0 frames out of 109,123**.
* **It LATCHES.** Samples are edge-triggered in `Gsd2Walk.NoteBlocked`. Do not
  undo that.

### 1.2 A refusal is a fact about an EDGE, not a cell

Scored against walked cells, two maps:

| refused cells derived from | count | walked-cell violations |
|---|---|---|
| at least one **cardinal** refusal | 67 | **0** |
| **diagonal** refusals only | 42 | **3** |

Counterexample: camp `(54,65)` `blockedDir=12` marks `(55,66)` solid, and
`(55,66)` is walked. So: **learn blocked directed edges; derive cell-solidity
only from cardinal refusals.** A learned grid that marks the `refused` cell solid
accumulates phantom walls at every diagonal corner.

Any A\* built here wants an **edge-shaped predicate** — `BlockedEdge(x, y, dir)`,
not `Blocked(x, y)`.

### 1.3 Direction encoding — 8-way, measured

```
0 = S  (0,+1)     11 = SW (-1,+1)
1 = N  (0,-1)     12 = SE (+1,+1)
2 = W  (-1, 0)    13 = NW (-1,-1)
3 = E  (+1, 0)    14 = NE (+1,-1)      255 = none
```

8-way is confirmed by observation, and the engine refuses diagonals
*individually*. A diagonal is a first-class direction, not two cardinals
composed — the opposite of Suikoden 1, where 8-way had to be disabled.

### 1.4 Coordinates

* `px`/`py` are EVENT_POS/8 — **half-tile**. A tile is 16 EVENT_POS.
* `dx`/`dy` are the sub-cell offset, 0..±7.
* `EVENT_POS` is 16.16 fixed point — read `all / 65536`. `Convert.ToInt32` throws.
* `TATEMONO.map_x/map_y` and `EVENT_HUMAN.x/y` are in the same px space.

### 1.5 Objects — `TATEMONO`, a real but partial result

Per-pattern fitted footprint at anchor `(+1, 0)`: **0 walked violations on both
maps**, covering 20 of 42 refusals on the barracks and 29 of 83 on the camp. So
objects are real and bounded at roughly a third to a half. They are not most of
it.

### 1.6 The oracle is PLAYER-LOCAL — closed

`EventMapAtariCheck(pno, dir)` answers only for `pno = 0`. This was previously
asserted from 3 entities at one position, which could not tell "only the player
works" from "those two were standing in the open." Now measured properly:

* Three uncapped sweeps of all 60 `ehuman` slots.
* **Zero non-zero results on any non-player entity, in any direction.**
* Including **13 entities standing beside a cell known solid** from a cardinal
  refusal, 5 of them displayed, across `otyp` 1, 2, 3 and 4.
* At the same instant the player returned `1` for **seven of sixteen** directions.

### 1.7 `EVENT_HUMAN.w`/`h` is a real rectangle — NEW

Never read before v62. Real, and selective:

| otyp | w × h seen | moves? | cells per entity |
|---|---|---|---|
| 1 | `0x0` always | **yes** — 14 entities over 40 cells | many |
| 2 | `0x0` always | no | exactly 1 |
| 3 | `0x0` always | no | exactly 1 |
| **4** | **`8x16`, `16x8`, `8x40`, `16x16`** | no | exactly 1 |

EVENT_POS units, so 1×2, 2×1, 1×5 and 2×2 px cells. **`otyp=4` is a static sized
obstacle living in `ehuman`** — the best new candidate collision source since
`TATEMONO`, and untested.

---

## 2. DEAD — measured, do not re-open without new evidence

| Route | How it died |
|---|---|
| `MAP` static data, all of it | Two maps, 714 walked cells, 125 blocked. `atari_data`, `index`, `index_new`, `han_new`, `srn_type`, `tatemono`, `typ`, `tokusyu_atari` all swept. `MAP`'s full field list is exhausted on inventory grounds too. |
| `BGCON.EventMapAtariCheckF1` | 1536 combinations, all 256 selectors × 6 scales, **zero variation**. |
| `SRN_TYPE` as a footprint table | All five entries read the whole map. Screen/scroll regions. |
| The oracle for non-player entities | §1.6. Three sweeps, 1888+ calls, zero. |
| **The slide oracle, both formulations** | v59: §2.1 below. v61 repair: §10.1 — worse than chance. Not recorded since v64. |
| **The `otyp=4` rectangle as a collision source** | §11.1. All 12 sized, exact positions: 0 of 1458 common anchors walk-clean even against its own session; per prop, the best walk-clean offset covers at most 3 terrain refusals, and the displayed props' own rectangle cells are walked. |
| **Passive entity-vs-terrain labels** — *but see §11.2: `wallOrg` is one* | `isJustOBJRITA`, `isRitaToWall`, `isHitHuman`, `isXPosCheck`, `isYPosCheck`, `isJustOpenDoor` all read **False on all 54** blocked rows, terrain and entity alike. A constant is not a label. Caveat: `wallStopDir` is edge-triggered on the *latch* change, which need not be the frame of contact — a flag that fires on contact and clears would look exactly like this. |

### 2.1 The slide oracle — refuted, and the post-mortem matters

The idea: `wallStopDir` can't see refusals the player *slides past*, and
`wallOrgMoveDir`/`wallMoveDir` looked like they carried them. The supporting
numbers were good — comparable volume, zero overlap with `wallStopDir`, every
substitution diagonal→cardinal, and the grid-alignment rival ruled out because
ordinary turning takes a separate path.

**All of it was true and the conclusion was still wrong.** First session that
recorded positions: **22 of 60 observations contradicted by an observed step in
exactly that direction out of exactly that cell**, 20 of them in the same
session's own `[EDGES]`. `wallStopDir` scored the same way: **0 in 107**.

Cause: 274 of 966 moving frames with `wallOrg` set had `currentMoveDir` equal to
the diagonal `wallOrg` called refused. The fields keep their last value while the
player walks on through that direction.

The guard was **vacuous**: it required `wallOrg == targetMoveDir` because that
held 5080/5080 — it holds *because the two fields latch together*, so it could
never fire.

**The lesson, and it is §0 of the pathfinding handoff repeated:** every check was
about the **fields**; none was about the **position**, which is what the
observation claims. Five field-level checks agreeing is not one position-level
check. Also: v59 stripped `cur`/`dx`/`dy` out of the key to keep the set small,
and those were exactly the fields that would have shown the failure in the dump.

A repair (`currentMoveDir == wallMoveDir`, 692/966 frames, excluding all 274 bad
ones) is in v61 and is **untested**. `[SLIDE]` stays quarantined from `[BLOCKED]`.

---

## 3. THE REFEREE IS CONTAMINATED — quantified

`wallStopDir` fires for **entities** as well as terrain, and
`objStopDir`/`isHitHuman` covers only some of them.

The case that found it, at camp `(61,76)`, clean pre-call snapshot:
`wallStopDir=1` said north refused; `EventMapAtariCheck(0,1)` returned 0, so the
**map** says north is open; `isHitHuman=False` and `objStopDir=255`, so the person
channel never fired; and an `otyp=2` entity stood directly north. Both readings
were at the exact cell origin, so sub-cell position does not explain it.

v62 annotated every refusal with what was standing near it. Result, 54
observations on the camp:

| | count |
|---|---|
| nothing at the refused cell — **terrain, the clean subset** | **36** |
| entity **ON** the refused cell | **2** (static prop 2, moving NPC **0**) |
| entity only **nearby** | 16 |
| unreadable | 0 |

**About 4% contamination on the strict measure, and it is static props rather
than moving NPCs** — the benign case, because a prop that blocks is legitimately
part of the map. The dangerous case, a moving NPC baked in as a permanent wall,
is 0 of 54 here. Not disproven: an earlier session had 1 of 18 pointing at a
moving-entity cell. Rare, not absent.

**The proximity is real signal, not base rate.** Static entities sit within
Chebyshev 1 of **31% of refused cells against 11% of walked cells** — threefold
enrichment — while sitting ON only 4% against 1%. Enrichment in the
*neighbourhood* rather than on the cell is exactly what an extent with an offset
anchor produces, which is the same shape as `TATEMONO` (sprites anchor at their
base). Recording radius 1 instead of radius 0 is what made this visible.

---

## 4. THE LEARNED GRID IS NOT VIABLE — measured across three sessions

This was the open design question. It now has an answer.

Merged camp `a0v0m0`, three sessions, **806 walked cells, 162 blocked
observations, 1237 observed steps**:

| | after 1 session | after 3 sessions |
|---|---|---|
| walked cells | 558 | **806** |
| frontier — unwalked 8-neighbours of walked cells | 942 | **981** |
| frontier **known solid** | 8% | **7%** |
| directed edges out of walked cells resolved | 47% | 58% |

**More walking did not close the frontier.** Walked cells grew by 44%, the
frontier grew with them, and the known-solid fraction went slightly *down*. Edge
resolution improved only because more cells are known walkable.

The reason is structural: refusals accumulate only where the player *deliberately
pushes into walls*. Ordinary walking adds walkable cells and almost no blocked
ones — 162 refusals against 806 walked cells across three sessions. A learned
occupancy grid would require the player to bump into every wall on the map, which
is precisely what a navigation aid exists to prevent.

**So the learned grid cannot be the primary grid source.** It remains useful as a
cache and as ground truth for scoring. That makes the remaining leads — §5 — the
only routes to a complete grid, and raises their priority accordingly.

What still stands from the review: the **A\* consumer is worth building** against
an edge-shaped predicate with three-state cells (observed-walkable /
observed-blocked / unknown) and a cost multiplier on unknown cells, because that
layer is identical whichever way the grid is obtained.

---

## 5. LIVE LEADS, in priority order

1. ~~**Score the `otyp=4` rectangle.**~~ **Done, §11.1: dead.** Original text kept: `w`/`h` is real, static and readable. Method
   is `TATEMONO`'s exactly: treat each `otyp=4` as a `w/8 × h/8` px rectangle,
   **sweep the anchor** (sprites anchor at their base in this engine, so start
   there), keep only anchors with **zero** violations against `[WALKED]`, and
   among those take the one covering most of `[BLOCKED]`. A rule that blocks
   nothing also scores zero violations, so coverage is not optional. v63's
   `[ENTITIES]` section exists to make this scorable.
2. ~~**Test the v61 slide repair.**~~ **Done, §10.1: refuted, route closed.**
3. **`OBJ_AT` + `EventNObjTObjAtariCheck(OBJ_AT, pno)`.** The only construct
   carrying its own rectangle. A reference type, so constructing one is not
   barred by the by-ref rule. Still takes `pno`, so it may stay entity-local.
   Needs the before/after snapshot before trust.
4. **`EventObjAtariCheck(int, int, int)`** — private, returns int, three ints,
   never tested anywhere. Right arity for a point query.
5. **The `ref RECT` call** — `EventMapAtariCheckFunc1(int, int, ref RECT)`.
   **Gated on the project owner's explicit decision.** The rule paid for in bugs
   is about *Harmony-patching* a by-ref hot path; *calling* it with our own
   `RECT` is a different mechanism. Still risky: it is private and
   `EventMapAtariCheck` was caught writing live controller state.
6. **`PatternTable.atari_data`** — improves objects from ~40% to maybe ~50%,
   cannot finish the job. Lowest odds, but the safest and the payoff is bounded.
7. **A town map.** `tokusyu_atari = 0` on both maps measured so far — a camp and
   a tent. The doors/exits lead (`TOKUSYU_ATARI` + `MAPINFO`), the analogue of
   game 1's biggest single win, is **untested, not dead**.

**The scoring corpus now exists.** 806 known-walkable and 69 known-solid cells on
the camp, so any candidate point query can be scored against ~875 labelled points
immediately. That converts the old method — "sweep parameters and look for
variation", which produced 1536 combinations of nothing — into "sweep parameters
and score against known answers." Build the sweep two-stage: probe a handful of
known cells for *any* variation, then full-score only what varies.

**Deliberately not done:** automating `EventMapAtariCheck` on every refusal to get
a hard terrain/entity label. It would be the strongest label, but the call
**writes `wallStopDir`** — the very field being measured — so it would contaminate
its own input. `Shift+O` gives a hard label on demand instead.

**Deliberately deferred:** merging dumps across sessions. Old observations carry
no entity annotation and mixing them would muddy the contamination measurement.
Worth doing now that the measurement is taken.

---

## 6. DATA — preserve before it is overwritten

Ground truth lives **only** in the game folder and a fresh `O` on the same map
**overwrites it**. Preserved copies, outside the game folder and outside the repo:

```
Desktop\Suikoden-Access-groundtruth\2026-09-21\               camp 558w/90b, barracks 156w/46b, + collision dumps
Desktop\Suikoden-Access-groundtruth\2026-09-21-v59-session\   camp 208w/18b, first slide + NPC data, + log
Desktop\Suikoden-Access-groundtruth\2026-09-23-v62-session\   camp 334w/54b, first entity annotation, + log
Desktop\Suikoden-Access-groundtruth\2026-09-28-v64-session-NO-DUMP\   log only - O was never pressed; not scoreable
Desktop\Suikoden-Access-groundtruth\2026-09-28-v64-session\   camp 964w/218b, all 12 otyp=4 sized, fine=, + log
```

**Copy the dumps out after every session.** They must NOT go in the repo —
`.gitignore` excludes all game data and that stays.

---

## 7. RULES PAID FOR IN BUGS — do not relearn these

* **Never Harmony-patch a hot-path method taking a by-ref struct.** Paid for in
  Suikoden 2: a postfix reading `object[] __args` on
  `EventMapAtariCheckFunc1(int, int, ref RECT)` corrupted live collision — walls
  stopped blocking, the player slid off the map edge. `EventMapAtariCheckFunc1`
  and `EventAtariCheckFunc1` are off limits **to patching**.
* **Never patch a base virtual AND its overrides.** Three trampolines in one
  vtable chain crashed the game natively at startup.
* **A hand-patched class must NOT carry a `[HarmonyPatch]` attribute** — it makes
  `PatchAll` abort `Plugin.Load()` and disable the whole mod.
* **Never cap a measurement, and log the zero.** The `pno` sweep was capped at 3
  of 60 and produced a conclusion that could not be drawn from it.
* **Score a candidate against the CLAIM, not against the fields it is built
  from.** §2.1. This is the expensive one.
* **Do not strip diagnostic fields out of a record to keep the set small.** They
  are what makes the next failure readable.
* **An unreadable field must not look like data.** `Math.Sign(int.MinValue)` is
  `-1`; "ehuman unreadable" must not land in the terrain column.
* **`CountOf` must check `Length` as well as `Count`.**
* **Il2Cpp lists are not `System.Collections.IEnumerable`** — index them.
* **This game runs the new Input System exclusively.** Use `Keyboard.current`.
* **Type names collide between the games.** Use `Gsd.FindIn(dll, name)`.
* **Menu readers need a CONTENT guard, not a time window.** The game re-asserts
  the selected cell every frame in some states; a 0.35s debounce keeps expiring
  and the row stutters its own name forever.
* **Controller note, not a mod bug:** a controller in **SXS** mode crashes the
  game natively on first input. **XInput works fine.**

---

## 8. ENVIRONMENT, BUILD, KEYS

Game path: `E:\SteamLibrary\steamapps\common\Suikoden I and II HD Remaster`

```
dotnet build
```

Post-build copies the dll into `BepInEx\plugins`. **`TitleProbe.Version` prints at
startup — confirm it before trusting any test result.** A stale build has masked
several sessions. Currently **v63**.

Read `BepInEx\LogOutput.log` directly. Tags: `[Nav]` Suikoden 2, `[Nav1]`
Suikoden 1. `BepInEx\_decompiled\GSD2.cs` and `GSDShare.cs` are partly UTF-16 —
**pipe through `tr -d '\000'`** before grepping or matches vanish silently. They
carry signatures and parameter names but **never method bodies**.

### Suikoden 2 keys

| Key | Action |
|---|---|
| `O` | walk dump — walked, BLOCKED, SLIDE, ENTITIES, NPC WALKED, edges, stuck, states, and the self-scoring `[SCORE]` section |
| `Shift+O` | one uncapped oracle sweep — all 60 `pno` × 16 dirs, 960 calls. Side-effecting; use deliberately |
| `F9` | ehuman dump |
| `F10` | collision dump — all MAPs, planes raw, `atari_data`, tatemono, OPD tables, MAPINFO |

`F5`–`F12` are taken; Suikoden 1 owns `F5`–`F8`, `F11`, `F12`.

### Files

| File | Purpose |
|---|---|
| `Gsd2Walk.cs` | **Suikoden 2 ground-truth recorder** — the file that matters |
| `NavProbe.cs` | Suikoden 2 static dumps — `F9` / `F10` |
| `Gsd1Path.cs` | Suikoden 1 A\* — the worked example to copy the *shape* of |
| `Gsd1Nav.cs` | Suikoden 1 `Village_c` hook, walked-cell recording |
| `MainMenuReader.cs` | in-game pause menu |
| `TitleProbe.cs` | `Gsd` lookup helpers, `UiText`, `Narration`, version |

---

## 9. WHAT THE NEXT SESSION SHOULD DO

1. **Collect one session with `[ENTITIES]` present** — walk, push into walls,
   push into the static props, push into a moving NPC, then `O`.
2. **Score the `otyp=4` rectangle** by the §5.1 method. If it has zero walked
   violations and covers a useful share of the 36 clean terrain refusals, it is
   the first new collision source since `TATEMONO`.
3. ~~**Score the v61 slide repair**~~ — **done, §10.1: refuted.**
4. Then pick from §5.3–5.7 with the corpus in hand, or start the A\* consumer,
   which is useful under every outcome.

**Quality over speed.** The waypoint-graph fallback remains the worst case, not
the plan. When something is genuinely ruled out, say so plainly and stop circling
it — and per §0 of the pathfinding handoff, reasoning generates candidates and
only an observation chooses between them.

---

## 10. RESULTS, same day — v64

No new play session was needed for either result below: the v62 session
(`groundtruth\2026-09-23-v62-session`) was recorded by a build that already
carried the v61 slide repair, and its `[NPC WALKED]` plus `ent=` annotations
hold 12 `otyp=4` positions and 5 sizes. The scorer is now in the repo:
`tools/gsd2_score.py` (reads dumps from outside the repo; holds no game data).

### 10.1 The slide oracle is CLOSED — the v61 repair is refuted too

Scored against the three camp sessions — 806 walked, 1237 steps, 162 blocked rows.

| test | v61 `[SLIDE]` | `[BLOCKED]` |
|---|---|---|
| distinct (cell, dir) claims | 116, **all diagonal** | 135 |
| **contradicted by an observed step out of that cell in that direction** | **32** (26 from its own session) | **0** |
| expected if the claims were random diagonals at the same cells | ~20 | — |
| claims at the exact cell origin (`moving=(0,0)`), contradicted | 25 of 84 | — |
| diagonal refused in open ground (destination + both orthogonals walked) | 14 | 0 of 64 |

**Worse than chance.** The repair fires where the player then walks the
diagonal. It is not a sub-cell artefact: rows taken at the exact cell origin, the
same condition every `[BLOCKED]` row has, fail at the same rate, and shifting
each claim one cell with or against the motion does not fix it (37 and 28
contradictions).

Two formulations, two refutations, and the second was designed around the
first one's failure. **`wallOrgMoveDir`/`wallMoveDir` while moving are not a
refusal oracle. Do not re-open.** v64 stops recording `[SLIDE]` (NoteSlide is no
longer called; the dump says RETIRED). Even had it survived, every claim was
diagonal, and diagonal refusals do not imply cell solidity (§1.2), so it could
never have closed the frontier.

### 10.2 `otyp=4` rectangle — PRELIMINARY, negative, not conclusive

| | result |
|---|---|
| `otyp=4` entities on the camp | 12 (7 in the walked region, 5 in an unvisited east area) |
| sized (from `ent=` annotations only) | **5 of 12** |
| common anchor with 0 violations vs **all** sessions' walked | **none** of 1458 |
| common anchor with 0 violations vs the **v62 session's own** walked | 72 of 1458 — best covers **2 of 69** cardinal refused cells, 7 tied |
| per-entity best offsets | **disagree**: (+2,−1), (−2,−5), (0,+1), (0,+1); one entity covers no refusal at any offset |
| ceiling: cardinal refused cells within reach of any sized `otyp=4` at all | 18 of 69 |

**A confound found on the way, and it matters beyond this test:** the
`disp=1` `otyp=4` at `(60,74)` sits on a cell walked in an **earlier** session
and not in the session that recorded it. Props are static *within* a session,
not necessarily *across* sessions. **Score entities against the same session's
walked cells.** The cross-session walked union is safe for terrain, not for
props.

**Why not conclusive:** 7 of 12 sizes unknown; `[ENTITIES]` truncated positions
to px, discarding up to 7 of the 8 units a 1-cell rectangle spans — the same
size as the disagreement between entities; and `EVENT_HUMAN.ex`/`ey`/`sx`/`sy`,
which sit beside `w`/`h`, had never been read.

**v64 adds to `[ENTITIES]`:** `fine=` (untruncated `EVENT_POS`), `exy=`,
`sxy=`, `jampy=`, `ppo=` (priorityPositionOffset). The scorer uses `fine=` when
present and sweeps the anchor in 4-unit steps, in both overlap and cell-origin
modes.

### 10.3 What the next session does

1. **Play one camp session on v64** (confirm `v64` in the log). Push into the
   `otyp=4` props from every side — the ones in reach are around px
   `(34..39, 83..91)`, `(44,60)`, `(55,63)` and `(60,74)`; the east group at
   `(86..98, 76..80)` has never been walked. Then `O`. **Copy the dump and log to
   `groundtruth\` before anything else.**
2. Run `tools/gsd2_score.py` with the new session **last**. If no anchor gets
   zero violations against that session's own walked cells while covering a
   useful share of the cardinal refusals, the `otyp=4` rectangle is dead as a
   collision source. Say so and move to §5.3–5.7 or the A\* consumer.

---

## 11. RESULTS — the v64 session, 2026-09-28

Session `groundtruth\2026-09-28-v64-session`: log confirms `v64`; 148,852 frames,
**964 walked, 218 blocked rows, 1813 steps** — the largest yet. Union of all four
camp sessions: 1187 walked, 2538 steps, 380 blocked rows. Every one of the 218
rows is at an exact cell origin. `[CAPS] stuck=True` (4000): logged as truncated,
and no score uses `[STUCK]`. An earlier run that day never pressed `O`; its log
is kept in `...-v64-session-NO-DUMP` and holds nothing scoreable.

Scorer: `tools/gsd2_score.py` now also reports the `wallOrg` split (§11.2) and
expands `~` itself, so the documented command works from PowerShell.

### 11.1 The `otyp=4` rectangle is DEAD as a collision source

All 12 sized; exact positions. What v64 added turned out to carry nothing:
`fine=` is exactly `8 × px` on all 12 (v63's truncation lost nothing), `exy=(0,0)`
on all 12, and `sxy=` repeats the position. No hidden offset field exists.

| test | result |
|---|---|
| common anchor, 0 violations vs **this session's** walked | **0 of 1458** (so coverage 0 of 147) |
| per prop, west group (4 props, 29–37 walked cells around each) | best walk-clean offset covers **0–3** terrain refusals; two props' own anchor cells are walked |
| displayed props `(55,63)` `(60,74)` `(44,60)` | the refusals around them are the **entity channel** (§11.2) and centre on the paired `otyp=2`, not the rectangle; `(60,74)`'s rectangle cells are walked |
| east group, 5 props | never walked, so unscorable — same `otyp`/`disp` as the west group that fails; **not a reason to keep the route open** |

**Correction to §10.2.** The "props are static within a session but not across
sessions" confound is withdrawn. The v59 `Shift+O` sweep lists every `otyp=2`/`4`
at exactly the positions v64 records, so the props did not move. `(60,74)` is
walked because the rectangle is not solid. Nothing moved.

**Pairing, measured on all three displayed pairs:** each displayed `otyp=2` sits
in the slot just before its `otyp=4`, at exactly the `otyp=4`'s `fine` + `(8,8)`.

### 11.2 `wallOrg=255` at a cardinal refusal = the ENTITY channel

§2 listed six passive flags that failed as an entity-vs-terrain label (all
constant). `wallOrgMoveDir` is the one that works, **for cardinal refusals**:

| session | `wallOrg=255` rows (cardinal) | their refused cell's distance to a displayed `otyp=2` | `wallOrg` set rows (cardinal) | cardinal set-rows within 1 of one |
|---|---|---|---|---|
| 09-21 | 12 (6) | all ≤ 2 | 78 (43) | 0 |
| v59 | 3 (2) | all ≤ 1 | 15 (6) | 0 |
| v62 | 7 (3) | all ≤ 2 | 47 (27) | 0 |
| v64 | 23 (12) | all ≤ 2 | 195 (125) | 0 |

**45 of 45 `wallOrg=255` rows sit around the three displayed people, and none
sits anywhere else on the camp.** No row in either class has its cardinal
refused cell walked. `wallMove` is always set on the entity rows.

**Hard labels agree, but only two positions have one.** At `(61,76)` north (§3),
`EventMapAtariCheck` said the map is open; that row reads `wallOrg=255`. At
`(37,77)` (v59 sweep) the map said S, W, SW and SE are solid; all four refusals
recorded there, in two sessions, have `wallOrg` set.

**Diagonals are NOT clean.** A diagonal bump into a person can come through
either channel. v64 `(60,76)` NE → `(61,75)`, the person's own cell, reads
`wallOrg=14`. The only two `[BLOCKED]` rows ever contradicted by an observed step
are also here: v64 `(57,66)` NE and `(58,65)` SW. Both are diagonal, both are in
the terrain channel, and both sit at the `(56,64)` person's corner, where an
`otyp=1 disp=0` wanders. That makes 2 of 271 distinct rows, where before it was
0 of 135. §1.2's diagonal counterexample, `(54,65)` SE → `(55,66)` walked, is an
entity-channel row: a person's corner, not terrain.

**Moving NPCs are a third channel.** v62 `(45,94)`: `wallStopDir=255`,
`objStopDir=2`, `isHitHuman=True`, beside the one sized `otyp=1`
(`(49,94)`, `64x32`, `kyori=42`). `[BLOCKED]` records `wallStopDir` only, so these
never enter it. One observation.

**Person footprints, empirical.** Cardinal refused cells relative to the
displayed `otyp=2`:

| person | paired `otyp=4` | refused | walked right beside |
|---|---|---|---|
| `(56,64)` | 16×16 | (−2,0) (−1,+1) (0,+1) (+1,+1) (+2,0) | (−2,+1) (+2,+1), row +2 |
| `(61,75)` | 16×8 | (−1,0) (0,0) (+1,0) | (−2,0) (+2,0), rows −1 and +1 |
| `(45,61)` | 8×8 | (0,0) | (0,+1) |

The footprint grows with the `otyp=4` size, so `w`/`h` is probably the person's
extent. But `(56,64)` is 5 wide on its own row and 3 wide below it, which is not
a rectangle in player-position space. **Not fitted:** 3 people and about 12 cells
would give a curve-fit, not a rule. It is a candidate only.

**What it buys:**
* §3's contamination can be removed at record time, for cardinal rows, with a
  field that is already recorded and needs no call.
* Displayed people become obstacles known from their **live** position, instead
  of phantom walls baked into a grid.

### 11.3 The §4 frontier moved for the first time

The same definitions as §4. The frontier count reproduces §4 exactly (942, 981),
and so does known-solid at three sessions. §4's one-session known-solid and
edge-resolution figures used a definition that could not be reproduced, so they
are not compared.

| sessions | walked | frontier | known solid (cardinal refused) |
|---|---|---|---|
| 1 | 558 | 942 | 4.9% |
| 3 | 806 | 981 | 7.0% |
| **4** | **1187** | **806** | **18.2%** (terrain channel alone: 138 cells) |

The frontier shrank for the first time, and known-solid more than doubled. **The
§4 verdict stands**: this session was spent deliberately pushing into things,
which is exactly what §4 says a navigation aid cannot require, and 82% of the
frontier is still unknown. It is softened, not reversed. The frontier is finite,
and walking the interior does close it. The learned grid remains a cache and
ground truth, not the primary source.

### 11.4 What next — the project owner's call

1. **Make the label official in the recorder** (a v65 build): split `[BLOCKED]`
   by channel in the dump, cardinal rows only. Fix the stale `[ENTITIES]` header
   too; it still calls `otyp=4` "the best new candidate". Cheap: pure field reads,
   already recorded.
2. **§5.3–5.7 with the corpus in hand**: 1187 walked and 138 terrain-channel solid
   cells, now free of entity contamination. `OBJ_AT`, `EventObjAtariCheck(int,
   int, int)`, the gated `ref RECT` call, `PatternTable.atari_data`, a town map.
3. **The A\* consumer**: useful under every outcome. It can now treat displayed
   people as live obstacles rather than grid cells.
4. Optional, and it strengthens §11.2: one short session with `Shift+O` at a few
   entity refusals and a few terrain refusals. That gives hard labels at more
   than two positions.

---

## 12. The remaining leads — 2026-09-28 evening (v65)

The project owner **approved lead 5.5**, calling `EventMapAtariCheckFunc1` with
our own `RECT`, on 2026-09-28. v65 adds one key, **`U`**, that runs every
in-game lead at the player's position (`Gsd2Leads.cs`). It writes
`suikoden2_leads_<map>.txt` after every stage, and quarantines `Gsd2Walk`
afterwards so our own writes to `wallStopDir` never become `[BLOCKED]` rows.
`Shift+O` is now quarantined as well, because it writes the same field.

### 12.1 Lead 5.6 — `PatternTable.atari_data` WORKS (objects)

Scored offline from the 2026-09-21 camp collision dump, which needed no play.
`atari_data` is a **per-8×8-cell bitmask**: MSB first, `size_x/8` bits per
row, **rows counted upward from the object's base**. The proof is pattern 1,
48×48, whose bytes `CF 3C F3 CF` read as six rows of `110011`: two pillars
with a walkable gap. Row 0 lands on the TATEMONO's own `map_y` row; cells are
`(map_x + c, map_y − r)`.

| rule | walked violations (1187, four sessions) | terrain-channel refusals covered (of 138) | cells |
|---|---|---|---|
| **decoded mask, shift (0,+1)** | **0** | **65** | 467 |
| decoded mask, shift (+1,+1) | 0 | 65 | 467 |
| whole sprite rectangle | 318 | 83 | 1247 |
| old fitted TATEMONO footprint (§1.5) | 0 | 29 of 83 | — |

No observed diagonal step squeezes between two mask cells. The x anchor is
**ambiguous by one cell**: the two shifts differ on 173 cells, and each covers
the same 18 refusals. The union of both is still walk-clean. Of the 73 terrain
refusals the mask does not cover, 39 are within 1 of some sprite and 34 are
nowhere near one. Those 34 are terrain proper.

`MAP.atari_data` values look like four 2-bit quadrant codes (85 = `01010101`).
That decoding was **already tried** (collision log, 2026-09-17: every
quadrant assignment, best 3 of 46), and the `index` planes point into an
all-zero page. It stays dead.

### 12.2 What the signatures alone settle

* **Lead 5.4, `EventObjAtariCheck`**: its three ints are named `pno, dir,
  orgDir`. It is an entity-step query like `EventMapAtariCheck`, not a point
  query. `U` measures it once at the player anyway.
* **Lead 5.3, `OBJ_AT`**: `{px, py, px2, py2, ptn, ptn2}` is a pair of
  pattern-bearing objects, and both checks also take `pno`. Probably
  human-versus-building, so player-local. `U` measures it anyway.
* **New: unnamed helpers.** EVENTCON has interop-renamed statics taking
  `(EVENT_HUMAN, x, x2, y, y2)` or `(EVENT_HUMAN, ano, ref x, ref x2, ref y,
  ref y2)`: rectangle and box helpers, probably for the person channel. `U`
  logs their real il2cpp names.
* **New: HD colliders.** `MapInfoHD` has `ColliderCheck(Vector3 pos, …)` and
  a `RaycastHit2D` buffer, and `MapFootSound` (Dirt/Stone/Grass/…) sits on
  Collider2D objects. Probably footstep and shadow surfaces rather than walls,
  but a collider answers for any point, so `U` dumps every one with calibration
  pairs.

---

## 13. RESULT — `EventMapAtariCheckFunc1` IS THE POINT QUERY (2026-09-29, v65)

Session `groundtruth\2026-09-29-v65-leads`: nine `U` presses on the camp, one in
the tent. 10,680 `Func1` calls per press, **0 threw**, about 10 ms per press.
Position never changed. Movement state did change (`currentMoveDir`,
`wallMoveDir`, `isMoveChanged`), and the quarantine kept every resulting latch
change out of `[BLOCKED]`: 4 were suppressed, all logged.

### 13.1 The terrain answer

`EventMapAtariCheckFunc1(0, dir, ref RECT{x=8·px, y=8·py, w=8, h=8}) != 0` means
**cell (px,py) is solid map terrain**. `dir`, `w` and `h` make no difference.
The other geometries give the same answers shifted by one cell, so it asks about
the cell containing `(rec.x, rec.y)`. Over nine overlapping 21×21 windows (1950
cells) there are **0 inconsistencies** between presses.

### 13.2 The full rule, scored on four sessions of camp ground truth

    blocked(x, y) = Func1(cell) || objectMask(x, y)      // objectMask = §12.1, BOTH x anchors
    diagonal step refused  iff  blocked(dest) || (blocked(side x) && blocked(side y))

| | result |
|---|---|
| walked cells called blocked | **0 of 648** |
| terrain-channel **cardinal** refusals explained | **50 of 50** |
| terrain-channel diagonal refusals explained | 46 of 48: one lies beside a person (§11.2), one has both side cells solid, which the corner rule covers; the three others are at a window edge |
| observed diagonal steps past exactly one solid side cell | 59, so the engine allows corner-cutting past a single corner |
| observed diagonal steps between two solid side cells | **0** |
| Func1 alone (no object mask) | 18 of 50: map and objects are separate layers |
| tent `a0v0m1` | 0 of 102 walked cells called solid |

The player footprint is **the single destination cell**. Every wider footprint
tested (up to ±2 in each direction) violates walked cells.

This is Suikoden 1's architecture: `GNDAtariCheck` becomes `Func1`, and
`BLDAtariCheck` becomes the pattern mask. **The learned grid is no longer
needed as a source.** It stays as the scoring referee.

### 13.3 The other leads, closed

* The interop-renamed rectangle helpers are `<EventFormTbl>g__CanCall_OSJINB`,
  `CanCallEvent(X/Y)`, `Slide_*` and similar: **event-trigger range checks**
  (talk/examine), not collision.
* The HD colliders: all 11 on the camp are tagged `FootSound` (grass polygons
  and boxes), and the tent has carpet. **Footstep surfaces only.**
* Leads 5.3 and 5.4: unneeded now. 5.4 returned `int.MinValue` for some
  directions, and neither was pursued.
* Lead 5.7, doors: `tokusyu_atari` and `MapInfoHD.eventObjects` are empty on the
  camp and the tent. **Still untested**; press `U` in a town.

---

## 14. The Suikoden 2 pathfinder — v66 (play-tested 2026-09-29: see §15)

`Gsd2Path.cs`, on Suikoden 1's keys: `J`/`L` previous/next target, `K` read,
`Shift+J`/`L` category (All / People / Objects), `P` pathfind and speak the route.

* **Collision** is §13.2 exactly: `Func1(cell)` for terrain, both-anchor pattern
  masks for objects (built once per map from `tatemono_data` and `machi_opd`),
  and live people. `otyp=1` blocks its own cell; a displayed `otyp=2` blocks
  (−1..+1) on its row, plus the rows its paired `otyp=4` spans.
* **A\***: 8-way, costs 10/14, octile heuristic. A diagonal is legal unless both
  side cells are blocked, which matches what the engine allows. A route ends on
  the target cell or one of its 8 neighbours.
* **Self-check**: if the player's own cell reads blocked, a people or object
  cell there is dropped and logged. If `Func1` itself says so, there is no route.
* **Units**: routes are spoken in px cells (half tiles), "north 6, east 3".
  The half-tile is the engine's own grid step. Whether that is the right unit for
  a listener is for play to decide.
* **Targets**: every non-player `ehuman` except parked `(16,16)` slots; names from
  `EventHumanNameGet(slot)`, else "person"/"object". The first `J`/`K`/`L`/`P` on
  each map logs the whole entity list with names, as `[Nav2] entities on …`.
* Every `P` is quarantined like `U`, and logs `[Nav2] path to …` with the Func1
  call count and which layer blocked how often.

Not yet: doors and exits (lead 5.7 needs a town), learned or marked places, and
re-planning after a refusal.


## 15. First play test of the pathfinder — 2026-09-29 (v66 → v67)

Log: `groundtruth\2026-09-29-v66-pathfinder\LogOutput.log` (no dumps; O was not
pressed). Maps tried: the camp `a0v0m0` and the sentry area `a0v0m2`.

* **Routes are right.** Two NPCs the owner recognized on screen (the wandering
  soldier, slot 13 on m0, and the sentry, slot 12 on m2) were reached by
  following the spoken routes, including a moving target re-planned on each `P`.
  No refusal on any route. Func1 threw 0 times in 34 requests, all ≤13 ms.
* **Phantom = a hidden slot.** Slot 8 at (47,85) on m0 is `otyp=1 disp=0`; P
  answered "already there" beside an empty cell. v66 listed `otyp=1` whatever
  its `disp`, and `BuildPeople` blocked their cells too. On m0, 6 of the 9
  `otyp=1` slots are `disp=0`. On m2, hidden slot 8 sits on the exact cell of
  the displayed slot 12. **v67: only `disp=1` entities are targets or people
  obstacles.** A displayed `otyp=4` is the second half of the `otyp=2` beside it
  (m0: 17/18, 19/20, 21/22; m2: 12/13, 14/15), so it is no longer a separate
  target either.
* **The v66 names were wrong — DEAD as a name source.** `EventHumanNameGet`'s
  parameter is `pno`, a character number, and `EVENT_HUMAN` has no field that
  holds one (fields checked in the decompile: no chara/pno). Called with the
  slot, it returned the recruitable-character roster by index: slot 8 'character N' and slot 13
  'character O' on BOTH maps, and the "character O" who spoke was a "soldier role
  3". v67 says "person" and numbers them nearest first. Real names need the
  Suikoden 1 route: the player names a target by ear (Gsd1Path Shift+K,
  `Gsd1Names`).
* **Objects category**: in v66 it held only hidden `otyp=3/4` slots. In v67 it
  holds displayed `otyp=3` only, which is none on these two maps. Real objects
  (chests, doors, exits) are future work.
* The arrival message is now "you are next to it": the goal set is the target
  and its 8 neighbours, so adjacent counts as arrived.


## 16. Shift+K naming — v68, built, NOT yet tested in play

The Suikoden 1 route, reused: select a target, Shift+K, type the name heard in
dialogue, Enter (Escape cancels). Gsd1TextInput is reused unchanged.

* **Store**: `Gsd2Names`, file `suikoden2_npc_names.txt` next to the plugin,
  separate from Suikoden 1's. Line: `<mapKey>:<slot> = <name>|<signature>`.
* **Signature** `t<otyp>.<w>x<h>` guards against slot reuse (Suikoden 1's character A →
  Character B). It is UNMEASURED: whether it tells two occupants apart is not
  known. The `[Nav2] entities on …` line now also logs `ano eano fpno ityp tdat`
  per entity, so two sessions at different story points can settle it. A name
  whose signature no longer matches is dropped and logged once.
* **Player freeze while typing**: a Harmony PREFIX on
  `EVENTCON.NewEventPlayerMove()` (zero arguments; the hooking rule allows it)
  skips the move while entry is open and for 0.3 s after, so the confirming
  Enter does not also talk. Postfix capture of EVENTCON is unaffected. Plus
  Gsd1TextInput's own `Time.timeScale = 0`. Whether the Escape that cancels also
  opens the game menu is untested.

## 17. v69–v70: loading, typing, NPC data research, cleanup — 2026-09-29

v68 session (groundtruth `2026-09-29-v68-naming`): naming worked, but typing
a two-word role name (placeholder: "gate guard") fired O three times and U once,
and saved "gate uard": the letter after the space was lost.

### 17.1 Fixed in v69
* **A stray menu help line on every save load**, both games: the
  pause menu's Log help line, written into Text_Auto while the game builds that
  menu hidden during the load (`UIMainMenuPartyList.SetCursor(-1,-1)` precedes it
  in every session log). `LoadAnnouncer`: a Text_Auto under a UIMainMenu parent
  while the menu is closed is not spoken; "Now loading" is said once per load,
  from the save screen's `funcType` (Load/DirectLoad) leaving mode Select/Confirm,
  with the hidden build as fallback. WHICH mode change marks the start is logged
  as `[Load] save screen funcType=… mode a -> b` — unmeasured until a session.
* MainMenuReader: SetCursor(-1,-1) no longer marks the menu open; a prefix on
  UIMainMenu.Open() opens a 1 s "opening" window, because Open() writes its first
  description before its postfix runs (v59 log).
* O / U no longer fire while a name is typed.
* The lost "s": text entry took ONE key per frame, so a letter in the same frame
  as Space was dropped. It now reads Keyboard.onTextInput (every character, in
  order, in the player's layout); polling of all keys per frame is the fallback.

### 17.2 NPC data — what the decompile says, and the F4 dump (v70)
Static signatures (no bodies) name where an NPC's identity can live:
* `EVENTCON.mdat` (MACHIDAT) → `epersondat[]` (EPERSONDAT: `fp_no`, `pno`,
  `kao_csr`, `hito_opd_table`). `pno` is a CHARACTER number — exactly what
  `EventHumanNameGet(pno)` takes. How an entity's `fpno` selects its row is the
  first thing to settle (camp soldiers read fpno 65121, sentry soldiers 64912;
  hidden slots read 80 / 145 / 140).
* `mdat.eventdata` (EVENTDAT) → `mapeventdat[]` (MAPEVDAT: `eventobj[]` with the
  same fields as EVENT_HUMAN — the initial state of the people — and
  `eventdat[]`, EVENT_WK: `etyp`, `eventForm`, `eventCom` script bytes) and
  `sce_msg[]` (message index lists).
* `EVENTCON.nameID` / `isQQQName`: the speaker of the line on screen. The message
  log UI records lines by nameID (`SetMessageLog(nameID, …)`).
* `TextMasterData.stayTextSet` (Dictionary<string, TextSet>), TextSet.fileNodes
  (uint → string): every loaded text table. `SystemTextBaseID` / `TextBaseID`
  give per-label base ids.

**F4** (Gsd2Npc) writes all of that, plus doors (MAP.tokusyu_atari,
EVENTCON.doorStateList, MapInfoHD.eventObjects — carried over from U), to
`suikoden2_npcs_<map>.txt`. Pure reads plus EventHumanNameGet per distinct pno.
Every Suikoden 2 dialogue line also logs `[Npc] speaker '…' nameID=… nearest
displayed slot …`.

Plan: talk to a few NPCs, then F4. Join (a) the speaker name heard ↔ nameID from
the log, (b) nameID ↔ a text-table entry holding that name (find the table and
offset), (c) ehuman slot ↔ eventobj row (same fields), (d) fpno ↔ epersondat row
↔ pno ↔ EventHumanNameGet. If (c)+(d) hold, named characters are named by the
game; if the talk script bytes reference a nameID, generic NPCs are too.

### 17.3 Cleanup (v70)
* Deleted: Gsd2Leads.cs (U; every lead closed, doors moved to F4), NavProbe F9
  (superseded by F4) and F10 (collision-grid dump, ~800 lines; its question is
  answered), Gsd2Walk's Shift+O oracle sweep and the retired slide recorder.
  NavProbe 1177 → ~190 lines, Gsd2Walk 1430 → ~1010. The O walk dump stays: it is
  the referee for a new map, and gsd2_score.py reads only it.
* The per-second `[Nav]` position line is written only when it changes; a P press
  no longer writes two quarantine lines unless a latch change was held back.
* **Pathfinder goal fix**: v66–v69 excepted the target from the people layer and
  aimed at its cell, so a route could end INSIDE a person ("character P (20,23) from
  (18,23): east 1", v66 log — the player was already beside the sentry). Now the
  target's footprint is solid, every free cell touching it is a goal, ONE
  multi-goal A* (octile to the nearest goal) finds the closest by route, and the
  route ends "then face <dir>" (cardinal preferred).
* README brought up to date for Suikoden II.

## 18. v71 — sanity pass: copyright, performance, stability (2026-10-06)

No play session; everything below builds clean and is UNTESTED in play. Every
change is either behaviour-preserving or logging-only, except the two marked
**behaviour**.

### 18.1 Copyright audit (repository and full git history)
* History holds only code, docs, the scorer and one screenshot of the game's
  install-folder listing. No log, dump, decompiled source or asset was ever
  committed. No dialogue line appears anywhere. Japanese text: only the engine's
  5-character "name undefined" placeholder, matched at runtime (functional).
  Character / place names appear only in explanatory comments (Suikoden 1 names
  are read live from the game, never stored).
* The one verbatim game UI sentence (the Log command's help line) was
  paraphrased in LoadAnnouncer.cs and §17.
* `.gitignore` now also covers every Suikoden II player file and diagnostic dump
  (`suikoden2_*.txt`, `suikoden1_*.txt`): the F4 dump writes EVERY loaded text
  table, i.e. the script, and nothing stopped it being committed. The F4 file now
  says so in its first lines. Screenshots (`*.png` etc.) are ignored too; the
  install-folder screenshot was untracked and kept on disk.

### 18.2 Performance
* **UiText.Member / Element cache their reflection lookups** per (type, name).
  The hottest path in the mod: every reader, the Suikoden 1 navigator and the
  Suikoden 2 recorder go through it, and each read used to redo GetProperty +
  GetField. Resolution order is unchanged. Gsd2Walk / Gsd2Path now share it.
* **Gsd.FindIn is cached** (it walked every type in GSD1/GSD2.dll per call).
* **One TMP_Text.set_text postfix instead of two.** Every text write in the game
  ran two postfixes, each copying the component's name out of Il2Cpp. Dialogue
  (Dlg) and descriptions (`DescriptionReader`, formerly TitleProbeSetTextPatch)
  are now both fed from TmpSetTextPatch. If descriptions ever go silent while
  dialogue works, that call is the place to look.
* Silent-component text is logged once per (component, text) instead of on every
  write; the per-instance text map is bounded (it never shrank).
* **A* (both games) uses a PriorityQueue keyed (F, insertion order)** instead of
  a linear scan of the open list: O(log n) per step instead of O(n). Proven
  route-identical to the old scan on 2000 random 8-way grids (0 differences),
  so Suikoden 1's spoken routes do not change.
* Gsd2Walk: the per-frame 19-field state string, `[STATE HISTOGRAM]` and
  `[STUCK]` are retired (their question is settled and the scorer reads neither);
  the map key string is rebuilt only when an input changes.
* Battle cursor: the label list is rebuilt at most 10×/s while the cursor rests
  on one row (it was every frame); a changed label is still caught within 0.1 s.
* Speech: NVDA's "is it running" IPC check is reused for 1 s; a broken SAPI
  fallback logs once and stops, instead of one error per spoken line.
* Suikoden 1's once-a-second position line is written only when it changes.

### 18.3 Stability — **behaviour**
* **Game switching without restarting.** The launcher can start the other game
  in the same process, and the controller of the game left behind stays
  referenced: after Suikoden 1 then Suikoden 2, Suikoden 1's J/K/L/P handler kept
  running on a dead Village_c beside Suikoden 2's. `Gsd1Nav.Live` /
  `NavProbe.Live`: whichever controller was captured MORE RECENTLY is the live
  game; only it ticks, and game detection uses it. A one-game session behaves
  exactly as before (the other is never captured).
* O / F4 report "no live EVENTCON" instead of acting on a stale one.

### 18.4 Not changed, deliberately
* Suikoden 1's diagnostic keys (F5–F12, Gsd1Probe) stay: they are its documented
  toolkit (NAV-STATUS) and run only on a keypress.
* Gsd2Walk (O) stays as the referee for new maps.
* `TEXT-HOOKING-HANDOFF.md` (untracked, no game content) was left untracked.

### 18.5 v72 — legal-hygiene check against the project owner's rules (2026-10-16)
* No game assets, executables, logs, dumps or decompiled files are tracked or
  have ever been committed; the build output holds only SuikodenAccess.dll (+ the
  System.Speech NuGet dll); no links, download pointers, DRM bypass or paid
  features anywhere; README states unofficial / unaffiliated.
* **Fixed:** Gsd1Probe kept a copy of the GSD1 `EVENT_COMMAND` / `EVENT_CHECK`
  enum names (~120) to gloss F8 byte dumps. They are now read from the player's
  GSD1.dll at runtime; no copy remains in the repo.
* `.gitignore` gains backstops for game folders (`BepInEx/`, `*_Data/`,
  `il2cpp_data/`, `StreamingAssets/`, `interop/`, `*.exe`, `*.pdb`) and the
  ground-truth folder. README lists third-party components and licences.
* Left for the owner to decide: the git history still holds the old enum tables
  and the install-folder screenshot (removing them means rewriting history and
  force-pushing); game names in comments/docs (character and NPC role names used
  as examples); the launcher words "Gallery" / "Settings" / "Language Settings"
  spoken from our own strings.
* 2026-10-16 follow-up: game names in every comment and doc were replaced with
  consistent neutral placeholders ("character A", "soldier role 1", "town A",
  `FP_A`); `LEGAL.md` holds the rules and how the repo meets them. The launcher
  words stay as they are (generic), with runtime reading noted as an option.
