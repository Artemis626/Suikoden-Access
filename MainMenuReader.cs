using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SuikodenAccess;

// ─────────────────────────────────────────────────────────────────────────────
// THE IN-GAME MENUS (Milestone 3–4 gap 6, now closed for the top level)
//
// Until now the pause menu read only the help line at the bottom of the screen —
// one sentence describing what the highlighted command does, for a command whose
// NAME was never spoken. STATUS-2 §4 says why: menu labels are
// written ONCE when the screen is built, so the text hook sees seven labels in a
// row at build time and nothing at all when the highlight moves. The missing
// piece is always the same one — the cursor method — and the §1 method finds it
// by mapping classes to the methods they DECLARE rather than guessing.
//
// What that map turned up, all in ShareUI.dll and all value parameters only:
//
//   UIMainMenuCommon.SetCursor(int)              the left command column
//   UIMainMenuPartyList.SetCursor(int, int)      the party rows
//   UIWindowList.SetSelectCell(int) / SetSelectOneCell(int)
//   UIWindowListTop.SetSelectCell(int) / SetSelectOneCell(int)
//
// and the shape behind them: every list in this game is a UIWindowList (or its
// sibling UIWindowListTop — they both derive from UIWindowBasis, neither from
// the other) holding a List<UIWindowListCell>, and a cell owns its own label
// objects. So ONE pair of hooks reads every list-shaped window rather than one
// reader per menu, the same economy that made UIBattleMain.MoveCursor cover all
// of battle.
//
// WHY IT IS GATED ON THE MENU BEING OPEN. Battle uses UIWindowList too —
// UIBattleMain.m_windowListDict is a Dictionary<WindowType, UIWindowList> — and
// battle already has a working reader on UIBattleMain.MoveCursor. Speaking from
// both would say every battle command twice. So UIMainMenu.Open()/Close() (no
// arguments at all, the safest patch there is) maintain a flag, and the list
// hooks speak only while a pause menu is actually up. Shops and storage are also
// UIWindowLists and are deliberately left silent for now for the same reason:
// they need their own pass, not a hook that fires everywhere and is right
// sometimes.
//
// SetSelectCell and SetSelectOneCell are BOTH hooked because which one moves the
// cursor is not known — "select one cell" reads like a cursor and "select cell"
// like a multi-select for the formation screen, but that is an argument about
// names, not a measurement. Both are hooked, both are logged with the class and
// method that fired, and speech is funnelled through
// Narration.LabelWithDescription, which coalesces everything arriving inside
// 0.2 s into one utterance — so if both fire for one keypress it is still spoken
// once, and the log says which to keep.
//
// Rules obeyed: a deliberately named set of methods, never a swept one; every
// argument a plain int or bool, no by-ref struct; two sibling classes rather
// than a base virtual and its overrides; each patch applied individually inside
// its own try/catch so one miss costs one menu and is named in the log; and no
// per-frame retry — TitleProbe.Tick already throttles to about once a second and
// gives up after a minute.
// ─────────────────────────────────────────────────────────────────────────────
internal static class MainMenuReader
{
    /// <summary>True while a pause menu is open. Gates the shared list hooks so
    /// they cannot speak over the battle reader.</summary>
    private static bool _menuOpen;
    private static object _menuInstance;
    private static float _openingAt = -999f;

    /// <summary>UIMainMenu, once resolved. LoadAnnouncer uses it to recognise the
    /// pause menu's own description line.</summary>
    internal static Type MenuType { get; private set; }

    /// <summary>Open, or inside Open() right now. Open() writes the first
    /// description BEFORE its postfix runs (v59 log: the Items help line comes
    /// one line ahead of "pause menu opened"), so "open" alone would lose it.</summary>
    internal static bool IsOpenOrOpening => MenuIsOpen() || Time.realtimeSinceStartup - _openingAt < 1f;
    private static bool _warned;

    /// <summary>Is a pause menu up right now?</summary>
    /// <remarks>
    /// The flag alone is not enough. If Open() fires and Close() somehow does not
    /// — a menu torn down by a scene change, say — the flag would stay true and
    /// the shared list hooks would start speaking over the battle reader, which
    /// is the one thing this gate exists to prevent. So the captured UIMainMenu
    /// is asked directly whenever there is one; the flag is only the fallback for
    /// a build where Open() never fires at all, and in that case the two
    /// ungated menu-specific hooks still open it.
    /// </remarks>
    private static bool MenuIsOpen()
    {
        try
        {
            if (_menuInstance != null)
            {
                var v = UiText.Member(_menuInstance, "IsOpen");
                if (v is bool b) return b;
            }
        }
        catch { }
        return _menuOpen;
    }

    public static bool TryHook()
    {
        try
        {
            var menu = Gsd.FindAnywhere("UIMainMenu");
            var common = Gsd.FindAnywhere("UIMainMenuCommon");
            var party = Gsd.FindAnywhere("UIMainMenuPartyList");
            var list = Gsd.FindAnywhere("UIWindowList");
            var listTop = Gsd.FindAnywhere("UIWindowListTop");

            if (menu == null || common == null)
            {
                if (!_warned)
                {
                    _warned = true;
                    Plugin.Logger.LogWarning("[Menu] UIMainMenu / UIMainMenuCommon not found yet; retrying.");
                }
                return false;
            }

            MenuType = menu;
            var h = new Harmony("io.github.artemis626.suikodenaccess.mainmenu");
            var done = new HashSet<MethodInfo>();
            int n = 0;

            // The gate. Zero-argument methods, so there is nothing to marshal.
            n += One(h, menu, "Open", Type.EmptyTypes, nameof(MenuOpened), done, nameof(MenuOpening));
            n += One(h, menu, "Close", Type.EmptyTypes, nameof(MenuClosed), done);

            // The left command column: Items / Runes / Equipment / Stats /
            // Formation / Log / Settings.
            n += One(h, common, "SetCursor", new[] { typeof(int) }, nameof(CommonCursor), done);

            // The party rows: name, level, HP, rune slots.
            n += One(h, party, "SetCursor", new[] { typeof(int), typeof(int) }, nameof(PartyCursor), done);

            // Every other list-shaped window.
            n += One(h, list, "SetSelectOneCell", new[] { typeof(int) }, nameof(CellCursor), done);
            n += One(h, list, "SetSelectCell", new[] { typeof(int) }, nameof(CellCursor), done);
            n += One(h, listTop, "SetSelectOneCell", new[] { typeof(int) }, nameof(CellCursor), done);
            n += One(h, listTop, "SetSelectCell", new[] { typeof(int) }, nameof(CellCursor), done);

            Plugin.Logger.LogInfo($"[Menu] hooked {n} in-game menu methods.");
            return n > 0;
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Menu] TryHook failed: {e.Message}"); return false; }
    }

    /// <summary>Resolved by exact signature, declared on this type only, patched
    /// once, and a miss is REPORTED — "not declared" and "never looked" must not
    /// produce the same silence.</summary>
    private static int One(Harmony h, Type t, string name, Type[] args, string postfix, HashSet<MethodInfo> done,
                           string prefix = null)
    {
        if (t == null) { Plugin.Logger.LogWarning($"[Menu] type for {name} not found; skipped."); return 0; }
        try
        {
            var m = t.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic |
                                      BindingFlags.Instance | BindingFlags.DeclaredOnly,
                                null, args, null);
            if (m == null) { Plugin.Logger.LogWarning($"[Menu] {t.Name}.{name} NOT DECLARED with that signature."); return 0; }
            if (m.IsAbstract || !done.Add(m)) return 0;
            h.Patch(m, prefix: prefix == null ? null : new HarmonyMethod(typeof(MainMenuReader), prefix),
                       postfix: new HarmonyMethod(typeof(MainMenuReader), postfix));
            Plugin.Logger.LogInfo($"[Menu] patched {t.Name}.{name}");
            return 1;
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Menu] patch {t.Name}.{name} failed: {e.Message}"); return 0; }
    }

    // ── the gate ────────────────────────────────────────────────────────────
    // Nothing is spoken here on purpose. The first command is announced with its
    // help line a fraction of a second later, and Narration.Label interrupts —
    // so an orientation word here would be heard as a clipped syllable and then
    // the command name followed by its own help line, which is the
    // orientation anyway.
    private static void MenuOpening() => _openingAt = Time.realtimeSinceStartup;

    private static void MenuOpened(object __instance)
    {
        _menuOpen = true;
        _menuInstance = __instance;
        ForgetSelection();
        Plugin.Logger.LogInfo("[Menu] pause menu opened.");
    }

    private static void MenuClosed()
    {
        _menuOpen = false;
        ForgetSelection();
        Plugin.Logger.LogInfo("[Menu] pause menu closed.");
    }

    // ── readers ─────────────────────────────────────────────────────────────

    // UIMainMenuCommon.SetCursor(int index) — its list is the UIWindowListTop in
    // the `windowList` field.
    private static void CommonCursor(object __instance, object[] __args)
    {
        try
        {
            int i = Arg(__args, 0);
            if (i < 0) return;
            _menuOpen = true;         // this only ever runs with the menu up
            Speak("UIMainMenuCommon", i, CellOf(UiText.Member(__instance, "windowList"), "m_cellList", i));
        }
        catch { }
    }

    // UIMainMenuPartyList.SetCursor(int, int). Which argument is the row is not
    // established, so BOTH are logged with the text they would produce and the
    // first is spoken. If the log shows the second is the row it is one index to
    // change — and a wrong row here is a misread menu, not a name written to a
    // shared file.
    private static void PartyCursor(object __instance, object[] __args)
    {
        try
        {
            int a = Arg(__args, 0), b = Arg(__args, 1);
            var list = UiText.Member(__instance, "partyItemList");
            string first = Text(UiText.Element(list, a));
            string second = Text(UiText.Element(list, b));
            // SetCursor(-1,-1) is the hidden build during a save load, not the
            // player opening the menu; counting it as open left the flag stuck
            // on until the first real Close.
            if (a < 0) return;
            _menuOpen = true;

            // Through the same gate as the command list, and for the same
            // reason: this hook can be re-asserted too, and the log line is as
            // capable of flooding as the speech is.
            bool empty = string.IsNullOrWhiteSpace(first);
            if (!IsNewSelection(a, empty ? "" : first)) return;

            Plugin.Logger.LogInfo($"[Menu] UIMainMenuPartyList.SetCursor({a},{b}) -> arg0='{first}' arg1='{second}'");
            if (!empty) SayNow(first);
        }
        catch { }
    }

    // UIWindowList / UIWindowListTop .SetSelectCell(int) / .SetSelectOneCell(int)
    private static void CellCursor(object __instance, MethodBase __originalMethod, object[] __args)
    {
        try
        {
            if (!MenuIsOpen()) return;      // battle owns its own reader
            int i = Arg(__args, 0);
            if (i < 0) return;
            string where = (__instance?.GetType().Name ?? "?") + "." + (__originalMethod?.Name ?? "?");
            Speak(where, i, Text(CellAt(__instance, i)));
        }
        catch { }
    }

    // ── plumbing ────────────────────────────────────────────────────────────
    private static int Arg(object[] args, int n)
    {
        try { return args != null && args.Length > n ? Convert.ToInt32(args[n]) : -1; }
        catch { return -1; }
    }

    private static object CellAt(object list, int i) => UiText.Element(UiText.Member(list, "m_cellList"), i);

    private static string CellOf(object list, string field, int i) => Text(UiText.Element(UiText.Member(list, field), i));

    /// <summary>Every visible label on one row, joined — an item row comes out as
    /// its name then its quantity, a party row as name, level and HP. A cell owns
    /// several text objects and which ones are
    /// filled depends on the window, so they are read rather than named.</summary>
    private static string Text(object cell)
    {
        if (cell == null) return null;
        var parts = UiText.All(cell);
        if (parts == null || parts.Count == 0) return null;
        var kept = new List<string>(parts.Count);
        foreach (var p in parts)
        {
            var t = p?.Trim();
            if (!string.IsNullOrEmpty(t) && !kept.Contains(t)) kept.Add(t);
        }
        return kept.Count == 0 ? null : string.Join(", ", kept);
    }

    // ── the repeat gate ─────────────────────────────────────────────────────
    //
    // This used to be a 0.35-second window: "the same row selected twice in
    // quick succession is one keypress seen by two hooks, not two moves." That
    // is true and it is not the only thing that happens.
    //
    // MEASURED 2026-09-21: coming back out of the Settings screen, the game
    // re-asserts the current cell BY ITSELF, every frame, for as long as the
    // menu stays open — the log shows UIWindowListTop.SetSelectCell[6] and
    // UIMainMenuCommon[6] alternating, both reading 'Settings', for 30 lines
    // until the menu was closed. Against that, a time window does not suppress
    // anything: it keeps expiring. Every 0.35s the guard lets one more through,
    // Narration.Label speaks it with interrupt=true, and the result is a row
    // that stutters its own name for as long as the menu is up.
    //
    // So the guard is on CONTENT and has no window to expire. The same row
    // re-asserted is silent for as long as it stays the same row; any real move
    // changes the key and speaks at once, with no delay added to navigation.
    //
    // Keyed on index AND label, and NOT on which hook reported it: one keypress
    // arrives at both UIMainMenuCommon and UIWindowListTop.SetSelectCell with
    // the same index and the same label, so keying on content still collapses
    // that pair — which is what the time window was for — while also fixing the
    // repeat. Index is in the key so two rows that happen to share a label are
    // still announced separately.
    private static int _lastIndex = int.MinValue;
    private static string _lastSaid;
    private static int _repeats;

    /// <summary>True when this is a NEW selection, and the caller should log and
    /// speak it. False when the game is re-asserting the row already announced.</summary>
    private static bool IsNewSelection(int index, string label)
    {
        if (index == _lastIndex && string.Equals(label, _lastSaid, StringComparison.Ordinal))
        { _repeats++; return false; }

        // Reported rather than merely suppressed. The session that found this
        // had thousands of identical [Menu] lines, which is the same failure as
        // the unthrottled retry that once put 60,000 lines in one log: noise
        // that hides the evidence around it. Silently dropping them would swap
        // that for the opposite failure, where a menu re-asserting itself every
        // frame looks exactly like a menu sitting still. One line, on the way
        // out, keeps it visible.
        if (_repeats > 0)
        {
            Plugin.Logger.LogInfo($"[Menu] (the game re-asserted '{_lastSaid}' {_repeats} more time(s); spoken once)");
            _repeats = 0;
        }

        _lastIndex = index; _lastSaid = label;
        return true;
    }

    /// <summary>Cleared whenever the menu opens or closes, so reopening on the
    /// row last announced speaks it again instead of coming up silent.</summary>
    private static void ForgetSelection()
    {
        if (_repeats > 0)
            Plugin.Logger.LogInfo($"[Menu] (the game re-asserted '{_lastSaid}' {_repeats} more time(s); spoken once)");
        _lastIndex = int.MinValue; _lastSaid = null; _repeats = 0;
    }

    private static void Speak(string where, int index, string label)
    {
        bool empty = string.IsNullOrWhiteSpace(label);
        if (!IsNewSelection(index, empty ? "" : label)) return;

        if (empty)
        {
            // Log the zero. A row that read as empty is evidence about the cell
            // layout; a silent reader is not.
            Plugin.Logger.LogInfo($"[Menu] {where}[{index}] -> no readable label");
            return;
        }
        Plugin.Logger.LogInfo($"[Menu] {where}[{index}] -> '{label}'");
        SayNow(label);
    }

    private static void SayNow(string label)
    {
        // LabelWithDescription, not Label: the help line at the bottom of the
        // screen is already collected by Narration, and this makes the pair one
        // utterance — the command name, then its help line — instead of a name
        // that is immediately cut off by its own description.
        if (TitleProbe.SpeakChanges) Narration.LabelWithDescription(label);
    }
}
