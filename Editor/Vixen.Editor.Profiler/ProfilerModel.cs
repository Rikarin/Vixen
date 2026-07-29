// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Profiler;

/// <summary>What the profiler panel is doing.</summary>
public enum ProfilerState : byte {
    /// <summary>Not sampling. What it opens as.</summary>
    Idle,

    /// <summary>Sampling into the current capture.</summary>
    Recording
}

/// <summary>The half of the profiler panel that is not chrome.</summary>
/// <remarks>
///     <para>
///         <b>Which source is selected, whether it is recording, what the last capture holds, and
///         what it is being compared against — none of which needs a <c>UiDocument</c> to be
///         right.</b> The same split <c>ConsoleModel</c> makes, for the same reason: the decisions
///         worth testing are all here and the view is bars and buttons over them.
///     </para>
///     <para>
///         ⚠ <b>Recording accumulates rather than replacing.</b> <c>Profiler.Collect</c> empties the
///         rings, so a model that rebuilt the capture from each tick's collect would show only the
///         last sixteen milliseconds — and a capture button that produced a sixteen-millisecond
///         window is one nobody can use. Samples are gathered across every tick between Start and
///         Stop and become one capture at the end.
///     </para>
///     <para>
///         ⚠ <b>The rings are drained while recording even though nothing draws them yet.</b> They
///         overwrite when full, so a five-second capture of a busy thread would otherwise be the
///         last sixty milliseconds of it with the rest silently gone. Draining per tick is what
///         makes a long capture a long capture.
///     </para>
/// </remarks>
public sealed class ProfilerModel {
    readonly List<IProfileSource> sources = [];
    readonly List<Vixen.Core.Diagnostics.ProfilerThreadSamples> gathered = [];

    IProfileSource? selected;
    long droppedAtStart;

    /// <summary>Raised when the capture, the state or the selection changed.</summary>
    public event Action<ProfilerModel>? Changed;

    /// <summary>The sources that can be profiled, in the order they were added.</summary>
    public IReadOnlyList<IProfileSource> Sources => sources;

    /// <summary>Which one is selected, or <see langword="null" /> when none has been added.</summary>
    public IProfileSource? Selected {
        get => selected;

        set {
            if (ReferenceEquals(selected, value)) {
                return;
            }

            // ⚠ A capture in progress is finished rather than discarded when the source changes.
            // Half a capture of the editor and half of the game merged into one flame chart is a
            // picture that cannot be interpreted and would not announce itself.
            if (State == ProfilerState.Recording) {
                Stop();
            }

            selected = value;
            Changed?.Invoke(this);
        }
    }

    /// <summary>Whether it is sampling.</summary>
    public ProfilerState State { get; private set; } = ProfilerState.Idle;

    /// <summary>The most recent finished capture.</summary>
    public ProfileCapture Capture { get; private set; } = ProfileCapture.Empty;

    /// <summary>What <see cref="Capture" /> is being compared against, or <see langword="null" />.</summary>
    public ProfileCapture? Baseline { get; private set; }

    /// <summary>The comparison, or an empty list when there is no baseline.</summary>
    public IReadOnlyList<ProfileDelta> Deltas { get; private set; } = [];

    /// <summary>Which thread's chart is on screen, by index into the capture's threads.</summary>
    public int Thread { get; set; }

    /// <summary>Which frame of the capture the chart is showing, or <see langword="null" /> for all of them.</summary>
    /// <remarks>
    ///     ⚠ <b>All of them is the default and is not the same as "the first".</b> A capture of two
    ///     hundred frames opened on frame one would show a chart of one frame's work and look like a
    ///     profiler that had recorded almost nothing; the whole window is what makes the shape of the
    ///     capture visible, and a frame is what somebody drills into afterwards.
    /// </remarks>
    public int? Frame { get; set; }

    /// <summary>Adds a source.</summary>
    /// <param name="source">The source. The first one added becomes the selection.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> is null.</exception>
    public void Add(IProfileSource source) {
        ArgumentNullException.ThrowIfNull(source);

        sources.Add(source);
        selected ??= source;

        Changed?.Invoke(this);
    }

    /// <summary>Removes a source, stopping the capture if it was the one being recorded.</summary>
    /// <param name="source">The source.</param>
    /// <returns>Whether it was there.</returns>
    public bool Remove(IProfileSource source) {
        if (!sources.Remove(source)) {
            return false;
        }

        if (ReferenceEquals(selected, source)) {
            if (State == ProfilerState.Recording) {
                Stop();
            }

            selected = sources.Count > 0 ? sources[0] : null;
        }

        Changed?.Invoke(this);
        return true;
    }

    /// <summary>Starts sampling into a fresh capture.</summary>
    /// <remarks>
    ///     The rings are emptied first, so the capture holds what happened after the button was
    ///     pressed rather than that plus however much history the ring happened to be holding.
    /// </remarks>
    public void Start() {
        if (State == ProfilerState.Recording || selected is not { } source) {
            return;
        }

        gathered.Clear();

        source.IsRecording = true;
        source.Collect();

        droppedAtStart = source.DroppedSampleCount;
        State = ProfilerState.Recording;

        Changed?.Invoke(this);
    }

    /// <summary>Takes whatever has been sampled since the last call.</summary>
    /// <remarks>Called once a frame by the view, and cheap when nothing was sampled.</remarks>
    public void Tick() {
        if (State != ProfilerState.Recording || selected is not { } source) {
            return;
        }

        gathered.AddRange(source.Collect());
    }

    /// <summary>Stops sampling and turns what was gathered into a capture.</summary>
    public void Stop() {
        if (State != ProfilerState.Recording || selected is not { } source) {
            return;
        }

        gathered.AddRange(source.Collect());

        // ⚠ Merged by thread, because every tick between Start and Stop produced its own
        // `ProfilerThreadSamples` for the same thread. Left as they came, one thread would draw as a
        // dozen lanes with a fragment of the frame in each.
        Capture = new(source.Name, Merge(gathered), source.DroppedSampleCount - droppedAtStart);

        gathered.Clear();
        State = ProfilerState.Idle;
        Thread = 0;
        Frame = null;

        Recompare();
        Changed?.Invoke(this);
    }

    /// <summary>Makes the current capture the thing later captures are compared against.</summary>
    /// <remarks>
    ///     ⚠ <b>The workflow doc 20's E4 asks for, in one button.</b> Capture, press this, make the
    ///     change, capture again — and the third column of the table is what the change cost. A
    ///     comparison against a capture nobody marked would be a comparison against whatever happened
    ///     to be there.
    /// </remarks>
    public void MarkBaseline() {
        Baseline = Capture.IsEmpty ? null : Capture;

        Recompare();
        Changed?.Invoke(this);
    }

    /// <summary>Forgets the baseline.</summary>
    public void ClearBaseline() {
        Baseline = null;
        Deltas = [];

        Changed?.Invoke(this);
    }

    /// <summary>The thread whose chart is on screen, or <see langword="null" /> when there is none.</summary>
    public ProfileThread? Current =>
        Thread >= 0 && Thread < Capture.Threads.Count ? Capture.Threads[Thread] : null;

    /// <summary>The roots the chart should draw, honouring <see cref="Frame" />.</summary>
    public IReadOnlyList<FlameNode> Roots =>
        Current is not { } thread
            ? []
            : Frame is { } frame
                ? ProfileCapture.Frame(thread, frame)
                : thread.Roots;

    void Recompare() =>
        Deltas = Baseline is { } baseline && !Capture.IsEmpty
            ? CaptureComparison.Compare(baseline, Capture)
            : [];

    /// <summary>Folds several collects' worth of one thread's samples back into one array each.</summary>
    static Vixen.Core.Diagnostics.ProfilerThreadSamples[] Merge(
        List<Vixen.Core.Diagnostics.ProfilerThreadSamples> collected
    ) {
        Dictionary<int, List<Vixen.Core.Diagnostics.ProfilerSample>> byThread = [];
        Dictionary<int, string> names = [];

        foreach (var thread in collected) {
            if (thread.Samples.Length == 0) {
                continue;
            }

            if (!byThread.TryGetValue(thread.ThreadId, out var samples)) {
                byThread[thread.ThreadId] = samples = [];
                names[thread.ThreadId] = thread.ThreadName;
            }

            samples.AddRange(thread.Samples);
        }

        var merged = new Vixen.Core.Diagnostics.ProfilerThreadSamples[byThread.Count];
        var index = 0;

        foreach (var (id, samples) in byThread) {
            merged[index++] = new(id, names[id], [.. samples]);
        }

        return merged;
    }
}
