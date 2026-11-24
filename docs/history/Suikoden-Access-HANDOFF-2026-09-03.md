> **Historical document, superseded.** Kept for the reasoning, not as
> instructions. For the current state, start with
> [`HANDOVER.md`](../../HANDOVER.md) in the repository root. Version numbers,
> next steps and file locations below are out of date.

> **SUPERSEDED 2026-09-05 by `Suikoden-Access-HANDOFF-2026-09-05.md`.** Kept for
> the record. Its collision answer was right about the coordinate space and wrong
> about `level` — it passed the map's `layer_no`, which held on the one map it
> was validated against and left every house in town A walkable.

# Suikoden Access — Session Handoff, 2026-09-03

**Supersedes `Suikoden-Access-HANDOFF-2026-08-31.md` entirely.** Read
`Suikoden-Access-NAV-STATUS.md` §1.1 and §1.9 first — §1.1 is the answer, §1.9 is
the method, and §1.9 is the more important of the two.

Suikoden 1 only. Suikoden 2 remains on hold.

---

## 1. The blocker is gone: both collision oracles are settled

```
Village_c.GNDAtariCheck(int x, int y, bool)                       -> byte
Village_c.BLDAtariCheck(int x, int y, int level, ref int, bool)   -> ushort

blocked(x, y) = GNDAtariCheck(x, y, false) != 0
             || (BLDAtariCheck(x, y, layer_no, ref no, false) & 0x0f) != 0
```

**Both take CELL coordinates — `HUMAN.map_x`/`map_y`, unshifted.** Nothing is
doubled and nothing is halved anywhere in the pathfinder. The ground byte is a
4-bit mask of one cell's four quarters and any bit set means solid. The building
`ushort` packs two 4-bit masks and only the **low** nibble decides.

Bounds: cells run `0..2*MAP.width-1`. Out of range must read SOLID — the oracle
answers an off-map coordinate with 0, which is "fully open".

### What was wrong, three times over

| When | The rule in the code | Symptom |
|---|---|---|
| before 2026-08-29 | doubled the input, then halved it again — right cell, but the quadrant bit always came out as bit 0 | only the NW quarter tested; **249 solid cells read as open** |
| 2026-08-29 → 09-02 | `GNDAtariCheck(x>>1, y>>1)` | queried a different part of the map |
| 2026-09-02 | building layer moved to chip space, high nibble | blocked 903 cells, 109 of them cells the player had stood on |

Each was argued from the data and each argument was unfalsifiable. §1.9 has the
general lesson; it is the thing worth carrying forward.

### Verified

Offline replay of the mod's own A\* over the F6 grid, from the positions the
player actually pressed `P` at:

* from (8,33), the old build said **"east 6, south 7"** — straight through the
  ground wall at x=11..17. The new rule says **"south 3, east 6"**, down the open
  corridor at x=8..10 and along the floor at y=36.
* from (19,33), a 22-cell detour west, north, west, south, east around the same
  wall, where the old build said "south 3, west 5" through it.
* every route ends at (14,36), the cell in front of the save desk, which is
  where the player stands to save.

**Not yet confirmed in play.** That is the one thing outstanding.

---

## 2. Also done this session

* **The main menu names the game.** `[Gsd] game detect -> Suikoden One ::
  Village_c null; EVENTCON null; ChapterManager.GR1Instance LIVE; scenes=[GSD1]`.
  `Village_c`/`EVENTCON` do not exist yet at the title menu; each game's boot
  chapter does, and exposes it as a static property on a **per-game type**:
  `ChapterManager.GR1Instance` (GSD1.dll) and `GRChapterManager.GRInstance`
  (GSD2.dll). GSD2 declares no `ChapterManager` and GSD1 no `GRChapterManager`,
  so there is nothing to disambiguate. The Unity scene is also literally named
  `GSD1`, confirmed, and is kept as the last fallback. Every candidate is logged
  whether it fires or not.
* **"Suikoden One", spelled out**, at the main menu and the launcher header —
  the synthesiser reads a lone roman `I` as the letter. `Suikoden II` is read
  correctly as "two" and is left alone.
* **`F6`** writes `BepInEx\plugins\suikoden1_collision_dump.txt`: the whole map's
  ground and building grids at every level, the `tatemono_no` reported at each
  coordinate, all 40 `TATEMONO`, all 42 `MAP.tatemono_data`, and every cell the
  player has stood on since the map loaded.
* **Walked cells are recorded every frame** (`Gsd1Nav.NoteVisited`), keyed by
  map, cleared on map change, capped at 20k. This is the ground truth that
  settled the collision question and it is worth keeping switched on.
* **`F12` no longer misleads.** Its ground window used to halve its input and
  pick a quadrant bit out of the answer, drawing a different part of the map from
  the one the router was searching — a diagnostic that helped hide the bug it
  existed to find. It now draws exactly what `Blocked()` computes.

---

## 3. Next session, in order

1. **Confirm the routes in play.** Villa bedroom, `P` on the save point from
   several spots — especially from the far side of the room, where the route
   should now take a long way round rather than a straight line.
2. **Name exits and objects** with `Shift+K` on the maps you use. They key on the
   event's index in static map data, so the file is shareable.
3. **The party-join popup** is still unread. Press `F7` while
   one is on screen.
4. **Verify text entry** — Enter may double-register, untested for several
   sessions.
5. **Extend the `eventCom` width table** only from ground-truth pairings
   (NAV-STATUS §1.7 "Still open" 1 — the length-based search is settled dead).
6. **User configuration** — a BepInEx `Config.Bind` .ini first, an audio-only
   in-game menu later.
7. **Suikoden 2 collision** — `F10` to settle the chip-attribute hypothesis. If
   it is uniformly zero, that route is dead; say so and stop.

### Open, and nothing depends on it

The `ref tatemono_no` read-back names entries whose stated `map_x`/`map_y` sit
nowhere near the queried coordinate under any reading tried. It may index
`MAP.tatemono_data` (42) rather than `Village_c.map_tatemono` (40). `F6` dumps
both. The mask alone routes, so this is curiosity, not a blocker.

---

## 4. Keys

| Key | Action |
|---|---|
| `J` / `L` | previous / next target |
| `K` | read current target |
| `Shift+J` / `Shift+L` | cycle category (All / People / Events / Doors / Places / Objects / Scenery) |
| `P` | pathfind, speaks steps |
| `M` | mark current tile |
| `Shift+K` | name the selected target |
| `F6` | **whole-map collision + walked cells, to a file** |
| `F12` | collision window around the player |
| `F11` | map / collision / NPC dump |
| `F8` | event table, doors, objects, people, service constants |
| `F7` | every on-screen label with its hierarchy path |
| `F9` / `F10` | Suikoden 2 probes |

## 5. Build and test loop

```powershell
dotnet build
```

```powershell
Select-String "\[Nav1\]|\[G1Dump\]|\[Save\]|\[Gsd\]" BepInEx\LogOutput.log | Out-File -Encoding utf8 out.txt
```

`TitleProbe.Version` prints at startup — currently **v47**. Confirm it before
trusting a test result; a stale build has masked several sessions.

**Environment:** `BepInEx\_decompiled\GSD1.cs` is a valid 23 MB decompile and is
ASCII. Parts of the other decompiled files are UTF-16 — pipe through
`tr -d '\000'` before grepping or matches vanish silently. The interop decompiles
carry signatures only, never method bodies, so no question about what a game
method *does* can be answered from them.

**Log the zero.** A measurement that found none and a silence that never looked
must never look the same in a log.

**Score, do not argue.** When a probe's meaning is in question, find an
observation the game makes for you that no interpretation can reinterpret, and
score every candidate against it. NAV-STATUS §1.9.
