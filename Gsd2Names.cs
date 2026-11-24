using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace SuikodenAccess;

// ─────────────────────────────────────────────────────────────────────────────
// PLAYER-GIVEN NPC NAMES (Suikoden 2)
//
// Suikoden 2 has no readable name for a map entity: EVENT_HUMAN carries no
// character number, and EventHumanNameGet(pno) wants one (handoff §15). The
// names a player knows ("soldier role 3") exist only in dialogue. So, as
// in Suikoden 1 (Gsd1Names), the player names a target by ear with Shift+K and
// the name is kept here.
//
// Key: <mapKey>:<ehuman slot>. A slot is a place in the map's entity list, not a
// person: Suikoden 1 reused character A's slot for character B once character A joined. So each
// name is stored with a signature of the occupant, and a name whose signature
// no longer matches is dropped rather than put on someone else.
//
// Kept apart from suikoden1_npc_names.txt on purpose: the two games' map keys
// and slots mean different things, and a shared file would invite collisions.
// ─────────────────────────────────────────────────────────────────────────────
internal static class Gsd2Names
{
    private static readonly Dictionary<string, string> _names =
        new Dictionary<string, string>(StringComparer.Ordinal);
    private static bool _loaded;
    private static string _path;

    private static string FilePath()
    {
        if (_path != null) return _path;
        var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (string.IsNullOrEmpty(dir)) dir = AppContext.BaseDirectory;
        _path = Path.Combine(dir, "suikoden2_npc_names.txt");
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
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var k = line.Substring(0, eq).Trim();
                var v = line.Substring(eq + 1).Trim();
                if (k.Length > 0 && v.Length > 0) _names[k] = v;
            }
            Plugin.Logger.LogInfo($"[Nav2] loaded {_names.Count} player-given NPC names.");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Nav2] name load failed: {e.Message}"); }
    }

    private static void Save()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Suikoden Access - Suikoden 2 NPC names, given with Shift+K.");
            sb.AppendLine("# One per line: <map>:<slot> = <name>|<signature>. Safe to edit or share.");
            sb.AppendLine("# Delete the |<signature> part to keep a name whoever occupies the slot.");
            foreach (var kv in _names) sb.AppendLine($"{kv.Key} = {kv.Value}");
            File.WriteAllText(FilePath(), sb.ToString());
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Nav2] name save failed: {e.Message}"); }
    }

    /// <summary>The stored name, or null if none or if the occupant changed.</summary>
    public static string Get(string key, string signature)
    {
        Load();
        if (key == null || !_names.TryGetValue(key, out var v) || string.IsNullOrEmpty(v)) return null;

        int bar = v.LastIndexOf('|');
        if (bar < 0) return v;                       // hand-written entry: trust it

        var name = v.Substring(0, bar);
        var sig = v.Substring(bar + 1);
        if (string.IsNullOrEmpty(signature) || sig == signature) return name;

        if (_rejected.Add(key))
            Plugin.Logger.LogInfo($"[Nav2] name '{name}' not used for {key}: occupant changed ({sig} -> {signature})");
        return null;
    }

    // Get runs on every J/K/L, so a rejection is logged once per key, not per press.
    private static readonly HashSet<string> _rejected = new HashSet<string>(StringComparer.Ordinal);

    public static void Set(string key, string name, string signature)
    {
        Load();
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(name)) return;
        var stored = $"{name.Trim()}|{signature}";
        _rejected.Remove(key);
        if (_names.TryGetValue(key, out var had) && had == stored) return;
        _names[key] = stored;
        Save();
        Plugin.Logger.LogInfo($"[Nav2] named {key} = {stored}");
    }
}
