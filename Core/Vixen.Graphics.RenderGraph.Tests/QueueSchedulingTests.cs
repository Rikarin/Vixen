// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics.Null;
using Xunit;

namespace Vixen.Graphics.RenderGraph.Tests;

/// <summary>Collects the lists the graph asks for, so a test can read every segment afterwards.</summary>
/// <remarks>
///     The graph submits into this rather than the device's real queues, for the same reason
///     <see cref="TrackingCommandList" /> exists at all: the property under test is which barrier went
///     onto which queue's list, and a backend records a barrier as a pair of counts.
/// </remarks>
sealed class RecordingQueues : IRenderGraphQueues {
    public List<(RenderGraphSegment Segment, TrackingCommandList List)> Recorded { get; } = [];

    /// <summary>Every segment, in submission order, as "queue: first-last".</summary>
    public IEnumerable<string> Order =>
        Recorded.Select(entry => $"{entry.Segment.Queue}: {entry.Segment.FirstPass}-{entry.Segment.LastPass}");

    public ICommandList Begin(RenderGraphSegment segment) => new TrackingCommandList(segment.Queue);

    public void Submit(RenderGraphSegment segment, ICommandList list) {
        Assert.False(list.IsRecorded, "The graph finished a list the queues had not taken back yet.");
        list.Finish();
        Recorded.Add((segment, (TrackingCommandList)list));
    }

    public IEnumerable<ObservedBarrier> Barriers => Recorded.SelectMany(entry => entry.List.Barriers);

    public IEnumerable<string> Passes => Recorded.SelectMany(entry => entry.List.Order);
}

/// <summary>Which queue a pass ends up on, and what has to happen at the boundary.</summary>
public sealed class QueueSchedulingTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });
    readonly List<RenderGraph> built = [];

    public void Dispose() {
        foreach (var graph in built) {
            graph.DisposePool();
        }

        device.Dispose();
    }

    static TextureDescription Target(string name, int size = 64) =>
        new(PixelFormat.Rgba8UNorm, size, size, TextureUsage.ColourTarget | TextureUsage.Sampled, Name: name);

    static TextureDescription Storage(string name, int size = 64) =>
        new(PixelFormat.Rgba8UNorm, size, size, TextureUsage.Storage | TextureUsage.Sampled, Name: name);

    /// <summary>Builds the same frame every time: draw, dispatch, draw, into an imported target.</summary>
    /// <remarks>
    ///     The shape every claim here is made about. One compute pass between two graphics ones is the
    ///     smallest frame in which a second queue changes anything at all — and it is also the shape
    ///     of a real one, since a GPU cull or a light-clustering dispatch sits exactly there.
    /// </remarks>
    static void Build(RenderGraph graph, TextureHandle imported, List<string> ran) {
        var depth = graph.CreateTexture(Target("depth"));
        var clusters = graph.CreateTexture(Storage("clusters"));

        var output = graph.ImportTexture(
            imported,
            TextureViewHandle.Null,
            Target("output"),
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
            pass.Execute(_ => ran.Add("cluster"));
        });

        graph.AddPass("shade", pass => {
            pass.Reads(clusters);
            pass.ColourAttachment(output);
            pass.Execute(_ => ran.Add("shade"));
        });
    }

    // ── The assignment ──────────────────────────────────────────────────────────────────────

    /// <summary>Off by default: a graph nobody configured puts the whole frame on one queue.</summary>
    [Fact]
    public void SchedulingIsSingleQueueUnlessAskedFor() {
        var graph = Graph();
        var ran = new List<string>();
        Build(graph, TextureHandle.Null, ran);

        graph.Compile();

        Assert.Equal(QueueScheduling.Single, graph.Scheduling);
        Assert.False(graph.Schedule!.IsMultiQueue);
        Assert.Single(graph.Schedule.Segments);
        Assert.Equal(QueueKind.Graphics, graph.Schedule.QueueOf(1));
    }

    /// <summary>A compute pass reaches the compute queue, and cuts the frame into three segments.</summary>
    [Fact]
    public void AComputePassIsHoistedOntoTheComputeQueue() {
        var graph = Graph();
        graph.Scheduling = QueueScheduling.Async;
        Build(graph, TextureHandle.Null, []);

        graph.Compile();

        var schedule = graph.Schedule!;
        Assert.True(schedule.IsMultiQueue);
        Assert.Equal(QueueKind.Graphics, schedule.QueueOf(0));
        Assert.Equal(QueueKind.Compute, schedule.QueueOf(1));
        Assert.Equal(QueueKind.Graphics, schedule.QueueOf(2));
        Assert.Equal(3, schedule.Segments.Count);
    }

    /// <summary>
    ///     A device with one queue schedules the same frame onto one queue, whatever the passes say.
    /// </summary>
    /// <remarks>
    ///     The claim the whole feature rests on. GL has no second queue at all and most Vulkan devices
    ///     this engine runs on — anything through MoltenVK — have one universal family, so a pass
    ///     marked Compute has to be a hint that costs nothing when it cannot be honoured.
    /// </remarks>
    [Fact]
    public void ADeviceWithOneQueueIgnoresTheRequest() {
        using var single = new NullDevice(new() { Features = GraphicsDeviceFeatures.Minimum });
        var graph = new RenderGraph(single);
        built.Add(graph);
        graph.Scheduling = QueueScheduling.Async;
        Build(graph, TextureHandle.Null, []);

        graph.Compile();

        Assert.False(single.Features.HasAsyncCompute);
        Assert.False(graph.Schedule!.IsMultiQueue);
        Assert.Equal(QueueKind.Graphics, graph.Schedule.QueueOf(1));
        Assert.Equal(0, graph.Schedule.OwnershipTransferCount);
    }

    /// <summary>A compute-kind pass that declares attachments stays where it can draw.</summary>
    [Fact]
    public void APassWithAttachmentsIsNotHoistedHoweverItIsMarked() {
        var graph = Graph();
        graph.Scheduling = QueueScheduling.Async;
        var output = graph.CreateTexture(Target("output"));

        graph.AddPass("confused", pass => {
            pass.Kind = PassKind.Compute;
            pass.ColourAttachment(output);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        graph.Compile();

        Assert.Equal(QueueKind.Graphics, graph.Schedule!.QueueOf(0));
        Assert.False(graph.Schedule.IsMultiQueue);
    }

    /// <summary>A transfer pass stays on graphics until its body has been audited against a DMA queue.</summary>
    [Fact]
    public void ATransferPassIsNotHoisted() {
        var graph = Graph();
        graph.Scheduling = QueueScheduling.Async;
        var buffer = graph.CreateBuffer(new(1024, BufferUsage.Storage, Name: "staged"));

        graph.AddPass("upload", pass => {
            pass.Kind = PassKind.Transfer;
            pass.Writes(buffer, ResourceState.CopyDestination);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        graph.Compile();

        Assert.Equal(QueueKind.Graphics, graph.Schedule!.QueueOf(0));
    }

    /// <summary>A culled pass takes its queue with it — scheduling runs after culling, not before.</summary>
    [Fact]
    public void ACulledComputePassCreatesNoSegment() {
        var graph = Graph();
        graph.Scheduling = QueueScheduling.Async;
        var wasted = graph.CreateTexture(Storage("wasted"));
        var kept = graph.CreateTexture(Target("kept"));

        graph.AddPass("nobody wants this", pass => {
            pass.Kind = PassKind.Compute;
            pass.Writes(wasted);
            pass.Execute(_ => { });
        });

        graph.AddPass("kept", pass => {
            pass.ColourAttachment(kept);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        graph.Compile();

        Assert.Equal(1, graph.SurvivingPassCount);
        Assert.False(graph.Schedule!.IsMultiQueue);
        Assert.Single(graph.Schedule.Segments);
    }

    // ── The handover ────────────────────────────────────────────────────────────────────────

    /// <summary>Every handover is recorded twice: released on one queue's list, acquired on the other's.</summary>
    /// <remarks>
    ///     The property that is worth a test more than any other here. One half of a transfer is not a
    ///     validation error on any API — the acquiring queue simply reads memory nobody gave it — so
    ///     the only thing that catches a missing release is counting them.
    /// </remarks>
    [Fact]
    public void EveryOwnershipTransferIsRecordedOnBothQueues() {
        var graph = Graph();
        graph.Scheduling = QueueScheduling.Async;
        Build(graph, TextureHandle.Null, []);

        var queues = new RecordingQueues();
        graph.Execute(queues);

        var transfers = queues.Barriers.Where(barrier => barrier.TransfersOwnership).ToList();
        Assert.NotEmpty(transfers);

        foreach (var group in transfers.GroupBy(barrier => (barrier.Texture, barrier.Buffer, barrier.Before, barrier.After, barrier.SourceQueue, barrier.DestinationQueue))) {
            var sides = group.Select(barrier => barrier.RecordedOn).ToHashSet();

            Assert.True(
                sides.SetEquals([group.Key.SourceQueue, group.Key.DestinationQueue]),
                $"A transfer from {group.Key.SourceQueue} to {group.Key.DestinationQueue} was recorded "
                + $"only on {string.Join(", ", sides)}."
            );
        }
    }

    /// <summary>A transfer is only ever recorded on a list at one of its two ends.</summary>
    [Fact]
    public void NoTransferIsRecordedOnAThirdQueue() {
        var graph = Graph();
        graph.Scheduling = QueueScheduling.Async;
        Build(graph, TextureHandle.Null, []);

        var queues = new RecordingQueues();
        graph.Execute(queues);

        foreach (var barrier in queues.Barriers.Where(barrier => barrier.TransfersOwnership)) {
            Assert.True(
                barrier.RecordedOn == barrier.SourceQueue || barrier.RecordedOn == barrier.DestinationQueue,
                $"A {barrier.SourceQueue}→{barrier.DestinationQueue} transfer was recorded on a "
                + $"{barrier.RecordedOn} list."
            );
        }
    }

    /// <summary>A single-queue frame carries no ownership barriers at all.</summary>
    /// <remarks>
    ///     The other half of "the same frame either way": the barrier stream a device with one queue
    ///     sees is the stream it saw before schedules existed, not the same stream with the queue
    ///     fields set to the same value twice.
    /// </remarks>
    [Fact]
    public void ASingleQueueFrameNamesNoQueues() {
        var graph = Graph();
        Build(graph, TextureHandle.Null, []);

        var list = new TrackingCommandList();
        graph.Execute(list);

        Assert.All(list.Barriers, barrier => Assert.False(barrier.TransfersOwnership));
        Assert.Equal(0, graph.Schedule!.OwnershipTransferCount);
    }

    /// <summary>An import ends the frame owned by graphics, so next frame's first barrier is true.</summary>
    [Fact]
    public void ImportsAreHandedBackToTheGraphicsQueue() {
        var graph = Graph();
        graph.Scheduling = QueueScheduling.Async;
        var imported = graph.ImportTexture(
            TextureHandle.Null,
            TextureViewHandle.Null,
            Storage("history"),
            ResourceState.ShaderRead,
            ResourceState.ShaderRead
        );

        graph.AddPass("simulate", pass => {
            pass.Kind = PassKind.Compute;
            pass.Writes(imported);
            pass.Execute(_ => { });
        });

        var queues = new RecordingQueues();
        graph.Execute(queues);

        // The last segment is a graphics one that exists only to take the resource back.
        var last = queues.Recorded[^1];
        Assert.Equal(QueueKind.Graphics, last.Segment.Queue);
        Assert.True(last.Segment.IsEmpty);

        var acquire = last.List.Barriers.Single(barrier => barrier.TransfersOwnership);
        Assert.Equal(QueueKind.Compute, acquire.SourceQueue);
        Assert.Equal(QueueKind.Graphics, acquire.DestinationQueue);
    }

    /// <summary>
    ///     A frame whose first pass is a compute one still releases its imports from graphics.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The shape where a handover can be recorded entirely on the acquiring queue.</b> An
    ///     import arrives owned by graphics, so the very first compute pass to touch one has to
    ///     acquire it — and if there is no graphics list in front, the release lands at the end of the
    ///     same compute list that acquires. That is legal to record, is checked by nothing, and means
    ///     the contents were never handed over at all. The schedule opens with an empty graphics
    ///     segment for it.
    /// </remarks>
    [Fact]
    public void AFrameThatOpensWithComputeStillReleasesItsImportsFromGraphics() {
        var graph = Graph();
        graph.Scheduling = QueueScheduling.Async;

        var imported = graph.ImportTexture(
            TextureHandle.Null,
            TextureViewHandle.Null,
            Storage("history"),
            ResourceState.ShaderRead,
            ResourceState.ShaderRead
        );

        graph.AddPass("simulate", pass => {
            pass.Kind = PassKind.Compute;
            pass.Reads(imported);
            pass.Writes(imported);
            pass.Execute(_ => { });
        });

        var queues = new RecordingQueues();
        graph.Execute(queues);

        var opening = queues.Recorded[0];
        Assert.Equal(QueueKind.Graphics, opening.Segment.Queue);
        Assert.True(opening.Segment.IsEmpty);

        var release = opening.List.Barriers.Single(barrier => barrier.TransfersOwnership);
        Assert.Equal(QueueKind.Graphics, release.SourceQueue);
        Assert.Equal(QueueKind.Compute, release.DestinationQueue);

        // And the compute segment waits for it rather than starting alongside.
        Assert.Equal([opening.Segment], queues.Recorded[1].Segment.WaitsOn);
    }

    /// <summary>A frame with no imports needs no empty segments at either end.</summary>
    [Fact]
    public void AFrameWithNoImportsGetsNoEmptySegments() {
        var graph = Graph();
        graph.Scheduling = QueueScheduling.Async;
        var clusters = graph.CreateTexture(Storage("clusters"));

        graph.AddPass("cluster", pass => {
            pass.Kind = PassKind.Compute;
            pass.Writes(clusters);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        graph.Compile();

        Assert.Single(graph.Schedule!.Segments);
        Assert.False(graph.Schedule.Segments[0].IsEmpty);
        Assert.Equal(QueueKind.Compute, graph.Schedule.Segments[0].Queue);
    }

    // ── The ordering ────────────────────────────────────────────────────────────────────────

    /// <summary>A segment that consumes another's output says so, and one that does not stays free.</summary>
    [Fact]
    public void CrossQueueDependenciesBecomeWaits() {
        var graph = Graph();
        graph.Scheduling = QueueScheduling.Async;
        Build(graph, TextureHandle.Null, []);

        graph.Compile();

        var schedule = graph.Schedule!;
        var compute = schedule.Segments[1];
        var shade = schedule.Segments[2];

        Assert.Equal([schedule.Segments[0]], compute.WaitsOn);
        Assert.Equal([compute], shade.WaitsOn);
    }

    /// <summary>A wait another wait already implies is not stated twice.</summary>
    [Fact]
    public void WaitsAreTransitivelyReduced() {
        var graph = Graph();
        graph.Scheduling = QueueScheduling.Async;

        var first = graph.CreateTexture(Storage("first"));
        var second = graph.CreateTexture(Storage("second"));
        var output = graph.CreateTexture(Target("output"));

        graph.AddPass("seed", pass => {
            pass.Writes(first, ResourceState.ColourTarget);
            pass.Execute(_ => { });
        });

        graph.AddPass("compute a", pass => {
            pass.Kind = PassKind.Compute;
            pass.Reads(first);
            pass.Writes(second);
            pass.Execute(_ => { });
        });

        graph.AddPass("draw", pass => {
            // Reads both, so the naive answer is two waits: one on the compute segment and one on
            // the graphics segment before it. The second is already implied.
            pass.Reads(first);
            pass.Reads(second);
            pass.ColourAttachment(output);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        graph.Compile();

        var schedule = graph.Schedule!;
        Assert.Equal([schedule.Segments[1]], schedule.Segments[2].WaitsOn);
    }

    /// <summary>Two segments on the same queue are ordered by submission, not by a wait.</summary>
    /// <remarks>
    ///     Written around a compute segment that nothing downstream reads, so that the graphics
    ///     segment after it depends only on the graphics segment <em>before</em> it. That is the one
    ///     shape where a spurious same-queue edge survives the transitive reduction and can be seen —
    ///     in the ordinary shape the reduction removes it for a different reason and the test passes
    ///     whether or not the rule is there.
    /// </remarks>
    [Fact]
    public void SameQueueSegmentsDoNotWaitOnEachOther() {
        var graph = Graph();
        graph.Scheduling = QueueScheduling.Async;

        var shared = graph.CreateTexture(Target("shared"));
        var aside = graph.CreateTexture(Storage("aside"));
        var output = graph.CreateTexture(Target("output"));

        graph.AddPass("produce", pass => {
            pass.Writes(shared, ResourceState.ColourTarget);
            pass.Execute(_ => { });
        });

        graph.AddPass("unrelated compute", pass => {
            pass.Kind = PassKind.Compute;
            pass.Writes(aside);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        graph.AddPass("consume", pass => {
            pass.Reads(shared);
            pass.ColourAttachment(output);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        graph.Compile();

        var schedule = graph.Schedule!;
        Assert.Equal(3, schedule.Segments.Count);
        Assert.Empty(schedule.Segments[2].WaitsOn);

        foreach (var segment in schedule.Segments) {
            Assert.All(segment.WaitsOn, producer => Assert.NotEqual(segment.Queue, producer.Queue));
        }
    }

    // ── The frame is the same frame ─────────────────────────────────────────────────────────

    /// <summary>
    ///     Scheduling changes which list a pass is recorded into, and nothing else about the frame.
    /// </summary>
    /// <remarks>
    ///     <b>The claim the feature has to keep or be deleted.</b> Passes run in declaration order
    ///     either way, so the two frames do the same work in the same order — and the only difference
    ///     in what is recorded is the ownership half of the barriers, which is a no-op on a device
    ///     whose queues are the same family. Compared as a trace rather than as counts, because a
    ///     count would pass for a frame that ran the passes backwards.
    /// </remarks>
    [Fact]
    public void TheSameFrameScheduledBothWaysRunsTheSamePassesInTheSameOrder() {
        var single = new List<string>();
        var async = new List<string>();

        {
            var graph = Graph();
            Build(graph, TextureHandle.Null, single);
            graph.Execute(new TrackingCommandList());
        }

        {
            var graph = Graph();
            graph.Scheduling = QueueScheduling.Async;
            Build(graph, TextureHandle.Null, async);
            graph.Execute(new RecordingQueues());
        }

        Assert.Equal(["prepass", "cluster", "shade"], single);
        Assert.Equal(single, async);
    }

    /// <summary>Both ways see the same render passes begun, with the same attachments.</summary>
    [Fact]
    public void TheSameFrameScheduledBothWaysBeginsTheSameRenderPasses() {
        List<string> Trace(QueueScheduling scheduling) {
            var graph = Graph();
            graph.Scheduling = scheduling;
            Build(graph, TextureHandle.Null, []);

            if (scheduling == QueueScheduling.Single) {
                var list = new TrackingCommandList();
                graph.Execute(list);
                return [.. list.Passes.Select(pass => pass.Name)];
            }

            var queues = new RecordingQueues();
            graph.Execute(queues);
            return [.. queues.Recorded.SelectMany(entry => entry.List.Passes).Select(pass => pass.Name)];
        }

        Assert.Equal(Trace(QueueScheduling.Single), Trace(QueueScheduling.Async));
    }

    /// <summary>
    ///     The state transitions are the same both ways; only the queue fields differ.
    /// </summary>
    [Fact]
    public void TheSameFrameScheduledBothWaysMakesTheSameTransitions() {
        static List<(ResourceState Before, ResourceState After)> Transitions(IEnumerable<ObservedBarrier> barriers) =>
            [.. barriers.Select(barrier => (barrier.Before, barrier.After))];

        List<(ResourceState, ResourceState)> single;
        List<(ResourceState, ResourceState)> async;

        {
            var graph = Graph();
            Build(graph, TextureHandle.Null, []);
            var list = new TrackingCommandList();
            graph.Execute(list);
            single = Transitions(list.Barriers);
        }

        {
            var graph = Graph();
            graph.Scheduling = QueueScheduling.Async;
            Build(graph, TextureHandle.Null, []);
            var queues = new RecordingQueues();
            graph.Execute(queues);

            // The releases are the extra half of each handover, and are the same transition again.
            async = Transitions(
                queues.Barriers.Where(barrier => !barrier.TransfersOwnership || barrier.RecordedOn == barrier.DestinationQueue)
            );
        }

        Assert.Equal(single, async);
    }

    /// <summary>The dump clusters the passes by segment and draws the waits between them.</summary>
    /// <remarks>
    ///     "Did anything actually overlap" is the second question a frame debugger gets opened for,
    ///     and it is one the numbers cannot answer: two segments on two queues with an edge between
    ///     them run one after the other, which is obvious in a drawing and invisible in a list.
    /// </remarks>
    [Fact]
    public void TheGraphvizDumpShowsTheSegmentsAndTheirWaits() {
        var graph = Graph();
        graph.Scheduling = QueueScheduling.Async;
        Build(graph, TextureHandle.Null, []);

        var dot = graph.ToGraphviz();

        Assert.Contains("subgraph cluster_s0", dot, StringComparison.Ordinal);
        Assert.Contains("label=\"segment 1 (Compute)\"", dot, StringComparison.Ordinal);
        Assert.Contains("label=\"waits\"", dot, StringComparison.Ordinal);
    }

    /// <summary>A single-queue dump is the dump it always was, with no segment boxes in it.</summary>
    [Fact]
    public void ASingleQueueDumpMentionsNoSegments() {
        var graph = Graph();
        Build(graph, TextureHandle.Null, []);

        var dot = graph.ToGraphviz();

        Assert.DoesNotContain("subgraph", dot, StringComparison.Ordinal);
        Assert.DoesNotContain("waits", dot, StringComparison.Ordinal);
    }

    // ── What it refuses ─────────────────────────────────────────────────────────────────────

    /// <summary>One command list cannot express a frame on two queues, and says so.</summary>
    [Fact]
    public void AMultiQueueScheduleRefusesASingleCommandList() {
        var graph = Graph();
        graph.Scheduling = QueueScheduling.Async;
        Build(graph, TextureHandle.Null, []);

        var failure = Assert.Throws<RenderGraphException>(() => graph.Execute(new TrackingCommandList()));
        Assert.Contains("IRenderGraphQueues", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A list opened for the wrong queue is refused before anything is recorded into it.</summary>
    [Fact]
    public void AListForTheWrongQueueIsRefused() {
        var graph = Graph();
        graph.Scheduling = QueueScheduling.Async;
        Build(graph, TextureHandle.Null, []);

        var failure = Assert.Throws<RenderGraphException>(() => graph.Execute(new WrongQueues()));
        Assert.Contains("command list", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Transients are not aliased once two queues could be running at once.</summary>
    /// <remarks>
    ///     "Their lifetimes do not overlap" is a statement about pass order, and pass order stops
    ///     being a statement about time the moment a second queue exists. Two transients sharing
    ///     memory across a queue boundary needs genuine concurrency to corrupt anything, which is to
    ///     say it corrupts things on the user's card and never in CI.
    /// </remarks>
    [Fact]
    public void AMultiQueueFrameDoesNotAliasTransients() {
        // Three storage textures of one description in a chain, so the third's lifetime starts after
        // the first's ends — the shape aliasing exists for. Single-queue gives it two physical
        // textures; async has to give it three.
        int Physical(QueueScheduling scheduling) {
            var graph = Graph();
            graph.Scheduling = scheduling;

            var first = graph.CreateTexture(Storage("first"));
            var second = graph.CreateTexture(Storage("second"));
            var third = graph.CreateTexture(Storage("third"));

            graph.AddPass("seed", pass => {
                pass.Writes(first);
                pass.Execute(_ => { });
            });

            graph.AddPass("first to second", pass => {
                pass.Kind = PassKind.Compute;
                pass.Reads(first);
                pass.Writes(second);
                pass.Execute(_ => { });
            });

            graph.AddPass("second to third", pass => {
                pass.Reads(second);
                pass.Writes(third);
                pass.SideEffect();
                pass.Execute(_ => { });
            });

            if (scheduling == QueueScheduling.Single) {
                graph.Execute(new TrackingCommandList());
            } else {
                graph.Execute(new RecordingQueues());
            }

            return graph.Pool.Count;
        }

        Assert.Equal(2, Physical(QueueScheduling.Single));
        Assert.Equal(3, Physical(QueueScheduling.Async));
    }

    // ── Against a device ────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The whole thing runs against a real device's three queues, and the backend accepts every
    ///     barrier it is given.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The half the fakes cannot check.</b> A backend refuses an ownership transfer recorded
    ///     on a list at neither end of it, and that refusal is the only thing standing between a
    ///     mis-paired release and a resource whose contents are whatever the memory held. The Null
    ///     backend has the same check the Vulkan one does, for exactly this: it costs no GPU and it
    ///     runs on every machine.
    /// </remarks>
    [Fact]
    public void ASerialisedFrameRunsThroughTheDevicesOwnQueues() {
        var graph = Graph();
        graph.Scheduling = QueueScheduling.Async;
        var ran = new List<string>();
        Build(graph, TextureHandle.Null, ran);

        device.BeginFrame();
        graph.Execute(new SerialisedQueues(device));
        device.EndFrame();

        Assert.Equal(["prepass", "cluster", "shade"], ran);

        // Two render passes begun — the prepass and the shade — and the dispatch pass between them
        // went onto a list of its own without one.
        Assert.Equal(2, device.Recorder!.CountOf(RecordedCommandKind.BeginRenderPass));
        Assert.True(device.Recorder.CountOf(RecordedCommandKind.Barrier) > 0);
    }

    /// <summary>A single-queue graph is happy to be executed through the same seam.</summary>
    [Fact]
    public void SerialisedQueuesAlsoRunsASingleQueueFrame() {
        var graph = Graph();
        var ran = new List<string>();
        Build(graph, TextureHandle.Null, ran);

        device.BeginFrame();
        graph.Execute(new SerialisedQueues(device));
        device.EndFrame();

        Assert.Equal(["prepass", "cluster", "shade"], ran);
        Assert.Single(graph.Schedule!.Segments);
    }

    RenderGraph Graph() {
        var graph = new RenderGraph(device);
        built.Add(graph);
        return graph;
    }

    /// <summary>Hands out graphics lists whatever it was asked for.</summary>
    sealed class WrongQueues : IRenderGraphQueues {
        public ICommandList Begin(RenderGraphSegment segment) => new TrackingCommandList();

        public void Submit(RenderGraphSegment segment, ICommandList list) => list.Finish();
    }
}
