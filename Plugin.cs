using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace SuikodenAccess;

internal static class PluginInfo
{
    // The GUID is this plugin's permanent identity: BepInEx keys its config files
    // on it, so changing it after a release orphans everyone's settings. It is
    // also the prefix of every Harmony instance id in this project, so the two
    // must be kept in step.
    public const string GUID = "io.github.artemis626.suikodenaccess";
    public const string Name = "Suikoden Access";
    public const string Version = "0.1.0";
}

[BepInEx.BepInPlugin(PluginInfo.GUID, PluginInfo.Name, PluginInfo.Version)]
public class Plugin : BasePlugin
{
    // Shared logger so any class can write to the BepInEx log/console.
    internal static ManualLogSource Logger { get; private set; }

    public override void Load()
    {
        Logger = Log;

        // Make sure nvdaControllerClient.dll is found next to THIS plugin dll
        // (i.e. inside BepInEx\plugins\), regardless of the process working directory.
        try
        {
            NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), ResolveNativeLibrary);
        }
        catch (Exception e)
        {
            Logger.LogWarning($"Could not install native library resolver: {e.Message}");
        }

        Speech.Initialize();

        var harmony = new Harmony(PluginInfo.GUID);
        PatchAllTolerantly(harmony);

        Logger.LogInfo($"{PluginInfo.Name} v{PluginInfo.Version} loaded. NVDA running: {Speech.NvdaAvailable}");

        // Audible heartbeat: hearing this on launch proves the whole pipeline works.
        Speech.Speak($"{PluginInfo.Name} loaded");
        TitleProbe.Install();
    }

    /// <summary>
    /// Apply the attribute-declared patches one class at a time, so that a single
    /// bad patch class cannot take the whole mod down with it.
    /// </summary>
    /// <remarks>
    /// Harmony.PatchAll(assembly) is all-or-nothing: it throws on the first class
    /// it cannot process and the exception escapes Load(), so BepInEx marks the
    /// plugin as failed and NOTHING runs — no dialogue, no menus, no battle, no
    /// navigation. That happened for real to a class carrying a bare
    /// [HarmonyPatch] with no target (see Gsd1MovementBlock). One broken hook
    /// should cost one feature, not every feature, and the failure has to be
    /// named in the log rather than swallowed.
    /// </remarks>
    private static void PatchAllTolerantly(Harmony harmony)
    {
        int ok = 0, failed = 0;
        Type[] types;
        try { types = Assembly.GetExecutingAssembly().GetTypes(); }
        catch (ReflectionTypeLoadException e) { types = e.Types; }

        foreach (var type in types)
        {
            if (type == null) continue;
            try
            {
                var processor = harmony.CreateClassProcessor(type);
                var applied = processor.Patch();
                if (applied != null && applied.Count > 0) ok++;
            }
            catch (Exception e)
            {
                failed++;
                Logger.LogError($"Harmony patch class '{type.FullName}' FAILED and was skipped: {e.Message}");
            }
        }
        Logger.LogInfo($"Harmony: {ok} patch class(es) applied, {failed} skipped.");
    }

    private static IntPtr ResolveNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        // Only intercept the NVDA client; let everything else resolve normally.
        if (libraryName.IndexOf("nvdaControllerClient", StringComparison.OrdinalIgnoreCase) < 0)
            return IntPtr.Zero;

        var dir = Path.GetDirectoryName(assembly.Location);
        if (string.IsNullOrEmpty(dir))
            dir = AppContext.BaseDirectory;

        var candidate = Path.Combine(dir, "nvdaControllerClient.dll");
        if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
            return handle;

        return IntPtr.Zero; // fall back to default search (game root, system paths)
    }
}
