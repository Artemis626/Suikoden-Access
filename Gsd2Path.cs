using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SuikodenAccess;

// ─────────────────────────────────────────────────────────────────────────────
// GSD2 PATHFINDING — Suikoden 2. TRUE A*, built on the rule measured 2026-09-29
// (handoff §12–§13). Suikoden 1's Gsd1Path is the model.
//
// THE COLLISION RULE, and what each half was scored against
//
//   blocked(x, y) = Func1(x, y) || objectMask(x, y) || person(x, y)
//
//   Func1       EVENTCON.EventMapAtariCheckFunc1(0, 0, ref RECT{8x, 8y, 8, 8})
//               != 0. The map's TERRAIN, for ANY cell - the GSD2 counterpart of
//               GSD1's GNDAtariCheck. dir/w/h make no difference; the answer is
//               about the cell containing (rec.x, rec.y). 0 inconsistencies
//               across nine overlapping 21x21 windows.
//   objectMask  each TATEMONO's pattern atari_data, a per-8x8-cell bitmask,
//               rows counted UP from the object's base: cell (map_x + c,
//               map_y - r). The GSD2 counterpart of BLDAtariCheck. The x anchor
//               is ambiguous by one cell, so BOTH anchors are used - the union
//               is still walk-clean and is what explained every refusal.
//   person      displayed people are the ENTITY channel (§11.2): Func1 does not
//               see them. Blocked from their live positions; footprint per
//               §11.2, widened by the paired otyp=4's height.
//
//   Scored on the camp: 0 of 648 walked cells called blocked; 50 of 50 cardinal
//   terrain refusals explained. The player's footprint is ONE cell.
//
// MOVEMENT is 8-way, measured: a diagonal is refused iff its destination is
// blocked or BOTH side cells are. The engine lets the player cut past a single
// solid corner (59 observed), so the search does too - refusing it would make
// routes longer than the ones the player can actually walk.
//
// SAFETY. Func1 is CALLED, never patched (approved 2026-09-28). It writes
// movement state (currentMoveDir / wallMoveDir / isMoveChanged) but never the
// position; the next frame recomputes it. Calls happen only on P, are cached
// per request, and Gsd2Walk is quarantined afterwards so the write can never
// become a [BLOCKED] row.
//
// SELF-CHECK, as in Suikoden 1: the player is standing somewhere walkable, so if
// the rule calls the player's own cell blocked, a layer is wrong for this map.
// The object layer is dropped for that request and the fact is logged; if Func1
// itself says so, there is no route rather than a route through a wall.
//
// GOAL. A route ends on a free cell BESIDE the target (any cell touching a
// person's footprint, or an object's cell), found by one multi-goal search so it
// is the nearest such cell by route, and ends with the way to face.
//
// UNITS. One grid cell is px - EVENT_POS/8, half a tile - which is the grid the
// engine itself steps on (isOnGrid / isCheckNextGrid). Routes are spoken in
// those cells as "steps".
//
// KEYS (as Suikoden 1)
//   J / L        previous / next target        K        read current target
//   Shift+J / L  cycle category                P        pathfind, speak route
//   Shift+K      name the selected target (typed; kept in suikoden2_npc_names.txt)
//
// TARGETS are the DISPLAYED entities only (disp=1). Names are not readable from
// the game (see RefreshTargets), so a target is what the player named it with
// Shift+K (Gsd2Names), else "person 1..n", numbered nearest first.
// ─────────────────────────────────────────────────────────────────────────────
internal static class Gsd2Path
{
    public static bool Enabled = true;

    private const BindingFlags ANY = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private static object _ec;

    // ── collision: Func1 ────────────────────────────────────────────────────
    private static MethodInfo _f1;
    private static Type _rectType;
    private static FieldInfo _rx, _ry, _rw, _rh;
    private static readonly Dictionary<int, bool> _terrain = new Dictionary<int, bool>();
    private static int _f1Calls, _f1Throws;
    private static string _f1FirstThrow;

    private static bool BindFunc1(object ec)
    {
        if (_f1 != null) return true;
        for (var t = ec.GetType(); t != null && _f1 == null; t = t.BaseType)
            foreach (var m in t.GetMethods(ANY | BindingFlags.DeclaredOnly))
                if (m.Name == "EventMapAtariCheckFunc1" && m.GetParameters().Length == 3 && m.GetParameters()[2].ParameterType.IsByRef)
                { _f1 = m; break; }
        if (_f1 == null) { Plugin.Logger.LogWarning("[Nav2] EventMapAtariCheckFunc1(int,int,ref RECT) not found - no terrain."); return false; }
        _rectType = _f1.GetParameters()[2].ParameterType.GetElementType();
        _rx = _rectType.GetField("x"); _ry = _rectType.GetField("y"); _rw = _rectType.GetField("w"); _rh = _rectType.GetField("h");
        if (_rx == null || _ry == null || _rw == null || _rh == null)
        { Plugin.Logger.LogWarning($"[Nav2] {_rectType.FullName} lacks x/y/w/h - no terrain."); _f1 = null; return false; }
        return true;
    }

    private static bool Terrain(int x, int y)
    {
        int key = (x << 16) | (y & 0xffff);
        if (_terrain.TryGetValue(key, out bool b)) return b;
        var r = Activator.CreateInstance(_rectType);
        _rx.SetValue(r, 8 * x); _ry.SetValue(r, 8 * y); _rw.SetValue(r, 8); _rh.SetValue(r, 8);
        _f1Calls++;
        try { b = Convert.ToInt32(_f1.Invoke(_ec, new object[] { 0, 0, r })) != 0; }
        catch (Exception e)
        {
            // An unanswerable cell is SOLID: open space the router invents walks
            // a blind player into a wall.
            _f1Throws++; _f1FirstThrow ??= (e.InnerException ?? e).Message; b = true;
        }
        _terrain[key] = b;
        return b;
    }

    // ── collision: object masks (per map, static) ───────────────────────────
    private static string _maskFor;
    private static readonly HashSet<int> _objMask = new HashSet<int>();
    private static int _maskObjects, _maskUnresolved;

    private static void BuildObjectMask(object ec, string mapKey)
    {
        if (_maskFor == mapKey) return;
        _maskFor = mapKey; _objMask.Clear(); _maskObjects = 0; _maskUnresolved = 0;
        try
        {
            var mdat = Val(ec, "mdat");
            var arr = Val(mdat, "map");
            int cnt = NavProbeShared.CountOf(arr), mno = Get(ec, "mno");
            var map = UiText.Element(arr, (mno >= 0 && mno < cnt) ? mno : 0);
            var tds = Val(map, "tatemono_data");
            var opd = Val(map, "machi_opd");
            var anim = Val(opd, "anime_table");
            var pats = Val(opd, "pattern_table");
            int n = NavProbeShared.CountOf(tds);
            for (int i = 0; i < Math.Max(n, 0); i++)
            {
                var td = UiText.Element(tds, i);
                int mx = Get(td, "map_x"), my = Get(td, "map_y");
                object ptn = null;
                try
                {
                    var an = UiText.Element(anim, Get(td, "anime_no"));
                    var tim = UiText.Element(Val(an, "timer_table"), 0);
                    ptn = UiText.Element(pats, Get(tim, "pattern_no"));
                }
                catch { }
                if (ptn == null || mx == int.MinValue) { _maskUnresolved++; continue; }
                int cw = Get(ptn, "size_x") / 8, ch = Get(ptn, "size_y") / 8;
                var ad = Val(ptn, "atari_data");
                int adn = NavProbeShared.CountOf(ad);
                if (cw <= 0 || ch <= 0 || adn <= 0) { _maskUnresolved++; continue; }
                var bytes = new int[adn];
                for (int j = 0; j < adn; j++) bytes[j] = NavProbeShared.ToInt(UiText.Element(ad, j)) & 0xff;
                _maskObjects++;
                for (int r = 0; r < ch; r++)
                    for (int c = 0; c < cw; c++)
                    {
                        int bit = r * cw + c;
                        if (bit / 8 >= adn) continue;
                        if (((bytes[bit / 8] >> (7 - bit % 8)) & 1) == 0) continue;
                        int cx = mx + c, cy = my - r;
                        _objMask.Add(Key(cx, cy));
                        _objMask.Add(Key(cx + 1, cy));   // the other x anchor (§12.1)
                    }
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Nav2] object mask failed on {mapKey}: {e.Message}"); }
        Plugin.Logger.LogInfo($"[Nav2] object mask for {mapKey}: {_objMask.Count} cells from {_maskObjects} objects ({_maskUnresolved} unresolved).");
    }

    // ── collision: people (live) ────────────────────────────────────────────
    private static readonly HashSet<int> _people = new HashSet<int>();

    private static void BuildPeople(object ec)
    {
        _people.Clear();
        var eh = Val(ec, "ehuman");
        int n = NavProbeShared.CountOf(eh);
        for (int i = 1; i < Math.Max(n, 0); i++) AddFootprint(eh, i, _people);
    }

    // The cells a displayed person occupies, for collision and as the thing a
    // route must end BESIDE. Hidden (disp=0) slots are not on the map: the
    // phantom 'character N' at (47,85) in the 2026-09-29 test was one.
    //   otyp=1  its own cell.
    //   otyp=2  one cell either side on its row (§11.2), and the rows below as
    //           tall as its otyp=4 partner (the (56,64) person with a 16x16
    //           partner also blocks the row below).
    private static void AddFootprint(object eh, int i, HashSet<int> into)
    {
        var h = UiText.Element(eh, i);
        if (h == null || Get(h, "disp") != 1) return;
        int otyp = Get(h, "otyp");
        if (otyp != 1 && otyp != 2) return;
        int x = PosPx(Val(h, "x")), y = PosPx(Val(h, "y"));
        if (x == int.MinValue || IsParked(x, y)) return;
        if (otyp == 1) { into.Add(Key(x, y)); return; }
        int rows = 1;
        var partner = UiText.Element(eh, i + 1);
        if (partner != null && Get(partner, "otyp") == 4) rows = Math.Max(1, Get(partner, "h") / 8);
        for (int dy = 0; dy < rows; dy++)
            for (int dx = -1; dx <= 1; dx++) into.Add(Key(x + dx, y + dy));
    }

    // ── the rule ────────────────────────────────────────────────────────────
    private static int _mapW = 120, _mapH = 120;
    private static bool _useObjects = true;
    private static int _blkTerrain, _blkObject, _blkPeople;

    private static bool Blocked(int x, int y)
    {
        if (x < 0 || y < 0 || x >= _mapW || y >= _mapH) return true;
        int k = Key(x, y);
        if (_people.Contains(k)) { _blkPeople++; return true; }
        if (_useObjects && _objMask.Contains(k)) { _blkObject++; return true; }
        if (Terrain(x, y)) { _blkTerrain++; return true; }
        return false;
    }

    private static int Key(int x, int y) => (x << 16) | (y & 0xffff);

    // ── A* ──────────────────────────────────────────────────────────────────
    // 0=N 1=NE 2=E 3=SE 4=S 5=SW 6=W 7=NW
    private static readonly int[] DX = { 0, 1, 1, 1, 0, -1, -1, -1 };
    private static readonly int[] DY = { -1, -1, 0, 1, 1, 1, 0, -1 };
    private static readonly string[] DName = { "north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest" };

    private sealed class Node { public int X, Y, G, F; public Node Parent; }

    // ONE search to the nearest of several goal cells: the shortest route to any
    // cell beside the target, not the first goal (by straight-line distance) that
    // happens to be reachable.
    private static List<Node> FindPath(int sx, int sy, List<(int X, int Y)> goals, int margin = 40)
    {
        var goalKeys = new HashSet<int>();
        int minX = sx, maxX = sx, minY = sy, maxY = sy;
        foreach (var (gx, gy) in goals)
        {
            if (Blocked(gx, gy)) continue;
            goalKeys.Add(Key(gx, gy));
            minX = Math.Min(minX, gx); maxX = Math.Max(maxX, gx);
            minY = Math.Min(minY, gy); maxY = Math.Max(maxY, gy);
        }
        if (goalKeys.Count == 0) return null;
        var live = goals.FindAll(g => goalKeys.Contains(Key(g.X, g.Y)));
        minX = Math.Max(0, minX - margin); maxX = Math.Min(_mapW - 1, maxX + margin);
        minY = Math.Max(0, minY - margin); maxY = Math.Min(_mapH - 1, maxY + margin);

        // Open set ordered by (F, insertion order): lowest F, earliest on a tie -
        // what a linear scan of a list picks, in O(log n) instead of O(n).
        var open = new PriorityQueue<Node, (int, int)>();
        int seq = 0;
        var best = new Dictionary<int, Node>();
        var closed = new HashSet<int>();
        var start = new Node { X = sx, Y = sy, G = 0, F = Heuristic(sx, sy, live) };
        open.Enqueue(start, (start.F, seq++)); best[Key(sx, sy)] = start;

        int guard = 0;
        while (open.Count > 0)
        {
            if (++guard > 80000) return null;   // never hang the game thread
            var cur = open.Dequeue();
            if (goalKeys.Contains(Key(cur.X, cur.Y))) return Rebuild(cur);
            if (!closed.Add(Key(cur.X, cur.Y))) continue;

            for (int d = 0; d < 8; d++)
            {
                int nx = cur.X + DX[d], ny = cur.Y + DY[d];
                if (nx < minX || nx > maxX || ny < minY || ny > maxY) continue;
                if (closed.Contains(Key(nx, ny))) continue;
                if (Blocked(nx, ny)) continue;
                bool diag = DX[d] != 0 && DY[d] != 0;
                if (diag && Blocked(cur.X + DX[d], cur.Y) && Blocked(cur.X, cur.Y + DY[d])) continue;

                int ng = cur.G + (diag ? 14 : 10);
                if (best.TryGetValue(Key(nx, ny), out var seen) && ng >= seen.G) continue;
                var node = new Node { X = nx, Y = ny, G = ng, F = ng + Heuristic(nx, ny, live), Parent = cur };
                best[Key(nx, ny)] = node;
                open.Enqueue(node, (node.F, seq++));
            }
        }
        return null;
    }

    // Octile distance to the NEAREST goal: admissible for 8-way movement costing
    // 10 / 14, so the first goal reached is the closest by route.
    private static int Heuristic(int x, int y, List<(int X, int Y)> goals)
    {
        int h = int.MaxValue;
        foreach (var (gx, gy) in goals)
        {
            int dx = Math.Abs(x - gx), dy = Math.Abs(y - gy);
            h = Math.Min(h, 10 * Math.Max(dx, dy) + 4 * Math.Min(dx, dy));
        }
        return h;
    }

    private static List<Node> Rebuild(Node n)
    {
        var path = new List<Node>();
        for (var c = n; c != null; c = c.Parent) path.Add(c);
        path.Reverse();
        return path;
    }

    private static string Describe(List<Node> path)
    {
        if (path == null || path.Count < 2) return "you are next to it";
        var parts = new List<string>();
        int runDir = -1, runLen = 0;
        for (int i = 1; i < path.Count; i++)
        {
            int dx = Math.Sign(path[i].X - path[i - 1].X), dy = Math.Sign(path[i].Y - path[i - 1].Y);
            int dir = -1;
            for (int d = 0; d < 8; d++) if (DX[d] == dx && DY[d] == dy) { dir = d; break; }
            if (dir < 0) continue;
            if (dir == runDir) runLen++;
            else
            {
                if (runLen > 0) parts.Add($"{DName[runDir]} {runLen}");
                runDir = dir; runLen = 1;
            }
        }
        if (runLen > 0) parts.Add($"{DName[runDir]} {runLen}");
        return string.Join(", ", parts);
    }

    // Which way to face at the end of the route: toward the target's footprint,
    // a cardinal direction when one is available (8-way facing exists, but
    // talking has only been seen done square-on).
    private static string Facing(int x, int y, HashSet<int> footprint)
    {
        foreach (int d in new[] { 0, 2, 4, 6, 1, 3, 5, 7 })
            if (footprint.Contains(Key(x + DX[d], y + DY[d]))) return DName[d];
        return null;
    }

    // ── targets ─────────────────────────────────────────────────────────────
    private enum Category { All = 0, People = 1, Objects = 2, Count = 3 }
    private static readonly string[] CatNames = { "All", "People", "Objects" };

    private sealed class Target { public string Id, Name, Key, Sig; public int X, Y, Slot; public Category Cat; public bool Named; }

    private static readonly List<Target> _targets = new List<Target>();
    private static int _index = -1;
    private static string _selectedId;
    private static Category _category = Category.All;
    private static string _loggedFor;

    // otyp=33..36 style parked slots sit at (16,16): unused, not a destination.
    private static bool IsParked(int x, int y) => x == 16 && y == 16;

    private static void RefreshTargets(object ec)
    {
        _targets.Clear();
        var eh = Val(ec, "ehuman");
        int n = NavProbeShared.CountOf(eh);
        var log = new System.Text.StringBuilder();
        string mk = Gsd2Walk.CurrentMapKey;
        for (int i = 1; i < Math.Max(n, 0); i++)
        {
            var h = UiText.Element(eh, i);
            if (h == null) continue;
            int otyp = Get(h, "otyp");
            if (otyp == 255 || otyp == int.MinValue) continue;
            int x = PosPx(Val(h, "x")), y = PosPx(Val(h, "y"));
            if (x == int.MinValue || IsParked(x, y) || x < 0 || y < 0 || x >= _mapW || y >= _mapH) continue;
            int disp = Get(h, "disp");
            string sig = Signature(h, otyp);
            // The sprite fields are logged so a later session can measure which
            // of them hold still for one occupant and change with another.
            log.Append($" [{i} otyp={otyp} disp={disp} ({x},{y}) sig={sig} ano={Get(h, "ano")} eano={Get(h, "eano")} fpno={Get(h, "fpno")} ityp={Get(h, "ityp")} tdat={Get(h, "tdat")}]");

            // Only what is on screen is a destination. disp=0 slots are hidden
            // (the 2026-09-29 phantom), and a displayed otyp=4 is the second half
            // of the otyp=2 person beside it, not someone else.
            // No name is read: EventHumanNameGet takes a CHARACTER number (pno),
            // and an entity carries none - called with the slot it returned the
            // recruitable-character roster by index ('character O' for a soldier role 3).
            if (disp != 1) continue;
            Category cat;
            if (otyp == 1 || otyp == 2) cat = Category.People;
            else if (otyp == 3) cat = Category.Objects;
            else continue;
            if (_category != Category.All && cat != _category) continue;
            string key = mk == null ? null : $"{mk}:{i}";
            string given = Gsd2Names.Get(key, sig);
            string name = given ?? (cat == Category.People ? "person" : "object");
            _targets.Add(new Target { Id = $"{i}", Name = name, Key = key, Sig = sig, Named = given != null,
                                      X = x, Y = y, Slot = i, Cat = cat });
        }
        if (mk != _loggedFor) { _loggedFor = mk; Plugin.Logger.LogInfo($"[Nav2] entities on {mk}:{log}"); }

        // Nearest first, so J/L walk outward from the player.
        int px = Get(ec, "px"), py = Get(ec, "py");
        _targets.Sort((a, b) => Dist(a.X, a.Y, px, py).CompareTo(Dist(b.X, b.Y, px, py)));
        NumberRepeatedNames();

        int found = _selectedId == null ? -1 : _targets.FindIndex(t => t.Id == _selectedId);
        _index = found >= 0 ? found : (_targets.Count > 0 ? Math.Max(0, Math.Min(_index, _targets.Count - 1)) : -1);
    }

    // Occupant fingerprint for a player-given name (see Gsd2Names). Only fields
    // that do not move with the entity are used: type and sprite size. Whether
    // they are enough to tell two occupants of one slot apart is NOT yet
    // measured - the entity log carries the other sprite fields for that.
    private static string Signature(object h, int otyp) => $"t{otyp}.{Get(h, "w")}x{Get(h, "h")}";

    // Shift+K: the player types the name they heard in dialogue. Typed rather than
    // taken from the last speaker, because party members speak too - taking the
    // speaker automatically is what mislabelled character B as "character A" in Suikoden 1.
    private static void NameSelected()
    {
        if (_index < 0 || _index >= _targets.Count) { Narration.Label("No target selected"); return; }
        var t = _targets[_index];
        if (t.Key == null) { Narration.Label("That target cannot be named"); return; }
        string key = t.Key, sig = t.Sig;
        Gsd1TextInput.Open($"Naming {t.Name}", name =>
        {
            Gsd2Names.Set(key, name, sig);
            if (_ec != null) RefreshTargets(_ec);
        });
    }

    private static void NumberRepeatedNames()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in _targets) { counts.TryGetValue(t.Name, out int c); counts[t.Name] = c + 1; }
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in _targets)
        {
            if (counts[t.Name] < 2) continue;
            seen.TryGetValue(t.Name, out int k); seen[t.Name] = k + 1;
            t.Name = $"{t.Name} {k + 1}";
        }
    }

    private static int Dist(int ax, int ay, int bx, int by) => Math.Max(Math.Abs(ax - bx), Math.Abs(ay - by));

    private static void Select(int i)
    {
        if (_targets.Count == 0) { _index = -1; _selectedId = null; return; }
        _index = ((i % _targets.Count) + _targets.Count) % _targets.Count;
        _selectedId = _targets[_index].Id;
    }

    private static void SpeakCurrent(object ec)
    {
        if (_index < 0 || _index >= _targets.Count) { Narration.Label($"No {CatNames[(int)_category].ToLowerInvariant()} targets"); return; }
        var t = _targets[_index];
        int px = Get(ec, "px"), py = Get(ec, "py");
        Narration.Label($"{t.Name}, {Bearing(px, py, t.X, t.Y)}");
    }

    private static string Bearing(int fx, int fy, int tx, int ty)
    {
        int dx = tx - fx, dy = ty - fy;
        if (dx == 0 && dy == 0) return "here";
        var parts = new List<string>();
        if (dy != 0) parts.Add($"{Math.Abs(dy)} {(dy < 0 ? "north" : "south")}");
        if (dx != 0) parts.Add($"{Math.Abs(dx)} {(dx < 0 ? "west" : "east")}");
        return string.Join(", ", parts);
    }

    // ── P ───────────────────────────────────────────────────────────────────
    private static void Pathfind(object ec)
    {
        if (_index < 0 || _index >= _targets.Count) { Narration.Label("No target selected"); return; }
        if (!BindFunc1(ec)) { Narration.Label("Collision unavailable"); return; }
        var t = _targets[_index];
        var sw = Stopwatch.StartNew();

        _terrain.Clear(); _f1Calls = 0; _f1Throws = 0; _f1FirstThrow = null;
        _blkTerrain = _blkObject = _blkPeople = 0;
        int w = Get(ec, "mapw"), h = Get(ec, "maph");
        _mapW = w > 0 ? w / 8 : 120; _mapH = h > 0 ? h / 8 : 120;
        string mk = Gsd2Walk.CurrentMapKey ?? "?";
        BuildObjectMask(ec, mk);
        BuildPeople(ec);
        _useObjects = true;

        // What the route must end BESIDE: a person's whole footprint, or an
        // object's own cell. v67 excepted the target from the people layer and
        // aimed at its cell, so a route could end inside a person the engine
        // will not let the player enter ("east 1" into the sentry, v66 log).
        var footprint = new HashSet<int>();
        if (t.Cat == Category.People) AddFootprint(Val(ec, "ehuman"), t.Slot, footprint);
        if (footprint.Count == 0) footprint.Add(Key(t.X, t.Y));

        int px = Get(ec, "px"), py = Get(ec, "py");
        string selfNote = "";
        if (Blocked(px, py))
        {
            bool byTerrain = Terrain(px, py);
            bool byPeople = _people.Contains(Key(px, py));
            if (byPeople) { _people.Remove(Key(px, py)); selfNote += " people-footprint-on-player dropped;"; }
            if (_objMask.Contains(Key(px, py))) { _useObjects = false; selfNote += " object layer OFF (player's cell read solid);"; }
            if (byTerrain)
            {
                Plugin.Logger.LogWarning($"[Nav2] SELF-CHECK FAILED: Func1 calls the player's own cell ({px},{py}) solid on {mk}. No route.");
                Narration.Label("Collision reads wrong here. No route.");
                Gsd2Walk.Quarantine(ec, "P pathfind");
                return;
            }
            Plugin.Logger.LogWarning($"[Nav2] self-check at ({px},{py}) on {mk}:{selfNote}");
        }

        // Every cell touching the footprint is a goal; FindPath drops the blocked ones.
        var goals = new List<(int X, int Y)>();
        var seenGoal = new HashSet<int>();
        foreach (int f in footprint)
        {
            int fx = f >> 16, fy = (short)(f & 0xffff);
            for (int d = 0; d < 8; d++)
            {
                int gx = fx + DX[d], gy = fy + DY[d];
                int gk = Key(gx, gy);
                if (footprint.Contains(gk) || !seenGoal.Add(gk)) continue;
                goals.Add((gx, gy));
            }
        }

        var path = seenGoal.Contains(Key(px, py))
            ? new List<Node> { new Node { X = px, Y = py } }
            : FindPath(px, py, goals);
        Gsd2Walk.Quarantine(ec, "P pathfind");

        string stats = $"Func1 {_f1Calls} calls ({_f1Throws} threw{(_f1FirstThrow != null ? ": " + _f1FirstThrow : "")}), " +
                       $"blocked by terrain {_blkTerrain} / objects {_blkObject} / people {_blkPeople}, " +
                       $"objects {(_useObjects ? "on" : "OFF")}, {sw.ElapsedMilliseconds} ms";
        if (path == null)
        {
            Narration.Label($"No path to {t.Name}. {Bearing(px, py, t.X, t.Y)}");
            Plugin.Logger.LogInfo($"[Nav2] pathfind FAILED to {t.Name} ({t.X},{t.Y}) from ({px},{py}) on {mk}: {stats}");
            return;
        }
        var end = path[path.Count - 1];
        string face = Facing(end.X, end.Y, footprint);
        string route = Describe(path) + (face == null ? "" : path.Count < 2 ? $", face {face}" : $", then face {face}");
        Plugin.Logger.LogInfo($"[Nav2] path to {t.Name} ({t.X},{t.Y}) from ({px},{py}) on {mk}: {path.Count - 1} steps -> {route} [{stats}]");
        Narration.Label(route);
    }

    // ── tick ────────────────────────────────────────────────────────────────
    private static bool _j, _k, _l, _p;

    public static void Tick(object ec)
    {
        if (!Enabled || ec == null) return;
        _ec = ec;
        try
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            bool shift = kb[UnityEngine.InputSystem.Key.LeftShift].isPressed || kb[UnityEngine.InputSystem.Key.RightShift].isPressed;
            bool j = kb[UnityEngine.InputSystem.Key.J].isPressed, k = kb[UnityEngine.InputSystem.Key.K].isPressed, l = kb[UnityEngine.InputSystem.Key.L].isPressed, p = kb[UnityEngine.InputSystem.Key.P].isPressed;

            // Text entry owns the keyboard while open: typing "j" must not cycle
            // targets underneath it. The latches follow the keys meanwhile, so a
            // letter still held when entry closes does not fire as a press.
            bool typing = Gsd1TextInput.Tick();
            if (_wasTyping && !typing) _blockMoveUntil = Time.realtimeSinceStartup + 0.3f;
            _wasTyping = typing;
            if (typing) { _j = j; _k = k; _l = l; _p = p; return; }

            if (j && !_j) { if (shift) CycleCategory(ec, -1); else { RefreshTargets(ec); Select(_index - 1); SpeakCurrent(ec); } }
            if (l && !_l) { if (shift) CycleCategory(ec, 1); else { RefreshTargets(ec); Select(_index + 1); SpeakCurrent(ec); } }
            if (k && !_k) { RefreshTargets(ec); if (shift) NameSelected(); else SpeakCurrent(ec); }
            if (p && !_p) { RefreshTargets(ec); Pathfind(ec); }
            _j = j; _k = k; _l = l; _p = p;
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Nav2] tick failed: {e.Message}"); }
    }

    // ── freezing the player while a name is typed ───────────────────────────
    private static bool _wasTyping;
    private static float _blockMoveUntil;

    /// <summary>Harmony prefix on EVENTCON.NewEventPlayerMove(), applied in
    /// NavProbe.TryHook. Skips the player's move while a name is being typed, so
    /// letters cannot walk the player, and for 0.3 s after, so the Enter that
    /// confirms a name does not also reach the game as "talk".</summary>
    /// <remarks>NewEventPlayerMove takes no arguments at all, so this is inside
    /// the hooking rule (no by-ref struct). Postfixes still run when a prefix
    /// skips, so NavProbe keeps capturing the live EVENTCON.</remarks>
    public static bool MoveBlockPrefix() =>
        !(Gsd1TextInput.IsOpen || Time.realtimeSinceStartup < _blockMoveUntil);

    private static void CycleCategory(object ec, int delta)
    {
        _category = (Category)(((int)_category + delta + (int)Category.Count) % (int)Category.Count);
        _index = 0; _selectedId = null;
        RefreshTargets(ec);
        Narration.Label($"{CatNames[(int)_category]}: {_targets.Count} targets");
    }

    // ── field helpers ───────────────────────────────────────────────────────
    private static object Val(object inst, string name) => UiText.Member(inst, name);   // cached lookup

    private static int Get(object inst, string name) => inst == null ? int.MinValue : NavProbeShared.ToInt(Val(inst, name));

    private static int PosPx(object pos)
    {
        try
        {
            var all = Val(pos, "all");
            if (all == null) return int.MinValue;
            return (int)(Convert.ToInt64(all) / 65536 / 8);
        }
        catch { return int.MinValue; }
    }
}
