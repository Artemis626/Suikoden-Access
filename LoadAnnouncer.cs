using System;
using UnityEngine;

namespace SuikodenAccess;

// ─────────────────────────────────────────────────────────────────────────────
// "NOW LOADING" (both games)
//
// Loading a save used to speak the Log command's help line. That is not a
// loading screen: it is the pause menu's description of its Log command. The
// game builds the pause menu, hidden, while the save loads, and the build
// writes that description into Text_Auto, the component every menu uses for
// its help line, which the detail reader speaks. Same in both games (every
// session log from v59 to v68, and a Suikoden 1 log).
//
// The loading screen with a tip (TipsText, seen in Suikoden 1) announces
// through here too, so one load is never announced twice.
//
// Two parts:
//   1. A pause-menu description written while the pause menu is closed is not
//      spoken. The pause menu is recognised by its UIMainMenu parent, not by the
//      text, so this holds in any language.
//   2. "Now loading" is said once when a load starts. The save screen
//      (UISaveLoadBase) says what it is doing in `funcType` (Load / DirectLoad)
//      and where it is in `mode` (Select, Confirm, Finish, Wait, LoadBadOk);
//      it is read from the Exec postfix SaveLoadReader already has. WHICH mode
//      change marks the start is not yet measured, so every change is logged
//      and leaving Select/Confirm on a load counts. If that never fires, the
//      hidden pause-menu build (part 1) says it instead: it happens during the
//      load, and it is where the stray sentence used to be heard.
// ─────────────────────────────────────────────────────────────────────────────
internal static class LoadAnnouncer
{
    private const int FuncLoad = 1, FuncDirectLoad = 2;   // UISaveLoadBase.FuncType
    private const int ModeSelect = 0, ModeConfirm = 1;     // UISaveLoadBase.Mode

    private static float _announcedAt = -999f;
    private static int _lastFunc = int.MinValue, _lastMode = int.MinValue;
    private static Il2CppSystem.Type _menuType;
    private static bool _menuTypeFailed;

    /// <summary>Once per load: a second trigger within a few seconds is the same load.</summary>
    public static void Announce(string why)
    {
        float now = Time.realtimeSinceStartup;
        if (now - _announcedAt < 15f) return;
        _announcedAt = now;
        Plugin.Logger.LogInfo($"[Load] now loading ({why})");
        if (TitleProbe.SpeakChanges) Narration.Label("Now loading");
    }

    /// <summary>From SaveLoadReader's Exec postfix, every frame the save screen runs.</summary>
    public static void NoteSaveScreen(object inst)
    {
        int func = ToInt(UiText.Member(inst, "funcType"));
        int mode = ToInt(UiText.Member(inst, "mode"));
        if (func == _lastFunc && mode == _lastMode) return;

        int was = _lastMode;
        bool sameScreen = func == _lastFunc;
        _lastFunc = func; _lastMode = mode;
        Plugin.Logger.LogInfo($"[Load] save screen funcType={func} mode {(sameScreen ? was.ToString() : "-")} -> {mode}");

        bool loading = func == FuncLoad || func == FuncDirectLoad;
        if (loading && sameScreen && (was == ModeSelect || was == ModeConfirm) && mode != ModeSelect && mode != ModeConfirm)
            Announce($"save screen mode {was} -> {mode}");
    }

    /// <summary>True if a description line must stay silent: it belongs to the
    /// pause menu and the pause menu is not open.</summary>
    public static bool Swallow(Component text)
    {
        if (MainMenuReader.IsOpenOrOpening) return false;
        if (!UnderPauseMenu(text)) return false;
        Plugin.Logger.LogInfo("[Load] pause-menu description written while the menu is closed (hidden build): not spoken");
        Announce("hidden pause-menu build");
        return true;
    }

    private static bool UnderPauseMenu(Component c)
    {
        if (_menuTypeFailed || c == null) return false;
        try
        {
            if (_menuType == null)
            {
                var t = MainMenuReader.MenuType;
                if (t == null) return false;
                _menuType = Il2CppInterop.Runtime.Il2CppType.From(t);
            }
            return c.GetComponentInParent(_menuType, true) != null;
        }
        catch (Exception e)
        {
            // Fail open: a description spoken is better than every description lost.
            _menuTypeFailed = true;
            Plugin.Logger.LogWarning($"[Load] pause-menu parent check unavailable: {e.Message}");
            return false;
        }
    }

    private static int ToInt(object v)
    {
        try { return v == null ? int.MinValue : Convert.ToInt32(v); } catch { return int.MinValue; }
    }
}
