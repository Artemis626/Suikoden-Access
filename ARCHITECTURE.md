# Suikoden Access: architecture

How the code is put together, for someone opening it for the first time. For
the project's status and what to do next, read [`HANDOVER.md`](HANDOVER.md).
For the reasoning behind each subsystem, read the matching doc in [`docs/`](docs/).

---

## 1. The big picture

*Suikoden I & II HD Remaster* is a Unity game built with IL2CPP: the game's C#
was compiled to native code. BepInEx 6 loads this plugin into the game process
and, through Il2CppInterop, generates managed "interop" assemblies (in
`BepInEx\interop\`) that expose the game's classes, fields and methods to C#.
This plugin compiles against those assemblies but never ships them.

The mod works in two ways:

1. **Listening.** Harmony postfixes on the game's UI methods (a cursor moved,
   a text label was written, a window opened) tell the mod what changed, and
   the mod speaks it.
2. **Asking.** On a key press, the mod reads the game's live objects (the
   player's position, the map's people, the map's event scripts) and calls the
   game's own collision checks to plan a route.

```
 Game (IL2CPP, native)                          Suikoden Access (managed, net6.0)
 ─────────────────────                          ─────────────────────────────────
 TMP_Text.set_text ──────── postfix ──────────▶ TmpSetTextPatch ─▶ Dlg / DescriptionReader
 UIMessageWindow.Update ─── postfix ──────────▶ UpdateTickPatch ─▶ dialogue flush, choices
 UI*.SetCursor / Open / … ─ postfix (manual) ─▶ the *Reader classes in TitleProbe.cs
 Village_c.NewPlayerMove ── postfix (manual) ─▶ Gsd1Nav   captures the Suikoden I field
 EVENTCON.NewEventPlayerMove postfix+prefix ──▶ NavProbe  captures the Suikoden II field
                                                    │
 Unity Update() on a hidden GameObject ───────▶ TitleProbe.Tick()  (every frame)
                                                    ├─ hook installers, retried ~1/s for 60 s
                                                    ├─ Narration, ConfigReader, NameEntryReader
                                                    └─ NavProbe.Tick()
                                                         ├─ Gsd1Nav.Tick() ─▶ Gsd1Path.Tick() ─▶ keys, A*, Gsd1Place
                                                         ├─ Gsd1Probe.Tick()   (F5/F7/F8 dumps)
                                                         └─ Gsd2Path.Tick()    (keys, A*), Gsd2Walk (O), Gsd2Npc (F4)
                                                    │
                                                Speech.Speak() ─▶ NVDA controller client, else SAPI
```

## 2. Boot sequence

1. **`Plugin.Load()`** ([`Plugin.cs`](Plugin.cs)) runs once when BepInEx loads
   the plugin.
   - Installs a native-library resolver, so `nvdaControllerClient.dll` is
     found next to the plugin DLL.
   - `Speech.Initialize()`.
   - `PatchAllTolerantly()`: applies every class with a `[HarmonyPatch]`
     attribute **one class at a time**, so a single broken patch costs one
     feature instead of the whole mod. Only two classes use attributes:
     `TmpSetTextPatch` and `UpdateTickPatch`, both in
     [`DialoguePatch.cs`](DialoguePatch.cs).
   - Speaks "Suikoden Access loaded", the audible heartbeat.
   - `TitleProbe.Install()`: creates a hidden, persistent `GameObject` with
     the `TitleProbeHeartbeat` MonoBehaviour, whose `Update()` calls
     `TitleProbe.Tick()` every frame.
2. **`TitleProbe.Tick()`** ([`TitleProbe.cs`](TitleProbe.cs)) is the main loop.
   Every frame it runs the readers that need settling time, then
   `NavProbe.Tick()`. About once a second, for up to 60 seconds, it calls each
   reader's `TryHook()` until it succeeds. Many UI classes do not exist until
   their screen first loads, which is why the hooks are retried rather than
   applied at load.
3. **Field hooks** are installed by `Gsd1Nav.TryHook()` (Suikoden I,
   `Village_c.NewPlayerMove`) and `NavProbe.TryHook()` (Suikoden II,
   `EVENTCON.NewEventPlayerMove`). Each postfix only captures the live
   controller instance and a timestamp. All real work happens later in
   `Tick()`, never inside a patch.

## 3. Which game is running

The two games run in the same process, and the launcher can switch from one
to the other without restarting. Below the UI layer they are **two unrelated
codebases** (two separate PlayStation ports): different classes, coordinates
and collision. Nothing found in one can be assumed to exist in the other.

- `Gsd.RunningGameName()` ([`TitleProbe.cs`](TitleProbe.cs)) answers
  "Suikoden One" / "Suikoden II" / null from several signals in order of
  certainty: the live field controller first, then each game's chapter
  manager, which exists earlier. Every decision is logged with its trace.
- `Gsd1Nav.Live` and `NavProbe.Live`: when both controllers have been
  captured, the one captured **most recently** is the live game. Only the
  live game's navigation ticks.
- In the game's own naming, **GSD1** = Suikoden I and **GSD2** = Suikoden II.
  The file prefixes `Gsd1*` and `Gsd2*` follow that.

## 4. Two ways of hooking, and the rules

| Style | Used for | How |
|---|---|---|
| Attribute (`[HarmonyPatch]`) | `TMP_Text.set_text`, `UIMessageWindow.Update`: types that exist from the start | picked up by `PatchAllTolerantly` |
| Manual (`harmony.Patch(...)`) | everything else: types found at runtime by name | a `TryHook()` method resolves the type with `Gsd.FindAnywhere("Name")` and patches it |

These rules each cost a crash or a lost session to learn. Keep them:

- **Never hook a method that takes a struct by `ref`.** Harmony boxes and
  unboxes by-ref struct arguments even when the patch never touches them. A
  postfix on `EventMapAtariCheckFunc1(int, int, ref RECT)` corrupted live
  collision: walls stopped blocking. *Calling* such a method yourself, on a
  key press, is fine.
- **Patch only methods whose arguments are all simple values**, and only a
  deliberately named set. Never sweep a list of classes.
- **Never patch a base virtual method and its overrides.** It crashed the game
  natively at startup.
- **A class you patch by hand must not carry `[HarmonyPatch]`.** If it does,
  `PatchAll` finds no target and throws. That once disabled the whole mod.
- **Do nothing heavy inside a patch.** Capture, timestamp, return. `Tick()`
  does the work.
- Il2Cpp lists are not `System.Collections.IEnumerable`: index them. Arrays
  expose `Length`, lists expose `Count` (`UiText.Element` / `CountOf` handle
  both).
- The game uses Unity's **new Input System only**. `UnityEngine.Input` throws.
  Read keys from `Keyboard.current`.

The full list, with the story behind each rule, is in
[`docs/Suikoden-Access-NAV-STATUS.md`](docs/Suikoden-Access-NAV-STATUS.md) §3
and [`docs/Suikoden-Access-STATUS-2.md`](docs/Suikoden-Access-STATUS-2.md) §4.

## 5. Speech

[`Speech.cs`](Speech.cs): `Speech.Speak(text, interrupt)` tries NVDA through
the native controller client, and uses `System.Speech` (SAPI) when NVDA is not
running. It never throws.

`Narration` ([`TitleProbe.cs`](TitleProbe.cs)) sits on top and decides
*when*: a label interrupts, a description that follows it within a moment is
queued behind it rather than cutting it off, and `LabelAfter` delays a line
that would otherwise be lost under a screen change. Readers collect data;
`Narration` decides what is heard.

## 6. File-by-file map

All source is flat in the repository root, in one namespace, `SuikodenAccess`.

### Core and shared UI (both games)

| File | What it holds |
|---|---|
| [`Plugin.cs`](Plugin.cs) | BepInEx entry point, tolerant patching, NVDA DLL resolver |
| [`Speech.cs`](Speech.cs) | NVDA + SAPI output |
| [`DialoguePatch.cs`](DialoguePatch.cs) | `Dlg` (dialogue lines with speaker), `ChoiceReader` (Suikoden II choices), `Gsd1Choices` (Suikoden I choices, from its event interpreter), the two attribute patches |
| [`TitleProbe.cs`](TitleProbe.cs) | **despite the name, the shared UI hub.** `TitleProbe` (version, main loop, hook retry), `UiText` (reading text out of UI objects, cached reflection), `Narration`, `Gsd` (type lookup across all interop assemblies, game detection), and the readers: `LauncherReader`, `ConfigReader`, `TitleMenuReader`, `GalleryReader`, `DialogReader` (yes/no boxes), `SaveLoadReader`, `NameEntryReader`, `BattleReader`, `DescriptionReader` |
| [`MainMenuReader.cs`](MainMenuReader.cs) | the in-game pause menu: command column and party rows |
| [`LoadAnnouncer.cs`](LoadAnnouncer.cs) | "Now loading", and suppression of the stray help line written while a save loads |
| [`Gsd1TextInput.cs`](Gsd1TextInput.cs) | audio text entry used by `Shift+K` / `N` in **both** games, plus the movement block while typing. The name is historical |

### Suikoden I field navigation

| File | What it holds |
|---|---|
| [`Gsd1Nav.cs`](Gsd1Nav.cs) | the `Village_c` hook, position tracking, `F6` / `F11` / `F12` diagnostics |
| [`Gsd1Path.cs`](Gsd1Path.cs) | **the main Suikoden I file**: targets, categories, keys, A\* over `GNDAtariCheck` + `BLDAtariCheck` |
| [`Gsd1Events.cs`](Gsd1Events.cs) | decoder for the map's event scripts: exits with destinations, triggers, interactable objects, shop kinds |
| [`Gsd1Names.cs`](Gsd1Names.cs) | player-typed NPC names, the game's own name table (`Gsd1GameNames`), and `MapKey` (the per-room id every store uses) |
| [`Gsd1Place.cs`](Gsd1Place.cs) | place names: the arrival-banner capture, the places file, `Shift+M`, `N` |
| [`Gsd1Exits.cs`](Gsd1Exits.cs) | learned transitions (the fallback from before exits were read from data) |
| [`Gsd1Marks.cs`](Gsd1Marks.cs) | player-marked tiles (`M`) |
| [`Gsd1Probe.cs`](Gsd1Probe.cs) | read-only dumps: `F5` names, `F7` on-screen UI labels, `F8` map data |

### Suikoden II field navigation

| File | What it holds |
|---|---|
| [`NavProbe.cs`](NavProbe.cs) | the `EVENTCON` hook (capture + freeze-while-typing prefix), dispatches the Suikoden II ticks and keys |
| [`Gsd2Path.cs`](Gsd2Path.cs) | **the main Suikoden II file**: targets, keys, collision (terrain from `EventMapAtariCheckFunc1`, building masks, people), 8-way A\* |
| [`Gsd2Names.cs`](Gsd2Names.cs) | player-typed NPC names for Suikoden II |
| [`Gsd2Walk.cs`](Gsd2Walk.cs) | ground-truth recorder (`O`): where the player walked and was refused. The referee for any new collision rule |
| [`Gsd2Npc.cs`](Gsd2Npc.cs) | `F4` research dump of everything the game knows about a map's people, for the name-source research. **Writes game text: local only** |
| [`tools/gsd2_score.py`](tools/gsd2_score.py) | offline scorer: tests a collision hypothesis against `O` dumps |

## 7. Data files

Written next to the plugin in `BepInEx\plugins\`. All are UTF-8 plain text,
one `key = value` entry per line, `#` for comments. Each is read once, the first
time it is needed in a session, so edit them with the game closed.

| File | Written by | Key |
|---|---|---|
| `suikoden1_npc_names.txt` | `Gsd1Names` | `MapKey` + event index |
| `suikoden1_exits.txt` | `Gsd1Exits` | `MapKey` + tile |
| `suikoden1_marks.txt` | `Gsd1Marks` | `MapKey` + tile, optional spoken label |
| `suikoden1_places.txt` | `Gsd1Place` | `MapKey`; a `player` row (typed with `N`) beats a `game` row (from the banner) |
| `suikoden2_npc_names.txt` | `Gsd2Names` | `<mapKey>:<slot> = <name>\|<signature>`; a name whose signature no longer matches is dropped |

`MapKey` is built from the game's own area and map getters, for example
`a0.m3`, and is never spoken aloud. The class that writes each file documents
its exact format in its header comment.

## 8. Logging and diagnostics

Everything goes to `BepInEx\LogOutput.log` with a tag per subsystem:

| Tag | Subsystem |
|---|---|
| `[TitleProbe]` | version and hook-installation summary: **check this first** |
| `[Gsd]` | type lookup and game detection |
| `[Dialog]` `[Dialogue]` `[Choice]` `[G1Choice]` `[Text]` | dialogue, choices, text entry |
| `[Launcher]` `[Config]` `[TitleMenu]` `[Gallery]` `[Save]` `[Name]` `[Detail]` | title-side readers |
| `[Battle]` `[Menu]` `[Load]` | battle, pause menu, loading |
| `[Nav1]` `[G1Dump]` | Suikoden I navigation and dumps |
| `[Nav]` `[Nav2]` `[Npc]` | Suikoden II field, pathfinder, NPC research |

To pull one subsystem out of the log:

```powershell
Select-String "\[Nav1\]" BepInEx\LogOutput.log | Out-File -Encoding utf8 nav_out.txt
```

**The log contains the game's own text.** Never commit it (`.gitignore`
blocks `*.log`).

### Diagnostic keys

| Key | Game | Writes |
|---|---|---|
| `F5` | Suikoden I | name table dump (`Gsd1Probe`) |
| `F6` | Suikoden I | collision grid file (`Gsd1Nav`) |
| `F7` | either | every on-screen text label with its hierarchy path: use it to find out what an unread popup is built from (`Gsd1Probe`) |
| `F8` | Suikoden I | map data dump: events, objects, people (`Gsd1Probe`) |
| `F11` | Suikoden I | position and state dump (`Gsd1Nav`) |
| `F12` | Suikoden I | collision probe around the player (`Gsd1Nav`) |
| `O` | Suikoden II | walk dump for the scorer (`Gsd2Walk`) |
| `F4` | Suikoden II | NPC data dump (`Gsd2Npc`) |

Dumps are written next to the plugin as `suikoden1_*.txt` / `suikoden2_*.txt`,
which `.gitignore` blocks. Keep them in a folder outside the repository.

## 9. Versioning

`TitleProbe.Version` (currently `"v72"`) is the single build counter and is
logged at startup. Bump it on every build you test. A stale DLL has wasted
more than one test session, so confirm the version in the log before trusting
a result. `PluginInfo.Version` (`0.1.0`) is the BepInEx metadata version and
has not been bumped. The plugin GUID, `io.github.artemis626.suikodenaccess`, is
the prefix of every Harmony id. A fork that ships separately should change
both together.

## 10. Working with the game's code locally

To find hook points you need to see the game's class and method signatures.
Decompile the interop assemblies **on your own machine, outside this
repository**:

```powershell
ilspycmd -o <outdir> "<GamePath>\BepInEx\interop\GSD1.dll"
```

- ilspycmd writes `<name>.decompiled.cs`, not `<name>.cs`.
- The output is partly UTF-16. Strip NUL bytes (`tr -d '\000'`) before
  grepping, or matches silently disappear.
- Interop assemblies carry **signatures and field layouts only, never method
  bodies**. To learn what a method does, call it on a key press and log the
  result, or observe it in play.
- Never commit decompiled output. `.gitignore` blocks the usual names; see
  [`LEGAL.md`](LEGAL.md).

The method for finding the right hook (map every class to the methods it
*declares*, then pick the one that means "the highlight moved") is in
[`docs/Suikoden-Access-STATUS-2.md`](docs/Suikoden-Access-STATUS-2.md) §1.
