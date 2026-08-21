// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics.Null;
using Xunit;

namespace Vixen.Graphics.RenderGraph.Tests;

/// <summary>What a cross-queue wait costs, and that taking the cheap one changes nothing else.</summary>
/// <remarks>
///     <para>
///         <b>Two paths, one frame.</b> <see cref="DeviceQueues" /> enforces a segment's wait edges by
///         submitting with the points its producers reached; <see cref="SerialisedQueues" /> enforces
///         them by draining the producing queue from the calling thread. The first is the reason async
///         compute exists and the second is what a device without timeline semaphores has to do, so
///         the engine ships both — and the claim that has to hold is that they produce the same frame.
///     </para>
///     <para>
///         ⚠ <b>Run against <see cref="NullDevice" /> because no real device here can.</b> Every
///         Vulkan device in reach — MoltenVK on Apple silicon, lavapipe in CI — exposes a single
///         universal queue family, so <c>HasAsyncCompute</c> is false and the schedule collapses to
///         one segment before any of this is reached. The Null backend is the only one that reports
///         three distinct queues, which makes it the only place a two-queue frame is executed at all.
///     </para>
/// </remarks>
public sealed class TimelineSubmissionTests : IDisposable {
    readonly List<RenderGraph> built = [];
    readonly List<NullDevice> devices = [];

    public void Dispose() {
        foreach (var graph in built) {
            graph.DisposePool();
        }

        foreach (var device in devices) {
            device.Dispose();
        }
    }

    /// <summary>The Null device has what a two-queue frame needs, which is why it is used here.</summary>
    [Fact]
    public void TheNullDeviceOffersTimelinesOnEveryQueue() {
        var device = Device();

        Assert.True(device.Features.HasTimelineSemaphores);
        Assert.True(device.Features.HasAsyncCompute);
        Assert.True(device.GraphicsQueue.HasTimeline);
        Assert.True(device.ComputeQueue.HasTimeline);
        Assert.True(device.TransferQueue.HasTimeline);
    }

    /// <summary>Each queue counts its own submissions, and the counters are independent.</summary>
    /// <remarks>
    ///     ⚠ <b>Independence is the assertion, not the numbering.</b> A single device-wide counter
    ///     would pass a test that only checked that values increase, and would be the bug: two queues
    ///     signalling one counter finish in an order nobody controls, and a timeline semaphore
    ///     signalled below its current value is invalid usage.
    /// </remarks>
    [Fact]
    public void EachQueueCountsItsOwnSubmissions() {
        var device = Device();

        var first = device.GraphicsQueue.Submit([Recorded(device)], []);
        var second = device.GraphicsQueue.Submit([Recorded(device)], []);
        var other = device.ComputeQueue.Submit([Recorded(device, QueueKind.Compute)], []);

        Assert.Equal(new(QueueKind.Graphics, 1), first);
        Assert.Equal(new(QueueKind.Graphics, 2), second);

        // The compute queue is at 1, not at 3: it has had one submission of its own.
        Assert.Equal(new(QueueKind.Compute, 1), other);
    }

    /// <summary>Waiting for a value nothing will ever signal is refused rather than executed.</summary>
    /// <remarks>
    ///     ⚠ <b>The failure with no other detector.</b> On hardware this is a device-side hang: no
    ///     validation message, no stack, and a frame that never completes. Refusing it on the backend
    ///     that costs no GPU is the only place it can be caught cheaply, which is the same argument
    ///     the Null backend already makes for "submitted without Finish()".
    /// </remarks>
    [Fact]
    public void APointNothingWillSignalIsRefused() {
        var device = Device();

        var failure = Assert.Throws<InvalidOperationException>(
            () => device.GraphicsQueue.Submit([Recorded(device)], [new(QueueKind.Compute, 7)])
        );

        Assert.Contains("has only issued 0", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A point handed back by a submitter is accepted by another queue.</summary>
    [Fact]
    public void APointAQueueIssuedIsAccepted() {
        var device = Device();

        var produced = device.ComputeQueue.Submit([Recorded(device, QueueKind.Compute)], []);
        device.GraphicsQueue.Submit([Recorded(device)], [produced]);

        Assert.Equal(
            2,
            device.Recorder!.Commands.Count(command => command.Kind == RecordedCommandKind.Submit)
        );

        var waited = device.Recorder.Commands.Single(c => c.Kind == RecordedCommandKind.WaitForPoint);
        Assert.Equal((long)QueueKind.Compute, waited.A);
        Assert.Equal(1, waited.B);
    }

    // ── The two paths, over one frame ───────────────────────────────────────────────────────

    /// <summary>The fast path stalls the host nowhere at all.</summary>
    /// <remarks>
    ///     <b>The whole point of the feature, stated as a count.</b> A drain per cross-queue edge is
    ///     what makes async compute cost more than it saves; the number that has to be zero is this
    ///     one.
    /// </remarks>
    [Fact]
    public void TheFastPathDrainsNoQueue() {
        var device = Device();
        var queues = new DeviceQueues(device);

        Assert.True(queues.UsesWaitValues);
        RunAsyncFrame(device, queues);

        Assert.Equal(0, Count(device, RecordedCommandKind.QueueWaitIdle));
        Assert.True(Count(device, RecordedCommandKind.WaitForPoint) > 0);
    }

    /// <summary>The fallback stalls the host once per edge, which is what it is for.</summary>
    /// <remarks>
    ///     The other arm of the comparison, and it has to be asserted rather than assumed: a test
    ///     that only said the fast path drains nothing would still pass if neither path drained
    ///     anything and the frame had no cross-queue edge to begin with.
    /// </remarks>
    [Fact]
    public void TheFallbackDrainsAQueuePerEdge() {
        var device = Device();
        RunAsyncFrame(device, new SerialisedQueues(device));

        Assert.True(Count(device, RecordedCommandKind.QueueWaitIdle) > 0);
        Assert.Equal(0, Count(device, RecordedCommandKind.WaitForPoint));
    }

    /// <summary>The frame is the same frame whichever way its waits were enforced.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The feature-flag promise, and the reason a golden image survives the switch.</b>
    ///         <c>HasTimelineSemaphores</c> decides what goes <em>between</em> the submissions;
    ///         everything inside them — every pass, every barrier, every draw and dispatch, in
    ///         declaration order — has to be untouched by it.
    ///     </para>
    ///     <para>
    ///         Compared as the whole recorded stream with only the three submission-shaped kinds
    ///         removed, rather than as a count of anything: a comparison of counts would miss a
    ///         barrier that moved from one list to another, which is precisely the mistake a
    ///         second queue makes possible.
    ///     </para>
    /// </remarks>
    [Fact]
    public void BothPathsRecordTheSameFrame() {
        var fast = Device();
        var slow = Device();

        var fastPasses = RunAsyncFrame(fast, new DeviceQueues(fast));
        var slowPasses = RunAsyncFrame(slow, new SerialisedQueues(slow));

        Assert.Equal(slowPasses, fastPasses);
        Assert.Equal(Work(slow), Work(fast));

        // And the comparison is over something, rather than over two empty sequences.
        Assert.NotEmpty(Work(fast));
    }

    /// <summary>Every wait edge the schedule declared is a point the submission waited for.</summary>
    /// <remarks>
    ///     ⚠ <b>Neither more nor fewer.</b> Too few is a half-synchronised second queue — the
    ///     corruption that reproduces once a week. Too many is a schedule that has bought a queue and
    ///     spent it on synchronisation, and is also a sign the transitive reduction stopped working.
    /// </remarks>
    [Fact]
    public void EveryDeclaredEdgeBecomesExactlyOneWait() {
        var device = Device();
        var graph = AsyncGraph(device, out _);

        graph.Execute(new DeviceQueues(device));

        var declared = graph.Schedule!.Segments.Sum(segment => segment.WaitsOn.Count);
        Assert.True(declared > 0, "The frame under test has no cross-queue edge to enforce.");
        Assert.Equal(declared, Count(device, RecordedCommandKind.WaitForPoint));
    }

    /// <summary>A submission only ever waits for work that was already submitted.</summary>
    /// <remarks>
    ///     A wait for a point later in the frame is the deadlock the shape exists to prevent, and it
    ///     would be invisible on the Null device — which completes everything immediately — if the
    ///     ordering were not asserted against the recorded stream directly.
    /// </remarks>
    [Fact]
    public void NoSubmissionWaitsForOneThatHasNotHappened() {
        var device = Device();
        var graph = AsyncGraph(device, out _);
        graph.Execute(new DeviceQueues(device));

        var issued = new Dictionary<QueueKind, long>();

        foreach (var command in device.Recorder!.Commands) {
            switch (command.Kind) {
                case RecordedCommandKind.WaitForPoint:
                    var have = issued.GetValueOrDefault((QueueKind)command.A);

                    Assert.True(
                        command.B <= have,
                        $"waited for {(QueueKind)command.A} value {command.B}, only {have} issued"
                    );

                    break;

                case RecordedCommandKind.Submit:
                    issued[(QueueKind)command.A] = command.C;
                    break;
            }
        }
    }

    /// <summary>A one-queue frame goes through the fast path without a wait or a drain.</summary>
    /// <remarks>
    ///     The single-segment case, which is what every real device in the tree actually runs. It has
    ///     no cross-queue edge, so the correct number of both kinds of synchronisation is zero — and
    ///     a <see cref="DeviceQueues" /> that waited anyway would be paying for a second queue on a
    ///     device that has one.
    /// </remarks>
    [Fact]
    public void ASingleQueueFrameSynchronisesNothing() {
        var device = Device();
        var graph = Graph(device);
        var ran = new List<string>();
        Build(graph, ran);

        device.BeginFrame();
        graph.Execute(new DeviceQueues(device));
        device.EndFrame();

        Assert.Single(graph.Schedule!.Segments);
        Assert.Equal(["prepass", "cluster", "shade"], ran);
        Assert.Equal(0, Count(device, RecordedCommandKind.QueueWaitIdle));
        Assert.Equal(0, Count(device, RecordedCommandKind.WaitForPoint));
    }

    /// <summary>A device without timeline semaphores takes the draining path and says so.</summary>
    [Fact]
    public void ADeviceWithoutTimelinesFallsBackToDraining() {
        var device = Device(timelines: false);
        var queues = new DeviceQueues(device);

        Assert.False(queues.UsesWaitValues);
        RunAsyncFrame(device, queues);

        Assert.True(Count(device, RecordedCommandKind.QueueWaitIdle) > 0);
        Assert.Equal(0, Count(device, RecordedCommandKind.WaitForPoint));
    }

    /// <summary>A second frame on the same instance enforces every edge, with its own points.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><see cref="DeviceQueues.Reset" /> is deliberately not called.</b> The second frame
    ///         is run exactly as a host that never heard of the method would run it, because a
    ///         correctness property that depends on a caller remembering something is not one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This does not prove the frame-boundary clear does anything, and it cannot.</b>
    ///         That was mutation-checked: deleting the clear leaves every test here passing, because
    ///         a consumer only ever reads producers with lower indices and those were overwritten
    ///         earlier in the same frame. What this does prove is the part that could break — that
    ///         the second frame waits as many times as the first, on values from the second frame.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ASecondFrameOnTheSameQueuesWaitsAsMuchAsTheFirst() {
        var device = Device();
        var queues = new DeviceQueues(device);

        var graph = AsyncGraph(device, out _);
        graph.Execute(queues);
        var first = Count(device, RecordedCommandKind.WaitForPoint);

        device.Recorder!.Clear();

        var second = AsyncGraph(device, out _);
        second.Execute(queues);

        Assert.True(first > 0);
        Assert.Equal(first, Count(device, RecordedCommandKind.WaitForPoint));

        // And the second frame's points are the second frame's: the counters climbed rather than
        // restarting, so a wait naming a value from frame one would be satisfied on arrival.
        var waited = device.Recorder.Commands.Where(c => c.Kind == RecordedCommandKind.WaitForPoint);
        Assert.All(waited, command => Assert.True(command.B > 1));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    static int Count(NullDevice device, RecordedCommandKind kind) =>
        device.Recorder!.Commands.Count(command => command.Kind == kind);

    /// <summary>The frame's recorded work, with the submission bookkeeping taken out.</summary>
    /// <remarks>
    ///     ⚠ <b>With <see cref="RecordedCommand.Sequence" /> flattened.</b> The synchronisation
    ///     entries take positions in the same stream, so the two paths number the calls after them
    ///     differently — and comparing the numbering rather than the calls would fail for the one
    ///     reason that is not a defect. Everything else about each command is compared.
    /// </remarks>
    static List<string> Work(NullDevice device) =>
    [
        .. device.Recorder!.Commands
            .Where(command => command.Kind is not (RecordedCommandKind.Submit
                or RecordedCommandKind.WaitForPoint
                or RecordedCommandKind.QueueWaitIdle))
            .Select(command => (command with { Sequence = 0 }).ToString())
    ];

    static ICommandList Recorded(NullDevice device, QueueKind kind = QueueKind.Graphics) {
        var list = device.BeginCommandList(kind, "work");
        list.Finish();
        return list;
    }

    List<string> RunAsyncFrame(NullDevice device, IRenderGraphQueues queues) {
        var graph = AsyncGraph(device, out var ran);

        device.BeginFrame();
        graph.Execute(queues);
        device.EndFrame();

        Assert.Equal(["prepass", "cluster", "shade"], ran);
        return ran;
    }

    RenderGraph AsyncGraph(NullDevice device, out List<string> ran) {
        var graph = Graph(device);
        graph.Scheduling = QueueScheduling.Async;
        ran = [];
        Build(graph, ran);
        graph.Compile();

        Assert.True(graph.Schedule!.IsMultiQueue, "The frame under test did not reach a second queue.");
        return graph;
    }

    /// <summary>Draw, dispatch, draw — the smallest frame a second queue changes anything in.</summary>
    static void Build(RenderGraph graph, List<string> ran) {
        var depth = graph.CreateTexture(
            new(PixelFormat.Rgba8UNorm, 64, 64, TextureUsage.ColourTarget | TextureUsage.Sampled, Name: "depth")
        );

        var clusters = graph.CreateTexture(
            new(PixelFormat.Rgba8UNorm, 64, 64, TextureUsage.Storage | TextureUsage.Sampled, Name: "clusters")
        );

        var output = graph.ImportTexture(
            TextureHandle.Null,
            TextureViewHandle.Null,
            new(PixelFormat.Rgba8UNorm, 64, 64, TextureUsage.ColourTarget | TextureUsage.Sampled, Name: "output"),
            ResourceState.Undefined,
            ResourceState.Present
        );

        graph.AddPass("prepass", pass => {
            pass.ColourAttachment(depth);
            pass.Execute(_ => ran.Add("prepass"));
        });

        graph.AddPass("cluster", pass => {
            pass.Kind = PassKind.Compute;
            pass.Reads(depth);
            pass.Writes(clusters);
            pass.Execute(context => {
                ran.Add("cluster");
                context.CommandList.Dispatch(1, 1, 1);
            });
        });

        graph.AddPass("shade", pass => {
            pass.Reads(clusters);
            pass.ColourAttachment(output);
            pass.Execute(_ => ran.Add("shade"));
        });
    }

    NullDevice Device(bool timelines = true) {
        var device = new NullDevice(new() { Record = true, Features = Features(timelines) });
        devices.Add(device);
        return device;
    }

    RenderGraph Graph(NullDevice device) {
        var graph = new RenderGraph(device);
        built.Add(graph);
        return graph;
    }

    static GraphicsDeviceFeatures Features(bool timelines) =>
        NullDevice.Everything with { HasTimelineSemaphores = timelines };
}
