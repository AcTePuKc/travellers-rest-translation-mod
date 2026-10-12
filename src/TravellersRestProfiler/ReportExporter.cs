using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TravellersRestProfiler;

/// <summary>
/// Writes a plain-English report for sharing with a mod author or attaching to a
/// bug report.
///
/// The report is deliberately in English even when the UI is not, because its
/// audience is whoever maintains the mods named inside it. That is exactly the
/// convention the Graveyard Keeper 2 profiler used, and it is a good one: a
/// report nobody can read is not evidence.
/// </summary>
public static class ReportExporter
{
    public static string Build(MetricsCollector metrics, IReadOnlyList<ModEntry> mods, string gameVersion,
        IReadOnlyList<LoadEvent> loads = null)
    {
        var builder = new StringBuilder();
        var generated = DateTime.Now;

        builder.AppendLine("Travellers Rest Performance Diagnostic report");
        builder.AppendLine("================================");
        builder.AppendLine($"Generated      : {generated:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"Game version   : {gameVersion}");
        builder.AppendLine($"Session uptime : {metrics.Uptime}");
        builder.AppendLine($"Recording      : {(metrics.Recording ? "active" : "stopped")}");
        builder.AppendLine();

        var duplicates = new List<ModEntry>();
        foreach (var mod in mods)
        {
            if (mod.Duplicated)
            {
                duplicates.Add(mod);
            }
        }

        builder.AppendLine($"Installed mods : {mods.Count}");
        if (duplicates.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("DUPLICATE MODS - the same GUID is loaded more than once");
            builder.AppendLine("Each duplicate runs twice, so removing the older copy halves its cost.");
            foreach (var mod in duplicates)
            {
                builder.AppendLine($"  ! {mod.ShortName}  [{mod.Guid}]  {mod.Version}");
            }
        }

        builder.AppendLine();
        // Loading is listed before gameplay timing because it is the phase this game
        // is reported to be worst at, and it is measured automatically rather than
        // depending on someone having pressed Record at the right moment.
        builder.AppendLine("Loading");
        builder.AppendLine("-------");
        if (loads == null || loads.Count == 0)
        {
            builder.AppendLine("  No loading window recorded. Loading is detected automatically from scene");
            builder.AppendLine("  events, so this fills in by itself once the game loads a scene.");
        }
        else
        {
            builder.AppendLine("  Loading is timed separately from gameplay. 'worst frame' is the single");
            builder.AppendLine("  longest frame inside that load, which is usually where the stall is.");
            builder.AppendLine();
            var worstFirst = new List<LoadEvent>(loads);
            worstFirst.Sort((a, b) => b.WorstFrameMs.CompareTo(a.WorstFrameMs));
            foreach (var load in worstFirst)
            {
                var flag = load.SuspectIdleTime ? "   [SUSPECT - includes idle time]" : "";
                builder.AppendLine($"  {load.Label} -> {load.Scene}{flag}");
                if (load.SuspectIdleTime)
                {
                    builder.AppendLine(
                        "      This window stayed open far longer than any load. A scene unloaded and");
                    builder.AppendLine(
                        "      the game then sat idle before the next one arrived, so the duration");
                    builder.AppendLine(
                        "      includes that idle time. Do not read it as a load time.");
                }
                builder.AppendLine($"      duration {load.Seconds:0.00} s   Update ticks during load: {load.Ticks}");
                if (load.Frames > 0)
                {
                    builder.AppendLine(
                        $"      worst frame {load.WorstFrameMs:0.0} ms   average frame {load.AverageFrameMs:0.0} ms " +
                        $"over {load.Frames} frames");
                }
                else if (load.Ticks < 2)
                {
                    // No heartbeat was running for this window at all. With the per-frame
                    // hook attached, later loads do get frame attribution.
                    builder.AppendLine(
                        "      No heartbeat during this window, so the duration is wall time and");
                    builder.AppendLine(
                        "      no frame figures exist for it. This is expected for loads that");
                    builder.AppendLine(
                        "      finished before the per-frame hook was attached.");
                }
                builder.AppendLine(                    $"      {load.Collections} garbage collection(s)   " +
                    $"managed {Bytes(load.ManagedDelta)}   reserved {Bytes(load.ReservedDelta)}");
            }
        }

        builder.AppendLine();
        // Stalls come before the aggregate frame figures, because a freeze is what a
        // player actually notices and the capture explains what caused it.
        builder.AppendLine("Stalls (frames of 2 s or more)");
        builder.AppendLine("------------------------------");
        if (metrics.Stalls.Count == 0)
        {
            builder.AppendLine("  None recorded.");
        }
        else
        {
            foreach (var stall in metrics.Stalls)
            {
                builder.AppendLine(
                    $"  {stall.DurationMs / 1000.0:0.00} s freeze, " +
                    $"{stall.StartedAt:HH:mm:ss.fff} to {stall.EndedAt:HH:mm:ss.fff}" +
                    (stall.DuringLoading ? "  (during a load)" : ""));
                builder.AppendLine("    what the game logged just before it:");
                foreach (var line in stall.CapturedLog.Split('\n'))
                {
                    builder.AppendLine("      " + line.TrimEnd());
                }
                builder.AppendLine();
            }
        }

        builder.AppendLine();
        // Sampling runs only while a load is in progress, and the samples are cleared at the
        // start of each load, so these figures describe one load rather than a session. The
        // heading says which, because "whole-session" would be a false claim.
        builder.AppendLine("Frame timing (during the most recent load)");
        builder.AppendLine("-----------------------------------------");
        if (metrics.FrameCount == 0)
        {
            builder.AppendLine("  No frame samples. This fills itself during a load; the per-load");
            builder.AppendLine("  timings and the freezes above are the primary measurements.");
        }
        else
        {
            builder.AppendLine($"  Frames sampled    : {metrics.FrameCount}");
            builder.AppendLine($"  Average frame     : {metrics.AverageFrameMs:0.00} ms  ({Fps(metrics.AverageFrameMs):0.0} FPS)");
            builder.AppendLine($"  99th percentile   : {metrics.Percentile99Ms:0.00} ms  ({Fps(metrics.Percentile99Ms):0.0} FPS)");
            builder.AppendLine($"  Worst frame       : {metrics.WorstFrameMs:0.00} ms  ({Fps(metrics.WorstFrameMs):0.0} FPS)");
            builder.AppendLine($"  Peak instantaneous: {metrics.PeakFps:0.0} FPS");
            builder.AppendLine($"  Garbage collections: {metrics.GcCollections}");
            builder.AppendLine($"  Managed allocation : {Bytes(metrics.AllocatedSinceStart)}");
        }

        builder.AppendLine();
        builder.AppendLine("Slowest frames");
        builder.AppendLine("--------------");
        if (metrics.Spikes.Count == 0)
        {
            builder.AppendLine("  None recorded yet.");
        }
        else
        {
            var spikes = new List<FrameSpike>(metrics.Spikes);
            spikes.Sort((a, b) => b.Milliseconds.CompareTo(a.Milliseconds));
            foreach (var spike in spikes)
            {
                var note = spike.Collections > 0
                    ? $", {spike.Collections} garbage collection(s)"
                    : "";
                if (spike.AllocatedBytes > 0)
                {
                    note += $", allocated {Bytes(spike.AllocatedBytes)}";
                }
                if (spike.DuringLoading)
                {
                    note += ", during loading";
                }
                builder.AppendLine(
                    $"  {spike.Milliseconds,8:0.00} ms at {TimeSpan.FromSeconds(spike.SecondsSinceStart):mm\\:ss}{note}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Memory");
        builder.AppendLine("------");
        if (!metrics.MemoryAvailable)
        {
            builder.AppendLine("  Unity's Profiler counters are unavailable in this build.");
        }
        else
        {
            builder.AppendLine($"  Managed heap       : {Bytes(metrics.ManagedHeapBytes)}");
            builder.AppendLine($"  Unity reserved     : {Bytes(metrics.TotalReservedBytes)}");
            builder.AppendLine($"  Unity allocated    : {Bytes(metrics.TotalAllocatedBytes)}");
            builder.AppendLine($"  Mono used          : {Bytes(metrics.MonoUsedBytes)}");
            builder.AppendLine();
            if (metrics.SnapshotBaselineManaged < 0)
            {
                builder.AppendLine("  Growth: no baseline snapshot yet. Take one now, keep playing,");
                builder.AppendLine("  then take a second one. A mod that keeps growing is a candidate leak.");
            }
            else
            {
                builder.AppendLine(
                    $"  Baseline taken     : {metrics.SnapshotBaselineManaged / 1024 / 1024.0:0.0} MB managed, " +
                    $"{metrics.SnapshotSecondsAgo:0} s ago");
                builder.AppendLine($"  Growth since       : {Bytes(metrics.GrowthManaged)} managed, {Bytes(metrics.GrowthReserved)} reserved");
                if (metrics.GrowthManaged > 0 && metrics.SnapshotSecondsAgo > 30)
                {
                    var perMinute = metrics.GrowthManaged / (metrics.SnapshotSecondsAgo / 60.0);
                    builder.AppendLine($"  Rate               : {Bytes((long)perMinute)} per minute");
                }
            }
        }

        builder.AppendLine();
        builder.AppendLine("Mod list");
        builder.AppendLine("--------");
        builder.AppendLine("  Harmony attributes counts the patch methods a mod declares, read from the");
        builder.AppendLine("  assembly. It is a static property of the code, not a runtime measurement.");
        builder.AppendLine();
        builder.AppendLine("  " + "Name".PadRight(34) + "Version".PadRight(12) + "Harmony".PadRight(9) + "GUID");
        foreach (var mod in mods)
        {
            builder.AppendLine("  "
                + Truncate(mod.ShortName, 33).PadRight(34)
                + Truncate(mod.Version, 11).PadRight(12)
                + mod.PatchCount.ToString(CultureInfo.InvariantCulture).PadRight(9)
                + mod.Guid + (mod.Duplicated ? "   [DUPLICATE]" : ""));
        }

        builder.AppendLine();
        builder.AppendLine("Not measured");
        builder.AppendLine("------------");
        builder.AppendLine("  Per-mod CPU and allocation attribution is not implemented, because");
        builder.AppendLine("  dynamic patching of every plugin method is unreliable and a wrong");
        builder.AppendLine("  attribution is worse than none. Frame cost, collections and memory");
        builder.AppendLine("  growth above are measured directly.");

        return builder.ToString();
    }

    /// <summary>
    /// Writes the report, overwriting the same file for the whole session.
    ///
    /// The report is rewritten every 20 seconds, so a timestamped name produced a pile of
    /// near-identical files. One "latest" file that is always current, plus one dated file
    /// kept from each session, is what is actually useful.
    /// </summary>
    public static string Write(string directory, string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "profiler-report-latest.txt");
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static double Fps(float milliseconds) => milliseconds > 0.001f ? 1000.0 / milliseconds : 0.0;

    private static string Bytes(long value)
    {
        if (value < 0)
        {
            return $"-{Bytes(-value)}";
        }
        if (value < 1024)
        {
            return $"{value} B";
        }
        if (value < 1024 * 1024)
        {
            return $"{value / 1024.0:0.0} KB";
        }
        if (value < 1024L * 1024 * 1024)
        {
            return $"{value / 1024.0 / 1024.0:0.0} MB";
        }
        return $"{value / 1024.0 / 1024.0 / 1024.0:0.00} GB";
    }

    private static string Truncate(string value, int max)
        => string.IsNullOrEmpty(value) ? "" : (value.Length <= max ? value : value.Substring(0, max - 1) + "…");
}
