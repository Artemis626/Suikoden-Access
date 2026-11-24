# Suikoden Access — Title Screen & Menus: Status Handoff

> **Current reference.** The project is shelved at v72 and unmaintained; start
> with [`HANDOVER.md`](../HANDOVER.md) in the repository root. File names below
> are as they were when this was written: the docs now live in `docs/`, and
> `PROJECT-STATUS.md` became `HANDOVER.md`.

**Companion to `Suikoden-Access-STATUS.md`.** That document covers Milestone 1 (dialogue)
and Milestone 2 (choices), both complete. This one covers Milestone 3: the title screen,
launcher, settings, and the menu-reading pattern that everything later will reuse.

All work lives in one temporary file, **`TitleProbe.cs`**, alongside the stable
`DialoguePatch.cs` / `Plugin.cs` / `Speech.cs`. It is wired in with a single line in
`Plugin.Load()`: `TitleProbe.Install();`

---

## 1. The method that works

Six subsystems have been mapped with the same four steps. It has not failed yet:

1. **Decompile once** — `ilspycmd` over `BepInEx\interop\*.dll` into `BepInEx\_decompiled\`.
2. **Find the class** — grep the decompiled sources for a name or a known string:
   ```powershell
   Select-String -Path BepInEx\_decompiled\*.cs -Pattern "Il2CppClassPointerStore<\w*(Config|Dialog|Gallery)\w*>" |
     ForEach-Object { ($_.Line -replace '.*Il2CppClassPointerStore<','') -replace '>.*','' } |
     Sort-Object -Unique
   ```
3. **List its members** — real field and method names, with signatures:
   ```powershell
   Select-String -Path BepInEx\_decompiled\*.cs -Pattern "Il2CppClassPointerStore<UIConfigWindow>" |
     Where-Object { $_.Line -match "GetIl2CppMethodByToken|GetIl2CppField" } |
     ForEach-Object { (($_.Line -replace '.*NativeMethodInfoPtr_','M:') -replace '.*NativeFieldInfoPtr_','F:') -replace ' =.*','' } |
     Sort-Object -Unique
   ```
4. **Hook the cursor method, read the label.** Every menu in this game exposes a
   "the highlight moved to X" method. Postfix it, read the label, speak.

**The recurring shape.** Each menu has a cursor method whose argument is the selection,
and a way to get the words. The names vary; the shape does not:

| Screen | Class | Cursor method | Labels from |
|---|---|---|---|
| Launcher (game picker) | `LauncherView` | `ChangeHeader(from,to)`, `ChangeGallery/Setting/AdditionalSetting/Language(bool)` | hardcoded (sprites, no text) |
| Settings 1 | `UIConfigWindow` | `SetItemSelect(row)` | `SetItemLocalizeText(row,label,s2,s3,opts[])` |
| Settings 2 | `UIConfig2Window` | same | same |
| Per-game title | `TitlePanel` | `SelectMenuButton(MenuType)` | `TitleMenuItem.SetText(s)` in index order |
| Gallery | `UIGalleryTop01` | `SetSelect(int)` | `text` field = `TMP_Text[]` |
| Prompts | `TitlePanel` | `SelectHardConfirmButton(int)` | `ConfirmHardText/Yes/No` fields |
| Save / load | `UISaveLoadBase` | `Exec()` (per frame) | `selectNo` + `saveLoadItemList` |

---

## 2. What works now

- **Launcher** — "Suikoden I" / "Suikoden II", Gallery, Settings, Settings 2, Language Settings.
- **Settings 1 and Settings 2** — name, description and value as ONE utterance
  (the row name, then its help sentence, then `7 of 10`). Sliders report their
  level, option rows their word (Fast/Normal/Slow, Type A), action rows their button
  word (View, Quit). **Also works in-game unchanged** — `UIConfigWindow` is shared.
- **Per-game title menu** — NEW GAME, CONTINUE, Manual, Back, Exit Game, in both games.
  Reads the on-screen words, not internal names (the item called `Quit` displays "Back").
- **Gallery** — Sound Mode, Cinematics, Event Viewer, Endings, Credits + descriptions.
- **Difficulty selection** and the Hard-difficulty warning, with its Yes/No.
- **Descriptions everywhere** — via the `Text_Auto` component (title screen AND in-game).
- Dialogue, speaker names and choices from Milestones 1–2 are unaffected throughout.

## 3. Not done yet

- **Exit Game confirmation** — the SOFT confirm window (`OpenConfirmWindowCoroutine`),
  distinct from the hard one. Message reading is newly attempted; **Yes/No selection for
  it is still unidentified** — no `SelectConfirmButton` exists on `TitlePanel`.
  *Next step:* dump `TitleChapter`'s members and look for a confirm-cursor method.
- **Save / load slots** — hooked, member names just corrected, unverified.
  Expect the wording to need a formatting pass (slot, location, level, date, playtime).
- **Language modal** (Confirm / Quit) — buttons hooked via `SelectLanguageButton`,
  message not yet read.
- **In-game menus** — `UIMainMenu`, `UIMainItemMenu`, `UIMainEquipMenu`,
  `UIMainFormationMenu`, `UIMainStrengthMenu`, `UIMainCompanionMenu`, `UIMainEmblemMenu`.
  Descriptions already read; item names do not. Apply the §1 method.
- **"Default" button** in settings reads its description but not its caption. Cosmetic.

---

## 4. Hard-won rules (do not relearn these)

**Never patch methods wholesale.** An "auto probe" that patched every method on a class
crashed the game natively on scene teardown. Only patch methods whose arguments are
**simple values** (primitives, strings, enums). Never patch a method taking a delegate or
a game object if the arguments will be logged — packaging a delegate for logging crashed
the process at startup. Never patch `Awake`/`Start`/`OnDestroy`.

**Never retry a failed hook every frame.** Doing so wrote ~60,000 log lines in one session
and burned work on every frame. Retry ~1/second, give up after ~60 attempts.

**Every `Speak()` with interrupt cancels the previous one.** This silently broke three
separate features (Gallery labels, slider names, dialog messages) — the data was correct
and simply never heard. Use `Narration.Label()` for the item and `Narration.Detail()` for
anything following it; Detail queues behind a label spoken in the last 0.8 s.

**Read the highlighted item, not text as it changes.** Menu labels are written ONCE when
a screen is built. Reading `set_text` announces whichever was written last and then goes
silent forever. This is why the save screen read the empty-slot placeholder caption
and then slot 1's contents.

**Whitelist components, don't guess by shape.** The catch-all `set_text` reader now speaks
only from `Text_Auto`. `Text` echoed captions the readers already speak and leaked stale
fragments during scene changes; `index_1` is a typewriter field that spoke one letter at
a time.

**A field can be a collection.** `UIGalleryTop01.text` is a `TMP_Text[]`, one per button;
`UIConfigWindow.bottomButtonList` likewise. Log `v.GetType().FullName` when a read comes
back empty.

**Interop assemblies load lazily** and hold names but no logic. Method **names** are real;
bodies are marshaling stubs. Hook the game's own methods at runtime.

---

## 5. Build & test loop

```powershell
dotnet build                      # post-build target copies the dll into BepInEx\plugins
# play, then:
Select-String "\[Config\]|\[Dialog\]|\[Save\]|\[Detail\]" BepInEx\LogOutput.log |
  Out-File -Encoding utf8 out.txt
```

`TitleProbe.cs` has switches at the top for bisecting a crash without guesswork:
`EnableLauncher`, `EnableConfig`, `EnableTitleMenu`, `EnableGallery`, `EnableDialog`,
`EnableSaveLoad`, `EnableAutoProbe` (leave that last one **false**).

Every reader logs what it found before speaking it, so a wrong readout is diagnosable
from the log alone without a second play session.

---

## 6. Notes for the pathfinding track

Pathfinding is independent of everything above and can proceed in parallel. From the
original reference dossier, the two primitives are already demonstrated in
d3xMachina/Suikoden-Fix:

- **Input injection** — `Village_c.NewPlayerMove` (GSD1) / `EVENTCON.NewEventPlayerMove`
  (GSD2), writing directional bits into the pad word:
  `Up 0x1000, Right 0x2000, Down 0x4000, Left 0x8000, Confirm 0x0020, Dash 0x0040`.
- **Map awareness** — `EncounterRate.cs` reads map number `mno` and indexes per-map data.

Open question, unchanged: whether a walkable/collision grid is readable (true A*) or
whether to use a waypoint graph with greedy stepping and stuck-detection, as the Final
Fantasy Pixel Remaster screen-reader mods do. Worst case falls back to waypoints.

The intended interaction model: a searchable list of landmarks per area, a key to select
one, and a second key that speaks the next step and updates as the player moves.

---

## 7. Suggested next steps, in order

1. Verify the save/load readout and tidy its wording — this gates loading a game.
2. Find the soft-confirm cursor method (`TitleChapter` members) → Exit Game done.
3. Language modal message → title screen complete.
4. Split `TitleProbe.cs` into a shared `MenuReader` core plus per-screen files, and fold
   it in beside `DialoguePatch.cs`. The `Narration` and `UiText` helpers are already the
   reusable core.
5. In-game main menu (`UIMainMenu` and siblings) using the §1 method.
