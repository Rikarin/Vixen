// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Core.Diagnostics;

namespace Vixen.Editor.Profiler;

/// <summary>Where a capture's samples come from.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Doc 20 is blunt that "the profiler must be able to profile the editor", and this
///         interface is the whole of the mechanism.</b> An editor that could only profile the game
///         cannot answer why the editor is slow — and doc 00's editor-shell performance bar is a
///         claim about the editor. So the panel never touches <see cref="Vixen.Core.Diagnostics.Profiler" />
///         directly: it asks a source, and which source is a dropdown.
///     </para>
///     <para>
///         ⚠ <b>Collecting empties the rings, which is why a source is a thing and not a call.</b>
///         <c>Profiler.Collect</c> hands over the samples <i>and clears them</i>, so two readers of
///         the same rings see half the frame each. One source per process-side ring, owned by the
///         panel, is what keeps the second reader from being a bug nobody can reproduce.
///     </para>
/// </remarks>
public interface IProfileSource {
    /// <summary>What the dropdown calls it — "Editor", "Game", the name of an attached device.</summary>
    string Name { get; }

    /// <summary>Whether this source is recording at all.</summary>
    /// <remarks>
    ///     Settable, because the local source's answer is <c>Profiler.IsEnabled</c> and a panel that
    ///     could not turn sampling on would show an empty chart on a build that leaves it off.
    /// </remarks>
    bool IsRecording { get; set; }

    /// <summary>Takes whatever has been sampled since the last call, emptying the rings.</summary>
    /// <returns>The samples, grouped by thread.</returns>
    ProfilerThreadSamples[] Collect();

    /// <summary>How many samples went over the side before anybody collected them.</summary>
    long DroppedSampleCount { get; }
}

/// <summary>The process the editor is running in.</summary>
/// <remarks>
///     A wrapper over four static members rather than a layer: what it buys is that the panel has
///     one shape for "this process" and "a phone on the desk", and that a test can hand the model a
///     source with samples in it and no static state at all.
/// </remarks>
public sealed class LocalProfileSource : IProfileSource {
    /// <summary>Names a source over this process's rings.</summary>
    /// <param name="name">What the dropdown calls it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name" /> is null.</exception>
    public LocalProfileSource(string name) {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public bool IsRecording {
        get => Vixen.Core.Diagnostics.Profiler.IsEnabled;
        set => Vixen.Core.Diagnostics.Profiler.IsEnabled = value;
    }

    /// <inheritdoc />
    public long DroppedSampleCount => Vixen.Core.Diagnostics.Profiler.DroppedSampleCount;

    /// <inheritdoc />
    public ProfilerThreadSamples[] Collect() => Vixen.Core.Diagnostics.Profiler.Collect();
}

/// <summary>A source whose samples somebody else supplies — a remote build, or a test.</summary>
/// <remarks>
///     What the remote inspector attaches: a device streams its rings across and this is where they
///     arrive. It buffers rather than replacing, so a burst that spans two of the panel's ticks is
///     one capture rather than two halves.
/// </remarks>
public sealed class BufferedProfileSource : IProfileSource {
    readonly List<ProfilerThreadSamples> pending = [];
    readonly Lock gate = new();

    /// <summary>Names a buffered source.</summary>
    /// <param name="name">What the dropdown calls it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name" /> is null.</exception>
    public BufferedProfileSource(string name) {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public bool IsRecording { get; set; } = true;

    /// <inheritdoc />
    public long DroppedSampleCount { get; private set; }

    /// <summary>Adds a thread's worth of samples from wherever they arrived.</summary>
    /// <param name="samples">The samples.</param>
    /// <param name="dropped">How many the far end says it lost.</param>
    /// <exception cref="ArgumentNullException"><paramref name="samples" /> is null.</exception>
    public void Offer(ProfilerThreadSamples samples, long dropped = 0) {
        ArgumentNullException.ThrowIfNull(samples);

        lock (gate) {
            pending.Add(samples);
            DroppedSampleCount += dropped;
        }
    }

    /// <inheritdoc />
    public ProfilerThreadSamples[] Collect() {
        lock (gate) {
            var taken = pending.ToArray();
            pending.Clear();

            return taken;
        }
    }
}

/// <summary>One thread's samples, and where in the capture's window they fall.</summary>
/// <param name="ThreadId">The managed thread id.</param>
/// <param name="ThreadName">Its name.</param>
/// <param name="Samples">Its samples, oldest first.</param>
/// <param name="Roots">The depth-zero scopes, each with its children.</param>
public sealed record ProfileThread(
    int ThreadId,
    string ThreadName,
    ProfilerSample[] Samples,
    IReadOnlyList<FlameNode> Roots
);

/// <summary>Everything one press of the capture button collected.</summary>
/// <remarks>
///     <para>
///         <b>Immutable, and that is what makes comparison possible at all.</b> Doc 20's E4 asks for
///         "capture/compare", which needs two captures to exist at once — so a capture is a value
///         taken out of the rings rather than a window onto them, and the rings are free to wrap
///         the moment it is taken.
///     </para>
///     <para>
///         ⚠ <b>The window is derived from the samples, not from a clock.</b> A capture's start is
///         its earliest <c>BeginTicks</c> and its end is the largest begin-plus-duration — because
///         the samples arrive from several threads whose rings were filled at different moments,
///         and a window taken from <c>Stopwatch.GetTimestamp</c> around the collect call would be
///         wider than the data on one side and narrower on the other.
///     </para>
/// </remarks>
public sealed class ProfileCapture {
    /// <summary>The capture with nothing in it, which is what a panel shows before the first press.</summary>
    public static ProfileCapture Empty { get; } = new("", []);

    readonly ProfileThread[] threads;

    /// <summary>Builds a capture out of collected samples.</summary>
    /// <param name="source">Which source it came from.</param>
    /// <param name="collected">What the source handed over.</param>
    /// <param name="dropped">How many samples the source says it lost.</param>
    /// <exception cref="ArgumentNullException"><paramref name="collected" /> is null.</exception>
    public ProfileCapture(string source, ProfilerThreadSamples[] collected, long dropped = 0) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(collected);

        Source = source;
        Dropped = dropped;

        var built = new List<ProfileThread>(collected.Length);
        var start = long.MaxValue;
        var end = long.MinValue;
        var first = int.MaxValue;
        var last = int.MinValue;
        var total = 0;

        foreach (var thread in collected) {
            // ⚠ A thread that recorded nothing is dropped rather than listed empty. A pool of
            // sixteen workers where two did the frame's work would otherwise draw fourteen blank
            // lanes, which is the whole height of the panel spent saying nothing happened.
            if (thread.Samples.Length == 0) {
                continue;
            }

            built.Add(new(thread.ThreadId, thread.ThreadName, thread.Samples, FlameNode.Build(thread.Samples)));
            total += thread.Samples.Length;

            foreach (var sample in thread.Samples) {
                start = Math.Min(start, sample.BeginTicks);
                end = Math.Max(end, sample.BeginTicks + sample.DurationTicks);
                first = Math.Min(first, sample.FrameIndex);
                last = Math.Max(last, sample.FrameIndex);
            }
        }

        // Ordered longest-busy first, so the thread that did the work is the one at the top of the
        // panel rather than whichever one happened to register its ring first.
        built.Sort((left, right) => Busy(right).CompareTo(Busy(left)));

        threads = [.. built];
        SampleCount = total;

        BeginTicks = start == long.MaxValue ? 0 : start;
        EndTicks = end == long.MinValue ? 0 : end;
        FirstFrame = first == int.MaxValue ? 0 : first;
        LastFrame = last == int.MinValue ? 0 : last;

        Summary = ProfileSummary.Build(threads);
    }

    /// <summary>Which source it came from.</summary>
    public string Source { get; }

    /// <summary>The threads that recorded something, busiest first.</summary>
    public IReadOnlyList<ProfileThread> Threads => threads;

    /// <summary>Every scope in it, aggregated by key.</summary>
    public IReadOnlyList<ProfileEntry> Summary { get; }

    /// <summary>How many samples it holds, across every thread.</summary>
    public int SampleCount { get; }

    /// <summary>How many the source lost before this was taken.</summary>
    /// <remarks>
    ///     Shown, for the reason the console's dropped count is shown: a capture missing its
    ///     beginning and a capture where nothing happened look identical, and have opposite fixes.
    /// </remarks>
    public long Dropped { get; }

    /// <summary>When the earliest scope in it began, in <see cref="Stopwatch" /> ticks.</summary>
    public long BeginTicks { get; }

    /// <summary>When the last scope in it ended.</summary>
    public long EndTicks { get; }

    /// <summary>The lowest frame index any sample was attributed to.</summary>
    public int FirstFrame { get; }

    /// <summary>The highest.</summary>
    public int LastFrame { get; }

    /// <summary>How many frames it spans, never fewer than one.</summary>
    public int FrameCount => Math.Max(1, LastFrame - FirstFrame + 1);

    /// <summary>How wide the capture's window is, in milliseconds.</summary>
    public double DurationMilliseconds => (EndTicks - BeginTicks) * 1000d / Stopwatch.Frequency;

    /// <summary>Whether anything was collected.</summary>
    public bool IsEmpty => threads.Length == 0;

    /// <summary>Turns ticks into a fraction of the capture's window.</summary>
    /// <param name="ticks">An absolute <see cref="Stopwatch" /> reading.</param>
    /// <returns>Zero at the window's start, one at its end.</returns>
    /// <remarks>
    ///     A capture whose scopes all began in the same tick has a zero-wide window, and a division
    ///     there would put every bar at NaN — which draws as nothing at all rather than as an
    ///     obviously wrong picture.
    /// </remarks>
    public float Fraction(long ticks) {
        var span = EndTicks - BeginTicks;
        return span <= 0 ? 0f : (float)Math.Clamp((ticks - BeginTicks) / (double)span, 0d, 1d);
    }

    /// <summary>The samples belonging to one frame, on one thread.</summary>
    /// <param name="thread">Which thread.</param>
    /// <param name="frame">Which frame index.</param>
    /// <returns>The roots of that frame's scopes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="thread" /> is null.</exception>
    public static IReadOnlyList<FlameNode> Frame(ProfileThread thread, int frame) {
        ArgumentNullException.ThrowIfNull(thread);

        List<FlameNode> roots = [];

        foreach (var root in thread.Roots) {
            if (root.Sample.FrameIndex == frame) {
                roots.Add(root);
            }
        }

        return roots;
    }

    /// <summary>How long a thread's depth-zero scopes ran for, in ticks.</summary>
    /// <remarks>
    ///     Roots rather than every sample, because nested scopes are inside their parents and
    ///     summing all of them counts the same microsecond once per level of nesting — which makes
    ///     the deeply instrumented thread look busiest rather than the busy one.
    /// </remarks>
    static long Busy(ProfileThread thread) {
        var total = 0L;

        foreach (var root in thread.Roots) {
            total += root.Sample.DurationTicks;
        }

        return total;
    }
}
