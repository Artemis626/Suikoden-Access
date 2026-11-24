# Suikoden Access — Status & Handoff (Milestones 3–4)

> **Current reference.** The project is shelved at v72 and unmaintained; start
> with [`HANDOVER.md`](../HANDOVER.md) in the repository root. File names below
> are as they were when this was written: the docs now live in `docs/`, and
> `PROJECT-STATUS.md` became `HANDOVER.md`.

**Supersedes nothing; extends `Suikoden-Access-STATUS.md`.** That document covers
Milestone 1 (dialogue) and 2 (choices). This covers Milestone 3 (title screen and
menus) and Milestone 4 (battle), and hands off to Milestone 5 (navigation).

All Milestone 3–4 work lives in one file, **`TitleProbe.cs`**, wired in with a single
line in `Plugin.Load()`: `TitleProbe.Install();`

---

## 1. The method. Use it for every new subsystem.

Seven subsystems have been mapped this way. Deviating from it has cost multiple
wasted builds every single time.

**Step 1 — Build a class-by-class map from the decompiled sources.** Never infer
which class owns a method from a merged dump; that error alone cost four builds.

```powershell
# every battle-ish class and the methods it DECLARES
Select-String -Path BepInEx\_decompiled\*.cs -Pattern "NativeMethodInfoPtr_(\w+) = IL2CPP\.GetIl2CppMethodByToken\(Il2CppClassPointerStore<(\w*Battle\w*)>" |
  ForEach-Object { "$($_.Matches[0].Groups[2].Value) :: $($_.Matches[0].Groups[1].Value)" } |
  Sort-Object -Unique | Out-File -Encoding utf8 battle_map.txt

# same for fields
Select-String -Path BepInEx\_decompiled\*.cs -Pattern "NativeFieldInfoPtr_(\w+) = IL2CPP\.GetIl2CppField\(Il2CppClassPointerStore<(\w*Battle\w*)>" |
  ForEach-Object { "$($_.Matches[0].Groups[2].Value) :: $($_.Matches[0].Groups[1].Value)" } |
  Sort-Object -Unique | Out-File -Encoding utf8 battle_fields.txt
```

**Step 2 — Hook the cursor method.** Every menu in this game has one method meaning
"the highlight moved to X". Postfix it, read the row's label, speak.

**Step 3 — Log everything before speaking it.** Each reader logs what it found and
what it will say, so a wrong readout is diagnosable from the log without a second
play session.

**Step 4 — Confirm the build actually ran.** `TitleProbe.Version` is printed at
startup. A stale hardcoded version string once masked several test runs.

### The recurring shape

| Screen | Class | Cursor method | Labels from |
|---|---|---|---|
| Launcher | `LauncherView` | `ChangeHeader(from,to)`, `ChangeGallery/Setting/…(bool)` | hardcoded (sprites) |
| Settings 1 / 2 | `UIConfigWindow` / `UIConfig2Window` | `SetItemSelect(row)` | `SetItemLocalizeText(row,label,…,opts[])` |
| Per-game title | `TitlePanel` | `SelectMenuButton(MenuType)` | `TitleMenuItem.SetText(s)` |
| Gallery | `UIGalleryTop01` | `SetSelect(int)` | `text` field (`TMP_Text[]`) |
| Title prompts | `TitlePanel` | `SelectHardConfirmButton(int)` | `ConfirmHardText/Yes/No` |
| Save / load | `UISaveLoadBase` | `Exec()` (per frame) | `selectNo` + `saveLoadItemList` |
| Name entry | `UISystemInputName` | `UpdateEditorCursorPosition` → read `m_editorCursorPos` | `m_textDict[Vector2Int]` |
| **Battle** | **`UIBattleMain.MoveCursor(WindowType,int)`** | one hook for every battle window | `m_windowListDict[WindowType]` → `itemNameList` |
| **In-game menu** | `UIMainMenuCommon.SetCursor(int)`, `UIMainMenuPartyList.SetCursor(int,int)`, **`UIWindowList`/`UIWindowListTop.SetSelectCell/SetSelectOneCell(int)`** | one pair of hooks for every list window, gated on `UIMainMenu.Open()` | the `UIWindowListCell`'s own visible text objects |

---

## 2. What works

**Title screen** — launcher, both settings pages (name + description + value as one
utterance, sliders as "7 of 10"), per-game menu, Gallery, save/load slots, name entry
(grid, capitals, confirmation), difficulty, and orientation cues (title screen,
loading, opening movie with skip instructions, main menu).

**Battle** — "Battle started"; the top row (Fight / Don't run / Bribe / Auto); the
Fight submenu; Items and Runes with quantities (name then count); Unite; enemy
target selection with duplicates numbered (same enemy name plus 1, 2, 3);
descriptions after each item; the end-of-battle message box; the confirmation
question; whose turn it is.

**Both games** share these classes, so Suikoden 1 should inherit all of it —
**unverified**, because reaching a Suikoden 1 battle needs navigation.

## 3. Known gaps (all non-blocking, all deliberate)

1. **Damage attribution** — damage is announced without naming who was hit.
   `CreateDamage` receives a `Transform`, not a name, so attribution needs the
   battle's own party/enemy lists. (The old "<speaker>: 12" bug is fixed: damage
   popups reuse the dialogue components, and `DialoguePatch.Flush` now drops a
   bare number.)
2. **Confirmation Yes/No highlight** — the question reads and both options are read,
   but which one is highlighted is not tracked. (**The question itself was NOT
   being read in the field until 2026-09-05.** `SysButtonPost` fires once per
   BUTTON, not once per dialog, and only spoke the caption — so the warning that
   quitting to the title screen loses unsaved progress was audible as a bare "No"
   then a bare "Yes". The message had been sitting in that hook's own labels list
   the whole time. It is now spoken once per question, with the captions queued
   behind it rather than interrupting it.) `SelectButton` only fires on
   movement, and the options are drawn on `index_1` / `index_2` with no visible
   highlight marker.
3. **Exit Game window** (title screen) — may now be fixed by the `index_0` route;
   untested.
4. **Suikoden 2 take-over prompt** — reads on revisit, not first appearance.
5. **Settings "Default" button** — reads its description, not its caption.
6. **In-game menus** — **top level CLOSED 2026-09-05**, sub-menus still open.
   The command column (Items / Runes / Equipment / Stats / Formation / Log /
   Settings) and the party rows now read. The cursor methods the §1 method found:
   `UIMainMenuCommon.SetCursor(int)`, `UIMainMenuPartyList.SetCursor(int,int)`,
   and — the one that matters — `UIWindowList` / `UIWindowListTop`
   `SetSelectCell(int)` / `SetSelectOneCell(int)`, because **every list in this
   game is a `UIWindowList` holding `List<UIWindowListCell>`**. One pair of hooks
   therefore covers every list-shaped window, the same economy as
   `UIBattleMain.MoveCursor`.

   **The hooks are gated on `UIMainMenu.Open()`/`Close()`**, and that gate is not
   optional: battle uses `UIWindowList` too (`UIBattleMain.m_windowListDict`), so
   an ungated hook would say every battle command twice and regress Milestone 4.
   Shops and storage are `UIWindowList`s as well and are deliberately left silent
   until they get their own pass. Sub-menus below the top level are next:
   `UIMainItemMenu` alone declares six cursor methods, each paired with a named
   window field. See `MainMenuReader.cs` and handoff §12.

---

## 4. Rules paid for in crashes and wasted builds

**Harmony**
- Patch a small, **deliberately named** set of methods. Never a swept set.
- Only patch methods whose arguments are all **simple values**. Marshalling a
  delegate or game object for logging crashed the process at startup.
- Never patch lifecycle methods (`Awake`/`Start`/`OnDestroy`).
- Enumerating a derived class returns **inherited** methods, so a sweep patches the
  same method many times over and crashes natively. Use `DeclaredOnly` **and** dedupe.
- Virtual methods must be patched on the class that **overrides** them; patching the
  base intercepts nothing.
- Never retry a failed hook every frame — that wrote ~60,000 log lines in a session.
  Retry ~1/second, give up after ~60 attempts, and log a **miss** explicitly.

**Lookup**
- Never hardcode the assembly list. `UIBattleMain` was invisible for several builds
  purely because the search covered four named DLLs. Enumerate every DLL in
  `BepInEx\interop`, skip engine ones, and log where each type was found.

**Speech**
- Every `Speak()` with interrupt cancels the previous one. This silently broke three
  features where the data was correct and simply never heard. One place decides when
  a description is spoken, so it can never be spoken twice or cut a label off.
- Read the **highlighted item**, not text as it changes. Menu labels are written once
  when a screen is built.
- Read **visible** labels only; UI objects carry several alternative layouts at once.

**Editing this file (two failed builds)**
Before handing over any `.cs`: braces balance; no duplicate method signatures within
a class; every `nameof(...)` postfix target exists; no references to removed members;
and **every class with `[HarmonyPostfix]` still carries its `[HarmonyPatch]`
attribute** — losing that attribute compiles cleanly and silently disables the hook.
Anchor edits on text unique to the target class: `CursorPost` exists in three classes.

---

## 5. Milestone 5 — navigation and pathfinding

Independent of everything above; nothing here blocks it.

**Target design** (from Pokémon Access and the FF Pixel Remaster mods): a searchable
list of landmarks per area, a key to select one, and a second key that speaks the
next step and updates as the player moves.

**Primitives already demonstrated** in d3xMachina/Suikoden-Fix:
- **Input injection** — `Village_c.NewPlayerMove` (GSD1) /
  `EVENTCON.NewEventPlayerMove` (GSD2), writing directional bits into the pad word:
  `Up 0x1000, Right 0x2000, Down 0x4000, Left 0x8000, Confirm 0x0020, Dash 0x0040`.
- **Map awareness** — `EncounterRate.cs` reads map number `mno` and indexes per-map
  event data; teleport code reads/writes location tables.

**The open question, unchanged:** is a walkable/collision grid readable (true A*), or
do we use a waypoint graph with greedy stepping and stuck-detection, as the FF mods
do? Worst case falls back to waypoints, which is still playable.

**First commands for this milestone:**
```powershell
Select-String -Path BepInEx\_decompiled\*.cs -Pattern "NativeMethodInfoPtr_(\w+) = IL2CPP\.GetIl2CppMethodByToken\(Il2CppClassPointerStore<(\w*(Map|Village|Player|Move|Field|Machi)\w*)>" |
  ForEach-Object { "$($_.Matches[0].Groups[2].Value) :: $($_.Matches[0].Groups[1].Value)" } |
  Sort-Object -Unique | Out-File -Encoding utf8 map_map.txt
```
Then the matching `NativeFieldInfoPtr_` sweep. Look for: the player's tile
coordinates, the current map id, a collision or passability array, and the list of
exits / NPCs / interactables per map.

**Why navigation is now the priority:** it is the last unknown of comparable size,
and it gates verifying Suikoden 1 at all — that game's intro is too long to reach a
battle without it.

---

## 6. Build & test loop

```powershell
dotnet build                      # post-build target copies the dll into BepInEx\plugins
# launch, confirm the log shows the expected version, play, then:
Select-String "\[Battle\]|\[Detail\]|\[Gsd\]" BepInEx\LogOutput.log | Out-File -Encoding utf8 out.txt
```

`TitleProbe.cs` has per-reader switches at the top (`EnableLauncher`, `EnableConfig`,
`EnableTitleMenu`, `EnableGallery`, `EnableDialog`, `EnableSaveLoad`,
`EnableNameEntry`, `EnableBattle`) for bisecting a crash without guesswork.

**Pending tidy-up:** `TitleProbe.cs` is now ~1,900 lines and should be split into a
shared core (`Narration`, `UiText`, `Gsd`) plus one file per subsystem, alongside
`DialoguePatch.cs`. Worth doing before navigation grows it further.
