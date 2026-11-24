> **Historical document, superseded.** Kept for the reasoning, not as
> instructions. For the current state, start with
> [`HANDOVER.md`](../../HANDOVER.md) in the repository root. Version numbers,
> next steps and file locations below are out of date.

Continuing Suikoden Access, my NVDA screen-reader accessibility mod for Suikoden
I & II HD Remaster (Steam). This session is Suikoden 2 only, and the subject is
navigation. Suikoden 1's navigation is finished and in daily use — don't touch it
except to read Gsd1Path.cs as a worked example.

Read these before proposing anything, in this order:

1. Suikoden-Access-GSD2-HANDOFF-2026-09-23.md — §13 onward is the current state:
   §13 how collision was solved, §14 the pathfinder, §15–§16 the first play tests
   and Shift+K naming, §17 the NPC data research and the v70 cleanup, §18 the
   v71 copyright / performance / stability pass and v72 legal check. Earlier
   sections are the history of how it got there.
2. Suikoden-Access-PATHFINDING-HANDOFF-2026-09-16.md — **§0 is the method and it
   is not optional.** Reasoning generates candidates; only an observation chooses
   between them, and the observation has to be about the CLAIM, not about the
   fields the claim is built from.

Then read Gsd2Path.cs (the pathfinder), Gsd2Npc.cs (F4 research dump) and
Gsd2Walk.cs (the ground-truth recorder, O).

## Where things stand (v72; see PROJECT-STATUS.md for the untested list)

- **Collision is solved** (§13): `EventMapAtariCheckFunc1(0, 0, ref RECT{8x,8y,8,8})`
  is a terrain point query for any cell, CALLED (never patched), plus each
  TATEMONO's pattern atari_data mask, plus displayed people. 0 of 648 walked cells
  called solid; 50/50 cardinal refusals explained on the camp.
- **The pathfinder works in play** (§15): 8-way A*, routes to real NPCs followed
  correctly. v70 ends routes beside the person and says which way to face.
- **Targets** are displayed people only. The game gives an entity no readable
  name; the player names people with Shift+K (suikoden2_npc_names.txt).
- **Open research** (§17): F4 dumps the map's event data (people's initial
  state, event scripts, the person-sprite table with character numbers `pno`,
  scene message indexes) and every loaded text table, and each Suikoden 2
  dialogue line logs `EVENTCON.nameID`. The goal is game-supplied names.

## Constraints that are not up for re-litigation

- **Never Harmony-patch a hot-path method taking a by-ref struct.** Paid for in
  Suikoden 2 specifically. Calling such a method is a separate question and is
  gated on my explicit decision (Func1 is approved).
- **Never cap a measurement, and log the zero.**
- **An unreadable field must not look like data.**
- Reading game state is strongly preferred over writing it.
- **Quality over speed.** When something is genuinely ruled out, say so plainly
  and stop circling it.

## Practical notes

- Build with `dotnet build`; the post-build step deploys to BepInEx\plugins.
  **Confirm TitleProbe.Version in the log before trusting any test result.**
- Read BepInEx\LogOutput.log directly.
- BepInEx\_decompiled\GSD2.cs and GSDShare.cs are partly UTF-16 — pipe through
  `tr -d '\000'` before grepping or matches vanish silently. They carry signatures
  and parameter names but never method bodies.
- Type names collide between the games — use Gsd.FindIn(dll, name).
- Game dumps are overwritten per map. Preserved ground truth lives in
  Desktop\Suikoden-Access-groundtruth\. **Copy new dumps out after every
  session.** Nothing from the game goes in the repo.
- The repo has a PRIVATE GitHub remote (origin). Commit freely; push ONLY when I
  say so in that message.
- Game dumps and logs carry Konami's text. `.gitignore` blocks them (logs,
  `suikoden1_*.txt`, `suikoden2_*.txt`, screenshots); keep it that way.
