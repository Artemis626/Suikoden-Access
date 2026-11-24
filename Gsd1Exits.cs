using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace SuikodenAccess;

// ─────────────────────────────────────────────────────────────────────────────
// LEARNED MAP EXITS (Suikoden 1)
//
// Suikoden 1 exposes NO enumerable list of warps, stairs or doorways:
//   - VILLAGE_WORK.map_door reads 0 entries on the maps tested
//   - there is no WARP/EXIT/KAIDAN(stairs)/DEGUCHI(exit) structure anywhere in
//     the decompile; the only STAIRS class belongs to a minigame
//   - transitions are driven by compiled EVENT BYTECODE that checks the player's
//     position, so there is nothing static to read
//
// That made stairs and doorways unreachable for a blind player — the exact case
// reported during the intro corridor, where the route to the stairs simply could
// not be found.
//
// So exits are LEARNED from what actually happens: when the map changes, the
// player's last position on the old map WAS an exit, by definition. That
// position is recorded as a landmark on that map, and the arrival position is
// recorded as the way back. Every transition the player crosses once becomes a
// permanent, pathfindable target — and unlike guessing from data, a learned exit
// is always real.
//
// Stored as plain text next to the plugin so it can be read, edited and shared.
// ─────────────────────────────────────────────────────────────────────────────
internal static class Gsd1Exits
{
    internal sealed class Exit
    {
        public string Map;
        public int X, Y;
        public string Leads;      // internal key of the map this led to — never spoken
        public string Label;      // optional human name, hand-written in the file
    }

    private static readonly List<Exit> _exits = new List<Exit>();
    private static bool _loaded;
    private static string _path;

    // Previous-frame state, used to notice a transition.
    private static string _lastMap;
    private static int _lastX = -1, _lastY = -1;

    private static string FilePath()
    {
        if (_path != null) return _path;
        var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (string.IsNullOrEmpty(dir)) dir = AppContext.BaseDirectory;
        _path = Path.Combine(dir, "suikoden1_exits.txt");
        return _path;
    }

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            int dropped = 0;
            if (!File.Exists(FilePath()))
            {
                // Log the zero: "no file yet" and "file with nothing usable in it"
                // must never look the same, and the path is worth printing because
                // the player may want to delete it.
                Plugin.Logger.LogInfo($"[Nav1] no learned-exit file at {FilePath()} — starting empty.");
                return;
            }
            foreach (var line in File.ReadAllLines(FilePath()))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal)) continue;
                // map | x | y | leadsTo | label
                var p = line.Split('|');
                if (p.Length < 3) continue;
                if (!int.TryParse(p[1].Trim(), out int x)) continue;
                if (!int.TryParse(p[2].Trim(), out int y)) continue;
                var map = p[0].Trim();
                var leads = p.Length > 3 ? p[3].Trim() : null;

                // Purge on load, so a file already poisoned by the old jump
                // heuristic repairs itself instead of needing the player to find
                // and delete it — which is awkward anyway, because the mod
                // rewrites the whole file from memory whenever it learns
                // something, so deleting it mid-session simply brings it back.
                //
                //   leads == map  : "this exit leads to where you already are"
                //   (0,0)         : the engine's own "not placed" sentinel
                if (!string.IsNullOrEmpty(leads) && leads == map) { dropped++; continue; }
                if (x == 0 && y == 0) { dropped++; continue; }
                if (x >= 254 || y >= 254) { dropped++; continue; }

                _exits.Add(new Exit
                {
                    Map = map,
                    X = x, Y = y,
                    Leads = leads,
                    Label = p.Length > 4 ? p[4].Trim() : null,
                });
            }
            Plugin.Logger.LogInfo($"[Nav1] loaded {_exits.Count} learned exits" +
                                  (dropped > 0 ? $", dropped {dropped} unusable (self-referencing or unplaced)" : ""));
            if (dropped > 0) Save();
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Nav1] exit load failed: {e.Message}"); }
    }

    private static void Save()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Suikoden Access - learned Suikoden 1 map exits.");
            sb.AppendLine("# map | tileX | tileY | leadsTo | label");
            sb.AppendLine("# 'label' is optional and is what gets SPOKEN - add e.g. 'stairs up'.");
            sb.AppendLine("# Safe to edit or share.");
            foreach (var e in _exits) sb.AppendLine($"{e.Map} | {e.X} | {e.Y} | {e.Leads} | {e.Label}");
            File.WriteAllText(FilePath(), sb.ToString());
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Nav1] exit save failed: {e.Message}"); }
    }

    /// <summary>Exits known on a given map.</summary>
    public static List<Exit> ForMap(string map)
    {
        Load();
        var res = new List<Exit>();
        foreach (var e in _exits) if (e.Map == map) res.Add(e);
        return res;
    }

    private static bool Has(string map, int x, int y)
    {
        foreach (var e in _exits)
            if (e.Map == map && Math.Abs(e.X - x) <= 1 && Math.Abs(e.Y - y) <= 1) return true;
        return false;
    }

    /// <summary>
    /// Called every tick with the player's current map and tile. Detects a map
    /// change and records both sides of the transition.
    /// </summary>
    public static void Observe(string map, int x, int y)
    {
        Load();

        // A null key means the area is mid-load and unreadable. Recording either
        // side of a transition against it would invent a destination that does
        // not exist, so nothing is recorded WHILE it is null.
        //
        // What must NOT happen here is forgetting where the player came from.
        // Every real transition runs old key -> several null frames -> new key,
        // so clearing _lastMap on the null (which this did) meant that by the
        // time the new key arrived there was nothing to compare it against, and
        // the "map != _lastMap" branch below could never fire. That silently
        // disabled exit learning outright: a whole session of walking up and
        // down the villa stairs produced four map-key changes in the position
        // log and not one learned exit. The pre-transition tile is kept instead,
        // and only discarded if the gap runs long enough to be a load screen or
        // a title-screen round trip rather than a doorway.
        float now = UnityEngine.Time.realtimeSinceStartup;
        if (string.IsNullOrEmpty(map))
        {
            if (_lastMap != null && now - _lastSeen > MaxGapSeconds)
            { _lastMap = null; _lastX = -1; _lastY = -1; }
            return;
        }

        // A transition is a CHANGE OF MAP ID. Nothing else.
        //
        // There used to be a second test: a position jump of more than 8 tiles
        // inside one map counted as a transition too. Its justification was that
        // the map key was a village-level fingerprint, so room-to-room doorways
        // inside one building would not change it. **That justification is gone**
        // — since 2026-08-29 the key is `a{area}.m{map}` from the game's own
        // get_area_idx()/get_map_idx(), which changes for every room, and the
        // recorded a0.m3 / a0.m1 / a0.m0 transitions prove it does.
        //
        // What the jump test actually detected was CUTSCENES. The game's opening
        // is one long scripted sequence that repositions the player repeatedly,
        // and in the throne room it invented three "exits" in a row — (23,45),
        // (70,45), (70,70) — while the player stood still through a conversation
        // with the Emperor. Every one recorded `leadsTo` as the map it was
        // already on, which is both the signature of the bug and unspeakable:
        // "this exit leads to where you are". They then showed up as duplicates
        // beside the real event-table exits.
        //
        // A doorway inside one map is not lost by this: those come from the
        // event table now (§1.7), which knows them before they are ever crossed.
        if (_lastMap != null && map != _lastMap)
        {
            bool changed = false;

            // Where the player stood before leaving: an exit on the OLD map.
            if (_lastX >= 0 && !Has(_lastMap, _lastX, _lastY))
            {
                _exits.Add(new Exit { Map = _lastMap, X = _lastX, Y = _lastY, Leads = map });
                Plugin.Logger.LogInfo($"[Nav1] learned exit on {_lastMap} at ({_lastX},{_lastY}) -> {map}");
                changed = true;
            }

            // Where the player arrived: the way back on the NEW map.
            if (!Has(map, x, y))
            {
                _exits.Add(new Exit { Map = map, X = x, Y = y, Leads = _lastMap });
                Plugin.Logger.LogInfo($"[Nav1] learned exit on {map} at ({x},{y}) -> {_lastMap}");
                changed = true;
            }

            if (changed) Save();
        }

        _lastMap = map;
        _lastX = x;
        _lastY = y;
        _lastSeen = now;
    }

    /// <summary>How long an unreadable area may last and still count as one
    /// doorway. A room change takes a couple of seconds; anything longer is a
    /// load screen, a battle or a return to the title, and the tile the player
    /// left is no longer a doorway into wherever they end up.</summary>
    private const float MaxGapSeconds = 20f;
    private static float _lastSeen = -999f;
}
