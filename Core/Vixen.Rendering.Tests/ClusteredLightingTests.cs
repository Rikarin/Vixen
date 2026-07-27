// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Features;
using Vixen.Shaders;
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

    static Effect Compiled(EffectKey key) =>
        new() {
            Key = key,
            Stages = key.ShaderName.Contains("Culling", StringComparison.Ordinal)
                ? [new(ShaderStage.Compute, [1, 2, 3, 4], "main")]
                : [
                    new(ShaderStage.Vertex, [1, 2, 3, 4], "main"),
                    new(ShaderStage.Fragment, [5, 6, 7, 8], "main")
                ]
        };

    sealed class AlwaysCompiles : IEffectProvider {
        public Effect? TryGet(EffectKey key) => Compiled(key);
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
        public required BufferHandle Vertices { get; init; }

        public void Dispose() {
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
        effects.AddProvider(new AlwaysCompiles());

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

        culling.Reads.Add("SceneLights");
        culling.Writes.Add("Clusters");

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

    /// <summary>A compute node naming a buffer nothing bound is refused by name.</summary>
    [Fact]
    public void A_compute_node_naming_an_unbound_buffer_is_refused() {
        using var h = Build();
        h.Culling.Reads.Add("NotBound");

        var thrown = Assert.Throws<CompositorBindingException>(() => Frame(h));

        Assert.Equal("buffer", thrown.Kind);
        Assert.Equal("NotBound", thrown.Name);
    }
}
