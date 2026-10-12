using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace TravellersRestProfiler;

/// <summary>
/// Gives the profiler a per-frame heartbeat.
///
/// Scene events alone are not enough: they fire only when a scene changes, so the
/// heartbeat stopped completely once the player was in a stable scene and normal play
/// produced no measurement at all.
///
/// The fix is the technique the other mods in this repository already use — a Harmony
/// patch on a method the game calls every frame. <c>WorldTime</c> is a developer-named
/// class that implements the game's singleton interface and owns an <c>Update</c>, so it
/// is a stable target. It is patched by name at runtime and every step is verified:
///
///   * the class is looked up by name, and may not exist yet at plugin load;
///   * the method may be renamed by the game's obfuscator in a future build;
///   * so attachment is retried and the outcome is always logged, never assumed.
///
/// Nothing here touches randomised names, which is what makes it safe across game
/// updates.
/// </summary>
public static class FrameHook
{
    private const string TargetTypeName = "WorldTime";
    private const string TargetMethodName = "Update";

    private static ManualLogSource log;
    private static bool attached;
    private static int attempts;
    private static string lastFailure = "not attempted";

    public static bool Attached => attached;
    public static string Status => attached ? "attached to WorldTime.Update" : lastFailure;

    public static void Initialise(ManualLogSource logger)
    {
        log = logger;
        TryAttach();
    }

    /// <summary>
    /// Called again on every scene load, because the target class lives in a scene that
    /// is not loaded when the plugin starts.
    /// </summary>
    public static void TryAttach()
    {
        if (attached || log == null)
        {
            return;
        }

        attempts++;
        try
        {
            var type = AccessTools.TypeByName(TargetTypeName);
            if (type == null)
            {
                lastFailure = $"type '{TargetTypeName}' not loaded yet (attempt {attempts})";
                return;
            }

            var method = AccessTools.Method(type, TargetMethodName);
            if (method == null)
            {
                lastFailure = $"'{TargetTypeName}.{TargetMethodName}' not found — the game may have renamed it";
                if (attempts <= 2)
                {
                    log.LogWarning($"Per-frame hook unavailable: {lastFailure}. Measurement is limited to scene events.");
                }
                return;
            }

            var harmony = new Harmony(Plugin.PluginGuid + ".frame");
            harmony.Patch(method, postfix: new HarmonyMethod(
                typeof(FrameHook).GetMethod(nameof(Postfix), BindingFlags.Public | BindingFlags.Static)));

            attached = true;
            log.LogInfo($"Per-frame hook attached: {TargetTypeName}.{TargetMethodName}. " +
                        "Measurement now runs every frame.");
        }
        catch (Exception exception)
        {
            lastFailure = $"{exception.GetType().Name}: {exception.Message}";
            log.LogWarning($"Per-frame hook failed ({lastFailure}). Measurement falls back to scene events.");
        }
    }

    /// <summary>Runs once per frame, on the game's own update.</summary>
    public static void Postfix()
    {
        ProfilerRuntime.Pump("frame");

        // The HUD cannot be drawn from here — Unity refuses GUI calls outside OnGUI — but this
        // is the only place known to run every frame, so it is used to create the component
        // that draws. It is created here rather than during Awake precisely because a
        // component created during Awake never receives OnGUI on this game.
        ProfilerRuntime.EnsureHud(log);
    }
}
