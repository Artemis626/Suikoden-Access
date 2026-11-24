# Suikoden Access

**A screen-reader accessibility mod for *Suikoden I & II HD Remaster* (PC),
for blind and low-vision players.** It speaks the game through NVDA, or through
Windows' built-in SAPI voice when NVDA is not running, and adds audio
navigation: a list of everything worth walking to on the current map, and real
pathfinding to any of it.

> [!IMPORTANT]
> **This project is shelved and unmaintained (since November 2026, at build v72).**
> The original author is not planning to continue it. The code is MIT-licensed:
> fork it, finish it, and ship it. **[`HANDOVER.md`](HANDOVER.md) is the
> place to start** if you are a programmer or an AI coding agent picking it up.

> This mod contains **no game content**: no assets, no script text, no code from
> the game. It reads the running game in memory and speaks what is already on
> screen. You need your own purchased copy.

---

## At a glance

| Area | Suikoden I | Suikoden II |
|---|---|---|
| Dialogue, with speaker name | ✅ works | ✅ works |
| Choices in conversations | ✅ | ✅ |
| Title screen, launcher, settings | ✅ | ✅ |
| Save / load screens | ✅ | ✅ |
| Character-name entry | ✅ | ✅ |
| Battle menus and targeting | ❔ untested | ✅ |
| Pause menu | ⚠️ top level only | ⚠️ top level only |
| Sub-menus, shops, storage | ❌ not read | ❌ not read |
| Field target list (people, doors, objects) | ✅ full | ⚠️ people only |
| Pathfinding to a target | ✅ | ✅ people only |
| Exits with destinations | ✅ | ❌ |
| Names the game supplies | ✅ characters and places | ❌ player-typed names only |
| "Where am I" | ✅ | ❌ |

✅ works in play · ⚠️ partial · ❔ built but never tested in play · ❌ not built

Everything from v69 to v72 was built and compiles but was **never tested in
play**. [`HANDOVER.md`](HANDOVER.md#1-first-session-back-check-these) lists the
checks for the first session.

---

## What works, in detail

### Both games: the shared UI layer

The two games ship in one executable and share their menu and battle code, so
the same readers run in both. Two exceptions to "works in both": battle was
only ever tested in Suikoden II, and the pause menu is only read at its top
level.

| Feature | What is spoken |
|---|---|
| **Dialogue** | every line, with the speaker's name |
| **Choices** | the prompt and each option as you move |
| **Title and launcher** | game selection, Gallery, Language, both Settings pages (name, description and value as one sentence; sliders as "7 of 10") |
| **Save / load** | slot contents, overwrite confirmations, the highlighted option |
| **Name entry** | the letter grid, capitals, and the confirmation |
| **Battle** (tested in Suikoden II only; untested in Suikoden I) | battle start, the command row and its sub-menus, quantities, enemy targeting with duplicates numbered, item descriptions, whose turn it is, the end-of-battle box |
| **Pause menu** (partial) | the command column and the party rows; nothing below the top level |
| **Loading** | "Now loading", once, when a save starts to load |

### Suikoden I: field navigation (complete)

| Feature | How it works |
|---|---|
| **Target list** | people, doors, events, objects, places and scenery on the current map, in a stable order, filterable by category |
| **A\* pathfinding** | over the game's own collision checks, indoors and outdoors. Routes go *around* buildings and furniture and are spoken as walking steps ("east 4, north 3") |
| **Exits with destinations** | read from the map's own event scripts, so a doorway can be targeted the first time you enter a room |
| **Self-naming landmarks** | save points, inns, shops, blacksmiths and rune shops are identified from static map data |
| **Character names** | from the game's own name table |
| **Place names** | from the location banner the game draws when you enter a town |
| **Where am I** | on demand, and automatically after loading a save |
| **Your own labels** | name any person, tile or area by ear; saved to plain text you can edit or share |

### Suikoden II: field navigation (early)

| Feature | How it works |
|---|---|
| **Target list** | the people on screen, nearest first ("person 1", "person 2", or the name you gave them) |
| **A\* pathfinding** | 8-way, over the game's own terrain check and the collision masks of buildings and props. The route ends beside the person and says which way to face |
| **Your own labels** | name a person by ear with `Shift+K`; saved to plain text |

Not built for Suikoden II: doors and exits, objects, place names, "where am I",
and names supplied by the game. A research plan for game-supplied names exists;
see [`HANDOVER.md`](HANDOVER.md#3-what-to-build-next).

### Known gaps in both games

- Menus below the pause menu's top level, shops and storage are not read.
- In confirmation boxes outside the save screen, which of Yes / No is
  highlighted is not announced.
- Damage is announced without naming who was hit.
- There is no settings file yet; every option is a constant in the code.

---

## Keys

The navigation keys are the same in both games. They only act on a field map.

| Key | Action |
|---|---|
| `J` / `L` | previous / next target |
| `K` | read the current target: name, direction, distance |
| `Shift+J` / `Shift+L` | previous / next target category |
| `P` | find a path to the selected target and speak it as steps |
| `Shift+K` | name the selected target (type the name, `Enter` to save, `Escape` to cancel) |
| `M` | mark the tile you are standing on as a landmark (Suikoden I) |
| `Shift+M` | say where you are (Suikoden I) |
| `N` | name the area you are in (Suikoden I) |

Developer and diagnostic keys write dumps next to the plugin or into the log.
They are listed in [`ARCHITECTURE.md`](ARCHITECTURE.md#diagnostic-keys).
Players can ignore them.

---

## Installing (players)

There is no release build. You need to build the DLL yourself (see below), or
get it from someone who has.

1. Install **BepInEx 6 for IL2CPP** (a "bleeding edge" build, since BepInEx 5
   does not support IL2CPP games) into the game folder, the folder that holds
   the game's `.exe`. Launch the game once and quit, so BepInEx generates its
   files in `BepInEx\interop\`.
2. For NVDA speech: keep **NVDA** running, and put a **64-bit**
   `nvdaControllerClient.dll` in `BepInEx\plugins\`. It comes with the NVDA
   Controller Client package from NV Access. The game is 64-bit, so the 32-bit
   copy will not load. Without it the mod uses Windows SAPI instead and still
   talks.
3. Put `SuikodenAccess.dll` (and `System.Speech.dll`, from the build output) in
   `BepInEx\plugins\`.
4. Start the game. You should hear **"Suikoden Access loaded"**. That means the
   plugin loaded and speech works.

If you hear nothing, open `BepInEx\LogOutput.log` and look for lines that
start with `[TitleProbe]` or contain `FAILED`.

## Building (developers)

Requirements: Windows, the **.NET 6 SDK** (or newer, targeting `net6.0`), and
the game with BepInEx installed and run once, as above.

1. Open `SuikodenAccess.csproj` and change the one `<GamePath>` line to your
   own game folder.
2. Build:

   ```powershell
   dotnet build
   ```

The build references BepInEx and the game's generated interop assemblies in
place and never copies them. A post-build step copies `SuikodenAccess.dll` into
`<GamePath>\BepInEx\plugins\`, so you can launch the game straight away. The
first line to check in the log is the version, `[TitleProbe] heartbeat installed (v72)`.

---

## Your data files

As you play, a few plain-text files appear next to the plugin in
`BepInEx\plugins\`:

| File | Contents |
|---|---|
| `suikoden1_npc_names.txt` | names you typed for people (Suikoden I) |
| `suikoden1_exits.txt` | transitions you have walked through (Suikoden I) |
| `suikoden1_marks.txt` | tiles you marked with `M` (Suikoden I) |
| `suikoden1_places.txt` | names for whole areas (Suikoden I) |
| `suikoden2_npc_names.txt` | names you typed for people (Suikoden II) |

These are yours. They are not tied to a save game, and they are meant to be
**edited and shared**: giving yours to another player gives them your labels
at once. Nothing is written to them unless you type it. Names that the game
supplies are worked out again each session and never saved, so a wrong one
stays a bug to fix instead of ending up in a file people pass around.

The diagnostic keys write other files there too. Those files, and
`BepInEx\LogOutput.log`, contain the game's own text. Keep them on your own
machine: do not share them or add them to this repository. `.gitignore` blocks
them.

---

## Repository layout

```
README.md            this file: what the mod does, install, build
HANDOVER.md          start here to continue the project: status, checks, next work, AI-agent prompt
ARCHITECTURE.md      how the code is organised: boot, tick loop, hooks, file-by-file map
LEGAL.md             what may and may not be committed, and how the repo meets it
LICENSE              MIT
SuikodenAccess.csproj
*.cs                 the plugin source (flat, one namespace: SuikodenAccess)
tools/gsd2_score.py  offline scorer for Suikoden II walk dumps (reads dumps from outside the repo)
docs/                the current design references, one per subsystem
docs/history/        superseded working notes, kept for the reasoning
docs/TEXT-HOOKING-HANDOFF.md  reusable guide: how to hook dialogue, menus and battle in another Unity game
```

## The one idea that solved the hard parts

**When it is unclear what some game data means, find something the game
itself observes that no interpretation can argue with. Where the player
actually walked, where they landed after a door, which name the game put on
screen. Then score every candidate reading against it. Reasoning about the
numbers produces candidates. It never chooses between them.**

Suikoden II's collision was answered wrongly four times before this rule was
adopted, and each wrong answer looked settled at the time.
[`docs/Suikoden-Access-PATHFINDING-HANDOFF-2026-09-16.md`](docs/Suikoden-Access-PATHFINDING-HANDOFF-2026-09-16.md)
§0 explains it in full.

---

## Credits and licence

Code: MIT, see [`LICENSE`](LICENSE). [`LEGAL.md`](LEGAL.md) explains what may
go into this repository. In short: no game assets, text, dumps or decompiled
code.

The mod runs on, or links against, the following third-party software. None
of it is included in this repository, and each is under its own licence:

| Component | Licence | How it is used |
|---|---|---|
| BepInEx 6 (IL2CPP) | LGPL-2.1 | the mod loader; the player installs it |
| HarmonyX | MIT | patches game methods at runtime; ships with BepInEx |
| Il2CppInterop | LGPL-3.0 | generates the interop assemblies on the player's machine; ships with BepInEx |
| System.Speech | MIT | the SAPI fallback voice; a NuGet package copied next to the plugin at build time |
| NVDA Controller Client | LGPL-2.1 | speech through NVDA; the player supplies it |

Some reverse-engineering patterns and hook points were informed by
d3xMachina's *Suikoden-Fix* (MIT), used as a reference implementation.

*Suikoden* is a trademark of Konami. This is an unofficial fan accessibility
project. It is not affiliated with or endorsed by Konami, and it contains none
of the game's code, text or assets.
