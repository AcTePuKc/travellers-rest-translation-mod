using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine.Profiling;

namespace TravellersRestProfiler;

/// <summary>
/// One recorded frame worth investigating.
/// </summary>
public sealed class FrameSpike
{
    public float Milliseconds { get; set; }
    public float SecondsSinceStart { get; set; }
    /// <summary>Collection delta observed on this frame, or 0.</summary>
    public int Collections { get; set; }
    public long AllocatedBytes { get; set; }
    public bool DuringLoading { get; set; }
}

/// <summary>
/// A frame long enough to be a freeze, with the game's own log lines from just before it.
///
/// The log context is what makes a stall explainable rather than merely observed: it says
/// what the game was doing while nothing was rendering. Reading the game's log by hand at
/// the right timestamp is impractical, so it is captured at the moment of the stall.
/// </summary>
public sealed class FrameStall
{
    public DateTime StartedAt { get; set; }
    public DateTime EndedAt { get; set; }
    public double DurationMs { get; set; }
    public bool DuringLoading { get; set; }
    public string CapturedLog { get; set; } = "";
}

/// <summary>
/// Frame timing and memory measurement.
///
/// Everything here is measured rather than inferred, which is the whole point of
/// building the profiler before building performance fixes. Per-mod attribution
/// via dynamic patching is deliberately *not* attempted here: it is the most
/// fragile part of the equivalent Graveyard Keeper 2 tool, and a wrong attribution
/// is worse than none. What this records instead is the frame cost, the garbage
/// collections that happened, and how the heap grows over time — enough to answer
/// "is the game leaking" and "where are the stalls" without guessing.
/// </summary>
public sealed class MetricsCollector
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly List<float> frameTimes = new List<float>(8192);
    private readonly List<FrameSpike> spikes = new List<FrameSpike>();
    private readonly List<FrameStall> stalls = new List<FrameStall>();
    private const int MaxSpikes = 25;

    private long lastFrameTimestamp;
    private long lastGcCollections;
    private long lastAllocatedBytes;

    public bool Recording { get; private set; }

    // ---- memory -----------------------------------------------------------------
    public long ManagedHeapBytes { get; private set; }
    public long TotalReservedBytes { get; private set; }
    public long TotalAllocatedBytes { get; private set; }
    public long MonoUsedBytes { get; private set; }
    public bool MemoryAvailable { get; private set; }

    // ---- aggregates -------------------------------------------------------------
    public int FrameCount => frameTimes.Count;
    public float AverageFrameMs { get; private set; }
    public float WorstFrameMs { get; private set; }
    public float Percentile99Ms { get; private set; }
    public float PeakFps { get; private set; }
    public int GcCollections { get; private set; }
    public long AllocatedSinceStart { get; private set; }
    public bool Loading { get; set; }

    /// <summary>Baseline captured by the first memory snapshot, for growth comparison.</summary>
    public long SnapshotBaselineManaged { get; private set; } = -1;
    public long SnapshotBaselineReserved { get; private set; } = -1;
    public DateTime? SnapshotTakenAt { get; private set; }
    public double SnapshotSecondsAgo =>
        SnapshotTakenAt.HasValue ? (DateTime.UtcNow - SnapshotTakenAt.Value).TotalSeconds : 0;

    public IReadOnlyList<FrameSpike> Spikes => spikes;

    /// <summary>
    /// Frames long enough to be a freeze rather than a slow frame. Captured separately
    /// because a freeze is the thing a player actually feels, and because each one is
    /// recorded with the game log lines that preceded it.
    /// </summary>
    public IReadOnlyList<FrameStall> Stalls => stalls;

    /// <summary>
    /// A frame at least this long is treated as a freeze and recorded with the game's own
    /// log lines from just before it. 2 s is far beyond any legitimate frame, including a
    /// heavy scene load. Configurable, because a machine that is slow throughout may want
    /// a higher bar to keep the report readable.
    /// </summary>
    public static double StallThresholdMs = 2000.0;

    /// <summary>
    /// The last exception raised inside a measurement call, or null.
    ///
    /// Every counter here is wrapped because a Unity exception thrown out of Update
    /// kills that method permanently and silently: no log line, no crash, the plugin
    /// simply stops ticking. Recording the failure converts that into evidence.
    /// </summary>
    public string LastError { get; private set; }

    private void Guarded(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            if (LastError == null)
            {
                LastError = $"{what}: {exception.GetType().Name}: {exception.Message}";
            }
        }
    }

    /// <summary>Duration of the most recent completed frame, for the loading tracker.</summary>
    public float LastFrameMs { get; private set; }

    /// <summary>
    /// Named rather than a raw property set, so callers read as intent: the loading
    /// tracker marks when a scene load window opens and closes, and spikes recorded
    /// inside it are tagged so the report can separate loading stalls from gameplay.
    /// </summary>
    public void MarkLoading(bool value)
    {
        Loading = value;
    }

    /// <summary>
    /// Accumulates frame time for a loading window independently of gameplay
    /// recording. Loading is the phase the game is reported to be worst at, so it
    /// must be measured whether or not anyone pressed Record first.
    /// </summary>
    public void MarkLoadingFrame(float milliseconds)
    {
        LoadingFrameCount++;
        LoadingFrameTotalMs += milliseconds;
        if (milliseconds > LoadingWorstFrameMs)
        {
            LoadingWorstFrameMs = milliseconds;
        }
    }

    public int LoadingFrameCount { get; private set; }
    public float LoadingFrameTotalMs { get; private set; }
    public float LoadingWorstFrameMs { get; private set; }

    /// <summary>
    /// Total Update ticks since load. A load window's delta tells us whether the
    /// plugin's Update ran at all while that load was in progress, which is the
    /// difference between "no frames were slow" and "we were not called".
    /// </summary>
    public long TickCount { get; private set; }

    public void ResetLoadingWindow()
    {
        LoadingFrameCount = 0;
        LoadingFrameTotalMs = 0f;
        LoadingWorstFrameMs = 0f;
    }

    /// <summary>
    /// Turns sampling on and off. Called by the loading tracker: frames are sampled while
    /// a load is in progress, which is when they matter and when there is no Record button
    /// to press. Outside a load the samples are cleared, so the frame statistics always
    /// describe one load rather than an arbitrary slice of a session.
    /// </summary>
    public void SetSampling(bool enabled)
    {
        if (enabled == Recording)
        {
            return;
        }

        if (enabled)
        {
            frameTimes.Clear();
            spikes.Clear();
            GcCollections = 0;
            AllocatedSinceStart = 0;
            AverageFrameMs = 0f;
            WorstFrameMs = 0f;
            Percentile99Ms = 0f;
            PeakFps = 0f;
            lastGcCollections = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2);
            lastAllocatedBytes = GC.GetTotalMemory(false);
            Recording = true;
        }
        else
        {
            Recording = false;
            Recalculate();
        }
    }

    public void StartRecording()
    {
        frameTimes.Clear();
        spikes.Clear();
        GcCollections = 0;
        AllocatedSinceStart = 0;
        AverageFrameMs = 0f;
        WorstFrameMs = 0f;
        Percentile99Ms = 0f;
        PeakFps = 0f;
        lastFrameTimestamp = clock.ElapsedTicks;
        lastGcCollections = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2);
        lastAllocatedBytes = GC.GetTotalMemory(false);
        Recording = true;
    }

    public void StopRecording()
    {
        Recording = false;
        Recalculate();
    }

    /// <summary>
    /// Feeds one frame-time sample directly, for drivers that are not a per-frame
    /// callback. The profiler's heartbeat runs on scene events, so this is how elapsed
    /// wall time between those events reaches the statistics.
    /// </summary>
    public void TickWithElapsed(double milliseconds)
    {
        try
        {
            LastFrameMs = (float)milliseconds;
            if (float.IsNaN(LastFrameMs) || float.IsInfinity(LastFrameMs) || LastFrameMs <= 0f)
            {
                return;
            }

            // A frame this long is a freeze, not a slow frame, and it must be recorded
            // whether or not a recording is running — the freeze that matters happens
            // during a load, when nothing is recording. This is deliberately before the
            // Recording check below, which returns early.
            if (milliseconds >= StallThresholdMs)
            {
                var endedAt = DateTime.Now;
                stalls.Add(new FrameStall
                {
                    StartedAt = endedAt.AddMilliseconds(-milliseconds),
                    EndedAt = endedAt,
                    DurationMs = milliseconds,
                    DuringLoading = Loading,
                    CapturedLog = ProfilerRuntime.CaptureGameLogContext(),
                });
                // Logged through the runtime, because this class has no logger of its own.
                ProfilerRuntime.LogFreeze(milliseconds, Loading);
            }
            if (!Recording)
            {
                return;
            }

            frameTimes.Add(LastFrameMs);
            var count = frameTimes.Count;
            AverageFrameMs += (LastFrameMs - AverageFrameMs) / count;
            if (LastFrameMs > WorstFrameMs)
            {
                WorstFrameMs = LastFrameMs;
            }
        }
        catch (Exception exception)
        {
            if (LastError == null)
            {
                LastError = $"TickWithElapsed: {exception.GetType().Name}: {exception.Message}";
            }
        }
    }

    /// <summary>Called once per frame from the plugin's Update.</summary>
    public void Tick()
    {
        try
        {
            TickCore();
        }
        catch (Exception exception)
        {
            if (LastError == null)
            {
                LastError = $"Tick: {exception.GetType().Name}: {exception.Message}";
            }
        }
    }

    private void TickCore()
    {
        TickCount++;
        var now = clock.ElapsedTicks;
        var elapsedMs = (float)((now - lastFrameTimestamp) * 1000.0 / Stopwatch.Frequency);
        lastFrameTimestamp = now;

        // net472 has no float.IsFinite, so check both cases explicitly.
        if (float.IsNaN(elapsedMs) || float.IsInfinity(elapsedMs) || elapsedMs <= 0f)
        {
            return;
        }

        // Recorded before the recording check, because the loading tracker needs the
        // latest frame time whether or not a gameplay recording is running. Loading
        // is measured automatically, so it must not depend on someone having pressed
        // Record first.
        LastFrameMs = elapsedMs;

        if (!Recording)
        {
            return;
        }

        // A frame this long is a freeze, not a slow frame. It is the thing worth
        // explaining, so it is recorded with the wall-clock window it occupied: the game's
        // own Player.log can then be read for that window to see what the game was doing
        // while nothing was rendering. The overshoot can be large because the counter only
        // runs when frames do.
        if (elapsedMs >= StallThresholdMs)
        {
            var endedAt = DateTime.Now;
            stalls.Add(new FrameStall
            {
                StartedAt = endedAt.AddMilliseconds(-elapsedMs),
                EndedAt = endedAt,
                DurationMs = elapsedMs,
                DuringLoading = Loading,
                CapturedLog = ProfilerRuntime.CaptureGameLogContext(),
            });
        }

        frameTimes.Add(elapsedMs);

        // Collections and allocation observed since the previous frame.
        var collections = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2);
        var collectionDelta = (int)(collections - lastGcCollections);
        lastGcCollections = collections;

        var allocated = GC.GetTotalMemory(false);
        var allocationDelta = allocated - lastAllocatedBytes;
        lastAllocatedBytes = allocated;

        GcCollections += collectionDelta;
        if (allocationDelta > 0)
        {
            AllocatedSinceStart += allocationDelta;
        }

        // A spike is a frame notably worse than the running average, or any frame
        // that triggered a collection.
        var threshold = AverageFrameMs > 0f ? AverageFrameMs * 1.75f : 33f;
        if (elapsedMs >= threshold || collectionDelta > 0)
        {
            spikes.Add(new FrameSpike
            {
                Milliseconds = elapsedMs,
                SecondsSinceStart = (float)clock.Elapsed.TotalSeconds,
                Collections = collectionDelta,
                AllocatedBytes = allocationDelta > 0 ? allocationDelta : 0,
                DuringLoading = Loading,
            });
            if (spikes.Count > MaxSpikes)
            {
                // drop the least interesting: the smallest spike seen so far
                var smallest = 0;
                for (var i = 1; i < spikes.Count; i++)
                {
                    if (spikes[i].Milliseconds < spikes[smallest].Milliseconds)
                    {
                        smallest = i;
                    }
                }
                spikes.RemoveAt(smallest);
            }
        }

        // keep the aggregate current without a full sort on every frame
        var count = frameTimes.Count;
        AverageFrameMs += (elapsedMs - AverageFrameMs) / count;
        if (elapsedMs > WorstFrameMs)
        {
            WorstFrameMs = elapsedMs;
        }
        var instantFps = 1000f / elapsedMs;
        if (instantFps > PeakFps)
        {
            PeakFps = instantFps;
        }
    }

    public void Recalculate()
    {
        if (frameTimes.Count == 0)
        {
            return;
        }
        var sorted = new List<float>(frameTimes);
        sorted.Sort();
        var index = (int)Math.Floor(sorted.Count * 0.99);
        Percentile99Ms = sorted[Math.Min(index, sorted.Count - 1)];
    }

    /// <summary>Reads the memory counters. Safe to call on demand rather than per frame.</summary>
    public void RefreshMemory()
    {
        Guarded("RefreshMemory", () =>
        {
            ManagedHeapBytes = GC.GetTotalMemory(false);
            TotalReservedBytes = Profiler.GetTotalReservedMemoryLong();
            TotalAllocatedBytes = Profiler.GetTotalAllocatedMemoryLong();
            MonoUsedBytes = Profiler.GetMonoUsedSizeLong();
            MemoryAvailable = true;
        });
    }

    /// <summary>
    /// Records the current memory as the baseline for growth comparison. Taking two
    /// snapshots some minutes apart is how a leak is identified.
    /// </summary>
    public void TakeSnapshot()
    {
        RefreshMemory();
        SnapshotBaselineManaged = ManagedHeapBytes;
        SnapshotBaselineReserved = TotalReservedBytes;
        SnapshotTakenAt = DateTime.UtcNow;
    }

    public long GrowthManaged => SnapshotBaselineManaged < 0 ? 0 : ManagedHeapBytes - SnapshotBaselineManaged;
    public long GrowthReserved => SnapshotBaselineReserved < 0 ? 0 : TotalReservedBytes - SnapshotBaselineReserved;

    public string Uptime => clock.Elapsed.ToString(@"hh\:mm\:ss");
}
