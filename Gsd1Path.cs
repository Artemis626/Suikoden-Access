using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SuikodenAccess;

// ─────────────────────────────────────────────────────────────────────────────
// GSD1 PATHFINDING — Milestone 5, Suikoden 1. TRUE A*, not a waypoint graph.
//
// THE COLLISION ORACLES (settled 2026-09-03 against 233 walked cells)
//
//   Village_c.GNDAtariCheck(int x, int y, bool)                        -> byte
//   Village_c.BLDAtariCheck(int x, int y, int level, ref int, bool)  -> ushort
//
// BOTH TAKE CELL COORDINATES — the player's own HUMAN.map_x / map_y, used with
// no shifting at all. Nothing is doubled and nothing is halved. The search grid
// IS the player's coordinate space, one grid cell is one walking step, and the
// route is spoken in those steps.
//
//     blocked(x, y) = GNDAtariCheck(x, y, false) != 0
//                  || (BLDAtariCheck(x, y, L, ref no, false) & 0x0f) != 0
//                     for any L in the levels this map actually answers at
//
// `L` is NOT the map's layer_no. That coincided with a live level on the mansion
// maps and does not in town A, where layer_no is 2, level 2 holds nothing,
// and every house therefore read as open floor. The live levels are sampled per
// map — see FindLiveLevels.
//
// Ground is terrain — cliffs, water, fences, and a building's outside walls.
// Buildings are TATEMONO: a room's interior walls, its bed, its bookcase, the
// save desk. Outdoors the ground layer is nearly all of the geometry, which is
// why routes looked right in a town and wrong in a bedroom for weeks.
//
// WHAT EACH RETURN MEANS
// The ground byte is a 4-bit mask of ONE CELL's four quarters, and any bit set
// means the player cannot stand there. It is not a chip's four cells: 544 cells
// of the sampled map carry a partial mask (3, 5, 10, 12 …) against 493 fully
// solid ones, and the player stood on none of the 544. "Partially solid" is
// simply solid at the resolution a route is spoken in.
//
// The building ushort packs TWO 4-bit masks: the high nibble describes the
// obstacle, the low nibble the part of it landing on the queried cell. Only the
// low nibble decides. It is never non-zero where the high nibble is zero.
//
// HOW THIS WAS SETTLED, because it had been got wrong three times
// Every earlier answer came from reasoning about the numbers — a 4-bit mask
// "must" describe a chip's four quadrants, a coordinate of 36 on a 24-wide map
// "must" be half-scale, and so on. Each of those is consistent with the data and
// none of them is checkable from it, which is why three different readings each
// looked settled and each let A* walk through a wall.
//
// What settled it is a fact arithmetic cannot argue with: a cell the player has
// STOOD ON is walkable. Gsd1Nav records every distinct one, F6 writes them out
// with the whole map's collision, and each candidate reading is scored by how
// many of them it calls solid. On 233 cells of town A map 3:
//
//     GROUND    GNDAtariCheck(cell) != 0                   0 violations  <-- used
//               GNDAtariCheck(chip) & quadrant bit        81 violations
//     BUILDING  BLDAtariCheck(cell) & 0x0f != 0            0 violations  <-- used
//               BLDAtariCheck(cell) != 0                  79 violations
//               BLDAtariCheck(chip) hi nibble & bit      109 violations
//               BLDAtariCheck(chip) != 0                 130 violations
//
// Zero violations is necessary, not sufficient — a rule that blocks nothing also
// scores zero — so the pair above is also checked for blocking a real amount:
// 1089 cells of 2304, and drawing them produces a room with a desk against its
// north wall and floor in front of it, which is where the save diary is.
//
// The two rules that were live before this: the ground layer was reading
// GNDAtariCheck(x>>1, y>>1), which is a different part of the map, and the
// building layer treated the whole return as a boolean, which blocked 812 cells
// instead of 166. Before 2026-08-29 the ground layer doubled its input and then
// halved it again, a net no-op that landed on the right cell — but the quadrant
// bit then always came out as bit 0, so only the NW quarter was ever tested and
// 249 solid cells read as open. Three wrong readings, three different symptoms,
// one cause: nothing was ever checked against a place the player had been.
//
// SAFETY
// Both methods take only value parameters (plus one primitive out), so calling
// them carries none of the by-ref-struct hazard that corrupted a live session
// when a hot-path method was HOOKED with Harmony's __args. Calls happen on
// demand (a keypress), never per frame, and results are cached per pathfind.
//
// Village_c.mapVillage IS the loaded MAP. Its width/height count chips and a
// chip is 2x2 cells, so the cell range is 0..2*width-1; out of range reads as
// SOLID, because open space the router invents walks a blind player into a wall.
//
// A SELF-CHECK guards all of it: whatever else is true, the player is standing
// somewhere walkable, so if the combined rule calls the player's own cell solid
// the building layer is switched off for that request and the fact is logged,
// rather than reporting "no path" from a corrupt grid.
//
// KEYS (mirroring Pokemon Access, which this is modelled on)
//   J / L        previous / next target        K        read current target
//   Shift+J / L  cycle target category         P        pathfind, speak route
// ─────────────────────────────────────────────────────────────────────────────
internal static class Gsd1Path
{
    public static bool Enabled = true;

    // ── Collision ───────────────────────────────────────────────────────────
    private static MethodInfo _gnd;
    private static object _village;
    private static readonly Dictionary<int, int> _tileCache = new Dictionary<int, int>();

    private static bool Bind(object village)
    {
        _village = village;
        if (_gnd != null && (_bld != null || _bldMissing)) return true;
        try
        {
            foreach (var m in village.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                           BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var ps = m.GetParameters();
                if (m.Name == "GNDAtariCheck" && ps.Length == 3 &&
                    ps[0].ParameterType == typeof(int) &&
                    ps[1].ParameterType == typeof(int) && ps[2].ParameterType == typeof(bool))
                { _gnd = m; continue; }

                // BLDAtariCheck(int x, int y, int level, ref int tatemono_no, bool isLog)
                if (m.Name == "BLDAtariCheck" && ps.Length == 5 && _bld == null)
                { _bld = m; Plugin.Logger.LogInfo("[Nav1] resolved BLDAtariCheck — building collision is ON."); }
            }
            if (_gnd != null) return true;
        }
        catch { }
        return false;
    }

    // Map bounds in CELLS, refreshed per pathfind from the CURRENT map.
    // Village_c.mapVillage is the loaded MAP; its width/height count CHIPS and a
    // chip is 2x2 cells, so the playable cell range is 0..2*width-1. Measured:
    // on a 24x24-chip map the collision oracle returns real geometry out to
    // cell (47,47), and the player was observed walking to (44,44).
    private static int _cellW, _cellH;

    private static void ReadMapBounds()
    {
        _cellW = 0; _cellH = 0;
        try
        {
            var map = UiText.Member(_village, "mapVillage");
            if (map == null) return;
            int w = NavProbeShared.ToInt(UiText.Member(map, "width"));
            int h = NavProbeShared.ToInt(UiText.Member(map, "height"));
            if (w > 0) _cellW = w * 2;
            if (h > 0) _cellH = h * 2;
        }
        catch { }
    }

    /// <summary>The ground layer's mask for one CELL. Non-zero means solid.</summary>
    private static int TileMask(int cx, int cy)
    {
        // Out of range reads as SOLID, and getting this wrong is what produced
        // routes straight through walls: GNDAtariCheck answers an out-of-bounds
        // coordinate with 0, which is "fully open", so an off-map query made the
        // whole grid walkable and A* returned a bare Manhattan line ("west 10"
        // across a room the player cannot walk across). A wall the router invents
        // is a detour; open space it invents walks a blind player into furniture.
        if (cx < 0 || cy < 0) return 15;
        if (_cellW > 0 && cx >= _cellW) return 15;
        if (_cellH > 0 && cy >= _cellH) return 15;
        if (cx > 255 || cy > 255) return 15;
        int key = (cx << 16) | cy;
        if (_tileCache.TryGetValue(key, out var v)) return v;
        int mask = 15;
        try { mask = Convert.ToInt32(_gnd.Invoke(_village, new object[] { cx, cy, false })); }
        catch { }
        _tileCache[key] = mask;
        return mask;
    }

    // ── buildings: the OTHER half of collision ──────────────────────────────
    // The game itself never uses one layer alone: player_atari_check collects
    // gnd_atari, bld_atari, evt_atari and tak_atari together. Only the first two
    // are read here; evt_atari and tak_atari are events and treasure, which are
    // targets rather than obstacles.
    //
    // ── BOTH LAYERS, MEASURED AGAINST WHERE THE PLAYER ACTUALLY WALKED ──────
    // Settled 2026-09-03 by a whole-map dump (F6) plus 233 distinct cells the
    // player had stood on. A cell somebody has stood on is walkable no matter
    // what any hypothesis says, so a rule that calls one of them solid is dead
    // on the spot. Seven readings of each layer were scored that way:
    //
    //   GROUND   GNDAtariCheck(cell)  != 0                  0 violations  <-- used
    //            GNDAtariCheck(chip)  & quadrant bit       81 violations
    //   BUILDING BLDAtariCheck(cell)  & 0x0f  != 0          0 violations  <-- used
    //            BLDAtariCheck(cell)  != 0                 79 violations
    //            BLDAtariCheck(chip)  hi nibble & bit     109 violations
    //            BLDAtariCheck(chip)  != 0                130 violations
    //
    // BOTH oracles take CELL coordinates — the player's own map_x/map_y, with no
    // shifting anywhere. Nothing is halved and nothing is doubled.
    //
    // The ground mask is not a chip's four quadrants; it is one CELL's four
    // quarters, and any bit set means the player cannot stand there. The
    // evidence is 544 cells on this map holding a partial mask (3, 5, 10, 12 …)
    // and the player standing on none of them, against 493 fully-solid ones.
    // "Partially solid" is simply solid at the resolution a route is spoken in.
    //
    // The building call returns TWO 4-bit masks packed into two bytes: the high
    // nibble describes the obstacle, the low nibble the part of it that lands on
    // the queried cell. Only the low nibble decides. It is never non-zero where
    // the high nibble is zero, and every one of the 233 walked cells has it
    // zero. Treating the whole return as a boolean — what the code did before —
    // blocked 812 cells instead of 166 and contradicted 79 walked cells.
    //
    // A structural argument said the opposite of all this, and was wrong: a 4-bit
    // mask looked like it had to describe a chip's four quadrants, so both calls
    // looked like chip queries. It describes a cell's four quarters. The
    // arithmetic could not distinguish the two; the walked cells could.
    //
    // `level` is the map's layer_no. Levels 0 and 1 differ on 12 cells of 2304
    // and levels 2 and 3 return 0 everywhere, so the range is small and layer_no
    // sits inside it.
    private static MethodInfo _bld;
    private static bool _bldMissing;
    private static readonly Dictionary<int, int> _bldCache = new Dictionary<int, int>();
    private static int _bldLevel = 1;
    private static bool _bldEnabled = true;
    private static int _gndBlocks, _bldBlocks;

    // ── which `level` carries the geometry — MEASURED PER MAP ───────────────
    // `_bldLevel` used to be the map's own layer_no. That worked on the mansion
    // maps by coincidence: their layer_no is 1, and the building layer answers at
    // levels 0 and 1. Town A's layer_no is **2**, the building layer answers
    // nothing at level 2, and so on the town map BLDAtariCheck reported every
    // cell open. The route to a soldier standing beyond a house read "north 12" —
    // straight through the building — and the log shows why in one line:
    //
    //     blocked by ground 0 / buildings 0, buildings on, level 2
    //
    // Zero blocked by BOTH layers over a fifty-cell search. The houses in a town
    // are TATEMONO, so the ground layer never had them, and the building layer
    // was being asked a level that holds nothing.
    //
    // layer_no is therefore NOT this argument. Rather than swap one assumption
    // for another, the live levels are MEASURED once per map: a coarse grid of
    // sample cells is queried at each level 0..3, and every level that answers
    // anywhere becomes part of the mask. A cell is solid if ANY live level says
    // so. That is the safe direction — a wall the router invents is a detour,
    // open space it invents walks a blind player into a house — and it is
    // provably identical on the map where the rule was validated: levels 0 and 1
    // give the same 166 low-nibble cells there, with zero walked-cell violations.
    //
    // The per-level counts are logged, so which level holds what is visible on
    // every map from now on instead of being assumed.
    private static readonly List<int> _liveLevels = new List<int>();
    private static string _levelsFor;        // map key the sample was taken on

    private static void FindLiveLevels()
    {
        // Static map data, so sample once per map rather than 2304 calls on
        // every press of P.
        var mapKey = Gsd1Names.MapKey(_village);
        if (mapKey != null && mapKey == _levelsFor) return;

        _liveLevels.Clear();
        _levelsFor = mapKey;
        if (_bldMissing || _bld == null) return;

        int w = _cellW > 0 ? _cellW : 96;
        int h = _cellH > 0 ? _cellH : 96;
        int stepX = Math.Max(1, w / 24), stepY = Math.Max(1, h / 24);

        var found = new List<string>();
        for (int level = 0; level <= 3; level++)
        {
            int hits = 0, seen = 0;
            for (int y = 0; y < h; y += stepY)
                for (int x = 0; x < w; x += stepX)
                {
                    try
                    {
                        var args = new object[] { x, y, level, 0, false };
                        int r = NavProbeShared.ToInt(_bld.Invoke(_village, args));
                        seen++;
                        if ((r & 0x0f) != 0) hits++;
                    }
                    catch (Exception e)
                    {
                        _bldMissing = true;
                        Plugin.Logger.LogWarning($"[Nav1] BLDAtariCheck threw at ({x},{y}) level {level}: {e.Message}; buildings ignored.");
                        return;
                    }
                }
            found.Add($"level {level}: {hits}/{seen}");
            if (hits > 0) _liveLevels.Add(level);
        }

        // Log the zero. "No level answers on this map" is a real and important
        // result — it means the map has no building collision at all — and it
        // must not read the same as "the levels were never sampled".
        Plugin.Logger.LogInfo($"[Nav1] building levels sampled on this map: {string.Join(", ", found)} " +
                              $"-> using {(_liveLevels.Count == 0 ? "NONE (no building collision on this map)" : string.Join("+", _liveLevels))} " +
                              $"(the map's own layer_no is {_bldLevel})");
    }

    /// <summary>The building layer's mask for one CELL, over every live level.
    /// Non-zero means solid.</summary>
    private static int BuildingMask(int cx, int cy)
    {
        if (!_bldEnabled || _bldMissing || _bld == null) return 0;
        if (cx < 0 || cy < 0) return 0;
        int key = (cx << 16) | (cy & 0xffff);
        if (_bldCache.TryGetValue(key, out var had)) return had;

        int mask = 0;
        try
        {
            foreach (var level in _liveLevels)
            {
                var args = new object[] { cx, cy, level, 0, false };
                mask |= NavProbeShared.ToInt(_bld.Invoke(_village, args)) & 0x0f;
                if (mask == 0x0f) break;
            }
        }
        catch (Exception e)
        {
            // One failure disables the layer for the session rather than throwing
            // per cell: a half-applied collision layer is worse than none.
            _bldMissing = true;
            Plugin.Logger.LogWarning($"[Nav1] BLDAtariCheck failed at ({cx},{cy}): {e.Message}; buildings ignored.");
        }
        _bldCache[key] = mask;
        return mask;
    }

    /// <summary>Is the cell (sx,sy) blocked, by ground OR building?</summary>
    private static bool Blocked(int sx, int sy)
    {
        if (sx < 0 || sy < 0) return true;
        if (TileMask(sx, sy) != 0) { _gndBlocks++; return true; }
        if (BuildingMask(sx, sy) != 0) { _bldBlocks++; return true; }
        return false;
    }

    // ── A* over the player's own cell grid ─────────────────────────────────
    // Suikoden 1 has FOUR-WAY movement only — the player cannot walk diagonally.
    // An 8-direction search produced routes like "northeast 6, north 2", which
    // describe a move the player physically cannot make; the same route in legal
    // moves is "east 4, north 3". Diagonals belong to Suikoden 2, not here.
    private static readonly int[] DX = { 0, 1, 0, -1 };
    private static readonly int[] DY = { -1, 0, 1, 0 };
    private static readonly string[] DName = { "north", "east", "south", "west" };

    /// <summary>Neighbours of a goal cell, orthogonals first: an object's own
    /// cell is usually solid, so the route ends beside it.</summary>
    private static readonly (int, int)[] Around =
        { (0, 1), (1, 0), (0, -1), (-1, 0), (1, 1), (1, -1), (-1, 1), (-1, -1) };

    private sealed class Node
    {
        public int X, Y, G, F;
        public Node Parent;
    }

    /// <summary>
    /// A* from one cell to another. Returns the cell path, or null.
    /// Diagonals require both orthogonal neighbours to be clear, so a route never
    /// slips through the corner gap between two walls.
    /// </summary>
    private static List<Node> FindPath(int sx, int sy, int gx, int gy, int margin = 24)
    {
        int minX = Math.Max(0, Math.Min(sx, gx) - margin), maxX = Math.Max(sx, gx) + margin;
        int minY = Math.Max(0, Math.Min(sy, gy) - margin), maxY = Math.Max(sy, gy) + margin;

        // Open set ordered by (F, insertion order): exactly the node the old
        // linear scan picked (lowest F, earliest on a tie), so routes are
        // unchanged, without rescanning the whole list on every step.
        var open = new PriorityQueue<Node, (int, int)>();
        int seq = 0;
        var best = new Dictionary<int, Node>();
        int Key(int x, int y) => (x << 16) | y;

        var start = new Node { X = sx, Y = sy, G = 0, F = Heuristic(sx, sy, gx, gy) };
        open.Enqueue(start, (start.F, seq++));
        best[Key(sx, sy)] = start;
        var closed = new HashSet<int>();

        int guard = 0;
        while (open.Count > 0)
        {
            if (++guard > 60000) return null;         // never hang the game thread

            var cur = open.Dequeue();

            if (cur.X == gx && cur.Y == gy) return Rebuild(cur);
            if (!closed.Add(Key(cur.X, cur.Y))) continue;

            for (int d = 0; d < DX.Length; d++)
            {
                int nx = cur.X + DX[d], ny = cur.Y + DY[d];
                if (nx < minX || nx > maxX || ny < minY || ny > maxY) continue;
                if (closed.Contains(Key(nx, ny))) continue;
                if (Blocked(nx, ny)) continue;

                int ng = cur.G + 10;
                if (best.TryGetValue(Key(nx, ny), out var seen) && ng >= seen.G) continue;

                var node = new Node { X = nx, Y = ny, G = ng, F = ng + Heuristic(nx, ny, gx, gy), Parent = cur };
                best[Key(nx, ny)] = node;
                open.Enqueue(node, (node.F, seq++));
            }
        }
        return null;
    }

    // Manhattan, matching four-way movement. (An octile heuristic assumes
    // diagonals are legal and would overestimate here.)
    private static int Heuristic(int x, int y, int gx, int gy)
        => 10 * (Math.Abs(x - gx) + Math.Abs(y - gy));

    private static List<Node> Rebuild(Node n)
    {
        var path = new List<Node>();
        for (var c = n; c != null; c = c.Parent) path.Add(c);
        path.Reverse();
        return path;
    }

    /// <summary>Compress a cell path into spoken runs: "north 5, east 3".</summary>
    private static string Describe(List<Node> path)
    {
        if (path == null || path.Count < 2) return "you are already there";

        var parts = new List<string>();
        int runDir = -1, runLen = 0;
        for (int i = 1; i < path.Count; i++)
        {
            int dx = Math.Sign(path[i].X - path[i - 1].X);
            int dy = Math.Sign(path[i].Y - path[i - 1].Y);
            int dir = -1;
            for (int d = 0; d < DX.Length; d++) if (DX[d] == dx && DY[d] == dy) { dir = d; break; }
            if (dir < 0) continue;

            if (dir == runDir) runLen++;
            else
            {
                if (runLen > 0) parts.Add($"{DName[runDir]} {Steps(runLen)}");
                runDir = dir; runLen = 1;
            }
        }
        if (runLen > 0) parts.Add($"{DName[runDir]} {Steps(runLen)}");
        return string.Join(", ", parts);
    }

    // One grid cell is one walking step. The route used to be reported in
    // "tiles" at half the cell count, which only looked right because the search
    // was running at twice the correct scale; both errors are gone together.
    private static string Steps(int cells) => cells <= 1 ? "1" : cells.ToString();

    // ── Targets ─────────────────────────────────────────────────────────────
    // map_human alone missed story NPCs (character A could not be selected during the
    // intro) because Village_c keeps event characters in a SECOND list,
    // map_event_human. Doors live further out still, on
    // Village_c.effect_work (EVENT_EFFECT) -> v_work (VILLAGE_WORK) -> map_door.
    private enum Category { All = 0, People = 1, Events = 2, Doors = 3, Marks = 4, Objects = 5, Scenery = 6, Count = 7 }

    private static readonly string[] CatNames = { "All", "People", "Events", "Doors", "Places", "Objects", "Scenery" };

    private sealed class Target
    {
        public string Name;
        public int TileX, TileY;
        public Category Cat;
        public int Ordinal;      // the entity's own number, for a stable order
        public string Key;       // slot identity, for learned names
        public string Sig;       // sprite fingerprint, to detect slot reuse

        /// <summary>Every cell worth aiming at. A doorway trigger is a rectangle
        /// and its centre is often the door frame itself, so offering only the
        /// centre reported "no path" for exits that are walkable through.
        /// Null means "just TileX,TileY".</summary>
        public List<int[]> Cells;

        /// <summary>True for a learned exit, which is a guess from having walked
        /// through somewhere; event exits are the game's own data and win.</summary>
        public bool Learned;

        /// <summary>Stable identity, independent of this target's position in
        /// the list. The selection follows THIS, not a list index — see
        /// RefreshTargets.</summary>
        public string Id;
    }

    /// <summary>
    /// A cheap fingerprint of an NPC's appearance. HUMAN carries no character id
    /// (chara_no reads 0), but sprite dimensions, palette and pattern counts do
    /// differ between characters, which is enough to notice that a slot's
    /// occupant has been swapped.
    /// </summary>
    // Largest Suikoden 1 map seen in vil.map_table is 96 tiles on its long side,
    // so a target further than this is a parked/off-map entity, not a destination.
    private const int MaxPlausibleTiles = 150;

    private static readonly string[] SigFields =
        { "width", "height", "pat_kazu", "clut_no", "vram_no", "csr_kazu" };

    private static string Signature(object human)
    {
        try
        {
            var parts = new List<string>(SigFields.Length);
            foreach (var f in SigFields)
                parts.Add(NavProbeShared.ToInt(UiText.Member(human, f)).ToString());
            return string.Join(".", parts);
        }
        catch { return ""; }
    }

    private static readonly List<Target> _targets = new List<Target>();
    private static int _index = -1;
    private static Category _category = Category.All;

    /// <summary>Number targets that end up sharing a name: "soldier role 1 1",
    /// "soldier role 1 2".</summary>
    /// <remarks>
    /// The game's own name table is generous with role names — soldier role 1,
    /// soldier role 2, villager role — and a room can hold several of
    /// the same. Cycling past three targets that all say "soldier role 1" is
    /// no better than three that all say "person". Numbered in list order, which
    /// is stable because the list is sorted by category then entity number, so
    /// the same guard keeps the same number between presses.
    ///
    /// This runs AFTER the sort for that reason, and it deliberately does not
    /// touch a name the player typed themselves: if they called two people
    /// "guard", they meant it.
    /// </remarks>
    private static void NumberRepeatedNames()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in _targets)
        {
            if (string.IsNullOrEmpty(t.Name)) continue;
            counts.TryGetValue(t.Name, out int c);
            counts[t.Name] = c + 1;
        }

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in _targets)
        {
            if (string.IsNullOrEmpty(t.Name)) continue;
            if (counts[t.Name] < 2) continue;
            seen.TryGetValue(t.Name, out int n);
            seen[t.Name] = n + 1;
            t.Name = $"{t.Name} {n + 1}";
        }
    }

    private static void RefreshTargets()
    {
        _targets.Clear();

        AddHumans("map_human", Category.People, "person");

        // map_event_human is NOT the source of story NPCs. It read 0 (not null —
        // the direct field is wired correctly) on the map where five story
        // characters were standing in plain sight; they live in vil_ekp instead,
        // as events carrying a putno and a sprite id. It is left out entirely
        // rather than kept as an empty category.
        AddEvents();

        // Learned exits — the only reliable source of stairs/doorways, since
        // Suikoden 1 exposes no warp list at all (see Gsd1Exits).
        try
        {
            string mapKey = Gsd1Names.MapKey(_village);
            var learned = Gsd1Exits.ForMap(mapKey);
            for (int i = 0; i < learned.Count; i++)
            {
                var e = learned[i];
                // The internal map key is a fingerprint, not a place name, so it
                // is never spoken — "exit 1 to m.31.13.0.40" told the player
                // nothing. A hand-edited label in the file is used when present.
                string to = string.IsNullOrEmpty(e.Label) ? "" : $", {e.Label}";
                _targets.Add(new Target
                {
                    // Named apart from event exits on purpose: the two lists
                    // overlap by nature (walking through an event exit records a
                    // learned one at the same spot), and two entries reading
                    // "exit 2" that go to the same place is worse than one.
                    Name = $"learned exit {i + 1}{to}",
                    TileX = e.X, TileY = e.Y, Cat = Category.Doors, Ordinal = 1000 + i,
                    Learned = true, Id = $"lexit:{e.X},{e.Y}",
                });
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Nav1] learned-exit load failed: {e.Message}"); }

        // Player-marked places (diary, bed, anything the game cannot label).
        try
        {
            string mapKey = Gsd1Names.MapKey(_village);
            var marks = Gsd1Marks.ForMap(mapKey);
            for (int i = 0; i < marks.Count; i++)
            {
                var m = marks[i];
                _targets.Add(new Target
                {
                    Name = string.IsNullOrEmpty(m.Label) ? $"place {i + 1}" : m.Label,
                    TileX = m.X, TileY = m.Y, Cat = Category.Marks, Ordinal = i,
                    Id = $"mark:{m.X},{m.Y}",
                });
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Nav1] mark load failed: {e.Message}"); }

        // Doors from VILLAGE_WORK. Village_c.v_work is a DIRECT field; the old
        // reader looked for it on effect_work, which has no such member, so this
        // read null on every map and the count was never even logged. Read
        // correctly it returns real entries (2 on town A map 3, and
        // MAP.map_door_kazu is non-zero on most maps in the table).
        //
        // DOOR is the animated door SPRITE, not the transition: its map_x/map_y
        // are in a different, smaller coordinate space than the player's tiles
        // (a door at (32,20) on a map the player walks at y=36), so a DOOR is not
        // yet safe to hand to the pathfinder. The transition itself comes from
        // the events above, which carry the player's own coordinates and a
        // destination. Doors are logged here so the two spaces can be correlated;
        // they are deliberately NOT added as targets until that is settled,
        // because a target at the wrong scale walks the player into a wall.
        try
        {
            var vwork = UiText.Member(_village, "v_work");
            var doors = vwork != null ? UiText.Member(vwork, "map_door") : null;
            int dn = NavProbeShared.CountOf(doors);
            int kazu = NavProbeShared.ToInt(UiText.Member(vwork, "map_door_kazu"));
            if (dn != _lastDoorLog) { _lastDoorLog = dn; Plugin.Logger.LogInfo($"[Nav1] map_door: {dn} entries, map_door_kazu={kazu} (not targetable yet — coordinate space unconfirmed)"); }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Nav1] door refresh failed: {e.Message}"); }

        // Scenery (map_tatemono).
        try
        {
            var blds = UiText.Member(_village, "map_tatemono");
            int bn = NavProbeShared.CountOf(blds);
            for (int i = 0; i < Math.Max(bn, 0); i++)
            {
                var b = UiText.Element(blds, i);
                int bx = NavProbeShared.ToInt(UiText.Member(b, "map_x"));
                int by = NavProbeShared.ToInt(UiText.Member(b, "map_y"));
                if (bx < 0 || by < 0 || bx >= 255 || by >= 255) continue;
                // TATEMONO (建物) covers every placed map structure — furniture,
                // scenery and props, not just houses — which is why a single room
                // reports dozens. A live dump settled the last hope for it: the
                // id histogram over 40 entries was 40 DISTINCT ids each appearing
                // once, i.e. id is the entry's own index, not a type code. There
                // is no way to tell a desk from a wall panel here, which is why
                // this is now "Scenery" and interactable objects come from the
                // event table instead. The size is spoken as the only usable hint.
                int sx2 = NavProbeShared.ToInt(UiText.Member(b, "size_x"));
                int sy2 = NavProbeShared.ToInt(UiText.Member(b, "size_y"));
                _targets.Add(new Target
                {
                    Name = $"object {i + 1}, {sx2} by {sy2}",
                    TileX = bx, TileY = by, Cat = Category.Scenery, Ordinal = i,
                    Id = $"scenery:{i}",
                });
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Nav1] building refresh failed: {e.Message}"); }

        // Drop a learned exit that duplicates one the event table already knows.
        // Every event exit the player walks through also gets recorded as a
        // learned exit at the same doorway, so without this every door in a room
        // is announced twice — once with a destination and once without. The
        // event entry is the game's own data and wins; the learned one survives
        // only where nothing static covers that spot, which is exactly the
        // fallback it is meant to be.
        _targets.RemoveAll(t => t.Learned && _targets.Exists(o =>
            !o.Learned && (o.Cat == Category.Doors || o.Cat == Category.Events) &&
            Math.Abs(o.TileX - t.TileX) + Math.Abs(o.TileY - t.TileY) <= 3));

        // Scenery is excluded from "All": it is dozens of nameless wall panels
        // per room and it buried the useful targets. It stays reachable only by
        // selecting that category deliberately.
        if (_category == Category.All) _targets.RemoveAll(t => t.Cat == Category.Scenery);
        else _targets.RemoveAll(t => t.Cat != _category);

        // STABLE order: category, then the entity's own number. Sorting by
        // distance re-ordered the list every time the player moved, so cycling
        // read out as 5, 12, 6, 1 — the numbers looked random. A fixed order
        // means the same key always reaches the same target; distance and
        // bearing are still spoken per item.
        _targets.Sort((a, b) =>
        {
            int c = a.Cat.CompareTo(b.Cat);
            return c != 0 ? c : a.Ordinal.CompareTo(b.Ordinal);
        });

        NumberRepeatedNames();

        // KEEP THE SELECTION ON THE SAME THING.
        //
        // _index is a position in a list that is rebuilt from live game state on
        // every refresh, and its membership genuinely changes: NPCs wander in and
        // out of range, a category filter changes the contents, an event list
        // reloads. Holding the selection as a list position meant the highlight
        // silently slid onto a different target whenever that happened — a person
        // three steps east became a different person twenty steps away, with
        // nothing said about it. The selection now follows the target's own
        // identity and only falls back to a position when that target is really
        // gone.
        if (_targets.Count == 0) { _index = -1; _selectedId = null; return; }

        if (_selectedId != null)
        {
            int found = _targets.FindIndex(t => t.Id == _selectedId);
            if (found >= 0) { _index = found; return; }
            Plugin.Logger.LogInfo($"[Nav1] selected target '{_selectedId}' is gone; keeping position {_index}.");
        }

        if (_index < 0 || _index >= _targets.Count) _index = 0;
        _selectedId = _targets[_index].Id;
    }

    /// <summary>Identity of the selected target, so a rebuild cannot move it.</summary>
    private static string _selectedId;

    /// <summary>Move the cursor and remember WHAT it landed on, not where.</summary>
    private static void Select(int index)
    {
        if (_targets.Count == 0) { _index = -1; _selectedId = null; return; }
        _index = ((index % _targets.Count) + _targets.Count) % _targets.Count;
        _selectedId = _targets[_index].Id;
    }

    private static int _lastDoorLog = int.MinValue;
    private static int _lastDroppedLog = int.MinValue;

    /// <summary>
    /// Targets from Village_c.vil_ekp — the map's own event table.
    /// </summary>
    /// <remarks>
    /// This is what replaces two dead ends at once. Exits stop having to be
    /// walked through before they can be walked to: a VILLAGEOUT command names
    /// its destination map and tile outright, so a doorway is targetable the
    /// first time a player enters a room. And a tap event with no person on it is
    /// an interactable piece of scenery — the save diary is one of these — which
    /// is the thing "Objects" was supposed to be and never was.
    ///
    /// The old Objects category came from map_tatemono, which lists every placed
    /// structure including each wall panel: 56 indistinguishable entries in one
    /// house, none of them interactable, and no way to tell a bed from a wall. It
    /// is kept behind its own category name for now rather than deleted, but it
    /// is no longer where an interactable object comes from.
    ///
    /// Names are learned per EVENT INDEX, not per entity slot. An event's index
    /// in vil_ekp is static map data, so unlike map_event_no — which is a slot
    /// that gets reused when a character joins the party — a name attached to it
    /// stays attached to the same thing forever.
    /// </remarks>
    private static void AddEvents()
    {
        try
        {
            string mapKey = Gsd1Names.MapKey(_village);
            if (mapKey == null) return;      // mid-load; nothing readable to add

            var events = Gsd1Events.ForCurrentMap(_village, mapKey);
            int exitNo = 0, objNo = 0, trigNo = 0, doorNo = 0;

            int dropped = 0;
            foreach (var e in events)
            {
                // Dead events — a cutscene that has played, a fireplace that no
                // longer responds — are decided by the story-flag rule in
                // Gsd1Flags, and the decision is made HERE rather than in the
                // event cache because flags change while a map stays loaded and
                // the cache does not. While Gsd1Flags.DropDeadEvents is false
                // this loop is a no-op and the report lives in the log.
                if (Gsd1Flags.DropDeadEvents && Gsd1Flags.CanFireAny(e.Alternatives) == false)
                { dropped++; continue; }

                Category cat;
                string fallback;

                // A service point names itself. EVENTSHOP's operand is one of
                // G1_event_h's SHOP_* constants, whose values were read at
                // runtime, and the save diary's script ends with exactly
                // "EVENTSHOP SHOP_SAVE". So a save point, an inn or a shop is
                // named from static map data — on maps the player has never
                // visited, without interacting with it once.
                string service = Gsd1Events.ServiceLabel(e);

                switch (e.Class)
                {
                    case Gsd1Events.Kind.Exit:
                        cat = Category.Doors;
                        exitNo++;
                        // Destination map 254/255 with a 253,253 tile is the
                        // sentinel for leaving the area altogether rather than a
                        // real map index, so it is not read out as one.
                        fallback = e.WarpMap >= 254
                            ? $"exit {exitNo}, leaving the area"
                            : $"exit {exitNo} to map {e.WarpMap}";
                        break;
                    case Gsd1Events.Kind.Object:
                        cat = Category.Objects;
                        fallback = service ?? $"object {++objNo}";
                        break;
                    default:
                        // A region trigger that requires a button press is a
                        // place the player ACTS, not a cutscene they walk into,
                        // and on the town map there are twelve of them: three
                        // cells wide, one deep, scattered across the plaza, in
                        // pairs, each sitting exactly where a building front is.
                        // The player was standing two cells from one of them.
                        // That is a building entrance, so it belongs under Doors
                        // where somebody looking for the armoury will find it.
                        //
                        // INFERRED FROM SHAPE AND POSITION, not decoded — their
                        // scripts stop at an opcode of unknown width. If they
                        // turn out to be signs rather than doors this is one line
                        // to move back, and the name is overridable with Shift+K.
                        bool doorish = e.HasPushKey && e.HasRegion;
                        cat = doorish ? Category.Doors : Category.Events;
                        fallback = service
                                ?? (doorish ? $"doorway {++doorNo}" : $"trigger {++trigNo}");
                        break;
                }

                string key = $"{mapKey}:ev:{e.Index}";
                _targets.Add(new Target
                {
                    Name = Gsd1Names.Get(key, "") ?? fallback,
                    TileX = e.TileX, TileY = e.TileY,
                    Cat = cat, Ordinal = e.Index, Key = key, Sig = "",
                    Cells = e.Cells != null && e.Cells.Count > 0 ? e.Cells : null,
                    Id = $"ev:{e.Index}",
                });
            }

            // Log the zero: "dropped none" and "never filtered" must not look
            // the same. Only when the count changes, so this cannot flood.
            if (dropped != _lastDroppedLog)
            {
                _lastDroppedLog = dropped;
                Plugin.Logger.LogInfo($"[Nav1] dead-event filter: {dropped} of {events.Count} places dropped " +
                                      $"(DropDeadEvents={Gsd1Flags.DropDeadEvents})");
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Nav1] event refresh failed: {e.Message}"); }
    }

    /// <summary>The People targets, named exactly as the player hears them.</summary>
    /// <remarks>
    /// The identity questionnaire (Gsd1Probe, F5) has to name each person the
    /// same way the target list does, or the player cannot tell which log line
    /// is about which target. Rebuilding it here from live state is the only way
    /// to guarantee that: the label is produced by the same code path, numbering
    /// and provisional prefix included.
    /// </remarks>
    public static List<string[]> PeopleSnapshot()
    {
        var rows = new List<string[]>();
        try
        {
            var savedCat = _category;
            _category = Category.All;
            RefreshTargets();
            foreach (var t in _targets)
                if (t.Cat == Category.People)
                    rows.Add(new[] { t.Name, t.TileX.ToString(), t.TileY.ToString(), t.Id ?? "", t.Key ?? "" });

            // Put the list back the way the player left it. A diagnostic must
            // not change what the next keypress reads out.
            _category = savedCat;
            RefreshTargets();
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Nav1] people snapshot failed: {e.Message}"); }
        return rows;
    }

    private static void AddHumans(string listName, Category cat, string label)
    {
        try
        {
            var humans = UiText.Member(_village, listName);
            int n = NavProbeShared.CountOf(humans);
            for (int i = 0; i < Math.Max(n, 0); i++)
            {
                var h = UiText.Element(humans, i);
                int hx = NavProbeShared.ToInt(UiText.Member(h, "map_x"));
                int hy = NavProbeShared.ToInt(UiText.Member(h, "map_y"));

                // 255,255 is the parked/unused slot marker seen in the dumps.
                if (hx < 0 || hy < 0 || hx >= 255 || hy >= 255) continue;

                // A duplicate "character A" once showed up 382 tiles away and reported
                // "no path": once a character joins the party their entity is
                // parked away from the walkable map rather than removed. No real
                // Suikoden 1 map exceeds ~96 tiles a side, so anything this far
                // is not a place the player can walk to.
                if (Math.Abs(hx - PlayerTileX()) + Math.Abs(hy - PlayerTileY()) > MaxPlausibleTiles) continue;

                int ev = NavProbeShared.ToInt(UiText.Member(h, "map_event_no"));

                // Three name sources, in falling order of authority:
                //   1. what the player typed with Shift+K   (learned, persisted)
                //   2. the game's own character name         (static, never saved)
                //   3. "person 11"                           (the ordinal)
                // The player's own name always wins: they confirmed it by typing
                // it, and the game's table cannot name an ambient townsperson.
                string key = $"{Gsd1Names.MapKey(_village)}:{listName}:{ev}";

                // The slot-reuse guard now uses the sprite id from the matching
                // event (EVENT_WK.image, keyed by putno == map_event_no) instead
                // of the sprite-dimension fingerprint, which read 0.0.0.0.0.0 for
                // every NPC in play and so could never detect a changed occupant.
                // Falls back to the old fingerprint when no event carries this
                // slot, so nothing regresses on maps without an event table.
                int img = Gsd1Events.ImageForPutNo(ev);
                string sig = img > 0 ? "img" + img : Signature(h);
                string learned = Gsd1Names.Get(key, sig);

                // "possible character C", not "character C", for the band where a named
                // character and a generic person sprite share one id. The name
                // is still spoken so the player can find that target and go and
                // check who it is — which is exactly the observation that will
                // settle the band — but it is spoken as a question, because a
                // wrong name asserted as fact is worse than no name. See
                // Gsd1GameNames.IsAmbiguousBand; this is a hold, not a rule.
                bool provisional = false;
                string given = img > 0 ? Gsd1GameNames.ForImage(img, out provisional) : null;
                if (given != null && provisional) given = "possible " + given;

                _targets.Add(new Target
                {
                    Name = learned ?? given ?? $"{label} {ev}",
                    TileX = hx, TileY = hy, Cat = cat, Ordinal = ev, Key = key, Sig = sig,
                    Id = $"{listName}:{ev}",
                });
            }
            if (n > 0) Plugin.Logger.LogInfo($"[Nav1] {listName}: {n} entries");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Nav1] {listName} refresh failed: {e.Message}"); }
    }

    /// <summary>
    /// The save screen just opened. Name the event that opened it "save point".
    /// </summary>
    /// <remarks>
    /// This is the answer to "can a save point be named automatically", and it
    /// is a different kind of inference from the dialogue-speaker attribution
    /// that had to be removed. That one guessed WHICH of several nearby NPCs was
    /// talking and got it wrong (a party follower speaking got character B
    /// permanently labelled "character A"). Here there is nothing to guess: exactly one
    /// event is running, the game itself has just opened the save UI, and
    /// Event_c.eventWK says which event that was. The evidence IS the outcome.
    ///
    /// Two guards keep it honest, because Exec() also runs for LOADING from the
    /// title screen, where eventWK holds whatever was last set:
    ///   * the event must match one of the CURRENT map's tap objects by position
    ///   * the player must be standing within a few cells of it
    /// A save screen reached any other way matches nothing and names nothing.
    ///
    /// The name is written to the shared names file, so a save point stays named
    /// across sessions and can be handed to another player. The static route —
    /// EVENTSHOP with G1_event_h.SHOP_SAVE, which would name save points on maps
    /// nobody has visited — needs operand widths that cannot be recovered from
    /// the data sampled so far; see Gsd1Events.
    /// </remarks>
    public static void NoteSaveScreenOpened()
    {
        try
        {
            if (_village == null) return;
            string mapKey = Gsd1Names.MapKey(_village);
            if (mapKey == null) return;

            var ec = StaticMember(_village.GetType(), "event_c");
            var wk = UiText.Member(ec, "eventWK");
            if (wk == null) return;
            int ex = NavProbeShared.ToInt(UiText.Member(wk, "x"));
            int ey = NavProbeShared.ToInt(UiText.Member(wk, "y"));

            int px = PlayerTileX(), py = PlayerTileY();
            var events = Gsd1Events.ForCurrentMap(_village, mapKey);
            foreach (var e in events)
            {
                if (e.Class != Gsd1Events.Kind.Object) continue;
                if (e.TileX != ex || e.TileY != ey) continue;
                if (Math.Abs(e.TileX - px) + Math.Abs(e.TileY - py) > 4) continue;

                string key = $"{mapKey}:ev:{e.Index}";
                if (Gsd1Names.Get(key, "") != null) return;      // already named
                Gsd1Names.Set(key, "save point", "");
                Plugin.Logger.LogInfo($"[Nav1] save screen opened from event {e.Index} at " +
                                      $"({ex},{ey}); named 'save point'. {Gsd1Events.Describe(e)}");
                RefreshTargets();
                return;
            }
            Plugin.Logger.LogInfo($"[Nav1] save screen opened but eventWK ({ex},{ey}) matches no " +
                                  $"tap object near the player ({px},{py}) — nothing named.");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Nav1] save-point naming failed: {e.Message}"); }
    }

    private static object StaticMember(Type t, string name)
    {
        try
        {
            const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            return t.GetProperty(name, BF)?.GetValue(null) ?? t.GetField(name, BF)?.GetValue(null);
        }
        catch { return null; }
    }

    private static int PlayerTileX()
    {
        try { return NavProbeShared.ToInt(UiText.Member(UiText.Member(_village, "player"), "map_x")); }
        catch { return 0; }
    }

    private static int PlayerTileY()
    {
        try { return NavProbeShared.ToInt(UiText.Member(UiText.Member(_village, "player"), "map_y")); }
        catch { return 0; }
    }

    private static string Bearing(int fromX, int fromY, int toX, int toY)
    {
        int dx = toX - fromX, dy = toY - fromY;
        int dist = Math.Abs(dx) + Math.Abs(dy);
        string ns = dy < 0 ? "north" : dy > 0 ? "south" : "";
        string ew = dx > 0 ? "east" : dx < 0 ? "west" : "";
        string dir = (ns + ew) == "" ? "here" : ns + ew;
        return $"{dir}, {dist} tiles";
    }

    /// <summary>Most recent dialogue speaker, for user-triggered naming.</summary>
    private static string _lastSpeaker;

    /// <summary>
    /// Record a dialogue speaker. This deliberately does NOT assign the name to
    /// anybody by itself.
    ///
    /// An earlier version attributed the speaker to the nearest NPC automatically,
    /// and it produced WRONG names: party followers walk beside the player and
    /// speak, so when character A (following the player) spoke during a conversation with
    /// character B, character B was labelled "character A". Cutscenes with several speakers have
    /// the same problem, and there is no "who is currently talking" signal in
    /// GSD1 to disambiguate. Since these names persist to a file players may
    /// share, a wrong name is worse than no name — so the player now confirms
    /// each one with Shift+K.
    /// </summary>
    public static void NoteSpeaker(string speaker)
    {
        if (string.IsNullOrWhiteSpace(speaker)) return;
        speaker = speaker.Trim().TrimEnd(':', ' ');
        if (speaker.Length > 0 && speaker.Length <= 24) _lastSpeaker = speaker;
    }

    /// <summary>Shift+K — name the SELECTED target after the last speaker heard.</summary>
    private static void NameSelected()
    {
        if (_index < 0 || _index >= _targets.Count) { Narration.Label("No target selected"); return; }
        var t = _targets[_index];
        if (t.Key == null) { Narration.Label("That target cannot be named"); return; }

        // Typed by ear rather than taken from the last speaker heard: attributing
        // the last speaker automatically is what mislabelled character B as
        // "character A", because party followers speak too. The player types the name, so
        // it is always the right one.
        var key = t.Key; var sig = t.Sig;
        Gsd1TextInput.Open($"Naming {t.Name}", name =>
        {
            Gsd1Names.Set(key, name, sig);
            RefreshTargets();
        });
    }

    private static void SpeakCurrent()
    {
        if (_index < 0 || _index >= _targets.Count) { Narration.Label("No targets"); return; }
        var t = _targets[_index];
        Narration.Label($"{t.Name}, {Bearing(PlayerTileX(), PlayerTileY(), t.TileX, t.TileY)}, " +
                        $"{_index + 1} of {_targets.Count}");
    }

    private static void Pathfind()
    {
        if (_index < 0 || _index >= _targets.Count) { Narration.Label("No target selected"); return; }
        var t = _targets[_index];

        // Fresh collision reads per request, so a map change can never leave
        // stale walkability behind.
        _tileCache.Clear();
        _bldCache.Clear();
        _gndBlocks = 0; _bldBlocks = 0;
        ReadMapBounds();
        _bldEnabled = true;
        _bldLevel = NavProbeShared.ToInt(
            UiText.Member(UiText.Member(_village, "mapVillage"), "layer_no"));
        FindLiveLevels();

        // SELF-CHECK, and it is the reason the building layer is safe to switch
        // on without a play session first: whatever else is true, the player is
        // standing somewhere walkable. If the combined collision says otherwise
        // then BLDAtariCheck is being asked the wrong question — wrong level,
        // wrong coordinate space, wrong sense of its return value — and the
        // honest response is to drop back to ground-only for this request and
        // say so, rather than report "no path" from a corrupt grid.
        if (Blocked(PlayerTileX(), PlayerTileY()))
        {
            _bldEnabled = false;
            _bldCache.Clear();
            bool stillBlocked = Blocked(PlayerTileX(), PlayerTileY());
            Plugin.Logger.LogWarning(
                $"[Nav1] the player's own cell ({PlayerTileX()},{PlayerTileY()}) reads BLOCKED " +
                $"(level={_bldLevel}); buildings disabled for this route. " +
                $"Ground-only still blocked: {stillBlocked}.");
        }

        int px = PlayerTileX(), py = PlayerTileY();

        // HUMAN.map_x / map_y ARE the search grid. They are not chip coordinates
        // to be doubled — see the note on the coordinate scale at the top.
        int sx = px, sy = py;

        // Try every cell the target offers, nearest first, and for each one the
        // eight cells around it. A doorway trigger is a rectangle whose centre
        // is often the door frame, and an object's own cell is usually solid
        // because it IS the furniture — so a single-goal search reported "no
        // path" for things standing right in front of the player. Trying the
        // rectangle turns most of those into routes, and the ones that remain
        // are genuinely unreachable rather than merely badly aimed.
        var goals = new List<int[]>();
        if (t.Cells != null && t.Cells.Count > 0) goals.AddRange(t.Cells);
        else goals.Add(new[] { t.TileX, t.TileY });
        goals.Sort((a, b) =>
            (Math.Abs(a[0] - px) + Math.Abs(a[1] - py)).CompareTo(
             Math.Abs(b[0] - px) + Math.Abs(b[1] - py)));

        List<Node> path = null;
        int triedCells = 0;
        foreach (var g in goals)
        {
            triedCells++;
            path = FindPath(sx, sy, g[0], g[1]);
            if (path != null) break;
            foreach (var (ox, oy) in Around)
            {
                path = FindPath(sx, sy, g[0] + ox, g[1] + oy);
                if (path != null) break;
            }
            if (path != null) break;
        }

        if (path == null)
        {
            Narration.Label($"No path to {t.Name}. {Bearing(px, py, t.TileX, t.TileY)}");
            Plugin.Logger.LogInfo($"[Nav1] pathfind FAILED to {t.Name} ({t.TileX},{t.TileY}) from " +
                                  $"({px},{py}) after {triedCells} candidate cell(s), map {_cellW}x{_cellH} cells, " +
                                  $"blocked by ground {_gndBlocks} / buildings {_bldBlocks} " +
                                  $"(buildings {(_bldEnabled ? "on" : "OFF")}, level {_bldLevel})");
            return;
        }

        string route = Describe(path);
        Plugin.Logger.LogInfo($"[Nav1] path to {t.Name}: {path.Count} cells -> {route} " +
                              $"[blocked by ground {_gndBlocks} / buildings {_bldBlocks}, " +
                              $"buildings {(_bldEnabled ? "on" : "OFF")}, level {_bldLevel}]");

        // Steps only. K already announces which target is selected, so repeating
        // the name on every P made the useful part — the directions — arrive late
        // and read as noise while walking a route step by step.
        Narration.Label(route);
    }

    // ── the live half of the story-flag proof ───────────────────────────────
    // Event_c.eventWK is whichever EVENT_WK the game is currently running. An
    // event that RUNS has fired, and that is an observation no reading of the
    // flag banks can reinterpret — the §1.9 move. Gsd1Flags scores its rule
    // against it and logs CONFIRMS or CONTRADICTED; nothing is spoken and
    // nothing is dropped on the strength of it.
    //
    // Field reads only, five times a second: eventWK stays set for as long as
    // the event runs, so nothing is missed by not polling every frame.
    private static float _nextEventWatch;
    private static string _questionnaireFor, _questionnairePending;
    private static float _questionnairePendingAt;

    private static void WatchRunningEvent()
    {
        if (Time.realtimeSinceStartup < _nextEventWatch) return;
        _nextEventWatch = Time.realtimeSinceStartup + 0.2f;

        string mapKey = Gsd1Names.MapKey(_village);
        if (mapKey == null) return;

        var ec = StaticMember(_village.GetType(), "event_c");
        var wk = ec != null ? UiText.Member(ec, "eventWK") : null;
        if (wk == null) return;

        Gsd1Flags.NoteRunningEvent(
            mapKey,
            NavProbeShared.ToInt(UiText.Member(wk, "x")),
            NavProbeShared.ToInt(UiText.Member(wk, "y")),
            NavProbeShared.ToInt(UiText.Member(wk, "putno")),
            NavProbeShared.ToInt(UiText.Member(wk, "image")),
            NavProbeShared.ToInt(UiText.Member(ec, "eventNo")),
            Gsd1Events.ForCurrentMap(_village, mapKey));
    }

    // ── Input ───────────────────────────────────────────────────────────────
    private static bool _j, _k, _l, _p, _m, _n;

    /// <summary>M — record the player's current tile, naming it by ear.</summary>
    private static void MarkHere()
    {
        string map = Gsd1Names.MapKey(_village);
        if (string.IsNullOrEmpty(map)) { Narration.Label("Cannot mark here yet"); return; }

        int x = PlayerTileX(), y = PlayerTileY();
        Gsd1TextInput.Open("Naming this place", name =>
        {
            Gsd1Marks.Add(map, x, y, name);
            RefreshTargets();
        });
    }

    public static void Tick(object village)
    {
        if (!Enabled || village == null) return;
        if (!Bind(village)) return;

        // Watch for map transitions so stairs and doorways become known targets.
        try { Gsd1Exits.Observe(Gsd1Names.MapKey(village), PlayerTileX(), PlayerTileY()); }
        catch { }

        // Where the player is, and whether a name for it has been learned.
        try { Gsd1Place.Tick(village); } catch { }

        // The live half of the story-flag proof. Cheap field reads, throttled.
        try { WatchRunningEvent(); } catch { }

        // The NPC identity questionnaire, once per map. See Gsd1Probe: it used
        // to need F5, and a whole session of play produced no rows because the
        // one measurement that settles the name band was the one that had to be
        // remembered.
        try
        {
            string mk = Gsd1Names.MapKey(village);
            if (mk != null && mk != _questionnaireFor)
            {
                // Settle first. The key appears before map_human is finished
                // filling, and a questionnaire taken on the first frame would
                // list half a room and then never run again for that map.
                if (mk != _questionnairePending)
                { _questionnairePending = mk; _questionnairePendingAt = Time.realtimeSinceStartup; }
                else if (Time.realtimeSinceStartup - _questionnairePendingAt > 1.5f)
                {
                    _questionnaireFor = mk;
                    Gsd1Probe.Questionnaire(village, mk);
                }
            }
        }
        catch { }

        // Text entry owns the keyboard while open, so navigation keys must not
        // also fire — otherwise typing a name would cycle targets underneath it.
        if (Gsd1TextInput.Tick()) return;

        try
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            bool shift = kb[Key.LeftShift].isPressed || kb[Key.RightShift].isPressed;

            bool j = kb[Key.J].isPressed;
            bool k = kb[Key.K].isPressed;
            bool l = kb[Key.L].isPressed;
            bool p = kb[Key.P].isPressed;

            if (j && !_j)
            {
                if (shift) CycleCategory(-1);
                else { RefreshTargets(); Select(_index - 1); SpeakCurrent(); }
            }
            if (l && !_l)
            {
                if (shift) CycleCategory(1);
                else { RefreshTargets(); Select(_index + 1); SpeakCurrent(); }
            }
            if (k && !_k)
            {
                RefreshTargets();
                if (shift) NameSelected();
                else SpeakCurrent();
            }
            if (p && !_p) { RefreshTargets(); Pathfind(); }

            // M — mark where the player is standing as a permanent landmark.
            // This is a DESTINATION, not a routing shortcut: A* still finds the
            // real path to it. It exists because some essential objects (the
            // diary desk that saves the game, a bed) are indistinguishable inside
            // map_tatemono, which carries no interactable flag.
            bool m = kb[Key.M].isPressed;
            if (m && !_m) { if (shift) Gsd1Place.Announce(); else MarkHere(); }
            _m = m;

            // N — name the room the player is standing in, by ear. Separate from
            // M because they are different things: M records a TILE to walk back
            // to, N names the MAP itself, which is what Shift+M reads out.
            bool nk = kb[Key.N].isPressed;
            if (nk && !_n) Gsd1Place.NameHere(_village);
            _n = nk;

            _j = j; _k = k; _l = l; _p = p;
        }
        catch { }
    }

    private static void CycleCategory(int delta)
    {
        int c = ((int)_category + delta + (int)Category.Count) % (int)Category.Count;
        _category = (Category)c;
        // A new category is a new list, so the remembered identity does not
        // belong to it; start at the top deliberately rather than letting the
        // stale id decide.
        _index = 0;
        _selectedId = null;
        RefreshTargets();
        Narration.Label($"{CatNames[(int)_category]}: {_targets.Count} targets");
    }
}
