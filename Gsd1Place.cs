using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace SuikodenAccess;

// ─────────────────────────────────────────────────────────────────────────────
// WHERE AM I — place names for Suikoden 1
//
// The mod has had a reliable map IDENTITY for weeks: Gsd1Names.MapKey returns
// a{area}.m{map} from the game's own getters, and it changes for every room, so
// shops and mansion floors are already distinguished. What it has never had is
// a SPEAKABLE name for one, and NAV-STATUS §1.5 forbids reading an internal key
// aloud — "you are in a0.m3" tells a player nothing.
//
// Everything tried before returned the wrong kind of string:
//   FieldBGManager.GetFieldName(area)      -> the asset folder, "a_data"
//   VillageManager.GetVillageNameID(a, v)  -> a text id, and resolving one needs
//                                             an instance of G1_string_id_c
//   Village_c.get_village_name()           -> empty in the the palace map
//
// THE SOURCE THAT WORKS, and it is the §1.9 shape again: find the observation
// the game makes for itself. When the player enters a village the game plays a
// name plate across the top of the screen, and it builds that plate by calling
//
//     WindowManager.PlayVillageNameAnimationHeaderUI(string villageName)
//
// with the display name as its only argument. That is the game stating, in the
// player's own language, where it thinks the player has just arrived — the same
// kind of evidence as "the player stood on this cell" or "VILLAGEOUT said map 1
// and the player landed on map 1". It cannot be reinterpreted, because it is
// already the finished string a sighted player reads.
//
// It was visible in the log all along, one character at a time, as a TMP_Text
// called "sprt_1" typing out G, Gr, Gre ... Town A — the animation this
// method starts. Reading the argument instead of the animation gets the whole
// name at once and cannot catch it half-typed.
//
// The hook obeys the project rules: one deliberately named method, not a sweep;
// a single string value parameter, no by-ref struct; resolved from GSD1.dll BY
// NAME because WindowManager is declared in BOTH games; and applied by hand, so
// this class carries NO [HarmonyPatch] attribute — one on a hand-patched class
// aborts PatchAll and disables the entire mod.
//
// WHAT THE BANNER NAMES, measured and not assumed. In the sampled session it
// fired on every map change inside area 0 and always said town A, so on
// that evidence it names the VILLAGE, not the room. That is why it is recorded
// against the map key it settles on and ALSO offered to the rest of that area:
// a name learned in one room of town A is town A's name. The room
// itself — the armoury, upstairs — has no name in the game data at all, so it
// is the player's to give with N, exactly as marks and NPC names are.
//
// prev_village_name is READ AND LOGGED beside the banner and is deliberately
// NOT spoken. It once read town A and is the obvious candidate, but the
// name says "previous" and nothing has established whether it means the village
// being entered or the one just left. Logging it next to a name the game itself
// displayed is what will settle that in a session or two, at no risk: if it
// always agrees with the banner it can be promoted, and if it lags by one
// village the log will say so. A wrong place name is worse than no place name.
// ─────────────────────────────────────────────────────────────────────────────
internal static class Gsd1PlaceNames
{
    public const string GameSource = "game";
    public const string PlayerSource = "player";

    private sealed class Entry { public string Source, Name; }

    private static readonly Dictionary<string, Entry> _byKey =
        new Dictionary<string, Entry>(StringComparer.Ordinal);
    private static bool _loaded;
    private static string _path;

    private static string FilePath()
    {
        if (_path != null) return _path;
        var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (string.IsNullOrEmpty(dir)) dir = AppContext.BaseDirectory;
        _path = Path.Combine(dir, "suikoden1_places.txt");
        return _path;
    }

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        int n = 0;
        try
        {
            if (File.Exists(FilePath()))
                foreach (var line in File.ReadAllLines(FilePath()))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal)) continue;
                    var parts = line.Split('|');
                    if (parts.Length < 3) continue;
                    var key = parts[0].Trim();
                    var src = parts[1].Trim();
                    var name = parts[2].Trim();
                    if (key.Length == 0 || name.Length == 0) continue;
                    _byKey[key] = new Entry { Source = src, Name = name };
                    n++;
                }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Nav1] place-name load failed: {e.Message}"); }

        // Log the zero: an empty file and a file that was never opened must not
        // produce the same silence.
        Plugin.Logger.LogInfo($"[Nav1] loaded {n} place name(s) from suikoden1_places.txt.");
    }

    private static void Save()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Suikoden Access - Suikoden 1 place names. Safe to edit or share.");
            sb.AppendLine("# <map key> | player or game | <spoken name>");
            sb.AppendLine("# A 'game' row came from the arrival banner and names the village.");
            sb.AppendLine("# A 'player' row was typed in-game with N and always wins.");
            foreach (var kv in _byKey) sb.AppendLine($"{kv.Key} | {kv.Value.Source} | {kv.Value.Name}");
            File.WriteAllText(FilePath(), sb.ToString());
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Nav1] place-name save failed: {e.Message}"); }
    }

    /// <summary>The name recorded for exactly this map, or null.</summary>
    public static string Get(string mapKey, out string source)
    {
        Load();
        source = null;
        if (string.IsNullOrEmpty(mapKey) || !_byKey.TryGetValue(mapKey, out var e)) return null;
        source = e.Source;
        return e.Name;
    }

    public static void Set(string mapKey, string name, string source)
    {
        Load();
        if (string.IsNullOrWhiteSpace(mapKey) || string.IsNullOrWhiteSpace(name)) return;

        // A name the player typed is never overwritten by one the game supplied.
        // The two answer different questions — "which room is this" against
        // "which village is this" — and the player's answer is the specific one.
        if (_byKey.TryGetValue(mapKey, out var had))
        {
            if (had.Name == name && had.Source == source) return;
            if (had.Source == PlayerSource && source == GameSource) return;
        }
        _byKey[mapKey] = new Entry { Source = source, Name = name.Trim() };
        Save();
        Plugin.Logger.LogInfo($"[Nav1] place name recorded: {mapKey} = '{name.Trim()}' ({source})");
    }

    /// <summary>The area prefix of a map key — "a0" out of "a0.m3". Null for the
    /// fingerprint fallback keys, which carry no area.</summary>
    public static string AreaOf(string mapKey)
    {
        if (string.IsNullOrEmpty(mapKey) || mapKey[0] != 'a') return null;
        int dot = mapKey.IndexOf('.');
        return dot > 1 ? mapKey.Substring(0, dot) : null;
    }

    /// <summary>The village name learned anywhere in this map's area. A banner
    /// seen in one room of a town names the town, so the other rooms inherit it
    /// without having to be visited first.</summary>
    public static string AreaName(string mapKey)
    {
        Load();
        var area = AreaOf(mapKey);
        if (area == null) return null;
        string prefix = area + ".";
        foreach (var kv in _byKey)
            if (kv.Value.Source == GameSource && kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                return kv.Value.Name;
        return null;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// The arrival banner. See the note at the top of this file for why this method
// is the name source and why the hook is shaped the way it is.
// NO [HarmonyPatch] ATTRIBUTE ON THIS CLASS — it is patched by hand.
// ─────────────────────────────────────────────────────────────────────────────
internal static class Gsd1VillageBanner
{
    /// <summary>The last name the game displayed, and when.</summary>
    public static string Name { get; private set; }
    public static float At { get; private set; } = -999f;

    /// <summary>How the current candidate name arrived — the method argument, or
    /// the plate's own text. Logged, so the two are never confused.</summary>
    public static string Source { get; private set; }

    /// <summary>True once the patched method has actually been called. It has
    /// NOT been, in any session so far — see the note below.</summary>
    public static bool MethodEverFired { get; private set; }

    public static void Postfix(string __0)
    {
        MethodEverFired = true;
        try
        {
            if (string.IsNullOrWhiteSpace(__0)) return;
            Note(__0, "PlayVillageNameAnimationHeaderUI argument");
        }
        catch { }
    }

    // ── THE HOOK IS INSTALLED AND NEVER FIRES ───────────────────────────────
    //
    // v52 patched WindowManager.PlayVillageNameAnimationHeaderUI successfully —
    // the log says so — and in a session that crossed four maps and displayed the
    // town A plate three times, the postfix ran ZERO times. So the plate is
    // not driven through that method, whatever its name says. The method is left
    // hooked because it costs nothing and would settle the question the moment it
    // ever does fire, and MethodEverFired is reported so that "never called" can
    // never again be mistaken for "never looked".
    //
    // What DOES happen every time is the plate's own TMP_Text, named 'sprt_1',
    // being written one character at a time: T, To, Tow ... Town A. That is
    // the same evidence one step further downstream — still the finished string
    // the game put on screen — so it is taken from there instead, with one guard
    // the method argument would not have needed: a value is only accepted once it
    // has STOPPED CHANGING for half a second, or a name would be recorded as
    // 'Tow'.
    public const string PlateComponent = "sprt_1";
    private const float StableFor = 0.5f;

    public static void Note(string text, string source)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var t = text.Trim();
        if (t == Name) return;                       // same value rewritten
        Name = t;
        Source = source;
        At = Time.realtimeSinceStartup;
    }

    /// <summary>Consume a name the game displayed recently AND has since stopped
    /// changing, or null.</summary>
    public static string Take(float withinSeconds)
    {
        if (Name == null) return null;
        float age = Time.realtimeSinceStartup - At;
        if (age < StableFor || age > withinSeconds) return null;   // still typing, or stale
        var n = Name;
        Name = null;
        return n;
    }
}

internal static class Gsd1Place
{
    // ── hook installation ───────────────────────────────────────────────────
    private static bool _hooked, _gaveUp;
    private static int _tries;
    private static float _nextTry;

    /// <summary>Retry about once a second and then stop. An unthrottled retry
    /// once wrote ~60,000 lines into one session log.</summary>
    public static void TickHooks()
    {
        if (_hooked || _gaveUp) return;
        if (Time.realtimeSinceStartup < _nextTry) return;
        _nextTry = Time.realtimeSinceStartup + 1f;
        if (TryHookBanner()) return;
        if (++_tries >= 60)
        {
            _gaveUp = true;
            Plugin.Logger.LogInfo("[Nav1] WindowManager.PlayVillageNameAnimationHeaderUI not hooked after " +
                                  "60 tries — place names will come only from the player's own N entries.");
        }
    }

    private static bool TryHookBanner()
    {
        try
        {
            // WindowManager is declared in GSD1.dll AND GSD2.dll. FindAnywhere
            // prefers GSD2 and would hand back an unrelated class of the same
            // name — the exact mistake that made every story-flag read report
            // "method not found" for a week.
            var wm = Gsd.FindIn("GSD1.dll", "WindowManager");
            if (wm == null) return false;

            var m = wm.GetMethod("PlayVillageNameAnimationHeaderUI",
                                 BindingFlags.Public | BindingFlags.NonPublic |
                                 BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (m == null)
            {
                _gaveUp = true;
                Plugin.Logger.LogWarning("[Nav1] WindowManager.PlayVillageNameAnimationHeaderUI is NOT DECLARED; " +
                                         "no place names from the game.");
                return false;
            }

            new Harmony("io.github.artemis626.suikodenaccess.place")
                .Patch(m, postfix: new HarmonyMethod(typeof(Gsd1VillageBanner),
                                                     nameof(Gsd1VillageBanner.Postfix)));
            _hooked = true;
            Plugin.Logger.LogInfo("[Nav1] patched WindowManager.PlayVillageNameAnimationHeaderUI " +
                                  "(the arrival banner is the game naming the village itself).");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[Nav1] banner hook failed: {e.Message}");
            return false;
        }
    }

    // ── arrival tracking ────────────────────────────────────────────────────
    // The map key reads null for several frames while an area loads, so an
    // arrival is only real once a non-null key has HELD for a moment. Announcing
    // on the first non-null frame would read out the map on its way in while the
    // one on its way out was still half unloaded.
    private const float SettleSeconds = 0.6f;
    private const float BannerWindow = 15f;

    /// <summary>How long the map key must have been unreadable for the return to
    /// count as arriving from somewhere else rather than stepping through a
    /// door.</summary>
    /// <remarks>
    /// Both look identical from here — the key reads null either way — and the
    /// only thing that separates them is how long it lasts. A doorway blanks it
    /// for a handful of frames; loading a save, a battle or a full-screen scene
    /// blanks it for seconds. Three seconds sits well clear of both, and it errs
    /// towards silence: a missed announcement costs one Shift+M, while a spurious
    /// one talks over the game on every doorway.
    /// </remarks>
    private const float AwaySeconds = 3.0f;

    private static string _pendingKey, _settledKey;
    private static float _pendingAt, _lastSeenAt = -999f;
    private static string _spokenArea;
    private static bool _awayPending;
    private static object _village;

    public static void Tick(object village)
    {
        _village = village;
        float now = Time.realtimeSinceStartup;

        string key = Gsd1Names.MapKey(village);
        if (key == null) return;                        // mid-load: nothing readable

        // Away = the field was unreadable for a while, which is what loading a
        // save, a battle, and a full-screen scene all look like from here.
        //
        // It has to be LATCHED, not recomputed. The gap is only visible on the
        // first readable frame, and an arrival is not declared until the key has
        // held for SettleSeconds after that — by which time this frame's own
        // update has already closed the gap and "was away" would read false at
        // exactly the moment it is needed.
        if (now - _lastSeenAt > AwaySeconds) _awayPending = true;
        _lastSeenAt = now;

        if (key != _pendingKey) { _pendingKey = key; _pendingAt = now; return; }
        if (now - _pendingAt < SettleSeconds) return;
        if (key == _settledKey && !_awayPending) { AbsorbBanner(key); return; }

        bool wasAway = _awayPending;
        _awayPending = false;

        _settledKey = key;
        AbsorbBanner(key);

        string area = AreaName(key);
        bool areaChanged = area != null && area != _spokenArea;

        // Announce after a load, and on entering a differently named village.
        // Walking room to room inside one stays silent: the player asks with
        // Shift+M whenever they want it, and a line on every doorway would be
        // noise on top of the route they are already following.
        if (wasAway || areaChanged)
        {
            _spokenArea = area;
            var said = Describe(key, false);
            Plugin.Logger.LogInfo($"[Nav1] arrived on {key} ({(wasAway ? "after a load or a scene" : "new village")}); " +
                                  $"announcing '{said}'");

            // Every candidate, on EVERY arrival, not only the ones where a name
            // plate happened to play. The palace showed no plate at all and
            // announced "An unnamed place", and the line that would have said
            // which of these calls could have named it was never written —
            // exactly the silence the "log the zero" rule exists to prevent.
            Plugin.Logger.LogInfo($"[Nav1]   candidates: {CandidateReport()}");
            Plugin.Logger.LogInfo($"[Nav1]   name-plate method has fired this session: " +
                                  $"{Gsd1VillageBanner.MethodEverFired}");
            Narration.Label(said);
        }
    }

    /// <summary>Record a banner the game displayed while this map was settling.</summary>
    private static void AbsorbBanner(string key)
    {
        var banner = Gsd1VillageBanner.Take(BannerWindow);
        if (banner == null) return;

        // The plate is the ground truth for this arrival, so this is the one
        // moment every other candidate can be scored for free. All of them are
        // logged beside it and none is spoken until the log shows it agreeing.
        Plugin.Logger.LogInfo($"[Nav1] the game displayed '{banner}' ({Gsd1VillageBanner.Source}); " +
                              $"it settles on {key}");
        Plugin.Logger.LogInfo($"[Nav1]   candidates: {CandidateReport()}");
        Gsd1PlaceNames.Set(key, banner, Gsd1PlaceNames.GameSource);
    }


    // ── SCORING THE OTHER PLACE-NAME CANDIDATES, AUTOMATICALLY ──────────────
    // The banner is ground truth: it is the finished string the game itself put
    // on screen for this arrival. That makes an arrival the one moment when
    // every other candidate can be scored for free, so all of them are read and
    // logged beside it, every time, and none of them is spoken until the log
    // shows it agreeing.
    //
    // The candidates, all static and all value parameters only, so calling them
    // is the same acceptable category as GNDAtariCheck and all of it happens on
    // an arrival rather than per frame:
    //
    //   VillageManager.GetAreaLabel(area)                  -> string
    //   VillageManager.GetVilLabel(area, village)          -> string
    //   VillageManager.GetVillageNameID(area, village)     -> int text id
    //   G1_string_id_h.G1_SID_MSG(int)                     -> string
    //   FieldBGManager.GetFieldName(area)                  -> string
    //   Village_c.get_village_name()                       -> string
    //   Village_c.prev_village_name                        -> field
    //
    // G1_SID_MSG is the piece that was missing. NAV-STATUS says a text id can
    // only be resolved through G1_string_id_c.g1_Sid2Msg, which is an INSTANCE
    // method with no instance to hand — so GetVillageNameID returning 66342 was
    // a dead end. G1_string_id_h.G1_SID_MSG(int) is the same lookup as a STATIC,
    // on a different class. If it turns 66342 into "town A" then every
    // village has a name from static data, including the ones nobody has walked
    // into yet, and the banner becomes the thing that PROVED it rather than the
    // thing the feature depends on.
    private static bool _candResolved;
    private static MethodInfo _areaLabel, _vilLabel, _vilNameId, _sidMsg, _fieldName;

    private static void ResolveCandidates()
    {
        if (_candResolved) return;
        _candResolved = true;
        var vm = Gsd.FindIn("GSD1.dll", "VillageManager") ?? Gsd.FindAnywhere("VillageManager");
        var sid = Gsd.FindIn("GSD1.dll", "G1_string_id_h") ?? Gsd.FindAnywhere("G1_string_id_h");
        var bg = Gsd.FindIn("GSD1.dll", "FieldBGManager") ?? Gsd.FindAnywhere("FieldBGManager");
        // Resolved by exact signature, not by name. G1_SID_MSG is declared twice
        // — (int) and (int, out uint) — and picking whichever the reflection API
        // happens to return first would call the wrong one and log "threw" for a
        // method that works perfectly.
        _areaLabel = FindStatic(vm, "GetAreaLabel", typeof(int));
        _vilLabel = FindStatic(vm, "GetVilLabel", typeof(int), typeof(int));
        _vilNameId = FindStatic(vm, "GetVillageNameID", typeof(int), typeof(int));
        _sidMsg = FindStatic(sid, "G1_SID_MSG", typeof(int));
        _fieldName = FindStatic(bg, "GetFieldName", typeof(int));

        // Log the zero: which ones resolved, so a blank column later reads as
        // "this call is missing" and never as "this call answered nothing".
        Plugin.Logger.LogInfo($"[Nav1] place-name candidates resolved: GetAreaLabel={(_areaLabel != null)} " +
                              $"GetVilLabel={(_vilLabel != null)} GetVillageNameID={(_vilNameId != null)} " +
                              $"G1_SID_MSG={(_sidMsg != null)} GetFieldName={(_fieldName != null)}");
    }

    private static MethodInfo FindStatic(Type t, string name, params Type[] args)
    {
        try
        {
            if (t == null) return null;
            return t.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                               null, args, null);
        }
        catch { return null; }
    }

    private static string Try(MethodInfo m, params object[] args)
    {
        if (m == null) return "n/a";
        try { return Convert.ToString(m.Invoke(null, args)) ?? ""; }
        catch (Exception e) { return "threw:" + e.Message; }
    }

    private static int AreaIdx() => CallIdx("get_area_idx");
    private static int MapIdx() => CallIdx("get_map_idx");

    private static int CallIdx(string name)
    {
        try
        {
            if (_village == null) return -1;
            var m = _village.GetType().GetMethod(name,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                        BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
            return m == null ? -1 : NavProbeShared.ToInt(m.Invoke(_village, null));
        }
        catch { return -1; }
    }

    /// <summary>Every candidate place name, read once and logged side by side
    /// with whatever the game has just displayed. Logging only.</summary>
    public static string CandidateReport(object village)
    {
        if (village != null) _village = village;
        return CandidateReport();
    }

    public static string CandidateReport()
    {
        ResolveCandidates();
        int area = AreaIdx(), map = MapIdx();
        string idTxt = "n/a", resolved = "n/a";
        if (_vilNameId != null)
        {
            try
            {
                int id = NavProbeShared.ToInt(_vilNameId.Invoke(null, new object[] { area, map }));
                idTxt = id.ToString();
                resolved = _sidMsg == null ? "no G1_SID_MSG" : Try(_sidMsg, id);
            }
            catch (Exception e) { idTxt = "threw:" + e.Message; }
        }
        return $"area={area} map={map} | GetAreaLabel='{Try(_areaLabel, area)}' | " +
               $"GetVilLabel='{Try(_vilLabel, area, map)}' | GetVillageNameID={idTxt} -> " +
               $"G1_SID_MSG='{resolved}' | GetFieldName='{Try(_fieldName, area)}' | " +
               $"get_village_name()='{CurrentVillageName()}' | prev_village_name='{PrevVillageName()}'";
    }

    private static string PrevVillageName()
    {
        try { return UiText.Member(_village, "prev_village_name") as string ?? ""; }
        catch { return "<unreadable>"; }
    }

    /// <summary>Village_c.get_village_name() — static, no parameters, so calling
    /// it is the acceptable category, and it is called on an arrival or a
    /// keypress, never per frame.</summary>
    private static string CurrentVillageName()
    {
        try
        {
            if (_village == null) return "";
            var m = _village.GetType().GetMethod("get_village_name",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                        null, Type.EmptyTypes, null);
            return m?.Invoke(null, null) as string ?? "";
        }
        catch { return "<threw>"; }
    }

    private static string AreaName(string key)
    {
        var stored = Gsd1PlaceNames.AreaName(key);
        if (!string.IsNullOrEmpty(stored)) return stored;

        // Nothing learned for this area yet: the game getter is the only other
        // thing that answers with a display name rather than an id.
        var live = CurrentVillageName();
        return string.IsNullOrWhiteSpace(live) || live[0] == '<' ? null : live.Trim();
    }

    /// <summary>What to say about where the player is standing.</summary>
    private static string Describe(string key, bool withHint)
    {
        string source;
        string room = Gsd1PlaceNames.Get(key, out source);

        // A "game" row names the VILLAGE, not this room, so it is not read out
        // as a room name — it is what AreaName already covers.
        if (source != Gsd1PlaceNames.PlayerSource) room = null;
        string area = AreaName(key);

        if (room != null && area != null && !string.Equals(room, area, StringComparison.OrdinalIgnoreCase))
            return $"{room}, in {area}";
        if (room != null) return room;
        if (area != null)
            return withHint ? $"{area}. This part has no name yet; press N to name it." : area;
        return withHint
            ? "This place has no name yet; press N to name it."
            : "An unnamed place";
    }

    /// <summary>Shift+M — say where the player is, on demand, always.</summary>
    public static void Announce()
    {
        string key = Gsd1Names.MapKey(_village);
        if (key == null) { Narration.Label("Still loading"); return; }
        var said = Describe(key, true);
        Plugin.Logger.LogInfo($"[Nav1] Shift+M on {key}: '{said}'");
        Narration.Label(said);
    }

    /// <summary>N — name the room the player is standing in, by ear.</summary>
    public static void NameHere(object village)
    {
        _village = village;
        string key = Gsd1Names.MapKey(village);
        if (key == null) { Narration.Label("Cannot name this place yet"); return; }
        Gsd1TextInput.Open("Naming this area", name =>
        {
            Gsd1PlaceNames.Set(key, name, Gsd1PlaceNames.PlayerSource);
            Narration.Label($"Saved. {Describe(key, false)}");
        });
    }
}
