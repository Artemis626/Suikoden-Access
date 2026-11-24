# Legal hygiene for accessibility mods

These rules apply to every file, commit and release in this repository. They are
good practice, not legal advice.

### Never commit game material
- No game assets: textures, models, audio, video, fonts, scripts, data files,
  executables, ROMs, disc images or BIOS files.
- No game text: dialogue, item names, file/document text, menu labels copied
  from the game. If the mod needs game text, read it at runtime from the
  player's own installed copy, or generate it locally on the player's machine.
- No game-derived test fixtures, logs, screenshots or memory dumps. Debug logs
  that contain game text stay local and are gitignored.
- Keep game folders and extracted data in .gitignore. Before each release,
  check the repo and its history for game-derived content.

### Keep code our own
- Hooks reference functions by address, pattern or name; do not paste
  disassembled or decompiled game code into this repo beyond what a hook
  strictly needs.
- When forking a decompilation or port, keep its license and notices intact
  and keep our changes in clearly separate files.
- Respect every dependency's license (e.g. GPL, LGPL, noncommercial terms).

### Modify only the player's own copy
- Distribute DLLs, patchers or source, never modified game executables.
- Do not bypass DRM, encryption or copy protection.
- Require the player's own legal copy; optionally verify it by hash. Never host,
  link to or describe where to download games, ROMs or BIOS files.

### Presentation
- State clearly that the mod is unofficial and unaffiliated with the publisher.
- No publisher logos, box art or trademarked artwork in the repo or releases.
- No paid features or paywalled releases.

---

## How Suikoden Access meets these rules

Last checked 2026-11-24 (v72, before the repository was made public), against
the working tree and the full git history. Previous checks: 2026-10-06 (v71)
and 2026-10-16 (v72).

- **Game text is read at runtime, never stored.** Dialogue, menus, character
  names and the GSD1 event-opcode names come from the player's running game.
  Code strings are either the mod's own speech ("Now loading") or the names of
  game classes, methods and fields that the hooks call.
- **Names in comments and docs are neutral placeholders** ("character A",
  "soldier role 1", "town A", `FP_A`). They are consistent across files, so a
  cross-reference still reads correctly. They never reach the player: comments
  are not compiled and the docs are not shipped. Short UI prompts are described
  rather than quoted ("the save prompt", "the party-join popup").
- **Identifiers are kept where a hook needs them.** Class, method, field and
  enum-member names of the game (`Village_c`, `GNDAtariCheck`, `SHOP_SAVE`) appear
  in code and docs because the mod looks them up by name at runtime. They are
  the interface the mod talks to, not game content; no method body from the
  game appears anywhere.
- **Dumps and logs stay local.** The diagnostic keys (`O`, `F4`–`F8`, `F11`, `F12`) and
  `BepInEx\LogOutput.log` can contain the game's own text; the F4 dump says so
  in its first lines. `.gitignore` blocks logs, every `suikoden1_*.txt` and
  `suikoden2_*.txt`, screenshots, game folders (`BepInEx/`, `*_Data/`,
  `il2cpp_data/`, `StreamingAssets/`, `interop/`), executables, DLLs and
  decompiled sources. Preserved dumps live outside the repo, in a separate
  ground-truth folder.
- **Only our DLL is distributed.** The build references the game's interop
  assemblies without copying them; its output is `SuikodenAccess.dll` plus the
  MIT-licensed System.Speech package. No game file is modified, and no DRM is
  touched.
- **Dependencies** (BepInEx LGPL-2.1, HarmonyX MIT, Il2CppInterop LGPL-3.0,
  System.Speech MIT, NVDA Controller Client LGPL-2.1) are listed in the README;
  none is bundled in this repository.
- **Presentation.** The README states the mod is unofficial and unaffiliated
  with Konami; there are no logos or artwork, no download links, and no paid
  features.

What the 2026-11-24 check found and fixed in the tree: a partial town name
(the location banner mid-animation), a two-word NPC role name, a few short
quoted UI prompts, and one game warning sentence quoted across two lines of a
doc, all in comments or docs. They are now placeholders or descriptions. A
local folder path with the owner's name was removed too. A scan for the main
character, place and role names of both games, and for every quoted phrase of
four words or more (with line breaks joined), finds nothing else.

**Git history.** In the private working history, commits before 2026-10-16
contain what the tree no longer does: character, place and NPC-role names in comments and docs, four short menu
help lines and one popup line quoted verbatim in comments (all since
paraphrased), a copy of two GSD1 enum name lists, and a screenshot of the
game's install-folder listing (file names only). No dialogue, asset, log, dump
or decompiled source was ever committed. That history is not published; see
*History before going public* below.

Optional for the future: the launcher announcements "Gallery", "Settings",
"Settings 2" and "Language Settings" are the mod's own strings. They are generic
terms, but they could be read from the launcher's own labels at runtime instead.

### History before going public

Decided 2026-11-24: the public repository starts from a single commit of the
clean tree, so its history begins where these rules are met. The full working
history (37 commits, 2026-09-16 to 2026-11-24) is kept privately by the owner
and is not published. Nothing in the public tree depends on it: the design
notes in `docs/` and `docs/history/` carry the reasoning.
