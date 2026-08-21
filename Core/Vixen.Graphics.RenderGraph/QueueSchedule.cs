// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics.RenderGraph;

/// <summary>Whether the graph puts the whole frame on one queue or spreads it over the device's.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>An optimisation, never a requirement.</b> Every backend has a graphics queue and some
///         have nothing else — OpenGL has one queue and no way to express a second — so a pass marked
///         <see cref="PassKind.Compute" /> has to draw the same picture on a device that runs it
///         inline. That is the property <see cref="RenderGraph" /> holds to: scheduling changes which
///         list a pass is recorded into and nothing else, and the passes stay in declaration order
///         either way.
///     </para>
/// </remarks>
public enum QueueScheduling : byte {
    /// <summary>Everything on the graphics queue. The default.</summary>
    Single = 0,

    /// <summary>
    ///     Compute passes on the device's compute queue, where it has one of its own.
    /// </summary>
    /// <remarks>
    ///     Falls back to <see cref="Single" /> without complaint on a device whose
    ///     <see cref="GraphicsDeviceFeatures.HasAsyncCompute" /> is false, which is most of them.
    /// </remarks>
    Async = 1
}

/// <summary>A run of consecutive passes recorded into one command list on one queue.</summary>
/// <remarks>
///     <para>
///         Segments are cut where the queue changes and nowhere else, so they partition the frame's
///         surviving passes in declaration order. That is what makes an async-scheduled frame
///         comparable with a single-queue one: the same passes run in the same order, and the only
///         difference is how many lists they were recorded into.
///     </para>
///     <para>
///         A segment with <see cref="FirstPass" /> greater than <see cref="LastPass" /> holds no
///         passes at all. There is at most one at each end of the schedule, both on
///         <see cref="QueueKind.Graphics" />, and they exist so that the barriers handing imported
///         resources to and from their owner have a graphics list to be recorded on. An import
///         arrives owned by the graphics queue and has to be released from it; a frame whose first
///         pass is a compute one would otherwise record both halves of that handover on the
///         destination queue, which is legal to record and means nothing.
///     </para>
/// </remarks>
public sealed class RenderGraphSegment {
    readonly List<RenderGraphSegment> waits = [];

    internal RenderGraphSegment(int index, QueueKind queue, int firstPass, int lastPass) {
        Index = index;
        Queue = queue;
        FirstPass = firstPass;
        LastPass = lastPass;
    }

    /// <summary>Its position in the schedule, from 0.</summary>
    public int Index { get; }

    /// <summary>Which queue its list is submitted to.</summary>
    public QueueKind Queue { get; }

    /// <summary>The first pass it covers, as an index into the graph's declaration order.</summary>
    public int FirstPass { get; }

    /// <summary>The last pass it covers. Below <see cref="FirstPass" /> when it holds none.</summary>
    public int LastPass { get; internal set; }

    /// <summary>Whether it holds any passes at all.</summary>
    public bool IsEmpty => LastPass < FirstPass;

    /// <summary>The segments whose work must have finished before this one's may start.</summary>
    /// <remarks>
    ///     <para>
    ///         Only cross-queue edges are here. Two segments on the same queue are already ordered by
    ///         the order they are submitted in, and stating that as a wait would be asking the device
    ///         to synchronise something it cannot reorder.
    ///     </para>
    ///     <para>
    ///         Transitively reduced: a segment that waits on B, where B waits on A, does not also
    ///         wait on A. The reduction is for the reader — a schedule with every implied edge in it
    ///         is a picture nobody can see the shape of — and costs nothing, since waiting on B
    ///         already waits on A.
    ///     </para>
    /// </remarks>
    public IReadOnlyList<RenderGraphSegment> WaitsOn => waits;

    internal List<RenderGraphSegment> Waits => waits;

    /// <summary>The barriers recorded at the end of this segment's list.</summary>
    /// <remarks>
    ///     The release halves of the handovers to later segments, plus — on the last segment — the
    ///     transitions that put imported resources back the way their owner expects them.
    /// </remarks>
    internal List<PlannedBarrier> Tail { get; } = [];

    /// <summary>A name for the list, the debugger and the Graphviz dump.</summary>
    public string Name => $"segment {Index} ({Queue})";
}

/// <summary>Which queue each of a frame's passes runs on, and what has to wait for what.</summary>
/// <remarks>
///     <para>
///         Produced by <see cref="RenderGraph.Compile" /> and readable before anything is recorded,
///         which is the point: "did my compute pass actually go anywhere" is a question about the
///         schedule, and answering it by profiling a frame is answering it far too late.
///     </para>
///     <para>
///         A schedule is always valid to execute. On a device with one queue — or under
///         <see cref="QueueScheduling.Single" /> — it is one segment holding every surviving pass,
///         which is exactly what the graph did before schedules existed.
///     </para>
/// </remarks>
public sealed class RenderGraphSchedule {
    readonly QueueKind[] queueOfPass;
    readonly RenderGraphSegment[] segments;

    internal RenderGraphSchedule(RenderGraphSegment[] segments, QueueKind[] queueOfPass, int handovers) {
        this.segments = segments;
        this.queueOfPass = queueOfPass;
        OwnershipTransferCount = handovers;
    }

    /// <summary>The segments, in the order they are recorded and submitted.</summary>
    public IReadOnlyList<RenderGraphSegment> Segments => segments;

    /// <summary>Whether more than one queue is used.</summary>
    public bool IsMultiQueue {
        get {
            foreach (var segment in segments) {
                if (segment.Queue != QueueKind.Graphics) {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>How many resources change hands between queues over the frame.</summary>
    /// <remarks>
    ///     <b>Worth watching for the same reason <see cref="RenderGraph.BarrierCount" /> is.</b> Every
    ///     handover is a release, an acquire and an edge that stops two segments overlapping, so a
    ///     schedule whose transfer count is close to its resource count has bought a second queue and
    ///     spent the whole of it on synchronisation.
    /// </remarks>
    public int OwnershipTransferCount { get; }

    /// <summary>Which queue a pass runs on.</summary>
    /// <param name="passIndex">The pass, by declaration index.</param>
    /// <returns><see cref="QueueKind.Graphics" /> for a pass that was culled.</returns>
    public QueueKind QueueOf(int passIndex) =>
        passIndex >= 0 && passIndex < queueOfPass.Length ? queueOfPass[passIndex] : QueueKind.Graphics;
}

/// <summary>Where the graph gets a command list per segment, and what it does with it after.</summary>
/// <remarks>
///     <para>
///         The seam exists because submission is not the graph's business anywhere else either:
///         <see cref="RenderGraph.Execute(ICommandList)" /> records into a list the caller opened,
///         finishes nothing and submits nothing. A frame on two queues cannot work that way — the
///         graph decides how many lists there are — so it asks for them, and hands each one back the
///         moment it is full.
///     </para>
///     <para>
///         ⚠ <b>The implementation owns the waiting.</b> <see cref="RenderGraphSegment.WaitsOn" />
///         says what must have finished; how that is enforced is a property of the device, and the
///         RHI has no cross-queue primitive to express it with yet. <see cref="SerialisedQueues" />
///         is the implementation that is correct today.
///     </para>
/// </remarks>
public interface IRenderGraphQueues {
    /// <summary>Opens a list for a segment.</summary>
    /// <param name="segment">The segment about to be recorded.</param>
    /// <returns>An open list whose <see cref="ICommandList.Kind" /> is the segment's queue.</returns>
    ICommandList Begin(RenderGraphSegment segment);

    /// <summary>Takes a segment's list back, recorded.</summary>
    /// <param name="segment">The segment that was recorded.</param>
    /// <param name="list">Its list, which the implementation finishes and submits.</param>
    /// <remarks>
    ///     Called in segment order, so everything a segment waits on has already been handed over by
    ///     the time its own submission is asked for.
    /// </remarks>
    void Submit(RenderGraphSegment segment, ICommandList list);
}

/// <summary>
///     The queues of a real device, with each segment's dependencies enforced by waiting for the
///     queue it depends on to go idle.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Correct, and slower than one queue.</b> A wait for a queue to go idle is the biggest
///         hammer the RHI owns, and using one per cross-queue edge means the second queue never
///         overlaps the first — the frame does the same work in the same order and pays for the
///         submissions on top. Nothing about the picture changes, which is the point: this is what
///         makes the segmentation, the ownership transfers and the barriers testable on hardware
///         before there is a primitive that would make them fast.
///     </para>
///     <para>
///         <b>The primitive it is waiting for is a timeline semaphore</b> — one counter per frame,
///         each submission signalling a value and each dependent submission waiting on the value its
///         producer signalled. <c>GraphicsDeviceFeatures.HasTimelineSemaphores</c> already says which
///         devices have them and is read by nothing; that flag and this class are the two halves of
///         the same unfinished sentence, and the RHI needs a way to submit with a wait value before
///         they can be joined (overview § 1.4, task 208).
///     </para>
///     <para>
///         Perfectly reasonable to use meanwhile. A frame scheduled this way is the frame the fast
///         path will produce, so a golden image taken through it stays valid.
///     </para>
/// </remarks>
public sealed class SerialisedQueues : IRenderGraphQueues {
    readonly IGraphicsDevice device;
    readonly HashSet<QueueKind> submitted = [];

    /// <summary>Creates one over a device's queues.</summary>
    /// <param name="device">The device whose queues the segments are submitted to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device" /> is null.</exception>
    public SerialisedQueues(IGraphicsDevice device) {
        ArgumentNullException.ThrowIfNull(device);
        this.device = device;
    }

    /// <inheritdoc />
    public ICommandList Begin(RenderGraphSegment segment) {
        ArgumentNullException.ThrowIfNull(segment);
        return device.BeginCommandList(segment.Queue, segment.Name);
    }

    /// <inheritdoc />
    public void Submit(RenderGraphSegment segment, ICommandList list) {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(list);

        // Everything this segment waits on was submitted before it, so waiting for those queues to
        // drain is enough — and is the only thing the RHI can express. Waiting for the queue rather
        // than for the submission is what makes this slow rather than wrong.
        foreach (var producer in segment.WaitsOn) {
            if (submitted.Contains(producer.Queue)) {
                SubmitterFor(producer.Queue).WaitIdle();
            }
        }

        list.Finish();
        ReadOnlySpan<ICommandList> one = [list];
        SubmitterFor(segment.Queue).Submit(one);
        submitted.Add(segment.Queue);
        list.Dispose();
    }

    /// <summary>Forgets which queues have been submitted to, for the next frame.</summary>
    /// <remarks>
    ///     Nothing breaks if this is not called — the set only ever suppresses a wait for a queue that
    ///     has had nothing submitted to it — but a long-lived instance holding three entries forever
    ///     waits three times where it could wait none.
    /// </remarks>
    public void Reset() => submitted.Clear();

    ICommandSubmitter SubmitterFor(QueueKind kind) => kind switch {
        QueueKind.Compute => device.ComputeQueue,
        QueueKind.Transfer => device.TransferQueue,
        _ => device.GraphicsQueue
    };
}
