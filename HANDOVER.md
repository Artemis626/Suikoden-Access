# Suikoden Access: handover (start here)

**Status: shelved and unmaintained since November 2026, at build v72**
(`TitleProbe.Version`). The original author has stopped work on it and does
not plan to come back to it. This document is for whoever picks it up, a
person or an AI coding agent. It should be enough to get going without asking
anyone.

Read in this order:

1. this file: where things stand and what to do next
2. [`ARCHITECTURE.md`](ARCHITECTURE.md): how the code is organised
3. [`LEGAL.md`](LEGAL.md): what must never be committed
4. the doc in [`docs/`](docs/) for the subsystem you are about to touch (see §6)

---

## 0. Where things stand

| Area | State |
|---|---|
| **Shared UI, both games**: dialogue, choices, title and launcher, settings, save/load, name entry, battle, pause menu (top level), "Now loading" | Works, and was used in play |
| **Suikoden I navigation**: target list, A\*, exits with destinations, self-naming landmarks, character and place names, marks, "where am I" | Complete, and was used daily |
| **Suikoden II navigation**: collision solved; 8-way A\* to the people on screen; `Shift+K` naming | Works for people (tested in v66–v67). Doors, exits, objects, places and game-supplied names are not built |
| **v69 to v72** | **Built and compiles, never run in play.** See §1 |
| **User settings** | None. Options are `static bool` constants in the code |
| **Releases** | None. Nothing has been packaged for players |

## 1. First session back: check these

Everything from v69 to v72 was compiled and deployed but never played. Before
building anything new, run one session and check:

1. The log says `v72`: `[TitleProbe] heartbeat installed (v72)` in
   `BepInEx\LogOutput.log`. If not, the DLL in `BepInEx\plugins\` is stale.
2. Loading a save says "Now loading" once, and the first pause-menu
   description is still spoken when you open the menu.
3. Typing a name with `Shift+K` loses no letters (especially the one after a
   space), and `O` / `F4` do not fire while you type.
4. A Suikoden II route ends **beside** the person and says "then face
   <direction>".
5. Dialogue **and** menu descriptions are both spoken. Since v71 a single
   `TMP_Text.set_text` postfix feeds both. If descriptions go silent while
   dialogue works, look at `TmpSetTextPatch` in `DialoguePatch.cs`.
6. Battle cursor announcements keep up when you move quickly (the label
   rebuild was throttled in v71).
7. Switching games through the launcher: only the game you are in responds to
   `J` / `K` / `L` / `P`.

Details of what changed in each of those builds:
[`docs/Suikoden-Access-GSD2-HANDOFF-2026-09-23.md`](docs/Suikoden-Access-GSD2-HANDOFF-2026-09-23.md) §17–§18.

## 2. Setting up

You need:

- *Suikoden I & II HD Remaster* on PC (the Steam version was used throughout).
- **BepInEx 6, IL2CPP build** (bleeding edge), installed in the game folder
  and run once, so `BepInEx\interop\` is populated.
- The **.NET 6 SDK**.
- **NVDA** and a **64-bit** `nvdaControllerClient.dll` in `BepInEx\plugins\`
  (optional: without it the mod falls back to SAPI).
- Optional, for research: **ilspycmd**, to decompile the interop assemblies
  into a folder **outside** this repository (see `ARCHITECTURE.md` §10).

Then set `<GamePath>` in `SuikodenAccess.csproj` and run `dotnet build`. The
build copies the DLL into the game's `BepInEx\plugins\`.

The test loop: bump `TitleProbe.Version`, build, launch, check the version in
the log, play the thing you changed, then read the log with the subsystem's
tag (`ARCHITECTURE.md` §8).

## 3. What to build next

In rough order of value to a player. Each item names where the groundwork is.

1. **Run the checks in §1.** Fix whatever fails before building on top of it.
2. **Suikoden II: names the game supplies.** Right now people are "person 1",
   "person 2" until the player names them. The game does hold the data. The
   plan: talk to a few people, press `F4` on that map, then join the dump with
   the `[Npc] speaker … nameID=…` lines in the log, through four links:
   speaker name ↔ `nameID` ↔ text-table entry, map slot ↔ initial-state row,
   sprite number ↔ person-sprite row ↔ character number ↔ the game's own name
   lookup.
   *Where:* GSD2 handoff §17.2, `Gsd2Npc.cs`.
3. **Suikoden II: doors and exits.** This needs a town map. `F4` already dumps
   a DOORS section (`MAP.tokusyu_atari`, `EVENTCON.doorStateList`,
   `MapInfoHD.eventObjects`). Use `O` walk dumps and `tools/gsd2_score.py` to
   check any rule against where the player actually went.
   *Where:* GSD2 handoff §5 (lead 5.7) and §17.2.
4. **Suikoden II: objects, places, "where am I".** Copy the shape of the
   Suikoden I versions (`Gsd1Events.cs`, `Gsd1Place.cs`), but find the data
   from scratch: the two games share nothing below the UI.
5. **User settings.** Start with a BepInEx `Config.Bind` `.ini` (speech
   verbosity, target categories, key bindings), and later an in-game menu that
   works entirely by audio.
6. **UI gaps, both games:** sub-menus below the pause menu's top level, shops,
   storage; which of Yes / No is highlighted outside the save screen; naming
   who was hit in battle; the party-join popup in Suikoden I (press `F7` while
   it is on screen to see what it is built from).
7. **A release.** A zip of `SuikodenAccess.dll` + `System.Speech.dll`, with
   install steps for players. Never include game files or BepInEx's generated
   interop folder.

Smaller Suikoden I items (extend the event-opcode width table, verify text
entry) are in [`docs/Suikoden-Access-NAV-STATUS.md`](docs/Suikoden-Access-NAV-STATUS.md) §5.

## 4. How to work on this: the method

One rule explains why the hard parts eventually got solved, and ignoring it
explains every week that was lost:

> **When it is unclear what game data means, find an observation the game
> makes for you that no interpretation can argue with (where the player
> actually walked, where a door put them, which name the game drew on screen)
> and score every candidate reading against it. Reasoning generates
> candidates; only an observation chooses between them.**

In practice:

- Build a recorder before building a feature. `Gsd2Walk` (`O`) and
  `tools/gsd2_score.py` exist for exactly this.
- A constant result from a probe usually means you are asking it wrongly, not
  that there is no data. Sweep the parameters and report only what varies.
- When something is genuinely ruled out, write it down as dead, with the
  evidence, and stop circling it. The handoff docs have "DEAD" sections for
  that reason.
- Prefer real pathfinding over a waypoint-graph shortcut. Waypoints were
  always the fallback of last resort, never the plan.
- A wrong name is worse than no name: names go into files players share. The
  player confirms every name by typing it. Never guess a speaker from
  proximity (party followers talk too).

The full write-up, with the four wrong answers that taught it:
[`docs/Suikoden-Access-PATHFINDING-HANDOFF-2026-09-16.md`](docs/Suikoden-Access-PATHFINDING-HANDOFF-2026-09-16.md) §0.

## 5. Rules paid for in crashes

Short list; [`ARCHITECTURE.md`](ARCHITECTURE.md) §4 has the reasons.

- Never Harmony-patch a method that takes a struct by `ref`. Calling one is fine.
- Patch only value-parameter methods, from a deliberately named set.
- Never patch a base virtual method and its overrides.
- A hand-patched class must not carry `[HarmonyPatch]`.
- Do the work in `Tick()`, not inside a patch.
- Index Il2Cpp lists; do not `foreach` them through `IEnumerable`.
- Use the new Input System (`Keyboard.current`); legacy `Input` throws.
- Confirm the build version in the log before trusting any test.

## 6. Which doc for what

**Current references** (in [`docs/`](docs/)):

| Doc | Use it for |
|---|---|
| [`Suikoden-Access-NAV-STATUS.md`](docs/Suikoden-Access-NAV-STATUS.md) | **Suikoden I navigation**, the main reference: collision, targets, persistence, event scripts, names, rules (§3), next steps (§5) |
| [`Suikoden-Access-GSD2-HANDOFF-2026-09-23.md`](docs/Suikoden-Access-GSD2-HANDOFF-2026-09-23.md) | **Suikoden II navigation, current.** Read §13 onward: collision solved (§13), pathfinder (§14–15), naming (§16), NPC-name research (§17), v71–v72 (§18). §0–§12 are the history of how it got there |
| [`Suikoden-Access-PATHFINDING-HANDOFF-2026-09-16.md`](docs/Suikoden-Access-PATHFINDING-HANDOFF-2026-09-16.md) | §0, the observation-first method. Still binding. The rest is superseded by the GSD2 handoff |
| [`Suikoden-Access-STATUS-2.md`](docs/Suikoden-Access-STATUS-2.md) | the shared UI layer: menus and battle, and §1, the method for finding a hook |
| [`Suikoden-Access-TITLE-STATUS.md`](docs/Suikoden-Access-TITLE-STATUS.md) | the title screen, launcher and settings readers |

**History** (in [`docs/history/`](docs/history/)), superseded and kept for the
reasoning:

| Doc | Covers |
|---|---|
| `Suikoden-Access-HANDOFF-2026-08-31.md`, `-09-03.md`, `-09-05.md` | Suikoden I navigation as it was built |
| `Suikoden-Access-GSD2-COLLISION-2026-09-17.md`, `-GSD2-HANDOFF-2026-09-19.md`, `-GSD2-REVIEW-2026-09-21.md` | the Suikoden II collision search before it was solved |
| `NEW-CHAT-PROMPT-suikoden2-pathfinding.md`, `NEW-CHAT-PROMPT-suikoden2-nav-2026-09-23.md` | kickoff prompts used for earlier AI sessions |

The docs sometimes refer to `Suikoden-Access-STATUS.md` (milestones 1–2,
dialogue and choices). It was never committed; `DialoguePatch.cs` and its
comments are the reference for that layer.

In comments and docs, **game names are replaced with neutral placeholders**
("character A", "soldier role 1", "town A", `FP_A`). They are consistent
across files, so a cross-reference still reads correctly. Keep it that way
(`LEGAL.md`).

## 7. Prompt for an AI coding agent

Paste this into a new session opened on a clone of this repository:

```text
You are picking up Suikoden Access, an unmaintained, MIT-licensed screen-reader
accessibility mod (BepInEx 6 IL2CPP, C#, net6.0) for Suikoden I & II HD
Remaster. Speech goes through NVDA with a SAPI fallback.

Before proposing anything, read HANDOVER.md, ARCHITECTURE.md and LEGAL.md in
full, then the docs/ file for the subsystem in question. HANDOVER.md §3 is the
work list; §1 lists builds that were never tested in play.

Ground rules:
- The two games share only the UI layer. Below it they are unrelated
  codebases. Never assume something found in one exists in the other.
- Observation first: a claim about what game data means must be scored
  against something the player observably did (walked, was refused, was
  shown), not argued from the numbers. Build or use a recorder first.
- Never Harmony-patch a method taking a by-ref struct. Patch only
  value-parameter methods from a named set. Do the work in Tick().
- Never commit game material: no logs, dumps, decompiled code, assets,
  screenshots, or verbatim game text (dialogue, names, menu strings). Use the
  neutral placeholders already used in the docs. Read text from the running
  game at runtime instead.
- Bump TitleProbe.Version on every build, and confirm it in
  BepInEx\LogOutput.log before trusting a test.
- I will run the game and report what I hear and what the log says. Ask me for
  a specific test when you need one; you cannot play the game yourself.

Start by telling me which item from HANDOVER.md §3 you suggest and what you
need me to do in-game to start it.
```

## 8. Local-only material (not in this repository)

The original author kept these on their own machine. They are not needed to
continue, and none of them may be committed:

- ground-truth dumps from play sessions (walk dumps, NPC dumps, logs), used by
  `tools/gsd2_score.py`. Make your own: they contain game text;
- `BepInEx\LogOutput.log` files;
- decompiled interop sources.
