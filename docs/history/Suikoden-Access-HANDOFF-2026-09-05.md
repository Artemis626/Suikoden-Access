# Suikoden Access — Session Handoff, 2026-09-05

> **Historical document, superseded.** Kept for the reasoning, not as
> instructions. For the current state, start with
> [`HANDOVER.md`](../../HANDOVER.md) in the repository root. Version numbers,
> next steps and file locations below are out of date.

**Supersedes `Suikoden-Access-HANDOFF-2026-09-03.md`.** Read this, then
`Suikoden-Access-NAV-STATUS.md` §1.1 (the collision oracles) and **§1.9 (the
method)** — §1.9 is the more important of the two and §2 of this file is why.

Suikoden 1 only. Suikoden 2 remains on hold. Plugin version at time of writing:
**v53**.

**Revised twice more the same day**, after the first live run of v52. Section 5
is rewritten: the method that was hooked never fires, and the name comes from
the plate's own text instead. Section 12 is new: the in-game menus.

**Revised earlier the same day.** Sections 3, 4, 5, 6 and 7 are rewritten: the NPC
name band is isolated and the measurement that settles it is built (nothing is
decided yet, and the answer needs the player); the story-flag rule is switched to
the tmp bank and now collects its own missing half of the proof; and place names
are solved — the game hands the mod the village's display name itself.

---

## 1. Confirmed working in play

| Feature | Evidence |
|---|---|
| **A\* indoors AND outdoors** | Routes now go *round* houses in town A and round furniture in the mansion. `path to character C 1: 34 cells -> east 20, south 11, west 2 [blocked by ground 0 / buildings 44]` |
| **Exits from the event table** | `ev[0] Exit at (19,35) -> map 1 at (17,36)`, walked and verified |
| **Save points name themselves** | From `EVENTSHOP` + `SHOP_SAVE` in the script tail |
| **Duplicate places collapse** | `ev[19] is the same place as ev[1]; dropped` — the mansion staircases were being announced twice, once as an exit and once as an event |
| **Learned exits no longer invent themselves** | 11 junk entries purged on load; the cutscene false-positives are gone |
| **Some NPC names are real** | Character B, character A, character F, character G, character I, character E, character J, character K, character L, character M — confirmed by a player who knew who was in the room |
| **Main menu names the game** | "Suikoden One" (spelled out — the synthesiser reads a lone roman `I` as the letter) |
| **Place names, from the game itself** | `WindowManager.PlayVillageNameAnimationHeaderUI("town A")` — the arrival plate a sighted player reads. `Shift+M` says where you are; `N` names the room; a load announces itself |
| **Dead-event rule on the tmp bank** | `GetTmpEventFlagS`, alternatives OR-ed per place, still in report mode pending one live firing |
| **The pause menu reads its commands** | `UIWindowList` / `UIWindowListTop` cell cursors, gated on `UIMainMenu.Open()` so battle is untouched — §12 |
| **Confirmations say what they ask** | the quit-to-title warning was in the log all along and never spoken |

---

## 2. The collision saga — READ THIS BEFORE TOUCHING COLLISION

**The same question was answered wrongly four times over five weeks.** Each
answer was argued from the data, each looked settled, and each let A\* walk
through a wall. This section exists so it does not happen a fifth time.

### The answers that were wrong

| When | The rule in the code | Why it was believed | Symptom |
|---|---|---|---|
| before 2026-08-29 | doubled the input, then halved it again | — | net no-op landed on the right cell, but the quadrant bit always came out as bit 0, so only the NW quarter was ever tested: **249 solid cells read as open** |
| 2026-08-29 | `GNDAtariCheck(x>>1, y>>1)` | `MAP.index` is 576 = 24×24, so width/height "must" be chips and `map_x` "must" be half-scale | queried a different part of the map |
| 2026-09-02 | building layer in chip space, high nibble | a 4-bit mask "must" describe a chip's four quadrants — what else has four parts? | blocked 903 cells, 109 of them cells the player had stood on |
| 2026-09-03 | `level` = the map's `layer_no` | it worked on the map it was validated on | **town A's `layer_no` is 2 and level 2 holds nothing, so every house read as open floor** |

Notice the shape: every one is a *plausible inference about a coordinate system,
checked only against the same coordinate system*. Arithmetic cannot referee that.

### The answer, and it is measured

```csharp
blocked(x, y) = GNDAtariCheck(x, y, false) != 0
             || (BLDAtariCheck(x, y, L, ref no, false) & 0x0f) != 0
                for any L in the levels this map actually answers at
```

* **Both oracles take CELL coordinates** — the player's own `HUMAN.map_x`/`map_y`,
  **unshifted**. Nothing is doubled and nothing is halved, anywhere.
* The **ground byte** is a 4-bit mask of *one cell's four quarters*. Any bit set
  means solid. Not a chip's four cells.
* The **building ushort** packs two 4-bit masks. Only the **low nibble** decides.
  It is never non-zero where the high nibble is zero.
* **`level` is not `layer_no`.** Sampled across three maps, levels **0 and 1**
  carry all the geometry and levels 2 and 3 are empty *on every map*, including
  the one whose `layer_no` is 2:

  ```
  level 0: 37/576   level 1: 37/576   level 2: 0/576   level 3: 0/576  (layer_no 1)
  level 0: 28/768   level 1: 28/768   level 2: 0/768   level 3: 0/768  (layer_no 1)
  level 0: 119/756  level 1: 119/756  level 2: 0/756   level 3: 0/756  (layer_no 2)
  ```

  `FindLiveLevels()` samples a coarse grid at each level once per map, caches by
  map key, and ORs every live level. Per-level counts are logged on every map.
* Out-of-range reads as **SOLID**. The oracle answers off-map with `0` = fully
  open, so a wrong bound makes the whole grid walkable. Cell range is
  `0..2*MAP.width-1`.

### How it was actually settled — the method that works

**A cell the player has STOOD ON is walkable, whatever any hypothesis says.**

`Gsd1Nav.NoteVisited()` records every distinct occupied cell, every frame, keyed
by map. `F6` writes that set out beside the whole map's collision grid. Every
candidate reading is then scored offline by how many walked cells it calls solid.
On 233 cells of one map:

| Reading | Cells blocked | Walked cells called solid |
|---|---|---|
| **`GNDAtariCheck(cell) != 0`** | 1037 | **0** |
| `GNDAtariCheck(chip) & quadrant bit` | 750 | 81 |
| **`BLDAtariCheck(cell) & 0x0f != 0`** | 166 | **0** |
| `BLDAtariCheck(cell) != 0` | 812 | 79 |
| `BLDAtariCheck(chip)` high nibble & bit | 903 | 109 |
| `BLDAtariCheck(chip) != 0` | 1276 | 130 |

**Three cautions that were each paid for:**

1. **Zero violations is necessary, not sufficient.** A rule that blocks nothing
   also scores zero. Also check that it blocks a substantial amount, and *draw*
   it — the winning pair draws a room with a desk against its north wall and
   floor in front of it, which is where the save diary is.
2. **The walked-cell test proves a rule does not OVER-block. Nothing proves it
   does not UNDER-block except trying another map.** That is exactly how the
   `layer_no` error survived: one map, `layer_no == 1`, never checked elsewhere.
   **One map is enough to find a rule and never enough to bound it.**
3. **Capping a measurement is how an assumption survives.** The first name sweep
   printed 96 of 256 ids and hid the evidence that refuted the filter built on it.

### The general rule, for any probe whose meaning is in doubt

Find an observation the game makes *for* you that no interpretation can
reinterpret — where the player has been, where they arrived after a warp, which
target they say is dead — and **score every candidate against it**. Reserve
reasoning about the numbers for *generating* candidates, never for *choosing*
between them. Same shape as the `VILLAGEOUT` proof (a stated destination matched
where the player actually landed). This is NAV-STATUS §1.9 and it is the single
most valuable thing in the documentation.

---

## 3. NPC names — the band is isolated, the measurement is built, the answer is yours

`Fhuman_c.GetCharacterName(int hno, bool short)` is a **static** method on a
static class, value parameters only, returning the localized display name.
`hno` is `EVENT_WK.image`. Confirmed against a player standing in the room:

```
ekp[16] putno=11 image=16  (FP_B) -> 'character B'   was "person 11"
ekp[22] putno=12 image=110 (FP_A) -> 'character A'
```

These names are **never written to `suikoden1_npc_names.txt`** — they come from
static data and are recomputed each session, so a mistake is a bug to fix, not a
wrong name persisted into a file people share. A name typed with `Shift+K` still
wins and is still saved. Targets sharing a name are numbered ("soldier role 1
1", "soldier role 1 2").

### The problem, stated once

`CHARACTER_ID` has 165 members: 143 three-letter character codes (`FP_A`,
`FP_B`) and 22 slots whose symbol carries a digit (`FP_00P`..`FP_18P` =
119–137, `FP_19P`..`FP_21P` = 146–148). **Those 22 are both things at once.**
`FP_02P` is "generic person sprite 2" *and* it is character C's character id. So an
id in that band is genuinely ambiguous and **cannot be resolved from the id
alone** — which is why the two rules adopted so far, one filtering the band out
and one letting it through, were both partly right and neither correct.

What the last run produced, for the record:

| image | symbol | name | verdict |
|---|---|---|---|
| 1, 2, 6, 108, 110, 113, 114 | `FP_I`, `FP_E`, `FP_J`, `FP_K`, `FP_A`, `FP_L`, `FP_M` | Character I, character E, character J, character K, character A, character L, character M | **correct** |
| 121 | `FP_02P` | Character C | **wrong** — a townsperson |
| 122 | `FP_03P` | Character D | **wrong** — a townsperson |
| 125 | `FP_06P` | Soldier role 2 | suspect (that army does not exist yet) |
| 126 | `FP_07P` | Villager role | plausible, unverified |
| 132, 136 | `FP_13P`, `FP_17P` | *(text id −1)* | correctly left generic |

### What was done this session — and what was deliberately NOT done

**No rule was chosen.** Choosing one from the id ranges is the move that has
already been made twice and been wrong twice, and §1.9 forbids it: reasoning
about the numbers generates candidates and never picks between them.

Two things were built instead.

**1. An honest interim label.** An id in the digit-bearing band is spoken as
**"possible character C"**, not "character C". The name is still spoken, because the
player has to be able to *find* that target in order to go and check who it is —
and because the alternative is a room full of "person 5". But it is spoken as a
question, so nothing false is asserted. `Gsd1GameNames.IsAmbiguousBand` and the
`"possible " +` prefix in `Gsd1Path.AddHumans` are one line each to delete once
the band is settled. **This is a hold, not a rule**, and it is uniform across
the whole band precisely so that no rule is smuggled in through it.

**2. The measurement, on `F5`.** The name probe now prints an **identity
questionnaire**: one row per person on the map, headed by *the exact label the
target list speaks* — "possible character C 2" — so an answer can be tied to a row
with no ambiguity, followed by every candidate discriminator side by side:

```
Q ekp[i] | 'spoken label' | (x,y) | image | CHARACTER_ID symbol | proposed name |
  textId | digitSymbol | chara_no | map_event_no | putno | flag1 |
  sharedByPeople | hitoSlots | comBytes | has0x1c
```

* `digitSymbol` — the three-letter-code rule.
* `chara_no` — read 0 for ambient NPCs once, before several other things were
  fixed; read again here rather than taken on trust.
* `sharedByPeople` / `hitoSlots` — "a unique character appears once, a generic
  sprite is reused", measured over the whole map rather than assumed.
* `flag1` — never examined before; free to print.
* `comBytes` / `has0x1c` — story dialogue against an ambient one-liner, as a raw
  byte observation. It does **not** claim the script contains a `STORYMSG`: the
  COM operand widths cannot be recovered (§1.7), so this reports only that the
  byte appears, and is labelled as a candidate signal.

Nothing is capped and nothing is filtered — capping the first sweep at 96 of 256
ids is how the last wrong rule survived.

### The measurement now runs itself — and why it had to

The first live run of v52 produced **no questionnaire rows at all**. The player
mapped four maps, crossed two areas and reloaded a save, and the one measurement
that settles this needed an `F5` that nobody had a reason to remember. *A
measurement that only happens when somebody triggers it is a measurement that
does not happen.*

So it runs **once per map, automatically**, 1.5 seconds after the map key settles
(the key appears before `map_human` has finished filling, and a questionnaire
taken on the first frame would list half a room and never run again for that
map). `F5` still triggers it on demand.

Each map now also logs **what every candidate rule would DO**, so a rule can be
checked against names already known to be right or wrong without reconstructing
it by hand from sixteen columns:

```
CANDIDATE 'accept every name the game gives': names 29 of 29 people, leaves 0 generic -> character C, character D, …
CANDIDATE 'reject digit-bearing symbols (FP_00P..FP_21P)': names 0 of 29 …
CANDIDATE 'accept only an image carried by ONE person on this map': …
CANDIDATE 'accept a three-letter symbol, or a unique image': …
```

A rule that keeps nothing scores no violations either, so the count kept is
printed beside the count dropped — the same caution that the collision scoring
needed.

### Where the evidence actually stands, stated plainly

| | |
|---|---|
| **Confirmed CORRECT** | Character A, character E, character I, character J, character K, character L, character M, character F, character G, character B |
| **Confirmed WRONG** | Character C, character D |
| **Unverified** | Soldier role 2, villager role, object-like name, official role, soldier role 1 |

Every confirmed-correct name is a three-letter symbol. Both confirmed-wrong names
are digit-bearing. **No digit-bearing name has ever been confirmed correct** —
and that is the fact the 2026-09-04 retraction was missing: the digit filter was
dropped on the argument that "official role" and "soldier role 1" *look* useful, not
on evidence that they are right.

That is two hits and zero known losses for rejecting the band, which is
suggestive and is **not** enough. Two data points is roughly what each of the
four wrong collision rules had. What would settle it either way is one of:

* a digit-band name that is **right** — most likely the palace guards reading
  "object-like name" (image 123) or a "villager role" in a village that has one.
  One such case saves the band and points at the uniqueness rule instead;
* the **uniqueness** column doing the work. Town A's town map has 29 people
  and about six distinct images, so generic sprites are demonstrably reused,
  while character A and character E appear once each. If "carried by exactly one person on
  this map" keeps all ten correct names and drops both wrong ones, it beats the
  digit rule on the same evidence *and* explains why the band is ambiguous
  instead of just avoiding it — and it keeps "villager role" in the village where
  there is exactly one of him.

Both come out of the automatic per-map rows with no extra effort: **play, and the
log scores them.** Then adopt on two maps, not one, and delete the "possible"
prefix.

---

## 4. Story flags — the bank is switched, one confirmation still stands between report and drop

An event's FORM script is *when it fires*, and two opcodes are its conditions:
`EVENTFCHK` (0x07) and `EVENTFCHK_NO` (0x08), two operands each. Operand **a** is
a small index, operand **b** is always a power of two (4, 8, 16, 32, 64), so
`(index, mask)` is settled by shape.

**`GAME_WORK` is declared in BOTH GSD1.dll and GSD2.dll.** `Gsd.FindAnywhere`
prefers GSD2, handed back the wrong one, and every flag read silently reported
"method not found". `Gsd.FindIn(dll, name)` exists for exactly this.

### The rule, now in the code

```
GetTmpEventFlagS(index, mask)      — NOT GetStoryFlagS
EVENTFCHK      requires the bit SET
EVENTFCHK_NO   requires the bit CLEAR
every condition in one script must hold
a PLACE is live if ANY of its alternative scripts can fire
unreadable flags mean UNKNOWN, and unknown never deletes a target
```

Scored against the two targets the player identified as dead:

| Event | Conditions | tmp bank says | Player |
|---|---|---|---|
| `ev[13]` a0.m3 dining hall | `EVENTFCHK(1,16)` holds, `EVENTFCHK_NO(20,4)` — `tmp[20]&4` is set | **cannot fire** | dead ✓ |
| `ev[4]` a0.m1 fireplace | `EVENTFCHK_NO(1,16)` — `tmp[1]=115`, bit 16 set | **cannot fire** | dead ✓ |
| `ev[23]` save point, `ev[0]`/`ev[1]` exits | no conditions at all | always live | live ✓ |

The **story** bank reads 0 at every index sampled: it explains `ev[13]` and gets
`ev[4]` wrong. `CanFire()` now calls `GetTmpEventFlagS`.

### The alternatives fix, which was a real bug of its own

Several scripts routinely cover one place — a building entrance carries two or
three, gated on different flags — and `SamePlace` folds them into one target,
keeping one script and discarding the rest. Judging that place by the kept
script alone would delete a working door whenever the kept one happened to be
the finished cutscene and the discarded one the everyday script. So the losers'
**conditions** now survive even though their scripts do not: `MapEvent.
Alternatives` holds one condition list per folded-in script, and
`Gsd1Flags.CanFireAny` reports the place dead only when **every** alternative is
dead. An alternative with no conditions makes the place permanently live.

### The half of the proof that is still missing, and the machine that will collect it

Every score above is on the **dead** side. No event carrying conditions has yet
been seen to **fire**. A rule with inverted polarity scores exactly the same on
those three and would then hide every live conditional event while keeping
precisely the dead ones — worse than the bug. **Zero violations is necessary,
not sufficient**, which is the same trap the collision work fell into four
times.

So the live side is now collected automatically, by the §1.9 move: find an
observation nothing can reinterpret. **`Event_c.eventWK` names the event the
game is RUNNING**, and an event that runs has fired. `Gsd1Path.WatchRunningEvent`
polls it five times a second (field reads only) and `Gsd1Flags.NoteRunningEvent`
matches it back to the table by the fields `EVENT_WK` actually stores —
raw `x`, `y`, `putno`, `image`, *not* the target position, because `Classify`
moves a region event's coordinates to its rectangle centre. Then it logs one of:

```
[Nav1] *** RULE CONFIRMS: ev[N] carries K condition(s), FIRED, and the tmp-bank rule said it could.
[Nav1] *** RULE CONTRADICTED: ev[N] FIRED but the tmp-bank rule called it dead.
```

**`eventWK` is never cleared** — it keeps the last event that ran, which is why
the save-point namer also checks the player is standing next to what it matched.
So only a *change* counts as a firing, and **the first value seen on a map is
adopted silently**, because it may be left over from the map before and scoring
a rule against a leftover is exactly how a false confirmation would be made.
`Event_c.eventNo` is logged beside the matched index every time: if the two
always agree, the running event can be read directly instead of matched by
position — but that is a claim to be scored, not assumed, and nothing depends on
it.

### What to do

`Gsd1Flags.DropDeadEvents` is `false`. Everything above **reports** and drops
nothing; the filter itself is one line in `Gsd1Path.AddEvents` and is a no-op
while the constant is false. Play until the log shows a **`RULE CONFIRMS`** line,
then flip the constant to `true`. If a **`RULE CONTRADICTED`** line appears
instead, the polarity or the bank is wrong and the rule goes back to the drawing
board — do not arm it.

`Event_c.GoEventForm(int, int)` is the game's own FORM evaluator and would settle
this in one call, but it is a "Go" method, not a query — it may *run* the event
rather than test it. Deliberately not called.

---

## 5. Place names — working, but NOT from the method that was hooked

### What the first live run said, and it overturns half of §5 as written

**The hook was installed and never fired.** v52 patched
`WindowManager.PlayVillageNameAnimationHeaderUI` successfully — the log says so
on line 21 — and across four maps and three town A name plates the postfix
ran **zero times**. Whatever the method's name says, the plate is not driven
through it. It is left hooked because it costs nothing and would settle the
question the moment it ever does fire, and `MethodEverFired` is now printed on
every arrival so "never called" can never again be mistaken for "never looked".

**What does happen, every single time, is the plate's own `TMP_Text`.** It is
named `sprt_1` and the game types the name into it one character at a time: `T`,
`To`, `Tow` … `town A`. That is the same evidence one step further
downstream — still the finished string the game put on screen — so the name is
taken from there instead, through the `TMP_Text` postfix that already existed.
One guard the method argument would not have needed: a value is accepted only
once it has **stopped changing for half a second**, or the recorded name would be
`Tow`.

**And `get_village_name()` works after all — sometimes.** The very first arrival
announced *"town A"* with an empty places file and no plate captured, which
leaves exactly one source: `Village_c.get_village_name()`. The old note that it
"returned empty in the the palace map" is also still true — arriving on `a10.m2`
announced *"An unnamed place"*. So it names **villages** and answers empty
**inside** them, which is consistent with the plate only playing for villages.
Together they cover the same ground and neither covers a room.

### What is settled

* A village names itself: town A, from two independent sources.
* A room inside one does not, anywhere in the game's data. It is the player's to
  give with `N`, and `Shift+M` reads it back. The player named `a0.m3`
  "Protagonist's Mansion" and `a10.m2` "Royal Palace" in the first session, which
  is the feature working as intended.
* Announcing after a load works: `arrived on a0.m3 (after a load or a scene)`.

### What is still being scored, automatically, on every arrival

The remaining candidates are read and logged in one line at **every** arrival —
not only the ones where a plate happened to play, which is why the palace
produced no scoring line at all last time:

```
[Nav1]   candidates: area=10 map=2 | GetAreaLabel='…' | GetVilLabel='…' |
         GetVillageNameID=… -> G1_SID_MSG='…' | GetFieldName='…' |
         get_village_name()='…' | prev_village_name='…'
```

`G1_string_id_h.G1_SID_MSG(int)` is the one that matters: a **static** text-id
resolver, where NAV-STATUS said only the instance-only
`G1_string_id_c.g1_Sid2Msg` existed. If it turns `GetVillageNameID`'s 66342 into
"town A" then every village is named from static data without being visited
— and, more usefully, `GetVilLabel(area, village)` may name the *rooms* that
neither the plate nor `get_village_name()` can. None of them is spoken until the
log shows one agreeing with a name the game itself displayed.

`prev_village_name` sits in the same line and is still not spoken: its name says
"previous" and nothing has established whether it means the village being entered
or the one just left.

**One more source turned up for free**, in the save-slot reader:

```
[Save] index=0 -> 'No.0, <date>, <time played>, <save kind>, LV 1, town A'
```

Every save slot carries a location name. Not needed while the two live sources
work, but it is a third independent way to score the others.

---

## 6. Next session, in order

1. **Read the questionnaire the log now writes by itself**, on two maps, and
   score the four `CANDIDATE` lines against the confirmed names in §3. The
   uniqueness column is the one to watch. If it separates them cleanly, adopt it
   and delete the "possible" prefix; if it does not, one confirmed identity for a
   digit-band NPC — the palace "object-like name", a village's "villager role" —
   decides it instead. §3.
2. **Watch for `RULE CONFIRMS` in the log**, then set
   `Gsd1Flags.DropDeadEvents = true` and dead events start disappearing for
   real. If `RULE CONTRADICTED` appears instead, the rule is wrong — do not arm
   it. §4.
3. **Score the static place-name candidates** against the banner lines already
   in the log. If `G1_SID_MSG(GetVillageNameID(area, map))` returns the same
   string the banner displayed, promote it and every village is named without
   being visited. §5.
4. **The menus below the top level** — the item command window, equipment slots,
   the Stats page, shops and storage. `UIMainItemMenu` alone declares six cursor
   methods, each paired with a named window field, so it is a table to fill in
   rather than a puzzle to solve. §12.
5. Verify text entry — `Enter` may double-register; untested for several
   sessions, and `N` now uses the same path.
6. Interactable object names: `Event_c` carries `eventWK`, `backLogMessageId`
   and `speaker_name_id`; tapping an object runs exactly one event and shows
   exactly one message, so the pairing is unambiguous the way the save point's
   was. `F8` already dumps all three, and the new event watcher already logs
   which event ran.
7. User configuration — a BepInEx `Config.Bind` .ini first, audio-only in-game
   menu later.
8. Suikoden 2 collision — `F10` to settle the chip-attribute hypothesis. If it
   is uniformly zero, say so plainly and stop.

---

## 7. Where am I — implemented

* **`Shift+M` — "where am I", always available.** Reads the player's own name for
  this map, then the village: *"character A's mansion, in town A"*. With only the
  village known it says *"town A. This part has no name yet; press N to name
  it."* With nothing known, *"This place has no name yet; press N to name it."*
* **`N` — name this area, by ear**, through the existing audio text entry.
  Separate from `M` because they are different things: `M` records a **tile** to
  walk back to, `N` names the **map**, which is what `Shift+M` reads out.
  Sub-areas are already distinguished, because `MapKey` changes for every room —
  a shop and a floor of a mansion each get their own name.
* **After a save is loaded, the location is announced** without being asked for.
* Stored in `BepInEx/plugins/suikoden1_places.txt`, `map key | player or game |
  spoken name`, hand-editable and shareable like the other three files. A
  `player` row always beats a `game` row: they answer different questions.
* Internal map keys are still never spoken (NAV-STATUS §1.5).

**How a load is told apart from a doorway.** Both blank the map key, and the only
thing separating them is how long for: a doorway blanks it for a handful of
frames, while loading a save, a battle or a full-screen scene blanks it for
seconds. Three seconds sits well clear of both. The flag is **latched** rather
than recomputed, because the gap is only visible on the first readable frame and
an arrival is not declared until the key has held for another 0.6 s — by which
time the frame's own update has closed the gap. Walking room to room inside one
village stays silent; entering a differently named village speaks. A missed
announcement costs one `Shift+M`; a spurious one talks over the game at every
door.

---

## 8. Keys

| Key | Action |
|---|---|
| `J` / `L` | previous / next target |
| `K` | read current target |
| `Shift+J` / `Shift+L` | cycle category (All / People / Events / Doors / Places / Objects / Scenery) |
| `P` | pathfind, speaks steps |
| `M` | mark current tile |
| `Shift+M` | **say where the player is** — room, then village |
| `N` | **name this area** (the map itself, not a tile) |
| `Shift+K` | name the selected target |
| `F5` | name probe — `GetCharacterName` sweep, the place-name candidate line, and the **NPC identity questionnaire** (logs only) |
| `F6` | whole-map collision + walked cells → `suikoden1_collision_dump.txt` |
| `F7` | every on-screen label with its hierarchy path |
| `F8` | event table, doors, objects, people, `Event_c` state |
| `F9` / `F10` | Suikoden 2 probes |
| `F11` | map / collision / NPC dump |
| `F12` | collision window around the player |

---

## 9. Rules paid for in bugs — do not relearn these

* **Never Harmony-patch a hot-path method taking a by-ref struct parameter.**
  A postfix using `object[] __args` on `EVENTCON.EventMapAtariCheckFunc1(int,
  int, ref RECT)` corrupted live collision in-game. Harmony round-trips by-ref
  structs through boxing even when the postfix never writes to them.
* **Never patch a base virtual AND its overrides.** Three trampolines in one
  vtable chain crashed the game natively at startup. A loop over class names is
  a sweep.
* **A hand-patched class must NOT carry a `[HarmonyPatch]` attribute** — it makes
  `PatchAll` abort `Plugin.Load()` and disables the entire mod.
* **Calling a value-parameter-only game method is a different, acceptable risk.**
  Verified safe in play: `GNDAtariCheck`, `BLDAtariCheck`, `GetCharacterName`,
  `GetStoryFlagS`. Keep such calls one-shot (a keypress or a map load), never
  per frame.
* **Do not auto-attribute dialogue speakers to nearby NPCs.** A party follower
  walking beside the player permanently mislabelled the character they stood
  next to. **A wrong name is worse than no name.**
* **Type names collide between the two games.** `GAME_WORK` exists in both.
  Use `Gsd.FindIn(dll, name)` for anything belonging to one game's field code.
* **`CountOf` must check `Length` as well as `Count`** — Il2Cpp arrays expose
  `Length`, lists expose `Count`.
* **Il2Cpp lists are not `System.Collections.IEnumerable`** — index them.
* **This game runs the new Input System exclusively.** Use `Keyboard.current`.
* **A constant probe result means "wrong parameters", not "no data".**
* **Log the zero.** A measurement that found none and a silence that never
  looked must never look the same in a log. Four conclusions were once wrong
  because every reader logged its count only `if (n > 0)`.

---

## 10. Build and test loop

```powershell
dotnet build
```

The post-build step copies the dll into `BepInEx\plugins`. `TitleProbe.Version`
prints at startup — **confirm it before trusting a test result**; a stale build
has masked several sessions.

```powershell
Select-String "\[Nav1\]|\[G1Dump\]|\[Save\]|\[Gsd\]" BepInEx\LogOutput.log | Out-File -Encoding utf8 out.txt
```

**Environment.** `BepInEx\_decompiled\GSD1.cs` is a valid 23 MB decompile and is
ASCII. Parts of the other decompiled files are UTF-16 — pipe through
`tr -d '\000'` before grepping or matches vanish silently. The interop decompiles
carry **signatures only, never method bodies**, so no question about what a game
method *does* can be answered from them — only measured in play.

Game path: `E:\SteamLibrary\steamapps\common\Suikoden I and II HD Remaster`.

## 11. Files

| File | Purpose |
|---|---|
| `Gsd1Path.cs` | A\*, targets, categories, collision — the main Suikoden 1 file |
| `Gsd1Nav.cs` | `Village_c` hook, position logging, walked-cell recording, F6/F11/F12 |
| `Gsd1Events.cs` | event-table decoder — exits, triggers, objects, story flags |
| `Gsd1Probe.cs` | read-only F8 map dump, F7 UI-label dump, F5 name probe |
| `Gsd1Names.cs` | NPC name store, `MapKey`, and `Gsd1GameNames` (the game's own names) |
| `Gsd1Place.cs` | place names — the name-plate capture, the places file, `Shift+M` and `N` |
| `MainMenuReader.cs` | the in-game pause menu — command column, party rows, list windows |
| `Gsd1Exits.cs` | learned transitions |
| `Gsd1Marks.cs` | player-marked places |
| `Gsd1TextInput.cs` | audio text entry + movement-block prefix |
| `TitleProbe.cs` | title, menus, battle, and the `Gsd` type/method lookup helpers |
| `NavProbe.cs` | Suikoden 2 probes (F9/F10) — diagnostics only |

---

## 12. The in-game menus — the top level is readable, and why one hook covers it

Until v53 the pause menu read only the help line at the bottom of the screen —
one sentence describing what the highlighted command does — for a command
whose **name** was never spoken. STATUS-2 §4 says why: menu labels are written
**once, when the screen is built**, so the text hook sees seven labels in a row at
build time and nothing at all when the highlight moves. The missing piece is
always the same one, the cursor method, and the STATUS-2 §1 method finds it by
mapping classes to the methods they **declare**.

What that map turned up, all in `ShareUI.dll`, all value parameters only:

```
UIMainMenuCommon.SetCursor(int)             the left command column
UIMainMenuPartyList.SetCursor(int, int)     the party rows
UIWindowList.SetSelectCell(int) / SetSelectOneCell(int)
UIWindowListTop.SetSelectCell(int) / SetSelectOneCell(int)
```

and the shape behind them: **every list in this game is a `UIWindowList`** (or
its sibling `UIWindowListTop` — both derive from `UIWindowBasis`, neither from
the other, so this is not a base-and-overrides sweep) holding a
`List<UIWindowListCell>`, and a cell owns its own label objects. One pair of
hooks therefore reads every list-shaped window, the same economy that made
`UIBattleMain.MoveCursor` cover all of battle. The label is read from the cell's
own visible text objects and joined — an item row comes out as its name then its
quantity, a party row as name, level and HP — rather than named field by field, because which of a cell's text objects are
filled depends on the window.

**It is gated on the pause menu being open, and that gate is the important part.**
Battle uses `UIWindowList` too: `UIBattleMain.m_windowListDict` is a
`Dictionary<WindowType, UIWindowList>`, and battle already has a working reader.
Speaking from both would say every battle command twice and regress a finished
milestone. So `UIMainMenu.Open()` / `Close()` — zero arguments, nothing to
marshal, the safest patch there is — maintain a flag, and the list hooks speak
only while a pause menu is up. **Shops and storage are `UIWindowList`s too and
are deliberately left silent**: they need their own pass, not a hook that fires
everywhere and is right some of the time.

`SetSelectCell` and `SetSelectOneCell` are **both** hooked because which one
moves the cursor is not established — "select one cell" reads like a cursor and
"select cell" like the formation screen's multi-select, but that is an argument
about method names, not a measurement. Both are hooked, both log the class and
method that fired, and speech goes through `Narration.LabelWithDescription`,
which folds everything arriving inside 0.2 s into one utterance. So if both fire
for one keypress it is still spoken once, and the log says which one to keep.

`UIMainMenuPartyList.SetCursor(int, int)` speaks the row at the **first**
argument and logs the text at both, because which argument is the row has not
been established either. A wrong row here is a misread menu, not a name written
to a shared file, so it is worth speaking while the log settles it.

**Still not read, and each needs its own pass:** the sub-menus below the top
level (the item command window, equipment slots, the Stats page's own cursor),
shops, and storage. `UIMainItemMenu` alone declares six cursor methods
(`SetItemListSelectCursor`, `SetMemberSelectCursor`, `SetItemCommandSelectCursor`,
`SetToolEquipSelectCursor`, `SetEquipItemSelectCursor`,
`SetEquipCommandSelectCursor`), each paired with a named window field, so they
are a table to fill in rather than a puzzle to solve.

### The confirmation box

The box warning that quitting to the title screen loses unsaved progress read out
as a bare "No" and then a bare "Yes" — the thing being agreed to was never said.
The message had been in the log the whole time, in the labels list of every one
of these lines:

```
[Dialog] system button Cancel -> 'No' (labels: <the warning sentence>|Yes|No)
```

`SysButtonPost` fires once per BUTTON, not once per dialog, so the question is
now spoken once per question — remembered for two seconds so the second button's
call does not repeat it — and the button captions are **queued** behind it rather
than interrupting it. Which of Yes/No is highlighted is still unknown outside the
save screen (STATUS-2 §3 gap 2), so both are still read; that gap is unchanged.
