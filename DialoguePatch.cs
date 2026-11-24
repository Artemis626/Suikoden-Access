using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Share.UI.Window;
using TMPro;

namespace SuikodenAccess;

// Dialogue narration (shared core) + game-2 choices (shared window state) + game-1 choices
// (GSD1 event interpreter). GSD1's interop assembly is loaded explicitly, because our managed
// code otherwise never references a GSD1 type, so that assembly would never load on its own.

internal static class Dlg
{
    public static TMP_Text NameLabel;

    private static readonly SortedDictionary<string, string> Lines =
        new SortedDictionary<string, string>(StringComparer.Ordinal);
    private static float _lastChange;
    private static bool _flushed = true;
    private const float SettleSeconds = 0.25f;

    private static bool IsDialogueLine(string name) =>
        name != null && name.StartsWith("Txt_Command", StringComparison.Ordinal);

    public static void OnSetText(string name, string value, float now)
    {
        if (!IsDialogueLine(name)) return;
        Lines.TryGetValue(name, out var old);
        if (value == old) return;

        bool isFreshLine = old == null || !value.StartsWith(old, StringComparison.Ordinal);
        if (name == "Txt_Command01" && isFreshLine && old != null)
            Lines.Clear();

        Lines[name] = value;
        _lastChange = now;
        _flushed = false;
    }

    public static void Tick(float now)
    {
        if (_flushed || Lines.Count == 0) return;
        if (now - _lastChange < SettleSeconds) return;
        Flush();
        _flushed = true;
    }

    private static void Flush()
    {
        var body = Clean(string.Join(" ", Lines.Values.Where(v => !string.IsNullOrWhiteSpace(v))));
        // Battle damage popups reuse the dialogue components, so a bare number here
        // is a damage figure spoken with a stale speaker name ("<speaker>: 12").
        if (body.Length <= 5 && body.TrimEnd('!').All(char.IsDigit)) return;
        if (string.IsNullOrWhiteSpace(body)) return;
        var speaker = ReadNameLabel();
        var line = string.IsNullOrEmpty(speaker) ? body : speaker.Trim() + ": " + body;
        Plugin.Logger.LogInfo($"[Dialogue] {line}");
        Speech.Speak(line);

        // Suikoden 1 keeps no display name on a map entity, so the navigation
        // target list can only learn who someone is from the dialogue they speak.
        if (!string.IsNullOrEmpty(speaker)) Gsd1Path.NoteSpeaker(speaker);

        // Suikoden 2: which speaker name ID this is, and who stands nearest (research, §17).
        if (!string.IsNullOrEmpty(speaker)) Gsd2Npc.NoteDialogue(speaker.Trim());
    }

    public static string GetLine(int zeroBasedLineIndex)
    {
        if (zeroBasedLineIndex < 0) return "";
        var key = "Txt_Command" + (zeroBasedLineIndex + 1).ToString("D2");
        return Lines.TryGetValue(key, out var v) ? Clean(v) : "";
    }

    public static void ResetNarration() { Lines.Clear(); _flushed = true; }

    public static string SnapshotLines() =>
        string.Join(" | ", Lines.Select(kv => $"{kv.Key}='{kv.Value}'"));

    private static string ReadNameLabel()
    {
        try
        {
            if (NameLabel != null)
            {
                var go = NameLabel.gameObject;
                if (go != null && go.activeInHierarchy)
                {
                    var t = NameLabel.text;
                    if (!string.IsNullOrWhiteSpace(t)) return t;
                }
            }
        }
        catch { }
        return "";
    }

    public static string Clean(string s)
    {
        if (s == null) return null;
        s = s.Replace("\r", " ").Replace("\n", " ");
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        return s.Trim();
    }
}

// Game 2: choice state lives on the shared window.
internal static class ChoiceReader
{
    private const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static bool _active;
    private static int _lastSel = int.MinValue;

    public static bool IsChoosing(UIMessageWindow win) => ReadBool(win, "isChoicesSelecting");

    // Runs every frame a message window exists, so the property lookups are
    // resolved once, not per read.
    private static readonly Dictionary<string, PropertyInfo> _props = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);

    private static object Read(UIMessageWindow w, string n)
    {
        if (!_props.TryGetValue(n, out var p))
        {
            try { p = typeof(UIMessageWindow).GetProperty(n, F); } catch { p = null; }
            _props[n] = p;
        }
        return p?.GetValue(w);
    }

    public static void Tick(UIMessageWindow win)
    {
        int start = ReadInt(win, "choiceStartPosition");
        int sel = ReadInt(win, "selectChoicesNum");
        int lineIdx = sel - start;

        if (!_active)
        {
            _active = true; _lastSel = sel;
            var opt = Dlg.GetLine(lineIdx);
            Plugin.Logger.LogInfo($"[Choice] enter, option {sel}: {opt}");
            Speech.Speak("Choose a dialogue option. " + opt);
            return;
        }
        if (sel != _lastSel)
        {
            _lastSel = sel;
            var opt = Dlg.GetLine(lineIdx);
            Plugin.Logger.LogInfo($"[Choice] option {sel}: {opt}");
            Speech.Speak(opt);
        }
    }

    public static void Reset() { _active = false; _lastSel = int.MinValue; }

    private static bool ReadBool(UIMessageWindow w, string n)
    { try { if (Read(w, n) is bool b) return b; } catch { } return false; }
    private static int ReadInt(UIMessageWindow w, string n)
    { try { if (Read(w, n) is int i) return i; } catch { } return -1; }
}

// Game 1: choices are driven by GSD1's event interpreter.
//   WindowManager.SetChoicesCursor(int selectNo) — highlight moved to selectNo.
//   Event_c.GoSentakuKekka() / GoSentakuKekkaS() — choice confirmed.
internal static class Gsd1Choices
{
    private static bool _active;
    private static int _lastSel = int.MinValue;
    private static float _lastCursorTime;
    private const float TimeoutSeconds = 15f;
    private static Assembly _gsd1;

    public static bool Active => _active;

    public static void TryHook()
    {
        try
        {
            var asm = LoadGsd1();
            if (asm == null) { Plugin.Logger.LogWarning("[G1Choice] GSD1 assembly not available — game 1 choices disabled."); return; }

            var wm = FindType(asm, "WindowManager");
            var ev = FindType(asm, "Event_c");
            Plugin.Logger.LogInfo($"[G1Choice] GSD1 loaded from '{asm.Location}'; WindowManager={(wm?.FullName ?? "null")}, Event_c={(ev?.FullName ?? "null")}");

            var h = new Harmony("io.github.artemis626.suikodenaccess.gsd1");

            var setCursor = wm != null ? AccessTools.Method(wm, "SetChoicesCursor") : null;
            if (setCursor != null)
                h.Patch(setCursor, postfix: new HarmonyMethod(typeof(Gsd1Choices), nameof(SetChoicesCursorPostfix)));
            else
                Plugin.Logger.LogWarning("[G1Choice] SetChoicesCursor not found — game 1 choices disabled.");

            foreach (var mn in new[] { "GoSentakuKekka", "GoSentakuKekkaS" })
            {
                var m = ev != null ? AccessTools.Method(ev, mn) : null;
                if (m != null) h.Patch(m, postfix: new HarmonyMethod(typeof(Gsd1Choices), nameof(GoSentakuKekkaPostfix)));
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[G1Choice] hook setup failed: {e.Message}");
        }
    }

    private static Assembly LoadGsd1()
    {
        if (_gsd1 != null) return _gsd1;
        try
        {
            string dir = null;
            var loc = typeof(UIMessageWindow).Assembly.Location; // shared window's assembly IS loaded
            if (!string.IsNullOrEmpty(loc)) dir = Path.GetDirectoryName(loc);
            if (string.IsNullOrEmpty(dir))
            {
                var pdir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
                dir = Path.GetFullPath(Path.Combine(pdir, "..", "interop"));
            }
            _gsd1 = Assembly.LoadFrom(Path.Combine(dir, "GSD1.dll"));
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[G1Choice] could not load GSD1.dll: {e.Message}");
        }
        return _gsd1;
    }

    private static Type FindType(Assembly asm, string simpleName)
    {
        try { return asm.GetTypes().FirstOrDefault(t => t.Name == simpleName); }
        catch (ReflectionTypeLoadException e) { return e.Types.FirstOrDefault(t => t != null && t.Name == simpleName); }
    }

    private static void SetChoicesCursorPostfix(int __0) => OnCursor(__0);
    private static void GoSentakuKekkaPostfix() => EndChoice("confirmed");

    private static void OnCursor(int selectNo)
    {
        _lastCursorTime = UnityEngine.Time.realtimeSinceStartup;

        if (!_active)
        {
            _active = true; _lastSel = selectNo;
            var opt = Dlg.GetLine(selectNo);
            Plugin.Logger.LogInfo($"[G1Choice] enter, option {selectNo}: {opt} | {Dlg.SnapshotLines()}");
            Speech.Speak("Choose a dialogue option. " + opt);
            return;
        }
        if (selectNo != _lastSel)
        {
            _lastSel = selectNo;
            var opt = Dlg.GetLine(selectNo);
            Plugin.Logger.LogInfo($"[G1Choice] option {selectNo}: {opt}");
            Speech.Speak(opt);
        }
    }

    public static void CheckTimeout(float now)
    {
        if (_active && now - _lastCursorTime > TimeoutSeconds)
            EndChoice("timeout");
    }

    private static void EndChoice(string why)
    {
        if (!_active) return;
        _active = false; _lastSel = int.MinValue;
        Plugin.Logger.LogInfo($"[G1Choice] end ({why})");
        Dlg.ResetNarration();
    }
}

// The ONLY TMP_Text.set_text patch. Every text write in the game passes through
// here, so the component name is read once and routed to both readers: dialogue
// lines (Dlg) and menu descriptions (DescriptionReader in TitleProbe.cs).
[HarmonyPatch(typeof(TMP_Text), "set_text")]
internal static class TmpSetTextPatch
{
    [HarmonyPostfix]
    private static void Postfix(TMP_Text __instance, string __0)
    {
        if (__instance == null || __0 == null) return;
        string name;
        try { name = __instance.name; } catch { return; }
        if (name == "Txt_Name") { Dlg.NameLabel = __instance; return; }
        Dlg.OnSetText(name, __0, UnityEngine.Time.realtimeSinceStartup);
        DescriptionReader.OnSetText(__instance, name, __0);
    }
}

[HarmonyPatch(typeof(UIMessageWindow), "Update")]
internal static class UpdateTickPatch
{
    private static bool _gsd1HookTried;
    private static bool _wasChoosing;

    [HarmonyPostfix]
    private static void Postfix(UIMessageWindow __instance)
    {
        if (!_gsd1HookTried) { _gsd1HookTried = true; Gsd1Choices.TryHook(); }
        if (__instance == null) return;

        float now = UnityEngine.Time.realtimeSinceStartup;
        Gsd1Choices.CheckTimeout(now);

        bool c2 = ChoiceReader.IsChoosing(__instance);
        bool c1 = Gsd1Choices.Active;
        bool choosing = c1 || c2;

        if (c2) ChoiceReader.Tick(__instance);

        if (!choosing)
        {
            if (_wasChoosing) { Dlg.ResetNarration(); ChoiceReader.Reset(); }
            Dlg.Tick(now);
        }
        _wasChoosing = choosing;
    }
}
