# Suikoden Access — Pathfinding Handoff, 2026-09-16

> **Current reference.** The project is shelved at v72 and unmaintained; start
> with [`HANDOVER.md`](../HANDOVER.md) in the repository root. File names below
> are as they were when this was written: the docs now live in `docs/`, and
> `PROJECT-STATUS.md` became `HANDOVER.md`.

**For a fresh chat whose subject is navigation and pathfinding, with Suikoden 2
as the target.** Self-contained: you should not need the other documents to start,
though `Suikoden-Access-NAV-STATUS.md` is the full reference and
`Suikoden-Access-HANDOFF-2026-09-05.md` is the current Suikoden 1 delta.

Plugin version at time of writing: **v53**. Suikoden 1's navigation is working and
in daily use. Suikoden 2 has position and identity and **no collision at all**.

---

## 0. Read this section before proposing anything

**The same collision question was answered wrongly four times over five weeks in
Suikoden 1.** Every wrong answer was a plausible inference about a coordinate
system, checked only against that same coordinate system. Each one looked settled
and each one let A\* walk through a wall.

| When | The rule in the code | Why it was believed | Symptom |
|---|---|---|---|
| before 2026-08-29 | doubled the input, then halved it again | — | the quadrant bit always came out as bit 0, so only the NW quarter was ever tested: **249 solid cells read as open** |
| 2026-08-29 | `GNDAtariCheck(x>>1, y>>1)` | `MAP.index` is 576 = 24×24, so width/height "must" be chips and `map_x` "must" be half-scale | queried a different part of the map |
| 2026-09-02 | building layer in chip space, high nibble | a 4-bit mask "must" describe a chip's four quadrants — what else has four parts? | blocked 903 cells, **109 of them cells the player had stood on** |
| 2026-09-03 | `level` = the map's `layer_no` | it worked on the map it was validated on | Town A's `layer_no` is 2 and level 2 holds nothing, so **every house read as open floor** |

Notice the shape. Arithmetic about a coordinate system cannot referee a question
about that same coordinate system.

### The method that finally worked, and the one you must use here

**Find an observation the game makes *for* you that no interpretation can
reinterpret, and score every candidate against it.** Reserve reasoning about the
numbers for *generating* candidates, never for *choosing* between them.

For Suikoden 1 that observation was: **a cell the player has STOOD ON is
walkable.** `Gsd1Nav.NoteVisited()` records every distinct occupied cell, every
frame, keyed by map. `F6` writes that set out beside the whole map's collision
grid. Every candidate reading was then scored offline by how many walked cells it
calls solid:

| Reading | Cells blocked | Walked cells called solid |
|---|---|---|
| **`GNDAtariCheck(cell) != 0`** | 1037 | **0** |
| `GNDAtariCheck(chip) & quadrant bit` | 750 | 81 |
| **`BLDAtariCheck(cell) & 0x0f != 0`** | 166 | **0** |
| `BLDAtariCheck(cell) != 0` | 812 | 79 |
| `BLDAtariCheck(chip)` high nibble & bit | 903 | 109 |
| `BLDAtariCheck(chip) != 0` | 1276 | 130 |

The same move settled three other questions in this project: `VILLAGEOUT`'s
stated destination matched where the player actually landed; the save point named
itself because the game itself opened the save UI; and the village name came from
the plate the game drew on screen.

### Four cautions, each paid for

1. **Zero violations is necessary, not sufficient.** A rule that blocks nothing
   also scores zero. Check that it blocks a substantial amount, and *draw* it —
   the winning Suikoden 1 pair draws a room with a desk against its north wall
   and floor in front of it, which is where the save diary is.
2. **The walked-cell test proves a rule does not OVER-block. Nothing proves it
   does not UNDER-block except trying another map.** That is exactly how the
   `layer_no` error survived. **One map is enough to find a rule and never enough
   to bound it.**
3. **Never cap a measurement.** A truncated sweep is how an assumption survives.
   The first Suikoden 1 name sweep printed 96 of 256 ids and hid the evidence
   that refuted the filter built on it. **Two live caps in the Suikoden 2 probe
   are named in §4 — they are the first thing to fix.**
4. **Log the zero.** "Found none" and "never looked" must never look the same in
   a log. Four conclusions were once wrong because every reader logged its count
   only `if (n > 0)`.

### A standing instruction from the project owner

**Quality over speed. Exhaust real pathfinding before falling back to a waypoint
graph.** The FF Pixel Remaster mods use waypoints with greedy stepping and
stuck-detection, and that remains the worst case, not the plan. When something is
genuinely ruled out, say so plainly and stop circling it.

---

## 1. The two games are different engines

They ship in one executable and share `GSDShare.dll` / `ShareUI.dll` for menus and
battle — which is why the title, menu and battle work ported across for free — but
their field/map code is two separately-ported PSX codebases with nothing in
common below the UI layer.

| | Suikoden 1 (`GSD1.dll`) | Suikoden 2 (`GSD2.dll`) |
|---|---|---|
| Field controller | `Village_c` | `EVENTCON` |
| Player entity | `Village_c.player` (`HUMAN`) | `ehuman[0]` (`EVENT_HUMAN`) |
| Coordinates | `map_x` / `map_y`, byte tile coords | `px` / `py` ints; entities use `EVENT_POS` |
| Coordinate scale | 1 = one tile | `px * 8` = `EVENT_POS`; tile = 16 units |
| NPC list | `map_human` | `ehuman` |
| Event/exit table | `vil_ekp` (`List<EVENT_WK>`) | `MACHIDAT.eventdata`, plus `MAP.tokusyu_atari` |
| Collision | **solved** | **unsolved** |
| Movement | **4-way only** | **8-way (diagonals legal)** |

**Do not assume anything found in one game exists in the other.** And note the
movement difference: Suikoden 1's A\* had to be forced to 4 directions because an
8-way search emitted "northeast 6" routes the player physically cannot walk.
Suikoden 2 is genuinely 8-way, so that constraint lifts — but verify it in play
rather than trusting this line.

**Type names collide.** `GAME_WORK` is declared in both. So is `WindowManager`.
So is `MAP`. `Gsd.FindAnywhere` searches in a fixed preference order and silently
returned GSD2's `GAME_WORK` in a Suikoden 1 session, which made every story-flag
read report "method not found" for a week. **Use `Gsd.FindIn(dll, name)` for
anything belonging to one game's field code.**

---

## 2. Suikoden 1, as the worked example

This is what a finished answer looks like. It is not a template to copy
numerically — the engines differ — but the *shape* of it is what you are aiming
for in game 2.

```csharp
blocked(x, y) = GNDAtariCheck(x, y, false) != 0
             || (BLDAtariCheck(x, y, L, ref no, false) & 0x0f) != 0
                for any L in the levels this map actually answers at
```

* **Both oracles take CELL coordinates** — the player's own `HUMAN.map_x`/`map_y`,
  **unshifted**. Nothing is doubled and nothing is halved, anywhere.
* The **ground byte** is a 4-bit mask of *one cell's four quarters*. Any bit set
  means solid. It is not a chip's four cells.
* The **building ushort** packs two 4-bit masks. Only the **low nibble** decides.
* **`level` is not `layer_no`.** Levels 0 and 1 carry all the geometry on every
  map sampled; 2 and 3 are empty everywhere. `FindLiveLevels()` samples a coarse
  grid at each level once per map, caches by map key, and ORs every live level.
* **Out-of-range must read as SOLID.** The oracle answers off-map with `0` =
  fully open, so a wrong bound makes the whole grid walkable. Cell range is
  `0..2*MAP.width-1`.
* **A self-check makes it safe to switch on without a play session.** Whatever
  else is true, the player is standing somewhere walkable. If the combined
  collision says the player's own cell is blocked, the router drops back to
  ground-only for that request and logs it, rather than reporting "no path" from
  a corrupt grid. This is what first showed the chip reading was wrong.

Two structural lessons that probably *do* transfer:

* **Collision was two layers and only one was being read.** Interiors read as one
  open rectangle for weeks because a room's walls, bed and bookcase are all
  `TATEMONO`, which the ground layer knows nothing about. **GSD2's `MAP` also has
  `tatemono_kazu` and `tatemono_data`** (§4), so look for the same split before
  concluding a single grid is wrong.
* **Exits turned out to be static data with destinations.** `vil_ekp` events
  carry `VILLAGEOUT destMap, destX, destY`, verified against a real transition.
  That replaced a whole learned-exit mechanism. **GSD2 has a plausible analogue
  in `MAPINFO` and `TOKUSYU_ATARI` (§4) and nobody has looked.**

---

## 3. Suikoden 2 — what is CONFIRMED

All of this was read at runtime, not inferred.

* `EVENTCON` is captured via a postfix on `NewEventPlayerMove()` — **zero
  arguments, nothing to marshal**. It is the only hook in the Suikoden 2 probe.
* `mno` (map id) flips correctly per area.
* `px` / `py` track the player. `mx` / `my`, `mapw`, `maph` are also readable.
* `ehuman[0]` **is the player** (`fpno = 1`).
* `otyp` classifies entries: `255` = unused slot, `1` = NPC, `2`/`4` = other.
* **`EVENT_POS` is a 16.16 fixed-point struct**, not an int — read `all / 65536`.
  `Convert.ToInt32` on it *throws*, which made `x`/`y` look unreadable for a
  while. Confirmed: `all=28835840` → 440.0, `all=34603008` → 528.0.
* **`px * 8 == EVENT_POS` exactly** (440/8 = 55, 528/8 = 66).
* **Map geometry:** 960×960 EVENT_POS units = **60×60 tiles at 16 units**, 6
  layers. Player tile = `px/2`, `py/2`.

---

## 4. Suikoden 2 — what is RULED OUT, what is UNTRIED, and the two live caps

### Routes already tried

| Route | Result |
|---|---|
| `MAP.atari_data` | 15×64 = 960 ints, all zero **in sampled rows**. Not a position grid — 960 is chip-count shaped. |
| `han_new` | 6 layers × 3600 (60×60), values only `{1,2,4}`, ~95% `4`. Too uniform for walkability on a cave map — probably height or layer. |
| `index_new` | 6 × 3600, 33–313 distinct values = tile graphics. |
| `BGCON.EventMapAtariCheckF1(byte typ, int mno, int px, int py)` | Instance found at **`EVENTCON.bcon`** (a direct field — `honprg` reads null). Returns **constant 0** for typ 0–7 at scales 1 and 8; **scale 16 throws** out of bounds (`79*16 > 960`). |
| `EVENTCON.EventMapAtariCheck(int pno, int dir)` | Value params only, so safe to call, but it queries an **entity's current position** — cannot query arbitrary tiles. Written off as "useless for A\*". **See §5: that write-off is probably wrong.** |

The parameter names above are **real** — the interop decompile carries signatures
*with* parameter names, so `(pno, dir)` is not a guess. What it carries is
signatures only, **never method bodies**, so no question about what a method
*does* can be answered from the decompile. Only measured in play.

### The two caps, which are the first thing to fix

Both are in `NavProbe.cs` and both violate caution 3 above.

1. **The `typ` sweep is capped at 8 of 256.** `ProbeBgCollision` runs
   `for (byte typ = 0; typ < 8; typ++)`. `typ` is a **byte**. The project's own
   rule — *a constant probe result means "wrong parameters", not "no data"* — was
   applied to the scale and not to the selector. **Sweep all 256.**
2. **The `F9` ehuman dump is capped at 20 entries.** It does say
   "(truncated at 20 of N)", so it is honest, but a map with more NPCs is being
   sampled rather than measured.

Also unswept: the coordinate scale tried only 1, 8 and 16. Not tried: 2, 4, 32,
or the raw `EVENT_POS.all` value.

> **An inference, labelled as one, worth testing rather than believing.** Scale 16
> threw out of bounds at `79*16 = 1264` against something of size **960**, and
> `MAP.atari_data` is exactly 960 entries (15×64). That is consistent with
> `EventMapAtariCheckF1` indexing a chip-attribute table directly rather than a
> position grid. It is also consistent with several other things. Do not build on
> it; use it to generate a candidate and then score the candidate.

### The chip-attribute hypothesis, written and NEVER RUN

`NavProbe.ProbeChipAttributes` implements:

```
chip = index_new[layer][ty * 60 + tx]
attr = atari_data[chip / 64].data[chip % 64]
```

The reasoning: an earlier pass sampled only `atari_data` rows 0–1 (chips 0..31 and
64..95) and saw zeros, but `index_new` holds chip ids up to ~313, which live in
rows 3–4. So the table may well be non-empty where nobody looked. The probe
histograms **all** of `atari_data` and resolves the attribute around the player.

**It has never been run.** `F10` in a Suikoden 2 session runs both this and the
BGCON sweep in one press. **If the full histogram has more than one distinct
value, that is the collision table and A\* follows almost immediately. If it is
uniformly zero, that route is dead — say so plainly and stop.**

### Never looked at, and they may matter more than the grid

These come from the GSD2 decompile and **no runtime reading of any of them
exists**. They are listed so the next session does not have to re-derive the
inventory.

```
MAP            tokusyu_kazu  tokusyu_atari  srn_kazu  tatemono_kazu  ID
               maxw  maxh  pri  up  shad  csr_kazu  typ
               atari_data  index  machi_opd  srn_type
               tatemono_data  index_new  han_new

TOKUSYU_ATARI  typ  x  y  w  h  dat0  dat1  dat2
MAPINFO        machist  machino  memtyp  mapno  px  py  d1  d2  bgmno  seqno  vabno
MACHIDAT       eventdata  skaz  map  overlay_func  hkaz  epersondat  etcdata
               battle_dat  msdat  dummy_etcdata
EVENTCON       mno px py mx my mapw maph ehuman mdat bcon mcon
               prePx prePy doorStateList est ewin ...
```

Two things jump out of that list.

* **`TOKUSYU_ATARI { typ, x, y, w, h, dat0, dat1, dat2 }` is a typed rectangle
  with three data words** — structurally the same idea as Suikoden 1's `vil_ekp`
  trigger regions, and `EVENTCON` has `EventTokusyuAtariCheck(int)`,
  `ChangeDoorState(int, TOKUSYU_ATARI, DOOR_STATE)` and
  `GetDoorState(TOKUSYU_ATARI)`. This is very likely where doors, stairs and
  transitions live. `F10` already dumps `tokusyu_atari` with its `typ`, so a
  single press produces the first real sample.
* **`MAPINFO` carries `mapno`, `px`, `py`** — a destination triple — and
  `EVENTCON.EMapinfoPtrGet(byte, byte)` returns one. If `TOKUSYU_ATARI.dat0/1/2`
  index into `MAPINFO`, then **Suikoden 2's exits are statically enumerable with
  destinations**, exactly as Suikoden 1's turned out to be. That would be the
  single biggest win available and it is pure field reads.

**`TATEMONO` exists in GSD2's `MAP` too.** In Suikoden 1, missing the building
layer made every interior read as one open rectangle for weeks. Check for the
same split before concluding a single plane is the whole answer.

---

## 5. The three things worth doing first, in order

### 5.1 Build the walked-cell recorder for Suikoden 2. Nothing else is falsifiable without it.

Suikoden 1 has `Gsd1Nav.NoteVisited()` + `F6`. **Suikoden 2 has no equivalent, and
that is the real reason its collision is unsolved** — not a shortage of
candidates, but the absence of anything that can referee between them. Every one
of the four wrong Suikoden 1 answers would have been caught in one play session by
this recorder.

It is the cheapest thing in this document: **pure field reads of `px`/`py`**, no
game call, no hook, keyed by `mno`, capped only against unbounded growth, written
out on a keypress alongside whatever candidate grid is being tested. Build it
before testing any hypothesis, not after.

### 5.2 Use `EventMapAtariCheck(pno, dir)` as the referee, not as the router

NAV-STATUS writes this method off as "useless for A\*" because it only asks about
an entity's *current* position. That is true and it is the wrong conclusion.
**An oracle that answers only about where the player is standing is exactly what
§0 asks for: an observation the game makes for you that no interpretation can
reinterpret.** Stand somewhere, ask all eight directions, and you have eight
labelled ground-truth samples — which cell, which direction, blocked or not — to
score any candidate grid against. Walk around and they accumulate.

That converts the hardest unknown in game 2 into a scoring problem of the kind
this project has already solved.

**Two cautions before implementing it.**

* It takes value parameters only, so *calling* it is the acceptable risk category
  — the same one as `GNDAtariCheck`, verified safe in play. But the standing rule
  is **one-shot, never per frame**. Sampling on a keypress is unambiguously safe.
  Sampling on every change of player tile is a few calls a second and is probably
  fine, but it is a **deliberate decision to make and log**, not something to slip
  in.
* `pno` is a person index, so confirm `pno = 0` is the player (`ehuman[0]` is,
  with `fpno = 1`) before trusting any sample.

### 5.3 Press `F10` in a Suikoden 2 field map and read what comes back

One press settles several things at once, and none of them have ever been
observed:

* the full `atari_data` histogram — **the chip-attribute route lives or dies here**;
* the resolved chip→attribute window around the player;
* `han_new` / `index_new` per-layer histograms;
* the `tokusyu_atari` zone list with types — the doors/exits lead;
* the BGCON `typ` × scale sweep (after raising the cap to 256).

---

## 6. Rules paid for in bugs — do not relearn these

**Never Harmony-patch a hot-path method taking a by-ref struct parameter.**
This one was paid for *in Suikoden 2 specifically*. A postfix using
`object[] __args` on `EVENTCON.EventMapAtariCheckFunc1(int, int, ref RECT)`
**corrupted live collision in-game**: walls stopped blocking and the player slid
along map edges. Harmony round-trips by-ref structs through boxing even when the
postfix never writes to them. `EventMapAtariCheckFunc1` and
`EventAtariCheckFunc1` both take `ref RECT` — **they are off limits.**

**Never patch a base virtual AND its overrides.** Three trampolines in one vtable
chain crashed the game natively at startup. A loop over class names is a sweep.

**A hand-patched class must NOT carry a `[HarmonyPatch]` attribute** — it makes
`PatchAll` abort `Plugin.Load()` and disable the entire mod.

**Calling a value-parameter-only game method is a different, acceptable risk.**
Verified safe in play: `GNDAtariCheck`, `BLDAtariCheck`, `GetCharacterName`,
`GetStoryFlagS`. Keep such calls one-shot — a keypress or a map load — never per
frame.

**`CountOf` must check `Length` as well as `Count`** — Il2Cpp arrays expose
`Length`, lists expose `Count`. Checking only `Count` made every array silently
read as `-1`.

**Il2Cpp lists are not `System.Collections.IEnumerable`** — a `foreach` via that
cast silently finds nothing even though `.Count` works. Index them.

**This game runs the new Input System exclusively.** Legacy `UnityEngine.Input`
is compiled in but throws at runtime. Use `Keyboard.current`.

**A constant probe result means "wrong parameters", not "no data".** When probing
an unknown API, sweep parameters and report only combinations that **vary**.

**Never speak an internal map key aloud.** Places are named from the player's own
labels or from a name the game itself displayed.

---

## 7. Environment, build and test loop

Game path: `E:\SteamLibrary\steamapps\common\Suikoden I and II HD Remaster`

```powershell
dotnet build
```

The post-build step copies the dll into `BepInEx\plugins`. **`TitleProbe.Version`
prints at startup — confirm it before trusting any test result.** A stale build
has masked several sessions. Currently **v53**.

Read `BepInEx\LogOutput.log` directly; it does not need pasting. Tags: `[Nav]`
Suikoden 2, `[Nav1]` Suikoden 1, `[G1Dump]` dumps, `[Save]` save/dialog, `[Gsd]`
lookups, `[Menu]` in-game menus, `[Battle]`, `[Dialog]`.

**`BepInEx\_decompiled\GSD1.cs` is a valid 23 MB ASCII decompile.** The others,
including the 145 MB `GSD2.cs`, are partly UTF-16 — **pipe through `tr -d '\000'`
before grepping or matches vanish silently.** The interop decompiles carry
signatures *and parameter names* but **never method bodies**.

Useful sweep for finding what a class declares (the method that has found every
subsystem in this project):

```powershell
Select-String -Path BepInEx\_decompiled\*.cs -Pattern "NativeMethodInfoPtr_(\w+) = IL2CPP\.GetIl2CppMethodByToken\(Il2CppClassPointerStore<(\w*Battle\w*)>" |
  ForEach-Object { "$($_.Matches[0].Groups[2].Value) :: $($_.Matches[0].Groups[1].Value)" } |
  Sort-Object -Unique
```

### Keys currently in use

| Key | Action |
|---|---|
| `J` / `L` | previous / next target |
| `K` | read current target |
| `Shift+J` / `Shift+L` | cycle category |
| `P` | pathfind, speaks steps |
| `M` | mark current tile |
| `Shift+M` | say where the player is |
| `N` | name this area |
| `Shift+K` | name the selected target |
| `F5` | name probe + NPC identity questionnaire (also automatic per map) |
| `F6` | whole-map collision + walked cells → `suikoden1_collision_dump.txt` |
| `F7` | every on-screen label with its hierarchy path |
| `F8` | event table, doors, objects, people, `Event_c` state |
| **`F9` / `F10`** | **Suikoden 2 probes — ehuman dump / collision dump** |
| `F11` | map / collision / NPC dump |
| `F12` | collision window around the player |

`O` is free. All of `F5`–`F12` are taken.

### Files

| File | Purpose |
|---|---|
| **`NavProbe.cs`** | **Suikoden 2 probes (F9/F10) — diagnostics only, no pathfinding** |
| `Gsd1Path.cs` | A\*, targets, categories, collision — the main Suikoden 1 file |
| `Gsd1Nav.cs` | `Village_c` hook, position logging, **walked-cell recording**, F6/F11/F12 |
| `Gsd1Events.cs` | event-table decoder — exits, triggers, objects, story flags |
| `Gsd1Probe.cs` | read-only F8 map dump, F7 UI-label dump, F5 name probe |
| `Gsd1Names.cs` | NPC name store, `MapKey`, and the game's own character names |
| `Gsd1Place.cs` | place names — name-plate capture, `Shift+M`, `N` |
| `Gsd1Exits.cs` | learned transitions (fallback only) |
| `Gsd1Marks.cs` | player-marked places |
| `Gsd1TextInput.cs` | audio text entry + movement-block prefix |
| `TitleProbe.cs` | title, menus, battle, and the `Gsd` type/method lookup helpers |
| `MainMenuReader.cs` | the in-game pause menu |

`NavProbe.Tick()` calls `Gsd1Nav.Tick()` and is itself called from
`TitleProbe.Tick()`, the existing per-frame heartbeat.

---

## 8. Open items on the Suikoden 1 side, so they are not lost

None of these blocks game 2. They are carried here only so a pathfinding chat
knows what is mid-flight elsewhere.

* **NPC names.** The digit-bearing `CHARACTER_ID` band (`FP_00P`..`FP_21P`) is
  both generic sprite slots and named characters, so it names town A
  townspeople "character C". Spoken as "possible character C" until scored. The
  questionnaire and four candidate rules now run automatically once per map.
* **Dead events.** The rule (`GetTmpEventFlagS`, `EVENTFCHK` wants the bit set,
  `EVENTFCHK_NO` wants it clear, a place is live if any alternative script can
  fire) is scored only on the dead side. `Gsd1Flags.DropDeadEvents` stays `false`
  until a `RULE CONFIRMS` line appears. Note for game 2: **`eventWK` carries
  sentinel coordinates (254,254 / 255,255) for person and region events**, so
  matching a running event back to its table entry by position mostly fails.
* **Place names.** Working. A village names itself; a room does not and is the
  player's to name. Static candidates (`G1_SID_MSG`, `GetVilLabel`,
  `GetAreaLabel`) are logged at every arrival and not yet promoted.
* **Menus below the top level**, shops and storage are not read yet.

---

## 9. The one-paragraph version

Suikoden 2's collision is unsolved because nothing in that game can currently
referee between competing hypotheses — not because there are too few
hypotheses. Build the walked-cell recorder first (pure field reads of `px`/`py`,
keyed by `mno`), add `EventMapAtariCheck(pno, dir)` as a sparingly-sampled
ground-truth oracle, then press `F10` with the `typ` sweep uncapped and read the
`atari_data` histogram and the `tokusyu_atari` zone list. If the chip-attribute
table is uniformly zero, say so and close that route. And look at `TOKUSYU_ATARI`
and `MAPINFO` early — Suikoden 1's biggest single win was discovering that exits
were static data with destinations, and game 2 has the same shape sitting
unexamined.
