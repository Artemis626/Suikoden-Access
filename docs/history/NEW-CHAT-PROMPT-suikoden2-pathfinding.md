# Prompt for the new Suikoden 2 pathfinding chat

> **Historical document, superseded.** Kept for the reasoning, not as
> instructions. For the current state, start with
> [`HANDOVER.md`](../../HANDOVER.md) in the repository root. Version numbers,
> next steps and file locations below are out of date.

Copy everything below the line into a fresh chat opened on
the repository folder.

---

Continuing Suikoden Access, my NVDA screen-reader accessibility mod for Suikoden I
& II HD Remaster (Steam). **This session is Suikoden 2 only, and the subject is
navigation and pathfinding.** Suikoden 1's navigation is finished and in daily use
— don't touch it except to read it as a worked example.

Read this first, all of it, before proposing anything:

**`Suikoden-Access-PATHFINDING-HANDOFF-2026-09-16.md`** — written for exactly this
session and self-contained. `Suikoden-Access-NAV-STATUS.md` is the full reference
if you need to go deeper; §2 of it is the Suikoden 2 section.

Four things from that document that I do not want re-litigated:

**§0 is the method and it is not optional.** The same collision question was
answered wrongly four times over five weeks in Suikoden 1. Every wrong answer was
a plausible inference about a coordinate system, checked only against that same
coordinate system, and every one let A* walk through a wall. It was settled by
scoring every candidate rule against cells the player had actually stood on.
Reasoning about the numbers generates candidates; only an observation chooses
between them. **If you are about to conclude something about coordinates from
arithmetic alone, stop and find an observation instead.**

**Zero violations is necessary, not sufficient** — a rule that blocks nothing also
scores zero. **And one map is enough to find a rule and never enough to bound it.**

**Never Harmony-patch a hot-path method taking a by-ref struct parameter.** This
was paid for in Suikoden 2 specifically: a postfix reading `object[] __args` on
`EVENTCON.EventMapAtariCheckFunc1(int, int, ref RECT)` corrupted live collision
in-game — walls stopped blocking and the player slid off the map edge. Harmony
round-trips by-ref structs through boxing even when the postfix never writes to
them. `EventMapAtariCheckFunc1` and `EventAtariCheckFunc1` are off limits.
Calling a static, value-parameter-only game method is a different and acceptable
risk; keep it one-shot, never per frame.

**Never cap a measurement, and log the zero.** "Found none" and "never looked"
must never look the same in a log. There are two live caps in `NavProbe.cs` right
now and §4 of the handoff names them.

Where things stand on game 2: position and identity work (`EVENTCON` captured via
`NewEventPlayerMove`, `px`/`py` tracking, `ehuman[0]` is the player, `EVENT_POS`
is 16.16 fixed point, 60×60 tiles at 16 units, 6 layers). **Collision is
unsolved.** Several routes have been tried and are recorded with their results;
one probe has been written and never run.

§5 of the handoff argues for three things in order, and I'd like you to start
there rather than with a new hypothesis:

1. **Build the walked-cell recorder for Suikoden 2.** Suikoden 1 has one and game
   2 does not, and that — not a shortage of candidate grids — is why its collision
   is unsolved: there is currently nothing that can referee between hypotheses.
   Pure field reads of `px`/`py`, keyed by `mno`, no game call, no hook.
2. **Reconsider `EVENTCON.EventMapAtariCheck(pno, dir)`.** NAV-STATUS writes it
   off as useless for A* because it only answers about an entity's current
   position. That's true and it's the wrong conclusion — an oracle that answers
   only about where the player is standing is exactly the kind of referee §0 asks
   for. Read the caution about call frequency before implementing it.
3. **Press F10 on a Suikoden 2 field map and read what comes back.** One press
   settles the chip-attribute hypothesis, dumps the `tokusyu_atari` zone list, and
   runs the BGCON sweep — once its `typ` cap is raised from 8 to 256.

Also worth an early look, and nobody has read either at runtime:
`TOKUSYU_ATARI { typ, x, y, w, h, dat0, dat1, dat2 }` and
`MAPINFO { ..., mapno, px, py, ... }`. Suikoden 1's single biggest win was
discovering that exits were static data carrying real destinations
(`VILLAGEOUT destMap, destX, destY`), and game 2 has the same shape sitting
unexamined. That would give targetable doors even if the walkable grid stays
unsolved.

**Quality over speed.** The waypoint-graph fallback that the FF Pixel Remaster
mods use is the worst case, not the plan — exhaust real pathfinding first. When
something is genuinely ruled out, say so plainly and stop circling it.

Practical notes:
- Build with `dotnet build`; the post-build step deploys to `BepInEx\plugins`.
  Game path is `E:\SteamLibrary\steamapps\common\Suikoden I and II HD Remaster`.
- **Confirm `TitleProbe.Version` in the log before trusting any test result** —
  currently v53. A stale build has masked several sessions.
- You can read `BepInEx\LogOutput.log` directly; I don't need to paste it. Suikoden
  2 lines are tagged `[Nav]`, Suikoden 1 `[Nav1]`.
- `BepInEx\_decompiled\GSD1.cs` is valid ASCII. The others, including the 145 MB
  `GSD2.cs`, are partly UTF-16 — pipe through `tr -d '\000'` before grepping or
  matches vanish silently. The interop decompiles carry signatures and parameter
  names but **never method bodies**, so nothing about what a method *does* can be
  answered from them; it can only be measured in play.
- Type names collide between the games — `MAP`, `GAME_WORK` and `WindowManager`
  are all declared in both. Use `Gsd.FindIn(dll, name)` for anything belonging to
  one game's field code.
- The project is now a **local git repo, with no remote on purpose.** Commit
  freely; do not add a remote, do not push, and do not publish anything anywhere
  until I say so.
- Nothing from the game — assets, text, decompiled code, logs — goes into the
  repo. `.gitignore` enforces it; keep it that way.
