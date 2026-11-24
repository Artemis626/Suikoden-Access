# Suikoden Access — Milestone 5: Navigation & Pathfinding (Status & Handoff)

> **Current reference.** The project is shelved at v72 and unmaintained; start
> with [`HANDOVER.md`](../HANDOVER.md) in the repository root. File names below
> are as they were when this was written: the docs now live in `docs/`, and
> `PROJECT-STATUS.md` became `HANDOVER.md`.

**Companion to `Suikoden-Access-STATUS-2.md`** (Milestones 3–4: title, menus, battle)
and `Suikoden-Access-TITLE-STATUS.md` (Milestone 3 detail). Those cover the shared
UI layer. This covers Milestone 5: field navigation and pathfinding, which is
**not** shared between the two games.

**Status: Suikoden 1 has true A\* pathfinding over the game's own collision, and
reads exits and interactable objects out of the map's event table. Suikoden 2
has its own A\* to the people on a map since v66 — see §2, which now only points
to `Suikoden-Access-GSD2-HANDOFF-2026-09-23.md`.**

**Maintenance, 2026-10-06 (v71) — what changed for Suikoden 1, all untested in
play:** (1) A\*'s open list is a `PriorityQueue` keyed (F, insertion order)
instead of a linear scan — it picks exactly the node the scan picked, proven
route-identical on 2000 random grids, so spoken routes do not change. (2) The
launcher can start the other game in the same process; `Gsd1Nav.Live` makes the
more recently captured field controller the live game, so a stale `Village_c`
no longer answers J/K/L/P while Suikoden 2 runs. (3) The once-a-second
`[Nav1]` position line is written only when it changes. (4) `UiText.Member` /
`Element` cache their reflection lookups, which every Suikoden 1 read goes
through. Details: GSD2 handoff §18.

**Last substantive revision: 2026-09-05.** Place names are solved and §1.10 is
rewritten: the game hands the mod a village's display name itself, through
`WindowManager.PlayVillageNameAnimationHeaderUI`. The digit-bearing
`CHARACTER_ID` band is isolated and explicitly UNSETTLED — those names are spoken
as "possible X" until the player's own answers score the candidates. The
story-flag rule now reads `GetTmpEventFlagS` and collects the live half of its
own proof from `Event_c.eventWK`.

**Previous substantive revision: 2026-09-03.** **Both collision oracles are settled,
and the coordinate scale this document asserted for a month was wrong.** Both
`GNDAtariCheck` and `BLDAtariCheck` take **CELL** coordinates — the player's own
`map_x`/`map_y`, unshifted — and a non-zero answer means solid (for buildings,
a non-zero **low nibble**). §1.1 and §1.7 are rewritten. The method that settled
it is in §1.9 and matters more than the answer: every earlier reading came from
reasoning about the numbers, and three different ones each looked settled and
each let A\* walk through a wall. Sections 1.7 and 1.8 were rewritten on
2026-08-29 from a live runtime dump and overturn four earlier conclusions.
Anything in this document that is inference rather than a runtime measurement
says so.

---

## 0. The single most important discovery

**The two games are different engines below the UI layer.** They ship in one
executable and share `GSDShare.dll` / `ShareUI.dll` for menus and battle — which
is why Milestones 1–4 ported across for free — but their field/map code is two
separately-ported PSX codebases with nothing in common:

| | Suikoden 1 (`GSD1.dll`) | Suikoden 2 (`GSD2.dll`) |
|---|---|---|
| Field controller | `Village_c` | `EVENTCON` |
| Player entity | `Village_c.player` (`HUMAN`) | `ehuman[0]` (`EVENT_HUMAN`) |
| Coordinates | `map_x`/`map_y`, **byte tile coords** | `px`/`py` ints; entities use `EVENT_POS` |
| Coordinate scale | 1 = one tile | `px * 8` = `EVENT_POS`; tile = 16 units |
| NPC list | `map_human`, `map_event_human` | `ehuman` |
| Collision | **`GNDAtariCheck` — solved** | **unsolved** |
| Movement | **4-way only** | 8-way (diagonals legal) |

Do not assume anything found in one game exists in the other.

---

## 1. Suikoden 1 — SOLVED

### 1.1 The collision oracles

```
Village_c.GNDAtariCheck(int x, int y, bool)                       -> byte
Village_c.BLDAtariCheck(int x, int y, int level, ref int, bool)   -> ushort
```

**Both take CELL coordinates — the player's own `HUMAN.map_x`/`map_y`, used with
no shifting at all.** Nothing is doubled and nothing is halved. The navigation
grid *is* the player's coordinate space, one grid cell is one walking step, and
routes are spoken in those steps.

```csharp
blocked(x, y) = GNDAtariCheck(x, y, false) != 0
             || (BLDAtariCheck(x, y, L, ref no, false) & 0x0f) != 0
                for any L in the levels this map actually answers at
```

**Ground** is terrain: cliffs, water, fences, and a building's outside walls.
**Buildings** are `TATEMONO`: a room's interior walls, its bed, its bookcase, the
save desk. Outdoors the ground layer is nearly all of the geometry, which is why
routes looked right in a town and wrong in a bedroom for weeks.

**What the returns mean.** The ground byte is a 4-bit mask of **one cell's four
quarters**, and any bit set means the player cannot stand there. It is *not* a
chip's four cells, which is what this section claimed until 2026-09-03: 544 cells
of the sampled map carry a partial mask (3, 5, 10, 12 …) against 493 fully solid
ones, and the player stood on **none** of the 544. "Partially solid" is simply
solid at the resolution a route is spoken in.

The building `ushort` packs **two** 4-bit masks. The high nibble describes the
obstacle; the **low nibble** is the part of it landing on the queried cell, and
only the low nibble decides. It is never non-zero where the high nibble is zero.
Treating the whole return as a boolean blocked 812 cells instead of 166.

**`level` is NOT the map's `layer_no`** — corrected 2026-09-04. It looked like it
was, because the mansion maps' `layer_no` is 1 and the building layer answers at
levels 0 and 1 there. **town A's `layer_no` is 2, level 2 holds nothing, and
so every house in the town read as open floor.** The route to a soldier standing
beyond a house came back "north 12" — straight through the building — and the log
named the cause in one line:

```
blocked by ground 0 / buildings 0, buildings on, level 2
```

Zero blocked by *both* layers across a fifty-cell search. A town's houses are
`TATEMONO`, so the ground layer never had them, and the building layer was being
asked a level that holds nothing.

The live levels are now **sampled per map** — a coarse grid at each of levels 0..3,
cached per map key — and a cell is solid if **any** live level says so. That is the
safe direction, and it is provably identical on the map where the rule was
validated: levels 0 and 1 give the same 166 low-nibble cells there, zero
walked-cell violations. Per-level counts are logged on every map.

The `bool` on `GNDAtariCheck` made no observable difference; `false` is passed.

> **A fourth wrong reading, and the same root cause as the first three.** The map
> where the rule was validated had `layer_no == 1` and it was never checked
> anywhere else. One map is enough to *find* a rule and never enough to *bound*
> it. The walked-cell test proves a rule does not over-block; nothing proves it
> does not under-block except trying another map.

> **Three wrong readings, one cause.** Before 2026-08-29 the code doubled its
> input and then halved it again — a net no-op that landed on the right cell, but
> the quadrant bit then always came out as bit 0, so only the NW quarter was ever
> tested and **249 solid cells read as open**. From 2026-08-29 it queried
> `GNDAtariCheck(x>>1, y>>1)`, a different part of the map. On 2026-09-02 the
> building layer was moved to chip space on a structural argument that turned out
> to be wrong. None of the three was ever checked against a place the player had
> actually been. §1.9 is how that was fixed for good.

### 1.2 What is implemented

- **True A\*** over the player's own cell grid, **4-directional only** (Suikoden 1 has
  no diagonal movement — an 8-way search emitted "northeast 6" routes the player
  physically cannot walk). Manhattan heuristic. 60k-node guard so it cannot hang
  the game thread. Search is bounded to a box around start+goal, and now also to
  the current map: `Village_c.mapVillage` **is** the loaded `MAP`, so its
  `width`/`height` are known exactly — the old note that "the map's true
  dimensions are not known" came from walking `vil.map_table` and guessing.
- **Route speech** compressed into runs and reported in walking steps:
  `"east 4, north 3"`. One grid cell is one step.
- **Target categories**: All / People / Events / Doors / Places / Objects / Scenery.
- **Persistence**: three shareable plain-text files (§1.5), keyed by the game's own `a{area}.m{map}` id.

### 1.3 Keys (modelled on Pokémon Access)

| Key | Action |
|---|---|
| `J` / `L` | previous / next target |
| `K` | read current target (name, bearing, distance, index) |
| `Shift+J` / `Shift+L` | cycle target category |
| `P` | pathfind — speaks **steps only** (K already names the target) |
| `M` | mark current tile as a place, then name it by typing |
| `Shift+M` | say where the player is — the player's name for this map, then the village |
| `N` | name this map by typing (the area itself, not a tile) |
| `Shift+K` | name the selected NPC by typing |
| `F11` | diagnostic dump (maps, collision, NPC list) |
| `F12` | collision window around the player: ground blocked-per-cell and raw masks, buildings in three candidate spaces x four levels, with the blocking entry named |
| `F6` | the whole map's collision written to `suikoden1_collision_dump.txt`, with every TATEMONO and every cell the player has walked on |
| `F5` | name probe: `GetCharacterName` / `GetFieldName` / `GetVillageNameID` against every person on the map, the place-name candidate line, and the **NPC identity questionnaire** (§1.10) — **logs only, speaks and stores nothing** |
| `F8` | diagnostic dump of the map's event table, doors, objects and people, plus `Event_c` (`eventWK`, `backLogMessageId`, `speaker_name_id`) |
| `F7` | diagnostic dump of every on-screen label with its hierarchy path |

### 1.4 Where targets come from

*(rewritten 2026-08-29 — see §1.7 for the evidence behind every row)*

| Category | Source | Notes |
|---|---|---|
| People | `Village_c.map_human` | ambient and story NPCs; identity from the matching event's `image` |
| Events | `vil_ekp` region triggers with no decodable warp | scripted-scene tiles |
| Doors | `vil_ekp` events whose command carries `VILLAGEOUT` + learned exits (§1.6) | destination map and tile are **known in advance** |
| Places | player marks (`M`) | now a convenience, no longer the only route to an object |
| Objects | `vil_ekp` tap events with `putno == 255` | interactable scenery — this is where the save diary is |
| Scenery | `Village_c.map_tatemono` | every wall panel; **excluded from All** |

Targets are sorted **stably** by category then entity number. (Sorting by
distance re-ordered the list on every step, so cycling read as 5, 12, 6, 1.)
Targets further than 150 tiles are filtered out: characters who join the party
are parked off-map rather than removed, and one appeared as a duplicate "character A"
382 tiles away reporting "no path".

### 1.5 Persistence (all in `BepInEx/plugins/`, written immediately, shareable)

| File | Contents | Key format |
|---|---|---|
| `suikoden1_npc_names.txt` | NPC names | `map:list:eventNo = name\|signature` |
| `suikoden1_exits.txt` | learned transitions | `map \| x \| y \| leadsTo \| label` |
| `suikoden1_marks.txt` | player-marked places | `map \| x \| y \| label` |
| `suikoden1_places.txt` | names for whole maps | `map \| player or game \| label` |

The `label` column is what gets **spoken**; internal map keys never are.
These files outlive save games. Sharing them makes another player's discovered
routes usable immediately.

**The map key** is a fingerprint, because Suikoden 1 exposes no per-map id
(`VIL.id` reads empty; `EVENTCON.mno` has no GSD1 equivalent):

```
{vilId or "m"}.{map_kazu}.{objectCount}.{checksum of first 12 object positions}
```

NPC counts are deliberately **excluded**: `map_human` went 14 → 13 in the same
room when a character joined the party, which would have changed the key and
orphaned everything learned there. Scenery does not move or join parties.
`MapKey` returns **null** while an area is mid-load (lists briefly read `-1`,
which previously produced keys like `m.31.-1.0.-1` and recorded exits leading to
a map that does not exist); callers must skip on null.

### 1.6 Exits are learned — SUPERSEDED, kept as the fallback

> **The position-jump heuristic is REMOVED, 2026-09-04.** Learning now fires on a
> change of map id and nothing else. The jump test — "more than 8 tiles inside
> one map is a teleport, so it is a doorway" — was justified by the map key being
> a village-level fingerprint that room-to-room moves would not change. **That
> justification died on 2026-08-29**, when the key became `a{area}.m{map}` from
> the game's own getters, which changes for every room.
>
> What the test actually detected was **cutscenes**. The game's opening is one
> long scripted sequence that repositions the player repeatedly; in the throne
> room it invented three exits in a row — (23,45), (70,45), (70,70) — while the
> player stood still through a conversation with the Emperor. Every one recorded
> `leadsTo` as the map it was already on, which is both the tell and unspeakable:
> "this exit leads to where you are". They then appeared as duplicates beside the
> real event-table exits.
>
> Entries like that are also **purged on load**, along with (0,0) and 254/255
> sentinels, because deleting the file by hand does not work: the mod rewrites it
> from memory whenever it learns something, so a mid-session delete comes back.

> **This section's premise is wrong.** Suikoden 1 *does* expose warp data:
> `vil_ekp` events carry `VILLAGEOUT destMap, destX, destY`, verified against a
> real transition. Exits are enumerable before they are crossed. Learned exits
> stay as a fallback for anything the script walker cannot decode. §1.7 has the
> replacement. The rest of this section is left for the learning mechanism,
> which is still in use — and which was **completely dead** until 2026-08-29:
> `Observe` cleared its "where I came from" state whenever the map key read null,
> and every transition passes through several null frames while loading, so the
> map-change branch could never fire. A whole session of walking up and down
> stairs produced four map-key changes and zero learned exits.

Suikoden 1 exposes **no warp data at all**. `map_door` reads 0; there is no
WARP / EXIT / KAIDAN(stairs) / DEGUCHI(exit) structure anywhere in the
decompile (the one `STAIRS` class belongs to a minigame); transitions are driven
by **compiled event bytecode** that tests the player's position. There is
nothing static to enumerate.

So exits are learned from what happens: on a transition, the tile the player
left **was** an exit by definition, and the arrival tile is the way back. Both
are recorded. Detection triggers on a map-key change **or** a position jump of
more than 8 tiles (the key is village-level, so room-to-room doorways inside one
building may not change it).

Consequence: **a doorway is not targetable until it has been crossed once.**
Sharing `suikoden1_exits.txt` is the workaround.

### 1.7 What the data actually says (rewritten 2026-08-29 from a live dump)

**Everything below was READ AT RUNTIME.** The previous version of this section was
written from the decompile plus inference, and got four things wrong — each of
which had blocked a feature for weeks. Where a claim is still an inference, it
says so.

#### Resolved — collision is TWO layers, and only one was being read

`GNDAtariCheck` is the **ground** layer. A room's walls, its bed, its bookcase
and the save desk are all `TATEMONO`, which the ground layer knows nothing
about — so every interior read as one open rectangle and A\* returned a bare
Manhattan line. From (19,33) to the save point at (14,35) it said "south 2,
west 5": seven moves for a Manhattan distance of seven, straight through the
furniture.

Outdoors this was mostly invisible, because cliffs, water and fences *are*
ground. That is why routes looked right in town and wrong in a bedroom, and why
the earlier scale fix — which was real and necessary — did not settle it.

The game never uses one layer alone: `player_atari_check` collects `gnd_atari`,
`bld_atari`, `evt_atari` and `tak_atari` together.
`BLDAtariCheck(int x, int y, int level, ref int tatemono_no, bool isLog)` is the
building half as a point query, in **cell** coordinates, and only its **low
nibble** decides. See §1.1 for the format and §1.9 for how it was settled.

> Between 2026-09-02 and 2026-09-03 this said "chip coordinates, high nibble".
> That came from a structural argument — a 4-bit mask looked like it had to
> describe a chip's four quadrants — and the argument was wrong: it describes a
> cell's four quarters. The reading it produced calls 109 walked cells solid.

**A self-check makes it safe to switch on without a play session first.**
Whatever else is true, the player is standing somewhere walkable. If the
combined collision says the player's own cell is blocked, then `BLDAtariCheck`
is being asked the wrong question — wrong level, wrong space, wrong sense of its
return — and the router drops back to ground-only for that request and logs it,
rather than reporting "no path" from a corrupt grid. Every route logs how many
cells each layer blocked, so over- and under-blocking are both visible. This
check is what first showed the chip reading was wrong: it fired on the player's
own cell.

**Still open, and nothing depends on it:** the `ref tatemono_no` read-back names
an entry whose stated `map_x`/`map_y` does not sit near the queried coordinate
under any reading tried. It may index `MAP.tatemono_data` (42 entries) rather
than `Village_c.map_tatemono` (40). `F6` dumps both. The mask alone routes.

#### RETRACTED — "the navigation grid was running at twice the correct scale"

**This section was wrong, and it is the third wrong answer to the same question.**
It argued that `GNDAtariCheck` and `MAP.width`/`height` count chips while
`map_x`/`map_y` count the cells inside them, and had the pathfinder query
`GNDAtariCheck(map_x >> 1, map_y >> 1)`. Measured against 233 cells the player
had stood on, that rule calls **81 of them solid**. `GNDAtariCheck(map_x, map_y)`
calls **none** of them solid. The oracle takes cell coordinates. §1.1 is the
replacement.

The five "independent facts" it rested on are all still true and none of them
settles the scale:

| Fact | What it actually shows |
|---|---|
| `MAP.index` is 576 = 24×24 | the tile-graphics index is 24×24. It does not follow that the collision query shares that unit. |
| the player was observed at (14,36) and (29,40) | the player's own space runs past 24 — which is consistent with the oracle taking that same space. |
| `map_tatemono` reaches (44,38) | same space again, so buildings and the player agree. Nothing about the query. |
| `map_dx`/`map_dy` range −8..+7 | one `map_x` step spans 16 px. True, and silent on what the query takes. |
| a warp destination (17,36) landed the player there | events and the player share one space. Also silent. |

What *is* true and worth keeping: **out-of-range must read as SOLID.** The oracle
answers an off-map coordinate with `0`, which reads as fully open, so a wrong
bound makes the whole grid walkable. The cell range is `0..2*width-1`: on the
sampled 24×24-chip map the oracle returns real geometry out to cell (47,47) and
the player was recorded walking to (44,44).

#### Resolved — exits are static data, with destinations

`Village_c.vil_ekp` is a `List<EVENT_WK>`, the map's own event table:

```
ushort x, y        contact tile (sentinels 0,0 / 254,254 / 255,255 = not placed)
ushort putno       person slot, 255 = nobody
ushort image       sprite id, == MAP.hito_data[..].fp_no
ushort flag1
byte[] eventPut    placement parameters
byte[] eventForm   WHEN it fires   (EVENT_CHECK opcodes)
byte[] eventCom    WHAT it does    (EVENT_COMMAND opcodes)
```

All 32 entries on the sampled map parse to exactly their declared byte length
with these widths, which is what makes them trustworthy rather than plausible:

```
FORM   EVENTTAP(5) 0   PUSHKEY(4) 0   CHKEND(6) 0/terminator
       PX_NEW(11) 2    PY_NEW(12) 2   EVENTFCHK(7) 2   EVENTFCHK_NO(8) 2
COM    VILLAGEOUT(3) 3 = destination map, destination x, destination y
```

Event 0 read `PX_NEW 18..20, PY_NEW 35..36` then `EVENTSND 117, VILLAGEOUT 1,17,36`,
and the play log shows the player walking off that rectangle and arriving on
`map_table[1]` at tile (17,36) exactly. Event 1 did the same for (26–28, 35–36)
to (29,36).

**§1.6 is superseded.** A doorway is targetable the first time a player enters a
room. Learned exits remain as a fallback and are no longer the ceiling.

#### Resolved — interactable objects, including the save diary

An event with `EVENTTAP` in its form, a real position, and `putno == 255` is
interactable scenery with nobody standing on it. On the sampled map that is
exactly one entry: **`ekp[23]` at (14,35)** — one tile from where the player stood
to save, and precisely the tile they had marked by hand as "save diary".

Classification, all from the parsed scripts:

| Kind | Test | Target tile | Category |
|---|---|---|---|
| Exit | region + a clean `VILLAGEOUT` | region centre | Doors |
| Doorway | region + `PUSHKEY`, no decodable warp | region centre | Doors |
| Trigger | region, no `PUSHKEY`, no warp | region centre | Events |
| Object | `EVENTTAP`, placed, `putno == 255` | `x`, `y` | Objects |
| Person | `EVENTTAP`, placed, `putno < 255` | **not a target** | — |

**A person event is never a target.** It carries the script and the sprite id;
the *position* is on the matching `HUMAN` in `map_human`. Listing both put every
townsperson in the list twice — 29 live entries and 23 frozen spawn points on one
town map — so cycling alternated between two copies of the same person and a
target three steps east became one twenty steps away.

**Events sharing a trigger rectangle are one target.** Building entrances
routinely carry two or three alternative scripts gated on different story flags.
They are one place. The entry that decoded a destination or a service wins.

**Doorways are inferred, not decoded.** Twelve of them on the town map: three
cells wide, one deep, in pairs, each at a building front, one of them two cells
from where the player was standing. Their scripts stop at an opcode of unknown
width, so this is shape-and-position evidence, not a parse. Collapsing 71 raw
events to 24 targets makes the town map usable; if they turn out to be signs
rather than doors it is one line to move them back to Events.

#### Resolved — `map_door` was never read

The reader looked for `v_work` on `effect_work`. **`v_work` is a direct field of
`Village_c`**; `effect_work` is an `EVENT_EFFECT` and has no such member, so the
read returned null every time — and the log line was guarded by `if (dn > 0)`, so
nothing was ever printed. "Empty on every map" was a null path, not a
measurement. Read correctly it returns 2 on the sampled map, and
`MAP.map_door_kazu` is non-zero on most maps in the table.

`DOOR` shares the player's coordinate space: the two entries matched
`map_tatemono[37]` and `[38]` exactly. But `DOOR` is the animated door *sprite*,
not the transition — neither matched an exit region — so doors are logged and not
yet targeted.

#### Resolved — `map_event_human` is empty, and it does not matter

It read `0`, not null: the field is wired correctly and the list really is empty.
Story NPCs are not in it. They are `vil_ekp` entries carrying a `putno`, and their
**position** lives on the matching `HUMAN` in `map_human`, linked by
`map_event_no == putno` (verified: thirteen entries reporting ev = 0..12 against
putno 0..12). It has been dropped as a target source rather than kept as a
permanently empty category.

#### Confirmed — `TATEMONO` carries no interactable flag

This one survives. The id histogram over 40 entries was **40 distinct ids, each
appearing once**: `id` is the entry's own array index, not a type code. There is
no way to tell a desk from a wall panel here. It is now the "Scenery" category,
excluded from "All", and it is no longer where an interactable object comes from.
**Stop looking at TATEMONO for interactables.**

#### Fixed — NPC identity

`map_event_no` is a slot index and is reused, and the sprite-dimension
fingerprint meant to detect that read `0.0.0.0.0.0` for every NPC, so it could
never fire. **`EVENT_WK.image` is a real character identity** (it equals
`hito_data[..].fp_no`) and does vary between characters. It is now the signature.

#### Fixed — the map key is a real id

`Village_c.get_map_idx()` and `get_area_idx()` are no-argument getters and both
work (area 0, map 3, matching `map_table[3]`'s dimensions and object count). The
key is now `a{area}.m{map}`, cached for a fraction of a second so the getters are
not called per frame. The composite fingerprint survives as a fallback.
> **Correction, 2026-09-03.** `get_village_name()` **is** declared on `Village_c`.
> It is **static**, and the lookup that missed it searched instance members only.
> It is one of several name sources found this session — see §1.10.

Files written before 2026-08-29 use the old fingerprint keys and will never
match. They are inert, not harmful; delete and re-walk.

#### Still open

1. **`eventCom` operand widths cannot be recovered from script lengths — settled,
   stop trying.** A brute-force search over all 22 complete scripts found every
   width from 0 to 3 feasible for every unknown opcode. The reason is structural,
   not a weak search: a script's length constrains the **sum** of its operand
   counts, and with roughly eight distinct opcodes per script and one equation
   per script the system is underdetermined by construction. Adding more maps
   adds more unknowns as fast as it adds equations. Only a different kind of
   evidence settles this — a ground-truth pairing like the one that proved
   `VILLAGEOUT` (its stated destination matched where the player actually
   arrived). Confirmed so far: `VILLAGEOUT` 3, `EVENTSND` 1, `COMMANDEND` 0.
   The walker stops at the first unknown opcode and reports a destination only
   from a clean walk, so an undecoded script costs a label, never a wrong route.
2. **Names for exits and objects.** No per-map display name exists, so an exit
   reads as "exit 1 to map 3" until a player renames it with `Shift+K`. Names key
   on the event's index in `vil_ekp`, which is static map data and therefore
   stable across sessions and shareable.

   **Save points name themselves.** When the save screen opens, `Event_c.eventWK`
   says which event opened it; if that matches a tap object on the current map
   within four cells of the player, it is named "save point" and written to the
   names file. This is inference from an outcome, not a guess: exactly one event
   is running and the game itself has opened the save UI. It is the opposite case
   from dialogue-speaker attribution (§3), which had to choose between several
   candidates and chose wrong.

   **Service points name themselves — CONFIRMED.** `G1_event_h` reads
   `USER_MENU=1 SHOP_OMISE=2 SHOP_KAJIYA=3 SHOP_MONSHO=4 SHOP_KANTEI=5
   SHOP_YADOYA=6 SHOP_SAVE=7 SHOP_SUTERU=8 SHOP_MINIMAP=9 SHOP_HANYOU=255
   SHOP_EFUNC=128`, and the save diary's script ends `43 07 ff` — `EVENTSHOP`
   followed by exactly `SHOP_SAVE`, and the only `0x43` among that map's 32
   events. So a save point, inn, blacksmith, rune shop, appraiser or item shop
   names itself from static map data, on maps nobody has visited, without being
   used once.

   Only the **tail** of a script is scanned (the last few bytes before the
   `COMMANDEND` padding), because that is the position the evidence covers: a
   service dispatch is the terminal action of an interaction script. `255` and
   `128` are excluded from the name table — they are generic dispatch slots, and
   `255` is also the commonest filler byte in these scripts, so accepting it
   would put a shop name on anything.
3. **`HUMAN.dir` encoding** (values 0–3) is still unconfirmed. Nothing depends on
   it. Do not guess it.

3a. **The selection follows the target's identity, not its list position.**
   `_targets` is rebuilt from live state on every keypress and its membership
   genuinely changes — NPCs wander in and out of range, categories filter, event
   lists reload. Holding the selection as a list index let the highlight slide
   silently onto a different target. It now re-finds the selected `Id` after each
   rebuild. **Any new target source must set a stable `Id`.**
4. **Enter may double-register** during text entry. Still untested.
5. **The title-screen confirm highlight.** `UISystemDialog` fires once per BUTTON
   whenever the dialog restyles, not once per selection, so it cannot say which
   option is highlighted. The save screen has `isConfirmSelect` and owns the
   dialog while it is up; the title screen has no equivalent signal and still
   reads both options.

### 1.8 The old "unexplored leads", resolved

Every lead from the previous version was followed at runtime. Recorded here so
none of them is opened a second time.

| Lead | Outcome |
|---|---|
| 1. `vil_ekp` — `List<EVENT_WK>` | **The answer to almost everything.** Exits, triggers and interactable objects all come from here. See §1.7. |
| 2. `vil_elp` — `EVENT_VL` | Not "a positioned event with a footprint" — that description was `TATEMONO`. It is the per-map event *container*: `eventDat`, `mapEventMax`, and `mapPutMapnum`/`mapPutMapsub`, which name the **two `map_table` entries this event table covers**, not a warp target. `VIL.eventData[k]` holds one per map pair; `vil_ekp` is the current map's `eventDat`. |
| 3. `MAP_PERSON.function_index` | Real but thin: a flat `int[]` (11 entries, values 0/1/2) and an `OMISE_DATA[]`. No positions, so it cannot be a target source on its own. Possibly a person-to-shop-function map. Low value. |
| 4. `HITO_DATA` | **Dead.** It has exactly one field, `short fp_no`. The `x/y/putno/image/flag1/kakuritsu/hosuu` credited to it belong to `EVENT_WK` and `BATTLE_DATA` — adjacent classes in the decompile that got merged when reading. Its one use: `fp_no` per slot matches `EVENT_WK.image`. |
| 5. `map_event_human` and `map_door` reading 0 | Two different causes, and the suspicion that "two lists reading zero smells like wrong wiring" was half right. `map_door` **was** miswired (null path through `effect_work`) and reads real data. `map_event_human` genuinely is 0 — but story NPCs live in `vil_ekp` instead, so the list being empty costs nothing. |
| 6. `TATEMONO.id` / `.level` | **Dead.** `id` is the array index (40 distinct ids over 40 entries). `level` was 1 for every entry. No type information. |
| 7. `HUMAN.dir` encoding | Not investigated; nothing depends on it. Still open, still not worth guessing. |

**Method note, paid for here.** Four of these had been written down as settled
without ever being read in play, and the log made that invisible: every reader
logged its count only `if (n > 0)`, so a list reading `0` and a list reading
`null` produced identical output — nothing — and both were recorded as "empty on
every map". **Log the zero.** A measurement that says "I looked and found none"
and a silence that says "I never looked" must never look the same in a log.

### 1.9 How the collision question was finally settled — use this again

**Three answers to "what does the collision oracle take" were written down as
settled, and each one let A\* walk through a wall.** They were not careless: each
was argued from the data, and each argument was sound-looking and unfalsifiable.

* A 4-bit return "must" describe a chip's four quadrants, because a 2×2 mask
  needs something made of four parts. It describes a *cell's* four quarters.
* A coordinate of 36 on a 24-wide map "must" be half-scale. It is not; the
  map's width simply counts a different unit from the query.
* A wall drawn in a probe window "looks like" it is in the right place. Both
  readings drew plausible rooms, because both were sampling real geometry — just
  different parts of it.

Every one of those is consistent with the data and none is checkable against it.
Arithmetic about a coordinate system cannot referee a question about that same
coordinate system.

**What settled it is a fact arithmetic cannot argue with: a cell the player has
STOOD ON is walkable.** `Gsd1Nav.NoteVisited` records every distinct one, every
frame, keyed by map. `F6` writes them out beside the whole map's collision grid.
Each candidate reading is then scored offline by how many of them it calls solid.
On 233 cells of town A map 3:

| Reading | Cells blocked | Walked cells called solid |
|---|---|---|
| **`GNDAtariCheck(cell) != 0`** | 1037 | **0** |
| `GNDAtariCheck(chip) & quadrant bit` | 750 | 81 |
| **`BLDAtariCheck(cell) & 0x0f != 0`** | 166 | **0** |
| `BLDAtariCheck(cell) != 0` | 812 | 79 |
| `BLDAtariCheck(chip)` high nibble & bit | 903 | 109 |
| `BLDAtariCheck(chip) != 0` | 1276 | 130 |

**Zero violations is necessary, not sufficient** — a rule that blocks nothing
also scores zero. So the surviving pair is checked two more ways: it must block a
substantial amount (1089 cells of 2304), and drawing it must produce something a
person recognises. It draws a room with a desk against its north wall and floor
in front of it, and the save diary is at the desk.

**The general rule.** When a probe's meaning is in question, find an observation
the game makes *for* you that no interpretation can reinterpret — where the
player has been, where the player arrived after a warp, which button the game
itself pressed — and score every candidate against it. Reserve reasoning about
the numbers for generating candidates, never for choosing between them. This is
the same shape as the `VILLAGEOUT` proof (§1.7: a stated destination matched
where the player actually landed) and the same shape as "log the zero", and it is
now the standard for anything of this kind.

---

### 1.10 Names — the game has them, and they are static

Everything the mod calls by a real name today, it got from static data that names
itself: `VILLAGEOUT` for an exit's destination, `EVENTSHOP` + `SHOP_SAVE` for the
save point. People are still "person 3" and places "map 2" because nothing had
been found that names *them*. These exist, all **static**, all value parameters,
so calling them is the same acceptable category as `GNDAtariCheck`:

| Call | Declared on | Returns |
|---|---|---|
| `GetCharacterName(int hno, bool short)` | `Fhuman_c` (a static class) | the character's display name |
| `GetCharacterNameTextID(int hno, bool short)` | `Fhuman_c` | its text id |
| `GetFieldName(int area_no)` | `FieldBGManager` | the area's name |
| `GetVillageNameID(int area, int village)` | `VillageManager` | a text id |
| `get_village_name()` | `Village_c` | the current village's name |
| `enum CHARACTER_ID { FP_Q, FP_I, FP_E, … }` | GSD1 | a symbol per character |

`G1_string_id_c.g1_Sid2Msg(int)` resolves a text id to a string but is an
**instance** method, so it needs a live `G1_string_id_c`; `GetCharacterName`
returns the string directly and avoids needing it.

**CONFIRMED 2026-09-04: `hno` is `EVENT_WK.image`.** Checked the §1.9 way —
against somebody standing in the room who knew who was there:

| | | |
|---|---|---|
| `ekp[16] putno=11 image=16` | `FP_B` | **character B** — had been "person 11" |
| `ekp[22] putno=12 image=110` | `FP_A` | **character A** |
| `ekp[11] putno=7 image=34` | `FP_F` | Character F |
| `ekp[12] putno=8 image=112` | `FP_G` | Character G |

Two agreeing signals for each: the localized name and the `CHARACTER_ID` symbol.

**One test disqualifies an id, and it was measured, not reasoned about.** A full
sweep of `hno` 0..255 partitions perfectly: **128** ids carry a real name and a
text id ≥ 0, **127** answer the engine's placeholder `名前未定義: <hno−1>` with
text id **−1**, and one is empty. No real name has −1 and no placeholder has ≥ 0.
So the rule is `GetCharacterNameTextID(image, false) >= 0` plus a check for the
placeholder marker — the marker is a literal in the executable, so it survives
any language setting.

> **RETRACTED 2026-09-04, one day after it was written.** This section also said
> to reject any id whose `CHARACTER_ID` symbol contains a digit — `FP_00P`,
> `FP_04P` — as a "generic sprite slot, not a person". **The full sweep shows
> that is wrong**, and it would have thrown away some of the best names the game
> offers:
>
> | | | |
> |---|---|---|
> | 119 `FP_00P` official role | 121 `FP_02P` character C | 124 `FP_05P` soldier role 1 |
> | 120 `FP_01P` character H | 122 `FP_03P` character D | 125 `FP_06P` soldier role 2 |
> | 123 `FP_04P` object-like name | | 126 `FP_07P` villager role |
>
> The rule was generalised from a single observation — the palace guards reading
> "object-like name" — plus an unchecked assumption that that name must be wrong
> for them. Its text id sits in the same block as official role, character C and
> soldier role 1, so it is an NPC name like the others, and those entities' symmetric
> pair-wise placement matches the armour displays flanking the hall. **The first
> sweep printed only 96 of 256 ids; capping a measurement is how an assumption
> gets to survive.**

Role names repeat, so targets that end up sharing a name are numbered in list
order — "soldier role 1 1", "soldier role 1 2" — the same way battle numbers
duplicate enemies. A name the player typed is never renumbered.

**These names are never written to `suikoden1_npc_names.txt`.** They come from
static game data and are recomputed every session, so a mistake is a bug to fix
rather than a wrong name persisted into a file people share. §3's rule is
satisfied without giving up the feature. A name typed with `Shift+K` still wins
and is still saved.

**Place names ARE usable — corrected 2026-09-05.** This section used to end
"place names are NOT usable", and the three calls it named are all still the
wrong kind of string: `GetFieldName` returns the asset folder (`'a_data'`),
`get_village_name()` returned empty in the palace, and `GetVillageNameID` returns
a text id. What was missing was any *observation* rather than another lookup.

The observation is that **the game names the place itself**. On entering a
village it plays a name plate across the top of the screen, built by

```
WindowManager.PlayVillageNameAnimationHeaderUI(string villageName)
```

whose single argument is the finished display name a sighted player reads. That
is the same class of evidence as a walked cell or `VILLAGEOUT`'s stated
destination, and it had been in the log for weeks as a `TMP_Text` called `sprt_1`
typing out `G`, `Gr`, `Gre` … `town A` one character at a time. The hook is
one deliberately named method with a single string value parameter, resolved
through `Gsd.FindIn("GSD1.dll", …)` because **`WindowManager` is declared in both
games**, and applied by hand — so `Gsd1VillageBanner` carries no
`[HarmonyPatch]` attribute.

Measured, not assumed: it fired on every map change inside area 0 and always said
`town A`, so on that evidence it names the **village**. It is stored against
the map key it settles on and inherited by the rest of that area. The **room**
has no name in the game's data at all and is the player's to give with `N`.

Three further candidates found while looking for the banner's caller, none of
them spoken and all of them logged beside the banner at every arrival so they can
be scored against it:

| Call | Declared on | Returns |
|---|---|---|
| `G1_SID_MSG(int index)` | `G1_string_id_h` | string — **a static text-id resolver** |
| `GetVilLabel(int area, int vil)` | `VillageManager` | string |
| `GetAreaLabel(int area)` | `VillageManager` | string |
| `vil_name_table` | `Event_c` | `int[]` of text ids |

`G1_SID_MSG` is the piece this document said did not exist: a text id can be
resolved by a **static** call after all, on `G1_string_id_h` rather than through
`G1_string_id_c.g1_Sid2Msg`, which is the instance method that made
`GetVillageNameID` look like a dead end. If it resolves 66342 to "town A",
every village is named from static data without being visited.

`Village_c.prev_village_name` is logged in the same line and deliberately not
spoken: its name says "previous" and nothing has established whether it means the
village being entered or the one just left.

**The digit-bearing `CHARACTER_ID` band is UNSETTLED — see the handoff §3.**
`FP_00P`..`FP_21P` are both generic person-sprite slots and named characters
(`FP_02P` is "person sprite 2" *and* character C), so an id in that band cannot be
resolved from the id alone and in play it names town A townspeople
"character C" and "character D". Until it is scored against player-supplied identities,
those names are spoken as **"possible character C"** — a hold, uniform across the
whole band so that no rule is smuggled in through it, and one line to delete. The
`F5` probe prints the questionnaire that will settle it: one row per person,
headed by the exact label the target list speaks, with every candidate
discriminator beside it and nothing capped.

**Interactable objects** have a second route, already observable and not yet
used. `Event_c` carries `eventWK` (which event is running — this is what names
the save point), `backLogMessageId` and `speaker_name_id`. Tapping an object runs
exactly one event and shows exactly one message, so the pair *(event, message)*
is unambiguous in the same way the save point's was. `F8` already dumps all three;
pressing it straight after tapping an object shows whether the pairing holds.

---

## 2. Suikoden 2 — see the GSD2 handoff

This section used to hold Suikoden 2's collision notes and a "next thing to try"
(F10, `ProbeChipAttributes`). All of that is superseded and the probe is gone.
Suikoden 2's collision was solved in September 2026 — `EventMapAtariCheckFunc1`
as a point query plus each building's pattern mask — and it has its own A\*,
target list and Shift+K naming. The current state and its history live in
`Suikoden-Access-GSD2-HANDOFF-2026-09-23.md` (§13 onward).

## 3. Rules paid for in bugs (do not relearn these)

**Never Harmony-patch a hot-path method taking a by-ref struct.**
A postfix using `object[] __args` on
`EVENTCON.EventMapAtariCheckFunc1(int, int, ref RECT)` **corrupted live collision
in-game**: walls stopped blocking and the player slid along map edges. Harmony's
`__args` round-trips by-ref struct parameters through boxing even when the
postfix never writes to them. This is the concrete instance of the existing rule
"only patch methods whose arguments are all simple values".

**Never patch a base virtual AND its overrides.** Hooking `Confirm(string,string,
string)` on `UISaveLoad1`, `UISaveLoad2` *and* `UISaveLoadBase` — three
trampolines in one vtable chain — crashed the game natively at startup with an
`AccessViolationException` inside the generated trampoline, marshalling one of
the three string pointers, the moment an unrelated patch triggered a class
initialisation. A `DeclaredOnly` filter stops the *same* method being patched
twice and does nothing about this. Patching `UISaveLoad2` also forced GSD2 class
init in a GSD1 session. **A loop over three class names is a sweep**, which the
existing "deliberately named set" rule already forbids. The replacement takes no
hook at all: `isConfirmSelect`, `saveResultMessage` and `saveResultButtonText`
are plain fields, read from an `Exec()` postfix that was already installed.

**A class patched by hand must NOT carry a `[HarmonyPatch]` attribute.**
`Gsd1MovementBlock` is applied manually against a method resolved at runtime, so
there is no compile-time type for an attribute to name. A bare `[HarmonyPatch]`
made `PatchAll` pick it up, find no target, throw "Undefined target method", and
abort `Plugin.Load()` — **disabling the entire mod**, dialogue and menus and
battle and navigation all at once. This is the mirror of the existing rule that
losing the attribute silently disables an attribute-driven hook: the two hazards
point opposite ways, and attribute patching and manual patching are exclusive.
`Plugin.Load()` now applies patch classes one at a time so one bad class costs
one feature and is named in the log.

**Calling a value-parameter-only game method is a different, acceptable risk.**
Verified safe in play: `GNDAtariCheck(int,int,bool)`. Keep such calls one-shot
(a keypress), never per-frame.

**Do not auto-attribute dialogue speakers to nearby NPCs.**
Implemented and removed. Party followers walk beside the player and speak, so
when character A (following the player) spoke during a conversation with character B,
Character B was permanently labelled "character A". Cutscenes with multiple speakers have the
same problem and GSD1 exposes no "who is talking" signal (`_talker_name` belongs
to a debug window). These names persist to a shareable file, so **a wrong name is
worse than no name.** The player confirms every name by typing it.

**`map_event_no` is a slot index, not an identity.** Slots are reused when
characters join the party. A sprite signature was added to detect this — but
note **every signature field read `0.0.0.0.0.0` in play**, so that mechanism is
currently ineffective and unproven.

**`CountOf` must check `Length` as well as `Count`.** Il2Cpp `List<T>` exposes
`Count`; `Il2CppReferenceArray<T>`/`Il2CppStructArray<T>` expose `Length`.
Checking only `Count` made every array silently read as `-1`.

**Il2Cpp lists are not `System.Collections.IEnumerable`.** `foreach` over one via
that cast silently finds nothing even though `.Count` works. Index them
(`UiText.Element`).

**This game runs the new Input System exclusively.** Legacy `UnityEngine.Input`
is compiled in but throws at runtime ("External component has thrown an
exception"). Use `Keyboard.current` / `Unity.InputSystem`.

**A constant probe result means "wrong parameters", not "no data".** Both the
`typ` sweep and the `atari_data` sample looked "empty" when they were simply
being queried wrongly. When probing an unknown API, sweep parameters and report
only combinations that **vary**.

**ilspycmd writes `<name>.decompiled.cs`, not `<name>.cs`.**
`BepInEx\_decompiled\GSD1.cs` sat at **0 bytes** for the whole project because of
this, so nothing had ever been verified against Suikoden 1. Regenerate with:

```powershell
ilspycmd -o <outdir> "<GamePath>\BepInEx\interop\GSD1.dll"
```

**The decompiled files are partly UTF-16.** Pipe through `tr -d '\000'` before
grepping, or matches are missed silently.

---

## 4. Files

| File | Purpose |
|---|---|
| `Gsd1Path.cs` | A*, targets, categories, keys — the main Suikoden 1 file |
| `Gsd1Nav.cs` | `Village_c` hook, position logging, F11/F12 diagnostics |
| `Gsd1Events.cs` | **the event-table decoder** — exits, triggers, interactable objects |
| `Gsd1Probe.cs` | read-only F8 map dump and F7 UI-label dump |
| `Gsd1Names.cs` | NPC name store + **`MapKey`** (used by all four stores) |
| `Gsd1Place.cs` | place names — the arrival-banner hook, the places file, `Shift+M` and `N` |
| `Gsd1Exits.cs` | learned transitions |
| `Gsd1Marks.cs` | player-marked places |
| `Gsd1TextInput.cs` | audio text entry + movement-block prefix |
| `NavProbe.cs` | **Suikoden 2** field entry point (EVENTCON hook, O / F4 keys); Suikoden 2's own files are `Gsd2*.cs` — see the GSD2 handoff |

`NavProbe.Tick()` calls `Gsd1Nav.Tick()`, and is itself called from
`TitleProbe.Tick()` (the existing per-frame heartbeat). One line in
`DialoguePatch.Flush()` calls `Gsd1Path.NoteSpeaker(speaker)`.

---

## 5. Suggested next steps, in order

1. **Verify the three fixes of 2026-08-29 in play.** All are unconfirmed:
   the un-doubled navigation grid (routes should now go round walls, and step
   counts are walking steps), exits and objects coming from `vil_ekp`, and the
   save confirmation reading one highlighted option instead of both.
2. **Name the exits and objects** on the maps you use, with `Shift+K`. They key
   on the event's index in static map data, so the file is worth sharing.
3. **Extend the `eventCom` width table** (§1.7 "Still open" 1) from the unknown
   opcodes the walker logs. Each one added lets more scripts decode cleanly,
   which means more exits with named destinations rather than bare triggers.
4. **The party-join popup** is still unread. Press `F7` while
   one is on screen — that names the components it is built from, which is all
   Milestones 3–4 ever needed.
5. **Verify text entry** in play — especially Enter double-registering.
6. **Suikoden 2** — tracked in the GSD2 handoff (§13 onward), not here.
7. **User configuration** — a BepInEx `Config.Bind` .ini first (verbosity,
   categories, keybindings), an audio-only in-game menu later.
8. **Old text files** written before 2026-08-29 use the retired fingerprint
   keys and never match. Delete them; nothing in them is worth migrating.

---

## 6. Build & test loop

```powershell
dotnet build          # post-build copies the dll into BepInEx\plugins
```

Then play and read `BepInEx\LogOutput.log`. Navigation lines are tagged
`[Nav1]` (Suikoden 1) and `[Nav]` (Suikoden 2).

```powershell
Select-String "\[Nav1\]" BepInEx\LogOutput.log | Out-File -Encoding utf8 nav_out.txt
```

Every reader logs what it found before speaking it, so a wrong readout is
diagnosable from the log without a second play session.
