using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TravellersRestProfiler;

/// <summary>
/// A measured loading window.
/// </summary>
public sealed class LoadEvent
{
    public string Label { get; set; } = "";
    public string Scene { get; set; } = "";
    public float Seconds { get; set; }
    public int Frames { get; set; }
    /// <summary>Update ticks the plugin received during this load window.</summary>
    public long Ticks { get; set; }
    public float WorstFrameMs { get; set; }
    /// <summary>
    /// True when the window stayed open past any plausible load. That happens when a
    /// scene unloads and the game then sits idle — in a menu, or waiting — before the
    /// next scene arrives, so the duration includes idle time and is not a load time.
    /// </summary>
    public bool SuspectIdleTime { get; set; }
    public float AverageFrameMs { get; set; }
    public int Collections { get; set; }
    public long ManagedDelta { get; set; }
    public long ReservedDelta { get; set; }
}

/// <summary>
/// Measures loading.
///
/// This is the phase the game is reported to be worst at, and the phase a profiler
/// is most likely to miss: by the time anyone opens a window and starts recording,
/// loading is long over. So loading is detected automatically instead, and each
/// window is recorded separately from gameplay frames.
///
/// Detection is layered, and every layer is optional by design:
///
///   1. Unity's own <c>SceneManager.sceneLoaded</c> and <c>sceneUnloaded</c> events,
///      which are stable API and cannot rot with a game patch.
///   2. A root object created with <c>DontDestroyOnLoad</c> running a coroutine, so
///      the end of a load can be detected by polling the scene's loading state even
///      when a scene load never completes with an event.
///   3. Reflection over the game's loading entry points. These are resolved by name
///      at runtime and each one is verified before use, because the game's own
///      loader is a named class (LoadingScene) while its neighbours are obfuscated
///      and change between patches. A hook that cannot be resolved is reported and
///      skipped rather than throwing.
/// </summary>
public sealed class LoadingTracker
{
    private readonly MetricsCollector metrics;
    private readonly ManualLogSource log;
    private readonly List<LoadEvent> loads = new List<LoadEvent>();

    private static LoadingTracker active;
    private bool loading;
    private string currentLabel = "";
    private string currentScene = "";
    private DateTime loadStartedAt;
    private int collectionsAtStart;
    private long managedAtStart;
    private long reservedAtStart;
    private long ticksAtStart;

    public IReadOnlyList<LoadEvent> Loads => loads;
    public bool IsLoading => loading;

    public LoadingTracker(MetricsCollector metrics, ManualLogSource log)
    {
        this.metrics = metrics;
        this.log = log;
    }

    // ---- lifecycle -------------------------------------------------------------

    /// <summary>
    /// Attaches the scene events and the game's loading hooks. Safe to call from Awake.
    ///
    /// These Unity scene events are the only callbacks observed to fire reliably on this
    /// game, so they carry both the loading measurement and the profiler's heartbeat.
    /// </summary>
    public void Install()
    {
        active = this;

        try
        {
            SceneManager.sceneLoaded += (scene, mode) => OnSceneLoaded(scene.name, mode.ToString());
            SceneManager.sceneUnloaded += scene => OnSceneUnloaded(scene.name);
            currentScene = SceneManager.GetActiveScene().name ?? "";
            log.LogInfo($"Loading tracker installed. Load hooks resolved: {ResolveHooks()}");
        }
        catch (Exception exception)
        {
            log.LogError($"LoadingTracker.Install failed (measurement degraded, plugin kept alive): {exception}");
        }
    }

    /// <summary>
    /// Kept for the plugin's Start, which does not run on this game. Nothing to do: the
    /// scene events drive everything.
    /// </summary>
    public void StartPolling()
    {
        log.LogInfo("Loading tracker relies on scene events; no polling object is created.");
    }

    /// <summary>
    /// Resolves the game's loading entry points. Returns how many were patchable.
    /// </summary>
    private int ResolveHooks()
    {
        var patched = 0;
        var harmony = new Harmony(Plugin.PluginGuid + ".loading");

        // These are named, developer-given types. Verify before patching.
        patched += TryPatch(harmony, "LoadingScene", "Start", nameof(LoadingSceneStartPostfix));
        patched += TryPatch(harmony, "LoadingScene", "Awake", nameof(LoadingSceneStartPostfix));

        return patched;
    }

    private int TryPatch(Harmony harmony, string typeName, string methodName, string postfixName)
    {
        try
        {
            var type = AccessTools.TypeByName(typeName);
            if (type == null)
            {
                log.LogInfo($"Loading hook skipped: type '{typeName}' not found (the game may have renamed it).");
                return 0;
            }

            var method = AccessTools.Method(type, methodName);
            if (method == null)
            {
                log.LogInfo($"Loading hook skipped: '{typeName}.{methodName}' not found.");
                return 0;
            }

            var postfix = new HarmonyMethod(typeof(LoadingTracker).GetMethod(
                postfixName, BindingFlags.Public | BindingFlags.Static));
            harmony.Patch(method, postfix: postfix);
            log.LogInfo($"Loading hook attached: {typeName}.{methodName}");
            return 1;
        }
        catch (Exception exception)
        {
            log.LogWarning($"Loading hook '{typeName}.{methodName}' failed: {exception.Message}");
            return 0;
        }
    }

    public static void LoadingSceneStartPostfix()
    {
        active?.BeginLoad("LoadingScene");
    }

    // ---- window bookkeeping ----------------------------------------------------

    private void OnSceneLoaded(string scene, string mode)
    {
        // The per-frame hook targets a class that only exists once a gameplay scene is
        // loaded, so attachment is retried here.
        FrameHook.TryAttach();

        // Scene events also feed the profiler, which keeps measurement working even if
        // the per-frame hook could not be attached.
        ProfilerRuntime.Pump($"sceneLoaded {scene}");

        // A gameplay scene arriving means the load window is over.
        metrics.MarkLoading(false);
        if (loading)
        {
            EndLoad(scene);
        }
        else
        {
            log.LogInfo($"Scene loaded: {scene} ({mode})");
        }
    }

    private void OnSceneUnloaded(string scene)
    {
        // Tearing a scene down is the expensive half, so a load starts here.
        BeginLoad($"unload {scene}");
    }

    /// <summary>
    /// Retained because the loading tracker no longer has a per-frame driver. Loading
    /// windows are opened and closed entirely by the Unity scene events, which are the
    /// only callbacks observed to fire on this game.
    /// </summary>
    internal void PollTick()
    {
        if (!loading && !SceneManager.GetActiveScene().isLoaded)
        {
            BeginLoad("scene load");
        }
    }

    public void BeginLoad(string label)
    {
        if (loading)
        {
            return;
        }

        try
        {
            metrics.RefreshMemory();
            // Start tailing the game's own log now, so that whatever causes a stall during
            // this load is already captured when the frame ends.
            ProfilerRuntime.BeginLoadCapture();
            loading = true;
            currentLabel = label;
            currentScene = SceneManager.GetActiveScene().name;
            loadStartedAt = DateTime.UtcNow;
            collectionsAtStart = metrics.GcCollections;
            managedAtStart = metrics.ManagedHeapBytes;
            reservedAtStart = metrics.TotalReservedBytes;
            ticksAtStart = metrics.TickCount;
            metrics.ResetLoadingWindow();
            metrics.MarkLoading(true);
            // Sample frames for the duration of this load. There is no Record button to
            // press, and a load is exactly when frame timing is worth having.
            metrics.SetSampling(true);
            log.LogInfo($"Loading window opened: {label}");
        }
        catch (Exception exception)
        {
            // A throw here would abort the caller, which may be the game's own load
            // path. Never let measurement break the thing being measured.
            loading = false;
            log.LogError($"BeginLoad failed: {exception}");
        }
    }

    public void EndLoad(string arrivedScene)
    {
        if (!loading)
        {
            return;
        }

        try
        {
            loading = false;
            metrics.MarkLoading(false);
            metrics.RefreshMemory();
            // Stop sampling now that the load is over, so the frame statistics describe
            // one load rather than an arbitrary slice of the session.
            metrics.SetSampling(false);

            var frames = metrics.LoadingFrameCount;
            var seconds = (float)(DateTime.UtcNow - loadStartedAt).TotalSeconds;
            // A load that appears to take minutes is almost always a window that stayed
            // open while the game sat idle. Zone changes were separately measured at
            // 0.07-0.22 s, so anything this large is reported as suspect rather than
            // presented as a load time.
            var suspect = seconds > 120f;
            var @event = new LoadEvent
            {
                Label = currentLabel,
                Scene = string.IsNullOrEmpty(arrivedScene) ? currentScene : arrivedScene,
                Seconds = seconds,
                Frames = frames,
                WorstFrameMs = metrics.LoadingWorstFrameMs,
                AverageFrameMs = frames > 0 ? metrics.LoadingFrameTotalMs / frames : 0f,
                Ticks = metrics.TickCount - ticksAtStart,
                Collections = metrics.GcCollections - collectionsAtStart,
                ManagedDelta = metrics.ManagedHeapBytes - managedAtStart,
                ReservedDelta = metrics.TotalReservedBytes - reservedAtStart,
                SuspectIdleTime = suspect,
            };
            loads.Add(@event);

            log.LogInfo(
                $"Loading window closed: {currentLabel} -> {@event.Scene} took {@event.Seconds:0.00}s " +
                $"over {frames} measured frames ({@event.Ticks} Update ticks), " +
                $"worst frame {@event.WorstFrameMs:0.0} ms, " +
                $"{@event.Collections} collections, managed {@event.ManagedDelta / 1024 / 1024.0:0.0} MB" +
                (suspect ? "  [SUSPECT: probably includes idle time, not a load]" : ""));
        }
        catch (Exception exception)
        {
            log.LogError($"EndLoad failed: {exception}");
        }

        // Outside the try: the report must still be attempted even if the accounting
        // above failed, because the report is the whole point.
        try
        {
            ProfilerRuntime.ExportNow();
        }
        catch (Exception exception)
        {
            log.LogError($"Reporting the finished load failed: {exception}");
        }
    }

    /// <summary>
    /// True when a window was too small to say anything about frames.
    ///
    /// This matters for honesty: the profile hooks mark loading as *finished*, so a
    /// window opened by such a hook closes moments later in the same frame. Reporting
    /// "0 Update ticks" for that as if it were evidence of a blocked main loop would
    /// be wrong. The duration of the load is still real; the frame attribution for it
    /// is not available this way.
    /// </summary>
    public static bool IsFrameWindowTooSmall(LoadEvent load)
        => load.Seconds < 0.5f && load.Ticks < 2;
}
