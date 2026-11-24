using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SuikodenAccess;

// ─────────────────────────────────────────────────────────────────────────────
// GSD2 (Suikoden II) GROUND-TRUTH RECORDER.
//
// Suikoden 2's collision is SOLVED (handoff §13: EventMapAtariCheckFunc1 as a
// point query, plus the TATEMONO pattern masks), and Gsd2Path routes on it. This
// recorder is how that was settled, and it stays as the pathfinder's referee:
// on a new map (a town, doors) O writes what the player actually did, and
// tools/gsd2_score.py scores a rule against it. The lesson it was built on:
//
//     Arithmetic about a coordinate system cannot referee a question about that
//     same coordinate system. Reasoning generates candidates; only an
//     observation chooses between them.
//
// So this collects observations and asserts nothing.
//
// WHAT IS RECORDED, and why each is trustworthy
//
//  1. WALKED positions — every distinct (px,py) the player has occupied. A place
//     the player stood is walkable, whatever any grid says. This is the same
//     falsifier that settled game 1.
//
//  2. MOVE EDGES — every observed (from) -> (to) step. Game 2 has diagonal
//     movement (EVENT_HUMAN carries isDiagonalMove / diagonalX / diagonalY), so
//     "which cells are walkable" is not the whole question: which TRANSITIONS
//     are legal matters too, and an observed step proves one. Game 1 never
//     needed this because it is 4-way.
//
//  3. BLOCKED SAMPLES — position plus the direction the engine refused. The
//     first session went looking for this in EVENTCON's movement fields without
//     assuming any of them meant what they are called, and the measurement came
//     back clean: wallStopDir holds the refused direction. NoteBlocked carries
//     the evidence. This is the referee the whole problem was missing, and the
//     only evidence that can show a rule UNDER-blocks: walked cells can never.
//
//  4. NPC positions, per (otyp, disp), as a second walked set to be scored.
//
// RETIRED in v71: the [STUCK] samples and the raw movement-state histogram. Both
// existed to discover which EVENTCON fields mean what; that is settled, the
// scorer reads neither, and building the 19-field state string cost a string
// allocation and 19 reflective reads on every frame of field play.
//
// SETTLED, so it is not re-litigated
//
//  * Directions: 0=S 1=N 2=W 3=E, 11=SW 12=SE 13=NW 14=NE, 255=none.
//  * EventMapAtariCheck(pno, dir) answers only for the player (1888 calls on 59
//    other entities, all zero). Its Shift+O sweep is retired: Func1 answers for
//    any cell. BGCON.EventMapAtariCheckF1 is dead (1536 combinations, no
//    variation). The slide route (wallOrgMoveDir / wallMoveDir while moving) is
//    closed: two formulations, both contradicted by observed steps (v59, v62).
//
// COST AND RISK
//
// Everything on the per-frame path is a pure field read on an instance the probe
// already holds. No game method is called, nothing is patched, nothing written.
// ─────────────────────────────────────────────────────────────────────────────
internal static class Gsd2Walk
{
    // ── caps ────────────────────────────────────────────────────────────────
    // Every cap here is a guard against unbounded growth over a long session,
    // never a limit on a measurement. Each one LOGS when it is reached, because
    // "found none" and "stopped looking" must never look the same in a dump.
    private const int MaxWalked = 60000;
    private const int MaxEdges  = 120000;

    private static string _mapKey;
    private static readonly HashSet<int>  _walked = new HashSet<int>();
    private static readonly HashSet<long> _edges  = new HashSet<long>();
    private static readonly Dictionary<string, int> _blocked = new Dictionary<string, int>(StringComparer.Ordinal);
    private static bool _walkedCapped, _edgesCapped, _blockedCapped;
    private const int MaxBlocked = 20000;

    // ── NPC positions as a second walked set, v59 ───────────────────────────
    // A cell an NPC stood on is walkable by exactly the argument that makes a
    // cell the PLAYER stood on walkable, and ehuman reported SIXTY entries on
    // the camp against one player. This is the cheapest coverage available
    // anywhere in the problem: pure field reads, no call, no risk, and it
    // accumulates while the player stands still.
    //
    // Kept per otyp rather than merged, so the composition stays visible.
    // Whether otyp 2 and 4 walk on the same ground the player does is a question
    // for scoring; filtering on it now would be a guess of the kind that has
    // been wrong four times in this project.
    private static readonly Dictionary<int, HashSet<int>> _npcByOtyp = new Dictionary<int, HashSet<int>>();
    private static bool _npcCapped;
    private static int _npcCells, _npcSeenMax;
    private const int MaxNpcCells = 40000;

    // Sixty entities is 120 reflective reads a frame. This is a stated SAMPLING RATE, not a cap on a
    // measurement: a cell can in principle be missed. Every second frame is
    // still several samples per px cell — crossing one takes about seven frames
    // at speed 1, which is measured (dx runs 0..7) — so a skipped cell would
    // need an NPC moving several times player speed.
    private const int NpcSampleEvery = 2;

    // wallStopDir LATCHES: it keeps its value after the player stops pushing and
    // is only cleared when movement resumes. Recording it on every frame it is
    // non-255 would therefore stamp a stale "blocked west" onto every position
    // the player walked to afterwards. Only the TRANSITION is a fresh
    // observation, so the previous value is kept and samples are edge-triggered.
    private static int _prevWallStop = 255;

    private static int _minPx = int.MaxValue, _maxPx = int.MinValue;
    private static int _minPy = int.MaxValue, _maxPy = int.MinValue;

    private static int _lastPx = int.MinValue, _lastPy = int.MinValue;
    private static int _frames;

    // ── per-frame recording ─────────────────────────────────────────────────
    // Called every frame, not once a second: the one-second position log samples
    // several tiles apart, which tells you where somebody went and not which
    // cells they crossed — and "which cells" is the whole falsifier.
    internal static void Note(object eventcon)
    {
        try
        {
            if (eventcon == null) return;

            string key = MapKey(eventcon);
            if (key == null) return;                       // mid-load; never record
            if (key != _mapKey) Reset(key);

            int px = Get(eventcon, "px"), py = Get(eventcon, "py");
            if (px == int.MinValue || py == int.MinValue) return;
            if (px < 0 || py < 0 || px > 16000 || py > 16000) return;

            _frames++;
            if (px < _minPx) _minPx = px;
            if (px > _maxPx) _maxPx = px;
            if (py < _minPy) _minPy = py;
            if (py > _maxPy) _maxPy = py;

            if (_walked.Count < MaxWalked) _walked.Add(Pack(px, py));
            else if (!_walkedCapped) { _walkedCapped = true; Plugin.Logger.LogWarning($"[Nav] walked-cell set hit its {MaxWalked} cap on {_mapKey}; later cells are NOT recorded."); }

            bool moved = (_lastPx != px || _lastPy != py);
            if (moved && _lastPx != int.MinValue)
            {
                long e = ((long)Pack(_lastPx, _lastPy) << 32) | (uint)Pack(px, py);
                if (_edges.Count < MaxEdges) _edges.Add(e);
                else if (!_edgesCapped) { _edgesCapped = true; Plugin.Logger.LogWarning($"[Nav] move-edge set hit its {MaxEdges} cap on {_mapKey}; later edges are NOT recorded."); }
            }

            NoteBlocked(eventcon, px, py);
            if ((_frames % NpcSampleEvery) == 0) NoteNpcs(eventcon);

            _lastPx = px; _lastPy = py;
        }
        catch { }
    }

    // -- the blocked-direction referee ---------------------------------------
    //
    // MEASURED, in the first play session, and this is why it is trusted rather
    // than because of what the field is called:
    //
    //  * Across 1550 frames, wallStopDir was non-255 on 433 of them and every
    //    single one had speed=0, dx=0, dy=0. On every frame the player was
    //    actually moving it read 255.
    //  * It was NOT merely a stationary flag: 913 frames of idle standing also
    //    had wallStopDir=255. It fires on blocked contact specifically.
    //  * Where the player pressed west (currentMoveDir=2) and did not move,
    //    wallStopDir was 2. Where they pressed north (currentMoveDir=1) and did
    //    not move, it was 1. The value is the direction that was refused.
    //  * Independently, EVENTCON.EventMapAtariCheck(0, dir) returned 1 for
    //    exactly the direction wallStopDir held, at four different positions on
    //    three different maps. Two unrelated readings agreeing four times out of
    //    four is the cross-check this project asks for.
    //
    // So: at this position, this direction is solid. That is ground truth about
    // collision, it accumulates for free as the player walks, and it is the
    // evidence that can show a candidate grid UNDER-blocks - which walked cells
    // can never do.
    //
    // Directions are recorded raw, and Dir below says what the raw values mean.
    /// <summary>
    /// The direction encoding, MEASURED rather than assumed. Every entry comes
    /// from frames where the player was actually moving: currentMoveDir was read
    /// alongside the sub-cell offsets dx/dy, and the four cardinals each showed
    /// rows with one axis exactly zero, which pins them unambiguously. The four
    /// diagonals never produced such a row.
    ///
    ///     0 = south      (0, +1)        11 = south-west  (-1, +1)
    ///     1 = north      (0, -1)        12 = south-east  (+1, +1)
    ///     2 = west       (-1, 0)        13 = north-west  (-1, -1)
    ///     3 = east       (+1, 0)        14 = north-east  (+1, -1)
    ///     255 = none
    ///
    /// The gap from 3 to 11 is the engine's, not a transcription error, and the
    /// pattern underneath it is 11 + 2*(north) + (east).
    ///
    /// 8-way movement is CONFIRMED, by observation rather than by the presence of
    /// EVENT_HUMAN.isDiagonalMove: 63 of 230 distinct observed steps changed both
    /// axes, covering all four diagonal deltas, and the engine refuses diagonals
    /// individually - blockedDir came back as 11, 12, 13 and 14. So a diagonal is
    /// a first-class direction here and NOT a composition of two cardinals, which
    /// means A* must ask about it separately rather than inferring it.
    /// </summary>
    internal static bool DirDelta(int dir, out int dx, out int dy)
    {
        switch (dir)
        {
            case 0:  dx =  0; dy =  1; return true;
            case 1:  dx =  0; dy = -1; return true;
            case 2:  dx = -1; dy =  0; return true;
            case 3:  dx =  1; dy =  0; return true;
            case 11: dx = -1; dy =  1; return true;
            case 12: dx =  1; dy =  1; return true;
            case 13: dx = -1; dy = -1; return true;
            case 14: dx =  1; dy = -1; return true;
            default: dx = 0; dy = 0; return false;
        }
    }

    // ── QUARANTINE after our own calls ──────────────────────────────────────
    // The pathfinder's Func1 calls write movement state, and NoteBlocked is
    // edge-triggered on wallStopDir, so a write from OUR call could be recorded
    // as a refusal at the player's cell - a fabricated row in the referee. After
    // a P press, latch changes are ignored until the player leaves the cell or
    // 120 frames pass. Every ignored change is LOGGED: kept out of [BLOCKED],
    // never hidden. A quarantine that held nothing back logs nothing (v70: it
    // used to write two lines per P).
    private static bool _quar;
    private static int _quarPx, _quarPy, _quarFrames, _quarSuppressed;
    private static string _quarWhy;

    internal static void Quarantine(object ec, string why)
    {
        int px = Get(ec, "px"), py = Get(ec, "py");
        _quar = true; _quarPx = px; _quarPy = py; _quarFrames = 120; _quarSuppressed = 0; _quarWhy = why;
    }

    private static bool Quarantined(int ws, int px, int py)
    {
        if (!_quar) return false;
        if (px == _quarPx && py == _quarPy && _quarFrames-- > 0)
        {
            if (ws != _prevWallStop)
            {
                _quarSuppressed++;
                Plugin.Logger.LogInfo($"[Nav] quarantine ({_quarWhy}): wallStopDir {_prevWallStop}->{ws} at ({px},{py}) NOT recorded as a refusal.");
            }
            _prevWallStop = ws;
            return true;
        }
        _quar = false;
        if (_quarSuppressed > 0)
            Plugin.Logger.LogInfo($"[Nav] quarantine ({_quarWhy}) ended at ({px},{py}); {_quarSuppressed} latch change(s) were kept out of [BLOCKED].");
        return false;
    }


    private static void NoteBlocked(object ec, int px, int py)
    {
        int ws = Get(ec, "wallStopDir");
        if (ws == int.MinValue) return;
        if (Quarantined(ws, px, py)) return;

        // Edge-triggered: only the frame the value appears or changes is a new
        // observation. See _prevWallStop.
        if (ws == _prevWallStop) return;
        _prevWallStop = ws;
        if (ws == 255) return;

        // The fine position too. px/py are half-tile, so a blocked sample keyed
        // only by them cannot say which side of the cell the player was on - and
        // that is exactly the distinction a stride hypothesis turns on.
        string fine = FinePos(ec);
        string nb = DirDelta(ws, out int ndx, out int ndy)
            ? $"({px + ndx},{py + ndy})" : "?";
        // ── v62: WHAT WAS STANDING THERE, at this exact instant ─────────────
        //
        // This exists because the referee turned out to be contaminated. At
        // (61,76) on the camp, with a clean pre-call snapshot: wallStopDir=1
        // said north was refused, EventMapAtariCheck(0,1) returned 0 meaning the
        // MAP says north is open, isHitHuman was False and objStopDir 255 so the
        // person channel never fired — and an otyp=2 entity was standing
        // directly north. Both readings were at the exact cell origin, so
        // sub-cell position does not explain it.
        //
        // So wallStopDir fires for ENTITIES as well as terrain, and
        // isHitHuman/objStopDir covers only some of them. [BLOCKED] is the one
        // validated referee in this project and some share of it is furniture
        // and people rather than map. 2 of 18 observations in that session point
        // at a cell an entity occupied.
        //
        // This records WHERE EVERYTHING WAS when the refusal happened. Not a
        // verdict, not a filter: the blocking radius is unknown, so entities are
        // reported with their offset out to Chebyshev 1 rather than only at the
        // refused cell, and the rule that separates a prop-block from a
        // terrain-block gets SCORED off these rows instead of assumed now.
        // Refusals are rare — tens per session — so the cost of scanning ehuman
        // here is nothing, unlike the per-frame path.
        string ents = EntitiesAround(ec, px + ndx, py + ndy);

        // Every flag EVENTCON publishes that could plausibly be the engine
        // saying "that was an object, not a wall". isJustOBJRITA, isXPosCheck
        // and isYPosCheck have NEVER been read anywhere in this project. They
        // are recorded raw and uninterpreted: if one of them separates the rows
        // that coincide with an entity from the rows that do not, that is a free
        // passive label for the whole problem, and the dump cross-tabulates it.
        // If none does, the entity column still answers the question.
        string key = $"({px},{py}) fine={fine} blockedDir={ws} refused={nb} " +
                     $"cur={Raw(ec, "currentMoveDir")} wallMove={Raw(ec, "wallMoveDir")} " +
                     $"wallOrg={Raw(ec, "wallOrgMoveDir")} objStop={Raw(ec, "objStopDir")} " +
                     $"objMove={Raw(ec, "objMoveDir")} " +
                     $"rita={Raw(ec, "isRitaToWall")} hitHuman={Raw(ec, "isHitHuman")} " +
                     $"objRita={Raw(ec, "isJustOBJRITA")} xChk={Raw(ec, "isXPosCheck")} " +
                     $"yChk={Raw(ec, "isYPosCheck")} doorOpen={Raw(ec, "isDoorOpen")} " +
                     $"justDoor={Raw(ec, "isJustOpenDoor")} push={Raw(ec, "oshidashiCountNew")}/{Raw(ec, "oshidashiCountOld")} " +
                     $"| ent={ents}";

        if (_blocked.Count < MaxBlocked || _blocked.ContainsKey(key)) Bump(_blocked, key);
        else if (!_blockedCapped) { _blockedCapped = true; Plugin.Logger.LogWarning($"[Nav] blocked-sample set hit its {MaxBlocked} cap on {_mapKey}."); }
    }

    // -- NPC positions: sixty more sources of "this cell is walkable" ---------
    private static void NoteNpcs(object ec)
    {
        var ehuman = Val(ec, "ehuman");
        int n = NavProbeShared.CountOf(ehuman);
        if (n <= 0) return;
        if (n > _npcSeenMax) _npcSeenMax = n;

        // Slot 0 is the player (fpno=1, measured) and is already in _walked.
        for (int i = 1; i < n; i++)
        {
            var h = UiText.Element(ehuman, i);
            if (h == null) continue;

            // otyp 255 is an unused slot — the one documented value, so the one
            // thing filtered here. Everything else is recorded UNDER its otyp
            // rather than judged now.
            int otyp = Get(h, "otyp");
            if (otyp == 255 || otyp == int.MinValue) continue;

            int hx = EventPosPx(Val(h, "x")), hy = EventPosPx(Val(h, "y"));
            if (hx == int.MinValue || hy == int.MinValue) continue;
            if (hx < 0 || hy < 0 || hx > 16000 || hy > 16000) continue;

            // disp is in the key as of v61, not just otyp. Scoring the v59 dump
            // showed 5 of 77 NPC cells sitting on a cell the player was REFUSED
            // entry to by a cardinal refusal, spread across otyp 1, 2 and 3 and
            // absent only from otyp 4 — so otyp alone does not separate entities
            // that share the player's collision from ones that do not. disp is
            // the next cheapest discriminator and it costs one key component.
            // Still not FILTERED on: recorded, so it can be scored.
            int disp = Get(h, "disp");
            int bucket = otyp * 100 + (disp == int.MinValue ? 99 : disp);
            if (!_npcByOtyp.TryGetValue(bucket, out var set)) { set = new HashSet<int>(); _npcByOtyp[bucket] = set; }
            if (_npcCells < MaxNpcCells) { if (set.Add(Pack(hx, hy))) _npcCells++; }
            else if (!_npcCapped) { _npcCapped = true; Plugin.Logger.LogWarning($"[Nav] NPC-cell set hit its {MaxNpcCells} cap on {_mapKey}; later cells are NOT recorded."); }
        }
    }

    // -- who was standing near the refused cell, at this instant --------------
    //
    // Reports every non-player ehuman entry within Chebyshev 1 of (rx,ry), with
    // its otyp, its offset from that cell, and whether it is displayed.
    //
    // RADIUS 1 AND NOT 0, deliberately. The obvious version asks "is an entity
    // ON the refused cell", which presumes the blocking rule is exactly one px
    // cell. px is HALF-tile and an entity has its own extent, so that is a
    // guess, and a guess baked into a recorder cannot be scored out of the data
    // later — it decides the answer by deciding what to write down. Offsets go
    // in the row and the radius that actually blocks is read off them.
    //
    // MEASURED, and the reason otyp is carried rather than a boolean: otyp 2, 3
    // and 4 held exactly one cell per entity across a whole session and none of
    // them moved between two presses minutes apart, while otyp 1 put 14 entities
    // across 40 cells. So 2/3/4 are STATIC PROPS living in ehuman, and a static
    // prop that blocks is a legitimate part of the map; a moving NPC that blocks
    // is contamination that must never become a permanent wall. Those two cases
    // have opposite consequences and the row has to keep them apart.
    private static string EntitiesAround(object ec, int rx, int ry)
    {
        try
        {
            var ehuman = Val(ec, "ehuman");
            int n = NavProbeShared.CountOf(ehuman);
            if (n <= 0) return "none(ehuman unreadable)";

            var sb = new System.Text.StringBuilder();
            int found = 0;
            for (int i = 1; i < n; i++)
            {
                var h = UiText.Element(ehuman, i);
                if (h == null) continue;
                int otyp = Get(h, "otyp");
                if (otyp == 255 || otyp == int.MinValue) continue;

                int hx = EventPosPx(Val(h, "x")), hy = EventPosPx(Val(h, "y"));
                if (hx == int.MinValue || hy == int.MinValue) continue;

                int dx = hx - rx, dy = hy - ry;
                if (Math.Abs(dx) > 1 || Math.Abs(dy) > 1) continue;

                if (found++ > 0) sb.Append(' ');
                sb.Append($"[otyp={otyp}@{(dx == 0 && dy == 0 ? "ON" : $"{dx:+0;-0;0},{dy:+0;-0;0}")}" +
                          $" disp={Raw(h, "disp")} wh={Raw(h, "w")}x{Raw(h, "h")}]");
            }
            // "none" is a result and has to be visible as one: it is the row
            // that says this refusal was terrain.
            return found == 0 ? "none" : sb.ToString();
        }
        catch { return "?"; }
    }

    // EVENT_POS is a 16.16 fixed-point struct and px is EVENT_POS/8, both
    // measured (all=28835840 -> 440.0 -> px 55). Returns int.MinValue when
    // unreadable rather than a plausible zero, because a zero here would be a
    // cell at the map origin and would look like data.
    private static int EventPosPx(object pos)
    {
        if (pos == null) return int.MinValue;
        try
        {
            var all = Val(pos, "all");
            if (all == null) return int.MinValue;
            return (int)(Convert.ToInt64(all) / 65536 / 8);
        }
        catch { return int.MinValue; }
    }

    // The player entity's own position, in EVENT_POS units, read rather than
    // derived. px*8 plus the dx/dy sub-offset would probably give the same
    // number, and "probably" is how this project has gone wrong before.
    private static string FinePos(object ec)
    {
        try
        {
            var ehuman = Val(ec, "ehuman");
            var p0 = UiText.Element(ehuman, 0);
            if (p0 == null) return "?";
            return $"{EventPos(Val(p0, "x"))},{EventPos(Val(p0, "y"))}";
        }
        catch { return "?"; }
    }

    // EVENT_POS is a 16.16 fixed-point struct, not an int: Convert.ToInt32 on it
    // throws, which is what once made these look unreadable.
    private static string EventPos(object pos)
    {
        if (pos == null) return "?";
        try
        {
            var all = Val(pos, "all");
            if (all != null) return (Convert.ToInt64(all) / 65536f).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch { }
        return "?";
    }

    private static void Reset(string key)
    {
        _mapKey = key;
        _walked.Clear(); _edges.Clear(); _blocked.Clear();
        _walkedCapped = _edgesCapped = _blockedCapped = false;
        // Everything clears: a wall learned on one map carried across a
        // transition would be the latched-wallStopDir error, one level up.
        _npcByOtyp.Clear(); _npcCapped = false; _npcCells = 0; _npcSeenMax = 0;
        _prevWallStop = 255;
        _minPx = _minPy = int.MaxValue; _maxPx = _maxPy = int.MinValue;
        _lastPx = _lastPy = int.MinValue; _frames = 0;
    }

    // ── map identity ────────────────────────────────────────────────────────
    // EVENTCON.mno alone is not known to be unique across the game: BGCON keeps
    // _ano (area) and _vno (village) beside its own _mno, and f_data.IsRoomMap
    // takes all three, which is the strongest available hint that the triple is
    // the real map identity. Both readings go in the key, and BGCON._mno is kept
    // separate from EVENTCON.mno rather than assumed equal — if they ever
    // disagree, the dump shows it instead of hiding it.
    //
    // There is deliberately no fallback key for a null bcon. An earlier version
    // fell back to "m{mno}", which means a bcon that goes null for even one frame
    // changes the key, fires Reset(), and throws away a whole map's ground truth
    // without a word. Losing the evidence is the single failure this file exists
    // to prevent, so a missing identity records nothing and says so instead.

    // The warning is throttled, not once-only: a bcon that flickers null and back
    // would warn on every other frame, and an unthrottled retry in this project
    // once wrote about 60,000 lines into a single session log.
    private static float _nextBconWarn;
    private static string _keyStr;
    private static int _keyA, _keyV, _keyM, _keyBm;

    private static string MapKey(object ec)
    {
        int mno = Get(ec, "mno");
        if (mno == int.MinValue) return null;

        var bcon = Val(ec, "bcon");
        if (bcon == null)
        {
            if (Time.realtimeSinceStartup >= _nextBconWarn)
            {
                _nextBconWarn = Time.realtimeSinceStartup + 10f;
                Plugin.Logger.LogWarning("[Nav] EVENTCON.bcon reads null; nothing is being recorded until it resolves. " +
                                         "If this persists on a field map, the recorder is inert and that is why.");
            }
            return null;
        }

        int a = Get(bcon, "_ano"), v = Get(bcon, "_vno"), bm = Get(bcon, "_mno");
        // Called every frame: the string is rebuilt only when an input changes.
        if (_keyStr != null && a == _keyA && v == _keyV && mno == _keyM && bm == _keyBm) return _keyStr;
        _keyA = a; _keyV = v; _keyM = mno; _keyBm = bm;
        // BGCON._mno is kept separate from EVENTCON.mno rather than assumed equal.
        // If they ever disagree the key says so, instead of one of them quietly
        // standing in for the other.
        _keyStr = $"a{a}v{v}m{mno}" + (bm == mno ? "" : $"_bm{bm}");
        return _keyStr;
    }

    /// <summary>A map key reduced to characters a filename can carry.</summary>
    private static string SafeKey(string k)
    {
        if (string.IsNullOrEmpty(k)) return "unknown";
        var sb = new System.Text.StringBuilder(k.Length);
        foreach (var c in k) sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
        return sb.ToString();
    }

    /// <summary>The map key the recorder is currently accumulating against, or null.</summary>
    internal static string CurrentMapKey => _mapKey;

    /// <summary>The same key, reduced to characters a filename can carry, so the
    /// collision dump and the walk dump for one map land beside each other under
    /// matching names rather than each overwriting its own predecessor.</summary>
    internal static string SafeMapKey => SafeKey(_mapKey);

    // ── O: write the evidence out ───────────────────────────────────────────
    internal static void Dump(object ec)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# Suikoden Access — GSD2 ground-truth dump");
            sb.AppendLine($"# map={_mapKey}  frames sampled={_frames}");
            sb.AppendLine("# px/py are EVENTCON's own units: px*8 = EVENT_POS, and a tile is 16 EVENT_POS");
            sb.AppendLine("# units, so px is HALF-TILE resolution and tile = px/2. Positions are stored at");
            sb.AppendLine("# the finest resolution the game reports and never pre-divided — which grid is");
            sb.AppendLine("# the right one is the open question, and rounding here would answer it by accident.");
            if (ec != null) sb.AppendLine($"# EVENTCON mapw={Raw(ec, "mapw")} maph={Raw(ec, "maph")} mx={Raw(ec, "mx")} my={Raw(ec, "my")}");
            sb.AppendLine();

            sb.AppendLine($"[EXTENT] px {(_minPx == int.MaxValue ? "-" : _minPx + ".." + _maxPx)}  py {(_minPy == int.MaxValue ? "-" : _minPy + ".." + _maxPy)}");
            sb.AppendLine($"[CAPS] walked={_walkedCapped} edges={_edgesCapped} blocked={_blockedCapped} npc={_npcCapped}  (true means the measurement was truncated)");
            sb.AppendLine();

            // The most valuable section in the file, and the first one to read.
            sb.AppendLine($"[BLOCKED] {_blocked.Count} distinct (position, refused direction) observations.");
            sb.AppendLine("# At this position, this direction is SOLID. The engine said so itself, in");
            sb.AppendLine("# wallStopDir, with no call made and nothing perturbed. Edge-triggered, because");
            sb.AppendLine("# the field latches after the player stops pushing and a stale value would");
            sb.AppendLine("# otherwise be stamped onto every position walked to afterwards.");
            sb.AppendLine("#");
            sb.AppendLine("# This is the evidence that shows a candidate grid UNDER-blocks. Walked cells");
            sb.AppendLine("# can only ever show the opposite, and a rule that blocks nothing scores a");
            sb.AppendLine("# perfect zero against them. A candidate needs BOTH: no walked cell called");
            sb.AppendLine("# solid, and every one of these called solid.");
            sb.AppendLine("#");
            sb.AppendLine("# Directions, measured: 0=S 1=N 2=W 3=E, 11=SW 12=SE 13=NW 14=NE, 255=none.");
            sb.AppendLine("#");
            sb.AppendLine("# CORRECTED 2026-09-21. This header used to say that 'refused' names a cell");
            sb.AppendLine("# that is SOLID. That was an inference stated as an observation, and scoring it");
            sb.AppendLine("# against the walked set refutes it for diagonals:");
            sb.AppendLine("#");
            sb.AppendLine("#   refused cells with at least one CARDINAL refusal: 0 walked violations in 67");
            sb.AppendLine("#   refused cells from DIAGONAL refusals only:        3 walked violations in 42");
            sb.AppendLine("#");
            sb.AppendLine("# The counterexample is explicit: on the camp, (54,65) blockedDir=12 marks");
            sb.AppendLine("# (55,66) solid, and (55,66) is in the WALKED set below. An engine can refuse a");
            sb.AppendLine("# diagonal because of what is BESIDE the destination rather than because of the");
            sb.AppendLine("# destination, and this one does.");
            sb.AppendLine("#");
            sb.AppendLine("# So a refusal is a fact about a DIRECTED EDGE. Cell solidity follows from a");
            sb.AppendLine("# CARDINAL refusal and does not follow from a diagonal one. 'wouldEnter' below");
            sb.AppendLine("# names the neighbour the engine declined to move into, and is safe to treat as");
            sb.AppendLine("# solid only when blockedDir is 0, 1, 2 or 3.");
            if (_blocked.Count == 0)
                sb.AppendLine("  (none - walk INTO walls, in every direction, to collect these)");
            var blk = new List<KeyValuePair<string, int>>(_blocked);
            blk.Sort((p2, q) => string.CompareOrdinal(p2.Key, q.Key));
            foreach (var kv in blk) sb.AppendLine($"  x{kv.Value,-5} {kv.Key}");
            sb.AppendLine();

            // ── v62: the verdict, computed HERE and not offline ──────────────
            //
            // This project's recurring failure is a candidate surviving because
            // the scoring happened late, somewhere else, or not at all: four
            // wrong collision rules shipped in game 1, and the slide oracle in
            // v59. Everything needed is in memory at this moment, so the dump
            // states its own verdict and a bad candidate dies in the session
            // that produced it.
            ScoreBlockedAgainstEntities(sb);
            sb.AppendLine();

            sb.AppendLine($"[WALKED] {_walked.Count} distinct (px,py) the player has occupied on this map.");
            sb.AppendLine("# Every one of these is walkable. A candidate rule that calls any of them solid is wrong.");
            var walked = new List<int>(_walked); walked.Sort();
            WriteCols(sb, walked.ConvertAll(v => $"({Ux(v)},{Uy(v)})"), 10);
            sb.AppendLine();

            // ── sixty more witnesses to walkability ───────────────────────
            DumpEntities(sb, ec);
            sb.AppendLine();

            sb.AppendLine($"[NPC WALKED] {_npcCells} distinct (px,py) occupied by NON-PLAYER ehuman entries, across {_npcByOtyp.Count} (otyp,disp) buckets.");
            sb.AppendLine($"# ehuman held up to {_npcSeenMax} entries on this map. Slot 0 is the player and is in [WALKED] above.");
            sb.AppendLine($"# Sampled every {NpcSampleEvery} frames - a stated sampling rate, not a cap. A px cell takes");
            sb.AppendLine("# about seven frames to cross at speed 1 (dx runs 0..7, measured), so a missed cell");
            sb.AppendLine("# needs an NPC moving several times player speed. capped=" + _npcCapped);
            sb.AppendLine("#");
            sb.AppendLine("# A cell an NPC stood on is walkable by the same argument that makes a cell the");
            sb.AppendLine("# player stood on walkable - and there are sixty of them to one of him. This is");
            sb.AppendLine("# the cheapest coverage in the problem: pure field reads, and it accumulates");
            sb.AppendLine("# while the player stands still.");
            sb.AppendLine("#");
            sb.AppendLine("# SPLIT BY otyp AND disp, AND NOT MERGED. otyp 255 (unused slot) is the one");
            sb.AppendLine("# documented value and the only one filtered out.");
            sb.AppendLine("#");
            sb.AppendLine("# AND THE SPLIT EARNED ITS KEEP. Scored against the v59 session, 5 of 77 NPC");
            sb.AppendLine("# cells sat on a cell the player had been REFUSED entry to by a cardinal");
            sb.AppendLine("# refusal - spread across otyp 1, 2 and 3, absent only from otyp 4. The");
            sb.AppendLine("# player's own walked set has 0 such violations in 672. So NPC positions are");
            sb.AppendLine("# NOT simply 'walkable by the player', otyp alone does not separate the ones");
            sb.AppendLine("# that are, and merging first would have hidden exactly that. disp is in the");
            sb.AppendLine("# key now as the next cheapest discriminator.");
            sb.AppendLine("#");
            sb.AppendLine("# DO NOT merge any bucket into a walkable set until that bucket scores 0");
            sb.AppendLine("# against [BLOCKED] on its own. 63 of the 77 were cells the player had never");
            sb.AppendLine("# stood on, so the coverage on offer is real - and so is the contamination.");
            if (_npcCells == 0)
                sb.AppendLine("  (none - either ehuman is empty here or every slot read otyp=255)");
            var otyps = new List<int>(_npcByOtyp.Keys); otyps.Sort();
            foreach (var ot in otyps)
            {
                var cells = new List<int>(_npcByOtyp[ot]); cells.Sort();
                sb.AppendLine($"  otyp={ot / 100} disp={(ot % 100 == 99 ? "?" : (ot % 100).ToString())}: {cells.Count} distinct cells");
                WriteCols(sb, cells.ConvertAll(v => $"({Ux(v)},{Uy(v)})"), 10);
            }
            sb.AppendLine();

            var edges = new List<long>(_edges); edges.Sort();
            var estr = new List<string>(edges.Count);
            int diag = 0;
            foreach (var e in edges)
            {
                int a = (int)(e >> 32), b = (int)(e & 0xffffffffL);
                int dx = Ux(b) - Ux(a), dy = Uy(b) - Uy(a);
                if (dx != 0 && dy != 0) diag++;
                estr.Add($"({Ux(a)},{Uy(a)})>({Ux(b)},{Uy(b)})d({dx},{dy})");
            }
            sb.AppendLine($"[EDGES] {_edges.Count} distinct observed steps, 'from > to d(dx,dy)'.");
            sb.AppendLine("# Each proves that transition is legal. Diagonal entries (both axes changing)");
            sb.AppendLine("# are the ones that settle whether game 2's 8-way movement is real in practice.");
            sb.AppendLine($"# {diag} of {edges.Count} observed steps changed BOTH axes.");
            WriteCols(sb, estr, 5);
            sb.AppendLine();

            var dir2 = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrEmpty(dir2)) dir2 = AppContext.BaseDirectory;
            var path = System.IO.Path.Combine(dir2, $"suikoden2_walk_{SafeKey(_mapKey)}.txt");
            System.IO.File.WriteAllText(path, sb.ToString());
            Plugin.Logger.LogInfo($"[Nav] O: wrote {sb.Length} chars to {path} " +
                                  $"({_walked.Count} walked, {_blocked.Count} BLOCKED, " +
                                  $"{_npcCells} npc cells over {_npcByOtyp.Count} otyp, {_edges.Count} edges).");
            Narration.Label($"Walk dump written. {_walked.Count} positions, {_blocked.Count} blocked.");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Nav] O dump failed: {e.Message}"); }
    }


    // ── v63: every entity, with its SIZE ────────────────────────────────────
    //
    // EVENT_HUMAN carries w and h. Nothing in this project had ever read them
    // until v62 put them on the blocked rows, and the first session back shows
    // they are real and they are SELECTIVE:
    //
    //     otyp 1, 2, 3 -> w/h is 0x0 on every entity seen
    //     otyp 4       -> 8x16, 16x8, 8x40, 16x16
    //
    // Those are EVENT_POS units, so 1x2, 2x1, 1x5 and 2x2 px cells. An otyp=4
    // entity carries a COLLISION RECTANGLE. Combined with the other measured
    // fact — otyp 2/3/4 hold exactly one cell per entity and never move, while
    // otyp 1 wanders — that makes otyp=4 a static, sized, readable obstacle
    // living in ehuman, and the first new candidate collision source since
    // TATEMONO.
    //
    // It matters because of what the refusals look like. Static entities are
    // within Chebyshev 1 of 31% of refused cells against 11% of walked cells, a
    // threefold enrichment, but sit ON only 4% of them. Enrichment in the
    // NEIGHBOURHOOD rather than on the cell is exactly what an extent with an
    // offset anchor produces — the same shape as TATEMONO, where sprites anchor
    // at their base.
    //
    // v62 only recorded w/h for entities that happened to be near a refusal, so
    // the rectangle model could not be scored. This writes every entity out, so
    // it can: sweep the anchor, require ZERO walked-cell violations, then
    // measure how many refusals it covers. That is the TATEMONO method and it is
    // the only one this project trusts.
    private static void DumpEntities(System.Text.StringBuilder sb, object ec)
    {
        try
        {
            var ehuman = Val(ec, "ehuman");
            int n = NavProbeShared.CountOf(ehuman);
            sb.AppendLine($"[ENTITIES] ehuman snapshot at dump time: {(n < 0 ? "UNREADABLE" : n.ToString())} slots.");
            sb.AppendLine("# A SNAPSHOT, not a history - [NPC WALKED] above has where each bucket has been.");
            sb.AppendLine("# Taken at dump time so the sizes can be matched to the positions in that section.");
            sb.AppendLine("#");
            sb.AppendLine("# w/h are EVENT_POS, so divide by 8 for px. MEASURED so far: otyp 1, 2 and 3 read");
            sb.AppendLine("# 0x0 on every entity, otyp 4 reads 8x16 / 16x8 / 8x40 / 16x16. Only otyp 4 seems");
            sb.AppendLine("# to carry an extent, and otyp 2/3/4 never moved across a whole session while");
            sb.AppendLine("# otyp 1 did - so otyp 4 is a STATIC SIZED OBSTACLE and the best new candidate");
            sb.AppendLine("# collision source since TATEMONO.");
            sb.AppendLine("#");
            sb.AppendLine("# HOW TO SCORE IT, and it is the TATEMONO method exactly: treat each otyp=4 as a");
            sb.AppendLine("# rectangle of w/8 x h/8 px cells, sweep the anchor (sprites anchor at their BASE");
            sb.AppendLine("# in this engine, so start there), and keep only anchors giving ZERO violations");
            sb.AppendLine("# against [WALKED]. Among those, take the one covering most of [BLOCKED]. A rule");
            sb.AppendLine("# that blocks nothing also scores zero violations, so coverage is not optional.");
            sb.AppendLine("#");
            sb.AppendLine("# PRELIMINARY RESULT from v62 (5 of 12 otyp=4 sized, via annotations only): NO");
            sb.AppendLine("# common anchor works. Per entity the best offsets disagree - (+2,-1), (-2,-5),");
            sb.AppendLine("# (0,+1), (0,+1) - and one covers no refusal anywhere. Against same-session");
            sb.AppendLine("# walked cells the best zero-violation common anchor covers 2 of 69 cardinal");
            sb.AppendLine("# refused cells. This dump settles it: every size, plus fine= and exy/sxy.");
            sb.AppendLine("#");
            sb.AppendLine("# SCORE AGAINST THIS SESSION'S [WALKED] ONLY. otyp=4 at (60,74), disp=1, sat on");
            sb.AppendLine("# a cell walked in an EARLIER session and not in the session that recorded it.");
            sb.AppendLine("# A disp=1 prop is static within a session, not necessarily across sessions.");
            sb.AppendLine("#");
            sb.AppendLine("# otyp 255 is an unused slot and is listed anyway - a slot that is 255 here but");
            sb.AppendLine("# holds a live position is worth seeing, and filtering it out would hide that.");
            if (n <= 0) { sb.AppendLine("  (nothing to list)"); return; }

            for (int i = 0; i < n; i++)
            {
                var h = UiText.Element(ehuman, i);
                if (h == null) { sb.AppendLine($"  ehuman[{i}] = null"); continue; }
                int hx = EventPosPx(Val(h, "x")), hy = EventPosPx(Val(h, "y"));
                // v64: fine= is the untruncated EVENT_POS and ex/ey/sx/sy sit
                // beside w/h in EVENT_HUMAN and had never been read. px= alone
                // truncates away up to 7 of the 8 units a 1-cell rectangle
                // spans, and the v62 preliminary fit disagreed between entities
                // by one to two cells - exactly the size of what was dropped.
                sb.AppendLine($"  ehuman[{i,2}] otyp={Raw(h, "otyp"),-4} disp={Raw(h, "disp"),-4} " +
                              $"px=({(hx == int.MinValue ? "?" : hx.ToString())},{(hy == int.MinValue ? "?" : hy.ToString())}) " +
                              $"fine=({EventPos(Val(h, "x"))},{EventPos(Val(h, "y"))}) " +
                              $"wh={Raw(h, "w")}x{Raw(h, "h")} exy=({Raw(h, "ex")},{Raw(h, "ey")}) " +
                              $"sxy=({Raw(h, "sx")},{Raw(h, "sy")}) jampy={Raw(h, "jampy")} " +
                              $"ppo={Raw(h, "priorityPositionOffset")} fpno={Raw(h, "fpno"),-6} " +
                              $"pri={Raw(h, "pri")} ityp={Raw(h, "ityp")} ozok={Raw(h, "ozok")} " +
                              $"sakaxy=({Raw(h, "sakax")},{Raw(h, "sakay")}) kyori={Raw(h, "kyori")} " +
                              $"spd={Raw(h, "spd")} objkan={Raw(h, "objkan")}");
            }
        }
        catch (Exception e) { sb.AppendLine($"[ENTITIES] failed: {e.Message}"); }
    }

    // ── the contamination score ─────────────────────────────────────────────
    //
    // Partitions [BLOCKED] by what was standing at the refused cell, and
    // cross-tabulates every candidate flag against that partition. Reads the
    // annotation back out of the keys rather than keeping a parallel structure,
    // so what is scored is exactly what is written to the file and the two can
    // never drift apart.
    private static void ScoreBlockedAgainstEntities(System.Text.StringBuilder sb)
    {
        try
        {
            int terrain = 0, onCell = 0, nearOnly = 0, unreadable = 0;
            int staticProp = 0, movingNpc = 0, mixed = 0;
            var flagTable = new Dictionary<string, int[]>(StringComparer.Ordinal);

            foreach (var kv in _blocked)
            {
                string k = kv.Key;
                int e = k.IndexOf("| ent=", StringComparison.Ordinal);
                if (e < 0) { unreadable++; continue; }
                string ent = k.Substring(e + 6);

                // "none" means the scan ran and found nobody. "none(ehuman
                // unreadable)" and "?" mean it could not run at all, and those
                // must NOT land in the terrain column - a failed read that looks
                // like a clean terrain refusal is the exact shape of "found
                // none" and "never looked" being indistinguishable.
                if (ent.StartsWith("none(", StringComparison.Ordinal) || ent.StartsWith("?", StringComparison.Ordinal))
                { unreadable++; continue; }

                bool none = ent.StartsWith("none", StringComparison.Ordinal);
                bool on   = ent.Contains("@ON");
                if (none) terrain++;
                else if (on) onCell++;
                else nearOnly++;

                // otyp 2/3/4 held one cell each all session and never moved;
                // otyp 1 moved. A prop that blocks is part of the map. A person
                // who blocks is a wall that will not be there next time.
                if (on)
                {
                    bool st = ent.Contains("otyp=2@ON") || ent.Contains("otyp=3@ON") || ent.Contains("otyp=4@ON");
                    bool mv = ent.Contains("otyp=1@ON");
                    if (st && mv) mixed++; else if (st) staticProp++; else if (mv) movingNpc++;
                }

                // Every candidate flag, against the same partition. A flag that
                // is True on exactly the entity rows and False on the terrain
                // rows is the passive label this whole question needs.
                foreach (var f in new[] { "objRita", "rita", "hitHuman", "xChk", "yChk", "justDoor" })
                {
                    string tag = f + "=";
                    int i = k.IndexOf(tag, StringComparison.Ordinal);
                    if (i < 0) continue;
                    int j = k.IndexOf(' ', i);
                    string v = j < 0 ? k.Substring(i + tag.Length) : k.Substring(i + tag.Length, j - i - tag.Length);
                    string cell = f + "=" + v;
                    if (!flagTable.TryGetValue(cell, out var row)) { row = new int[3]; flagTable[cell] = row; }
                    row[none ? 0 : (on ? 1 : 2)]++;
                }
            }

            sb.AppendLine("[SCORE] Is [BLOCKED] contaminated by entities? Computed from the rows above.");
            sb.AppendLine("# WHY THIS EXISTS. At (61,76) on the camp, clean pre-call snapshot: wallStopDir=1");
            sb.AppendLine("# said north refused, EventMapAtariCheck(0,1) returned 0 so the MAP says north is");
            sb.AppendLine("# OPEN, isHitHuman was False and objStopDir 255 so the person channel never fired,");
            sb.AppendLine("# and an otyp=2 entity stood directly north. Both readings were at the exact cell");
            sb.AppendLine("# origin, so sub-cell position does not explain it. wallStopDir fires for ENTITIES");
            sb.AppendLine("# as well as terrain, and isHitHuman/objStopDir covers only some of them.");
            sb.AppendLine("#");
            sb.AppendLine("# A refusal caused by a STATIC PROP (otyp 2/3/4 - one cell each, never moved all");
            sb.AppendLine("# session) is a legitimate part of the map. A refusal caused by a MOVING NPC");
            sb.AppendLine("# (otyp 1 - 14 entities over 40 cells) is a wall that will not be there next time.");
            sb.AppendLine("# Only the first may ever enter a persisted grid.");
            sb.AppendLine($"  observations              : {_blocked.Count}");
            sb.AppendLine($"  nothing at refused cell   : {terrain}   <- terrain, the clean subset");
            sb.AppendLine($"  entity ON the refused cell: {onCell}   (static prop {staticProp}, moving NPC {movingNpc}, both {mixed})");
            sb.AppendLine($"  entity only NEARBY        : {nearOnly}   <- radius undecided on purpose; read the offsets");
            sb.AppendLine($"  no annotation             : {unreadable}");
            if (_blocked.Count > 0)
                sb.AppendLine($"  CONTAMINATION (entity on cell): {onCell * 100 / _blocked.Count}% of the referee");

            sb.AppendLine("#");
            sb.AppendLine("# CANDIDATE PASSIVE LABELS. A flag whose True rows are exactly the 'entity ON");
            sb.AppendLine("# cell' column and whose False rows are exactly the terrain column IS the label,");
            sb.AppendLine("# and it costs nothing to read.");
            sb.AppendLine("#");
            sb.AppendLine("# RESULT SO FAR: NONE OF THEM IS. Across 54 observations on the camp, all six -");
            sb.AppendLine("# isJustOBJRITA, isRitaToWall, isHitHuman, isXPosCheck, isYPosCheck and");
            sb.AppendLine("# isJustOpenDoor - read FALSE on every single row, terrain and entity alike. A");
            sb.AppendLine("# constant is not a label. Kept in the dump because a constant here means 'not at");
            sb.AppendLine("# this instant' and not 'never': wallStopDir is edge-triggered on the frame the");
            sb.AppendLine("# LATCH changes, which need not be the frame of contact, so a flag that fires on");
            sb.AppendLine("# contact and clears immediately would look exactly like this. Re-reading them");
            sb.AppendLine("# one frame earlier is the cheap follow-up if a label is ever needed.");
            sb.AppendLine("#   flag value              terrain  entityON  nearOnly");
            var keys = new List<string>(flagTable.Keys); keys.Sort(StringComparer.Ordinal);
            foreach (var f in keys)
            {
                var r = flagTable[f];
                string verdict = (r[1] > 0 && r[0] == 0) ? "  <- fires ONLY with an entity on the cell"
                               : (r[0] > 0 && r[1] == 0) ? "  <- fires ONLY without one"
                               : "";
                sb.AppendLine($"#   {f,-22} {r[0],7} {r[1],9} {r[2],9}{verdict}");
            }
            if (flagTable.Count == 0) sb.AppendLine("#   (no flags parsed - no observations yet)");
        }
        catch (Exception e)
        {
            sb.AppendLine($"[SCORE] failed: {e.Message}");
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────
    private static void WriteCols(System.Text.StringBuilder sb, List<string> items, int perLine)
    {
        var line = new System.Text.StringBuilder();
        int n = 0;
        foreach (var s in items)
        {
            line.Append(s).Append(' ');
            if (++n % perLine == 0) { sb.AppendLine("  " + line); line.Clear(); }
        }
        if (line.Length > 0) sb.AppendLine("  " + line);
    }

    private static void Bump(Dictionary<string, int> d, string k) { d.TryGetValue(k, out int had); d[k] = had + 1; }

    private static int Pack(int x, int y) => ((x & 0x3fff) << 14) | (y & 0x3fff);
    private static int Ux(int p) => (p >> 14) & 0x3fff;
    private static int Uy(int p) => p & 0x3fff;

    // UiText.Member caches its lookups per (type, name), so the per-frame reads
    // here share that cache instead of keeping a second one keyed by a string
    // built on every call.
    private static object Val(object inst, string name) => UiText.Member(inst, name);

    private static int Get(object inst, string name) => NavProbeShared.ToInt(Val(inst, name));

    // "?" for a member that is not there at all, so a renamed or missing field is
    // visible in the dump rather than silently reading as a plausible zero.
    private static string Raw(object inst, string name)
    {
        var v = Val(inst, name);
        return v == null ? "?" : v.ToString();
    }
}
