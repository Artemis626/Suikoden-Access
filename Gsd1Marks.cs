using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace SuikodenAccess;

// ─────────────────────────────────────────────────────────────────────────────
// PLAYER-MARKED PLACES (Suikoden 1)
//
// NOT a waypoint-graph fallback: routing is still true A* over the game's own
// collision data. These are only DESTINATIONS — the "where do I want to go"
// half — for things the game gives us no way to identify.
//
// The concrete case: the diary desk that saves the game. It lives in
// map_tatemono along with every wall panel, flowerpot and rug — TATEMONO carries
// id/level/size but no "interactable" flag and no name, so one house reports 56
// indistinguishable objects. There is no way to pick the diary out of that list
// programmatically. A player who has found it once can mark it, and from then on
// it is a first-class pathfinding target.
//
// Marks are per-map, saved as plain text next to the plugin, and shareable: one
// player's marks file makes those places reachable for everyone else.
// ─────────────────────────────────────────────────────────────────────────────
internal static class Gsd1Marks
{
    internal sealed class Mark
    {
        public string Map;
        public int X, Y;
        public string Label;
    }

    private static readonly List<Mark> _marks = new List<Mark>();
    private static bool _loaded;
    private static string _path;

    private static string FilePath()
    {
        if (_path != null) return _path;
        var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (string.IsNullOrEmpty(dir)) dir = AppContext.BaseDirectory;
        _path = Path.Combine(dir, "suikoden1_marks.txt");
        return _path;
    }

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath())) return;
            foreach (var line in File.ReadAllLines(FilePath()))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal)) continue;
                var p = line.Split('|');
                if (p.Length < 3) continue;
                if (!int.TryParse(p[1].Trim(), out int x)) continue;
                if (!int.TryParse(p[2].Trim(), out int y)) continue;
                _marks.Add(new Mark
                {
                    Map = p[0].Trim(), X = x, Y = y,
                    Label = p.Length > 3 ? p[3].Trim() : null,
                });
            }
            Plugin.Logger.LogInfo($"[Nav1] loaded {_marks.Count} marked places.");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Nav1] mark load failed: {e.Message}"); }
    }

    private static void Save()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Suikoden Access - places marked by the player in Suikoden 1.");
            sb.AppendLine("# map | tileX | tileY | label");
            sb.AppendLine("# 'label' is what gets spoken - edit it to something useful,");
            sb.AppendLine("# e.g. 'diary' for the desk that saves the game. Safe to share.");
            foreach (var m in _marks) sb.AppendLine($"{m.Map} | {m.X} | {m.Y} | {m.Label}");
            File.WriteAllText(FilePath(), sb.ToString());
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Nav1] mark save failed: {e.Message}"); }
    }

    public static List<Mark> ForMap(string map)
    {
        Load();
        var res = new List<Mark>();
        foreach (var m in _marks) if (m.Map == map) res.Add(m);
        return res;
    }

    /// <summary>Adds a mark; returns its number within the current map.</summary>
    public static int Add(string map, int x, int y, string label)
    {
        Load();
        foreach (var m in _marks)
            if (m.Map == map && m.X == x && m.Y == y) return ForMap(map).IndexOf(m) + 1;

        _marks.Add(new Mark { Map = map, X = x, Y = y, Label = label });
        Save();
        Plugin.Logger.LogInfo($"[Nav1] marked place on {map} at ({x},{y}) label='{label}'");
        return ForMap(map).Count;
    }
}
