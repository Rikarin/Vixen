// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Runtime.InteropServices;
using Vixen.Core.Collections;

namespace Vixen.Core.Diagnostics;

/// <summary>
///     One timed scope: which key, when it started, how long it took, and how deeply nested it was.
/// </summary>
/// <remarks>
///     Twenty-four bytes. <see cref="DurationTicks" /> is a 32-bit count rather than a second
///     timestamp, which halves the field and costs nothing real — at the stopwatch's frequency it
///     tops out around two seconds, and a scope that long is a hang rather than a sample.
/// </remarks>
/// <param name="Key">Which scope this is.</param>
/// <param name="Depth">How many scopes were already open on this thread when it began.</param>
/// <param name="BeginTicks">When it began, in <see cref="Stopwatch" /> ticks.</param>
/// <param name="DurationTicks">How long it ran, in <see cref="Stopwatch" /> ticks.</param>
/// <param name="FrameIndex">Which frame it belongs to.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct ProfilerSample(
    ProfilingKey Key,
    int Depth,
    long BeginTicks,
    int DurationTicks,
    int FrameIndex
) {
    /// <summary>How long the scope ran, in milliseconds.</summary>
    public double DurationMilliseconds => DurationTicks * 1000.0 / Stopwatch.Frequency;

    /// <summary>How long the scope ran, in microseconds — the unit a trace viewer wants.</summary>
    public double DurationMicroseconds => DurationTicks * 1_000_000.0 / Stopwatch.Frequency;
}

/// <summary>
///     Scoped CPU sampling. Samples go into a per-thread ring, so recording one is a handful of
///     stores with no lock, no allocation and no contention.
/// </summary>
/// <remarks>
///     <para>
///         <b>Meant to be left on.</b> A profiler you have to enable is a profiler that is off when
///         the bug happens. The cost of an inactive scope is one <c>volatile</c> read; the cost of an
///         active one is a timestamp pair and a ring write. Build with
///         <c>VIXEN_NO_PROFILER</c> to remove even that.
///     </para>
///     <para>
///         Rings overwrite, so a thread that samples heavily and is never collected keeps its most
///         recent history rather than growing. <see cref="DroppedSampleCount" /> says how much went
///         over the side, because a truncated trace that does not admit it is worse than no trace.
///     </para>
/// </remarks>
public static class Profiler {
#if VIXEN_NO_PROFILER
    /// <summary>Whether this build can sample at all. A compile-time constant.</summary>
    public const bool IsSupported = false;
#else
    /// <summary>Whether this build can sample at all. A compile-time constant.</summary>
    public const bool IsSupported = true;
#endif

    /// <summary>How many samples each thread's ring holds before it starts overwriting.</summary>
    public const int DefaultCapacityPerThread = 65_536;

    static readonly Lock RecorderLock = new();
    static readonly List<ThreadRecorder> Recorders = [];

    [ThreadStatic]
    static ThreadRecorder? recorder;

    static volatile bool enabled;
    static int capacityPerThread = DefaultCapacityPerThread;
    static int frameIndex;

    // Bumped by Reset. A recorder carries the generation it was registered under, so a thread that
    // is still holding one from before a reset can tell — see Begin.
    static volatile int generation;

    /// <summary>
    ///     Whether scopes are being recorded. A single volatile read on the hot path, which is what
    ///     makes leaving the instrumentation compiled in affordable.
    /// </summary>
    public static bool IsEnabled {
        get => IsSupported && enabled;
        set => enabled = value && IsSupported;
    }

    /// <summary>The frame samples are currently being attributed to.</summary>
    public static int FrameIndex => frameIndex;

    /// <summary>How many samples each thread's ring holds. Takes effect for threads not yet seen.</summary>
    public static int CapacityPerThread {
        get => capacityPerThread;
        set {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            capacityPerThread = value;
        }
    }

    /// <summary>How many samples have been overwritten before anybody collected them.</summary>
    public static long DroppedSampleCount {
        get {
            lock (RecorderLock) {
                var total = 0L;
                foreach (var thread in Recorders) {
                    total += thread.Samples.OverwrittenCount;
                }

                return total;
            }
        }
    }

    /// <summary>Opens a scope. Dispose it — or let <c>using</c> do so — to record the sample.</summary>
    /// <param name="key">Which scope this is.</param>
    /// <returns>A scope that records itself when it closes.</returns>
    public static Scope Begin(ProfilingKey key) {
        if (!IsEnabled) {
            return default;
        }

        // The generation check is what keeps the thread-static pointer honest: Reset runs on one
        // thread but has to be seen by all of them, and a recorder from a previous generation is no
        // longer in Recorders, so anything written into it would never be collected.
        var thread = recorder;
        if (thread is null || thread.Generation != generation) {
            thread = recorder = CreateRecorder();
        }

        return new(key, Stopwatch.GetTimestamp(), thread.Depth++);
    }

    /// <summary>Advances the frame counter that later samples are attributed to.</summary>
    /// <returns>The new frame index.</returns>
    public static int BeginFrame() => Interlocked.Increment(ref frameIndex);

    /// <summary>
    ///     Copies out every thread's samples and empties the rings.
    /// </summary>
    /// <returns>The samples, grouped by thread and ordered by when each scope began.</returns>
    /// <remarks>
    ///     Takes a lock over the recorder list, so it is a frame-boundary operation and not something
    ///     to call from inside a measured region.
    /// </remarks>
    public static ProfilerThreadSamples[] Collect() {
        lock (RecorderLock) {
            var result = new ProfilerThreadSamples[Recorders.Count];

            for (var i = 0; i < Recorders.Count; i++) {
                var thread = Recorders[i];
                var samples = new ProfilerSample[thread.Samples.Count];
                thread.Samples.CopyTo(samples);
                thread.Samples.Clear();

                result[i] = new(thread.ThreadId, thread.ThreadName, samples);
            }

            return result;
        }
    }

    /// <summary>Discards every recorded sample and forgets every thread that has recorded one.</summary>
    /// <remarks>
    ///     Safe to call from any thread, and forgets the other threads' recorders as thoroughly as the
    ///     caller's: each one is stamped with the generation it was registered under, so a thread that
    ///     was holding a recorder when the reset happened registers a fresh one the next time it opens
    ///     a scope rather than writing into a ring nobody is listed as owning.
    /// </remarks>
    public static void Reset() {
        lock (RecorderLock) {
            Recorders.Clear();

            // Under the lock and paired with the clear, so a recorder cannot be registered against a
            // generation that has already been thrown away.
            generation++;
        }

        frameIndex = 0;
    }

    static ThreadRecorder CreateRecorder() {
        lock (RecorderLock) {
            var thread = new ThreadRecorder(
                Environment.CurrentManagedThreadId,
                Thread.CurrentThread.Name ?? $"Thread {Environment.CurrentManagedThreadId}",
                capacityPerThread,
                generation
            );

            Recorders.Add(thread);
            return thread;
        }
    }

    static void Record(ProfilingKey key, long beginTicks, int depth) {
        var thread = recorder;
        if (thread is null) {
            return;
        }

        thread.Depth = depth;

        // A scope opened while sampling was on and closed after it was turned off still records:
        // dropping it would leave the trace with an unbalanced begin, which every viewer renders as
        // a scope that never ends.
        var duration = Stopwatch.GetTimestamp() - beginTicks;
        thread.Samples.Enqueue(
            new(key, depth, beginTicks, (int)Math.Min(duration, int.MaxValue), frameIndex)
        );
    }

    sealed class ThreadRecorder(int threadId, string threadName, int capacity, int generation) {
        public int ThreadId => threadId;
        public string ThreadName => threadName;

        /// <summary>Which <see cref="Profiler.generation" /> this recorder was registered under.</summary>
        public int Generation => generation;

        public RingBuffer<ProfilerSample> Samples { get; } = new(capacity);
        public int Depth { get; set; }
    }

    /// <summary>
    ///     An open profiling scope. A <c>ref struct</c>, so it cannot outlive the frame that opened
    ///     it or be captured into something that closes it on another thread.
    /// </summary>
    public readonly ref struct Scope {
        readonly ProfilingKey key;
        readonly long beginTicks;
        readonly int depth;

        internal Scope(ProfilingKey key, long beginTicks, int depth) {
            this.key = key;
            this.beginTicks = beginTicks;
            this.depth = depth;
        }

        /// <summary>Closes the scope and records the sample.</summary>
        public void Dispose() {
            if (beginTicks != 0) {
                Record(key, beginTicks, depth);
            }
        }
    }
}

/// <summary>One thread's worth of collected samples.</summary>
/// <param name="ThreadId">The managed thread id.</param>
/// <param name="ThreadName">The thread's name, or a generated one.</param>
/// <param name="Samples">The samples, oldest first.</param>
public sealed record ProfilerThreadSamples(int ThreadId, string ThreadName, ProfilerSample[] Samples);
