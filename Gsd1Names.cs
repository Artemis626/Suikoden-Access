using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace SuikodenAccess;

// ─────────────────────────────────────────────────────────────────────────────
// LEARNED NPC NAMES (Suikoden 1)
//
// Suikoden 1 stores no display name on a map entity: HUMAN carries chara_no
// (reads 0 for every ambient NPC), map_event_no (a bare index) and sprite ids
// via HITO_DATA — nothing a player would recognise. The names players know
// ("character A", "character E") exist only in DIALOGUE, which this mod already reads and
// speaks. So names are LEARNED: when a line is spoken with a speaker prefix,
// it is attributed to the NPC the player is standing next to and remembered.
//
// Attribution is by proximity rather than facing, because HUMAN.dir's encoding
// is not yet confirmed and a wrong guess would mislabel people. Talking happens
// adjacent, so "nearest within a couple of tiles, and only one candidate" is
// both simpler and safer: ambiguous cases are skipped rather than guessed.
//
// Names persist next to the plugin as a plain text file, one record per line,
// so a player can read, hand-edit or share it.
// ─────────────────────────────────────────────────────────────────────────────
internal static class Gsd1Names
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
        _path = Path.Combine(dir, "suikoden1_npc_names.txt");
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
            Plugin.Logger.LogInfo($"[Nav1] loaded {_names.Count} learned NPC names.");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Nav1] name load failed: {e.Message}"); }
    }

    private static void Save()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Suikoden Access - learned Suikoden 1 NPC names.");
            sb.AppendLine("# One per line: <map>:<list>:<eventNo> = <name>. Safe to edit or share.");
            foreach (var kv in _names) sb.AppendLine($"{kv.Key} = {kv.Value}");
            File.WriteAllText(FilePath(), sb.ToString());
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Nav1] name save failed: {e.Message}"); }
    }

    // Stored value is "name|signature". map_event_no is a SLOT index, not an
    // identity: once character A joined the party his slot was reused by character B, and
    // a name keyed on the slot alone followed the slot rather than the character.
    // The signature is a cheap fingerprint of the entity's sprite (dimensions,
    // palette and pattern counts), so a changed occupant no longer matches and
    // the stale name is discarded instead of shown.
    public static string Get(string key, string signature)
    {
        Load();
        if (!_names.TryGetValue(key, out var v) || string.IsNullOrEmpty(v)) return null;

        int bar = v.LastIndexOf('|');
        if (bar < 0) return v;                       // hand-written entry: trust it

        var name = v.Substring(0, bar);
        var sig = v.Substring(bar + 1);
        if (string.IsNullOrEmpty(signature) || sig == signature) return name;

        Plugin.Logger.LogInfo($"[Nav1] name '{name}' rejected for {key}: occupant changed ({sig} -> {signature})");
        return null;
    }

    public static void Set(string key, string name, string signature)
    {
        Load();
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(name)) return;
        var stored = $"{name}|{signature}";
        if (_names.TryGetValue(key, out var had) && had == stored) return;
        _names[key] = stored;
        Save();
        Plugin.Logger.LogInfo($"[Nav1] learned name: {key} = {stored}");
    }

    /// <summary>
    /// A stable per-map key.
    /// </summary>
    /// <remarks>
    /// Suikoden 1 DOES expose a current-map id, contrary to the earlier note here:
    /// Village_c.get_map_idx() and get_area_idx() are no-argument getters, and a
    /// live dump confirmed them (area 0, map 3) against map_table[3], whose
    /// width/height/object count match the loaded map exactly. Both are used when
    /// present, because real ids beat a fingerprint on every axis: they survive a
    /// room being edited by a patch, they cannot collide between two rooms that
    /// happen to share a prop layout, and they are readable by a human editing
    /// the shared text files.
    ///
    /// The fingerprint remains as a fallback, so a build where the getters are
    /// missing still works rather than merging every room onto one key.
    ///
    /// The getters are game method calls, and the project rule is that those stay
    /// one-shot rather than per-frame. MapKey is called several times per frame,
    /// so the result is cached for a fraction of a second — long enough to make
    /// the call rate a handful per second, short enough that a room change is
    /// noticed well within the time it takes to load one.
    /// </remarks>
    private static string _keyCache;
    private static float _keyCacheAt = -999f;
    private static object _keyCacheVillage;
    private const float KeyCacheSeconds = 0.25f;

    public static string MapKey(object village)
    {
        float now = UnityEngine.Time.realtimeSinceStartup;
        if (ReferenceEquals(village, _keyCacheVillage) && now - _keyCacheAt < KeyCacheSeconds)
            return _keyCache;

        var key = ComputeMapKey(village);
        _keyCache = key; _keyCacheAt = now; _keyCacheVillage = village;
        return key;
    }

    private static bool _gettersMissing;

    private static string ComputeMapKey(object village)
    {
        try
        {
            // Mid-transition the area is not loaded and every list reads back as
            // -1. Establish that FIRST, before calling any game method: an id read
            // during a load describes a map that is on its way out.
            var objList0 = UiText.Member(village, "map_tatemono");
            if (NavProbeShared.CountOf(objList0) < 0 || UiText.Member(village, "mapVillage") == null)
                return null;

            if (!_gettersMissing)
            {
                int area = CallNoArgInt(village, "get_area_idx");
                int map = CallNoArgInt(village, "get_map_idx");
                if (area != int.MinValue && map != int.MinValue && map >= 0)
                    return $"a{area}.m{map}";
                _gettersMissing = true;
                Plugin.Logger.LogInfo("[Nav1] get_area_idx/get_map_idx unavailable; using the fingerprint key.");
            }
        }
        catch { }
        return FingerprintKey(village);
    }

    private static int CallNoArgInt(object inst, string name)
    {
        try
        {
            var m = inst.GetType().GetMethod(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                null, Type.EmptyTypes, null);
            if (m == null || m.ReturnType != typeof(int)) return int.MinValue;
            return NavProbeShared.ToInt(m.Invoke(inst, null));
        }
        catch { return int.MinValue; }
    }

    private static string FingerprintKey(object village)
    {
        try
        {
            var vil = UiText.Member(village, "vil");

            // VIL.id read empty in play, so every room collapsed onto the literal
            // key "map" — which would merge one room's exits and names into every
            // other room's. Fall back to a composite fingerprint of the loaded
            // area instead: map count, NPC counts and object count together
            // change when the area does, which is enough to keep rooms apart.
            var id = vil != null ? UiText.Member(vil, "id") as string : null;
            string baseId = string.IsNullOrWhiteSpace(id) ? "m" : id.Trim();

            int mapKazu = NavProbeShared.ToInt(UiText.Member(vil, "map_kazu"));
            var objList = UiText.Member(village, "map_tatemono");
            int objs = NavProbeShared.CountOf(objList);

            // Mid-transition the lists are briefly unreadable and CountOf returns
            // -1, which produced keys like "m.31.-1.0.-1" and recorded exits
            // leading to a map that does not exist. An unreadable area has no
            // usable key, so say so and let callers skip rather than invent one.
            if (objs < 0 || mapKazu < 0) return null;

            // NPC counts are deliberately NOT part of the key. map_human went
            // from 14 to 13 in the same room when a character joined the party,
            // which would have changed the key and orphaned every name and exit
            // learned there. Scenery does not move and does not join parties, so
            // the room is fingerprinted by its object layout instead: the count
            // plus a checksum of the first objects' positions, which keeps two
            // rooms that happen to hold the same number of props apart.
            int check = 0;
            for (int i = 0; i < Math.Min(objs, 12); i++)
            {
                var o = UiText.Element(objList, i);
                check = unchecked(check * 31 + NavProbeShared.ToInt(UiText.Member(o, "map_x")));
                check = unchecked(check * 31 + NavProbeShared.ToInt(UiText.Member(o, "map_y")));
            }

            return $"{baseId}.{mapKazu}.{objs}.{check & 0xFFFF:x4}";
        }
        catch { }
        return "map";
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// THE GAME'S OWN CHARACTER NAMES
//
// Fhuman_c.GetCharacterName(int hno, bool short_name) is a static method on a
// static class, value parameters only, and it returns the real display name in
// the player's own language. EVENT_WK.image is the key. Confirmed in play in the
// the palace map, against two characters named by somebody standing in the room:
//
//   ekp[16] putno=11 image=16  (FP_B) -> 'character B'   was "person 11"
//   ekp[22] putno=12 image=110 (FP_A) -> 'character A'
//   ekp[11] putno=7  image=34  (FP_F) -> 'character F'
//   ekp[12] putno=8  image=112 (FP_G) -> 'character G'
//
// The CHARACTER_ID enum gives the same id a second, independent label — FP_B,
// FP_A — so each of those is two agreeing signals, not one.
//
// WHAT DISQUALIFIES AN ID, measured rather than reasoned about. A full sweep of
// hno 0..255 partitions perfectly: 128 ids carry a real name and a text id >= 0,
// 127 answer the engine's placeholder '名前未定義: <hno-1>' with text id -1, and
// one is empty. No real name has text id -1 and no placeholder has one >= 0, so
// ONE test decides it:
//
//     GetCharacterNameTextID(image, false) >= 0, and the name is not the
//     placeholder.
//
// The placeholder marker is checked as well as the -1 because it is a literal in
// the executable and so survives any language setting.
//
// A PREVIOUS VERSION OF THIS FILE ALSO REJECTED ANY id whose CHARACTER_ID symbol
// contained a digit — FP_00P, FP_04P — on the reasoning that those are generic
// sprite slots rather than people. **That was wrong, and the sweep is what shows
// it.** Those ids carry some of the best names on offer:
//
//     119 FP_00P = official role          123 FP_04P = object-like name
//     120 FP_01P = character H
//     121 FP_02P = character C           124 FP_05P = soldier role 1
//     122 FP_03P = character D             125 FP_06P = soldier role 2
//                                    126 FP_07P = villager role
//
// The filter would have thrown away character C and character D and left a room full of
// "person 5" where the game itself says "soldier role 1". The digit rule was
// generalised from one observation — the palace guards reading 'object-like name'
// — and from an assumption that that name must be wrong for them, which was never
// checked. Its text id sits in the same block as official role, character C and
// soldier role 1, so it is an NPC name like the others, and the pair-wise symmetric
// placement of those entities matches the armour displays flanking the hall.
// ─────────────────────────────────────────────────────────────────────────────
internal static class Gsd1GameNames
{
    private static bool _resolved;
    private static MethodInfo _getName, _getNameTextId;
    private static Type _charId;
    private static readonly Dictionary<int, string> _cache = new Dictionary<int, string>();

    /// <summary>The engine's placeholder for an id with no name behind it.</summary>
    private const string Undefined = "名前未定義";

    private static void Resolve()
    {
        if (_resolved) return;
        _resolved = true;
        var fhuman = Gsd.FindAnywhere("Fhuman_c");
        _charId = Gsd.FindAnywhere("CHARACTER_ID");
        _getName = Gsd.FindMethod(fhuman, "GetCharacterName");
        _getNameTextId = Gsd.FindMethod(fhuman, "GetCharacterNameTextID");
        Plugin.Logger.LogInfo($"[Nav1] character names: Fhuman_c={(fhuman != null)} " +
                              $"GetCharacterName={(_getName != null)} " +
                              $"GetCharacterNameTextID={(_getNameTextId != null)} " +
                              $"CHARACTER_ID={(_charId != null)}" +
                              (_getName == null ? " — NPCs will stay 'person N'." : ""));
    }

    public static string Symbol(int id)
    {
        try
        {
            if (_charId == null || !_charId.IsEnum) return null;
            return Enum.GetName(_charId, Enum.ToObject(_charId, id));
        }
        catch { return null; }
    }

    // ── THE BAND WHERE A NAME IS NOT YET TRUSTWORTHY ────────────────────────
    //
    // CHARACTER_ID has 165 members: 143 three-letter character codes (FP_A,
    // FP_B) and 22 slots whose symbol carries a digit (FP_00P..FP_18P = 119-137,
    // FP_19P..FP_21P = 146-148). Those 22 are BOTH generic person-sprite slots
    // AND named characters: FP_02P is "generic person sprite 2" and it is also
    // character C's id. In play that produces "character C" and "character D" for ordinary
    // townspeople in town A, and "soldier role 2" before that army
    // exists.
    //
    // The project has already been wrong about this band twice — once by
    // filtering it out entirely, once by retracting that filter — and both
    // answers were arguments about what the id ranges must mean. NAV-STATUS §1.9
    // says an argument about the numbers can generate a candidate and must never
    // choose between candidates: only an observation can. The observation being
    // collected is the player saying who these people actually are (Gsd1Probe F5
    // prints the questionnaire), and until it is in, this is NOT a rule and NOT
    // a filter.
    //
    // What it IS, in the meantime, is honesty about a name the mod is not sure
    // of. The name is still spoken, because the player needs to be able to find
    // "possible character C" in the target list to go and check who it is, and
    // because the alternative is a room of "person 5". But it is spoken as a
    // question rather than a fact, so nothing is asserted that may be false.
    // Delete the prefix and this test in one line once the band is settled.
    public static bool IsAmbiguousBand(int image)
    {
        var sym = Symbol(image);
        if (string.IsNullOrEmpty(sym)) return false;
        foreach (var c in sym) if (c >= '0' && c <= '9') return true;
        return false;
    }

    public static string ForImage(int image) => ForImage(image, out _);

    /// <summary>The character's real name for a sprite id, or null when the id is
    /// not a named character. <paramref name="provisional"/> is true when the id
    /// falls in the band described above, where a real name and a generic sprite
    /// slot share one number and the name may belong to somebody else entirely.
    /// Never throws, never speaks, never persists.</summary>
    public static string ForImage(int image, out bool provisional)
    {
        provisional = false;
        if (image <= 0) return null;
        if (_cache.TryGetValue(image, out var had))
        {
            provisional = had != null && IsAmbiguousBand(image);
            return had;
        }

        string name = null;
        try
        {
            Resolve();
            if (_getName != null)
            {
                // No text id method means no way to tell a name from a
                // placeholder, so nothing is named rather than something guessed.
                int textId = _getNameTextId == null
                    ? -1
                    : NavProbeShared.ToInt(_getNameTextId.Invoke(null, new object[] { image, false }));
                if (textId >= 0)
                {
                    var s = _getName.Invoke(null, new object[] { image, false }) as string;
                    if (!string.IsNullOrWhiteSpace(s) && s.IndexOf(Undefined, StringComparison.Ordinal) < 0)
                        name = s.Trim();
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[Nav1] GetCharacterName({image}) failed: {e.Message}");
        }

        _cache[image] = name;
        provisional = name != null && IsAmbiguousBand(image);
        Plugin.Logger.LogInfo($"[Nav1] image {image} ({Symbol(image) ?? "no CHARACTER_ID symbol"}) -> " +
                              (name ?? "no name; staying generic") +
                              (provisional ? "  [PROVISIONAL: digit-bearing symbol, this band is unsettled]" : ""));
        return name;
    }
}
