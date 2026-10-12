using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TravellersRestProfiler;

/// <summary>
/// The profiler's per-frame work, driven by callbacks that are proven to run on this
/// game rather than by Start or Update.
///
/// What is proven here, after a lot of failed attempts:
///
///   * A component created during Awake is never given Start, Update, LateUpdate or
///     OnGUI by this game, and FindObjectOfType does not find it afterwards.
///     Coroutines started on such a component never tick either.
///   * Unity's own <c>SceneManager.sceneLoaded</c> and <c>sceneUnloaded</c> events DO
///     fire, and they are what has been measuring loading successfully all along.
///
/// So this class is a plain object with no Unity lifecycle at all. The tracker calls
/// <see cref="Pump"/> from the scene events, and that is the heartbeat. Frame-by-frame
/// sampling is not available through this route, which is why the loading figures are
/// wall-clock durations — they are honest, but they are not frame attributions.
///
/// Reaching genuine per-frame measurement needs a Harmony patch on a method the game
/// calls every frame, the same technique the other mods in this repository use. That is
/// a deliberate next step, not something to guess at.
/// </summary>
public static class ProfilerRuntime
{
    private static ManualLogSource log;
    private static MetricsCollector metrics;
    private static List<ModEntry> mods;
    private static LoadingTracker loading;
    private static ConfigAdapter config;

    private static DateTime startedAt = DateTime.UtcNow;
    private static DateTime lastPump = DateTime.UtcNow;
    private static DateTime lastExport = DateTime.UtcNow;
    private static int pumpCount;
    private static string lastExportPath = "";
    private static bool reportedFailure;
    private static bool baselineTaken;
    private static bool interactiveSignalled;
    private static DateTime? interactiveAt;
    private static DateTime lastStatusWrite = DateTime.MinValue;

    /// <summary>
    /// How often the status file is rewritten. 500 ms is fast enough that a visible counter
    /// looks live and slow enough that the write is irrelevant next to a frame.
    /// </summary>
    private const int StatusIntervalMs = 500;

    /// <summary>
    /// How long the game must be running before the memory baseline is taken. Long enough
    /// for the initial load to finish, so growth afterwards means a leak rather than
    /// loading. Measured from when the game became interactive, not from process start,
    /// because startup itself takes 15-20 s here.
    /// </summary>
    private const int BaselineAfterSeconds = 45;

    /// <summary>
    /// Everything the profiler needs from the plugin's configuration, captured during
    /// Awake while configuration binding still works.
    /// </summary>
    public sealed class ConfigAdapter
    {
        public float AutoExportSeconds = 20f;
        public int StallThresholdMs = 2000;
        public bool ShowHud = true;
        public float HudMargin = 0f;
        public bool HudLeft = true;
        public bool HudBottom = true;
    }

    private static bool showHud = true;

    /// <summary>Distance from the corner, in pixels. 0 or less means "use the default".</summary>
    public static float HudMargin { get; private set; }

    /// <summary>True to place the panel on the left instead of the right.</summary>
    public static bool HudLeft { get; private set; }

    /// <summary>True to place the panel at the bottom instead of the top.</summary>
    public static bool HudBottom { get; private set; }

    /// <summary>The assembled parts, shared with whoever draws the report.</summary>
    public static MetricsCollector Metrics => metrics;
    public static LoadingTracker Loading => loading;
    public static IReadOnlyList<ModEntry> Mods => mods;
    public static int PumpCount => pumpCount;
    public static string LastExportPath => lastExportPath;
    public static string Uptime => (DateTime.UtcNow - startedAt).ToString(@"hh\:mm\:ss");

    public static void Initialise(MetricsCollector collector, List<ModEntry> modList,
        LoadingTracker tracker, ConfigAdapter adapter, ManualLogSource logger)
    {
        log = logger;
        metrics = collector;
        mods = modList;
        loading = tracker;
        config = adapter ?? new ConfigAdapter();
        startedAt = DateTime.UtcNow;
        lastPump = lastExport = startedAt;

        if (config.StallThresholdMs > 0)
        {
            MetricsCollector.StallThresholdMs = config.StallThresholdMs;
        }
        showHud = config.ShowHud;
        // 0 or negative in the config means "pick something sensible for this screen".
        HudMargin = config.HudMargin > 0f ? config.HudMargin : Math.Max(8f, Screen.height * 0.012f);
        HudLeft = config.HudLeft;
        HudBottom = config.HudBottom;

        log.LogInfo("Profiler runtime initialised. Measurement is driven by scene events, " +
                    "which are the callbacks proven to run on this game.");
    }

    /// <summary>
    /// Called from the loading tracker's scene callbacks. This is the heartbeat: it
    /// advances the frame timer and writes reports on schedule.
    /// </summary>
    public static void Pump(string reason)
    {
        try
        {
            var now = DateTime.UtcNow;
            pumpCount++;

            // Keep the game's log tail current. It is what explains a freeze.
            TailGameLog();

            var sinceLastPump = (now - lastPump).TotalSeconds;
            lastPump = now;

            // Feed the frame timer so its statistics reflect real elapsed time between
            // pumps. On a long load this is the load itself; between events it is the
            // time spent in that scene.
            if (sinceLastPump > 0)
            {
                metrics.TickWithElapsed(sinceLastPump * 1000.0);
            }

            // Frame timing for an open loading window is accumulated here rather than in
            // the plugin's Update, which this game never calls. Without this the frame
            // columns could never be anything but zero.
            if (loading != null && loading.IsLoading)
            {
                metrics.MarkLoadingFrame(metrics.LastFrameMs);
            }

            if (pumpCount == 1)
            {
                log.LogInfo("Profiler heartbeat is live (driven by scene events).");
            }

            // Signal that the game has reached a scene the player can interact with.
            //
            // The menu takes about 85-105 s to appear on a cold start, and the save load
            // is a separate 20-30 s after that. An automated test otherwise has to guess
            // a wait long enough for the slowest case, which wastes minutes every run.
            // The profiler already knows when loading finished, so it says so.
            if (!interactiveSignalled && !(loading?.IsLoading ?? false) && pumpCount > 2)
            {
                interactiveSignalled = true;
                interactiveAt = now;
                WriteReadyMarker();
            }

            // Take the memory baseline only once the game has settled. A baseline taken
            // during startup measures one-time scene construction, not a leak, and
            // reporting that as a growth rate would be actively misleading. The clock
            // starts when the game became interactive, because reaching that point
            // already costs 15-20 s here.
            var settledFor = interactiveAt.HasValue ? (now - interactiveAt.Value).TotalSeconds : -1;
            if (!baselineTaken && settledFor >= BaselineAfterSeconds)
            {
                baselineTaken = true;
                metrics.TakeSnapshot();
                log.LogInfo($"Memory baseline taken {BaselineAfterSeconds}s after the game became " +
                            $"interactive: {metrics.ManagedHeapBytes / 1024 / 1024} MB managed. " +
                            "Growth from here on is the meaningful figure.");
            }

            // Keep the status file current so the optional on-screen window can show what is
            // happening. Throttled, because this runs every frame and a file write per frame
            // would itself become a performance problem — which would be an absurd thing for
            // a performance tool to do.
            if ((now - lastStatusWrite).TotalMilliseconds >= StatusIntervalMs)
            {
                lastStatusWrite = now;
                WriteStatus(now);
            }

            if (config.AutoExportSeconds > 0f && (now - lastExport).TotalSeconds >= config.AutoExportSeconds)
            {
                lastExport = now;                Export(true);
            }
        }
        catch (Exception exception)
        {
            if (!reportedFailure)
            {
                reportedFailure = true;
                log.LogError($"Profiler heartbeat failed: {exception}");
            }
        }
    }

    /// <summary>Writes a report now. Used by the heartbeat and by the finished-load hook.</summary>
    public static void ExportNow() => Export(true);

    public static void Export(bool automatic)
    {
        try
        {
            // Re-read the mod list rather than using whatever was captured at startup.
            //
            // BepInEx loads plugins one after another, and this plugin is loaded early, so
            // during Awake only the plugins before it exist. Reading Chainloader.PluginInfos
            // then reported "Installed mods: 2" while five were actually loaded — Mod Config
            // Menu, the translation loader and MaterialsNow were all missing from the list.
            // Re-reading here fixes it, and the report is the only place the list is used.
            var currentMods = ModInventory.Collect(log) ?? mods;
            if (currentMods.Count > 0)
            {
                mods = currentMods;
            }

            var content = ReportExporter.Build(metrics, mods, GameVersion(), loading?.Loads);
            lastExportPath = ReportExporter.Write(ReportDirectory(), content);
            log.LogInfo(automatic
                ? $"Report written automatically to {lastExportPath} ({mods.Count} mods listed)"
                : $"Report written to {lastExportPath} ({mods.Count} mods listed)");
        }
        catch (Exception exception)
        {
            lastExportPath = "";
            log.LogError($"Could not write the report: {exception}");
        }
    }

    /// <summary>
    /// The game's real version string, for example "v0.7.6.12.0".
    ///
    /// Application.version reports "0.1" for this game, which is build metadata and not
    /// the version a player sees, so the report was misleading. The game keeps its own
    /// string on VersionNumberManager, reached by reflection and wrapped, because a game
    /// update could rename any of it. Falling back to Application.version is deliberate:
    /// a slightly wrong version is better than a failed report.
    /// </summary>
    private static string GameVersion()
    {
        try
        {
            var type = Type.GetType("VersionNumberManager")
                       ?? FindTypeByName("VersionNumberManager");
            if (type != null)
            {
                var instanceField = type.GetField("instance",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                var instance = instanceField?.GetValue(null);

                if (instance == null)
                {
                    var manager = UnityEngine.Object.FindObjectOfType(type);
                    instance = manager;
                }

                var versionField = type.GetField("version",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (instance != null && versionField?.GetValue(instance) is string version &&
                    !string.IsNullOrEmpty(version))
                {
                    return version;
                }
            }
        }
        catch (Exception exception)
        {
            log?.LogInfo($"Could not read the game's own version string ({exception.Message}); " +
                         "falling back to Application.version.");
        }

        return Application.version;
    }

    private static Type FindTypeByName(string name)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var found = assembly.GetType(name, throwOnError: false);
                if (found != null)
                {
                    return found;
                }
            }
            catch (Exception)
            {
                // Some assemblies refuse reflection; skip them.
            }
        }
        return null;
    }

    /// <summary>
    /// Writes a short status file that the optional on-screen window displays.
    ///
    /// This is how a visible progress indicator is provided without drawing anything inside
    /// the game. This game never calls OnGUI on a plugin component, so an in-game overlay
    /// would need a Harmony patch on a per-frame method — a new patch that could break on a
    /// game update. Writing a text file cannot break anything, and the window that reads it
    /// is entirely separate: if it is not running, nothing is lost.
    ///
    /// Format is "key = value" per line, which the reader parses without a real format.
    /// </summary>
    private static void WriteStatus(DateTime now)
    {
        try
        {
            var directory = ReportDirectory();
            var path = System.IO.Path.Combine(directory, "status.txt");
            var uptime = (now - startedAt).TotalSeconds;
            var sinceInteractive = interactiveAt.HasValue ? (now - interactiveAt.Value).TotalSeconds : -1;
            var untilExport = config.AutoExportSeconds > 0f
                ? Math.Max(0, config.AutoExportSeconds - (now - lastExport).TotalSeconds)
                : 0;

            var text = new System.Text.StringBuilder();
            text.AppendLine("text = " + Describe(sinceInteractive, untilExport));
            text.AppendLine("elapsed = " + (int)uptime);
            text.AppendLine("step = " + (loading?.IsLoading == true ? "loading" : "running"));
            text.AppendLine("loads = " + (loading?.Loads.Count ?? 0));
            text.AppendLine("stalls = " + metrics.Stalls.Count);
            text.AppendLine("report = " + (string.IsNullOrEmpty(lastExportPath) ? "(not written yet)" : lastExportPath));

            System.IO.Directory.CreateDirectory(directory);
            System.IO.File.WriteAllText(path, text.ToString(), new System.Text.UTF8Encoding(false));
        }
        catch (Exception exception)
        {
            // Never let the indicator break the measurement.
            if (statusFailures++ < 2)
            {
                log?.LogWarning($"Could not write the status file: {exception.Message}");
            }
        }
    }

    private static int statusFailures;

    private static string Describe(double sinceInteractive, double untilExport)
    {
        if (loading?.IsLoading == true)
        {
            return $"Loading a scene ({loading.Loads.Count} finished so far)";
        }
        if (!interactiveAt.HasValue)
        {
            return "Starting the game...";
        }
        if (sinceInteractive < BaselineAfterSeconds)
        {
            return $"Waiting for the game to settle ({(int)(BaselineAfterSeconds - sinceInteractive)}s)";
        }
        if (untilExport > 0)
        {
            return $"Measuring, next report in {(int)untilExport}s";
        }
        return "Measuring";
    }

    /// <summary>
    /// Drops a marker file the moment the game is interactive.
    ///
    /// This is for automated testing: a harness can wait for the file instead of guessing
    /// a delay generous enough for the slowest cold start. It costs one small write.
    /// </summary>
    private static void WriteReadyMarker()
    {
        try
        {
            var path = System.IO.Path.Combine(ReportDirectory(), "ready-for-input.txt");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            System.IO.File.WriteAllText(path,
                $"interactive at {DateTime.Now:yyyy-MM-dd HH:mm:ss} after {Uptime}{Environment.NewLine}");
            log.LogInfo($"Interactive marker written: {path}");
        }
        catch (Exception exception)
        {
            log.LogWarning($"Could not write the interactive marker: {exception.Message}");
        }
    }

    /// <summary>
    /// The game's own log lines from the moments before a freeze.
    ///
    /// This is how a stall becomes explainable: the profiler can say a frame took 13.5 s,
    /// but only the game's log says what it was doing at the time. The file is tailed
    /// continuously while a load is in progress and the most recent lines are held in
    /// memory, so the answer is ready at the instant the frame ends.
    /// </summary>
    private static readonly Queue<string> recentGameLog = new Queue<string>(LogRingSize);
    private const int LogRingSize = 500;
    private const int CapturedLogLines = 40;

    private static string gameLogPath;
    private static long gameLogPosition;
    private static bool capturingLog;
    private static int logReadFailures;

    /// <summary>Start tailing the game's log. Called when a load begins.</summary>
    public static void BeginLoadCapture()
    {
        try
        {
            if (gameLogPath == null)
            {
                // Unity writes Player.log next to the game's other player data.
                var localLow = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                gameLogPath = System.IO.Path.Combine(
                    System.IO.Path.GetFullPath(System.IO.Path.Combine(localLow, "..", "LocalLow")),
                    "Louqou", "TravellersRest", "Player.log");
            }

            if (!System.IO.File.Exists(gameLogPath))
            {
                return;
            }

            // Start from the end of the file: only what happens from now on is interesting.
            gameLogPosition = new System.IO.FileInfo(gameLogPath).Length;
            recentGameLog.Clear();
            capturingLog = true;
        }
        catch (Exception exception)
        {
            if (logReadFailures++ < 2)
            {
                log?.LogWarning($"Could not start tailing the game log: {exception.Message}");
            }
        }
    }

    /// <summary>
    /// Reads whatever the game has appended since the last call. Cheap: it usually finds
    /// nothing, and the read is skipped entirely with no file handle held open.
    /// </summary>
    private static void TailGameLog()
    {
        if (!capturingLog || gameLogPath == null)
        {
            return;
        }

        try
        {
            using (var stream = new System.IO.FileStream(gameLogPath, System.IO.FileMode.Open,
                       System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
            {
                if (stream.Length <= gameLogPosition)
                {
                    // The game truncated its own log on restart.
                    if (stream.Length < gameLogPosition)
                    {
                        gameLogPosition = 0;
                        recentGameLog.Clear();
                    }
                    return;
                }

                stream.Seek(gameLogPosition, System.IO.SeekOrigin.Begin);
                using (var reader = new System.IO.StreamReader(stream))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        recentGameLog.Enqueue(line);
                        while (recentGameLog.Count > LogRingSize)
                        {
                            recentGameLog.Dequeue();
                        }
                    }
                    gameLogPosition = stream.Position;
                }
            }
        }
        catch (Exception exception)
        {
            if (logReadFailures++ < 3)
            {
                log?.LogWarning($"Game log tail failed: {exception.Message}");
            }
        }
    }

    /// <summary>
    /// Announces a recorded freeze. Called from the metrics collector, which has no logger
    /// of its own so that it stays usable without one.
    /// </summary>
    public static void LogFreeze(double milliseconds, bool duringLoading)
    {
        log?.LogWarning($"Freeze recorded: {milliseconds / 1000.0:0.00}s" +
                        (duringLoading ? " during a load" : "") +
                        ", with the game log from just before it.");
    }

    /// <summary>The last lines the game wrote before a stall, newest last.</summary>
    public static string CaptureGameLogContext()
    {
        try
        {
            var lines = recentGameLog.ToArray();
            var take = Math.Min(CapturedLogLines, lines.Length);
            if (take == 0)
            {
                return "(the game log had no new lines before this frame)";
            }

            var start = lines.Length - take;
            var builder = new System.Text.StringBuilder();
            for (var index = start; index < lines.Length; index++)
            {
                builder.AppendLine(lines[index]);
            }
            return builder.ToString().TrimEnd();
        }
        catch (Exception)
        {
            return "(log context unavailable)";
        }
    }

    /// <summary>
    /// Asks for the HUD to be created. Called every frame from the per-frame patch, which is
    /// the only place known to run that often; it creates the drawing component on first call
    /// and does nothing afterwards.
    ///
    /// Drawing itself cannot happen here. Unity refuses GUI calls outside OnGUI, which was
    /// learned the hard way: "You can only call GUI functions from inside OnGUI".
    /// </summary>
    public static void EnsureHud(ManualLogSource logger)
    {
        if (showHud && !hudDisabled)
        {
            HudRenderer.EnsureExists(logger);
        }
    }

    /// <summary>Stops the HUD for the session and says why, once.</summary>
    public static void DisableHud(string reason)
    {
        if (hudDisabled)
        {
            return;
        }
        hudDisabled = true;
        log?.LogWarning($"{reason} The HUD is disabled for this session; measurement continues.");
    }

    public static string StatusLine()
    {
        var sinceInteractive = interactiveAt.HasValue
            ? (DateTime.UtcNow - interactiveAt.Value).TotalSeconds
            : -1;
        var untilExport = config.AutoExportSeconds > 0f
            ? Math.Max(0, config.AutoExportSeconds - (DateTime.UtcNow - lastExport).TotalSeconds)
            : 0;
        return Describe(sinceInteractive, untilExport);
    }

    public static string ElapsedLine()
    {
        var uptime = DateTime.UtcNow - startedAt;
        return $"elapsed {(int)uptime.TotalMinutes:00}:{uptime.Seconds:00}";
    }

    public static string CountersLine()
        => $"loads {loading?.Loads.Count ?? 0}   freezes {metrics?.Stalls.Count ?? 0}";

    private static bool hudDisabled;

    private static string ReportDirectory()
    {        try
        {
            var root = BepInEx.Paths.BepInExRootPath;
            if (!string.IsNullOrEmpty(root))
            {
                return System.IO.Path.Combine(root, "TravellersRestProfiler");
            }
        }
        catch (Exception)
        {
            // fall through to the plugin directory
        }

        return System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(typeof(ProfilerRuntime).Assembly.Location), "reports");
    }
}
