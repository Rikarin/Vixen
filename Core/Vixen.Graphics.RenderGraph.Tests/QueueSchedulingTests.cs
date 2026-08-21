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

    /// <summary>A transfer pass stays on graphics, and the reason is no longer that nobody has looked.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The audit finished and the switch stays off deliberately</b>, which is a different
    ///         thing from "not done yet". Six passes in this tree declare
    ///         <see cref="PassKind.Transfer" /> over a body that really is a copy, and the blocker the
    ///         audit named — the graph's own acquire in front of a transfer pass naming a stage no DMA
    ///         family has — was solved by the queue clamp in <c>VulkanBarriers.SupportedStages</c>.
    ///         What stops it is what the clamp does not touch. See
    ///         <see cref="ACopyDoesNotCostTheFrameItsTransientAliasing" /> and
    ///         <c>docs/guide/rendering/async-compute.md</c>.
    ///     </para>
    /// </remarks>
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

    /// <summary>
    ///     ⚠ <b>What hoisting a copy would cost, and why the switch stays off: the frame's transient
    ///     aliasing, all of it, for a copy of four kilobytes.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="RenderGraph.Realise" /> aliases transients only while the schedule is
    ///         <em>not</em> multi-queue — see <see cref="AMultiQueueFrameDoesNotAliasTransients" />
    ///         for why, which is a good reason and is not in question here. The consequence is what
    ///         is: hoisting one copy makes the whole frame multi-queue, and a frame is not multi-queue
    ///         by degrees. Every large target in it stops sharing memory, including the ones the copy
    ///         never touches.
    ///     </para>
    ///     <para>
    ///         Measured on the shape below — a six-step post-FX chain at 1920×1080 whose links do not
    ///         coexist, plus one <see cref="PassKind.Transfer" /> pass of exactly
    ///         <c>BufferUploadRenderer</c>'s declaration. Single-queue, the six targets alias down to
    ///         two physical textures. With the copy hoisted onto a transfer queue they cost six —
    ///         four extra 1920×1080 RGBA8 targets, about 31 MiB, held for the life of the pool.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the row it is measured against is not "async is expensive", it is "async was
    ///         free here".</b> A frame with no hoistable compute pass compiles identically under
    ///         <see cref="QueueScheduling.Async" /> and <see cref="QueueScheduling.Single" />: one
    ///         segment, two physical textures. Hoisting copies is what would turn every such frame
    ///         into a multi-queue one, on the strength of a buffer upload — and buy nothing back,
    ///         since no device this engine runs on reports a transfer family of its own.
    ///     </para>
    ///     <para>
    ///         This test pins the property the refusal protects, so it fails the moment somebody
    ///         throws the switch rather than after somebody profiles a memory graph.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ACopyDoesNotCostTheFrameItsTransientAliasing() {
        int Physical(QueueScheduling scheduling) {
            var graph = Graph();
            graph.Scheduling = scheduling;

            var uniforms = graph.CreateBuffer(
                new(4096, BufferUsage.Uniform | BufferUsage.CopyDestination, Name: "uniforms")
            );

            graph.AddPass("upload", pass => {
                pass.Kind = PassKind.Transfer;
                pass.Writes(uniforms, ResourceState.CopyDestination);
                pass.Execute(_ => { });
            });

            var previous = graph.CreateTexture(Storage("step 0", 256));

            graph.AddPass("step 0", pass => {
                pass.Reads(uniforms, ResourceState.UniformRead);
                pass.Writes(previous);
                pass.Execute(_ => { });
            });

            for (var index = 1; index < 6; index++) {
                var next = graph.CreateTexture(Storage($"step {index}", 256));
                var source = previous;

                graph.AddPass($"step {index}", pass => {
                    pass.Reads(source);
                    pass.Writes(next);
                    pass.SideEffect();
                    pass.Execute(_ => { });
                });

                previous = next;
            }

            graph.Execute(new TrackingCommandList());

            // Single-queue, so one list is enough — which is itself the assertion. A hoisted copy
            // would make this throw before it reached the count.
            Assert.False(graph.Schedule!.IsMultiQueue);
            Assert.Equal(QueueKind.Graphics, graph.Schedule.QueueOf(0));

            return graph.Pool.Count;
        }

        // Two textures for the six links of the chain, plus the uniform buffer.
        Assert.Equal(3, Physical(QueueScheduling.Single));
        Assert.Equal(3, Physical(QueueScheduling.Async));
    }

    /// <summary>
    ///     ⚠ A compute pass that declares no write is not hoisted, however honest the rest of it is.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The shape of four of this tree's nine compute passes before the audit, and the reason
    ///         the rule is in the scheduler rather than in a guide. A wait edge comes from a declared
    ///         write and from nothing else, so a pass declaring none is a pass no later segment can be
    ///         made to wait for — and hoisting it produces a compute segment with an edge going in and
    ///         none coming out. What it really wrote — a HiZ pyramid, a shadow page table, a
    ///         draw-argument buffer — is then read by the graphics queue while the dispatch is still
    ///         running, which on one queue family is invisible and on a discrete card is corruption.
    ///     </para>
    ///     <para>
    ///         Read carefully, this is a claim about <em>declarations</em> and not about honesty: a
    ///         pass may declare one write and quietly touch five other things, and the graph cannot
    ///         tell. It is the half the graph can check, and it makes under-declaration fail towards
    ///         the frame the engine already draws.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AComputePassThatDeclaresNoWriteIsNotHoisted() {
        var graph = Graph();
        graph.Scheduling = QueueScheduling.Async;
        var depth = graph.CreateTexture(Target("depth"));

        graph.AddPass("prepass", pass => {
            pass.ColourAttachment(depth);
            pass.Execute(_ => { });
        });

        // A reduction into something the graph cannot see: it reads a graph resource and honestly
        // says its product is not one. HiZRenderer, exactly.
        graph.AddPass("reduce", pass => {
            pass.Kind = PassKind.Compute;
            pass.Reads(depth);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        graph.Compile();

        Assert.Equal(QueueKind.Graphics, graph.Schedule!.QueueOf(1));
        Assert.False(graph.Schedule.IsMultiQueue);
        Assert.Single(graph.Schedule.Segments);
    }

    /// <summary>
    ///     And the same pass with its production declared is hoisted, so the rule above is the write
    ///     and not the side effect, the read, or the pass's position.
    /// </summary>
    /// <remarks>
    ///     ⚠ The other arm, because a scheduler that hoisted nothing would pass the test above too.
    /// </remarks>
    [Fact]
    public void TheSamePassWithItsProductionDeclaredIsHoisted() {
        var graph = Graph();
        graph.Scheduling = QueueScheduling.Async;
        var depth = graph.CreateTexture(Target("depth"));
        var pyramid = graph.CreateTexture(Storage("pyramid"));

        graph.AddPass("prepass", pass => {
            pass.ColourAttachment(depth);
            pass.Execute(_ => { });
        });

        graph.AddPass("reduce", pass => {
            pass.Kind = PassKind.Compute;
            pass.Reads(depth);
            pass.Writes(pyramid);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        graph.Compile();

        Assert.Equal(QueueKind.Compute, graph.Schedule!.QueueOf(1));
        Assert.True(graph.Schedule.IsMultiQueue);
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

    // ── The sharing mode ────────────────────────────────────────────────────────────────────

    /// <summary>Builds a frame in which two queues want to read one texture and neither writes it.</summary>
    /// <remarks>
    ///     Draw depth, then read it from a compute pass and from a later graphics pass. That is a
    ///     light-clustering frame, and under exclusive sharing it is also the frame in which the two
    ///     readers take turns over a texture neither of them changes.
    /// </remarks>
    /// <param name="bothRead">
    ///     Whether the later graphics pass reads the depth too. False leaves one reader on each
    ///     queue, which is the arrangement a handover exists for.
    /// </param>
    RenderGraph TwoReaders(bool bothRead) {
        var graph = Graph();
        graph.Scheduling = QueueScheduling.Async;
        var depth = graph.CreateTexture(Target("depth"));

        // A buffer rather than a texture, so a recorded barrier says which resource it named without
        // a handle-to-name map the graph does not expose.
        var clusters = graph.CreateBuffer(new(4096, BufferUsage.Storage, Name: "clusters"));
        var output = graph.CreateTexture(Target("output"));

        graph.AddPass("prepass", pass => {
            pass.ColourAttachment(depth);
            pass.Execute(_ => { });
        });

        graph.AddPass("cluster", pass => {
            pass.Kind = PassKind.Compute;
            pass.Reads(depth);
            pass.Writes(clusters);
            pass.Execute(_ => { });
        });

        graph.AddPass("shade", pass => {
            if (bothRead) {
                pass.Reads(depth);
            }

            pass.Reads(clusters);
            pass.ColourAttachment(output);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        return graph;
    }

    /// <summary>
    ///     ⚠ Two queues that both only read one resource do not hand it to each other.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The gap this closes. Under exclusive sharing ownership follows <em>use</em>, so the
    ///         second reader has to acquire from the first — and an acquire cannot begin until the
    ///         release has finished, which is two queues taking turns over a texture neither of them
    ///         is changing. Concurrent sharing is the only thing that removes it: it is not an
    ///         optimisation of the barrier, it is the absence of an owner.
    ///     </para>
    ///     <para>
    ///         Asserted as "no handover for this resource" rather than as a count over the frame,
    ///         because the frame still has a real one — the clusters buffer the compute pass writes
    ///         and the shade pass reads — and a count would pass for the wrong reason if that
    ///         disappeared.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TwoQueuesReadingOneResourceDoNotHandItOver() {
        var graph = TwoReaders(bothRead: true);
        var queues = new RecordingQueues();
        graph.Execute(queues);

        // The depth is the only texture that crosses; the clusters buffer is the frame's one real
        // handover and is asserted separately below.
        Assert.DoesNotContain(queues.Barriers, barrier => barrier.TransfersOwnership && barrier.Texture.IsValid);
        Assert.Equal(1, graph.Schedule!.OwnershipTransferCount);
    }

    /// <summary>
    ///     ⚠ And with only one reader on each queue the very same texture <em>is</em> handed over.
    /// </summary>
    /// <remarks>
    ///     The mutation guard for the test above, which a graph that had stopped transferring
    ///     anything at all would also pass. One declaration differs between the two frames — whether
    ///     the shade pass reads the depth — and that one declaration is the whole rule.
    /// </remarks>
    [Fact]
    public void OneReaderPerQueueStillHandsTheTextureOver() {
        var graph = TwoReaders(bothRead: false);
        var queues = new RecordingQueues();
        graph.Execute(queues);

        Assert.Contains(queues.Barriers, barrier => barrier.TransfersOwnership && barrier.Texture.IsValid);
        Assert.Equal(2, graph.Schedule!.OwnershipTransferCount);
    }

    /// <summary>
    ///     And the read-only sharing does not spread: a resource one queue writes is still owned.
    /// </summary>
    /// <remarks>
    ///     ⚠ The other arm, and the one that keeps the trade honest. Concurrent sharing costs
    ///     bandwidth on hardware that answers it by not compressing, so a rule that quietly applied
    ///     to everything two queues touched would be a slower frame everywhere in exchange for a
    ///     handover that was doing real work.
    /// </remarks>
    [Fact]
    public void AResourceOneQueueWritesIsStillHandedOver() {
        var graph = TwoReaders(bothRead: true);
        var queues = new RecordingQueues();
        graph.Execute(queues);

        Assert.Contains(queues.Barriers, barrier => barrier.TransfersOwnership && barrier.Buffer.IsValid);
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
