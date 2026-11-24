> **Historical document, superseded.** Kept for the reasoning, not as
> instructions. For the current state, start with
> [`HANDOVER.md`](../../HANDOVER.md) in the repository root. Version numbers,
> next steps and file locations below are out of date.

> **SUPERSEDED 2026-09-03 by `Suikoden-Access-HANDOFF-2026-09-03.md`.** Kept for
> the record. §2.1 below is wrong in a way worth knowing about: it proposed that
> `BLDAtariCheck` takes chip coordinates, which was half right (the space was
> wrong, but so was the return format, and the ground layer was wrong too), and
> the reasoning that produced it is the exact failure mode NAV-STATUS §1.9 now
> guards against.

# Suikoden Access — Session Handoff, 2026-08-31

**Read `Suikoden-Access-NAV-STATUS.md` first** — §1.7 and §1.8 were rewritten this
session and are the reference. This file is the delta: what changed, what is
confirmed in play, what is still broken, and what to measure next.

Suikoden 1 only. Suikoden 2 remains on hold.

---

## 1. Confirmed working in play

Verified by the player this session, not inferred.

| Feature | Evidence |
|---|---|
| **Save confirmation dialogs** | The overwrite prompt and "Save successful." both read. One option is announced, not both. |
| **Save point auto-naming** | `[Nav1] ev[23] Object at (14,35) tap EVENTSHOP type=7` and the target now reads **"save point"**, not "object 1". No interaction needed. |
| **Doorways are building entrances** | "doorway 1" is the villa's main entrance. The shape-and-position inference was right. |
| **Person tracking is stable** | Cycling people in the city plaza no longer jumps between duplicates. |
| **Exits from the event table** | `ev[0] Exit at (19,35) -> map 1 at (17,36)`, and walking it lands the player exactly at (17,36). |
| **Real map ids** | `mapKey=a0.m3` from `get_area_idx()` / `get_map_idx()`. |

---

## 2. Still broken

### 2.1 Interior routes still cross walls — TOP PRIORITY

**The building-collision layer is live and is NOT fixing it.** That is new
information and it narrows the problem sharply.

From the last run, in the villa bedroom:

```
[Nav1] resolved BLDAtariCheck — building collision is ON.
[Nav1] map_x=18 map_y=33 ... mapKey=a0.m3
[Nav1] path to save point: 8 sub-cells -> south 3, west 4
       [blocked by ground 91 / buildings 98, buildings on, level 1]
```

What this tells us:

* `BLDAtariCheck` resolved and was called. **98 cells were blocked by the
  building layer** during the search, so it is returning non-trivial data.
* The self-check never fired — the player's own cell reads walkable under the
  combined test — so `level = 1` is not obviously wrong.
* The route runs (18,33) → south 3 → (18,36) → west 4 → **(14,36)**, which is the
  cell directly below the save desk at (14,35). That is the correct goal: it is
  exactly where the player stood to save. So the **target and the neighbour
  fallback are right**; it is the path between that is wrong.

**The leading hypothesis: `BLDAtariCheck` takes CHIP coordinates, not cell
coordinates.** It is currently called with cell coordinates. If it wants chips
(as `GNDAtariCheck` does), then the 98 blocked cells are the wrong 98 —
scattered, plausible-looking, misaligned by a factor of two. That would produce
exactly what is observed: collision that is clearly *present* and still lets the
router walk through a bedroom wall.

Supporting doubt: `TATEMONO` positions on this map ran 2..44 (cell-shaped, since
the map is 24×24 chips = 48×48 cells) but `TATEMONO` **sizes** are in pixels
(32×40, 64×48, 176×64). Position and size are not in the same unit, so
"positions are cells" is an inference, not a measurement.

**This is measurable and was not measured.** `F12` was extended to sweep
`BLDAtariCheck` over levels 0–2 and print its window beside the ground one.
`grep -c "F12:"` on the last log returns **0** — it was never pressed.

### 2.2 The main menu says the wrong thing

```
[TitleMenu] menu shown (game: unknown)
```

`Gsd.RunningGameName()` tests whether `Village_c` or `EVENTCON` has been
instantiated. **The per-game title menu appears before either exists**, so it
returns null and the old wording plays. The mechanism is sound, the signal is
too late. A different source is needed — see §4.2.

---

## 3. What changed this session

All deployed. `dotnet build` copies into `BepInEx\plugins` automatically.

### Fixed and confirmed
* **Save confirmation** — read from **fields** (`isConfirmSelect`,
  `saveResultMessage`, `saveResultButtonText`) off the `Exec()` postfix that was
  already installed. `isConfirmSelect` 0 = decision, 1 = cancel, proven by a real
  save succeeding on 0. `UISystemDialog`'s reader fires once per BUTTON on every
  restyle, so it announced both options; the save screen now owns the dialog
  while it is up (a 0.5 s lease) and that reader logs only.
* **Service points name themselves.** `G1_event_h` reads
  `USER_MENU=1 SHOP_OMISE=2 SHOP_KAJIYA=3 SHOP_MONSHO=4 SHOP_KANTEI=5
  SHOP_YADOYA=6 SHOP_SAVE=7 SHOP_SUTERU=8 SHOP_MINIMAP=9 SHOP_HANYOU=255
  SHOP_EFUNC=128`. The diary's script ends `43 07 ff` = `EVENTSHOP SHOP_SAVE`.
  Only the script **tail** is scanned; 255 and 128 are excluded because they are
  generic slots and 255 is also the commonest filler byte.
* **Person events are never targets.** Their position lives on the matching
  `HUMAN` in `map_human`; listing both put every townsperson in twice (29 live +
  23 frozen spawn points on one map), which is what made a target three steps
  east become one twenty steps away.
* **Events sharing a trigger rectangle collapse to one target.** Building
  entrances carry two or three flag-gated scripts for the same place. Town map:
  **71 raw events → 24 targets**.
* **Selection follows identity, not list position.** `_targets` is rebuilt from
  live state on every keypress and its membership changes. Any new target source
  **must set a stable `Id`**.
* **Learned exits deduplicated** against event exits within 3 cells, and renamed
  `learned exit N` so the two sources are never confused.
* **Multi-cell goals.** A doorway trigger is a rectangle; every cell is tried
  nearest-first, each with its eight neighbours.

### Fixed, unverified
* **Building collision** (§2.1) — live, not yet correct.
* **Coordinate scale.** `map_x`/`map_y` are cells; `GNDAtariCheck` and
  `MAP.width/height` are chips (a chip is 2×2 cells, which is why collision
  answers a 2×2 quadrant mask). The code used to double them, querying off-map
  chips, which `GNDAtariCheck` answers with 0 = open. Out-of-range now reads
  solid. **Real, necessary, and not sufficient** — routes still cross walls.
* **Exit learning.** Was completely dead: `Observe` cleared its "where I came
  from" state on the null map key that every transition passes through while
  loading, so the map-change branch could never fire.

### Ruled out — do not reopen
* **`eventCom` operand widths cannot be recovered from script lengths.** A
  brute-force search over all 22 complete scripts found every width 0–3 feasible
  for every unknown opcode. A script's length constrains the **sum** of its
  operand counts; with ~8 opcodes per script and one equation each the system is
  underdetermined by construction. More maps add unknowns as fast as equations.
  Only ground-truth pairing settles it, as it did for `VILLAGEOUT`.
* **`TATEMONO` carries no interactable flag.** 40 entries, 40 distinct ids, each
  once — `id` is the array index. `level` was 1 everywhere.
* **`HITO_DATA`** has one field, `short fp_no`.
* **`map_event_human`** genuinely reads 0; story NPCs live in `vil_ekp`.

### Two hooking rules paid for in crashes this session
* **Never patch a base virtual AND its overrides.** Hooking `Confirm` on
  `UISaveLoad1`, `UISaveLoad2` and `UISaveLoadBase` — three trampolines in one
  vtable chain — crashed the game natively at startup. `DeclaredOnly` does not
  protect against this. A loop over three class names is a sweep.
* **A hand-patched class must NOT carry `[HarmonyPatch]`.** A bare attribute on
  `Gsd1MovementBlock` made `PatchAll` abort `Plugin.Load()` and disabled the
  entire mod. `Plugin.Load()` now applies patch classes one at a time.

---

## 4. Next session, in order

### 4.1 Settle `BLDAtariCheck`'s coordinate space — everything else waits

1. Stand in the villa bedroom near the save desk. Press **F12**. It prints the
   ground window and then `bld row` windows for levels 0, 1 and 2.
2. Compare the building window against the room's real shape. A wall three cells
   west of the player should show as non-zero three cells west, not six.
3. If it is misaligned by a factor of two, call it with `x >> 1, y >> 1` and
   re-test.
4. **Cross-check offline, no play session needed:** rasterise `map_tatemono`'s
   rectangles from an `F8` dump and compare with what `BLDAtariCheck` reports for
   the same cells. Agreement proves the space. `F8` already dumps every
   `TATEMONO` with `pos`, `size`, `level` and `pri`.
5. Also unconfirmed: whether a non-zero return always means solid, and what
   `level` ranges over. `TATEMONO.level` read 1 on every entry of the one map
   dumped — not enough to know.

If buildings turn out to be aligned and correct, the remaining suspects are
`get_komono_atari(x, y)` (small props, 0 on this map) and the event/treasure
layers `player_atari_check` also collects (`evt_atari`, `tak_atari`).

### 4.2 Name the game at the main menu

`Village_c`/`EVENTCON` do not exist yet when the title menu appears. Look for a
signal that is set earlier — the launcher's selection, `ChapterManager` /
`TitleChapter` state, or `GlobalWork`. `TitleChapter` was found in **GSD2.dll**
at startup, which suggests a per-game chapter type may itself be the signal.

### 4.3 Smaller, after the above
* The party-join popup is still unread. Press **F7** while one is
  on screen; that names the components it is built from.
* Verify text entry — Enter may double-register (untested for several sessions).
* Extend the `eventCom` width table only from ground-truth pairings.
* Suikoden 2 collision — press **F10** to settle the chip-attribute hypothesis.

---

## 5. Keys

| Key | Action |
|---|---|
| `J` / `L` | previous / next target |
| `K` | read current target |
| `Shift+J` / `Shift+L` | cycle category (All / People / Events / Doors / Places / Objects / Scenery) |
| `P` | pathfind, speaks steps |
| `M` | mark current tile |
| `Shift+K` | name the selected target |
| **`F12`** | **ground + building collision windows — the measurement that is needed** |
| `F11` | map / collision / NPC dump |
| `F8` | event table, doors, objects, people, service constants |
| `F7` | every on-screen label with its hierarchy path |
| `F9` / `F10` | Suikoden 2 probes |

## 6. Build and test loop

```powershell
dotnet build
```

```powershell
Select-String "\[Nav1\]|\[G1Dump\]|\[Save\]" BepInEx\LogOutput.log | Out-File -Encoding utf8 out.txt
```

**Environment:** `BepInEx\_decompiled\GSD1.cs` is a valid 23 MB decompile. Parts
of the other decompiled files are UTF-16 — pipe through `tr -d '\000'` before
grepping or matches vanish silently. `GSD1.cs` itself is ASCII.

**Log the zero.** Four conclusions in the old status doc were wrong because every
reader logged its count only `if (n > 0)`, so a list reading `0` and a list
reading `null` produced identical output — nothing — and both were written down
as "empty on every map". A measurement that found none and a silence that never
looked must never look the same in a log.
