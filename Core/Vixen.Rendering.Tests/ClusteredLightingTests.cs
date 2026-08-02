// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Features;
using Vixen.Shaders;
using Vixen.Shaders.Generated;
using Xunit;

namespace Tests;

/// <summary>
///     Forward+ — docs/plan/06 § Pipelines, the default preset.
/// </summary>
/// <remarks>
///     <para>
///         The shader half has existed for a while: <c>ClusterCulling.rvn</c> bins lights into a
///         froxel grid, and <c>ForwardPlus.rvn</c> has the permutation that swaps its uniform-array
///         loop for the cluster list. What was missing was the CPU side, and what was <em>blocking</em>
///         it was the edge in the middle — compute writes the cluster buffer and the shading pass
///         reads it, and until the compositor declared its dependencies there was nowhere for that
///         barrier to come from.
///     </para>
///     <para>
///         So the load-bearing test here is the ordering one. The rest is arithmetic that has to
///         match a shader nobody can run in a unit test, which is why it is asserted against the
///         constants that shader declares rather than against itself.
///     </para>
/// </remarks>
public class ClusteredLightingTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });
    readonly EffectSystem effects = new();

    // --- The grid -----------------------------------------------------------

    /// <summary>
    ///     The grid's shape is the one <c>ClusterCulling.rvn</c> declares.
    /// </summary>
    /// <remarks>
    ///     Duplicated on purpose and asserted for that reason. They are <c>const</c> in the shader
    ///     because they size an array <em>inside a struct</em>, and a struct's shape cannot depend on
    ///     a variant while the host binds one buffer — so the two sides have to agree by construction
    ///     and this is the thing that notices when they stop.
    /// </remarks>
    [Fact]
    public void The_grid_matches_the_shaders_constants() {
        Assert.Equal(16, ClusterGrid.TilesX);
        Assert.Equal(9, ClusterGrid.TilesY);
        Assert.Equal(24, ClusterGrid.Slices);
        Assert.Equal(32, ClusterGrid.Capacity);
        Assert.Equal(16 * 9 * 24, ClusterGrid.Count);

        // count + 32 indices, four bytes each.
        Assert.Equal(132, Marshal.SizeOf<ClusterLights>());
        Assert.Equal(16 * 9 * 24 * 132L, ClusterGrid.BufferSize);
    }

    /// <summary>
    ///     The dispatch covers the grid, and rounds up where the grid does not divide.
    /// </summary>
    /// <remarks>
    ///     Nine tiles down the screen against a workgroup four deep, so the last group has three
    ///     invocations with no cluster. The shader bounds-tests them; rounding down instead would
    ///     leave the bottom row of every frame unlit by anything but the sun.
    /// </remarks>
    [Fact]
    public void The_dispatch_covers_every_cluster() {
        var groups = ClusterGrid.GroupCount;
        var size = ClusterGrid.WorkgroupSize;

        Assert.Equal(new Int3(4, 3, 6), groups);
        Assert.True(groups.X * size.X >= ClusterGrid.TilesX);
        Assert.True(groups.Y * size.Y >= ClusterGrid.TilesY);
        Assert.True(groups.Z * size.Z >= ClusterGrid.Slices);
    }

    /// <summary>Every cluster has its own slot, and the slots fill the buffer exactly.</summary>
    [Fact]
    public void Cluster_indices_are_a_bijection_onto_the_buffer() {
        var seen = new HashSet<int>();

        for (var slice = 0; slice < ClusterGrid.Slices; slice++) {
            for (var y = 0; y < ClusterGrid.TilesY; y++) {
                for (var x = 0; x < ClusterGrid.TilesX; x++) {
                    var index = ClusterGrid.Index(x, y, slice);

                    Assert.True(seen.Add(index), $"cluster ({x},{y},{slice}) collides at {index}");
                    Assert.InRange(index, 0, ClusterGrid.Count - 1);
                }
            }
        }

        Assert.Equal(ClusterGrid.Count, seen.Count);
    }

    /// <summary>
    ///     Finding a depth's slice inverts the slice's own depth.
    /// </summary>
    /// <remarks>
    ///     The property the exponential split was chosen for: it is invertible in closed form, so a
    ///     fragment finds its slice with a logarithm rather than a search. If the two disagree, a
    ///     fragment reads a cluster the culler filled for somewhere else — which looks like lights
    ///     flickering at particular distances and nothing else.
    /// </remarks>
    [Fact]
    public void A_slices_depth_and_a_depths_slice_invert_each_other() {
        const float near = 0.1f;
        const float far = 1000f;

        for (var slice = 0; slice < ClusterGrid.Slices; slice++) {
            // Just inside the slab, so a boundary's rounding does not decide the answer.
            var start = ClusterGrid.SliceDepth(slice, near, far);
            var end = ClusterGrid.SliceDepth(slice + 1, near, far);
            var middle = MathF.Sqrt(start * end);

            Assert.Equal(slice, ClusterGrid.SliceOf(middle, near, far));
        }
    }

    /// <summary>The slices ascend, start at the near plane and end at the far one.</summary>
    [Fact]
    public void The_slices_span_the_frustum() {
        const float near = 0.1f;
        const float far = 1000f;

        Assert.Equal(near, ClusterGrid.SliceDepth(0, near, far), 4);
        Assert.Equal(far, ClusterGrid.SliceDepth(ClusterGrid.Slices, near, far), 1);

        for (var slice = 1; slice <= ClusterGrid.Slices; slice++) {
            Assert.True(
                ClusterGrid.SliceDepth(slice, near, far) > ClusterGrid.SliceDepth(slice - 1, near, far),
                $"slice {slice} does not start past slice {slice - 1}"
            );
        }
    }

    /// <summary>Anything outside the frustum lands in a slice that exists.</summary>
    /// <remarks>
    ///     A fragment exactly on the far plane rounds to <c>Slices</c> and would read the cluster
    ///     after the last one, which is somebody else's memory.
    /// </remarks>
    [Theory]
    [InlineData(-5f)]
    [InlineData(0f)]
    [InlineData(0.1f)]
    [InlineData(1000f)]
    [InlineData(100000f)]
    public void A_depth_outside_the_frustum_still_lands_in_the_grid(float depth) =>
        Assert.InRange(ClusterGrid.SliceOf(depth, 0.1f, 1000f), 0, ClusterGrid.Slices - 1);

    // --- The frame ----------------------------------------------------------

    /// <summary>The camera the culler and the shading pass are both given.</summary>
    static RenderCamera Camera => RenderCamera.Default with { Position = Vector3.Zero };

    /// <summary>The culler's own block, laid out as <c>ClusterCulling.rvn</c> declares it.</summary>
    /// <remarks>
    ///     Only what a host writes: the camera's half-angle tangents and planes, its view matrix, and
    ///     where this frame's lights are in the ring and how many of them there are. The buffers beside
    ///     them are bindings rather than values.
    /// </remarks>
    static ImmutableArray<EffectParameter> CullingBlock => [
        new(ParameterKeys.New<Vector2>("ClusterCulling.tanHalfFov"), 0, 8),
        new(ParameterKeys.New<float>("ClusterCulling.nearPlane"), 8, 4),
        new(ParameterKeys.New<float>("ClusterCulling.farPlane"), 12, 4),
        new(ParameterKeys.New<Matrix4x4>("ClusterCulling.view"), 16, 64),
        new(ParameterKeys.New<int>("ClusterCulling.lightCount"), 80, 4),
        new(ParameterKeys.New<int>("ClusterCulling.lightBase"), 84, 4)
    ];

    static Effect Compiled(EffectKey key, DescriptorSetLayoutHandle culling = default) =>
        key.ShaderName.Contains("Culling", StringComparison.Ordinal)
            ? new() {
                Key = key,
                Stages = [new(ShaderStage.Compute, [1, 2, 3, 4], "main")],
                SetLayouts = [default, default, culling, default],
                ConstantBufferSize = 96,
                Parameters = CullingBlock,
                Bindings = [
                    new("constants", DescriptorSetSlot.PerMaterial, 0, DescriptorKind.UniformBuffer) { Size = 96 },
                    new("lights", DescriptorSetSlot.PerMaterial, 1, DescriptorKind.StorageBuffer),
                    new("clusters", DescriptorSetSlot.PerMaterial, 2, DescriptorKind.StorageBuffer)
                ]
            }
            : new() {
                Key = key,
                Stages = [
                    new(ShaderStage.Vertex, [1, 2, 3, 4], "main"),
                    new(ShaderStage.Fragment, [5, 6, 7, 8], "main")
                ]
            };

    /// <summary>
    ///     Every variant, with a real layout for the culler's set.
    /// </summary>
    /// <remarks>
    ///     The layout is not decoration: <see cref="ComputeRenderer" /> takes the set it writes from
    ///     the resolved effect, because a set is only bindable to a pipeline whose layout it was
    ///     allocated from. An effect with none binds nothing, and a fixture whose fake had none would
    ///     assert that a pass which binds nothing binds nothing.
    /// </remarks>
    sealed class AlwaysCompiles(NullDevice device) : IEffectProvider {
        readonly DescriptorSetLayoutHandle culling = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerMaterial,
                [
                    new(0, DescriptorKind.UniformBuffer, ShaderStage.Compute),
                    new(1, DescriptorKind.StorageBuffer, ShaderStage.Compute),
                    new(2, DescriptorKind.StorageBuffer, ShaderStage.Compute)
                ],
                "ClusterCulling"
            )
        );

        public Effect? TryGet(EffectKey key) => Compiled(key, culling);
    }

    sealed class Harness : IDisposable {
        public required RenderSystem System { get; init; }
        public required GraphicsCompositor Compositor { get; init; }
        public required RenderGraph Graph { get; init; }
        public required RenderStage Opaque { get; init; }
        public required RenderView Camera { get; init; }
        public required MeshRenderFeature Meshes { get; init; }
        public required MaterialRenderFeature Materials { get; init; }
        public required ForwardLightingRenderFeature Lighting { get; init; }
        public required ComputeRenderer Culling { get; init; }
        public required DescriptorAllocator Allocator { get; init; }
        public required BufferHandle Vertices { get; init; }

        public void Dispose() {
            Culling.Dispose();
            Allocator.Dispose();
            Lighting.Dispose();
            Graph.DisposePool();
            System.Dispose();
        }
    }

    ImportedBuffer Storage(long size, string name) {
        var description = new BufferDescription(size, BufferUsage.Storage, MemoryAccess.DeviceLocal, name);
        return new(device.CreateBuffer(description), description);
    }

    Harness Build(bool clustered = true) {
        var allocator = new DescriptorAllocator(device);
        var system = new RenderSystem();
        var opaque = system.AddStage(new("Opaque"));

        var meshes = new MeshRenderFeature {
            Pipelines = new(device),
            Describer = new EffectPipelineDescriber(device)
        };

        var materials = new MaterialRenderFeature { Effects = effects };
        var lighting = new ForwardLightingRenderFeature { Device = device, Clustered = clustered };

        meshes.Add(materials);
        meshes.Add(lighting);
        system.AddFeature(meshes);
        effects.AddProvider(new AlwaysCompiles(device));

        materials.PermutationKeys["Lit"] = [lighting.PermutationKeys[0]];

        var view = Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, -1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 16f / 9f, 0.1f, 1000f);

        var camera = new RenderView("camera") {
            Position = Vector3.Zero,
            Frustum = new(view * projection)
        };

        var culling = new ComputeRenderer {
            Name = "ClusterCulling",
            ShaderName = "ClusterCulling",
            Pipelines = new(device),
            Groups = ClusterGrid.GroupCount
        };

        culling.BufferReads.Add("SceneLights");
        culling.BufferWrites.Add("Clusters");

        // The culler's own uniforms, which had nowhere to go until `ComputeRenderer` grew a block:
        // the camera both passes are given, and how many of the scene's lights are live.
        culling.ConstantBinding = 0;
        culling.Descriptors.Allocator = allocator;
        culling.Descriptors.Bindings.Add(new() { Name = "lights", Resource = "SceneLights" });
        culling.Descriptors.Bindings.Add(new() { Name = "clusters", Resource = "Clusters" });

        ClusterGrid.Apply(culling.Parameters, Camera, "ClusterCulling");
        culling.Parameters.Set(ParameterKeys.New<Matrix4x4>("ClusterCulling.view"), Camera.View);

        var shading = new RenderPassRenderer { Name = "Forward" };
        shading.ColourTargets.Add("SceneColour");
        shading.Children.Add(new SingleStageRenderer { View = camera, Stage = opaque });

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(1280, 720),
            Game = new SceneRendererSequence { Children = { culling, shading } }
        };

        var colour = new TextureDescription(
            PixelFormat.Rgba16Float,
            1280,
            720,
            TextureUsage.ColourTarget | TextureUsage.Sampled,
            Name: "SceneColour"
        );

        var texture = device.CreateTexture(colour);
        compositor.Imports["SceneColour"] = new(texture, device.CreateTextureView(texture), colour);
        compositor.BufferImports["SceneLights"] = Storage(64 * 256, "SceneLights");
        // Declared rather than imported, and that is the difference that matters: the cluster list is
        // written and read inside one frame and needed by nothing outside it, so the graph owns it —
        // and can therefore drop the culling pass when nothing consumes what it wrote.
        compositor.BufferResources.Add(
            new() { Name = "Clusters", Size = ClusterGrid.BufferSize, Usage = BufferUsage.Storage }
        );

        return new() {
            System = system,
            Compositor = compositor,
            Graph = new(device),
            Opaque = opaque,
            Camera = camera,
            Meshes = meshes,
            Materials = materials,
            Lighting = lighting,
            Culling = culling,
            Allocator = allocator,
            Vertices = device.CreateBuffer(new() { Size = 1024, Usage = BufferUsage.Vertex })
        };
    }

    static void AddMesh(Harness h, float z) {
        var id = h.System.Objects.Add(
            new() {
                Bounds = new(new Vector3(0f, 0f, z), 1f),
                Stages = h.Opaque.Mask,
                FeatureIndex = h.Meshes.Index
            }
        );

        h.System.Objects.Data.Data(h.Meshes.Draws)[id.Index] = new() {
            VertexBuffer = h.Vertices, Count = 3, InstanceCount = 1
        };

        h.Materials.Assign(h.System, id, new("Lit"));
    }

    void Frame(Harness h) {
        var list = device.BeginCommandList();

        h.Allocator.BeginFrame();
        h.Graph.Reset();
        h.Compositor.Build(h.Graph, effects, device);
        h.Graph.Execute(list);

        list.Finish();
        device.GraphicsQueue.Submit([list]);
    }

    /// <inheritdoc />
    public void Dispose() {
        device.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     The culling dispatch runs before the pass that reads what it wrote, with a barrier between.
    /// </summary>
    /// <remarks>
    ///     The edge Forward+ was waiting on, and the reason the compositor had to move onto the render
    ///     graph first. Compute writes the cluster buffer, the shading pass declares it reads it, and
    ///     the ordering and the barrier both fall out of those two lines — where before there was
    ///     nowhere in a compositor for a barrier to come from at all.
    /// </remarks>
    [Fact]
    public void The_cull_dispatches_before_the_shading_pass_and_a_barrier_separates_them() {
        using var h = Build();
        AddMesh(h, -10f);

        ((RenderPassRenderer)((SceneRendererSequence)h.Compositor.Game!).Children[1]).BufferReads.Add("Clusters");

        Frame(h);

        var stream = device.Recorder!.Commands;
        var dispatch = stream.ToList().FindIndex(command => command.Kind == RecordedCommandKind.Dispatch);
        var pass = stream.ToList().FindIndex(command => command.Kind == RecordedCommandKind.BeginRenderPass);

        Assert.True(dispatch >= 0, "nothing dispatched");
        Assert.True(pass > dispatch, "the shading pass did not follow the cull");
        Assert.True(h.Graph.BarrierCount > 0, "nothing was placed between the write and the read");
    }

    /// <summary>The dispatch is the grid's, in the workgroups the shader declares.</summary>
    [Fact]
    public void The_cull_dispatches_one_invocation_per_cluster() {
        using var h = Build();
        AddMesh(h, -10f);

        ((RenderPassRenderer)((SceneRendererSequence)h.Compositor.Game!).Children[1]).BufferReads.Add("Clusters");

        Frame(h);

        var dispatch = Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.Dispatch));

        Assert.Equal(4, dispatch.A);
        Assert.Equal(3, dispatch.B);
        Assert.Equal(6, dispatch.C);
        Assert.Equal(1, h.Culling.Pipelines!.Count);
    }

    /// <summary>
    ///     A cull nothing reads is dropped, like any other pass.
    /// </summary>
    /// <remarks>
    ///     Which is the check on the previous test rather than a curiosity: if the shading pass did
    ///     not have to declare that it reads the clusters, the ordering above would be an accident of
    ///     declaration order rather than a dependency.
    /// </remarks>
    [Fact]
    public void A_cull_whose_clusters_nothing_reads_is_culled_itself() {
        using var h = Build();
        AddMesh(h, -10f);

        Frame(h);

        Assert.Empty(device.Recorder!.OfKind(RecordedCommandKind.Dispatch));
        Assert.Single(device.Recorder.OfKind(RecordedCommandKind.BeginRenderPass));
    }

    /// <summary>
    ///     Clustered lighting does no per-object work at all.
    /// </summary>
    /// <remarks>
    ///     The point of the whole pipeline, and the thing that is easy to claim and easy to get wrong.
    ///     No per-object selection, no block per object, and no descriptor bound per draw — a fragment
    ///     looks itself up in the grid instead.
    /// </remarks>
    [Fact]
    public void The_clustered_path_costs_nothing_per_object() {
        using var h = Build();

        for (var i = 0; i < 8; i++) {
            AddMesh(h, -10f - i);
        }

        h.Lighting.Lights.Add(RenderLight.Point(new(0f, 0f, -10f), 50f, new(1f)));

        Frame(h);

        Assert.Equal(0, h.Lighting.UsedBytes);
        Assert.Equal(8, device.Recorder!.CountOf(RecordedCommandKind.Draw));

        Assert.DoesNotContain(
            device.Recorder.OfKind(RecordedCommandKind.BindDescriptorSet),
            command => command.A == (long)DescriptorSetSlot.PerDraw
        );
    }

    /// <summary>The scene's lights go up once for the frame, however many objects there are.</summary>
    /// <remarks>
    ///     One list for the whole scene is what the culling pass consumes, where the per-object path
    ///     writes a block each. Eight objects and three lights is three records, not twenty-four.
    /// </remarks>
    [Fact]
    public void The_scene_light_list_is_uploaded_once() {
        using var h = Build();

        for (var i = 0; i < 8; i++) {
            AddMesh(h, -10f - i);
        }

        h.Lighting.Lights.Add(RenderLight.Point(new(0f, 0f, -10f), 50f, new(1f)));
        h.Lighting.Lights.Add(RenderLight.Spot(new(1f, 0f, -10f), new(0f, -1f, 0f), 20f, 0.2f, 0.4f, new Color3(1f)));
        h.Lighting.Lights.Add(RenderLight.Directional(new(0f, -1f, 0f), new(1f)));

        Frame(h);

        // The two punctual lights. The sun is a uniform on both paths — it reaches every cluster, so
        // culling it would be paying traversal for something always present.
        Assert.Equal(2, h.Lighting.SceneLightCount);
        Assert.True(h.Lighting.SceneBuffer.IsValid);
        Assert.NotNull(h.Lighting.Sun);
    }

    /// <summary>
    ///     Clustered and unclustered are different variants of the same shader.
    /// </summary>
    /// <remarks>
    ///     A permutation rather than a branch because the two read different bindings: unclustered has
    ///     a per-draw block and clustered has a buffer every fragment indexes, and a runtime branch
    ///     would keep both alive in every variant.
    /// </remarks>
    [Fact]
    public void Clustered_and_unclustered_are_different_variants() {
        using (var clustered = Build()) {
            AddMesh(clustered, -10f);
            Frame(clustered);
        }

        var before = effects.Count;

        using (var plain = Build(clustered: false)) {
            AddMesh(plain, -10f);
            Frame(plain);
        }

        Assert.Equal(before + 1, effects.Count);
    }

    /// <summary>Without a pipeline cache the node declares nothing rather than throwing.</summary>
    [Fact]
    public void A_compute_node_with_no_cache_does_nothing() {
        using var h = Build();
        h.Culling.Pipelines = null;
        AddMesh(h, -10f);

        Frame(h);

        Assert.Empty(device.Recorder!.OfKind(RecordedCommandKind.Dispatch));
    }

    // --- The camera both passes are given ------------------------------------

    /// <summary>
    ///     The published half-tangents are the camera's own projection, derived another way.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The check that matters about <see cref="ClusterGrid.Apply" />, and the reason
    ///         <c>ClusterCulling.rvn</c> divides by a tangent pair rather than multiplying by a
    ///         projection matrix: the culler bins a light into a froxel from these numbers and a
    ///         fragment finds its own froxel from them, so if they disagree with the matrix the
    ///         geometry was projected with, every fragment reads the list that was culled for
    ///         somewhere else.
    ///     </para>
    ///     <para>
    ///         Asserted against the projection matrix rather than against trigonometry repeated here —
    ///         two derivations of one quantity is exactly the failure, so the test has to use the
    ///         other one.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_published_tangents_agree_with_the_cameras_projection() {
        var camera = new RenderCamera(Vector3.Zero, new(0f, 0f, 1f), new(0f, 1f, 0f), MathF.PI / 3f, 16f / 9f, 0.1f, 1000f);
        var parameters = new ParameterCollection();

        ClusterGrid.Apply(parameters, camera, "ClusterCulling");

        var tangents = parameters.Get(ParameterKeys.New<Vector2>("ClusterCulling.tanHalfFov"));
        var projection = camera.Projection;

        foreach (var point in (Vector3[])[new(3f, 2f, -12f), new(-7f, 4f, -40f), new(0.5f, -1.5f, -3f)]) {
            var clip = Matrix4x4.TransformVector4(new(point, 1f), projection);
            var uv = ClusterGrid.UvOf(point, tangents);

            // Exactly, sign included: the grid's coordinates are the rasteriser's. That is what makes
            // "the cluster this fragment is in" mean the same thing as "the cluster over this pixel",
            // and it only became true once the ray pointed the way the view matrix does.
            Assert.Equal(((clip.X / clip.W) * 0.5f) + 0.5f, uv.X, 3);
            Assert.Equal(((clip.Y / clip.W) * 0.5f) + 0.5f, uv.Y, 3);
        }

        Assert.Equal(camera.NearPlane, parameters.Get(ParameterKeys.New<float>("ClusterCulling.nearPlane")));
        Assert.Equal(camera.FarPlane, parameters.Get(ParameterKeys.New<float>("ClusterCulling.farPlane")));
    }

    /// <summary>
    ///     A fragment's cluster is the box the culler tested lights against.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The round trip the whole pass rests on, and the one that was broken.
    ///         <c>Transform.ViewRay</c> pointed down <em>+Z</em> while the engine's view space is
    ///         right-handed, so a cluster's box came out mirrored in z from the light positions
    ///         <c>Touches</c> transformed into the same space. Nearly nothing intersected: every
    ///         cluster list came back empty and the clustered path lit a scene by the sun alone.
    ///     </para>
    ///     <para>
    ///         <strong>A handedness mistake produces an empty result, not a wrong-looking one</strong>,
    ///         which is why it survived being written down on both sides. What catches it is asking
    ///         the two halves the same question about the same point: the fragment says which cluster
    ///         it is in, the culler says what that cluster contains, and a light at the fragment has
    ///         to be inside it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_fragments_cluster_contains_the_fragment() {
        const float near = 0.1f;
        const float far = 1000f;

        var camera = new RenderCamera(Vector3.Zero, new(0f, 0f, -1f), new(0f, 1f, 0f), MathF.PI / 3f, 16f / 9f, near, far);
        var parameters = new ParameterCollection();

        ClusterGrid.Apply(parameters, camera, "ClusterCulling");
        var tangents = parameters.Get(ParameterKeys.New<Vector2>("ClusterCulling.tanHalfFov"));

        foreach (var world in Spread()) {
            // What the vertex stage hands the fragment: the world position through the view matrix.
            var position = Matrix4x4.TransformPosition(world, camera.View);

            // In front of the camera, which under a right-handed view space means a negative z. The
            // rest of the test is meaningless if this is not true — the grid's depths are distances.
            Assert.True(position.Z < 0f, $"{world} is not in front of the camera");

            var cluster = ClusterGrid.Of(position, tangents, near, far);
            var found = false;

            for (var slice = 0; slice < ClusterGrid.Slices && !found; slice++) {
                for (var y = 0; y < ClusterGrid.TilesY && !found; y++) {
                    for (var x = 0; x < ClusterGrid.TilesX && !found; x++) {
                        if (ClusterGrid.Index(x, y, slice) != cluster) {
                            continue;
                        }

                        found = true;
                        var bounds = ClusterGrid.Bounds(x, y, slice, tangents, near, far);

                        // A hair of slack, because the fragment's tile comes from a floor and the
                        // box from the tile's own corners: a point exactly on a boundary is in both.
                        Assert.True(
                            Contains(bounds, position, 1e-3f),
                            $"{world} is in cluster ({x},{y},{slice}) and outside its bounds {bounds.Minimum}..{bounds.Maximum}"
                        );
                    }
                }
            }

            Assert.True(found, $"cluster {cluster} is not in the grid");
        }
    }

    /// <summary>
    ///     Depth decides the slice, so two fragments a decade apart are not in the same cluster.
    /// </summary>
    /// <remarks>
    ///     The other half of the same failure, and the one that would have survived a fix to the sign
    ///     alone: with a positive-z reading of a negative-z position, every fragment's depth clamped
    ///     to the near plane and the whole scene collapsed into slice zero — a grid of 3456 clusters
    ///     using 144 of them.
    /// </remarks>
    [Fact]
    public void Distance_moves_a_fragment_through_the_slices() {
        const float near = 0.1f;
        const float far = 1000f;

        var tangents = new Vector2(1f, 0.5625f);
        var seen = new HashSet<int>();

        for (var distance = 1f; distance < far; distance *= 2f) {
            seen.Add(ClusterGrid.Of(new(0f, 0f, -distance), tangents, near, far));
        }

        // Ten doublings, and the exponential split gives each of them its own slab.
        Assert.Equal(10, seen.Count);
    }

    /// <summary>
    ///     The shader still negates where the mirror does.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A test that reads shader source, which is worth defending. Everything above tests the
    ///         <em>host's copy</em> of the grid — and the host's copy is not what runs. The bug it
    ///         encodes was precisely two sides disagreeing about a sign while each was internally
    ///         consistent, so a test of one side alone would have passed throughout.
    ///     </para>
    ///     <para>
    ///         Narrow on purpose: two lines, each the single place its file states the convention.
    ///         Anything broader would be a test of formatting.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_shader_reads_view_space_the_way_the_host_does() {
        Assert.Contains(
            "func DepthOf(positionVS: float3): float => -positionVS.z",
            Source("Pipeline", "ClusterCulling.rvn"),
            StringComparison.Ordinal
        );

        // The other end of the same convention: the ray the culler builds its boxes from points the
        // way the view matrix does, so a box and a light land in the same half of the world.
        Assert.Contains(
            "return float3(ndc.x * tanHalfFov.x, ndc.y * tanHalfFov.y, -1f)",
            Source("Geometry", "Transform.rvn"),
            StringComparison.Ordinal
        );
    }

    /// <summary>A shipped shader's source, found by walking up rather than by counting directories.</summary>
    static string Source(string folder, string file) {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
            var candidate = Path.Combine(directory.FullName, "Raven", "Library", folder, file);

            if (File.Exists(candidate)) {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException($"Raven/Library/{folder}/{file} was not found above '{AppContext.BaseDirectory}'.");
    }

    /// <summary>A spread of points in front of a camera looking down −Z.</summary>
    static IEnumerable<Vector3> Spread() {
        foreach (var depth in (float[])[0.5f, 3f, 17f, 120f, 800f]) {
            yield return new(0f, 0f, -depth);
            yield return new(depth * 0.4f, depth * 0.2f, -depth);
            yield return new(-depth * 0.7f, -depth * 0.3f, -depth);
        }
    }

    static bool Contains(in BoundingBox box, Vector3 point, float slack) =>
        point.X >= box.Minimum.X - slack && point.X <= box.Maximum.X + slack
        && point.Y >= box.Minimum.Y - slack && point.Y <= box.Maximum.Y + slack
        && point.Z >= box.Minimum.Z - slack && point.Z <= box.Maximum.Z + slack;

    // --- Selecting the clustered variant -------------------------------------

    /// <summary>
    ///     The clustered flag reaches the shader's own permutation, under the shader's own name.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Two naming schemes meet here and nothing joined them. A sub-feature's key is the
    ///         <em>renderer's</em> — <c>Vixen.Clustered</c>, deliberately, because one feature drives
    ///         the same flag across every shader that has it — and a shader's permutation is the
    ///         shader's: <c>ForwardPlus.UseClusteredLights</c>. The effect key is built from the keys
    ///         registered for the shader, read out of a collection the sub-features wrote under
    ///         <em>their</em> names, so registering the shader's key found nothing and registering the
    ///         renderer's key produced a define no compiler could match.
    ///     </para>
    ///     <para>
    ///         Invisible until something compiled from the key, which is why every test of this passed:
    ///         a provider that answers every key alike cannot tell the two apart. What it means in a
    ///         shipping build is that <strong>the clustered variant was never selected</strong> — the
    ///         culler filled a buffer and the shading pass read the uniform-array loop beside it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_clustered_flag_reaches_the_shaders_own_permutation() {
        using var h = Build();

        h.Materials.PermutationKeys["Lit"] = [ForwardPlusKeys.UseClusteredLights];
        h.Materials.PermutationSources[ForwardPlusKeys.UseClusteredLights] = h.Lighting.PermutationKeys[0];

        AddMesh(h, -10f);
        Frame(h);

        var key = Assert.Single(effects.Requests, candidate => candidate.ShaderName == "Lit");

        Assert.Equal("true", Assert.Single(key.Values, value => value.Key == "ForwardPlus.UseClusteredLights").Value);
    }

    /// <summary>And the unclustered frame says so, rather than saying nothing.</summary>
    /// <remarks>
    ///     The pairing that makes the one above mean something: a mapping that wrote <c>true</c>
    ///     whatever the feature said would pass it and light every scene through the wrong loop.
    /// </remarks>
    [Fact]
    public void The_unclustered_frame_selects_the_unclustered_variant() {
        using var h = Build(clustered: false);

        h.Materials.PermutationKeys["Lit"] = [ForwardPlusKeys.UseClusteredLights];
        h.Materials.PermutationSources[ForwardPlusKeys.UseClusteredLights] = h.Lighting.PermutationKeys[0];

        AddMesh(h, -10f);
        Frame(h);

        var key = Assert.Single(effects.Requests, candidate => candidate.ShaderName == "Lit");

        Assert.Equal("false", Assert.Single(key.Values, value => value.Key == "ForwardPlus.UseClusteredLights").Value);
    }

    // --- The culler's own uniforms -------------------------------------------

    /// <summary>
    ///     A compute pass fills its own block, and binds it beside the resources it declared.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The half of a compute node that did not exist. It could declare the buffers and
    ///         textures it read and wrote, and the <em>values</em> beside them had to go through
    ///         <c>OnBind</c> — a host building a buffer, filling it and writing a descriptor by hand.
    ///         <c>ClusterCulling.rvn</c> is the case that made it visible: the camera's half-angle
    ///         tangents, its planes, its view matrix and a light count, none of which could be
    ///         written, which meant <strong>the clustered path could not run in a composed frame at
    ///         all</strong> while every test of it passed.
    ///     </para>
    ///     <para>
    ///         Asserted on the bytes, because that is the only place a wrong offset shows: the block
    ///         is filled from the effect's own plan, so a value written under the right name and
    ///         placed at the wrong offset is a camera the culler reads as something else.
    ///     </para>
    ///     <para>
    ///         <strong>That the block reaches the shader is not this test's claim.</strong> A recording
    ///         backend takes whatever writes it is given and reports nothing about them, so a block
    ///         filled correctly and never bound looks identical from here.
    ///         <c>ClusterCullingDeviceTests</c> is where that is asserted, and it fails when the
    ///         write is dropped.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_compute_pass_fills_its_own_block() {
        using var h = Build();

        // Something has to consume the cluster list, or the graph drops the pass that wrote it — and
        // a dispatch that never ran binds nothing, which would pass half of this for the wrong reason.
        Consume(h);

        h.Culling.Parameters.Set(ParameterKeys.New<int>("ClusterCulling.lightCount"), 3);
        Frame(h);

        var block = h.Culling.Constants;

        Assert.True(h.Culling.UploadCount > 0);
        Assert.Equal(96, block.Length);

        var tangents = MemoryMarshal.Read<Vector2>(block);
        var vertical = MathF.Tan(Camera.FieldOfView * 0.5f);

        Assert.Equal(vertical * Camera.AspectRatio, tangents.X, 4);
        Assert.Equal(vertical, tangents.Y, 4);

        Assert.Equal(Camera.NearPlane, MemoryMarshal.Read<float>(block[8..]), 4);
        Assert.Equal(Camera.FarPlane, MemoryMarshal.Read<float>(block[12..]), 4);
        Assert.Equal(Camera.View, MemoryMarshal.Read<Matrix4x4>(block[16..]));
        Assert.Equal(3, MemoryMarshal.Read<int>(block[80..]));

        // And a set went down for the pass to read it through.
        var sets = device.Recorder!.OfKind(RecordedCommandKind.BindDescriptorSet)
            .Count(command => command.A == (long)DescriptorSetSlot.PerMaterial);

        Assert.True(sets > 0);
    }

    /// <summary>Declares that the shading pass reads what the culler wrote, so the cull survives.</summary>
    static void Consume(Harness h) =>
        ((RenderPassRenderer)((SceneRendererSequence)h.Compositor.Game!).Children[1]).BufferReads.Add("Clusters");

    /// <summary>A block nobody changed is not uploaded again.</summary>
    /// <remarks>
    ///     What <see cref="ParameterCollection.Version" /> is for, at the one place it would be easy
    ///     to lose: a camera that has not moved should cost a comparison per frame and not a write.
    /// </remarks>
    [Fact]
    public void A_block_nobody_changed_is_not_uploaded_again() {
        using var h = Build();
        Consume(h);

        Frame(h);
        var first = h.Culling.UploadCount;

        Frame(h);
        Assert.Equal(first, h.Culling.UploadCount);

        // And a value that did change is.
        h.Culling.Parameters.Set(ParameterKeys.New<int>("ClusterCulling.lightCount"), 17);
        Frame(h);

        Assert.True(h.Culling.UploadCount > first);
    }

    /// <summary>A compute node naming a buffer nothing bound is refused by name.</summary>
    [Fact]
    public void A_compute_node_naming_an_unbound_buffer_is_refused() {
        using var h = Build();
        h.Culling.BufferReads.Add("NotBound");

        var thrown = Assert.Throws<CompositorBindingException>(() => Frame(h));

        Assert.Equal("buffer", thrown.Kind);
        Assert.Equal("NotBound", thrown.Name);
    }
}
