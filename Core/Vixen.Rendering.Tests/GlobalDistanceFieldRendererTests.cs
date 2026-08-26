// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.DistanceFields;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     The node that keeps the clipmap over the camera, on the device and named in the frame's set.
/// </summary>
/// <remarks>
///     Everything it sequences is tested elsewhere — the composite against closed forms, the upload
///     against the recorded command stream. What is left, and all this asserts, is <i>when</i> it does
///     them: the recomposite is the most expensive thing in a frame and a still camera must not pay
///     for it.
/// </remarks>
public class GlobalDistanceFieldRendererTests {
    [Fact]
    public void TheFirstFrameCompositesAndUploads() {
        using var device = new NullDevice(new() { Record = true });
        using var node = Node(device, out var scene);
        var context = Context(device);

        Record(node, context);

        Assert.Equal(1, node.Composites);
        Assert.NotNull(node.Texture);
        Assert.Equal(1, node.Texture!.Uploads);
        Assert.True(node.Field!.HasContent);
    }

    /// <summary>
    ///     The point of snapping the levels, cashed in. A camera that has not crossed a cell boundary
    ///     would get the same numbers back from a composite that costs every cell of every level.
    /// </summary>
    [Fact]
    public void AStillCameraCompositesOnceAndThenNeverAgain() {
        using var device = new NullDevice(new() { Record = true });
        using var node = Node(device, out _);
        var context = Context(device);

        for (var frame = 0; frame < 10; frame++) {
            Record(node, context);
        }

        Assert.Equal(1, node.Composites);
        Assert.Equal(1, node.Texture!.Uploads);
    }

    [Fact]
    public void MovingLessThanACellChangesNothing() {
        using var device = new NullDevice(new() { Record = true });
        using var node = Node(device, out _);
        var context = Context(device);

        Record(node, context);

        node.ViewPosition = new Vector3(node.Field!.CellSizeOf(0) * 0.3f, 0, 0);
        Record(node, context);

        Assert.Equal(1, node.Composites);
    }

    [Fact]
    public void CrossingACellRecomposites() {
        using var device = new NullDevice(new() { Record = true });
        using var node = Node(device, out _);
        var context = Context(device);

        Record(node, context);

        node.ViewPosition = new Vector3(node.Field!.CellSizeOf(0), 0, 0);
        Record(node, context);

        Assert.Equal(2, node.Composites);
        Assert.Equal(2, node.Texture!.Uploads);
    }

    /// <summary>
    ///     A camera that crossed a cell recomposites, but keeps what the movement did not invalidate.
    ///     A camera that stopped and had something move around it keeps nothing, because a kept cell
    ///     would be geometry left behind where it used to be.
    /// </summary>
    [Fact]
    public void MovingScrollsAndChangingTheSceneDoesNot() {
        using var device = new NullDevice(new() { Record = true });
        using var node = Node(device, out _);
        var context = Context(device);

        Record(node, context);

        Assert.Equal(0, node.Reused);

        node.ViewPosition = new Vector3(node.Field!.CellSizeOf(0), 0, 0);
        Record(node, context);

        Assert.True(node.Reused > 0, "a one-cell step kept nothing");

        node.InstancesVersion++;
        Record(node, context);

        Assert.Equal(0, node.Reused);
    }

    /// <summary>
    ///     Comparing the instances themselves every frame would cost more than the comparison saves,
    ///     so the list carries a version and whoever changes it says so.
    /// </summary>
    [Fact]
    public void ChangingTheInstancesNeedsTheVersionBumpedToBeSeen() {
        using var device = new NullDevice(new() { Record = true });
        using var node = Node(device, out _);
        var context = Context(device);

        Record(node, context);

        node.Instances.Add(DistanceFieldInstance.At(Sphere(), Vector3.Zero));
        Record(node, context);

        Assert.Equal(1, node.Composites);

        node.InstancesVersion++;
        Record(node, context);

        Assert.Equal(2, node.Composites);
    }

    /// <summary>
    ///     The names are the frame's answer to "where is the clipmap now", so they go in every frame
    ///     even when nothing was recomposited — a set rebuilt for some other reason would otherwise
    ///     bind whatever the last frame left.
    /// </summary>
    [Fact]
    public void TheNamesAreWrittenEvenOnAFrameThatCompositedNothing() {
        using var device = new NullDevice(new() { Record = true });
        using var node = Node(device, out var scene);
        var context = Context(device);

        Record(node, context);
        scene.Parameters.Clear();
        Record(node, context);

        Assert.Equal(1, node.Composites);
        Assert.True(scene.Parameters.Has(ParameterKeys.New<float>("DistanceFieldAo.GlobalDistanceField.distanceFieldVolumes[0].maxDistance")));
    }

    [Fact]
    public void ANodeWithNoClipmapDoesNothingAtAll() {
        using var device = new NullDevice(new() { Record = true });
        using var node = new GlobalDistanceFieldRenderer();
        var context = Context(device);

        Record(node, context);

        Assert.Equal(0, node.Composites);
        Assert.Null(node.Texture);
    }

    [Fact]
    public void DisposingReleasesTheMirror() {
        using var device = new NullDevice(new() { Record = true });
        var node = Node(device, out _);
        var context = Context(device);

        Record(node, context);
        node.Dispose();

        Assert.Null(node.Texture);

        node.Dispose();
    }

    /// <summary>
    ///     The first composite is not deferred, because there is nothing to draw instead.
    /// </summary>
    /// <remarks>
    ///     ⚠ It is also not scheduled into the background tier, and that is the point of asserting
    ///     it: the frame genuinely is blocked on this one, and <c>Background</c> on work the caller
    ///     is blocked on is a pessimisation rather than a no-op — the waiting thread drains every
    ///     unrelated frame item it can reach before the one it wants.
    ///     <c>WaitingOnBackgroundWorkRunsUnrelatedFrameWorkFirst</c> is that property, next door.
    /// </remarks>
    [Fact]
    public void TheFirstCompositeIsNotDeferredBecauseThereIsNothingToDrawInstead() {
        using var device = new NullDevice(new() { Record = true });
        using var jobs = new JobScheduler(0);
        using var node = Node(device, out _);
        var context = Context(device);

        node.Jobs = jobs;
        Record(node, context);

        Assert.False(node.IsRefreshing);
        Assert.Equal(0, node.Deferred);
        Assert.Equal(1, node.Composites);
        Assert.Equal(1, node.Texture!.Uploads);
    }

    /// <summary>
    ///     A recomposite the frame is not waiting for: scheduled, kept, and drawn around.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is the whole of what makes the node a consumer of the background tier</b>, and
    ///         the assertion that it did not simply relabel a <c>ParallelFor</c>. At nought workers
    ///         nothing can run except on the thread that asks for it, so "no slice of the new
    ///         composite has run and the frame carried on anyway" is decided rather than raced.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The names have to still describe the old clipmap too.</b> The bounds a shader
    ///         turns a world position into a texture coordinate with come off the same field the
    ///         upload reads, so a field that advanced its box before its cells would name the new box
    ///         over the old texels — every distance reported at an offset from where it is, for as
    ///         many frames as the refresh takes.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ARecompositeIsDeferredAndTheFrameDrawsTheClipmapItReplaces() {
        using var device = new NullDevice(new() { Record = true });
        using var jobs = new JobScheduler(0);
        using var node = Node(device, out var scene);
        var context = Context(device);
        var field = node.Field!;

        node.Jobs = jobs;
        Record(node, context);

        var slices = field.SlicesComposited;
        var before = scene.Parameters.Get(ParameterKeys.New<Vector3>(Minimum));

        node.ViewPosition = new Vector3(field.CellSizeOf(0) * 4f, 0, 0);
        Record(node, context);

        Assert.True(node.IsRefreshing, "the recomposite was not deferred at all");
        Assert.Equal(1, node.Deferred);

        // Counted from inside the work rather than timed from outside: not one slice of the new
        // composite has run, and the frame recorded anyway.
        Assert.Equal(slices, field.SlicesComposited);
        Assert.Equal(1, node.Composites);
        Assert.Equal(1, node.Texture!.Uploads);
        Assert.Equal(before, scene.Parameters.Get(ParameterKeys.New<Vector3>(Minimum)));

        // ⚠ And every frame after it, which is the half that is easy to leave out and the half that
        // matters. A node that polled with `Complete` rather than `IsCompleted` would defer the
        // frame that started the refresh and block on the very next one — deferral by exactly one
        // frame, which is the defect wearing the shape of the fix.
        for (var frame = 0; frame < 3; frame++) {
            Record(node, context);
        }

        Assert.True(node.IsRefreshing, "a later frame waited for the refresh instead of drawing around it");
        Assert.Equal(4, node.Deferred);
        Assert.Equal(slices, field.SlicesComposited);
        Assert.Equal(1, node.Composites);
        Assert.Equal(before, scene.Parameters.Get(ParameterKeys.New<Vector3>(Minimum)));

        // The control: the slices were runnable and only waiting, which is what makes the assertions
        // above about the tier rather than about a refresh that was silently dropped.
        node.WaitForRefresh();

        Assert.Equal(slices + field.LevelCount * field.Resolution, field.SlicesComposited);

        // And the next frame takes it — both halves at once, the cells and the names.
        Record(node, context);

        Assert.False(node.IsRefreshing);
        Assert.Equal(2, node.Composites);
        Assert.Equal(2, node.Texture.Uploads);
        Assert.NotEqual(before, scene.Parameters.Get(ParameterKeys.New<Vector3>(Minimum)));
    }

    /// <summary>
    ///     The deferred slices are in the tier that yields, and unrelated frame work goes first.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Work against work, at nought workers, so the take order is decided.</b> The claim
    ///         is that a thread completing an unrelated frame job runs every frame item it can reach
    ///         and not one composite slice — which is true of <c>Background</c> and false of
    ///         <c>Frame</c>, because the slices were queued first and a frame-tier taker drains in
    ///         the order things arrived.
    ///     </para>
    ///     <para>
    ///         ⚠ Eight frame jobs on purpose, well inside the scheduler's share of one background
    ///         item per sixty-four frame ones. A version of this with a hundred would be asserting
    ///         that the fairness rule does not exist.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheDeferredCompositeYieldsToFrameWorkOnTheSameScheduler() {
        const int FrameJobs = 8;

        using var device = new NullDevice(new() { Record = true });
        using var jobs = new JobScheduler(0);
        using var node = Node(device, out _);
        var context = Context(device);
        var field = node.Field!;

        node.Jobs = jobs;
        Record(node, context);

        node.ViewPosition = new Vector3(field.CellSizeOf(0) * 4f, 0, 0);
        Record(node, context);

        Assert.True(node.IsRefreshing, "nothing was deferred, so there is no take order to assert");

        var slices = field.SlicesComposited;
        var ran = new StrongBox<int>();
        var frame = default(JobHandle);

        for (var index = 0; index < FrameJobs; index++) {
            frame = jobs.Schedule(new CountJob(ran));
        }

        jobs.Complete(frame);

        Assert.Equal(FrameJobs, ran.Value);
        Assert.Equal(slices, field.SlicesComposited);

        // The control again, and the half that stops this passing on a scheduler that had lost the
        // slices rather than deferred them.
        node.WaitForRefresh();

        Assert.Equal(slices + field.LevelCount * field.Resolution, field.SlicesComposited);
    }

    /// <summary>
    ///     Without a scheduler the node composites inside the frame, exactly as it always did.
    /// </summary>
    [Fact]
    public void ANodeWithNoSchedulerDefersNothing() {
        using var device = new NullDevice(new() { Record = true });
        using var node = Node(device, out _);
        var context = Context(device);

        Record(node, context);

        node.ViewPosition = new Vector3(node.Field!.CellSizeOf(0) * 4f, 0, 0);
        Record(node, context);

        Assert.False(node.IsRefreshing);
        Assert.Equal(0, node.Deferred);
        Assert.Equal(2, node.Composites);
        Assert.Equal(2, node.Texture!.Uploads);
    }

    /// <summary>
    ///     Disposing with a refresh in flight drains it rather than walking away from it.
    /// </summary>
    /// <remarks>
    ///     The slices write into the clipmap's spare buffers, which belong to a field this node does
    ///     not own — so work still running after the node has gone is a second composite writing the
    ///     buffer the first one is in the middle of.
    /// </remarks>
    [Fact]
    public void DisposingDrainsARefreshThatIsStillOutstanding() {
        using var device = new NullDevice(new() { Record = true });
        using var jobs = new JobScheduler(0);
        var node = Node(device, out _);
        var context = Context(device);
        var field = node.Field!;

        node.Jobs = jobs;
        Record(node, context);

        node.ViewPosition = new Vector3(field.CellSizeOf(0) * 4f, 0, 0);
        Record(node, context);

        var slices = field.SlicesComposited;

        Assert.True(node.IsRefreshing);

        node.Dispose();

        Assert.False(node.IsRefreshing);
        Assert.False(field.IsRefreshing);
        Assert.Equal(slices + field.LevelCount * field.Resolution, field.SlicesComposited);
    }

    /// <summary>
    ///     A refresh that could not be started leaves the clipmap able to start the next one.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>There is one spare buffer per level, so there can be one refresh</b> — and a
    ///         refresh nobody published or abandoned is a clipmap that can never start another. The
    ///         throw would then arrive once and the refusal every frame for ever after, which is a far
    ///         worse failure than whatever went wrong.
    ///     </para>
    ///     <para>
    ///         A disposed scheduler is the cheapest way to make the scheduling call itself fail, and
    ///         it is not a contrived one: a host tearing down while a frame is in flight is exactly
    ///         this order.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ARefreshThatCouldNotBeScheduledIsGivenBackRatherThanLeftOutstanding() {
        using var device = new NullDevice(new() { Record = true });
        var jobs = new JobScheduler(0);
        using var node = Node(device, out _);
        var context = Context(device);
        var field = node.Field!;

        node.Jobs = jobs;
        Record(node, context);

        jobs.Dispose();
        node.ViewPosition = new Vector3(field.CellSizeOf(0) * 4f, 0, 0);

        Assert.Throws<ObjectDisposedException>(() => Record(node, context));

        Assert.False(node.IsRefreshing);
        Assert.False(field.IsRefreshing);

        // And the proof that it is usable rather than merely reporting so.
        field.Update(Vector3.Zero, []);
    }

    /// <summary>The name of the finest level's box, which is what says which composite is bound.</summary>
    const string Minimum = "DistanceFieldAo.GlobalDistanceField.distanceFieldVolumes[0].minimum";

    static GlobalDistanceFieldRenderer Node(NullDevice device, out SceneConstants scene) {
        scene = new(device, "ForwardPlus");

        return new() {
            Field = new GlobalDistanceField(8, 4f, 2),
            SceneConstants = scene,
            ViewPosition = Vector3.Zero,
            Parallel = false
        };
    }

    static MeshDistanceField Sphere() {
        var (vertices, indices) = Icosahedron();

        return MeshDistanceFieldBaker.Bake(vertices, indices, new() { Resolution = 4, SignRayCount = 8 });
    }

    /// <summary>The cheapest closed mesh that is not degenerate. The bake is not what is under test.</summary>
    static (Vector3[] Vertices, int[] Indices) Icosahedron() {
        Vector3[] vertices = [
            new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)
        ];

        int[] indices = [0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3];

        return (vertices, indices);
    }

    static RenderDrawContext Context(NullDevice device) =>
        new(device.BeginCommandList(), new EffectSystem()) { Device = device };

    /// <summary>
    ///     Driven directly rather than through a compositor. The phase methods are
    ///     <c>protected internal</c> and this assembly is a friend, so what is under test is the node
    ///     and not the graph that would otherwise have to be stood up around it.
    /// </summary>
    static void Record(GlobalDistanceFieldRenderer node, RenderDrawContext context) =>
        node.Record(null!, context);

    /// <summary>Frame work that says it ran, and nothing else.</summary>
    /// <param name="ran">The counter.</param>
    /// <remarks>
    ///     A box rather than a field, because the scheduler copies the struct: a counter incremented
    ///     on the copy would be a test that always saw nought and never noticed.
    /// </remarks>
    readonly struct CountJob(StrongBox<int> ran) : IJob {
        /// <inheritdoc />
        public void Execute() => Interlocked.Increment(ref ran.Value);
    }
}
