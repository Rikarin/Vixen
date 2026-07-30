// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Serialization;
using Vixen.Core.Yaml;
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
///     The compositor as an authored document — docs/plan/06's third idea, as a file.
/// </summary>
/// <remarks>
///     The claim is that a frame's structure is <em>data</em>: this test builds no renderer tree in
///     C# at all. It parses the YAML below, binds four names to resources a device made, and draws a
///     frame — two passes, a shadow atlas and a main pass with two stages in it, all of which exist
///     because the document says so.
/// </remarks>
public class CompositorAssetTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });
    readonly EffectSystem effects = new();

    const string Document = """
        version: 2
        resources:
          - name: SceneColour
            format: Rgba16Float
            usage: ColourTarget, Sampled
          - name: SceneDepth
            format: Depth32Float
            usage: DepthStencilTarget
        stages:
          - name: ShadowCaster
            cull: Front
            depthBias: 1
            depthBiasSlope: 2
          - name: Opaque
          - name: Transparent
            sortMode: BackToFront
            blend: AlphaBlend
            depth: TestOnly
        game: !Sequence
          name: Frame
          children:
            - !ShadowMap
              name: Shadows
              stage: ShadowCaster
              atlas: ShadowAtlas
              cascadeCount: 2
              resolution: 256
              shadowDistance: 100
            - !RenderPass
              name: Main
              colourTargets: [SceneColour]
              depthTarget: SceneDepth
              reads: [ShadowAtlas]
              children:
                - !SingleStage
                  name: OpaqueDraw
                  view: Camera
                  stage: Opaque
                - !SingleStage
                  name: TransparentDraw
                  view: Camera
                  stage: Transparent
        """;

    /// <summary>A frame that culls on the device, as a document says it.</summary>
    /// <remarks>
    ///     Two nodes and two flags. What the file cannot say is what the resources are — a visibility
    ///     group holds device memory across frames — so the host supplies those and the document
    ///     decides where the passes go, which is the division <c>descriptors</c> and <c>samplers</c>
    ///     already have.
    /// </remarks>
    const string CullingDocument = """
        version: 2
        resources:
          - name: SceneColour
            format: Rgba16Float
            usage: ColourTarget, Sampled
          - name: SceneDepth
            format: Depth32Float
            usage: DepthStencilTarget, Sampled
        stages:
          - name: Opaque
        game: !Sequence
          name: Frame
          children:
            - !GpuCulling
              name: Culling
              readBack: false
              indirectDraws: true
            - !RenderPass
              name: Main
              colourTargets: [SceneColour]
              depthTarget: SceneDepth
              children:
                - !SingleStage
                  name: OpaqueDraw
                  view: Camera
                  stage: Opaque
            - !HiZ
              name: Pyramid
              depth: SceneDepth
        """;

    /// <summary>The same frame, culled in two phases, which is four nodes in an order.</summary>
    /// <remarks>
    ///     The whole of two-phase culling as a document sees it: cull, draw, reduce, cull again, draw
    ///     what the second answer found. Nothing in the file says "two-phase" — the ordering
    ///     <em>is</em> the feature, and the second culling node's position after the reduction is what
    ///     makes its answer about this frame's depth rather than the last one's.
    /// </remarks>
    const string TwoPhaseDocument = """
        version: 2
        resources:
          - name: SceneColour
            format: Rgba16Float
            usage: ColourTarget, Sampled
          - name: SceneDepth
            format: Depth32Float
            usage: DepthStencilTarget, Sampled
        stages:
          - name: Opaque
        game: !Sequence
          name: Frame
          children:
            - !GpuCulling
              name: Culling
              readBack: false
              indirectDraws: true
            - !RenderPass
              name: Main
              colourTargets: [SceneColour]
              depthTarget: SceneDepth
              children:
                - !SingleStage
                  name: OpaqueDraw
                  view: Camera
                  stage: Opaque
            - !HiZ
              name: Pyramid
              depth: SceneDepth
            - !GpuCulling
              name: LateCulling
              phase: Late
              indirectDraws: true
            - !RenderPass
              name: Late
              colourTargets: [SceneColour]
              depthTarget: SceneDepth
              load: Load
              depthLoad: Load
              children:
                - !SingleStage
                  name: LateOpaqueDraw
                  view: Camera
                  stage: Opaque
            - !HiZ
              name: FinalPyramid
              depth: SceneDepth
        """;

    // --- Fixture ------------------------------------------------------------

    static Effect Compiled(EffectKey key) =>
        new() {
            Key = key,
            Stages = [
                new(ShaderStage.Vertex, [1, 2, 3, 4], "main"),
                new(ShaderStage.Fragment, [5, 6, 7, 8], "main")
            ]
        };

    sealed class AlwaysCompiles : IEffectProvider {
        public Effect? TryGet(EffectKey key) => Compiled(key);
    }

    /// <summary>The virtualized path as a document describes it: traverse, then draw and shade.</summary>
    /// <remarks>
    ///     Two nodes for what is one system, because the placement decision genuinely is two: the
    ///     traversal has to run before the draw its answer feeds, and the draw has to share the depth
    ///     the classic geometry is in. Everything between the draw, the binning and the shading is one
    ///     node, because their order is not something a file should be able to get wrong.
    /// </remarks>
    const string ClusterDocument = """
        version: 2
        resources:
          - name: SceneColour
            format: Rgba16Float
            usage: ColourTarget, Sampled, Storage
          - name: SceneDepth
            format: Depth32Float
            usage: DepthStencilTarget, Sampled
        stages:
          - name: Opaque
        game: !Sequence
          name: Frame
          children:
            - !ClusterCulling
              name: Traversal
            - !VisibilityBuffer
              name: Visibility
              output: Identities
              depth: SceneDepth
              colour: SceneColour
        """;

    /// <summary>
    ///     A document can describe the virtualized path, and a host that supplied nothing gets nodes
    ///     that do nothing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The asymmetry this removes: the object cull has been placeable by a document since phase
    ///         3, and its sibling — the same decision one level down the same hierarchy — could only be
    ///         assembled in code. A path that cannot be written down is a path every host has to
    ///         reimplement.
    ///     </para>
    ///     <para>
    ///         Both halves are asserted because both are the contract. A document names placement and a
    ///         host supplies the device memory, so the same file has to build on a project with no
    ///         virtualized geometry in it — and build nodes that draw nothing rather than nodes that
    ///         throw.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_document_can_place_the_virtualized_path() {
        using var h = Build();
        using var clusters = new GpuClusterVisibility(device);
        using var pages = new MeshletPagePool(device, new MemoryMeshletPageSource(), 4, 8 * 1024);
        using var raster = new GpuClusterRaster(device);
        using var tiles = new GpuVisibilityTiles(device);
        using var resolve = new GpuClusterResolve(device);

        h.Builder.Clusters = clusters;
        h.Builder.Pages = pages;
        h.Builder.Raster = raster;
        h.Builder.Tiles = tiles;
        h.Builder.Resolve = resolve;

        var compositor = h.Builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(ClusterDocument));
        var children = Assert.IsType<SceneRendererSequence>(compositor.Game).Children;

        var traversal = Assert.IsType<ClusterCullingRenderer>(children[0]);
        var visibility = Assert.IsType<VisibilityBufferRenderer>(children[1]);

        Assert.Same(clusters, traversal.Visibility);
        Assert.Same(pages, traversal.Pages);
        Assert.Same(raster, traversal.Raster);

        Assert.Same(raster, visibility.Raster);
        Assert.Same(tiles, visibility.Tiles);
        Assert.Same(resolve, visibility.Resolve);

        // The names the document chose, which is the whole of what it decides beyond placement.
        Assert.Equal("Identities", visibility.Output);
        Assert.Equal("SceneDepth", visibility.Depth);
        Assert.Equal("SceneColour", visibility.Colour);
    }

    /// <summary>The same document on a host with no virtualized geometry builds nodes that do nothing.</summary>
    [Fact]
    public void The_same_document_builds_on_a_host_that_has_no_clusters() {
        using var h = Build();

        var compositor = h.Builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(ClusterDocument));
        var children = Assert.IsType<SceneRendererSequence>(compositor.Game).Children;

        Assert.Null(Assert.IsType<ClusterCullingRenderer>(children[0]).Visibility);
        Assert.Null(Assert.IsType<VisibilityBufferRenderer>(children[1]).Raster);
    }

    sealed class Harness : IDisposable {
        public required RenderSystem System { get; init; }
        public required CompositorBuilder Builder { get; init; }
        public required RenderGraph Graph { get; init; }
        public required RenderView Camera { get; init; }
        public required MeshRenderFeature Meshes { get; init; }
        public required MaterialRenderFeature Materials { get; init; }
        public required BufferHandle Vertices { get; init; }

        public void Dispose() {
            Graph.DisposePool();
            System.Dispose();
        }
    }

    ImportedTexture Imported(PixelFormat format, TextureUsage usage, string name) {
        var description = new TextureDescription(format, 512, 512, usage | TextureUsage.Sampled, Name: name);
        var texture = device.CreateTexture(description);

        return new(texture, device.CreateTextureView(texture), description);
    }

    Harness Build() {
        var system = new RenderSystem();

        var meshes = new MeshRenderFeature {
            Pipelines = new(device),
            Describer = new EffectPipelineDescriber(device)
        };

        var materials = new MaterialRenderFeature { Effects = effects };
        meshes.Add(materials);
        system.AddFeature(meshes);
        effects.AddProvider(new AlwaysCompiles());

        var view = Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, -1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 1000f);

        var camera = new RenderView("camera") { Position = Vector3.Zero, Frustum = new(view * projection) };

        var builder = new CompositorBuilder(system);
        builder.Views["Camera"] = camera;

        return new() {
            System = system,
            Builder = builder,
            Graph = new(device),
            Camera = camera,
            Meshes = meshes,
            Materials = materials,
            Vertices = device.CreateBuffer(new() { Size = 1024, Usage = BufferUsage.Vertex })
        };
    }

    /// <summary>
    ///     A document turns GPU culling on: the group, the pyramid, the arguments and the features.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The claim this file makes, applied to the one feature that reaches past its own pass.
    ///         Culling on the device is not a pass a document can simply place — the render system's
    ///         visibility has to <em>become</em> the group, and every feature that draws indirectly
    ///         has to be handed the arguments — and a host that placed the node and forgot either
    ///         gets a frame that culls on the CPU or draws everything, with nothing to say why.
    ///     </para>
    ///     <para>
    ///         So the builder makes those assignments, and this is what says it did. Twelve lines of
    ///         YAML and three objects the host owns, against the eight steps assembling it by hand
    ///         used to take.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_document_can_turn_gpu_culling_on() {
        using var h = Build();
        using var visibility = new GpuVisibilityGroup(device);
        using var pyramid = new HiZPyramid(device);
        using var arguments = new GpuDrawArguments(device);

        h.Builder.Visibility = visibility;
        h.Builder.Occluders = pyramid;
        h.Builder.Arguments = arguments;

        var compositor = h.Builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(CullingDocument));
        var children = Assert.IsType<SceneRendererSequence>(compositor.Game).Children;

        var culling = Assert.IsType<GpuCullingRenderer>(children[0]);
        var reduce = Assert.IsType<HiZRenderer>(children[2]);

        // The node holds what the document asked for.
        Assert.Same(visibility, culling.Visibility);
        Assert.Same(arguments, culling.Arguments);
        Assert.Same(pyramid, reduce.Pyramid);
        Assert.Equal("SceneDepth", reduce.Depth);

        // And the two assignments a document cannot make itself but a frame does not work without.
        Assert.Same(visibility, h.System.Visibility);
        Assert.Same(pyramid, visibility.Occluders);
        Assert.Same(arguments, h.Meshes.Arguments);
        Assert.False(visibility.ReadBack);
    }

    /// <summary>
    ///     The same document on a host that supplied nothing builds nodes that do nothing.
    /// </summary>
    /// <remarks>
    ///     One document across targets, which is the point of the division: a device with no compute
    ///     gets the CPU path, and the file does not change. The nodes are still there, and still
    ///     named, so a frame debugger shows the same tree either way.
    /// </remarks>
    [Fact]
    public void A_host_that_supplies_nothing_gets_the_cpu_path() {
        using var h = Build();

        var compositor = h.Builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(CullingDocument));
        var children = Assert.IsType<SceneRendererSequence>(compositor.Game).Children;

        Assert.Null(Assert.IsType<GpuCullingRenderer>(children[0]).Visibility);
        Assert.Null(Assert.IsType<HiZRenderer>(children[2]).Pyramid);

        Assert.IsType<VisibilityGroup>(h.System.Visibility);
        Assert.Null(h.Meshes.Arguments);
    }

    /// <summary>
    ///     A second culling node makes the frame two-phase, and sizes the rings that has to grow.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Three consequences a document states by placing one node, and each of them is a bug
    ///         somewhere else if the builder does not draw it. The group has to know a late dispatch
    ///         will be asked for, because that is what makes it pack a second set of view records. The
    ///         readback has to be off, because two dispatches straddling a set of draws cannot exist
    ///         on a path that submits and waits before any of them are recorded. And two nodes of a
    ///         kind in one document are two descriptor-set rewrites before one submission, which
    ///         sizing a ring to frames in flight alone does not cover.
    ///     </para>
    ///     <para>
    ///         The ring depths are counted from the nodes rather than inferred from the late one,
    ///         because a document may reduce twice without culling twice — as this one does, so that
    ///         the frame after has depth including the late draws to test against.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_second_culling_node_makes_the_frame_two_phase() {
        using var h = Build();
        using var visibility = new GpuVisibilityGroup(device);
        using var pyramid = new HiZPyramid(device);
        using var arguments = new GpuDrawArguments(device);

        h.Builder.Visibility = visibility;
        h.Builder.Occluders = pyramid;
        h.Builder.Arguments = arguments;

        var compositor = h.Builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(TwoPhaseDocument));
        var children = Assert.IsType<SceneRendererSequence>(compositor.Game).Children;

        Assert.Equal(CullPhase.Main, Assert.IsType<GpuCullingRenderer>(children[0]).Phase);
        Assert.Equal(CullPhase.Late, Assert.IsType<GpuCullingRenderer>(children[3]).Phase);

        // The late node draws from the same argument buffer, because the late draws are the same
        // draws reading a buffer whose contents have changed.
        Assert.Same(arguments, Assert.IsType<GpuCullingRenderer>(children[3]).Arguments);
        Assert.Same(arguments, h.Meshes.Arguments);

        Assert.True(visibility.TwoPhase);
        Assert.False(visibility.ReadBack);

        // Two reductions and two argument passes in one frame, so two rings deep.
        Assert.Equal(2, pyramid.BuildsPerFrame);
        Assert.Equal(2, arguments.DispatchesPerFrame);
    }

    /// <summary>
    ///     The one-phase document leaves everything one deep, including after a two-phase build.
    /// </summary>
    /// <remarks>
    ///     Assigned rather than raised, so that an editor reloading a document that dropped its late
    ///     node does not keep a ring sized for the node that used to be there — which is a spare
    ///     descriptor set and, more to the point, a number that no longer describes the frame.
    /// </remarks>
    [Fact]
    public void Dropping_the_late_node_puts_the_frame_back_to_one_phase() {
        using var h = Build();
        using var visibility = new GpuVisibilityGroup(device);
        using var pyramid = new HiZPyramid(device);
        using var arguments = new GpuDrawArguments(device);

        h.Builder.Visibility = visibility;
        h.Builder.Occluders = pyramid;
        h.Builder.Arguments = arguments;

        h.Builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(TwoPhaseDocument));
        h.Builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(CullingDocument));

        Assert.False(visibility.TwoPhase);
        Assert.Equal(1, pyramid.BuildsPerFrame);
        Assert.Equal(1, arguments.DispatchesPerFrame);
    }

    /// <summary>A document that asks for no indirect draws gets none, and says so twice.</summary>
    /// <remarks>
    ///     The flag is separate from <c>readBack</c> because the memory is: twenty bytes per object
    ///     per view is a cost a project chooses. A node with the arguments unset and the features
    ///     unassigned is the same frame it was before, one dispatch heavier.
    /// </remarks>
    [Fact]
    public void Indirect_draws_are_asked_for_separately() {
        using var h = Build();
        using var visibility = new GpuVisibilityGroup(device);
        using var arguments = new GpuDrawArguments(device);

        h.Builder.Visibility = visibility;
        h.Builder.Arguments = arguments;

        var document = CullingDocument.Replace("indirectDraws: true", "indirectDraws: false", StringComparison.Ordinal);
        var compositor = h.Builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(document));
        var children = Assert.IsType<SceneRendererSequence>(compositor.Game).Children;

        Assert.Null(Assert.IsType<GpuCullingRenderer>(children[0]).Arguments);
        Assert.Null(h.Meshes.Arguments);
        Assert.Same(visibility, h.System.Visibility);
    }

    static void AddMesh(Harness h, float z, Material material, RenderStageMask stages) {
        var id = h.System.Objects.Add(
            new() {
                Bounds = new(new Vector3(0f, 0f, z), 1f),
                Stages = stages,
                FeatureIndex = h.Meshes.Index
            }
        );

        h.System.Objects.Data.Data(h.Meshes.Draws)[id.Index] = new() {
            VertexBuffer = h.Vertices, Count = 3, InstanceCount = 1
        };

        h.Materials.Assign(h.System, id, material);
    }

    /// <summary>Builds the compositor an asset describes, and lends it the host-owned textures.</summary>
    /// <remarks>
    ///     <para>
    ///         Two imports, for the two reasons anything is imported. The shadow atlas outlives the
    ///         frame. The scene colour <em>is</em> the swapchain image here, and the frame's last
    ///         target has to be one: a transient nothing reads afterwards is a pass the graph is
    ///         right to cull, and the only thing that makes the final pass matter is that its target
    ///         belongs to somebody outside the graph.
    ///     </para>
    ///     <para>
    ///         The document declares <c>SceneColour</c> too, and the import wins — which is the point
    ///         of that rule: the same document runs against a swapchain in one preset and an
    ///         offscreen buffer in another without being edited.
    ///     </para>
    /// </remarks>
    GraphicsCompositor Compose(Harness h, GraphicsCompositorAsset asset) {
        var compositor = h.Builder.Build(asset);
        compositor.FrameSize = new(512, 512);

        compositor.Imports["ShadowAtlas"] =
            Imported(PixelFormat.Depth32Float, TextureUsage.DepthStencilTarget, "ShadowAtlas");

        compositor.Imports["SceneColour"] =
            Imported(PixelFormat.Rgba16Float, TextureUsage.ColourTarget, "SceneColour");

        return compositor;
    }

    void Frame(Harness h, GraphicsCompositor compositor) {
        var list = device.BeginCommandList();

        h.Graph.Reset();
        compositor.Build(h.Graph, effects, device);
        h.Graph.Execute(list);

        list.Finish();
        device.GraphicsQueue.Submit([list]);
    }

    /// <inheritdoc />
    public void Dispose() {
        device.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- Reading ------------------------------------------------------------

    /// <summary>
    ///     A document parses into the tree it describes, with the tags choosing the node types.
    /// </summary>
    /// <remarks>
    ///     A <c>[DataContract]</c> name per node type is the YAML tag, which is how the rest of the
    ///     engine does polymorphism in a file — one attribute defines the tag, the type and the
    ///     serializer, with no registration table to keep in sync.
    /// </remarks>
    [Fact]
    public void A_document_parses_into_the_tree_its_tags_describe() {
        var asset = YamlSerializer.Parse<GraphicsCompositorAsset>(Document);

        Assert.Equal(2, asset.Version);
        Assert.Equal(3, asset.Stages.Length);

        var root = Assert.IsType<SequenceAsset>(asset.Game);
        Assert.Equal(2, root.Children.Length);

        var shadows = Assert.IsType<ShadowMapAsset>(root.Children[0]);
        Assert.Equal(2, shadows.CascadeCount);
        Assert.Equal(100f, shadows.ShadowDistance);

        var main = Assert.IsType<RenderPassAsset>(root.Children[1]);
        Assert.Equal(["SceneColour"], main.ColourTargets);
        Assert.Equal("SceneDepth", main.DepthTarget);
        Assert.Equal(2, main.Children.Length);

        Assert.Equal("Transparent", Assert.IsType<SingleStageAsset>(main.Children[1]).Stage);
    }

    /// <summary>Stage settings survive the file, including the ones a preset stands for.</summary>
    [Fact]
    public void Stage_settings_come_from_the_document() {
        var asset = YamlSerializer.Parse<GraphicsCompositorAsset>(Document);
        using var h = Build();

        h.Builder.Build(asset);

        var caster = h.Builder.Stages["ShadowCaster"];
        var transparent = h.Builder.Stages["Transparent"];

        Assert.Equal(CullMode.Front, caster.Rasterizer.Cull);
        Assert.Equal(1f, caster.Rasterizer.DepthBias);
        Assert.Equal(2f, caster.Rasterizer.DepthBiasSlope);

        Assert.Equal(RenderSortMode.BackToFront, transparent.SortMode);
        Assert.Equal(BlendState.AlphaBlend, transparent.Blend);
        Assert.False(transparent.DepthStencil.DepthWrite);

        // The stage the document said nothing about keeps every default.
        Assert.Equal(BlendState.Opaque, h.Builder.Stages["Opaque"].Blend);
    }

    /// <summary>A document survives being written back out and read again.</summary>
    /// <remarks>
    ///     What an editor does every time somebody drags a node. The tags have to round-trip too, or
    ///     saving turns a shadow-map node into whatever the base type would deserialise as.
    /// </remarks>
    [Fact]
    public void A_document_round_trips_through_the_serializer() {
        var original = YamlSerializer.Parse<GraphicsCompositorAsset>(Document);
        var written = YamlSerializer.ToYaml(original);
        var reread = YamlSerializer.Parse<GraphicsCompositorAsset>(written);

        Assert.Equal(original.Stages.Length, reread.Stages.Length);

        var root = Assert.IsType<SequenceAsset>(reread.Game);
        Assert.IsType<ShadowMapAsset>(root.Children[0]);

        var main = Assert.IsType<RenderPassAsset>(root.Children[1]);
        Assert.Equal(["SceneColour"], main.ColourTargets);
        Assert.Equal(2, main.Children.Length);

        // The culling nodes too, whose flags are the part a round trip is most likely to drop: a
        // bool that defaults to true and was written false is exactly what a serialiser that omits
        // defaults gets wrong in the direction nobody notices.
        var culling = Assert.IsType<GpuCullingAsset>(
            Assert.IsType<SequenceAsset>(
                YamlSerializer.Parse<GraphicsCompositorAsset>(
                    YamlSerializer.ToYaml(YamlSerializer.Parse<GraphicsCompositorAsset>(CullingDocument))
                ).Game
            ).Children[0]
        );

        Assert.False(culling.ReadBack);
        Assert.True(culling.IndirectDraws);
        Assert.Equal(CullPhase.Main, culling.Phase);

        // And the phase, which is the one field whose loss turns a two-phase frame into a frame that
        // culls twice and draws everything twice — a document that reads back as valid.
        var phases = Assert.IsType<SequenceAsset>(
            YamlSerializer.Parse<GraphicsCompositorAsset>(
                YamlSerializer.ToYaml(YamlSerializer.Parse<GraphicsCompositorAsset>(TwoPhaseDocument))
            ).Game
        ).Children;

        Assert.Equal(CullPhase.Main, Assert.IsType<GpuCullingAsset>(phases[0]).Phase);
        Assert.Equal(CullPhase.Late, Assert.IsType<GpuCullingAsset>(phases[3]).Phase);
    }

    // --- Building -----------------------------------------------------------

    /// <summary>
    ///     The document draws a frame, and every pass in it exists because the file says so.
    /// </summary>
    /// <remarks>
    ///     No renderer tree is built in C# anywhere in this test. Two passes — the shadow atlas and
    ///     the main pass — two cascades' viewports, and two stages' worth of draws in the second
    ///     pass, all of it read from six lines of YAML.
    /// </remarks>
    [Fact]
    public void An_authored_document_draws_a_frame() {
        var asset = YamlSerializer.Parse<GraphicsCompositorAsset>(Document);
        using var h = Build();

        var compositor = Compose(h, asset);
        var everywhere = h.Builder.Stages.Values.Aggregate(RenderStageMask.None, (mask, stage) => mask | stage.Mask);

        AddMesh(h, -10f, new Material("Lit"), everywhere);

        Frame(h, compositor);

        // The shadow atlas and the main pass.
        Assert.Equal(2, device.Recorder!.CountOf(RecordedCommandKind.BeginRenderPass));

        // Two cascades, each with its own tile.
        Assert.Equal(2, device.Recorder.CountOf(RecordedCommandKind.SetViewport));

        // Two cascades plus the opaque and transparent stages of the main pass.
        Assert.Equal(4, device.Recorder.CountOf(RecordedCommandKind.Draw));
    }

    /// <summary>
    ///     An opaque and a transparent stage from the file are two pipelines.
    /// </summary>
    /// <remarks>
    ///     The authored blend and depth presets reaching the driver. Same shader, same attachments,
    ///     different pipelines — which is the file actually deciding something rather than being
    ///     parsed and ignored.
    /// </remarks>
    [Fact]
    public void The_authored_stage_state_reaches_the_pipeline() {
        var asset = YamlSerializer.Parse<GraphicsCompositorAsset>(Document);
        using var h = Build();

        var compositor = Compose(h, asset);
        var opaque = h.Builder.Stages["Opaque"];
        var transparent = h.Builder.Stages["Transparent"];

        AddMesh(h, -10f, new Material("Lit"), opaque.Mask | transparent.Mask);

        Frame(h, compositor);

        Assert.Equal(2, h.Meshes.Pipelines!.Count);
    }

    /// <summary>
    ///     Views are declared by the tree, so the shadow node's cascades are culled for.
    /// </summary>
    /// <remarks>
    ///     The collect phase working through an authored graph: nobody registered two cascade views
    ///     with the render system, and there are three views in the frame because the document has a
    ///     shadow node with two cascades and a pass that draws from a camera.
    /// </remarks>
    [Fact]
    public void The_frames_views_are_a_consequence_of_the_document() {
        var asset = YamlSerializer.Parse<GraphicsCompositorAsset>(Document);
        using var h = Build();

        var compositor = Compose(h, asset);
        compositor.Collect();

        Assert.Equal(3, compositor.Views.Count);
        Assert.Contains(h.Camera, compositor.Views);
    }

    // --- Refusals -----------------------------------------------------------

    /// <summary>A target neither declared nor imported names the node, the kind and the name.</summary>
    /// <remarks>
    ///     Declaring what it can and skipping the rest would produce a frame missing a pass and
    ///     report nothing — the failure that takes a day to find. It surfaces at build time rather
    ///     than at parse time because a name is only wrong once you know what the frame has, and the
    ///     frame is what the host contributes to.
    /// </remarks>
    [Fact]
    public void A_target_that_is_neither_declared_nor_imported_is_refused_by_name() {
        var asset = YamlSerializer.Parse<GraphicsCompositorAsset>(Document);
        using var h = Build();

        var compositor = Compose(h, asset);
        compositor.Resources.Clear();

        var thrown = Assert.Throws<CompositorBindingException>(() => Frame(h, compositor));

        // SceneColour survives — the host imported it — and SceneDepth, which only the document
        // declared, is the one that is now bound to nothing.
        Assert.Equal("Main", thrown.Node);
        Assert.Equal("target", thrown.Kind);
        Assert.Equal("SceneDepth", thrown.Name);
    }

    /// <summary>
    ///     The document's own resources are what the frame renders into.
    /// </summary>
    /// <remarks>
    ///     The half of "the frame is data" that version 1 could not express. A document that can say
    ///     "a half-resolution R11G11B10 target" can describe a post-processing chain; one that could
    ///     only name textures somebody else made could describe the order of passes and nothing about
    ///     what flows between them.
    /// </remarks>
    [Fact]
    public void The_document_declares_the_targets_it_renders_into() {
        var asset = YamlSerializer.Parse<GraphicsCompositorAsset>(Document);
        using var h = Build();

        Assert.Equal(2, asset.Resources.Length);
        Assert.Equal(PixelFormat.Rgba16Float, asset.Resources[0].Format);

        var compositor = Compose(h, asset);
        var frame = compositor.Build(h.Graph, effects, device);

        // Two imports plus the one declaration the imports did not already cover.
        Assert.Equal(3, h.Graph.ResourceCount);
        Assert.True(frame.Has("SceneColour"));
        Assert.True(frame.Has("ShadowAtlas"));
    }

    /// <summary>A scaled resource is a fraction of the frame, not a fixed size.</summary>
    /// <remarks>
    ///     What lets a bloom chain authored at half resolution stay half resolution on a window
    ///     nobody anticipated. Floored at one, so a chain of halvings ends at 1×1 rather than at a
    ///     zero-sized texture the backend refuses.
    /// </remarks>
    [Theory]
    [InlineData(1f, 512, 512)]
    [InlineData(0.5f, 256, 256)]
    [InlineData(0.001f, 1, 1)]
    public void A_scaled_resource_follows_the_frame_size(float scale, int width, int height) {
        var declared = new RenderResourceAsset { Name = "Bloom", Scale = scale };
        var description = declared.Describe(new(512, 512));

        Assert.Equal(width, description.Width);
        Assert.Equal(height, description.Height);
    }

    /// <summary>A node naming a stage that the document does not declare is refused too.</summary>
    [Fact]
    public void A_stage_the_document_never_declared_is_refused() {
        var asset = YamlSerializer.Parse<GraphicsCompositorAsset>(Document) with { Stages = [] };
        using var h = Build();

        var thrown = Assert.Throws<CompositorBindingException>(() => h.Builder.Build(asset));

        Assert.Equal("stage", thrown.Kind);
    }

    /// <summary>A document from a later editor is refused by version rather than half-read.</summary>
    [Fact]
    public void A_future_version_is_refused() {
        var asset = YamlSerializer.Parse<GraphicsCompositorAsset>(Document) with { Version = 3 };
        using var h = Build();

        var thrown = Assert.Throws<NotSupportedException>(() => h.Builder.Build(asset));

        Assert.Contains("version 3", thrown.Message, StringComparison.Ordinal);
    }

    // --- The baked form -----------------------------------------------------

    /// <summary>
    ///     A compositor survives the binary path a content build bakes and a runtime reads.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The other half of "the frame is data". The editor writes the YAML above; the content
    ///         build serialises the same record graph into a chunk; the runtime reads the chunk
    ///         through <c>AssetManager</c> and never links a parser. The types carry
    ///         <c>[DataContract]</c>, so the serializer is generated at compile time and nothing is
    ///         discovered by reflection — which is what makes this work on a trimmed NativeAOT build.
    ///     </para>
    ///     <para>
    ///         The tags have to survive too: a node graph whose types were resolved by a YAML tag has
    ///         to come back out of bytes as the same types, or a baked build gets a compositor whose
    ///         shadow node deserialised as something else.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_compositor_survives_the_baked_binary_form() {
        var authored = YamlSerializer.Parse<GraphicsCompositorAsset>(Document);

        var baked = Serializer.ToBytes(authored);
        var loaded = Serializer.Read<GraphicsCompositorAsset>(baked);

        Assert.Equal(2, loaded.Version);
        Assert.Equal(3, loaded.Stages.Length);

        var root = Assert.IsType<SequenceAsset>(loaded.Game);
        var shadows = Assert.IsType<ShadowMapAsset>(root.Children[0]);
        var main = Assert.IsType<RenderPassAsset>(root.Children[1]);

        Assert.Equal(2, shadows.CascadeCount);
        Assert.Equal(["SceneColour"], main.ColourTargets);
        Assert.Equal("Transparent", Assert.IsType<SingleStageAsset>(main.Children[1]).Stage);
    }

    /// <summary>And the baked form builds and draws the same frame the authored one did.</summary>
    /// <remarks>
    ///     The claim that matters for a shipping build: what comes out of the bundle is not merely
    ///     structurally equal to what went in, it renders the same.
    /// </remarks>
    [Fact]
    public void The_baked_form_draws_the_same_frame() {
        var authored = YamlSerializer.Parse<GraphicsCompositorAsset>(Document);
        var loaded = Serializer.Read<GraphicsCompositorAsset>(Serializer.ToBytes(authored));

        using var h = Build();
        var compositor = Compose(h, loaded);
        var everywhere = h.Builder.Stages.Values.Aggregate(RenderStageMask.None, (mask, stage) => mask | stage.Mask);

        AddMesh(h, -10f, new Material("Lit"), everywhere);

        Frame(h, compositor);

        Assert.Equal(2, device.Recorder!.CountOf(RecordedCommandKind.BeginRenderPass));
        Assert.Equal(2, device.Recorder.CountOf(RecordedCommandKind.SetViewport));
        Assert.Equal(4, device.Recorder.CountOf(RecordedCommandKind.Draw));
    }

    /// <summary>Building the same asset twice does not add its stages twice.</summary>
    /// <remarks>
    ///     What an editor does on every reload, and a render system holds 64 stages: a builder that
    ///     added rather than reused would exhaust the mask after a couple of dozen saves.
    /// </remarks>
    [Fact]
    public void Rebuilding_reuses_the_stages_rather_than_adding_them_again() {
        var asset = YamlSerializer.Parse<GraphicsCompositorAsset>(Document);
        using var h = Build();

        h.Builder.Build(asset);
        h.Builder.Build(asset);

        Assert.Equal(3, h.System.Stages.Count);
    }

    // --- Post-processing, authored ------------------------------------------

    /// <summary>
    ///     A post chain written in a document, with no C# building any of it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         What doc 06's "the frame is data the user edits, not code" had never been true of. Two
    ///         things used to make it impossible and both are gone: a binding index is the shader's
    ///         decision, so a binding names what the shader calls it instead; and a sampler is a device
    ///         handle, so a document names a preset and the frame's cache resolves it.
    ///     </para>
    ///     <para>
    ///         The chain here is the shape a real frame ends with — a bloom pyramid and a tonemap that
    ///         reads it — declared in twenty lines that mention no index, no handle and no pass count.
    ///     </para>
    /// </remarks>
    const string PostChain = """
        version: 2
        resources:
          - name: SceneColour
            format: Rgba16Float
            usage: ColourTarget, Sampled
        stages:
          - name: Opaque
        game: !Sequence
          name: Frame
          children:
            - !RenderPass
              name: Main
              colourTargets: [SceneColour]
              children:
                - !SingleStage
                  name: OpaqueDraw
                  view: Camera
                  stage: Opaque
            - !FullScreen
              name: Blur
              shader: Blur
              colourTargets: [BloomResult]
              reads: [SceneColour]
              constantBinding: 2
            - !FullScreen
              name: Tonemap
              shader: Tonemap
              colourTargets: [Display]
              reads: [BloomResult]
              constantBinding: 2
              bindings:
                - name: source
                  resource: BloomResult
                - kind: Sampler
                  binding: 1
                  sampler: LinearClamp
        """;

    [Fact]
    public void A_document_can_author_a_post_chain() {
        var asset = YamlSerializer.Parse<GraphicsCompositorAsset>(PostChain);
        using var h = Build();

        var sequence = Assert.IsType<SequenceAsset>(asset.Game);

        var blur = Assert.IsType<FullScreenAsset>(sequence.Children[1]);
        Assert.Equal("SceneColour", blur.Reads[0]);
        Assert.Equal("BloomResult", blur.ColourTargets[0]);

        var tonemap = Assert.IsType<FullScreenAsset>(sequence.Children[2]);
        Assert.Equal("Tonemap", tonemap.Shader);
        Assert.Equal(2u, tonemap.ConstantBinding);

        // The texture binding names what the shader calls it and carries no index at all; the sampler
        // names a preset rather than a handle.
        Assert.Equal("source", tonemap.Bindings[0].Name);
        Assert.Equal("BloomResult", tonemap.Bindings[0].Resource);
        Assert.Equal(SamplerPreset.LinearClamp, tonemap.Bindings[1].Sampler);
    }

    /// <summary>The builder turns those into nodes, wired to the caches the host gave it.</summary>
    /// <remarks>
    ///     The division the whole asset model rests on: the document says what, and a running renderer
    ///     supplies the four things a file cannot carry — a device, a module cache, a descriptor
    ///     allocator and a sampler cache.
    /// </remarks>
    [Fact]
    public void The_builder_wires_authored_post_nodes_to_the_hosts_caches() {
        var asset = YamlSerializer.Parse<GraphicsCompositorAsset>(PostChain);
        using var h = Build();
        using var allocator = new DescriptorAllocator(device);
        using var samplers = new SamplerCache(device);

        h.Builder.Device = device;
        h.Builder.Modules = new(device);
        h.Builder.Descriptors = allocator;
        h.Builder.Samplers = samplers;

        var compositor = h.Builder.Build(asset);
        var sequence = Assert.IsType<SceneRendererSequence>(compositor.Game);

        var blur = Assert.IsType<FullScreenRenderer>(sequence.Children[1]);
        Assert.Same(samplers, blur.Samplers);
        Assert.Same(allocator, blur.Descriptors.Allocator);

        var tonemap = Assert.IsType<FullScreenRenderer>(sequence.Children[2]);
        Assert.Equal(2u, tonemap.ConstantBinding);
        Assert.Same(allocator, tonemap.Descriptors.Allocator);
        Assert.Equal(2, tonemap.Descriptors.Bindings.Count);

        // The preset became a description, which is what the frame's cache resolves.
        Assert.Equal(SamplerDescription.LinearClamp, tonemap.Descriptors.Bindings[1].Sampled);
    }

    /// <summary>
    ///     A node kind this assembly does not define is built by whoever does.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The seam the effect set needed and every game will: <c>Vixen.Rendering.PostFx</c> is
    ///         downstream of this assembly, so a builder that switched on its asset types would be a
    ///         cycle — and a document naming <c>!Bloom</c> would be a document only the engine could
    ///         extend.
    ///     </para>
    ///     <para>
    ///         The factory here is a fake rather than the real one, deliberately: what is under test
    ///         is that an <em>unknown</em> kind reaches a registered factory and comes back a node,
    ///         which a factory this project could not have referenced proves better than one it could.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_node_kind_the_builder_does_not_know_is_built_by_a_factory() {
        using var h = Build();
        var asset = new GraphicsCompositorAsset { Game = new StrangeAsset { Name = "Strange" } };

        // Nothing registered: the exception names the type nobody could build, rather than producing
        // a frame quietly missing a pass.
        var error = Assert.Throws<CompositorBindingException>(() => h.Builder.Build(asset));
        Assert.Contains(nameof(StrangeAsset), error.Message, StringComparison.Ordinal);

        h.Builder.Factories.Add(new StrangeFactory());

        var built = h.Builder.Build(asset);
        Assert.Equal("Strange", Assert.IsType<SceneRendererSequence>(built.Game).Name);
    }

    /// <summary>A factory that does not recognise a kind lets the next one answer.</summary>
    /// <remarks>
    ///     Returning null rather than throwing is what lets several projects each contribute node
    ///     kinds to one document — a game's effects beside the engine's, in any order.
    /// </remarks>
    [Fact]
    public void A_factory_that_does_not_recognise_a_kind_defers() {
        using var h = Build();

        h.Builder.Factories.Add(new SilentFactory());
        h.Builder.Factories.Add(new StrangeFactory());

        var built = h.Builder.Build(new() { Game = new StrangeAsset { Name = "Strange" } });

        Assert.NotNull(built.Game);
    }

    /// <summary>A node kind defined outside the assembly that builds documents.</summary>
    sealed record StrangeAsset : ISceneRendererAsset {
        public string Name { get; init; } = string.Empty;

        public bool Enabled { get; init; } = true;
    }

    sealed class StrangeFactory : ISceneRendererFactory {
        public SceneRenderer? Create(ISceneRendererAsset declared, CompositorBuilder builder) =>
            declared is StrangeAsset strange ? new SceneRendererSequence { Name = strange.Name } : null;
    }

    sealed class SilentFactory : ISceneRendererFactory {
        public SceneRenderer? Create(ISceneRendererAsset declared, CompositorBuilder builder) => null;
    }

    /// <summary>A document with post nodes survives the baked binary form too.</summary>
    [Fact]
    public void The_baked_form_carries_the_post_chain() {
        var original = YamlSerializer.Parse<GraphicsCompositorAsset>(PostChain);
        var reread = Serializer.Read<GraphicsCompositorAsset>(Serializer.ToBytes(original));
        var sequence = Assert.IsType<SequenceAsset>(reread.Game);

        Assert.Equal("Blur", Assert.IsType<FullScreenAsset>(sequence.Children[1]).Name);

        var tonemap = Assert.IsType<FullScreenAsset>(sequence.Children[2]);

        Assert.Equal("source", tonemap.Bindings[0].Name);
        Assert.Equal(SamplerPreset.LinearClamp, tonemap.Bindings[1].Sampler);
    }

    // --- The per-view block, authored ---------------------------------------

    /// <summary>
    ///     The one part of the four-set convention a document has a reason to describe.
    /// </summary>
    /// <remarks>
    ///     Sets 2 and 3 belong to a material and a draw and follow from the shaders. Set 1 is a
    ///     contract <em>between</em> shaders — a descriptor set survives a pipeline change only if the
    ///     layouts agree up to it — so the frame is the only thing that can state it, and until now
    ///     the only thing that could was a host writing C#.
    /// </remarks>
    const string WithViewBlock = """
        version: 2
        viewBlock:
          binding: 0
          stages: Vertex
        resources:
          - name: SceneColour
            format: Rgba16Float
            usage: ColourTarget, Sampled
        stages:
          - name: Opaque
        game: !RenderPass
          name: Main
          colourTargets: [SceneColour]
          children:
            - !SingleStage
              name: OpaqueDraw
              view: Camera
              stage: Opaque
        """;

    [Fact]
    public void A_document_can_declare_the_per_view_block() {
        var asset = YamlSerializer.Parse<GraphicsCompositorAsset>(WithViewBlock);
        using var h = Build();
        using var allocator = new DescriptorAllocator(device);

        h.Builder.Device = device;
        h.Builder.Descriptors = allocator;

        var compositor = h.Builder.Build(asset);

        using var block = h.Builder.ViewBlock;

        Assert.NotNull(block);
        Assert.True(block.IsConfigured);
        Assert.Equal(DescriptorSetSlot.PerView, block.Slot);

        // Declared with no members, so the standard block: the view-projection, the view position and
        // the view matrix, which is what `ForwardPlus.rvn` declares for set 1.
        Assert.Equal(3, block.Members.Count);
        Assert.Equal(ViewConstants.ViewProjection, block.Members[0].Key);
        Assert.Equal(ViewConstants.View, block.Members[2].Key);

        // And every node that draws a view was handed it.
        var pass = Assert.IsType<RenderPassRenderer>(compositor.Game);
        Assert.Same(block, Assert.IsType<SingleStageRenderer>(pass.Children[0]).Constants);
    }

    /// <summary>A frame that declares no block builds nodes that bind none.</summary>
    /// <remarks>
    ///     Which keeps the block optional rather than mandatory: a project whose shaders read no
    ///     camera should not have to declare one to draw.
    /// </remarks>
    [Fact]
    public void A_frame_with_no_view_block_binds_none() {
        var asset = YamlSerializer.Parse<GraphicsCompositorAsset>(Document);
        using var h = Build();

        h.Builder.Device = device;
        h.Builder.Build(asset);

        Assert.Null(h.Builder.ViewBlock);
    }

    /// <summary>A member naming a parameter nothing declares is refused.</summary>
    /// <remarks>
    ///     The alternative is a value that silently never arrives — a document and a shader that
    ///     disagree about what is in the block, which produces a frame drawn with whatever the block
    ///     happened to contain.
    /// </remarks>
    [Fact]
    public void A_view_member_naming_an_unknown_parameter_is_refused() {
        var asset = YamlSerializer.Parse<GraphicsCompositorAsset>(
            """
            version: 2
            viewBlock:
              members:
                - name: Nothing.Declares.This
                  offset: 0
                  size: 4
            stages:
              - name: Opaque
            game: !SingleStage
              name: Draw
              view: Camera
              stage: Opaque
            """
        );

        using var h = Build();
        h.Builder.Device = device;

        var thrown = Assert.Throws<CompositorBindingException>(() => h.Builder.Build(asset));

        Assert.Equal("parameter", thrown.Kind);
        Assert.Equal("Nothing.Declares.This", thrown.Name);
    }

    /// <summary>
    ///     A compute dispatch, authored — the last node kind that was code-only.
    /// </summary>
    /// <remarks>
    ///     Its value over a hand-written dispatch is the two lists it declares: a pass that says it
    ///     writes a buffer, beside one that says it reads it, is a pass the graph orders first and puts
    ///     a barrier after. A document can now say so, which is what makes a Forward+ preset a file
    ///     rather than a build.
    /// </remarks>
    [Fact]
    public void A_document_can_author_a_compute_dispatch() {
        var asset = YamlSerializer.Parse<GraphicsCompositorAsset>(
            """
            version: 2
            buffers:
              - name: Clusters
                size: 4096
            stages:
              - name: Opaque
            game: !Sequence
              name: Frame
              children:
                - !Compute
                  name: ClusterCull
                  shader: ClusterCulling
                  bufferReads: [SceneLights]
                  bufferWrites: [Clusters]
                  groupsX: 4
                  groupsY: 3
                  groupsZ: 6
                  bindings:
                    - name: lights
                      resource: SceneLights
                    - name: clusters
                      resource: Clusters
            """
        );

        using var h = Build();
        using var allocator = new DescriptorAllocator(device);

        h.Builder.Device = device;
        h.Builder.Descriptors = allocator;

        var compositor = h.Builder.Build(asset);
        var sequence = Assert.IsType<SceneRendererSequence>(compositor.Game);
        var cull = Assert.IsType<ComputeRenderer>(sequence.Children[0]);

        Assert.Equal("ClusterCulling", cull.ShaderName);
        Assert.Equal(new Int3(4, 3, 6), cull.Groups);
        Assert.Equal(["SceneLights"], cull.BufferReads);
        Assert.Equal(["Clusters"], cull.BufferWrites);

        // Named, not numbered — the shader's plan is what says where they go.
        Assert.Equal("lights", cull.Descriptors.Bindings[0].Name);
        Assert.Same(allocator, cull.Descriptors.Allocator);
    }
}
