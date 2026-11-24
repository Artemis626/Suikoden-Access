using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SuikodenAccess;

// ─────────────────────────────────────────────────────────────────────────────
// NAV PROBE — the Suikoden 2 field entry point. PASSIVE: nothing here patches a
// method on the game's collision/movement hot path.
//
// EVENTCON.NewEventPlayerMove() — zero arguments, nothing to marshal — is the
// one hook: its postfix captures the live field controller (EVENTCON), and its
// prefix freezes the player while a Shift+K name is typed (Gsd2Path). Everything
// else runs from Tick(), never from inside a patch.
//
// An earlier version also postfixed EventMapAtariCheckFunc1 and friends to log
// `object[] __args`. One of them takes `ref RECT rec`, and Harmony's __args
// round-trips by-ref struct parameters through boxing whether or not the postfix
// writes them - confirmed in play to corrupt live collision (walls stopped
// blocking). DO NOT hook those methods again. Collision is now answered by
// CALLING EventMapAtariCheckFunc1 (Gsd2Path, handoff §13), which is safe.
//
// Per frame: Gsd2Walk (walked cells / refusals, the pathfinder's referee) and
// Gsd2Path (J/K/L/P). Keys here: O writes the walk dump, F4 the NPC data dump.
// ─────────────────────────────────────────────────────────────────────────────
internal static class NavProbe
{
    public static bool Enabled = true;

    private static bool _hooked;
    private static object _player;   // live EVENTCON instance, captured from NewEventPlayerMove

    /// <summary>The live EVENTCON, or null. Non-null means Suikoden 2 is the
    /// game actually running — see Gsd.RunningGameName.</summary>
    internal static object Player => _player;

    /// <summary>When NewEventPlayerMove last ran (realtime), or -1.</summary>
    internal static float CapturedAt { get; private set; } = -1f;

    /// <summary>Suikoden 2's field is the one running: captured, and more recently
    /// than Suikoden 1's controller (see Gsd1Nav.Live - the launcher can switch
    /// games in one process, and the other game's controller stays referenced).</summary>
    internal static bool Live => _player != null && CapturedAt > Gsd1Nav.CapturedAt;

    public static bool TryHook()
    {
        if (!Enabled || _hooked) return _hooked;
        try
        {
            var eventCon = Gsd.FindAnywhere("EVENTCON");
            if (eventCon == null) return false;

            var h = new Harmony("io.github.artemis626.suikodenaccess.nav");
            int n = 0;

            // The prefix freezes the player while a Shift+K name is typed
            // (Gsd2Path.MoveBlockPrefix). Zero-argument method: no by-ref struct.
            n += Hook(h, eventCon, "NewEventPlayerMove", nameof(CapturePlayer),
                      new HarmonyMethod(typeof(Gsd2Path), nameof(Gsd2Path.MoveBlockPrefix)));
            // Nothing else is hooked here, deliberately: see the header on the
            // collision-check hooks that corrupted live collision.

            Plugin.Logger.LogInfo($"[Nav] hooked {n} methods on EVENTCON.");
            _hooked = n > 0;
            return _hooked;
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Nav] TryHook failed: {e.Message}"); return false; }
    }

    private static int Hook(Harmony h, Type t, string name, string postfix, HarmonyMethod prefix = null)
    {
        try
        {
            var m = t.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic |
                                      BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (m == null) return 0;   // not present under this exact name — fine, it's a candidate list
            h.Patch(m, prefix: prefix, postfix: new HarmonyMethod(typeof(NavProbe), postfix));
            Plugin.Logger.LogInfo($"[Nav] patched EVENTCON.{name}{(prefix != null ? " (with prefix)" : "")}");
            return 1;
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Nav] patch EVENTCON.{name} failed: {e.Message}"); return 0; }
    }

    // NewEventPlayerMove() — zero arguments, called every frame the field player
    // is processed. Only captures the live instance; nothing here is logged, so
    // this can never itself flood the log the way an unthrottled reader would.
    private static void CapturePlayer(object __instance)
    {
        _player = __instance;
        CapturedAt = Time.realtimeSinceStartup;
    }

    private static float _nextLog;
    private static string _lastPosition;
    private static bool _oDown;
    private static bool _f4Down;

    public static void Tick()
    {
        if (!Enabled) return;

        // GSD1 hooks independently of GSD2: whichever game is running, only its
        // own controller class exists, so a miss on one must not block the other.
        Gsd1Nav.Tick();
        Gsd1Probe.Tick();   // F7 UI-text snapshot / F8 GSD1 map-data dump, read-only

        if (!_hooked && !TryHook()) return;

        if (Live)
        {
            // Every frame, not once a second. A position sampled once a second is
            // several tiles from the last one, which tells you where the player
            // went and not which cells they crossed — and "which cells" is the
            // entire falsifier. Pure field reads; see Gsd2Walk's header.
            Gsd2Walk.Note(_player);

            // Suikoden 2 pathfinding: J/K/L/P, the same keys as Suikoden 1 (only one
            // game's controller is ever live, so they never both fire).
            Gsd2Path.Tick(_player);

            if (Time.realtimeSinceStartup >= _nextLog)
            {
                _nextLog = Time.realtimeSinceStartup + 1f;
                LogPosition();
            }
        }

        PollDumpKey();
    }

    // This game runs the new Input System exclusively (confirmed from a play
    // session: legacy UnityEngine.Input.GetKey threw "External component has
    // thrown an exception" — the legacy manager is compiled in but disabled at
    // runtime). Unity.InputSystem.dll is in BepInEx\interop, covered by the
    // csproj's existing wildcard reference, so Keyboard.current is used directly.
    private static bool _inputUnavailable;

    private static void PollDumpKey()
    {
        if (_inputUnavailable) return;
        try
        {
            var kb = Keyboard.current;
            if (kb == null) return;   // no keyboard device registered yet

            // While a Shift+K name is typed, letters are the name: the "o" and "u"
            // in "soldier role 3" wrote three walk dumps and ran a probe
            // (v68 session). The latches follow the keys meanwhile, so a key still
            // held when entry closes does not fire as a fresh press.
            if (Gsd1TextInput.IsOpen)
            {
                _oDown = kb[Key.O].isPressed; _f4Down = kb[Key.F4].isPressed;
                return;
            }

            // O writes the accumulated walk ground truth (Gsd2Walk) - pure reads.
            bool downO = kb[Key.O].isPressed;
            if (downO && !_oDown)
            {
                if (!Live) Plugin.Logger.LogInfo("[Nav] O: no live EVENTCON - this is a Suikoden 2 key and nothing was recorded.");
                else Gsd2Walk.Dump(_player);
            }
            _oDown = downO;

            // F4 writes what the game knows about the map's people (Gsd2Npc). Pure
            // reads plus EventHumanNameGet.
            bool down4 = kb[Key.F4].isPressed;
            if (down4 && !_f4Down)
            {
                if (!Live) Plugin.Logger.LogInfo("[Npc] F4: no live EVENTCON - this is a Suikoden 2 key.");
                else Gsd2Npc.Run(_player);
            }
            _f4Down = down4;
        }
        catch (Exception e)
        {
            _inputUnavailable = true;
            Plugin.Logger.LogWarning($"[Nav] Input System unavailable, O / F4 disabled: {e.Message}");
        }
    }

    // Once a second, and only when something changed: a player standing still
    // used to write the same line sixty times a minute.
    private static void LogPosition()
    {
        try
        {
            int mno = NavProbeShared.ToInt(UiText.Member(_player, "mno"));
            int px = NavProbeShared.ToInt(UiText.Member(_player, "px"));
            int py = NavProbeShared.ToInt(UiText.Member(_player, "py"));
            int mx = NavProbeShared.ToInt(UiText.Member(_player, "mx"));
            int my = NavProbeShared.ToInt(UiText.Member(_player, "my"));
            int ehumanCount = NavProbeShared.CountOf(UiText.Member(_player, "ehuman"));
            var line = $"mno={mno} px={px} py={py} mx={mx} my={my} ehuman={ehumanCount}";
            if (line == _lastPosition) return;
            _lastPosition = line;
            Plugin.Logger.LogInfo("[Nav] " + line);
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Nav] LogPosition failed: {e.Message}"); }
    }
}
