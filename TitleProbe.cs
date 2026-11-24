using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Share.UI.Window;
using TMPro;
using UnityEngine;
using Il2CppInterop.Runtime.Injection;

namespace SuikodenAccess;

// ─────────────────────────────────────────────────────────────────────────────
// TITLE READER v40 — launcher (done) + settings window (single-utterance readout).
//
// KEY CORRECTION over v7: 'currentIndex' is NOT the highlighted row — it's the
// position WITHIN the current row's choices (the slider notch). The highlighted
// row is the ARGUMENT to SetItemSelect(int). Reading currentIndex as a row is what
// made sliding a volume recite other rows' names.
//
// Confirmed layout:
//   SetItemLocalizeText(row, label, minusLbl, plusLbl, string[] options) — label
//     (+ possibly the option words, e.g. Fast/Normal/Slow — logged to verify)
//   SetItemSelect(row)                       — highlight moved to 'row'
//   SetItemInSelect(row, n, valueText, ...)  — text value ("Type A", "Windowed")
//   SetItemInSelectCursor(row, cursor)       — slider/segment position for 'row'
//
// Rows hidden at the title screen (Manual, Difficulty, Language Settings, Stop the
// event timer) are simply never passed to SetItemSelect, so they're skipped for
// free. Settings 1 and Settings 2 are different row sets, so the name cache is
// cleared whenever a new page's localize burst arrives.
//
// Slider steps seen: UI Opacity 5, volumes 10 (matches select_num).
// Not yet handled: the Language Settings modal (English + Confirm/Quit).
// ─────────────────────────────────────────────────────────────────────────────
internal static class TitleProbe
{
    // Single source of truth for the version. The logged version was a separate
    // hardcoded literal and drifted to "v16" while the file moved on — which made
    // every log misleading about which build was running.
    public const string Version = "v72";

    public static bool Enabled = true;
    public static bool SpeakChanges = true;


    // Per-reader switches. If a crash ever returns, flip these to false one at a
    // time (rebuild between each) to find the culprit without guesswork.
    public static bool EnableLauncher  = true;
    public static bool EnableConfig    = true;   // Settings 1 and Settings 2
    public static bool EnableTitleMenu = true;   // NEW GAME / CONTINUE
    public static bool EnableGallery   = true;
    public static bool EnableDialog    = true;   // Yes/No confirmations
    public static bool EnableSaveLoad  = true;   // save / load slots
    public static bool EnableNameEntry = true;   // character-name grid
    public static bool EnableBattle    = true;   // battle command windows
    public static bool EnableMainMenu  = true;   // the in-game pause menu

    public static void Install()
    {
        if (!Enabled) return;
        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<TitleProbeHeartbeat>();
            var go = new GameObject("SuikodenAccessProbe");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<TitleProbeHeartbeat>();
            Plugin.Logger.LogInfo($"[TitleProbe] heartbeat installed ({Version}).");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[TitleProbe] heartbeat NOT installed ({e.Message})."); }
    }

    private static bool _launcher, _config, _titleMenu, _gallery, _dialog, _saveLoad, _nameEntry, _battle, _mainMenu;
    private static bool _hookingDone;
    private static float _nextTry;
    private static int _tries;

    // A failed hook used to be retried EVERY FRAME, which flooded the log with tens
    // of thousands of lines and burned work on every frame of the session. Retries
    // are now about once a second and stop after a minute.
    internal static void Tick()
    {
        if (!Enabled) return;

        ConfigReader.Tick();            // per-frame settle for the settings readout
        Narration.Tick();               // deferred announcements
        NameEntryReader.Tick();         // name readback + its confirmation box
        NavProbe.Tick();                // Milestone 5: navigation groundwork (logging only)

        if (_hookingDone) return;
        if (Time.realtimeSinceStartup < _nextTry) return;
        _nextTry = Time.realtimeSinceStartup + 1f;

        if (EnableLauncher  && !_launcher)  _launcher  = LauncherReader.TryHook();
        if (EnableConfig    && !_config)    _config    = ConfigReader.TryHook();
        if (EnableTitleMenu && !_titleMenu) _titleMenu = TitleMenuReader.TryHook();
        if (EnableGallery   && !_gallery)   _gallery   = GalleryReader.TryHook();
        if (EnableDialog    && !_dialog)    _dialog    = DialogReader.TryHook();
        if (EnableSaveLoad  && !_saveLoad)  _saveLoad  = SaveLoadReader.TryHook();
        if (EnableNameEntry && !_nameEntry) _nameEntry = NameEntryReader.TryHook();
        if (EnableBattle    && !_battle)    _battle    = BattleReader.TryHook();
        if (EnableMainMenu  && !_mainMenu)  _mainMenu  = MainMenuReader.TryHook();

        bool all = (!EnableLauncher || _launcher) && (!EnableConfig || _config)
                && (!EnableTitleMenu || _titleMenu) && (!EnableGallery || _gallery)
                && (!EnableDialog || _dialog) && (!EnableSaveLoad || _saveLoad)
                && (!EnableNameEntry || _nameEntry) && (!EnableBattle || _battle)
                && (!EnableMainMenu || _mainMenu);

        if (all || ++_tries >= 60)
        {
            _hookingDone = true;
            Plugin.Logger.LogInfo($"[TitleProbe] hooking finished after {_tries} attempts " +
                $"(launcher={_launcher} config={_config} titleMenu={_titleMenu} " +
                $"gallery={_gallery} dialog={_dialog} saveLoad={_saveLoad} " +
                $"name={_nameEntry} battle={_battle} mainMenu={_mainMenu}).");
        }
    }
}

public class TitleProbeHeartbeat : MonoBehaviour
{
    public TitleProbeHeartbeat(IntPtr ptr) : base(ptr) { }
    public void Update() => TitleProbe.Tick();
}

// Speech ordering. Every Speak() with interrupt cancels the previous one, which is
// why a label followed by its description used to leave only the description
// audible (the Gallery names WERE found — they were just cut off). Labels
// interrupt; details that follow within a moment are queued behind them instead.
// Reads the words out of a UI object: its own text, or every label beneath it.
internal static class UiText
{
    private const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    public static string First(object o)
    {
        var all = All(o);
        return all.Count > 0 ? all[0] : null;
    }

    // Visible labels only, with NO fallback to hidden ones — for polling something
    // that may legitimately be closed, where the hidden fallback would report it as
    // open forever.
    public static List<string> Visible(object o)
    {
        var res = new List<string>();
        try { if (o != null) Collect(o, false, res); } catch { }
        return res;
    }

    public static List<string> All(object o)
    {
        var res = new List<string>();
        try
        {
            if (o == null) return res;

            if (o is string s) { if (!string.IsNullOrWhiteSpace(s)) res.Add(s.Trim()); return res; }

            var direct = o.GetType().GetProperty("text", BF)?.GetValue(o) as string;
            if (!string.IsNullOrWhiteSpace(direct)) { res.Add(direct.Trim()); return res; }

            // VISIBLE labels only. UI objects here carry several alternative layouts
            // at once and hide the ones that don't apply — a save slot holds both the
            // filled and the empty layout, so including hidden ones made a real save
            // end with the empty-slot placeholder caption, and empty slots recite
            // untranslated placeholder strings.
            Collect(o, false, res);

            // Some objects are read a moment before they are shown; fall back to
            // hidden labels only when nothing visible was found.
            if (res.Count == 0) Collect(o, true, res);
        }
        catch { }
        return res;
    }

    // The message of a confirmation window sits beside its buttons, so the parent
    // of a known button object is a reliable way to reach it.
    private static void Collect(object o, bool includeHidden, List<string> into)
    {
        try
        {
            TMP_Text[] labels = null;
            if (o is GameObject go)    labels = go.GetComponentsInChildren<TMP_Text>(includeHidden);
            else if (o is Component c) labels = c.GetComponentsInChildren<TMP_Text>(includeHidden);
            if (labels == null) return;
            foreach (var l in labels)
            { try { if (!string.IsNullOrWhiteSpace(l.text)) into.Add(l.text.Trim()); } catch { } }
        }
        catch { }
    }

    public static object ParentOf(object o)
    {
        try
        {
            Transform t = (o as GameObject)?.transform ?? (o as Component)?.transform;
            return t != null && t.parent != null ? t.parent.gameObject : null;
        }
        catch { return null; }
    }

    // Member lookups are cached per (runtime type, name). This is the hottest path
    // in the mod: the Suikoden 2 recorder and the Suikoden 1 navigator read dozens
    // of fields every frame, and a fresh GetProperty/GetField for each read cost
    // more than the read itself. Resolution is unchanged — property first, then a
    // field of the same name, and a lookup that throws (an ambiguous name declared
    // at two levels) reads as null, as it always did.
    private sealed class Accessor { public PropertyInfo P; public FieldInfo F; public bool Broken; }
    private static readonly Dictionary<(Type, string), Accessor> _accessors = new Dictionary<(Type, string), Accessor>();
    private static readonly Dictionary<Type, PropertyInfo> _indexers = new Dictionary<Type, PropertyInfo>();

    private static Accessor AccessorFor(Type t, string name)
    {
        if (_accessors.TryGetValue((t, name), out var a)) return a;
        a = new Accessor();
        try { a.P = t.GetProperty(name, BF); } catch { a.Broken = true; }
        if (!a.Broken) { try { a.F = t.GetField(name, BF); } catch { a.F = null; } }
        _accessors[(t, name)] = a;
        return a;
    }

    public static object Member(object inst, string name)
    {
        try
        {
            if (inst == null) return null;
            var a = AccessorFor(inst.GetType(), name);
            if (a.Broken) return null;
            return a.P?.GetValue(inst) ?? a.F?.GetValue(inst);
        }
        catch { return null; }
    }

    public static object Element(object list, int idx)
    {
        try
        {
            if (list == null || idx < 0) return null;
            var t = list.GetType();
            if (!_indexers.TryGetValue(t, out var item))
            {
                try { item = t.GetProperty("Item", BF); } catch { item = null; }
                _indexers[t] = item;
            }
            if (item != null) { try { return item.GetValue(list, new object[] { idx }); } catch { } }
            if (list is System.Collections.IEnumerable e)
            { int i = 0; foreach (var o in e) if (i++ == idx) return o; }
        }
        catch { }
        return null;
    }
}

internal static class Narration
{
    private static float _lastLabel = -999f;

    public static void Label(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return;
        _lastLabel = Time.realtimeSinceStartup;
        Speech.Speak(s, true);
    }

    private static string _pendingText;
    private static float _pendingAt;

    // Speak shortly after now. Used where a message is announced at the very moment
    // the next screen loads, and would otherwise be cut off before it is heard.
    public static void LabelAfter(float delay, string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return;
        _pendingText = s;
        _pendingAt = Time.realtimeSinceStartup + delay;
    }

    private static string _pendLabel;
    private static float _pendLabelAt;
    private static string _desc;
    private static float _descAt = -999f;
    private static bool _descSpoken = true;

    /// <summary>A description the game just wrote. Never spoken by the caller — this
    /// class decides, so a description can never be spoken twice (once folded into a
    /// label and again on its own, which is what caused the doubled words).</summary>
    public static void NoteDescription(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return;
        var t = s.Trim();
        float now = Time.realtimeSinceStartup;
        if (t == _desc && now - _descAt < 1.5f) return;    // the same line rewritten
        _desc = t; _descAt = now; _descSpoken = false;
        if (_pendLabel != null) FlushLabel();               // speak it with its label now
    }

    public static bool AwaitingLabel => _pendLabel != null;

    /// <summary>Speak an item and its description as ONE utterance.</summary>
    public static void LabelWithDescription(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return;
        _pendLabel = s.Trim();
        _pendLabelAt = Time.realtimeSinceStartup + 0.2f;
        if (!_descSpoken && Time.realtimeSinceStartup - _descAt < 1.5f) FlushLabel();
    }

    private static void FlushLabel()
    {
        var l = _pendLabel; _pendLabel = null;
        bool useDesc = !_descSpoken && Time.realtimeSinceStartup - _descAt < 1.5f;
        if (useDesc) _descSpoken = true;
        Label(useDesc ? l + ". " + _desc : l);
    }

    public static void Tick()
    {
        float now = Time.realtimeSinceStartup;

        if (_pendingText != null && now >= _pendingAt)
        { var t = _pendingText; _pendingText = null; Label(t); }

        if (_pendLabel != null && now >= _pendLabelAt) FlushLabel();

        // A description with no label behind it is spoken on its own, once.
        if (!_descSpoken && _pendLabel == null && now - _descAt > 0.25f)
        { _descSpoken = true; Detail(_desc); }
    }

    public static void Detail(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return;
        bool recent = Time.realtimeSinceStartup - _lastLabel < 0.8f;
        Speech.Speak(s, !recent);   // queue behind a label just spoken
    }
}

internal static class Gsd
{
    private const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    // Look for a type across EVERY interop assembly. This used to check only four
    // named DLLs, which silently failed for anything living elsewhere —
    // UIBattleMain was invisible for that reason alone. Each assembly's type map is
    // built once, and the assembly a type came from is logged the first time.
    private static readonly Dictionary<string, Type> _typeCache =
        new Dictionary<string, Type>(StringComparer.Ordinal);
    private static readonly Dictionary<string, Dictionary<string, Type>> _typeMaps =
        new Dictionary<string, Dictionary<string, Type>>(StringComparer.Ordinal);
    private static List<string> _interopDlls;

    /// <summary>
    /// "Suikoden One" or "Suikoden II" — whichever game is running, or null if
    /// nothing has yet said which.
    /// </summary>
    /// <remarks>
    /// Named at the main menu so somebody who launched the wrong game from the
    /// launcher finds out immediately, rather than after an unskippable intro.
    ///
    /// Spelled "Suikoden One", not "Suikoden I": the synthesiser reads a lone
    /// roman "I" as the letter, which is wrong and confusing. "II" is read as
    /// "two" correctly and is left as it is.
    ///
    /// The test is always which object has actually been INSTANTIATED, never
    /// which type exists: both GSD1.dll and GSD2.dll are present in every
    /// session, so the types are always findable.
    ///
    /// This used to ask only the field controllers — Village_c for Suikoden 1,
    /// EVENTCON for Suikoden 2. That is a sound test and it answered too late:
    /// the per-game title menu is drawn before either exists, so the menu
    /// announced "unknown" every time. DetectGame keeps them as the first and
    /// most certain signals and falls through to earlier ones.
    /// </remarks>
    public static string RunningGameName()
    {
        string trace;
        var name = DetectGame(out trace);
        Plugin.Logger.LogInfo($"[Gsd] game detect -> {name ?? "unknown"} :: {trace}");
        return name;
    }

    /// <summary>
    /// Every candidate signal, tried in order of certainty, and ALL of them
    /// reported in <paramref name="trace"/> whether they fired or not.
    /// </summary>
    /// <remarks>
    /// The field controllers are the certain signal and the useless one: they
    /// are certain because a live Village_c can only be Suikoden 1, and useless
    /// at the main menu because neither exists yet — which is exactly why the
    /// menu announced "unknown". The chapter managers are the earlier signal.
    /// Each game's boot chapter creates its own, and each exposes it as a static
    /// property with a name unique to that game:
    ///
    ///     Suikoden 1   GSD1.dll   ChapterManager.GR1Instance
    ///     Suikoden 2   GSD2.dll   GRChapterManager.GRInstance
    ///
    /// They are different TYPES, not one shared type with a game field, so
    /// there is no ambiguity to resolve: GSD2 declares no ChapterManager and
    /// GSD1 declares no GRChapterManager (checked in both decompiles).
    ///
    /// Reading a static property on a GSD2 type from a Suikoden 1 session is no
    /// more invasive than what already happens every session — NavProbe patches
    /// GSD2's EVENTCON unconditionally — and it is a property read, not a
    /// Harmony trampoline, so the vtable hazard that crashed the save-dialog
    /// sweep does not apply.
    ///
    /// The loaded scene names are the last resort and are logged even when they
    /// are not used, because they cost nothing and one play session then says
    /// whether they carry the answer. GSDTitleSelect.SceneNames is {GSD1, GSD2},
    /// so a scene called after its game is plausible and unverified.
    ///
    /// Nothing is cached: the player can return to the launcher and start the
    /// other game without restarting the process, and this runs once when a menu
    /// appears, not per frame.
    /// </remarks>
    private static string DetectGame(out string trace)
    {
        var notes = new List<string>();
        string answer = null;

        try
        {
            if (Gsd1Nav.Live) { answer = "Suikoden One"; notes.Add("Village_c LIVE"); }
            else notes.Add("Village_c null");
        }
        catch (Exception e) { notes.Add("Village_c threw: " + e.Message); }

        if (answer == null)
        {
            try
            {
                if (NavProbe.Live) { answer = "Suikoden II"; notes.Add("EVENTCON LIVE"); }
                else notes.Add("EVENTCON null");
            }
            catch (Exception e) { notes.Add("EVENTCON threw: " + e.Message); }
        }

        if (answer == null && LiveStatic("ChapterManager", "GR1Instance", notes)) answer = "Suikoden One";
        if (answer == null && LiveStatic("GRChapterManager", "GRInstance", notes)) answer = "Suikoden II";

        var scenes = LoadedSceneNames();
        notes.Add("scenes=[" + scenes + "]");
        if (answer == null)
        {
            bool s1 = scenes.IndexOf("GSD1", StringComparison.OrdinalIgnoreCase) >= 0;
            bool s2 = scenes.IndexOf("GSD2", StringComparison.OrdinalIgnoreCase) >= 0;
            if (s1 && s2) notes.Add("scene names name BOTH games; not deciding from them");
            else if (s1) { answer = "Suikoden One"; notes.Add("decided from scene name"); }
            else if (s2) { answer = "Suikoden II"; notes.Add("decided from scene name"); }
        }

        trace = string.Join("; ", notes);
        return answer;
    }

    /// <summary>Is a static instance property holding a live object? Appends what
    /// it found — including "not found" and "null" — to <paramref name="notes"/>,
    /// so a signal that was looked for and absent never reads the same as one
    /// that was never looked for.</summary>
    private static bool LiveStatic(string typeName, string propName, List<string> notes)
    {
        try
        {
            var t = FindAnywhere(typeName);
            if (t == null) { notes.Add($"{typeName}: type not found"); return false; }

            var p = t.GetProperty(propName, BindingFlags.Public | BindingFlags.NonPublic |
                                            BindingFlags.Static);
            if (p == null) { notes.Add($"{typeName}.{propName}: not declared"); return false; }

            object v = p.GetValue(null);
            if (v == null) { notes.Add($"{typeName}.{propName}=null"); return false; }

            // A destroyed UnityEngine.Object is still a live managed reference;
            // only Unity's own equality knows it is gone.
            if (v is UnityEngine.Object uo && uo == null)
            { notes.Add($"{typeName}.{propName}=destroyed"); return false; }

            // An Il2Cpp wrapper around a null native pointer is also non-null
            // on the managed side.
            var ptrProp = v.GetType().GetProperty("Pointer", BindingFlags.Public | BindingFlags.Instance);
            if (ptrProp != null && ptrProp.GetValue(v) is IntPtr ip && ip == IntPtr.Zero)
            { notes.Add($"{typeName}.{propName}=null pointer"); return false; }

            notes.Add($"{typeName}.{propName} LIVE");
            return true;
        }
        catch (Exception e) { notes.Add($"{typeName}.{propName} threw: {e.Message}"); return false; }
    }

    private static string LoadedSceneNames()
    {
        try
        {
            var parts = new List<string>();
            int n = UnityEngine.SceneManagement.SceneManager.sceneCount;
            for (int i = 0; i < n; i++)
            {
                var s = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                parts.Add(string.IsNullOrEmpty(s.name) ? "(unnamed)" : s.name);
            }
            return parts.Count == 0 ? "none loaded" : string.Join(",", parts);
        }
        catch (Exception e) { return "unreadable: " + e.Message; }
    }

    /// <summary>Find a type in ONE named assembly, never another.</summary>
    /// <remarks>
    /// FindAnywhere searches assemblies in a fixed preference order and returns
    /// the first hit, which is wrong whenever both games declare a type of the
    /// same name. GAME_WORK is declared in GSD1.dll AND GSD2.dll: FindAnywhere
    /// handed back GSD2's, which has no GetStoryFlagS, so every story-flag read
    /// silently reported "method not found" in a Suikoden 1 session. Use this for
    /// any type that belongs to one game's field code — the two games share their
    /// UI layer and nothing below it (§0), so a name collision there means two
    /// unrelated classes, not two views of one.
    /// </remarks>
    // Cached like FindAnywhere: a lookup used to walk every type in the assembly
    // (GSD2.dll holds thousands) on every call. A miss is cached and logged once.
    private static readonly Dictionary<(string, string), Type> _inCache = new Dictionary<(string, string), Type>();

    public static Type FindIn(string dll, string name)
    {
        if (_inCache.TryGetValue((dll, name), out var hit)) return hit;
        Type found = null;
        try
        {
            var asm = LoadNamed(dll);
            if (asm == null) { Plugin.Logger.LogWarning($"[Gsd] {dll} did not load; cannot find {name}."); return null; }
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types; }
            foreach (var t in types)
                if (t != null && string.Equals(t.Name, name, StringComparison.Ordinal)) { found = t; break; }
            if (found == null) Plugin.Logger.LogInfo($"[Gsd] {name} is NOT declared in {dll}.");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Gsd] FindIn({dll},{name}) failed: {e.Message}"); return null; }
        _inCache[(dll, name)] = found;
        return found;
    }

    public static Type FindAnywhere(string name)
    {
        if (_typeCache.TryGetValue(name, out var cached)) return cached;

        Type found = null;
        foreach (var dll in InteropDlls())
        {
            try
            {
                if (!_typeMaps.TryGetValue(dll, out var map))
                {
                    map = new Dictionary<string, Type>(StringComparer.Ordinal);
                    var asm = LoadNamed(dll);
                    if (asm != null)
                    {
                        Type[] types;
                        try { types = asm.GetTypes(); }
                        catch (ReflectionTypeLoadException e) { types = e.Types; }
                        foreach (var t in types)
                            if (t != null && !map.ContainsKey(t.Name)) map[t.Name] = t;
                    }
                    _typeMaps[dll] = map;
                }
                if (map.TryGetValue(name, out found))
                {
                    Plugin.Logger.LogInfo($"[Gsd] {name} found in {dll}");
                    break;
                }
            }
            catch { }
        }

        _typeCache[name] = found;
        return found;
    }

    // Game assemblies only: the engine and framework DLLs never hold game UI, and
    // loading them would be slow for nothing.
    private static readonly string[] SkipPrefixes =
    {
        "UnityEngine", "Unity.", "System", "mscorlib", "netstandard", "Mono",
        "Microsoft", "Il2Cpp", "BepInEx", "0Harmony", "HarmonyLib", "MonoMod",
        "Newtonsoft", "Cinemachine", "WindowsBase", "Accessibility", "Tizen",
    };

    private static List<string> InteropDlls()
    {
        if (_interopDlls != null) return _interopDlls;
        var list = new List<string>();
        try
        {
            var dir = InteropDir();
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                foreach (var path in Directory.GetFiles(dir, "*.dll"))
                {
                    var file = Path.GetFileName(path);
                    bool skip = false;
                    foreach (var p in SkipPrefixes)
                        if (file.StartsWith(p, StringComparison.OrdinalIgnoreCase)) { skip = true; break; }
                    if (!skip) list.Add(file);
                }

            // The assemblies most likely to hold what we want, first.
            var preferred = new[] { "GSDShare.dll", "ShareUI.dll", "GSD2.dll", "GSD1.dll", "FrameWork.dll" };
            list.Sort((a, b) =>
            {
                int ia = Array.IndexOf(preferred, a); if (ia < 0) ia = int.MaxValue;
                int ib = Array.IndexOf(preferred, b); if (ib < 0) ib = int.MaxValue;
                return ia != ib ? ia.CompareTo(ib) : string.CompareOrdinal(a, b);
            });
            Plugin.Logger.LogInfo($"[Gsd] scanning {list.Count} interop assemblies.");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Gsd] could not list interop: {e.Message}"); }
        _interopDlls = list;
        return list;
    }

    private static string InteropDir()
    {
        try
        {
            var loc = typeof(UIMessageWindow).Assembly.Location;
            if (!string.IsNullOrEmpty(loc)) return Path.GetDirectoryName(loc);
            var pdir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
            return Path.GetFullPath(Path.Combine(pdir, "..", "interop"));
        }
        catch { return null; }
    }

    private static readonly Dictionary<string, Assembly> _asms = new Dictionary<string, Assembly>();

    public static Assembly LoadNamed(string dll)
    {
        if (_asms.TryGetValue(dll, out var cached)) return cached;
        Assembly a = null;
        try
        {
            var dir = InteropDir();
            if (!string.IsNullOrEmpty(dir))
            {
                var path = Path.Combine(dir, dll);
                if (File.Exists(path)) a = Assembly.LoadFrom(path);
            }
        }
        catch { }
        _asms[dll] = a;
        return a;
    }

    public static MethodInfo FindMethod(Type t, string name)
    {
        if (t == null || string.IsNullOrEmpty(name)) return null;
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic |
                               BindingFlags.Instance | BindingFlags.Static;
        try { return t.GetMethod(name, F); }
        catch (AmbiguousMatchException)
        { try { return t.GetMethods(F).FirstOrDefault(m => m.Name == name); } catch { return null; } }
        catch { return null; }
    }


    public static string Read(object inst, string member)
    {
        try
        {
            var t = inst.GetType();
            var p = t.GetProperty(member, BF); if (p != null) return Convert.ToString(p.GetValue(inst));
            var f = t.GetField(member, BF);    if (f != null) return Convert.ToString(f.GetValue(inst));
        }
        catch { }
        return null;
    }
}

// ── Launcher top level (WORKING — unchanged) ─────────────────────────────────
internal static class LauncherReader
{
    private static readonly string[] Methods =
    { "ChangeHeader", "ChangeGallery", "ChangeSetting", "ChangeAdditionalSetting", "ChangeLanguage",
      "SelectUpper", "SelectLower", "ChangeGS1", "ChangeGS2", "ChangeTitle", "SetInitalByState" };
    private static string _last = "";

    public static bool TryHook()
    {
        try
        {
            var lv = Gsd.FindAnywhere("LauncherView"); if (lv == null) return false;
            var h = new Harmony("io.github.artemis626.suikodenaccess.launcher");
            var post = new HarmonyMethod(typeof(LauncherReader), nameof(Post));
            int n = 0;
            foreach (var name in Methods)
            {
                MethodInfo m = null; try { m = Gsd.FindMethod(lv, name); } catch { }
                if (m == null) continue;
                try { h.Patch(m, postfix: post); n++; } catch { }
            }
            Plugin.Logger.LogInfo($"[Launcher] hooked {n} methods.");
            return n > 0;
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Launcher] TryHook failed: {e.Message}"); return false; }
    }

    private static void Post(MethodBase __originalMethod, object[] __args)
    {
        try
        {
            string m = __originalMethod?.Name ?? "";
            string announce = null;
            switch (m)
            {
                case "ChangeHeader":
                    string to = Arg(__args, 1);
                    if (to == "GS1") announce = "Suikoden One"; else if (to == "GS2") announce = "Suikoden II";
                    break;
                case "ChangeGallery":           if (Arg(__args, 0) == "True") announce = "Gallery"; break;
                case "ChangeSetting":           if (Arg(__args, 0) == "True") announce = "Settings"; break;
                case "ChangeAdditionalSetting": if (Arg(__args, 0) == "True") announce = "Settings 2"; break;
                case "ChangeLanguage":          if (Arg(__args, 0) == "True") announce = "Language Settings"; break;
            }
            if (announce != null && announce != _last) { _last = announce; if (TitleProbe.SpeakChanges) Narration.Label(announce); }
        }
        catch { }
    }

    private static string Arg(object[] a, int i)
    { try { return (a != null && a.Length > i) ? Convert.ToString(a[i]) : ""; } catch { return ""; } }
}

// ── Settings window ──────────────────────────────────────────────────────────
// v10: name + description + value are spoken as ONE utterance. Previously each was
// a separate Speak() call, and every call interrupts the last — which is why
// sliders lost their name (their value arrives a moment later than a cached text
// value). Pieces are collected and flushed after a short settle, exactly like the
// dialogue core does. Also: the game sets a row's cursor to -1 when the row loses
// focus, so the last VALID cursor is remembered (that "0 of 10" bug).
internal static class ConfigReader
{
    private const float SettleSeconds = 0.15f;

    private static Type _cw;

    private static readonly Dictionary<int, string>   _names   = new Dictionary<int, string>();
    private static readonly Dictionary<int, string[]> _options = new Dictionary<int, string[]>();
    private static readonly Dictionary<int, string>   _values  = new Dictionary<int, string>();
    private static readonly Dictionary<int, int>      _cursors = new Dictionary<int, int>();
    private static readonly Dictionary<int, string>   _actions = new Dictionary<int, string>();
    private static readonly Dictionary<int, bool>     _sliders = new Dictionary<int, bool>();

    private static int _row = int.MinValue;
    private static string _lastBottomArg = "";
    private static float _lastLoc = -999f, _lastActivity = -999f;

    // Pending announcement.
    private static bool _pending, _pendingFull;
    private static float _pendingSince;
    private static string _pName, _pValue, _lastSpoken = "";

    public static bool Active => (Time.realtimeSinceStartup - _lastActivity) < 0.5f;

    public static bool TryHook()
    {
        try
        {
            // Settings 1 is UIConfigWindow; Settings 2 is a SEPARATE class,
            // UIConfig2Window — which is why Settings 2 never announced anything.
            // Both are tried with the same method names; whatever is missing is
            // logged rather than assumed.
            var one = Gsd.FindAnywhere("UIConfigWindow");
            var two = Gsd.FindAnywhere("UIConfig2Window");
            if (one == null && two == null) return false;

            var h = new Harmony("io.github.artemis626.suikodenaccess.config");
            foreach (var t in new[] { one, two })
            {
                if (t == null) continue;
                _cw = t;
                Hook(h, t, "SetItemLocalizeText",   nameof(LocPost));
                Hook(h, t, "SetItemSelect",         nameof(SelectPost));
                Hook(h, t, "SetItemInSelect",       nameof(InSelPost));
                Hook(h, t, "SetItemInSelectCursor", nameof(CursorPost));
                Hook(h, t, "SetBottomButtonSelect",  nameof(BottomPost));
                Plugin.Logger.LogInfo($"[Config] hooked {t.Name}.");
            }
            return true;
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Config] TryHook failed: {e.Message}"); return false; }
    }

    private static void Hook(Harmony h, Type t, string name, string postfix)
    {
        MethodInfo m = null; try { m = Gsd.FindMethod(t, name); } catch { }
        if (m != null) { try { h.Patch(m, postfix: new HarmonyMethod(typeof(ConfigReader), postfix)); } catch (Exception e) { Plugin.Logger.LogWarning($"[Config] patch {t.Name}.{name} failed: {e.Message}"); } }
        else Plugin.Logger.LogWarning($"[Config] {t.Name}.{name} not found.");
    }

    // SetItemLocalizeText(row, label, minusLbl, plusLbl, string[] options)
    private static void LocPost(object[] __args)
    {
        _lastActivity = Time.realtimeSinceStartup;
        try
        {
            // Labels arrive in a burst per page; a gap means a different page
            // (Settings 1 vs Settings 2), so start that page's cache clean.
            if (Time.realtimeSinceStartup - _lastLoc > 0.5f)
            {
                _names.Clear(); _options.Clear(); _values.Clear(); _cursors.Clear();
                _actions.Clear(); _sliders.Clear();
                _row = int.MinValue; _pending = false; _lastSpoken = "";
                Plugin.Logger.LogInfo("[Config] --- new page ---");
            }
            _lastLoc = Time.realtimeSinceStartup;

            int row = ToInt(__args, 0);
            string label = Str(__args, 1);
            string s2 = Str(__args, 2), s3 = Str(__args, 3);
            var opts = ToStringArray(__args, 4);

            if (!string.IsNullOrWhiteSpace(label)) _names[row] = label.Trim();
            if (opts != null && opts.Length > 0)   _options[row] = opts;

            // s2/s3 are the side labels: "-"/"+" marks a slider row; anything else
            // (View, Quit) is the row's button word — which is its value.
            bool slider = s2 == "-" || s3 == "+";
            _sliders[row] = slider;
            if (!slider && !string.IsNullOrWhiteSpace(s2)) _actions[row] = s2.Trim();

            Plugin.Logger.LogInfo($"[Config] Loc row={row} label='{label}' s2='{s2}' s3='{s3}' opts=[{(opts == null ? "" : string.Join("|", opts))}]");
        }
        catch { }
    }

    // SetItemSelect(row) — the highlighted row.
    private static void SelectPost(object __instance, object[] __args)
    {
        _lastActivity = Time.realtimeSinceStartup;
        try
        {
            int row = ToInt(__args, 0);
            if (row < 0 || row == _row) return;
            _row = row;

            _pending = true; _pendingFull = true; _pendingSince = Time.realtimeSinceStartup;
            _pName  = _names.TryGetValue(row, out var n) ? n : null;
            _pValue = ValueFor(__instance, row);  // may be refined before the flush
        }
        catch { }
    }

    // SetItemInSelect(row, n, valueText, ...) — text value for a row.
    private static void InSelPost(object __instance, object[] __args)
    {
        _lastActivity = Time.realtimeSinceStartup;
        try
        {
            int row = ToInt(__args, 0);
            string val = Str(__args, 2);
            if (val != null) _values[row] = val.Trim();
            if (row == _row) QueueValue(__instance, row);
        }
        catch { }
    }

    // SetItemInSelectCursor(row, cursor) — slider / segment position.
    // cursor == -1 means "row deselected": keep the last valid position.
    private static void CursorPost(object __instance, object[] __args)
    {
        _lastActivity = Time.realtimeSinceStartup;
        try
        {
            int row = ToInt(__args, 0);
            int cur = ToInt(__args, 1);
            if (row < 0 || cur < 0) return;
            if (_cursors.TryGetValue(row, out var prev) && prev == cur) return;   // fires every frame
            _cursors[row] = cur;
            if (row == _row) QueueValue(__instance, row);
        }
        catch { }
    }

    // The bottom "Default" button is NOT a row: it arrives through its own call, so
    // landing on it used to be silent. Selecting it also clears the remembered row,
    // otherwise moving back up looks like "no change" and stays quiet too.
    private static void BottomPost(object __instance, object[] __args)
    {
        _lastActivity = Time.realtimeSinceStartup;
        try
        {
            string a = (__args != null && __args.Length > 0) ? Convert.ToString(__args[0]) : "";
            if (a == _lastBottomArg) return;                    // fires every frame
            _lastBottomArg = a;

            bool selected = a == "True" || (int.TryParse(a, out var iv) && iv >= 0);
            Plugin.Logger.LogInfo($"[Config] bottom button arg={a} selected={selected}");
            if (!selected) { _row = int.MinValue; return; }

            _row = int.MinValue;                                // re-announce on the way back
            _pending = true; _pendingFull = true;
            _pendingSince = Time.realtimeSinceStartup;
            _pName = BottomLabel(__instance, int.TryParse(a, out var bi) ? bi : 0);
            _pValue = null;
        }
        catch { }
    }

    private static bool _bottomTypeLogged;

    // The Default button's caption lives in the window's own bottomButtonList,
    // the same shape as the Gallery's label array.
    private static string BottomLabel(object inst, int idx)
    {
        try
        {
            if (inst == null) return null;
            const BindingFlags BF2 = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var t = inst.GetType();
            object list = t.GetProperty("bottomButtonList", BF2)?.GetValue(inst)
                       ?? t.GetField("bottomButtonList", BF2)?.GetValue(inst);
            if (list == null) return null;

            object element = null;
            var item = list.GetType().GetProperty("Item", BF2);
            if (item != null) { try { element = item.GetValue(list, new object[] { idx }); } catch { } }
            if (element == null && list is System.Collections.IEnumerable en)
            { int i = 0; foreach (var o in en) { if (i++ == idx) { element = o; break; } } }
            if (element == null) return null;

            if (!_bottomTypeLogged)
            { _bottomTypeLogged = true; Plugin.Logger.LogInfo($"[Config] bottom button element type = {element.GetType().FullName}"); }

            // Its own text, or a label somewhere beneath it.
            var direct = element.GetType().GetProperty("text", BF2)?.GetValue(element) as string;
            if (!string.IsNullOrWhiteSpace(direct)) return direct.Trim();

            if (element is Component c)
            {
                var labels = c.GetComponentsInChildren<TMP_Text>(true);
                if (labels != null)
                    foreach (var l in labels)
                    { try { if (!string.IsNullOrWhiteSpace(l.text)) return l.text.Trim(); } catch { } }
            }
        }
        catch { }
        return null;
    }

    // Description text seen while a settings window is open (routed here by the
    // set_text patch instead of being spoken immediately, so it lands in order).
    // The per-row description is drawn on a label named Text_Auto (confirmed:
    // one short sentence of help text per row, such as the wording beside a volume
    // slider or the control-scheme picker).
    // Other labels that pass through here are value texts and the control-diagram
    // captions — not descriptions — so they're ignored.
    private const string DescComponent = "Text_Auto";
    private static string _desc; private static float _descAt = -999f;

    public static void NoteText(string componentName, string text)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (!string.Equals(componentName, DescComponent, StringComparison.Ordinal)) return;
            _desc = text.Trim(); _descAt = Time.realtimeSinceStartup;
            if (_pending) _pendingSince = _descAt;   // let it land before we speak
        }
        catch { }
    }

    private static void QueueValue(object inst, int row)
    {
        var val = ValueFor(inst, row);
        if (string.IsNullOrEmpty(val)) return;
        if (_pending) { _pValue = val; _pendingSince = Time.realtimeSinceStartup; return; }
        _pending = true; _pendingFull = false; _pendingSince = Time.realtimeSinceStartup;
        _pName = null; _pValue = val;
    }

    // Flush once the pieces stop arriving, as a single utterance:
    //   "Name. Description, Value"   (period = longer pause, comma = short one)
    public static void Tick()
    {
        if (!_pending) return;
        if (Time.realtimeSinceStartup - _pendingSince < SettleSeconds) return;
        _pending = false;

        var parts = new List<string>();
        if (_pendingFull && !string.IsNullOrWhiteSpace(_pName)) parts.Add(_pName.Trim());
        if (_pendingFull && !string.IsNullOrWhiteSpace(_desc)
            && Time.realtimeSinceStartup - _descAt < 1.0f) parts.Add(_desc.Trim());
        string head = string.Join(". ", parts);
        string say  = string.IsNullOrEmpty(_pValue)
                    ? head
                    : (string.IsNullOrEmpty(head) ? _pValue : head + ", " + _pValue);

        if (string.IsNullOrWhiteSpace(say) || say == _lastSpoken) return;
        _lastSpoken = say;
        Plugin.Logger.LogInfo($"[Config] say: '{say}'");
        if (TitleProbe.SpeakChanges) Speech.Speak(say);
    }

    // Best available value: the game's own option word, else its value text,
    // else the slider level ("7 of 10").
    private static string ValueFor(object inst, int row)
    {
        bool haveCursor = _cursors.TryGetValue(row, out var cur) && cur >= 0;

        if (haveCursor && _options.TryGetValue(row, out var opts) && opts != null
            && cur < opts.Length && !string.IsNullOrWhiteSpace(opts[cur]))
            return opts[cur].Trim();

        if (_values.TryGetValue(row, out var v) && !string.IsNullOrWhiteSpace(v)) return v;

        // Action rows (Manual, License, Return Title Screen) read their button word.
        if (_actions.TryGetValue(row, out var act) && !string.IsNullOrWhiteSpace(act)) return act;

        // Sliders only. The cursor is 0-based but the game counts from 1.
        bool isSlider = _sliders.TryGetValue(row, out var sl) && sl;
        if (isSlider && haveCursor)
        {
            int max = 0; int.TryParse(Gsd.Read(inst, "select_num"), out max);
            return max > 0 ? $"{cur + 1} of {max}" : (cur + 1).ToString();
        }
        return null;
    }

    private static string[] ToStringArray(object[] a, int i)
    {
        try
        {
            if (a == null || a.Length <= i || a[i] == null) return null;
            var list = new List<string>();
            if (a[i] is System.Collections.IEnumerable e)
                foreach (var o in e) list.Add(o?.ToString());
            return list.Count > 0 ? list.ToArray() : null;
        }
        catch { return null; }
    }

    private static int ToInt(object[] a, int i)  { try { return Convert.ToInt32(a[i]); } catch { return -1; } }
    private static string Str(object[] a, int i) { try { return a != null && a.Length > i ? a[i]?.ToString() : null; } catch { return null; } }
}

// ── Per-game title menu: NEW GAME / CONTINUE / Manual / Back / Exit Game ─────
// Confirmed from a live trace:
//   TitleMenuItem.SetText(s)        — the on-screen words, in index order
//   TitlePanel.SetMenuButton(x)     — initial selection when the screen opens
//   TitlePanel.SelectMenuButton(x)  — the cursor moved to x
// The internal names differ from the display text (the item called Quit reads
// "Back"), so the game's own SetText string is announced, never the enum name.
internal static class TitleMenuReader
{
    private static readonly List<string> _labels = new List<string>();
    private static float _lastText = -999f;
    private static string _lastSpoken = "";

    // Enum order as observed, used only if the argument won't convert to an int.
    private static readonly string[] Order = { "NewGame", "Continue", "Manual", "Quit", "Exit" };

    public static bool TryHook()
    {
        try
        {
            var item  = Gsd.FindAnywhere("TitleMenuItem");
            var panel = Gsd.FindAnywhere("TitlePanel");
            if (item == null || panel == null) return false;

            var h = new Harmony("io.github.artemis626.suikodenaccess.titlemenu");
            int n = 0;
            n += Patch(h, item,  "SetText",          nameof(SetTextPost));
            n += Patch(h, panel, "ShowScreen",       nameof(ScreenPost));
            n += Patch(h, panel, "SetMenuButton",    nameof(SelectPost));
            n += Patch(h, panel, "SelectMenuButton", nameof(SelectPost));
            Plugin.Logger.LogInfo($"[TitleMenu] hooked {n} methods.");
            return n > 0;
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[TitleMenu] TryHook failed: {e.Message}"); return false; }
    }

    private static int Patch(Harmony h, Type t, string name, string postfix)
    {
        try
        {
            var m = Gsd.FindMethod(t, name);
            if (m == null) { Plugin.Logger.LogWarning($"[TitleMenu] {t.Name}.{name} not found."); return 0; }
            h.Patch(m, postfix: new HarmonyMethod(typeof(TitleMenuReader), postfix));
            return 1;
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[TitleMenu] patch {name} failed: {e.Message}"); return 0; }
    }

    // Labels arrive as a burst in index order when the screen is built.
    private static void SetTextPost(object[] __args)
    {
        try
        {
            var s = __args != null && __args.Length > 0 ? __args[0]?.ToString() : null;
            if (string.IsNullOrWhiteSpace(s)) return;
            float now = Time.realtimeSinceStartup;
            if (now - _lastText > 0.5f) { _labels.Clear(); _lastSpoken = ""; }
            _lastText = now;
            _labels.Add(s.Trim());
            Plugin.Logger.LogInfo($"[TitleMenu] label {_labels.Count - 1} = '{s.Trim()}'");
        }
        catch { }
    }

    private static float _cueAt = -999f;

    // ShowScreen(MenuSelect) is the moment the New Game / Continue list appears.
    private static void ScreenPost(object[] __args)
    {
        try
        {
            var screen = (__args != null && __args.Length > 0) ? Convert.ToString(__args[0]) : null;
            if (!string.Equals(screen, "MenuSelect", StringComparison.OrdinalIgnoreCase)) return;
            if (Time.realtimeSinceStartup - _cueAt < 2f) return;
            _cueAt = Time.realtimeSinceStartup;
            _lastSpoken = "";
            var game = Gsd.RunningGameName();
            Plugin.Logger.LogInfo($"[TitleMenu] menu shown (game: {game ?? "unknown"})");
            if (TitleProbe.SpeakChanges)
                Narration.Label((game == null ? "" : game + ". ") +
                                "Main menu. Use up and down arrows to move between options.");
        }
        catch { }
    }

    private static void SelectPost(object[] __args)
    {
        try
        {
            if (__args == null || __args.Length == 0) return;
            var raw = __args[0];
            string name = Convert.ToString(raw);
            int idx = AsInt(raw);
            if (idx < 0) idx = Array.IndexOf(Order, name);

            string say = (idx >= 0 && idx < _labels.Count) ? _labels[idx] : Pretty(name);
            if (string.IsNullOrWhiteSpace(say) || say == _lastSpoken) return;
            _lastSpoken = say;
            Plugin.Logger.LogInfo($"[TitleMenu] select {name} (idx {idx}) -> '{say}'");
            if (!TitleProbe.SpeakChanges) return;
            if (Time.realtimeSinceStartup - _cueAt < 2.5f) Narration.Detail(say);
            else Narration.Label(say);
        }
        catch { }
    }

    private static int AsInt(object o)
    {
        try { return Convert.ToInt32(o); } catch { }
        return -1;
    }

    private static string Pretty(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i]) && !char.IsUpper(s[i - 1])) sb.Append(' ');
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
}

// ── Gallery top menu: Sound Mode / Cinematics / Event Viewer / Endings / Credits
// From the decompiled members of UIGalleryTop01:
//   field 'text', field 'select', property 'Select' (int), Init(), SetSelect(int)
// SetSelect is the cursor method (same shape as SetItemSelect / SelectMenuButton).
// 'text' is most likely the item's own label; everything read is logged so the
// mapping can be corrected in one step if it holds something else.
internal static class GalleryReader
{
    private const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static string _last = "";

    public static bool TryHook()
    {
        try
        {
            var t = Gsd.FindAnywhere("UIGalleryTop01");
            if (t == null) return false;

            var m = Gsd.FindMethod(t, "SetSelect");
            if (m == null) { Plugin.Logger.LogWarning("[Gallery] SetSelect not found."); return false; }

            new Harmony("io.github.artemis626.suikodenaccess.gallery")
                .Patch(m, postfix: new HarmonyMethod(typeof(GalleryReader), nameof(Post)));
            Plugin.Logger.LogInfo("[Gallery] hooked UIGalleryTop01.SetSelect.");
            return true;
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Gallery] TryHook failed: {e.Message}"); return false; }
    }

    private static void Post(object __instance, object[] __args)
    {
        try
        {
            int idx = -1;
            try { if (__args != null && __args.Length > 0) idx = Convert.ToInt32(__args[0]); } catch { }
            string label = ReadLabel(__instance, idx);

            Plugin.Logger.LogInfo($"[Gallery] SetSelect({idx}) text='{label}'");

            if (!string.IsNullOrWhiteSpace(label) && label != _last)
            {
                _last = label;
                if (TitleProbe.SpeakChanges) Narration.Label(label);
            }
        }
        catch { }
    }

    private static bool _typeLogged;

    // 'text' came back empty when read as one value, so it is probably the set of
    // labels rather than a single one. Handle string, TMP label, array/list and
    // indexer, and log the field's real type once so it can be pinned down exactly.
    private static string ReadLabel(object inst, int idx)
    {
        try
        {
            if (inst == null) return null;
            var t = inst.GetType();
            object v = t.GetProperty("text", BF)?.GetValue(inst) ?? t.GetField("text", BF)?.GetValue(inst);
            if (v == null) { if (!_typeLogged) { _typeLogged = true; Plugin.Logger.LogInfo("[Gallery] text field is null."); } return null; }
            if (!_typeLogged) { _typeLogged = true; Plugin.Logger.LogInfo($"[Gallery] text field type = {v.GetType().FullName}"); }
            return Extract(v, idx);
        }
        catch { return null; }
    }

    private static string Extract(object v, int idx)
    {
        try
        {
            if (v == null) return null;
            if (v is string s) return string.IsNullOrWhiteSpace(s) ? null : s.Trim();

            // A TMP label (or anything exposing .text)
            var p = v.GetType().GetProperty("text", BF);
            if (p != null)
            {
                var inner = p.GetValue(v) as string;
                if (!string.IsNullOrWhiteSpace(inner)) return inner.Trim();
            }

            // A collection of labels: take the selected one
            if (idx >= 0)
            {
                var item = v.GetType().GetProperty("Item", BF);
                if (item != null)
                {
                    try { var got = Extract(item.GetValue(v, new object[] { idx }), -1); if (got != null) return got; }
                    catch { }
                }
                if (v is System.Collections.IEnumerable e)
                {
                    int i = 0;
                    foreach (var o in e) { if (i++ == idx) return Extract(o, -1); }
                }
            }
        }
        catch { }
        return null;
    }
}

// ── TitlePanel prompts: Exit confirmation, difficulty, HD/Classic, language ──
// The Exit Game box is NOT UISystemDialog — it belongs to TitlePanel:
//   OpenHardConfirmWindow()          — the box appeared
//   SelectHardConfirmButton(int)     — highlight moved (0 = Yes, 1 = No)
//   fields ConfirmHardText / ConfirmHardYes / ConfirmHardNo hold the wording
// The same panel drives the rest of the New Game flow, so those are read too.
internal static class DialogReader
{
    private static string _last = "";
    private static float _lastAt = -999f;

    public static bool TryHook()
    {
        try
        {
            var tp = Gsd.FindAnywhere("TitlePanel");
            if (tp == null) return false;

            var h = new Harmony("io.github.artemis626.suikodenaccess.dialog");
            int n = 0;
            n += Hook(h, tp, "OpenHardConfirmWindow",   nameof(ConfirmOpened));
            n += Hook(h, tp, "SelectHardConfirmButton", nameof(ConfirmButton));
            n += Hook(h, tp, "SelectDifficultyButton",  nameof(EnumButton));
            n += Hook(h, tp, "SelectModeButton",        nameof(EnumButton));
            n += Hook(h, tp, "SelectLanguageButton",    nameof(LanguageButton));
            n += Hook(h, tp, "SelectTakeOverButton",    nameof(TakeOverButton));
            n += Hook(h, tp, "SetTakeOverWindow",       nameof(TakeOverOpened));
            n += Hook(h, tp, "OpenConfirmWindowCoroutine", nameof(SoftConfirmOpened));

            // Cue for the opening movie: there is no on-screen prompt saying it can
            // be skipped, or that it takes two presses.
            // The skip prompt shown over an opening movie. TitleChapter.MovieStart
            // only covers one of the two games; this insert covers both.
            var mv = Gsd.FindAnywhere("UITitleMovieInsert");
            if (mv != null) n += Hook(h, mv, "OpenInsert", nameof(MovieStarted));

            var tc = Gsd.FindAnywhere("TitleChapter");
            if (tc != null) n += Hook(h, tc, "MovieStart", nameof(MovieStarted));

            // The name screen's confirmation box is one of these; its buttons were
            // announced once and then never tracked.
            // SelectButton only fires if the cursor MOVES, so pressing Enter on the
            // default (Yes) was silent. SetMessage carries the question itself and is
            // a plain string, so it is safe to hook.
            foreach (var cls in new[] { "UISystemDialog", "UIDialog" })
            {
                var d = Gsd.FindAnywhere(cls);
                if (d == null) { Plugin.Logger.LogWarning($"[Dialog] {cls} not found."); continue; }
                n += Hook(h, d, "SelectButton", nameof(SysButtonPost));
                n += Hook(h, d, "SetMessage",   nameof(SysMessagePost));
                n += Hook(h, d, "SetText",      nameof(SysMessagePost));
            }

            // Title screen orientation cue, and the skip cue.
            var ts = Gsd.FindAnywhere("GSDTitleSelect");
            if (ts != null)
            {
                n += Hook(h, ts, "SetInitialLauncherState", nameof(TitleOpened));
                // Cue on CHOOSING a game, not on the movie starting: Suikoden 2 only
                // creates the skip prompt once a key is pressed, so a movie-based
                // trigger cannot fire early there. Confirming a game happens the same
                // way in both.
                n += Hook(h, ts, "CheckDecideSelectContent", nameof(GameChosen));
            }
            Plugin.Logger.LogInfo($"[Dialog] hooked {n} TitlePanel methods.");
            return n > 0;
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Dialog] TryHook failed: {e.Message}"); return false; }
    }

    private static int Hook(Harmony h, Type t, string name, string postfix)
    {
        try
        {
            var m = Gsd.FindMethod(t, name);
            if (m == null) { Plugin.Logger.LogWarning($"[Dialog] {name} not found."); return 0; }
            h.Patch(m, postfix: new HarmonyMethod(typeof(DialogReader), postfix));
            return 1;
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Dialog] patch {name} failed: {e.Message}"); return 0; }
    }

    private static void ConfirmOpened(object __instance)
    {
        try
        {
            var msg = UiText.First(UiText.Member(__instance, "ConfirmHardText"));
            Plugin.Logger.LogInfo($"[Dialog] confirm opened: '{msg}'");
            _last = "";
            if (!string.IsNullOrWhiteSpace(msg)) Say(msg);
        }
        catch { }
    }

    private static void ConfirmButton(object __instance, object[] __args)
    {
        try
        {
            int n = Arg(__args);
            var field = n == 0 ? "ConfirmHardYes" : "ConfirmHardNo";
            var label = UiText.First(UiText.Member(__instance, field)) ?? (n == 0 ? "Yes" : "No");
            Plugin.Logger.LogInfo($"[Dialog] confirm button {n} -> '{label}'");
            // Queue behind the message just spoken instead of cutting it off — the
            // prompt WAS being read, then immediately interrupted by this.
            if (!string.IsNullOrWhiteSpace(label) && label != _last)
            { _last = label; if (TitleProbe.SpeakChanges) Narration.Detail(label); }
        }
        catch { }
    }

    private static void LanguageButton(object __instance, object[] __args)
    {
        try
        {
            int n = Arg(__args);
            var label = UiText.First(UiText.Member(__instance, n == 0 ? "yesButton2" : "noButton2"))
                     ?? (n == 0 ? "Confirm" : "Quit");
            Plugin.Logger.LogInfo($"[Dialog] language button {n} -> '{label}'");
            Say(label);
        }
        catch { }
    }

    private static void TakeOverButton(object[] __args)
    {
        int n = Arg(__args);
        string label = n == 0 ? "Yes" : "No";
        Plugin.Logger.LogInfo($"[Dialog] take over button {n} -> {label}");
        Queue(label);          // queue, so it never cuts off the message above it
    }

    private static void TakeOverOpened(object __instance)
    {
        try
        {
            var msg = string.Join(" ", UiText.All(UiText.Member(__instance, "TakeOverWindow")));
            if (string.IsNullOrWhiteSpace(msg))
                msg = string.Join(" ", UiText.All(UiText.ParentOf(UiText.Member(__instance, "yesButton"))));
            Plugin.Logger.LogInfo($"[Dialog] take over window: '{msg}'");
            _last = "";
            // Delayed: the name-entry screen loads immediately behind this prompt and
            // was cutting it off on first appearance (it only survived on a re-visit).
            if (!string.IsNullOrWhiteSpace(msg)) Narration.LabelAfter(0.5f, msg);
        }
        catch { }
    }

    // The Exit Game box is the SOFT confirm window — a different one from the
    // difficulty warning, which is why OpenHardConfirmWindow never fired for it.
    // Its message lives alongside its Yes/No buttons.
    private static void SoftConfirmOpened(object __instance)
    {
        try
        {
            var parts = UiText.All(UiText.ParentOf(UiText.Member(__instance, "ConfirmYesObject")));
            var msg = string.Join(" ", parts);
            Plugin.Logger.LogInfo($"[Dialog] soft confirm opened: '{msg}'");
            _last = "";
            if (!string.IsNullOrWhiteSpace(msg)) Say(msg);
        }
        catch { }
    }

    // The argument is an internal name ("Decision", "Cancel"), not what is on screen.
    // The dialog reports its own labels as [question, confirm caption, cancel
    // caption], so the caption is taken from there — which also stays correct in
    // other languages.
    private static void SysMessagePost(object __instance, object[] __args)
    {
        try
        {
            string msg = null;
            if (__args != null)
                foreach (var a in __args)
                    if (a is string s && !string.IsNullOrWhiteSpace(s)) { msg = s.Trim(); break; }
            if (string.IsNullOrWhiteSpace(msg))
                msg = string.Join(" ", UiText.Visible(__instance));

            Plugin.Logger.LogInfo($"[Dialog] message: '{msg}'");
            if (string.IsNullOrWhiteSpace(msg) || msg == _last) return;
            _last = msg; _lastAt = Time.realtimeSinceStartup;
            if (TitleProbe.SpeakChanges) Narration.Label(msg);
        }
        catch { }
    }

    /// <summary>
    /// Labels of the confirm dialog currently on screen: [message, decision,
    /// cancel]. Published for SaveLoadReader, which knows which one is
    /// highlighted and this class does not.
    /// </summary>
    internal static List<string> DialogLabels = new List<string>();
    internal static float DialogLabelsAt = -999f;

    /// <summary>The last question read out, so that a dialog whose buttons each
    /// fire this hook asks once rather than twice.</summary>
    private static string _lastQuestion = "";
    private static float _lastQuestionAt = -999f;

    private static void SysButtonPost(object __instance, object[] __args)
    {
        try
        {
            var name = (__args != null && __args.Length > 0) ? Convert.ToString(__args[0]) : null;
            if (string.IsNullOrWhiteSpace(name)) return;

            var parts = UiText.Visible(__instance);
            if (parts.Count > 0) { DialogLabels = parts; DialogLabelsAt = Time.realtimeSinceStartup; }

            // THE QUESTION ITSELF, which was never spoken. In the field this hook
            // is the only one that fires for the pause menu's confirmations — the
            // box warning that quitting to the title screen loses unsaved progress
            // read out as a bare "No" and then a bare "Yes", with the thing being
            // agreed to never said at all. The message has been sitting in
            // parts[0] the whole time; it is logged there on every one of these
            // lines. It is spoken once per question, before the buttons, and
            // remembered so that the second call (this fires once per BUTTON, not
            // once per dialog) does not repeat it.
            bool sameQuestionJustAsked = parts.Count >= 3 && parts[0] == _lastQuestion &&
                                         Time.realtimeSinceStartup - _lastQuestionAt < 2f;
            if (parts.Count >= 3 && !string.IsNullOrWhiteSpace(parts[0]) && !sameQuestionJustAsked)
            {
                _lastQuestion = parts[0];
                _lastQuestionAt = Time.realtimeSinceStartup;
                Plugin.Logger.LogInfo($"[Dialog] question: '{parts[0]}'");
                if (TitleProbe.SpeakChanges) Narration.Label(parts[0]);
            }

            var n = name.ToLowerInvariant();
            string say = null;
            if (parts.Count >= 3)
            {
                if (n.Contains("decision") || n.Contains("decide") || n.Contains("ok") || n.Contains("yes"))
                    say = parts[1];
                else if (n.Contains("cancel") || n.Contains("no"))
                    say = parts[2];
            }
            say ??= Pretty(name);

            Plugin.Logger.LogInfo($"[Dialog] system button {name} -> '{say}' (labels: {string.Join("|", parts)})");

            // This fires once PER BUTTON whenever the dialog restyles, not once
            // per selection, so it announces "Yes" AND "No" every time the
            // highlight moves. On the save overwrite prompt that produced a
            // stream of alternating captions, each one interrupting the last, so
            // every move was heard as a clipped "No" and the question itself
            // never finished. The save screen tracks the real highlight in
            // isConfirmSelect, so while it is up it owns the dialog and this is
            // logged only. Elsewhere — the title-screen difficulty warning — no
            // highlight signal exists at all, so reading both options is still
            // better than reading neither, and the old behaviour stands.
            if (SaveLoadReader.OwnsDialog) return;

            // Queue, never Label: Narration.Label interrupts, and the buttons
            // arrive in the same frame as the question above them. Detail waits
            // its turn, so the question is heard in full and the options follow.
            Queue(say);
        }
        catch { }
    }

    private static void TitleOpened()
    {
        Plugin.Logger.LogInfo("[Dialog] title screen opened");
        _last = "";
        if (TitleProbe.SpeakChanges)
            Narration.LabelAfter(0.6f, "Title screen. Left and right to choose a game, " +
                                       "down for Gallery, Settings and Language.");
    }

    private static float _movieCueAt = -999f;

    private static void GameChosen(object[] __args)
    {
        try
        {
            var which = (__args != null && __args.Length > 0) ? Convert.ToString(__args[0]) : null;
            if (which != "GS1" && which != "GS2") return;   // Gallery / Settings etc.
            Plugin.Logger.LogInfo($"[Dialog] game chosen: {which}");
            MovieStarted();
        }
        catch { }
    }

    private static void MovieStarted()
    {
        // Two different triggers can fire for the same movie; announce once.
        float now = Time.realtimeSinceStartup;
        if (now - _movieCueAt < 3f) return;
        _movieCueAt = now;

        Plugin.Logger.LogInfo("[Dialog] opening movie started");
        _last = "";
        if (TitleProbe.SpeakChanges)
            Narration.LabelAfter(0.4f, "Opening movie. Press Tab twice to skip.");
    }

    private static void Queue(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || s == _last) return;
        _last = s;
        if (TitleProbe.SpeakChanges) Narration.Detail(s);
    }

    // Difficulty (Easy / Normal / Hard) and mode (HD / Classic) arrive as named
    // values, so the name itself is what to say.
    private static void EnumButton(MethodBase __originalMethod, object[] __args)
    {
        try
        {
            var name = (__args != null && __args.Length > 0) ? Convert.ToString(__args[0]) : null;
            if (string.IsNullOrWhiteSpace(name)) return;
            Plugin.Logger.LogInfo($"[Dialog] {__originalMethod?.Name} -> '{name}'");
            var pretty = Pretty(name);
            // Suppress only rapid repeats (this fires per frame). Suppressing the
            // value outright meant re-entering New Game on the same difficulty was
            // silent, because it matched the last thing said minutes earlier.
            float now = Time.realtimeSinceStartup;
            if (pretty == _last && now - _lastAt < 0.5f) { _lastAt = now; return; }
            _last = pretty; _lastAt = now;
            // Spoken together with the description the game writes alongside it —
            // separately, whichever spoke second cancelled the first.
            if (TitleProbe.SpeakChanges) Narration.LabelWithDescription(pretty);
        }
        catch { }
    }

    private static int Arg(object[] a)
    { try { return (a != null && a.Length > 0) ? Convert.ToInt32(a[0]) : 0; } catch { return 0; } }

    private static void Say(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || s == _last) return;
        _last = s;
        if (TitleProbe.SpeakChanges) Narration.Label(s);
    }

    private static string Pretty(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i]) && !char.IsUpper(s[i - 1])) sb.Append(' ');
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
}

// ── Save / load slots ────────────────────────────────────────────────────────
// Every slot's text is written ONCE when the list is built, so reading text as it
// arrives announced whichever slot was written last and then went silent forever.
// Instead the highlighted slot is read as a whole: UISaveLoadBase exposes
// currentIndex and ItemObjectList, so the cursor tells us which slot to read.
internal static class SaveLoadReader
{
    private static string _last = "";
    private static int _lastIndex = int.MinValue;

    public static bool TryHook()
    {
        try
        {
            var t = Gsd.FindAnywhere("UISaveLoadBase");
            if (t == null) return false;

            // UISaveLoadBase does NOT have Increese/DecreeseCurrentIndex — those
            // belong to UIConfigWindow. Several plausible names are tried and the
            // ones that exist are hooked; a miss is reported once, not per frame.
            var h = new Harmony("io.github.artemis626.suikodenaccess.saveload");
            int n = 0;
            foreach (var name in new[] { "SetSelect", "SetItemSelect", "SelectSlot",
                                         "UpdateSelect", "OnUpdateSelect", "SetCursor",
                                         "CursorMove", "ChangeSelect", "Exec" })
            {
                try
                {
                    var m = Gsd.FindMethod(t, name);
                    if (m == null) continue;
                    if (m.GetParameters().Length > 1) continue;   // keep arguments simple
                    h.Patch(m, postfix: new HarmonyMethod(typeof(SaveLoadReader), nameof(Moved)));
                    Plugin.Logger.LogInfo($"[Save] hooked {name}.");
                    n++;
                }
                catch (Exception e) { Plugin.Logger.LogWarning($"[Save] patch {name} failed: {e.Message}"); }
            }
            Plugin.Logger.LogInfo($"[Save] hooked {n} methods on UISaveLoadBase.");
            return n > 0;
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Save] TryHook failed: {e.Message}"); return false; }
    }

    // ── the confirmation dialog ─────────────────────────────────────────────
    // The save prompt, the overwrite prompt and the saved notice were all silent, which is
    // what actually blocked saving: the slot list read fine, and then the player
    // was left in front of an unannounced yes/no.
    //
    // NO NEW HOOK IS TAKEN HERE, and that is deliberate.
    //
    // UISaveLoadBase declares `virtual void Confirm(string msg, string yes,
    // string no)`, which looks like the perfect hook: the question and both
    // captions as plain strings. Patching it CRASHED THE GAME NATIVELY at
    // startup — an AccessViolation inside the generated Confirm trampoline,
    // marshalling one of the three string pointers, the moment an unrelated
    // patch triggered a class initialisation:
    //
    //     il2cpp_string_length -> Il2CppStringToManaged
    //     -> DynamicClass.(il2cpp -> managed) Confirm
    //     -> Share.UI.UISystemInputName..cctor()
    //
    // The cause was patching a base virtual AND its overrides: the hook loop
    // walked UISaveLoad1, UISaveLoad2 and UISaveLoadBase and patched Confirm on
    // all three, putting three trampolines into one vtable chain. That is the
    // same failure the battle work already paid for once ("enumerating a derived
    // class returns inherited methods, so a sweep patches the same method many
    // times over and crashes natively") — a DeclaredOnly filter stops the SAME
    // method being patched twice, but does nothing about a virtual and its
    // overrides. Patching UISaveLoad2 also forced GSD2 class init while game 1
    // was the one running.
    //
    // So the state is read from FIELDS instead, off the Exec() postfix that is
    // already installed and already working. isConfirmSelect, saveResultMessage,
    // saveResultButtonText and isSaveSuccess are all declared on UISaveLoadBase
    // and are plain values. Zero additional hooks, no cross-assembly class init,
    // and nothing new that can crash.
    private static int _lastConfirmSel = int.MinValue;
    private static string _lastResult = "";
    private static string _lastQuestion = "";
    private static float _ownsUntil = -999f;
    private static bool _notedSaveScreen;
    private static float _lastExecAt = -999f;

    /// <summary>True while the save screen is up and tracking isConfirmSelect,
    /// so the generic dialog reader stays quiet and does not talk over it.</summary>
    internal static bool OwnsDialog => Time.realtimeSinceStartup < _ownsUntil;

    /// <summary>A placeholder row, not a caption. Unfilled dialog slots come back
    /// as runs of dashes ("---", "--- --- ---") and were being read aloud.</summary>
    private static bool IsPlaceholder(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return true;
        foreach (var ch in s) if (ch != '-' && ch != ' ' && ch != '—') return false;
        return true;
    }

    /// <summary>Polled from the Exec postfix: highlighted button, then result.</summary>
    private static void TickConfirm(object inst)
    {
        // The save screen is up, so it owns the confirm dialog for the next
        // moment. Refreshed every frame Exec runs; it lapses on its own when the
        // screen closes, so the title screen's own confirm is never muted.
        // A gap since the last Exec means the screen closed and reopened, so the
        // save-point check arms again for the next visit.
        float now = Time.realtimeSinceStartup;
        if (now - _lastExecAt > 1f) _notedSaveScreen = false;
        _lastExecAt = now;
        _ownsUntil = now + 0.5f;

        // UISystemDialog reports its labels as [message, decision, cancel] —
        // confirmed live against the save-overwrite prompt, which reports as
        // [question|confirm|cancel]. Taking
        // them from there rather than scraping the scene means the captions are
        // whatever the game actually drew, in the player's own language.
        var labels = DialogReader.DialogLabels;
        bool fresh = now - DialogReader.DialogLabelsAt < 5f;
        string msg = fresh && labels.Count > 0 && !IsPlaceholder(labels[0]) ? labels[0] : null;
        string yes = fresh && labels.Count > 1 && !IsPlaceholder(labels[1]) ? labels[1] : null;
        string no  = fresh && labels.Count > 2 && !IsPlaceholder(labels[2]) ? labels[2] : null;

        // isConfirmSelect 0 = the decision button, 1 = cancel. Read off a real
        // save: the value alternated 1,0,1,0 as the player moved, and the save
        // succeeded on the press taken while it was 0.
        int sel = NavProbeShared.ToInt(UiText.Member(inst, "isConfirmSelect"));
        string caption = sel == 0 ? yes : no;

        if (msg != null && msg != _lastQuestion)
        {
            _lastQuestion = msg;
            Plugin.Logger.LogInfo($"[Save] confirm: '{msg}' yes='{yes}' no='{no}' isConfirmSelect={sel}");
            _lastConfirmSel = sel;
            // The question AND the option resting under the cursor, as one
            // utterance: spoken separately, the second cancels the first.
            var say = caption == null ? msg : msg + ". " + caption;
            if (TitleProbe.SpeakChanges) Narration.Label(say);
        }
        else if (msg != null && sel != _lastConfirmSel)
        {
            _lastConfirmSel = sel;
            Plugin.Logger.LogInfo($"[Save] isConfirmSelect={sel} -> '{caption}'");
            if (caption != null && TitleProbe.SpeakChanges) Narration.Label(caption);
        }
        else if (msg == null) { _lastQuestion = ""; _lastConfirmSel = int.MinValue; }

        // A save screen that is up while a field event is running identifies that
        // event as a save point — see Gsd1Path.NoteSaveScreenOpened. Fired once
        // per visit, on the first frame Exec runs after the screen appears.
        if (!_notedSaveScreen) { _notedSaveScreen = true; Gsd1Path.NoteSaveScreenOpened(); }

        // The outcome. saveResultMessage is written by the base class itself, so
        // this reports what the game decided rather than assuming success.
        var res = Dlg.Clean(Convert.ToString(UiText.Member(inst, "saveResultMessage")));
        if (!string.IsNullOrWhiteSpace(res) && res != _lastResult)
        {
            _lastResult = res;
            var btn = Dlg.Clean(Convert.ToString(UiText.Member(inst, "saveResultButtonText")));
            Plugin.Logger.LogInfo($"[Save] result: '{res}' button='{btn}' success={UiText.Member(inst, "isSaveSuccess")}");
            if (TitleProbe.SpeakChanges) Narration.Label(string.IsNullOrWhiteSpace(btn) ? res : res + ". " + btn);
        }
        else if (string.IsNullOrWhiteSpace(res)) _lastResult = "";
    }

    // Drops the "|" separators and the bare 1-6 party-slot numbers, and collapses
    // anything repeated back-to-back.
    private static List<string> Tidy(List<string> parts)
    {
        var res = new List<string>();
        foreach (var raw in parts)
        {
            var p = raw?.Trim();
            if (string.IsNullOrEmpty(p)) continue;
            if (p == "|" || p == "/") continue;
            if (p.Length == 1 && char.IsDigit(p[0])) continue;
            if (res.Count > 0 && res[res.Count - 1] == p) continue;
            res.Add(p);
        }
        return res;
    }

    private static void Moved(object __instance)
    {
        try
        {
            TickConfirm(__instance);
            LoadAnnouncer.NoteSaveScreen(__instance);

            // UISaveLoadBase names: the cursor is selectNo (not currentIndex) and the
            // slots are saveLoadItemList (not ItemObjectList).
            int idx = -1;
            foreach (var f in new[] { "selectNo", "SelectNumber", "currentItemNo" })
            {
                var v = Convert.ToString(UiText.Member(__instance, f));
                if (!string.IsNullOrEmpty(v) && int.TryParse(v, out var got)) { idx = got; break; }
            }

            // Exec runs every frame. Nothing below is worth doing unless the
            // highlighted slot actually changed.
            if (idx == _lastIndex) return;
            _lastIndex = idx;

            object list = UiText.Member(__instance, "saveLoadItemList")
                       ?? UiText.Member(__instance, "itemList");
            var slot = UiText.Element(list, idx);
            if (slot != null && !(slot is GameObject) && !(slot is Component))
                slot = UiText.Member(slot, "gameObject") ?? slot;

            string say = string.Join(", ", Tidy(UiText.All(slot)));

            // Exec runs every frame, so log only when it actually changes.
            if (string.IsNullOrWhiteSpace(say) || say == _last) return;
            Plugin.Logger.LogInfo($"[Save] index={idx} -> '{say}'");
            _last = say;
            if (TitleProbe.SpeakChanges) Narration.Label(say);
        }
        catch { }
    }
}

// ── Name entry (both games) ──────────────────────────────────────────────────
// UISystemInputName drives the character grid:
//   UpdateEditorCursorPosition(dx, dy) — MOVEMENT DIRECTION, not a destination
//     (confirmed live: the only values ever passed are 1,0 / -1,0 / 0,1 / 0,-1)
//   m_editorCursorPos — where the cursor actually is; read this after the move
//   m_textDict — the grid, keyed by position, each cell holding its character
//   NameStr    — the name typed so far
//   commandLineY + backspaceX / determineX — the bottom Backspace / Determine row
// Per-character announcement, deliberately: this screen is used once per playthrough
// and predictability matters more than cleverness. Backspace and Determine are named,
// and the name is read back after every change, since that cannot be checked visually.
internal static class NameEntryReader
{
    private static int _lastX = int.MinValue, _lastY = int.MinValue;

    private static object _inst;
    private static string _lastNameValue;
    private static float _nextNameCheck;
    private static float _confirmReadAt = -1f;
    private static float _cueAt = -999f;

    public static bool TryHook()
    {
        try
        {
            var t = Gsd.FindAnywhere("UISystemInputName");
            if (t == null) return false;

            var h = new Harmony("io.github.artemis626.suikodenaccess.nameentry");
            int n = 0;
            n += Hook(h, t, "UpdateEditorCursorPosition", nameof(CursorPost));
            n += Hook(h, t, "SetEditorCursorPosition",    nameof(CursorPost));
            n += Hook(h, t, "Determine",                  nameof(DeterminePost));
            n += Hook(h, t, "Open",                       nameof(OpenPost));
            n += Hook(h, t, "OpenNoAnim",                 nameof(OpenPost));
            Plugin.Logger.LogInfo($"[Name] hooked {n} methods on UISystemInputName.");
            return n > 0;
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Name] TryHook failed: {e.Message}"); return false; }
    }

    private static int Hook(Harmony h, Type t, string name, string postfix)
    {
        try
        {
            var m = Gsd.FindMethod(t, name);
            if (m == null) { Plugin.Logger.LogWarning($"[Name] {name} not found."); return 0; }
            h.Patch(m, postfix: new HarmonyMethod(typeof(NameEntryReader), postfix));
            return 1;
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Name] patch {name} failed: {e.Message}"); return 0; }
    }

    private static void CursorPost(object __instance)
    {
        try
        {
            if (!Position(__instance, out int x, out int y)) return;
            if (x == _lastX && y == _lastY) return;
            _lastX = x; _lastY = y; _inst = __instance;

            string say = Command(__instance, x, y) ?? Spoken(Character(__instance, x, y));
            Plugin.Logger.LogInfo($"[Name] at {x},{y} -> '{say}'");
            if (string.IsNullOrEmpty(say) || !TitleProbe.SpeakChanges) return;

            // Queue behind the opening cue rather than talking over it.
            if (Time.realtimeSinceStartup - _cueAt < 2f) Narration.Detail(say);
            else Narration.Label(say);
        }
        catch { }
    }

    private static bool _posTypeLogged;

    // Where the cursor ended up. The method arguments are only the direction moved.
    private static bool Position(object inst, out int x, out int y)
    {
        x = y = 0;
        foreach (var field in new[] { "m_editorCursorPos", "m_cursor", "m_select" })
        {
            var v = UiText.Member(inst, field);
            if (v == null) continue;

            if (!_posTypeLogged)
            { _posTypeLogged = true; Plugin.Logger.LogInfo($"[Name] {field} type = {v.GetType().FullName}"); }

            if (v is Vector2Int vi) { x = vi.x; y = vi.y; return true; }

            var px = UiText.Member(v, "x"); var py = UiText.Member(v, "y");
            if (px != null && py != null
                && int.TryParse(Convert.ToString(px), out x) && int.TryParse(Convert.ToString(py), out y))
                return true;
        }
        return false;
    }

    // The bottom row holds Backspace and Determine rather than characters.
    private static string Command(object inst, int x, int y)
    {
        try
        {
            if (!TryInt(inst, "commandLineY", out var cy) || y != cy) return null;
            if (TryInt(inst, "determineX", out var dx) && x >= dx) return "Determine";
            if (TryInt(inst, "backspaceX", out var bx) && x >= bx) return "Backspace";
            return null;
        }
        catch { return null; }
    }

    private static string Character(object inst, int x, int y)
    {
        try
        {
            var dict = UiText.Member(inst, "m_textDict");
            if (dict == null) return null;
            var item = dict.GetType().GetProperty("Item",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (item == null) return null;
            var cell = item.GetValue(dict, new object[] { new Vector2Int(x, y) });
            return UiText.First(cell);
        }
        catch { return null; }
    }

    // The name is read a moment LATER, not here: at this point the game has not yet
    // finished updating it, which is why typing and backspacing reported the previous
    // value (usually empty).
    // Typing does NOT go through InputKey — the log showed no readback at all while
    // entering letters. Rather than hunt for the method that does, the name field is
    // watched directly and announced whenever it changes: that covers typing,
    // backspacing and anything else, without needing to know what caused it.
    private static void DeterminePost(object __instance)
    {
        _inst = __instance;
        // UpdateConfirm never fires, but Determine does — and the confirmation box
        // appears right after it, so this is the reliable moment to read it.
        _confirmReadAt = Time.realtimeSinceStartup + 0.35f;
    }

    private static void OpenPost(object __instance)
    {
        _inst = __instance;
        _lastX = int.MinValue; _lastY = int.MinValue;
        _lastNameValue = null; _confirmReadAt = -1f;
        _cueAt = Time.realtimeSinceStartup;
        Plugin.Logger.LogInfo("[Name] screen opened");
        if (TitleProbe.SpeakChanges)
            Narration.Label("Enter a name. Move with the arrow keys and confirm a letter. " +
                            "The bottom row has Backspace and Determine; choose Determine when finished.");
    }

    public static void Tick()
    {
        try
        {
            if (_inst == null) return;
            float now = Time.realtimeSinceStartup;

            // Stop polling once the screen is gone, instead of reading a dead object
            // ten times a second for the rest of the session.
            if (now >= _nextNameCheck)
            {
                var active = UiText.Member(_inst, "isActive");
                if (active is bool b && !b) { _inst = null; _lastNameValue = null; return; }
            }

            // The confirmation box, read a moment after Determine.
            if (_confirmReadAt > 0f && now >= _confirmReadAt)
            {
                _confirmReadAt = -1f;
                var msg = string.Join(" ", UiText.Visible(UiText.Member(_inst, "m_dialog")));
                if (string.IsNullOrWhiteSpace(msg)) msg = "Please confirm.";
                Plugin.Logger.LogInfo($"[Name] confirm: '{msg}'");
                if (TitleProbe.SpeakChanges) Narration.Label(msg);
                return;
            }

            if (now < _nextNameCheck) return;
            _nextNameCheck = now + 0.1f;

            var name = LiveName(_inst);
            if (name == _lastNameValue) return;
            _lastNameValue = name;

            var say = string.IsNullOrWhiteSpace(name) ? "name empty" : name;
            Plugin.Logger.LogInfo($"[Name] name is now '{say}'");
            // Detail, so it follows the letter just announced rather than cutting it off.
            if (TitleProbe.SpeakChanges) Narration.Detail(say);
        }
        catch { }
    }


    // NameStr is only populated when the name is CONFIRMED — during typing it stays
    // empty, which is why every keystroke reported "name empty". The live text comes
    // from the name-field label (m_nameText) or GetText().
    private static string LiveName(object inst)
    {
        try
        {
            if (inst == null) return null;

            // The name is drawn as ONE LABEL PER CHARACTER (hence "U U U" on screen),
            // so reading the first label only ever returned the first letter and the
            // name never appeared to change.
            foreach (var f in new[] { "m_editorWindow", "m_nameWindowRoot", "m_editorWindowCell",
                                      "m_inputNameText", "m_nameText", "m_text" })
            {
                var parts = UiText.Visible(UiText.Member(inst, f));
                if (parts.Count == 0) continue;
                var s = string.Concat(parts).Trim();
                if (!string.IsNullOrWhiteSpace(s)) return Note(f, s);
            }

            var get = Gsd.FindMethod(inst.GetType(), "GetText");
            if (get != null && get.GetParameters().Length == 0)
            {
                var s = Convert.ToString(get.Invoke(inst, null));
                if (!string.IsNullOrWhiteSpace(s)) return Note("GetText", s);
            }

            var ns = Convert.ToString(UiText.Member(inst, "NameStr"));
            if (!string.IsNullOrWhiteSpace(ns)) return Note("NameStr", ns);
        }
        catch { }
        return null;
    }

    private static string _noteSource;

    private static string Note(string source, string value)
    {
        if (source != _noteSource)
        { _noteSource = source; Plugin.Logger.LogInfo($"[Name] live name read from {source}"); }
        return value.Trim();
    }

    // "capital A" vs "a" — a single letter alone gives a synthesiser no way to convey
    // case, and the two are different keys on this grid.
    private static string Spoken(string ch)
    {
        if (string.IsNullOrEmpty(ch)) return ch;
        var t = ch.Trim();
        if (t.Length == 1 && char.IsLetter(t[0]) && char.IsUpper(t[0])) return "capital " + t;
        return t;
    }

    private static bool TryInt(object inst, string member, out int value)
    {
        value = 0;
        var s = Convert.ToString(UiText.Member(inst, member));
        return !string.IsNullOrEmpty(s) && int.TryParse(s, out value);
    }
}

// ── Battle command windows ───────────────────────────────────────────────────
// Established from a class-by-class map of the battle assembly, after several
// rounds of inferring the wrong owner from a merged member list:
//
//   UIBattleMain.MoveCursor(WindowType, int)  — THE cursor method. It names both
//       the window and the row, so one hook covers the top row, the Fight submenu,
//       Items, Runes, Unite and target selection alike.
//   UIBattleMain fields m_command / m_common / m_item / m_magic / m_monster /
//       m_party / m_unite / m_emblem — the window object for each menu.
//   UIBattleWindowBase.itemNameList — that window's row labels.
//   UIBattleManager.SetTipsTextFightCommand / SetTipsTextTopCommand(int) — the
//       description line, which is why descriptions tracked the cursor all along.
//
// UIBattle also has Update*Cursor methods, but it is the presentation layer driven
// by the manager, which is why patching it never intercepted anything.
internal static class BattleReader
{
    private static string _last = "";
    private static float _lastAt = -999f;
    private static readonly HashSet<string> _labelsLogged = new HashSet<string>();
    private static bool _warned;

    public static bool TryHook()
    {
        try
        {
            var main = Gsd.FindAnywhere("UIBattleMain");
            if (main == null)
            {
                if (!_warned) { _warned = true; Plugin.Logger.LogWarning("[Battle] UIBattleMain not found; retrying."); }
                return false;
            }

            var h = new Harmony("io.github.artemis626.suikodenaccess.battle");
            var done = new HashSet<MethodInfo>();
            int n = 0;

            n += One(h, main, "MoveCursor",            nameof(MoveCursorPost), done);
            n += One(h, main, "MoveCursorPartyWindow", nameof(SimpleCursorPost), done);
            n += One(h, main, "MoveCursorMonsterWindow", nameof(SimpleCursorPost), done);

            // The in-battle confirmation box. UIBattleDialog has just OpenWindow
            // and a WindowText field, so the message is read when it opens.
            // UIBattleDialog.OpenWindow was patched but never fired, so the message
            // box is opened through the manager instead.
            var mgr = Gsd.FindAnywhere("UIBattleManager");
            if (mgr != null) n += One(h, mgr, "OpenDialogWindowUI", nameof(MessagePost), done);

            // Entering a fight is otherwise unannounced.
            if (mgr != null) n += One(h, mgr, "CreateBattleUI", nameof(BattleStartPost), done);

            var dlg = Gsd.FindAnywhere("UIBattleDialog");
            if (dlg != null) n += One(h, dlg, "OpenWindow", nameof(DialogPost), done);

            // Logs which window opened, so a confirmation window that never moves its
            // cursor (you press Enter on the default) can still be identified.
            n += One(h, main, "OpenWindow", nameof(WindowOpenedPost), done);

            // Some attacks and items hit every enemy or the whole party rather than
            // one target, which is invisible without the screen.
            if (mgr != null) n += One(h, mgr, "SetCustomCursorUI", nameof(TargetTypePost), done);
            var ub = Gsd.FindAnywhere("UIBattle");
            if (ub != null) n += One(h, ub, "SetCustomCursor", nameof(TargetTypePost), done);

            Plugin.Logger.LogInfo($"[Battle] hooked {n} methods on UIBattleMain.");
            return n > 0;
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Battle] TryHook failed: {e.Message}"); return false; }
    }

    // Declared on this type only, patched once, and a miss is reported rather than
    // silently returning zero (which previously looked like a runtime problem).
    private static int One(Harmony h, Type t, string name, string postfix, HashSet<MethodInfo> done)
    {
        try
        {
            var m = t.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic |
                                      BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (m == null) { Plugin.Logger.LogWarning($"[Battle] {t.Name}.{name} NOT DECLARED."); return 0; }
            if (m.IsAbstract || !done.Add(m)) return 0;
            h.Patch(m, postfix: new HarmonyMethod(typeof(BattleReader), postfix));
            Plugin.Logger.LogInfo($"[Battle] patched {t.Name}.{name}");
            return 1;
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Battle] patch {t.Name}.{name} failed: {e.Message}"); return 0; }
    }

    // MoveCursor(WindowType window, int index)
    private static void MoveCursorPost(object __instance, object[] __args)
    {
        try
        {
            if (__args == null || __args.Length < 2) return;
            int index = Convert.ToInt32(__args[1]);
            Announce(__instance, __args[0], Convert.ToString(__args[0]), index);
        }
        catch { }
    }

    // MoveCursorPartyWindow(int) / MoveCursorMonsterWindow(int)
    private static void SimpleCursorPost(object __instance, MethodBase __originalMethod, object[] __args)
    {
        try
        {
            if (__args == null || __args.Length < 1) return;
            int index = Convert.ToInt32(__args[0]);
            string window = (__originalMethod?.Name ?? "").Contains("Party") ? "Party" : "Monster";
            Announce(__instance, null, window, index);
        }
        catch { }
    }

    // OpenDialogWindowUI(string message) — the in-battle confirmation box.
    private static void MessagePost(object[] __args)
    {
        try
        {
            var msg = (__args != null && __args.Length > 0) ? Convert.ToString(__args[0]) : null;
            Plugin.Logger.LogInfo($"[Battle] message box: '{msg}'");
            if (string.IsNullOrWhiteSpace(msg)) return;
            _last = ""; _lastAt = 0f;
            if (TitleProbe.SpeakChanges) Narration.Label(msg.Trim());
        }
        catch { }
    }

    private static string _lastTargetType = "";
    private static float _battleStartAt = -999f;
    private static float _commandOpenedAt = -999f;

    private static void BattleStartPost()
    {
        float now = Time.realtimeSinceStartup;
        if (now - _battleStartAt < 5f) return;
        _battleStartAt = now;
        _last = ""; _lastTargetType = "";
        Plugin.Logger.LogInfo("[Battle] battle started");
        if (TitleProbe.SpeakChanges) Narration.Label("Battle started");
    }

    // SetCustomCursor(TargetSelectType type, bool, int)
    private static void TargetTypePost(object[] __args)
    {
        try
        {
            var type = (__args != null && __args.Length > 0) ? Convert.ToString(__args[0]) : null;
            if (string.IsNullOrWhiteSpace(type) || type == _lastTargetType) return;
            _lastTargetType = type;
            Plugin.Logger.LogInfo($"[Battle] target type: {type}");

            var t = type.ToLowerInvariant();
            string say = null;
            if (t.Contains("all") || t.Contains("whole"))
                say = t.Contains("party") || t.Contains("player") || t.Contains("friend")
                    ? "All allies" : "All enemies";
            else if (t.Contains("row"))    say = "Whole row";
            else if (t.Contains("column")) say = "Whole column";
            else if (t.Contains("self"))   say = "Self";

            if (say != null && TitleProbe.SpeakChanges) Narration.Label(say);
        }
        catch { }
    }

    private static void WindowOpenedPost(object[] __args)
    {
        try
        {
            var w = (__args != null && __args.Length > 0) ? Convert.ToString(__args[0]) : null;
            Plugin.Logger.LogInfo($"[Battle] window opened: {w}");

            // The Unit row that follows a Command window opening identifies whose
            // turn it is — see Announce.
            if (string.Equals(w, "Command", StringComparison.OrdinalIgnoreCase))
                _commandOpenedAt = Time.realtimeSinceStartup;
        }
        catch { }
    }

    private static void DialogPost(object __instance, object[] __args)
    {
        try
        {
            var passed = (__args != null && __args.Length > 0) ? Convert.ToString(__args[0]) : null;
            var shown = string.Join(" ", UiText.Visible(UiText.Member(__instance, "WindowObject")));
            var say = !string.IsNullOrWhiteSpace(shown) ? shown : passed;

            Plugin.Logger.LogInfo($"[Battle] dialog opened: arg='{passed}' text='{shown}'");
            if (string.IsNullOrWhiteSpace(say)) return;
            _last = say; _lastAt = Time.realtimeSinceStartup;
            if (TitleProbe.SpeakChanges) Narration.Label(say.Trim());
        }
        catch { }
    }

    // MoveCursor runs every frame while a window is open. Rebuilding the label
    // list (window lookup, item list, possibly every TMP_Text under the window)
    // each frame was the expensive part, so while the cursor stays on the same row
    // it is rebuilt at most ten times a second; a label that changes under a still
    // cursor is still caught within 0.1 s.
    private static string _lastRow = "";
    private static float _lastReadAt = -999f;

    private static void Announce(object main, object windowKey, string window, int index)
    {
        float t0 = Time.realtimeSinceStartup;
        string row = window + "#" + index;
        if (row == _lastRow && t0 - _lastReadAt < 0.1f && t0 - _lastAt < 0.4f) { _lastAt = t0; return; }
        _lastRow = row; _lastReadAt = t0;

        var labels = LabelsOf(main, windowKey, window);
        string say = (index >= 0 && index < labels.Count) ? labels[index] : null;

        if (string.IsNullOrWhiteSpace(say)) return;

        // Three enemies of the same kind share one name and are indistinguishable,
        // so repeated labels get their position among the duplicates.
        say = Disambiguate(labels, index, say);

        // A Unit row right after the Command window opens is the character now
        // choosing an action, rather than a target being picked.
        if (string.Equals(window, "Unit", StringComparison.OrdinalIgnoreCase) &&
            Time.realtimeSinceStartup - _commandOpenedAt < 1.5f)
        {
            var who = say.Split(',')[0].Trim();
            if (!string.IsNullOrEmpty(who)) say = who + "'s turn";
        }

        // Keyed on the row, not just the text: moving between two identically named
        // enemies is a real move and must be announced.
        string key = window + "#" + index + "#" + say;
        float now = Time.realtimeSinceStartup;
        // MoveCursor runs every frame while a window is open, so logging before this
        // check wrote the same line over a thousand times in one battle.
        if (key == _last && now - _lastAt < 0.4f) { _lastAt = now; return; }
        _last = key; _lastAt = now;
        Plugin.Logger.LogInfo($"[Battle] {window}[{index}] -> '{say}' ({labels.Count} labels)");

        // Row first, then the description underneath it, as one utterance.
        if (TitleProbe.SpeakChanges) Narration.LabelWithDescription(say);
    }

    private static string Disambiguate(List<string> labels, int index, string say)
    {
        int total = 0, ordinal = 0;
        for (int i = 0; i < labels.Count; i++)
        {
            if (!string.Equals(labels[i], say, StringComparison.Ordinal)) continue;
            total++;
            if (i == index) ordinal = total;
        }
        return total > 1 && ordinal > 0 ? $"{say} {ordinal}" : say;
    }

    // The rows of the highlighted window. UIBattleMain keeps a dictionary of windows
    // keyed by WindowType, which is used in preference to any hand-written map of
    // field names: that map had "Monster" and "Party" while the real values are
    // "Enemy" and "Unit", so those two windows silently returned nothing.
    private static readonly Dictionary<string, string> WindowFallback =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "Command", "m_command" }, { "Common", "m_common" }, { "Item",   "m_item"   },
        { "Magic",   "m_magic"   }, { "Enemy",  "m_monster"}, { "Monster","m_monster"},
        { "Unit",    "m_unite"   }, { "Party",  "m_party"  }, { "Unite",  "m_unite"  },
        { "Emblem",  "m_emblem"  }, { "Bag",    "m_bag"    },
    };

    private static List<string> LabelsOf(object main, object windowKey, string window)
    {
        var res = new List<string>();
        try
        {
            object win = null;
            string how = null;

            // Keyed lookup first.
            if (windowKey != null)
                foreach (var dictField in new[] { "m_windowListDict", "m_windowBasisDict" })
                {
                    var dict = UiText.Member(main, dictField);
                    if (dict == null) continue;
                    var item = dict.GetType().GetProperty("Item",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (item == null) continue;
                    try
                    {
                        win = item.GetValue(dict, new object[] { windowKey });
                        if (win != null) { how = dictField; break; }
                    }
                    catch { }
                }

            // Named field as a fallback.
            if (win == null && WindowFallback.TryGetValue(window ?? "", out var field))
            {
                win = UiText.Member(main, field);
                if (win != null) how = field;
            }
            if (win == null) return res;

            var names = new List<string>();
            var list = UiText.Member(win, "itemNameList");
            if (list is System.Collections.IEnumerable e && !(list is string))
                foreach (var o in e)
                {
                    var s = o as string ?? UiText.First(o);
                    if (!string.IsNullOrWhiteSpace(s)) names.Add(s.Trim());
                }

            if (names.Count > 0) { res = names; how += "/itemNameList"; }
            else { res = Rows(UiText.Visible(win)); how += "/visible"; }

            if (res.Count > 0 && _labelsLogged.Add(window ?? "?"))
                Plugin.Logger.LogInfo($"[Battle] {window} via {how}: {string.Join(" | ", res)}");
        }
        catch { }
        return res;
    }

    // The Items and Runes windows draw a name and a quantity as separate labels, so
    // a flat list put the cursor on "6" instead of the second item. A number
    // following a name is folded into that name as its quantity.
    private static List<string> Rows(List<string> labels)
    {
        var res = new List<string>();
        foreach (var raw in labels)
        {
            var s = raw?.Trim();
            if (string.IsNullOrEmpty(s) || s == "---" || s == "|") continue;

            if (IsNumber(s))
            {
                if (res.Count > 0) res[res.Count - 1] = res[res.Count - 1] + ", " + s;
                continue;               // a lone number is never a row of its own
            }
            res.Add(s);
        }
        return res;
    }

    private static bool IsNumber(string s)
    {
        foreach (var c in s) if (!char.IsDigit(c)) return false;
        return s.Length > 0;
    }
}

// Reads the description line under every menu (Text_Auto). Called from the ONE
// TMP_Text.set_text postfix (TmpSetTextPatch in DialoguePatch.cs), which reads the
// component's name once for both dialogue and descriptions: every text write in
// the game passes through it, and each name read copies an Il2Cpp string.
// If descriptions ever go silent while dialogue still works, check that call.
internal static class DescriptionReader
{
    // Last text per component instance. Bounded: the game creates and destroys
    // text objects with every scene, and an id that never comes back would
    // otherwise be held for the whole session.
    private static readonly Dictionary<int, string> _spoken = new Dictionary<int, string>();
    private const int MaxTracked = 4096;

    // Silent components are logged so new ones can be found and whitelisted, but
    // each (component, text) pair only once: every UI write in the game passes
    // through here, and logging repeats (the same "LV" six times per menu build)
    // was disk I/O on every screen change for no new information.
    private static readonly HashSet<string> _loggedSilent = new HashSet<string>(StringComparer.Ordinal);

    // Same text from a DIFFERENT component moments later is a duplicate too — games
    // often draw a second copy of a label (shadow / reflection), which is why the
    // Gallery description was heard twice.
    private static string _lastText = "";
    private static float _lastTextAt = -999f;

    // Loading screens show a rotating tip on the SAME component as every menu
    // description (Text_Auto), so the tip cannot be silenced by name. TipsText
    // appears alongside it and is used as the "a loading screen is up" signal:
    // announce "Loading" once and stay quiet for a few seconds.
    private static float _loadingUntil = -999f;
    private const string LoadingSignal = "TipsText";

    // Confirmed from a live trace, so this no longer guesses by shape.
    //   Text_Auto  — the description line under every menu (title AND in-game)
    //   Txt_*      — the save-slot fields and their two error messages
    // Deliberately excluded: 'Text' (echoes captions the readers already speak,
    // and left stale fragments mid-scene-change) and 'index_1' (a typewriter
    // field that spoke one letter at a time while a save loaded).
    private static readonly HashSet<string> Speakable = new HashSet<string>(StringComparer.Ordinal)
    {
        "Text_Auto", "Text_Auto02",        // the description line under every menu
        "index_0",                         // confirmation questions, which are
                                           // typed one character at a time, so the
                                           // settle in Narration speaks only the
                                           // finished line
        // Deliberately NOT here: Txt_Invalid / Txt_NowLoading. They belong to
        // EMPTY slots and are written when the list builds, which is why an
        // unrelated empty-slot caption was announced.
    };

    // TextMeshPro markup ("<color=#FFFCF4>...</color>") would otherwise be
    // read out as tag soup.
    private static string StripTags(string s)
    {
        if (string.IsNullOrEmpty(s) || s.IndexOf('<') < 0) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        bool inTag = false;
        foreach (var c in s)
        {
            if (c == '<') { inTag = true; continue; }
            if (c == '>') { inTag = false; continue; }
            if (!inTag) sb.Append(c);
        }
        return sb.ToString();
    }

    internal static void OnSetText(TMP_Text __instance, string nm, string __0)
    {
        if (!TitleProbe.Enabled || !TitleProbe.SpeakChanges || __instance == null || __0 == null) return;

        try
        {
            if (nm == "active_txt" || nm == "inactive_txt" || nm == "menu_txt" || nm == "reflect_txt") return;
            if (nm == "Txt_Name" || nm.StartsWith("Txt_Command", StringComparison.Ordinal)) return;

            // While a settings window is open the description is collected, not
            // spoken here, so it can be folded into the single readout.
            if (ConfigReader.Active) { ConfigReader.NoteText(nm, __0); return; }

            int id = __instance.GetInstanceID();
            if (_spoken.TryGetValue(id, out var prev) && prev == __0) return;
            if (_spoken.Count >= MaxTracked) _spoken.Clear();
            _spoken[id] = __0;

            var text = StripTags(__0).Trim();
            if (string.IsNullOrEmpty(text)) return;

            // The village name plate. Suikoden 1 types it out one character at a
            // time through this component, and it is the only place the game
            // states a place's display name in a form that can be read. It is
            // NOT spoken from here — Gsd1Place decides, once the value has
            // stopped changing and a map key has settled under it.
            if (nm == Gsd1VillageBanner.PlateComponent)
            {
                Gsd1VillageBanner.Note(text, "the '" + nm + "' name plate");
                Plugin.Logger.LogInfo($"[Nav1] name plate '{nm}' now reads '{text}'");
                return;
            }

            if (nm == LoadingSignal)
            {
                bool fresh = Time.realtimeSinceStartup > _loadingUntil;
                _loadingUntil = Time.realtimeSinceStartup + 6f;
                Plugin.Logger.LogInfo($"[Detail] loading screen (tip: '{text}')");
                if (fresh) LoadAnnouncer.Announce("loading-screen tip");
                return;
            }

            // Everything is logged; only confirmed components are spoken.
            if (!Speakable.Contains(nm))
            {
                if (_loggedSilent.Count >= MaxTracked) _loggedSilent.Clear();
                if (_loggedSilent.Add(nm + "\u0001" + text)) Plugin.Logger.LogInfo($"[Detail] (silent) name='{nm}' text='{text}'");
                return;
            }

            // The pause menu is built hidden while a save loads, and the build
            // writes its Log command's help line here (LoadAnnouncer).
            if (LoadAnnouncer.Swallow(__instance)) { Plugin.Logger.LogInfo($"[Detail] (hidden menu, silent) '{text}'"); return; }

            // Menu descriptions are worth hearing; loading-screen tips are not.
            if (Time.realtimeSinceStartup < _loadingUntil)
            { Plugin.Logger.LogInfo($"[Detail] (loading, silent) '{text}'"); return; }
            if (text == _lastText && Time.realtimeSinceStartup - _lastTextAt < 1.5f) return;
            _lastText = text; _lastTextAt = Time.realtimeSinceStartup;

            // Logged so the handful of components worth speaking can be whitelisted
            // and the stale leftovers (a half-read label during a scene change)
            // silenced for good.
            Plugin.Logger.LogInfo($"[Detail] name='{nm}' text='{text}'");
            Narration.NoteDescription(text);   // Narration decides when it is spoken
        }
        catch { }
    }
}
