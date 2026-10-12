using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace TravellersRestProfiler;

/// <summary>
/// A performance diagnostic for Travellers Rest.
///
/// It measures and writes a report. There are no keys, no in-game window and nothing to
/// press: on this game a BepInEx plugin component's Awake runs but Unity never then calls
/// Start, Update, LateUpdate or OnGUI on it, so an interactive window was never going to
/// be reliable. Removing it made the plugin simpler and more honest about what it is.
///
/// This class is deliberately small. Everything else lives in
/// <see cref="ProfilerRuntime"/>, driven by Unity scene events and a Harmony patch on a
/// method the game calls every frame.
///
/// It changes no gameplay and writes nothing into saves. It reads counters and writes one
/// text report.
/// </summary>
[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "actepukc.travellersrest.profiler";
    public const string PluginName = "Travellers Rest Performance Diagnostic";
    public const string PluginVersion = "0.1.0";

    internal static ManualLogSource Log;
    internal static ConfigEntry<float> ReportIntervalSeconds;
    internal static ConfigEntry<int> StallThresholdMs;
    internal static ConfigEntry<bool> ShowHud;
    internal static ConfigEntry<float> HudMargin;
    internal static ConfigEntry<bool> HudOnLeft;
    internal static ConfigEntry<bool> HudAtBottom;

    private void Awake()
    {
        try
        {
            Log = Logger;

            ReportIntervalSeconds = Config.Bind("General", "ReportIntervalSeconds", 20f,
                "Rewrite the report this often, in seconds. A report is also written whenever a " +
                "load finishes and shortly after the game becomes interactive, so a short session " +
                "still produces one. 0 disables the timed rewrite.");
            StallThresholdMs = Config.Bind("General", "StallThresholdMs", 2000,
                "A frame at least this long is recorded as a freeze, together with the game's own " +
                "log lines from just before it. This is what makes a freeze explainable rather " +
                "than merely observed.");
            ShowHud = Config.Bind("General", "ShowHud", true,
                "Show the small status panel in the corner of the screen. It exists so a long " +
                "wait is not mistaken for a hang. Set to false for a completely invisible mod.");
            HudMargin = Config.Bind("General", "HudMargin", 0f,
                "Distance of the status panel from the screen edge, in pixels. 0 picks a value " +
                "based on your screen height. Raise it if the panel overlaps something.");
            HudOnLeft = Config.Bind("General", "HudOnLeft", true,
                "Place the status panel on the left. Bottom-left is the only corner this game " +
                "leaves empty: the top-left holds the clock and money, the top-right is where " +
                "achievement notifications appear, and the bottom centre holds the hotbar.");
            HudAtBottom = Config.Bind("General", "HudAtBottom", true,
                "Place the status panel at the bottom of the screen rather than the top.");

            var metrics = new MetricsCollector();
            var mods = ModInventory.Collect(Log);
            Log.LogInfo($"{PluginName} {PluginVersion} loaded. Installed mods: {mods.Count}");

            metrics.RefreshMemory();

            // No memory snapshot here. A baseline taken during Awake compares a pre-load
            // process against a fully loaded one, which produced a meaningless
            // "701 MB per minute" growth figure. The runtime takes it once the game settles.

            var loading = new LoadingTracker(metrics, Log);
            loading.Install();

            // A per-frame heartbeat, which scene events cannot provide: they fire only when a
            // scene changes, so without this the measurement stops as soon as the player is in
            // a stable scene. The target class may not be loaded yet, so attachment is retried
            // from the scene events.
            FrameHook.Initialise(Log);

            ProfilerRuntime.Initialise(metrics, mods, loading, new ProfilerRuntime.ConfigAdapter
            {
                AutoExportSeconds = ReportIntervalSeconds.Value,
                StallThresholdMs = StallThresholdMs.Value,
                ShowHud = ShowHud.Value,
                HudMargin = HudMargin.Value,
                HudLeft = HudOnLeft.Value,
                HudBottom = HudAtBottom.Value,
            }, Log);
        }
        catch (Exception exception)
        {
            // An exception escaping Awake kills this component silently, so it is caught and
            // reported. The scene-event measurement is installed by then and is independent
            // of this component entirely.
            Log?.LogError($"Plugin Awake failed: {exception}");
        }
    }
}
