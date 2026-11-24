# Suikoden 2 navigation — fresh-eyes review, 2026-09-21

> **Historical document, superseded.** Kept for the reasoning, not as
> instructions. For the current state, start with
> [`HANDOVER.md`](../../HANDOVER.md) in the repository root. Version numbers,
> next steps and file locations below are out of date.

**A review of `Suikoden-Access-GSD2-HANDOFF-2026-09-19.md` §9, answering its three
questions.** Plugin version reviewed: **v58**.

Everything in §1 below was measured against the preserved ground truth
(`suikoden2_walk_a0v0m0.txt`, `suikoden2_walk_a0v0m1.txt`), not inferred from the
handoffs. The handoffs' own numbers were re-derived rather than taken on trust,
and they check out; what follows is what fell out of the dumps that nobody had
extracted yet.

**Ground truth is preserved.** §6 of the handoff asked for this and it had not
been done. The four files are copied to
`Desktop\Suikoden-Access-groundtruth\2026-09-21\`, outside both the game folder
and the repo.

---

> **RESULTS ARE IN — READ §7 FIRST.** The measurement build was run the same
> day. **The slide oracle proposed in §1.1 and ranked first in §3 is REFUTED.**
> The oracle's player-locality is now properly measured and the question is
> closed, which also undercuts the reserved write route. §1.2, §1.3, §2 and §4
> stand. §7 has the numbers and what they change.

## 0. The one-paragraph version

There is a **second free oracle** in `EVENTCON` that nobody had listed:
`wallOrgMoveDir` records the direction the engine refused *while the player keeps
moving*, which is the case `wallStopDir` structurally cannot see. It is
comparable in volume, disjoint, and — the part that matters — it fires along the
route the player walks rather than only where they deliberately push. Separately,
scoring the existing blocked set against the walked set shows that **a refusal is
a fact about a directed edge, not about a cell**: cardinal refusals imply cell
solidity with 0 violations in 67, diagonal refusals do not, with 3 violations in
42. And the frontier measurement says the learned-grid proposal is premature by
about one session: after six minutes of walking, **8% of the frontier is known**.
The recommendation is to take one measurement build first and decide with numbers.

---

## 1. NEW MEASUREMENTS

### 1.1 A second oracle: `wallOrgMoveDir` during movement

`wallStopDir` only ever fires when the player has fully stopped. Measured across
both maps: `wallStopDir != 255 AND speed != 0` is **0 frames out of 109,123**.
Every refusal the player *slides past* is therefore invisible to the current
recorder.

Those refusals are recorded anyway, in two fields already present in
`Gsd2Walk.StateFields` and never read by `NoteBlocked`:

| | camp `a0v0m0` | barracks `a0v0m1` |
|---|---|---|
| frames | 22,774 | 86,349 |
| `wallStopDir != 255` | 3,978 | 1,928 |
| **slide: `wallOrg != 255 && wallMove != wallOrg && speed != 0`** | **3,618** | **1,462** |
| overlap between the two | **0** | **0** |

The relation is exact rather than approximate:

* `wallOrgMoveDir == targetMoveDir` in **5,080 of 5,080** moving frames with
  `wallOrg != 255`, across both maps. Zero exceptions.
* Every observed substitution is diagonal to cardinal. All eight legal pairs
  appear: `14>3 13>2 13>1 11>2 12>3 14>1 11>0 12>0`.
  **No cardinal-origin substitution has ever been observed.**

**The rival explanation was tested and is contradicted.** If this were
grid-alignment rather than collision, ordinary turning would produce it too.
Ordinary turning has its own separate path with `wallOrg = 255` (1,508 frames on
the camp), and *that* path does contain cardinal-to-cardinal turns (`3>0`, `1>3`,
`2>1`, `0>2`) and cardinal-to-diagonal ones. The `wallOrg` path never does. The
two mechanisms are disjoint in the data.

Supporting name evidence, not load-bearing: `EVENTCON` also declares
`suberiTargetX` / `suberiTargetY` — *suberi* is slipping/sliding — so the
wall-slide is a first-class concept in this engine rather than a side effect.

**Reading:** `wallOrgMoveDir` while moving is a refusal of that direction at that
position, captured without the player stopping. Pure field read, no call, nothing
perturbed.

**Status: CANDIDATE, not confirmed.** It has not been scored against walked
cells, because positions were never recorded for these frames. That is what the
v59 recorder change below is for.

> **OUTCOME: REFUTED.** The v59 build recorded the positions and the first
> scoring pass killed it — 22 of 60 observations contradicted by the player's own
> observed steps. Every check in this section was about the *fields*; none was
> about the *position*, which is what the claim is. See §7.1. The section is left
> as written, because how a wrong candidate looked convincing is the useful part.

### 1.2 A refusal is a fact about an EDGE, not a cell

`Gsd2Walk.Dump` writes into every dump: *"'refused' is the neighbouring px cell
the engine would not let the player enter. That cell is SOLID."*

That is an inference stated as an observation, in the file whose whole purpose is
to keep those apart. Scored against walked cells, on both maps:

| refused cells derived from... | barracks | camp | walked-cell violations |
|---|---|---|---|
| at least one **cardinal** refusal | 21 | 46 | **0 of 67** |
| **diagonal** refusals only | 21 | 21 | **3 of 42** |

The camp counterexample is explicit: at `(54,65)`, `blockedDir=12` (SE) marks
`(55,66)` solid, and `(55,66)` is in the walked set. Two more on the barracks.

This is the standard diagonal-corner case: an engine may refuse a diagonal
because of what is beside the destination, not because of the destination itself.

**Design consequence, and it is cheap:** learn **blocked directed edges**, and
derive cell-solidity only from cardinal refusals. A learned occupancy *grid*
built by marking `refused` cells solid accumulates phantom walls at every
diagonal corner, and a phantom wall is indistinguishable from a real one after
the fact. `Gsd1Path.FindPath` shows how small the change is: a single
`if (Blocked(nx, ny)) continue;` in the neighbour loop becomes
`if (BlockedEdge(cur.X, cur.Y, d)) continue;`.

### 1.3 `objStopDir` is PERSON collision, not object collision

`objStopDir != 255` and `isHitHuman == True` are the **same 420 frames** on the
camp, and the same **0 frames** on the barracks. Perfect correspondence, both
directions, both maps.

Two consequences. NPC contact never sets `wallStopDir`, so the existing recorder
is already immune to baking moving NPCs into the grid — by luck rather than by
design, and worth writing down so it stays true. And the name invites exactly the
wrong reading: "obj" here means a person, not a `TATEMONO`. Any slide-based
recorder must carry the same filter deliberately.

### 1.4 The "oracle only answers for pno 0" claim is not supported

There is exactly **one** surviving `Shift+O` reading, in `LogOutput.log` at line
851. Both preserved walk dumps report `[ORACLE] 0 readings`.

That reading says `ehuman=60`, and `SampleOracle` caps the sweep at
`maxPno = Math.Min(people, 3)`. So the claim rests on **3 of 60 entities, at one
position**, where pno 1 and 2 were presumably standing in the open. All-zero is
the expected result under *both* hypotheses, so the measurement cannot
distinguish them. It is also a live cap on a measurement, which is the rule §7
says was paid for in bugs.

The same reading is *better* evidence than it has been credited with. At
`(22,66)` with `wallStopDir=2`, pno=0 returned `1` for dirs **2, 11, 13** — west,
south-west, north-west, and no others. Geometrically coherent, and it shows the
oracle answers about diagonals as well as cardinals.

### 1.5 Smaller findings

* **NPCs do not carry their own wall fields.** `EVENT_HUMAN` has no
  `wallStopDir` equivalent — the wall state lives on `EVENTCON`, the controller.
  So "read every NPC's refusals for free" does not work. Only their *positions*
  do. Ruled out; do not re-open.
* **`tokusyu_atari count=0` on both measured maps.** The doors/exits lead is
  **untested, not dead** — a camp and a tent interior are precisely the maps that
  would not have any. It needs a town map before anything is concluded.
* **`MAP` is genuinely exhausted.** Its full field list is `ID atari_data
  csr_kazu han_new index index_new machi_opd maxh maxw pri shad srn_kazu srn_type
  tatemono_data tatemono_kazu tokusyu_atari tokusyu_kazu typ up`. Every
  non-scalar has been swept. The handoff's §2 conclusion holds on inventory
  grounds as well as on scoring grounds.
* **Never read:** `BGCON.index` (distinct from `MAP.index`), `BGCON.colno`,
  `MACHIDAT.etcdata` / `dummy_etcdata`, `EVENTCON.bounds`.
* **`EventObjAtariCheck(int, int, int)`** — private, returns int, three ints.
  Not in any document. Three ints is the right arity for `(pno, x, y)`.

---

## 2. ANSWER TO §9.3 — the load-bearing inference

The §2 inference conflates two claims:

* **(a)** the engine keeps no flat grid anywhere;
* **(b)** no arbitrary-point collision query is reachable.

Only **(b)** matters to this project, and **(a) does not imply it.**

`EventMapAtariCheckFunc1(int, int, ref RECT)` is a *rectangle* query. If the
engine's collision primitive is rect-versus-world rather than point-versus-grid,
then (a) is probably **true** and (b) is probably **false** — there would be a
general query, and it simply would not be a table lookup. The five negatives are
all about static tables. They are strong evidence for (a) and close to no
evidence about (b).

**So: §2 is probably right and mostly irrelevant, and the leads in §3 are ordered
by odds on the wrong question.** What needs testing is reachability, not
architecture.

What would test it, cheapest first:

1. **The uncapped `pno` sweep.** All `ehuman` entries, every direction, with each
   entity's position logged beside its row — a row of zeros is uninterpretable
   unless you know whether that entity was next to anything. If any `pno > 0`
   ever returns 1, the oracle is entity-general rather than player-local and §2's
   framing is wrong as written. One keypress, one session, direct falsifier.
2. **NPC positions as additional walked-cell ground truth.** `ehuman[i].x/y` are
   readable per frame, there are 60 of them, and they walk. "A cell an NPC stood
   on is walkable" is the same falsifier as the player's, from 60 sources instead
   of one, at zero risk and zero play effort.
3. **`EventObjAtariCheck(int, int, int)`**, swept reflectively alongside lead 3.

A direct test of (a) itself: there is no cheap one, and it is not needed. Stated
plainly here so it is not circled again.

---

## 3. ANSWER TO §9.1 — the leads, reordered

| | lead | why here |
|---|---|---|
| ~~1~~ | ~~**Slide oracle** (`wallOrgMoveDir` while moving) — §1.1~~ | **REFUTED, §7.1.** A v61 repair exists and is untested |
| ~~2~~ | ~~**NPC walked cells + uncapped `pno` sweep**~~ | **SPLIT, §7.2–7.3.** The `pno` half is closed as a measured negative; the NPC half survives with a contamination caveat |
| **3** | `OBJ_AT` + `EventNObjTObjAtariCheck` (was 3) | the only construct in the inventory carrying *its own rectangle*. Still takes `pno`, so it may remain entity-local. Needs the before/after snapshot before trust — correctly flagged in the handoff |
| **4** | the `ref RECT` path (was 5) | see the note below |
| **5** | `PatternTable.atari_data` (was 4) | the handoff's odds are right. Keep it low — but note it is the only lead with a *bounded, already-known* payoff, and the safest thing on the list |
| **6** | `BGCON.mapobj` (was 1) | agree, low. Slight promotion on one ground only: §2's own hypothesis is "structures built at load", and `mapobj` / `mapobj_sorted` is where that hypothesis predicts something |
| — | `BGCON.kakikae` (was 2) | agree, dead as a collision lead. But it is tile *rewriting*, which is how doors open, and `EVENTCON` has `doorStateList` / `isDoorOpen`. That is a note for the learned-grid design — a learned wall may be a closed door — not a route to collision |

**On the `ref RECT` path, a distinction that is not a re-litigation of the rule.**
The rule paid for in bugs is about *Harmony-patching* a hot-path by-ref method:
Harmony round-trips the struct through boxing on the engine's own calls, and that
is what corrupted live collision. *Calling* `Func1` with our own `RECT` from
managed code is a different mechanism — no trampoline, no interception, the
engine's own calls untouched. The rule as written covers patching, and this is
not patching. It remains genuinely risky for a separate reason: `Func1` is
private, and `EventMapAtariCheck` was already caught writing to live controller
state, so assume `Func1` does too until a snapshot says otherwise. It stays gated
on an explicit decision, as the handoff says. The only claim here is that the
constraint does not forbid it.

---

## 4. ANSWER TO §9.2 — the design path

**Verdict: the A\* consumer is right to build. The learned grid as the grid
source is premature — by about one play session.**

The handoff's decisive argument ("the A\* layer is identical whichever way the
grid is obtained") holds. One amendment, from §1.2: make the predicate
**edge-shaped** (`BlockedEdge(x, y, dir)`), not cell-shaped. Every grid source
can fill an edge predicate; not every source can safely fill a cell predicate.

### The number that decides the rest

| | camp `a0v0m0` (~6 min) | barracks `a0v0m1` (~24 min) |
|---|---|---|
| walked cells | 558 | 156 |
| **frontier** — unwalked 8-neighbours of walked cells | **942** | 240 |
| frontier known solid | 83 = **8%** | 40 = **16%** |
| directed edges out of walked cells resolved | **47%** | 50% |

The frontier is **1.7x the size of the known walkable set**, and 92% of it is
unknown. This is not a grid with gaps; the gaps dominate the boundary — and the
boundary is where every routing decision lives: doorways, gaps between tents, map
edges. Fewer than half the moves available from cells the player *has* visited
are known either way.

### The four objections, pressed

**"You cannot path somewhere you have never been."** The real cost is worse than
stated, and it is not about unvisited *places*: even inside fully-explored
territory, half the moves are unknown. But this is also exactly what the slide
oracle attacks — frontier cells are by definition adjacent to walked cells, and
the slide fires while the player walks past them. Which is why this should be
measured before it is decided.

**"Is optimistic replanning acceptable in an audio-only interface?"** This is the
objection to weight highest, higher than §4 weights it. At 8% frontier knowledge
the mod will route the player into walls routinely, and a spoken route that turns
out to be wrong is not a small degradation — it costs trust in the navigation
feature, and the trust is the product. Suikoden 1 never does this.

Mitigable, and one mitigation is missing from the proposal: **optimistic does not
mean free.** Give unknown cells a cost multiplier, so A\* prefers a longer
known-good route over a shorter speculative one and crosses unknown ground only
when there is no alternative. That converts most replans from "walked into a
wall" into "route was slightly longer than optimal". Pair it with confidence in
the narration: a route known to its end should sound different from one verified
for three steps.

**"Does the partial object model help or hurt?"** The handoff's instinct is
right; made concrete: three states per cell — *observed walkable*, *observed
blocked*, *unknown* — and let the object model act **only** as a cost prior that
raises the price of unknown cells. Never as an occupancy write. Then a wrong
object inference produces a longer route, never a wrong one, and the measured set
stays uncontaminated. The zero-over-blocking result on both maps means the prior
is safe in the direction that matters.

**"Is the §2 inference right?"** See §2 above. Probably right, and the wrong
question.

---

## 5. RECOMMENDED SEQUENCE

1. **Build the A\* + target + describe layer**, against an **edge** predicate,
   with three-state cells and unknown-cost weighting. Unconditionally useful and
   correct under every outcome below.
2. **One measurement build, one session, three free measurements**, all landing
   in the same dump — this is v59, below.
3. **Recompute the frontier number and decide with it.** If known-solid frontier
   goes from 8% to above roughly 40%, the learned grid is viable and gets built.
   If it stays low, leads 3/4 or the §5 write route become the answer, with the
   numbers to justify which.

### What would justify the write route (§5 of the handoff)

Nothing has changed yet, so it stays reserved. But the specific result to watch
for is named here: **if the uncapped `pno` sweep shows the oracle answers for
entities other than the player**, the write route stops being "write to game
state and hope" and becomes "move a slot the engine already ignores, having
measured that non-player entities are legitimate query subjects". That is a
category drop in risk, and it is the only thing in sight that would change the
argument.

---

## 6. THE v59 MEASUREMENT BUILD

Three changes to `Gsd2Walk.cs`, all in service of step 2 above.

1. **Slide refusals, recorded with positions.** New `[SLIDE]` section, kept in a
   **separate set from `[BLOCKED]`** — it is an unvalidated candidate and must
   not contaminate the referee's own data. Guarded on `wallOrg == targetMoveDir`
   (true in 5,080 of 5,080 observed frames) so a latched stale value cannot be
   stamped onto later positions, and on `isHitHuman == false` per §1.3. When the
   guard fails it is logged rather than silently skipped.
2. **NPC positions as a second walked set**, keyed by `otyp` so the composition
   stays filterable offline rather than being filtered on a guess now.
3. **The `pno` sweep uncapped**, with each entity's `otyp` and position logged
   beside its row, so a row of zeros can be told apart from an uninformative one.

The `[BLOCKED]` header text is also corrected: it asserted that a refused cell is
solid, which §1.2 shows is false for diagonals.

### How to score the result

After one session on a map with both walls and NPCs:

* **Does the slide oracle survive?** Score `[SLIDE]` cells against `[WALKED]`.
  Cardinal-derived entries should give 0 violations, as `[BLOCKED]`'s do. If they
  do, the oracle is confirmed and the blocked set roughly triples.
* **Does the frontier close?** Recompute known-solid frontier with `[SLIDE]`
  folded in. 8% to above 40% makes the learned grid viable.
* **Is the oracle entity-general?** Any `1` from a `pno > 0` whose logged
  position is adjacent to a known refused cell settles §2.
* **Log the zeros.** All three can come back negative, and a negative here is a
  result that closes a route rather than a failed session.

---

## 7. RESULTS OF THE v59 MEASUREMENT BUILD — same day

Run on the camp `a0v0m0`, 6286 frames, plugin v59 confirmed in the log. Dump and
log preserved at `Desktop\Suikoden-Access-groundtruth\2026-09-21-v59-session\`.
No cap was hit on any set. Yield: 208 walked, 18 blocked, 60 slide, 77 NPC cells
over 4 otyp, 120 oracle rows from two presses.

Scored against the union of both sessions on this map — **672 walked cells, 107
blocked observations, 930 observed steps.**

### 7.1 The slide oracle is REFUTED

| test | `[SLIDE]` | `[BLOCKED]` |
|---|---|---|
| refused cells landing on a walked cell (cardinal rows) | — (no cardinal rows) | 0 of 53 |
| **(pos, dir) contradicted by an observed step from that cell** | **22 of 60** | **0 of 107** |
| of those, contradicted by the same session's own steps | **20** | — |

**Cause, measured.** 274 of 966 moving frames with `wallOrg` set had
`currentMoveDir` equal to the *diagonal* that `wallOrg` called refused. The wall
fields keep their last value while the player walks on through that direction. A
row said "north-west is refused here" about a cell the player was in the act of
leaving north-west.

**The guard was vacuous.** v59 required `wallOrgMoveDir == targetMoveDir` because
it held 5080 of 5080. It held 966 of 966 again — *because the two fields latch
together*, so it could never fire. "This relation always holds" was read as the
guard working when it meant the guard was inert.

**The error, named.** Every check behind §1.1 — volume, zero overlap, the
diagonal→cardinal pattern, the ruled-out grid-alignment rival — was about the
**fields**. None was about the **position**, which is what the observation
actually claims. Five field-level checks agreeing is not one position-level
check. This is §0 of the pathfinding handoff, repeated: *a plausible inference
about a state, checked only against that same state.*

**Status:** the v59 formulation is dead. v61 carries a repair
(`currentMoveDir == wallMoveDir` — true in 692 of 966 frames, excluding all 274
bad ones) with `cur=` and `moving=` now in every row. It is **untested**, it is
again reasoning about the same fields, and `[SLIDE]` stays quarantined from
`[BLOCKED]`. **The `[EDGES]` contradiction test is the one that caught this and
nothing in v59 was running it** — the dump now states both tests.

A lesson worth keeping separately: v59 stripped `cur`/`dx`/`dy` out of the key to
stop the set exploding. Those were exactly the fields that would have shown the
failure in the dump itself, and the diagnosis had to be rebuilt from the
position-independent state histogram instead.

### 7.2 The oracle is player-local — measured, question CLOSED

Two uncapped sweeps, 60 entities × 16 directions each, **1920 calls, no crash or
hitch**. On the 59 non-player entities: **1888 calls, zero non-zero results.**

What makes this a real negative rather than the old uninterpretable one — the
positions are now logged:

* **13 entities stood adjacent to a cell known solid from a cardinal refusal**,
  at both press positions. All returned zero in all 16 directions.
* **5 of those were displayed** (`disp=1`), across `otyp` 1, 2, 3 and 4.
* At the same instant the player returned `1` for **seven of sixteen** directions.

`EventMapAtariCheck(pno, dir)` answers only for `pno = 0`. The old claim was
right; it just had no evidence behind it. Now it does.

**This also undercuts the reserved write route (§5 of the handoff).** Its premise
was to place an unused `ehuman` slot and ask the oracle about *that* entity. The
oracle ignores non-player entities that are real, displayed and pressed against
geometry, so an unused slot would answer zero too. The route is now much weaker —
and learning that cost nothing, because no write was made. That is the opposite
of what §5 of this document said to watch for, and it is the cheaper outcome.

A second confirmation fell out of the press taken while walking into an NPC: the
oracle reported **nothing blocked**. Consistent with §1.3 — person collision is a
separate channel (`objStopDir` / `isHitHuman`), and this oracle is about walls.

### 7.3 NPC cells — promising, not yet usable

77 cells across 4 `otyp`, **63 of them cells the player has never stood on** —
real coverage, free. But:

| | cells | on a cell the player was refused entry to |
|---|---|---|
| `otyp=1` | 40 | 2 |
| `otyp=2` | 8 | 2 |
| `otyp=3` | 19 | 2 |
| `otyp=4` | 12 | 0 |
| **union** | **77** | **5** |

The player's own walked set has **0 violations in 672**. So NPC positions are not
simply "walkable by the player", and `otyp` alone does not separate the entities
that share the player's collision from the ones that do not. Keeping them split
rather than merged is what made this visible — merging first would have quietly
put 5 false walkable cells into the ground truth.

v61 adds `disp` to the bucket key as the next discriminator. **No bucket is
merged into a walkable set until it scores 0 against `[BLOCKED]` on its own.**

### 7.4 What this changes

* **§3's lead ordering:** lead 1 (slide oracle) is refuted pending the untested
  v61 repair. Lead 2 splits — the `pno` half is closed as a negative, the NPC
  half survives with a contamination caveat.
* **§2 stands, and the cheapest test of it has now been run and come back
  negative.** No arbitrary-point query is reachable through the entity oracle.
  What remains untested is lead 3 (`OBJ_AT`) and lead 4 (the `ref RECT` call).
* **§4 is unaffected and the frontier problem is unchanged.** Nothing in this
  session closed the frontier, so the learned occupancy grid is still premature
  on the same evidence — and one of the two things that might have closed it is
  now gone.
* **The A\* consumer recommendation stands**, and so does the edge-shaped
  predicate from §1.2, which this session independently reinforced: the only
  refusals with a perfect record are the cardinal ones.
