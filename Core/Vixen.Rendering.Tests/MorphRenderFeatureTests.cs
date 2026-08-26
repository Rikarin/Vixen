// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Rendering.Ecs;
using Vixen.Rendering.Features;
using Vixen.Shaders;
using Vixen.Shaders.Generated;
using Xunit;

namespace Vixen.Rendering.Tests;

/// <summary>
///     Blend shapes reaching a frame: the buffer, the pre-pass, and the draw that reads what it wrote.
/// </summary>
/// <remarks>
///     <para>
///         <b>What <c>MorphTargetTests</c> and <c>MorphScatterDeviceTests</c> deliberately do not
///         cover.</b> Those two are the arithmetic — the quantiser, the packing, and the two
///         processors agreeing about <c>v += w·Δ</c>. This is the wiring, and it is a separate claim:
///         a kernel that computes the right answer into a buffer no draw reads is exactly the state
///         the feature was built out of, and every counter in it said the scene was healthy.
///     </para>
///     <para>
///         <b>Asserted against the recorded command stream rather than against a picture.</b> A morph
///         that copied the wrong range, dispatched the wrong number of times, or ran the copy after
///         the dispatch renders <em>plausibly</em> — the face still has a face on it — so what is
///         checked here is which calls went on the list, in what order, with what arguments. The
///         numbers those calls produce are <c>MorphScatterDeviceTests</c>' half.
///     </para>
///     <para>
///         ⚠ <b>Over a real <see cref="GeometryBuffer" />, a real
///         <see cref="Graphics.DescriptorAllocator" /> and a real <see cref="ComputePipelineCache" />
///         on the null device</b>, so the suballocation, the state transitions and the set caching are
///         the ones a frame runs rather than stubs of them. Only the effect provider is a stand-in,
///         because compiling Raven here would make this a test of the compiler.
///     </para>
/// </remarks>
public sealed class MorphRenderFeatureTests : IDisposable {
    /// <summary>How many vertices the fixture mesh has.</summary>
    const int Vertices = 24;

    /// <summary>The quantiser's own step for a target whose range is <c>32767·2⁻¹⁵</c>.</summary>
    /// <remarks>
    ///     ⚠ The same discipline the rest of this feature's fixtures keep: every delta is an exact
    ///     multiple of it, so nothing here is true only within a tolerance.
    /// </remarks>
    const float Unit = 1f / 32768f;

    readonly NullDevice device = new(new() { Record = true });
    readonly EffectSystem effects = new();
    readonly RenderSystem system = new();
    readonly MeshRenderFeature meshes = new();
    readonly TransformRenderFeature transforms = new();
    readonly MaterialRenderFeature materials = new();
    readonly GeometryBuffer scene;
    readonly GeometryResidency residency;
    readonly DescriptorAllocator descriptors;
    readonly MorphRenderFeature morph;
    readonly RenderStage opaque;

    public MorphRenderFeatureTests() {
        scene = new(device, SurfaceVertex.SizeInBytes, vertexCapacity: 4096, indexCapacity: 8192);
        residency = new(scene);
        descriptors = new(device, "Morph.Tests");

        morph = new(device, vertexCapacity: 256, entryCapacity: 1024) {
            Effects = effects,
            Pipelines = new(device),
            Descriptors = descriptors,
            Source = scene
        };

        opaque = system.AddStage(new("Opaque"));

        meshes.Add(transforms);
        meshes.Add(materials);
        meshes.Add(morph);
        system.AddFeature(meshes);

        effects.AddProvider(new Kernel(device));

        // ⚠ A pad, so that no mesh in this fixture starts at vertex zero of the scene buffer. Without
        // it the rest slice's base and the morph range's base are both zero, and every assertion that
        // the draw was re-based passes against a feature that rewrote nothing.
        Assert.True(scene.TryAllocate(7, 0, out _));
    }

    public void Dispose() {
        morph.Dispose();
        descriptors.Dispose();
        scene.Dispose();
        system.Dispose();
        device.Dispose();
    }

    // --- The seam ------------------------------------------------------------

    /// <summary>
    ///     ⚠ An attached object draws its vertices out of the morph buffer at its own base, and keeps
    ///     every other field of the draw.
    /// </summary>
    /// <remarks>
    ///     <b>The whole deliverable in one assertion.</b> <see cref="MeshDraw" /> is per render object
    ///     and every stage reads the same array, so a rewritten handle morphs the shading pass, the
    ///     shadow pass, the velocity pass and the depth pre-pass together. The index buffer is checked
    ///     because it must <em>not</em> move: a morph displaces vertices and never renumbers them, and
    ///     an index is relative to <see cref="MeshDraw.VertexOffset" /> — so the offset has to be
    ///     rewritten in the same breath as the handle or the object draws some other mesh's vertices.
    /// </remarks>
    [Fact]
    public void An_attached_object_draws_from_the_morph_buffer_at_its_own_base() {
        var (id, draw, slice) = Attach();

        Assert.Equal(morph.Buffer, draw.VertexBuffer);
        Assert.NotEqual(scene.Vertices, draw.VertexBuffer);
        Assert.Equal(0, draw.VertexOffset);
        Assert.NotEqual(slice.BaseVertex, draw.VertexOffset);

        Assert.Equal(scene.Indices, draw.IndexBuffer);
        Assert.Equal(slice.IndexCount, draw.Count);
        Assert.Equal(slice.FirstIndex, draw.FirstIndex);

        var record = system.Objects.Data.Data(morph.Instances)[id.Index];

        Assert.True(record.IsMorphed);
        Assert.Equal(Vertices, record.VertexCount);
        Assert.Equal(2, record.TargetCount);
    }

    /// <summary>A mesh with no blend shapes is left alone, and that is not a failure.</summary>
    [Fact]
    public void A_mesh_with_no_shapes_keeps_the_scene_buffer() {
        var mesh = Mesh(shapes: false);
        var key = GeometryKey.Of(Reference(1));

        Assert.True(residency.Acquire(key, () => mesh, out var slice, out _));

        var draw = new MeshDraw { InstanceCount = 1 };
        scene.Apply(ref draw, slice);

        var id = Object();

        Assert.False(morph.Attach(system, id, key, mesh, slice, ref draw));
        Assert.Equal(scene.Vertices, draw.VertexBuffer);
        Assert.Equal(slice.BaseVertex, draw.VertexOffset);
        Assert.False(system.Objects.Data.Data(morph.Instances)[id.Index].IsMorphed);
    }

    /// <summary>
    ///     Two instances of one mesh get a vertex range each and share one copy of the deltas.
    /// </summary>
    /// <remarks>
    ///     The memory claim the guide makes, as an assertion. A head with twenty shapes is 1.28 MB of
    ///     entries however many characters wear it, and forty-eight bytes a vertex per character —
    ///     because the deltas are the mesh's and the weights are the instance's.
    /// </remarks>
    [Fact]
    public void Two_instances_of_one_mesh_share_its_deltas_and_not_its_vertices() {
        var mesh = Mesh();
        var key = GeometryKey.Of(Reference(1));

        var first = Attach(mesh, key);
        var second = Attach(mesh, key);

        Assert.Equal(1, morph.MeshCount);
        Assert.Equal(2, morph.InstanceCount);
        Assert.Equal(2 * Vertices, morph.UsedVertices);
        Assert.Equal(Entries(mesh), morph.UsedEntries);

        Assert.NotEqual(first.Draw.VertexOffset, second.Draw.VertexOffset);
        Assert.Equal(first.Draw.VertexBuffer, second.Draw.VertexBuffer);
    }

    // --- The pre-pass --------------------------------------------------------

    /// <summary>
    ///     ⚠ The first frame restores the rest pose even when every weight is zero, and dispatches
    ///     nothing.
    /// </summary>
    /// <remarks>
    ///     <b>The zero-value trap this feature is most exposed to.</b> An instance that reached its
    ///     first record clean would be drawn out of whatever the allocator left in its range — which
    ///     does not look like a morph gone wrong, it looks like the geometry is missing. So "no weight
    ///     has been set" must still copy, and must still not dispatch: a shape at zero costs no
    ///     dispatch, which is what makes twenty shapes cost the two a face is actually making.
    /// </remarks>
    [Fact]
    public void An_unweighted_instance_is_copied_once_and_dispatched_for_never() {
        var attached = Attach();

        Record();

        Assert.Equal(1, morph.Copies);
        Assert.Equal(0, morph.Dispatches);

        var copy = Assert.Single(MorphCopies);

        Assert.Equal(Packed(scene.Vertices), copy.A);
        Assert.Equal((long)attached.Slice.BaseVertex * SurfaceVertex.SizeInBytes, copy.B);
        Assert.Equal(Packed(morph.Buffer), copy.C);
        Assert.Equal((long)attached.Draw.VertexOffset * SurfaceVertex.SizeInBytes, copy.D);
        Assert.Equal((long)Vertices * SurfaceVertex.SizeInBytes, copy.E);

        // And the second frame does neither, because nothing changed.
        Record();

        Assert.Equal(0, morph.Copies);
        Assert.Equal(0, morph.Dispatches);
        Assert.Empty(MorphCopies);
    }

    /// <summary>
    ///     ⚠ One dispatch per <em>active</em> shape, each of the entries that shape has, and a barrier
    ///     between every pair.
    /// </summary>
    /// <remarks>
    ///     <b>Not one dispatch per instance</b>, which is where the implementation and doc 33 § D4's
    ///     sketch differ: two shapes may move the same vertex — that is what a corrective <em>is</em> —
    ///     so a single dispatch over their concatenated entries would have two invocations
    ///     read-modify-writing one vertex and the answer would be whichever landed last. There is no
    ///     float atomic to fix that with. The barrier between them is what makes the order mean
    ///     something, and it is asserted by count because a stream with one fewer of them is a race
    ///     that produces a correct picture on the machine it was written on.
    /// </remarks>
    [Fact]
    public void Each_active_shape_is_one_dispatch_of_its_own_entries() {
        var mesh = Mesh();
        var attached = Attach(mesh);

        Assert.True(morph.SetWeights(attached.Id, [0.5f, 0f]));

        Record();

        var dispatch = Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.Dispatch));

        Assert.Equal(Groups(mesh.MorphTargets[0].Count), dispatch.A);
        Assert.Equal(1, morph.Dispatches);

        // Both shapes now, and the second is the one that was skipped a moment ago.
        Assert.True(morph.SetWeights(attached.Id, [0.5f, 1f]));

        Record();

        var both = device.Recorder.OfKind(RecordedCommandKind.Dispatch);

        Assert.Equal(2, both.Count);
        Assert.Equal(Groups(mesh.MorphTargets[0].Count), both[0].A);
        Assert.Equal(Groups(mesh.MorphTargets[1].Count), both[1].A);
        Assert.Equal(2, morph.Dispatches);
    }

    /// <summary>
    ///     ⚠ The rest pose is copied in before anything is dispatched onto it, and the buffer ends the
    ///     pass as a vertex buffer.
    /// </summary>
    /// <remarks>
    ///     <b>The order is the correctness.</b> The kernel adds, so a dispatch recorded before the copy
    ///     has its answer overwritten by the rest pose — a mesh that is drawn perfectly, at rest, with
    ///     every counter reporting the dispatches it made. And the last transition is what the draws
    ///     need: the barrier between the dispatch and the draw is the pre-pass's to record, because it
    ///     is the side that knows the write happened.
    /// </remarks>
    [Fact]
    public void The_copy_precedes_the_dispatch_and_the_buffer_ends_as_vertex_input() {
        var attached = Attach();

        morph.SetWeights(attached.Id, [1f, 1f]);

        Record();

        var copy = MorphCopies[0].Sequence;
        var dispatch = Sequence(device.Recorder!.Commands, RecordedCommandKind.Dispatch);

        Assert.True(copy < dispatch, $"the copy is at {copy} and the first dispatch at {dispatch}");

        // Four transitions of the morph buffer and two of the scene's, and the shape of them is what
        // says the brackets are closed: nothing here checks the states directly, because the recorder
        // does not carry them — what it carries is that the barriers happened, and the count is what
        // a missing one changes.
        Assert.Equal(2, morph.Dispatches);
        Assert.True(device.Recorder.CountOf(RecordedCommandKind.Barrier) >= 6);
    }

    /// <summary>A weight that is set to what it already was leaves the instance clean.</summary>
    /// <remarks>
    ///     The property that makes <see cref="MorphWeightSystem" /> able to push every character's
    ///     weights every frame: a face that is holding an expression costs a comparison, not a copy of
    ///     its whole vertex range and a dispatch per shape.
    /// </remarks>
    [Fact]
    public void Re_setting_the_same_weights_records_nothing() {
        var attached = Attach();

        morph.SetWeights(attached.Id, [1f, 0.25f]);
        Record();

        Assert.Equal(1, morph.Copies);
        Assert.Equal(2, morph.Dispatches);

        morph.SetWeights(attached.Id, [1f, 0.25f]);
        Record();

        Assert.Equal(0, morph.Copies);
        Assert.Equal(0, morph.Dispatches);
        Assert.Empty(device.Recorder!.OfKind(RecordedCommandKind.Dispatch));
    }

    /// <summary>Clearing the weights puts the face back at rest rather than leaving it where it was.</summary>
    /// <remarks>
    ///     ⚠ The half of the dirty check that a "skip when nothing is active" implementation gets
    ///     wrong: the vertices still hold last frame's expression, so returning to rest is a
    ///     <em>copy</em> and no dispatches, not nothing at all.
    /// </remarks>
    [Fact]
    public void Clearing_the_weights_restores_the_rest_pose() {
        var attached = Attach();

        morph.SetWeights(attached.Id, [1f, 1f]);
        Record();

        morph.SetWeights(attached.Id, []);
        Record();

        Assert.Equal(1, morph.Copies);
        Assert.Equal(0, morph.Dispatches);
    }

    /// <summary>A frame that cannot resolve the kernel says so rather than drawing a stale face.</summary>
    [Fact]
    public void A_missing_kernel_degrades_and_is_named() {
        using var own = new Isolated(device, vertexCapacity: 256, entryCapacity: 1024);

        var mesh = Mesh();
        var key = GeometryKey.Of(Reference(9));

        Assert.True(residency.Acquire(key, () => mesh, out var slice, out _));

        var draw = new MeshDraw { InstanceCount = 1 };
        scene.Apply(ref draw, slice);

        own.Morph.Source = scene;

        var id = own.Object();

        Assert.True(own.Morph.Attach(own.System, id, key, mesh, slice, ref draw));
        Assert.True(own.Morph.SetWeights(id, [1f, 1f]));

        using var list = device.BeginCommandList(QueueKind.Graphics);

        // ⚠ True, and the rest pose was still copied. A frame that could not resolve the kernel has to
        // put the mesh in its buffer anyway — drawing it at rest is a picture, and drawing it out of an
        // uninitialised range is not.
        Assert.True(own.Morph.Record(list));
        Assert.Equal(1, own.Morph.Copies);
        Assert.Equal(0, own.Morph.Dispatches);

        Assert.NotNull(own.Morph.Degraded);
        Assert.Contains("effect system", own.Morph.Degraded);
    }

    // --- Giving it back ------------------------------------------------------

    /// <summary>Forgetting an object frees its range, and the last one frees the mesh's deltas.</summary>
    [Fact]
    public void The_last_instance_of_a_mesh_takes_its_deltas_with_it() {
        var mesh = Mesh();
        var key = GeometryKey.Of(Reference(1));

        var first = Attach(mesh, key);
        var second = Attach(mesh, key);

        Assert.True(morph.Forget(first.Id));

        Assert.Equal(1, morph.MeshCount);
        Assert.Equal(Entries(mesh), morph.UsedEntries);
        Assert.Equal(Vertices, morph.UsedVertices);

        Assert.True(morph.Forget(second.Id));

        Assert.Equal(0, morph.MeshCount);
        Assert.Equal(0, morph.UsedEntries);
        Assert.Equal(0, morph.UsedVertices);

        Assert.False(morph.Forget(second.Id));
    }

    /// <summary>An instance that does not fit is refused, counted, and drawn at rest.</summary>
    /// <remarks>
    ///     ⚠ <b>Refused rather than grown</b>, <see cref="GeometryBuffer" />'s reason: every
    ///     <see cref="MeshDraw" /> already attached holds the handle. What that costs is a character
    ///     whose face stops animating, which is why <see cref="MorphRenderFeature.Dropped" /> exists
    ///     rather than the refusal being silent.
    /// </remarks>
    [Fact]
    public void An_instance_that_does_not_fit_draws_at_rest_and_is_counted() {
        using var own = new Isolated(device, vertexCapacity: Vertices, entryCapacity: 1024);

        var mesh = Mesh();
        var key = GeometryKey.Of(Reference(1));

        Assert.True(residency.Acquire(key, () => mesh, out var slice, out _));

        var first = new MeshDraw { InstanceCount = 1 };
        var second = new MeshDraw { InstanceCount = 1 };

        scene.Apply(ref first, slice);
        scene.Apply(ref second, slice);

        own.Morph.Source = scene;

        Assert.True(own.Morph.Attach(own.System, own.Object(), key, mesh, slice, ref first));
        Assert.False(own.Morph.Attach(own.System, own.Object(), key, mesh, slice, ref second));

        Assert.Equal(1, own.Morph.Dropped);
        Assert.Equal(scene.Vertices, second.VertexBuffer);
        Assert.Equal(slice.BaseVertex, second.VertexOffset);

        // ⚠ And the mesh's deltas stayed resident. Dropping them with the refused instance would
        // re-upload the same run the next time one of them fitted.
        Assert.Equal(1, own.Morph.MeshCount);
    }

    // --- The frame path ------------------------------------------------------

    /// <summary>
    ///     ⚠ An entity with a blend-shaped mesh and a weight is morphed by the systems a game runs,
    ///     without anything in the test touching the feature.
    /// </summary>
    /// <remarks>
    ///     <b>The "built but never fed" assertion, and the one this whole change exists to make
    ///     true.</b> Everything before this drives <see cref="MorphRenderFeature" /> directly, which
    ///     proves the feature works and proves nothing about whether a frame reaches it. Here the only
    ///     inputs are a component, a mesh source and two systems — the three things a level actually
    ///     has — and what is asserted is that a dispatch went on the list.
    /// </remarks>
    [Fact]
    public void An_entity_with_weights_is_morphed_by_the_systems_a_frame_runs() {
        using var world = new World(nameof(An_entity_with_weights_is_morphed_by_the_systems_a_frame_runs));

        var reference = Reference(7);
        var mesh = Mesh();

        var extraction = new MeshExtractionSystem(system, meshes, transforms, materials, residency) {
            Stages = opaque.Mask,
            Meshes = new OneMesh(mesh),
            Morphing = morph
        };

        var weights = new MorphWeightSystem { Feature = morph };

        var entity = world.Create();

        MeshRenderables.Attach(world, entity, MeshRenderables.Default(reference));
        world.Add(entity, new WorldTransform { Value = Matrix4x4.Identity });
        world.Add(entity, new BlendShapeWeights { Weights = [1f, 0f] });

        extraction.Extract(world);
        weights.Run(world);

        Assert.Equal(1, morph.InstanceCount);
        Assert.Equal(1, weights.Weighted);

        Record();

        Assert.Equal(1, morph.Copies);
        Assert.Equal(1, morph.Dispatches);
        Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.Dispatch));

        // And the draw the mesh feature will record reads the morph buffer, which is the end of the
        // chain: extraction attached, the system weighted, the pre-pass wrote, and the draw points at
        // what it wrote.
        var handle = world.Read<RenderHandle>(entity);

        Assert.Equal(morph.Buffer, system.Objects.Data.Data(meshes.Draws)[handle.Object.Index].VertexBuffer);

        // Then the entity goes, and so does its range — the leak that would otherwise look like a
        // level that stops morphing faces after a few reloads.
        world.Remove<MeshRenderable>(entity);
        extraction.Extract(world);

        Assert.Equal(0, morph.InstanceCount);
        Assert.Equal(0, morph.UsedVertices);
    }

    /// <summary>
    ///     ⚠ The weight system tells the entity what its mesh calls each slot, and that is what a clip
    ///     binds against.
    /// </summary>
    /// <remarks>
    ///     <b>The other direction, and the link that makes animating a blend shape possible at all.</b>
    ///     A clip names a shape — <c>MorphTargetData.Name</c>'s rule, so that re-exporting a mesh with
    ///     its shapes reordered does not re-target every curve — and the component is addressed by
    ///     slot. The feature is the only thing that has seen both ends, so it publishes the table.
    ///     Published once and not every frame: a caller that wrote its own binding is stating one, and
    ///     correcting it every frame would make a hand-built binding impossible.
    /// </remarks>
    [Fact]
    public void The_weight_system_publishes_what_the_mesh_calls_each_slot() {
        using var world = new World(nameof(The_weight_system_publishes_what_the_mesh_calls_each_slot));

        var extraction = new MeshExtractionSystem(system, meshes, transforms, materials, residency) {
            Stages = opaque.Mask,
            Meshes = new OneMesh(Mesh()),
            Morphing = morph
        };

        var weights = new MorphWeightSystem { Feature = morph };
        var entity = world.Create();

        MeshRenderables.Attach(world, entity, MeshRenderables.Default(Reference(11)));
        world.Add(entity, new WorldTransform { Value = Matrix4x4.Identity });
        world.Add(entity, new BlendShapeWeights());

        // Nothing is bound before extraction, because the feature has nothing attached to answer from.
        weights.Run(world);

        Assert.Equal(0, weights.Bound);
        Assert.Null(world.Read<BlendShapeWeights>(entity).Shapes);

        extraction.Extract(world);
        weights.Run(world);

        Assert.Equal(1, weights.Bound);
        Assert.Equal(["jawOpen", "browRaise"], world.Read<BlendShapeWeights>(entity).Shapes!);

        // And it is not republished, so a binding somebody wrote by hand survives the next frame.
        world.Get<BlendShapeWeights>(entity).Shapes = ["mine"];
        weights.Run(world);

        Assert.Equal(0, weights.Bound);
        Assert.Equal(["mine"], world.Read<BlendShapeWeights>(entity).Shapes!);
    }

    // --- The fixture ---------------------------------------------------------

    static long Packed(BufferHandle handle) => (long)handle.Value.Packed;

    static Vector3 Exact(int x, int y, int z) => new(x * Unit, y * Unit, z * Unit);

    static AssetReference Reference(int seed) =>
        new(new AssetId(new($"{seed:D8}-0000-0000-0000-000000000000")));

    static int Groups(int entries) => (entries + MorphRenderFeature.GroupSize - 1) / MorphRenderFeature.GroupSize;

    static int Entries(MeshData mesh) {
        var total = 0;

        foreach (var target in mesh.MorphTargets) {
            total += target.Count;
        }

        return total;
    }

    static int Sequence(IReadOnlyList<RecordedCommand> stream, RecordedCommandKind kind) {
        foreach (var command in stream) {
            if (command.Kind == kind) {
                return command.Sequence;
            }
        }

        return int.MaxValue;
    }

    /// <summary>A mesh whose vertices are all different, so a scatter at the wrong stride shows.</summary>
    /// <remarks>
    ///     Two shapes, of different sizes, one of which moves a vertex the other does too — which is
    ///     what makes "one dispatch per target" a claim rather than a coincidence.
    /// </remarks>
    static MeshData Mesh(bool shapes = true) {
        var positions = new Vector3[Vertices];
        var normals = new Vector3[Vertices];
        var indices = new int[Vertices];

        for (var index = 0; index < Vertices; index++) {
            positions[index] = Exact(index * 71, index * -37, index * 13);
            normals[index] = Exact(index * 5, 32768, index * -3);
            indices[index] = index;
        }

        int[] jaw = [2, 5, 9, 11, 20];
        int[] brow = [5, 6, 21];

        return new() {
            Name = "head",
            Positions = positions,
            Normals = normals,
            Indices = indices,
            MorphTargets = shapes
                ?
                [
                    MorphTargetData.Encode(
                        "jawOpen",
                        jaw,
                        [.. jaw.Select(v => Exact(32767, -16384, 8192 + (v * 3)))],
                        [.. jaw.Select(v => Exact(v * 7, -4096, 16384))]
                    ),
                    MorphTargetData.Encode(
                        "browRaise",
                        brow,
                        [.. brow.Select(v => Exact(-8192, 32767, v * -5))],
                        []
                    )
                ]
                : []
        };
    }

    /// <summary>
    ///     The copies whose destination is the morph buffer, which is not every copy on the list.
    /// </summary>
    /// <remarks>
    ///     ⚠ The frame also flushes the scene's staging and this feature's own entry staging, so an
    ///     assertion over every <c>CopyBuffer</c> would be counting three other things — and would pass
    ///     on a frame where the rest pose was never restored at all.
    /// </remarks>
    IReadOnlyList<RecordedCommand> MorphCopies => [
        .. device.Recorder!.OfKind(RecordedCommandKind.CopyBuffer).Where(copy => copy.C == Packed(morph.Buffer))
    ];

    RenderObjectId Object() =>
        system.Objects.Add(new() { Bounds = new(Vector3.Zero, 1f), Stages = opaque.Mask, FeatureIndex = meshes.Index });

    (RenderObjectId Id, MeshDraw Draw, GeometrySlice Slice) Attach() => Attach(Mesh(), GeometryKey.Of(Reference(1)));

    (RenderObjectId Id, MeshDraw Draw, GeometrySlice Slice) Attach(MeshData mesh) =>
        Attach(mesh, GeometryKey.Of(Reference(1)));

    (RenderObjectId Id, MeshDraw Draw, GeometrySlice Slice) Attach(MeshData mesh, GeometryKey key) {
        Assert.True(residency.Acquire(key, () => mesh, out var slice, out _));

        var draw = new MeshDraw { InstanceCount = 1 };
        scene.Apply(ref draw, slice);

        var id = Object();

        Assert.True(morph.Attach(system, id, key, mesh, slice, ref draw));
        system.Objects.Data.Data(meshes.Draws)[id.Index] = draw;

        return (id, draw, slice);
    }

    /// <summary>One frame's pre-pass, on a list of its own and with the frame boundaries a host keeps.</summary>
    void Record() {
        device.Recorder!.Clear();
        device.BeginFrame();
        descriptors.BeginFrame();

        using (var list = device.BeginCommandList(QueueKind.Graphics, "morph")) {
            // The rest poses first, exactly as WorldRenderer.Draw does — the copy this pass makes reads
            // what the flush put there.
            residency.Flush(list);
            morph.Record(list);

            list.Finish();
            device.GraphicsQueue.Submit([list]);
        }

        device.EndFrame();
    }

    /// <summary>
    ///     A second render system with a morph feature of its own, for the tests that need one with
    ///     different capacities or a different wiring.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A whole system, not just a second feature.</b> A <c>RenderDataKey</c> is a slot index
    ///     and a sub-feature registers its array when it is added to a root feature — so a feature
    ///     built beside the fixture's and never added would write its records over slot zero, which is
    ///     the mesh feature's draws. The feature refuses to attach at all in that state; this is what
    ///     the refusal is telling a caller to do instead.
    /// </remarks>
    sealed class Isolated : IDisposable {
        readonly MeshRenderFeature meshes = new();
        readonly RenderStage stage;

        public Isolated(NullDevice device, int vertexCapacity, int entryCapacity) {
            System = new();
            stage = System.AddStage(new("Isolated"));

            Morph = new(device, vertexCapacity, entryCapacity) {
                Pipelines = new(device),
                Descriptors = new(device, "Isolated")
            };

            meshes.Add(Morph);
            System.AddFeature(meshes);
        }

        public RenderSystem System { get; }

        public MorphRenderFeature Morph { get; }

        public RenderObjectId Object() =>
            System.Objects.Add(
                new() { Bounds = new(Vector3.Zero, 1f), Stages = stage.Mask, FeatureIndex = meshes.Index }
            );

        public void Dispose() {
            Morph.Dispose();
            System.Dispose();
        }
    }

    /// <summary>A source that answers every reference with one mesh.</summary>
    sealed class OneMesh(MeshData mesh) : IMeshSource {
        public bool TryGet(AssetReference reference, out MeshData found) {
            found = mesh;
            return true;
        }
    }

    /// <summary>
    ///     Answers with a compute variant of the kernel, carrying the set layout the dispatch binds.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Set three, because that is where the shader puts its whole interface</b> — the block,
    ///     the vertices and the entries are all <c>[PerDraw]</c>, and the generated
    ///     <see cref="MorphScatterKeys" /> is what says so rather than a number written down here. A
    ///     stand-in that declared them somewhere else would be a test that passed against a layout the
    ///     shipped shader does not have.
    /// </remarks>
    sealed class Kernel(NullDevice device) : IEffectProvider {
        readonly DescriptorSetLayoutHandle layout = device.CreateDescriptorSetLayout(
            new(
                (DescriptorSetSlot)MorphScatterKeys.ConstantBufferSet,
                [
                    new(MorphScatterKeys.ConstantBufferBinding, DescriptorKind.UniformBuffer, ShaderStage.Compute),
                    new(MorphScatterKeys.VerticesBinding, DescriptorKind.StorageBuffer, ShaderStage.Compute),
                    new(MorphScatterKeys.EntriesBinding, DescriptorKind.StorageBuffer, ShaderStage.Compute)
                ],
                MorphScatterKeys.ShaderName
            )
        );

        public Effect? TryGet(EffectKey key) {
            if (key.ShaderName != MorphScatterKeys.ShaderName) {
                return null;
            }

            var layouts = new DescriptorSetLayoutHandle[MorphScatterKeys.ConstantBufferSet + 1];
            layouts[MorphScatterKeys.ConstantBufferSet] = layout;

            return new() {
                Key = key,
                SetLayouts = [.. layouts],
                Stages = [new(ShaderStage.Compute, [1, 2, 3, 4], "main")]
            };
        }
    }
}
