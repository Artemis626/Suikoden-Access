using System;
using System.Runtime.InteropServices;
using BepInEx.Logging;

namespace SuikodenAccess;

/// <summary>
/// Screen-reader output. Prefers NVDA via nvdaControllerClient.dll, and falls back to
/// Windows SAPI (System.Speech) when NVDA is not running. Every call is safe to make
/// from the game's main thread and never throws out of Speak().
/// </summary>
internal static class Speech
{
    private static ManualLogSource Log => Plugin.Logger;

    // ---- NVDA controller client (native; MUST be the 64-bit build) ----
    [DllImport("nvdaControllerClient.dll")]
    private static extern int nvdaController_testIfRunning();

    [DllImport("nvdaControllerClient.dll", CharSet = CharSet.Unicode)]
    private static extern int nvdaController_speakText([MarshalAs(UnmanagedType.LPWStr)] string text);

    [DllImport("nvdaControllerClient.dll")]
    private static extern int nvdaController_cancelSpeech();

    // ---- SAPI fallback ----
    private static System.Speech.Synthesis.SpeechSynthesizer _sapi;

    // Becomes false if the native dll is missing, so we stop probing it every call.
    private static bool _nvdaClientUsable = true;

    // testIfRunning is an IPC round-trip to NVDA. A battle turn can speak several
    // lines in one frame, so the answer is reused for a second; NVDA being started
    // or closed mid-session is still noticed within that second.
    private static bool _nvdaRunning;
    private static long _nvdaCheckedAt = long.MinValue;
    private const long NvdaRecheckMs = 1000;

    // Set after the first SAPI failure, so a missing System.Speech logs ONE error
    // instead of one per spoken line for the rest of the session.
    private static bool _sapiBroken;

    /// <summary>True only if the NVDA client loaded AND NVDA is currently running.</summary>
    public static bool NvdaAvailable
    {
        get
        {
            if (!_nvdaClientUsable) return false;
            long now = Environment.TickCount64;
            if (now - _nvdaCheckedAt < NvdaRecheckMs) return _nvdaRunning;
            _nvdaCheckedAt = now;
            try
            {
                return _nvdaRunning = nvdaController_testIfRunning() == 0; // 0 == running
            }
            catch (DllNotFoundException)
            {
                _nvdaClientUsable = false;
                return _nvdaRunning = false;
            }
            catch (Exception e)
            {
                Log?.LogWarning($"NVDA check failed: {e.Message}");
                return _nvdaRunning = false;
            }
        }
    }

    public static void Initialize()
    {
        // Probe once at startup so a missing dll is reported now, not mid-battle.
        _ = NvdaAvailable;
        if (!_nvdaClientUsable)
        {
            Log?.LogWarning("nvdaControllerClient.dll (64-bit) was not found next to the plugin. " +
                            "Put it in BepInEx\\plugins\\. Speech will use SAPI until then.");
        }
    }

    /// <summary>Speak text; by default interrupts anything currently being spoken.</summary>
    public static void Speak(string text, bool interrupt = true)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        text = text.Trim();

        if (NvdaAvailable)
        {
            try
            {
                if (interrupt) nvdaController_cancelSpeech();
                if (nvdaController_speakText(text) == 0) return; // spoken by NVDA, done
            }
            catch (Exception e)
            {
                Log?.LogWarning($"NVDA speak failed, using SAPI: {e.Message}");
                _nvdaCheckedAt = long.MinValue;   // ask again next time rather than trust the cache
            }
        }

        SpeakSapi(text, interrupt);
    }

    private static void SpeakSapi(string text, bool interrupt)
    {
        if (_sapiBroken) return;
        try
        {
            _sapi ??= new System.Speech.Synthesis.SpeechSynthesizer();
            if (interrupt) _sapi.SpeakAsyncCancelAll();
            _sapi.SpeakAsync(text);
        }
        catch (Exception e)
        {
            // If System.Speech isn't deployed we land here. The NVDA path is unaffected.
            _sapiBroken = true;
            Log?.LogError($"SAPI speak failed, SAPI disabled for this session: {e.Message}");
        }
    }
}
