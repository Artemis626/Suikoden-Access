using System;
using System.Collections.Generic;
using System.Text;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SuikodenAccess;

// ─────────────────────────────────────────────────────────────────────────────
// AUDIO TEXT ENTRY (both games)
//
// Lets the player name a marked place or an NPC by typing, entirely by ear —
// so naming no longer means alt-tabbing out to hand-edit a text file. The file
// stays editable for typos and bulk fixes; this is the in-game path. Suikoden 2
// uses it too (Gsd2Path Shift+K); the class name is historical.
//
// Speech design follows the rest of the mod: each keystroke echoes the character
// so the player can hear what was typed, backspace announces what was removed,
// and confirming reads the whole name back. Letters are echoed as bare
// characters, which NVDA reads naturally.
//
// CHARACTERS come from Keyboard.onTextInput, which delivers every character in
// the order typed and in the player's own keyboard layout. Polling key states
// could not: it took one key per frame, so a letter pressed in the same frame as
// Space was lost ("gate guard" saved as "gate uard", v68). Polling remains as the fallback
// if the event cannot be subscribed. Enter, Backspace and Escape are polled
// either way; typed characters are taken first, so a letter typed just before
// Enter is in the name.
//
// While entry is open the game is frozen two ways, because one alone is not
// enough: Time.timeScale is zeroed (stops animation and timers) AND the
// player's move is skipped by a Harmony prefix — Village_c.NewPlayerMove(int)
// in Suikoden 1 (Gsd1MovementBlock), EVENTCON.NewEventPlayerMove() in Suikoden 2
// (Gsd2Path.MoveBlockPrefix). Neither takes a by-ref struct.
//
// Keys: type to enter, Backspace delete, Enter confirm, Escape cancel.
// ─────────────────────────────────────────────────────────────────────────────
internal static class Gsd1TextInput
{
    public static bool IsOpen { get; private set; }

    private const int MaxLength = 24;
    private static readonly StringBuilder _buf = new StringBuilder();
    private static Action<string> _onConfirm;
    private static float _savedTimeScale = 1f;

    // Keyboard.onTextInput, subscribed only while entry is open.
    private static Keyboard _eventKb;
    private static Il2CppSystem.Action<char> _handler;
    private static readonly Queue<char> _typed = new Queue<char>();

    public static void Open(string prompt, Action<string> onConfirm)
    {
        if (IsOpen) return;
        IsOpen = true;
        _buf.Clear();
        _typed.Clear();
        _onConfirm = onConfirm;

        try { _savedTimeScale = Time.timeScale; Time.timeScale = 0f; } catch { }
        Subscribe();

        Narration.Label($"{prompt}. Type a name, then press Enter to save, or Escape to cancel.");
    }

    private static void Close(bool restore = true)
    {
        IsOpen = false;
        _onConfirm = null;
        _buf.Clear();
        Unsubscribe();
        if (restore) { try { Time.timeScale = _savedTimeScale; } catch { } }
    }

    private static void Subscribe()
    {
        try
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            _handler ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<char>>(new Action<char>(OnChar));
            kb.add_onTextInput(_handler);
            _eventKb = kb;
        }
        catch (Exception e)
        {
            _eventKb = null;
            Plugin.Logger.LogWarning($"[Text] keyboard text events unavailable, polling keys instead: {e.Message}");
        }
    }

    private static void Unsubscribe()
    {
        try { if (_eventKb != null && _handler != null) _eventKb.remove_onTextInput(_handler); } catch { }
        _eventKb = null;
        _typed.Clear();
    }

    // Runs inside the Input System's update, before this frame's Tick.
    private static void OnChar(char c) { if (IsOpen) _typed.Enqueue(c); }

    // '=' and '|' separate fields in the names files; control characters are the
    // Enter / Backspace / Escape handled as keys below.
    private static bool Accepted(char c) => !char.IsControl(c) && c != '=' && c != '|';

    // Letter/digit keys are contiguous in the Input System's Key enum, so they
    // can be walked rather than listed one by one.
    private static readonly Key[] Letters = BuildRange(Key.A, 26);
    private static readonly Key[] Digits = BuildRange(Key.Digit1, 9);

    private static Key[] BuildRange(Key first, int count)
    {
        var arr = new Key[count];
        for (int i = 0; i < count; i++) arr[i] = (Key)((int)first + i);
        return arr;
    }

    /// <summary>Per-frame poll. Returns true while it owns the keyboard.</summary>
    public static bool Tick()
    {
        if (!IsOpen) return false;

        try
        {
            var kb = Keyboard.current;
            if (kb == null) return true;

            // Characters first, so one typed in the same frame as Enter is kept.
            if (_eventKb != null)
                while (_typed.Count > 0) { char c = _typed.Dequeue(); if (Accepted(c)) Append(c); }
            else PollCharacters(kb);

            if (kb[Key.Escape].wasPressedThisFrame)
            {
                Narration.Label("Cancelled");
                Close();
                return true;
            }

            if (kb[Key.Enter].wasPressedThisFrame || kb[Key.NumpadEnter].wasPressedThisFrame)
            {
                var text = _buf.ToString().Trim();
                if (text.Length == 0) { Narration.Label("Name is empty. Type a name, or press Escape to cancel."); return true; }
                var cb = _onConfirm;
                Close();
                Narration.Label($"Saved {text}");
                try { cb?.Invoke(text); } catch (Exception e) { Plugin.Logger.LogWarning($"[Text] name callback failed: {e.Message}"); }
                return true;
            }

            if (kb[Key.Backspace].wasPressedThisFrame)
            {
                if (_buf.Length == 0) { Narration.Label("Empty"); return true; }
                char removed = _buf[_buf.Length - 1];
                _buf.Length--;
                Narration.Label($"{Spoken(removed)} deleted");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[Text] text entry failed: {e.Message}");
            Close();
        }
        return true;
    }

    // The fallback. Every key pressed this frame is taken, not only the first.
    // Their order within one frame cannot be known, so it is fixed: letters and
    // digits, then space and hyphen.
    private static void PollCharacters(Keyboard kb)
    {
        bool shift = kb[Key.LeftShift].isPressed || kb[Key.RightShift].isPressed;
        for (int i = 0; i < Letters.Length; i++)
            if (kb[Letters[i]].wasPressedThisFrame) { char c = (char)('a' + i); Append(shift ? char.ToUpperInvariant(c) : c); }
        for (int i = 0; i < Digits.Length; i++)
            if (kb[Digits[i]].wasPressedThisFrame) Append((char)('1' + i));
        if (kb[Key.Digit0].wasPressedThisFrame) Append('0');
        if (kb[Key.Space].wasPressedThisFrame) Append(' ');
        if (kb[Key.Minus].wasPressedThisFrame) Append('-');
    }

    private static void Append(char c)
    {
        if (_buf.Length >= MaxLength) { Narration.Label("Name is full"); return; }
        _buf.Append(c);
        Narration.Label(Spoken(c));
    }

    private static string Spoken(char c) => c == ' ' ? "space" : c.ToString();
}

/// <summary>
/// Freezes the player while text entry is open. Village_c.NewPlayerMove takes a
/// single int, so a prefix here is safe under the project's hooking rule.
/// </summary>
/// <remarks>
/// This class must NOT carry a [HarmonyPatch] attribute. It is applied by hand in
/// Gsd1Nav.TryHook(), against a method resolved at runtime from the interop
/// assembly — there is no compile-time type to name in an attribute. A bare
/// [HarmonyPatch] here makes Plugin.Load()'s PatchAll pick the class up, find no
/// target method, and throw "Undefined target method", which aborts Load and
/// disables the ENTIRE plugin (dialogue, menus, battle and navigation all go
/// silent at once). The companion rule in the status doc is the opposite hazard:
/// a class patched BY attribute silently does nothing if the attribute is lost.
/// Attribute patching and manual patching are exclusive — never both.
/// </remarks>
internal static class Gsd1MovementBlock
{
    public static bool Prefix() => !Gsd1TextInput.IsOpen;
}
