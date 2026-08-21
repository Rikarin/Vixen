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
using Vixen.Rendering.DistanceFields;
using Vixen.Rendering.Features;
using Vixen.Rendering.IrradianceFields;
using Vixen.Rendering.Lighting;
using Vixen.Rendering.SurfaceCache;
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

    /// <summary>The same frame, splitting its shading across the ambient split's four planes.</summary>
    /// <remarks>
    ///     <para>
    ///         What a split document <em>is</em>, in the only terms a file has: four
    ///         <c>colourTargets</c> on the shading pass, in the order <c>ForwardPlus.rvn</c> declares
    ///         its output struct in. Nothing here says <c>SplitOutputs</c>, because nothing in a
    ///         document can — that is a permutation, and the whole point of
    ///         <c>RenderPassRenderer.SplitOutputsKey</c> is that the builder reads it back off these
    ///         four names.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The velocity pass is here for the or, and only for it.</b> It leaves
    ///         <see cref="RenderPassRenderer.ShaderName" /> at its default, so it is qualified by the
    ///         same name as <c>Main</c> and carries one colour target — which is sample 13's shape and
    ///         the standard frame's, and which a builder that took the <em>last</em> pass's answer
    ///         would let turn the split back off.
    ///     </para>
    /// </remarks>
    const string SplitDocument = """
        version: 2
        resources:
          - name: SceneColour
            format: Rgba16Float
            usage: ColourTarget, Sampled
          - name: SceneAlbedo
            format: Rgba8UNormSrgb
            usage: ColourTarget, Sampled
          - name: SceneNormals
            format: Rgba16Float
            usage: ColourTarget, Sampled
          - name: SceneSpecular
            format: Rgba8UNorm
            usage: ColourTarget, Sampled
          - name: SceneMotion
            format: Rg16Float
            usage: ColourTarget, Sampled
          - name: SceneDepth
            format: Depth32Float
            usage: DepthStencilTarget
        stages:
          - name: Opaque
          - name: Motion
        game: !Sequence
          name: Frame
          children:
            - !RenderPass
              name: Main
              colourTargets: [SceneColour, SceneAlbedo, SceneNormals, SceneSpecular]
              depthTarget: SceneDepth
              children:
                - !SingleStage
                  name: OpaqueDraw
                  view: Camera
                  stage: Opaque
            - !RenderPass
              name: Velocity
              colourTargets: [SceneMotion]
              depthTarget: SceneDepth
              depthLoad: Load
              readOnlyDepth: true
              children:
                - !SingleStage
                  name: MotionVectors
                  view: Camera
                  stage: Motion
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
    /// <summary>
    ///     The lit path: a clipmap, a probe field, and the two screen passes that read them.
    /// </summary>
    const string LightingDocument = """
        version: 2
        resources:
          - name: SceneDepth
            format: Depth32Float
            usage: DepthStencilTarget, Sampled
          - name: SceneNormals
            format: Rgba16Float
            usage: ColourTarget, Sampled
        stages:
          - name: Opaque
        game: !Sequence
          name: Frame
          children:
            - !GlobalDistanceField
              name: Clipmap
              parallel: false
            - !IrradianceField
              name: Probes
              budget: 16
              dilationPasses: 2
        """;

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
              albedo: SceneAlbedo
              normals: SceneNormals
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

        // The ambient split's planes reach the node by name too — the document's half of
        // `GpuClusterResolve.SplitOutputs`.
        Assert.Equal("SceneAlbedo", visibility.Albedo);
        Assert.Equal("SceneNormals", visibility.Normals);
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

    /// <summary>
    ///     A document can place doc 19's lit path, and a host that supplied no fields gets nodes that
    ///     do nothing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The asymmetry this removes is the one the virtualized path already had removed: every
    ///         renderer in the global-illumination chain existed and none of them had an asset, so a
    ///         game could reach doc 19 only by building its compositor in C# — which is the thing
    ///         doc 06 made the compositor an asset in order to avoid.
    ///     </para>
    ///     <para>
    ///         Both halves are the contract. A document names placement and the numbers; a host
    ///         supplies the field, which owns volume textures and a probe budget that outlive a frame.
    ///         So the same file has to build on a project with no field in it, and build nodes that
    ///         draw nothing rather than nodes that throw.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_document_can_place_the_lit_path() {
        using var h = Build();
        var clipmap = new GlobalDistanceField();

        var probes = new IrradianceField(
            new BoundingBox(new(-8f, -8f, -8f), new(8f, 8f, 8f)),
            new Int3(4, 4, 4)
        );

        h.Builder.DistanceField = clipmap;
        h.Builder.IrradianceField = probes;

        var compositor = h.Builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(LightingDocument));
        var children = Assert.IsType<SceneRendererSequence>(compositor.Game).Children;

        var field = Assert.IsType<GlobalDistanceFieldRenderer>(children[0]);
        var irradiance = Assert.IsType<IrradianceFieldRenderer>(children[1]);
        // The host's objects reached the nodes that need them.
        Assert.Same(clipmap, field.Field);
        Assert.Same(probes, irradiance.Field);

        // And the numbers the document chose reached the ones that do not.
        Assert.False(field.Parallel);
        Assert.Equal(16, irradiance.Budget);
        Assert.Equal(2, irradiance.DilationPasses);
    }

    /// <summary>
    ///     ⚠ A shader the document left empty keeps the renderer's own default rather than becoming a
    ///     nameless slot, which would be a dark frame for a reason no document mentions.
    /// </summary>
    [Fact]
    public void An_unnamed_field_shader_leaves_the_honest_default() {
        using var h = Build();

        var compositor = h.Builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(LightingDocument));
        var children = Assert.IsType<SceneRendererSequence>(compositor.Game).Children;

        var irradiance = Assert.IsType<IrradianceFieldRenderer>(children[1]);

        Assert.NotEmpty(irradiance.Source);

        // And with no host field at all the nodes still built.
        Assert.Null(irradiance.Field);
        Assert.Null(Assert.IsType<GlobalDistanceFieldRenderer>(children[0]).Field);
    }

    /// <summary>Doc 19 § L4's node: the cache kept in the frame, and who reads it.</summary>
    const string SurfaceCacheDocument = """
        version: 2
        stages:
          - name: Opaque
        game: !Sequence
          name: Frame
          children:
            - !SurfaceCache
              name: Cache
              source: SurfaceCache
              passes:
                - ScreenProbeTrace
                - ReflectionTrace
        """;

    /// <summary>
    ///     A document can place the surface cache, and a host that supplied no store gets a node
    ///     that does nothing.
    /// </summary>
    /// <remarks>
    ///     The lit path's remaining seam, on the terms the clipmap and the probe field already
    ///     settled: the store, the fills and the capture own an atlas and a double buffer that
    ///     outlive a frame, so the host supplies them and the document decides placement and who
    ///     reads the answer. Both halves are asserted because both are the contract.
    /// </remarks>
    [Fact]
    public void A_document_can_place_the_surface_cache() {
        using var h = Build();
        var store = new SurfaceCacheStore(new SurfaceCacheAtlas(new(64, 64)));
        using var light = new SurfaceCacheLightFill(device);
        using var gather = new SurfaceCacheGatherFill(device);

        h.Builder.SurfaceCache = store;
        h.Builder.SurfaceCacheLightFill = light;
        h.Builder.SurfaceCacheGatherFill = gather;

        var compositor = h.Builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(SurfaceCacheDocument));
        var children = Assert.IsType<SceneRendererSequence>(compositor.Game).Children;

        var cache = Assert.IsType<SurfaceCacheRenderer>(children[0]);

        // The host's objects reached the node that needs them.
        Assert.Same(store, cache.Store);
        Assert.Same(light, cache.LightFill);
        Assert.Same(gather, cache.GatherFill);

        // And the names the document chose: the slot's shader, and the consumers — replaced rather
        // than added to, because a document that names its readers means those.
        Assert.Equal("SurfaceCache", cache.Source);
        Assert.Equal(["ScreenProbeTrace", "ReflectionTrace"], cache.Passes);

        cache.Dispose();
    }

    /// <summary>The same document on a host with no cache builds a node that does nothing.</summary>
    [Fact]
    public void The_same_document_builds_on_a_host_that_has_no_cache() {
        using var h = Build();

        var minimal = YamlSerializer.Parse<GraphicsCompositorAsset>(
            """
            version: 2
            stages:
              - name: Opaque
            game: !SurfaceCache
              name: Cache
            """
        );

        var compositor = h.Builder.Build(minimal);
        var cache = Assert.IsType<SurfaceCacheRenderer>(compositor.Game);

        Assert.Null(cache.Store);
        Assert.Null(cache.LightFill);

        // Named nothing, so the honest defaults stand: the compiler's own name for the cache shader,
        // and the screen-probe trace as the one consumer whose hit branch composes the slot.
        Assert.NotEmpty(cache.Source);
        Assert.Equal(["ScreenProbeTrace"], cache.Passes);

        cache.Dispose();
    }

    /// <summary>A document can say "snapshot this target into that one" — [docs/plan/35 § B1].</summary>
    /// <remarks>
    ///     The node exists so that a pass may read the frame so far and then contribute to it, which
    ///     is undefined against a single target. What is asserted here is only that a document can
    ///     name it and that the two names survive the build; what the pass does is
    ///     <c>TextureCopyTests</c>'.
    /// </remarks>
    [Fact]
    public void A_document_can_declare_a_copy_between_two_targets() {
        using var h = Build();

        var asset = YamlSerializer.Parse<GraphicsCompositorAsset>(
            """
            version: 2
            stages:
              - name: Opaque
            resources:
              - name: SceneColour
                format: Rgba16Float
                usage: ColourTarget, Sampled, CopySource
              - name: SceneColourCopy
                format: Rgba16Float
                usage: Sampled, CopyDestination
            game: !Copy
              name: SceneColourSnapshot
              source: SceneColour
              destination: SceneColourCopy
            """
        );

        var compositor = h.Builder.Build(asset);
        var copy = Assert.IsType<TextureCopyRenderer>(compositor.Game);

        Assert.Equal("SceneColourSnapshot", copy.Name);
        Assert.Equal("SceneColour", copy.Source);
        Assert.Equal("SceneColourCopy", copy.Destination);

        // ⚠ And the usages survive the document, because they are what the node refuses on. A
        // resource round-tripping without them is a copy that fails at build time in a frame that
        // loaded cleanly.
        var declared = compositor.Resources.Single(resource => resource.Name == "SceneColour");
        Assert.True(declared.Usage.HasFlag(TextureUsage.CopySource));
    }

    /// <summary>
    ///     A document can declare a multisampled pass and say where its samples go.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The half of MSAA that did not exist.</b> <c>sampleCount</c> on a resource and on a
    ///         pass have always parsed and always reached the texture and the pipeline, and both
    ///         backends have always honoured <c>ColourAttachment.ResolveView</c> — with nothing in
    ///         between naming a pair, so a document could declare a 4× target, draw into it correctly,
    ///         and end the pass with the result in memory no later pass can read.
    ///     </para>
    ///     <para>
    ///         ⚠ The two resources' usages are the assertion's other half. The multisampled one is a
    ///         colour target and <em>not</em> sampled — a multisampled image is not readable through
    ///         an ordinary sampler — and the resolve is what carries <c>Sampled</c> and what the rest
    ///         of the frame reads by name.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_document_can_resolve_a_multisampled_pass() {
        using var h = Build();

        var asset = YamlSerializer.Parse<GraphicsCompositorAsset>(
            """
            version: 2
            stages:
              - name: Opaque
            resources:
              - name: SceneSamples
                format: Rgba16Float
                usage: ColourTarget
                sampleCount: 4
              - name: SceneColour
                format: Rgba16Float
                usage: ColourTarget, Sampled
            game: !RenderPass
              name: Main
              colourTargets: [SceneSamples]
              sampleCount: 4
              resolveTargets:
                - target: SceneSamples
                  into: SceneColour
            """
        );

        var compositor = h.Builder.Build(asset);
        var pass = Assert.IsType<RenderPassRenderer>(compositor.Game);

        Assert.Equal(4, pass.SampleCount);
        Assert.Equal("SceneColour", Assert.Contains("SceneSamples", pass.ResolveTargets));

        var samples = compositor.Resources.Single(resource => resource.Name == "SceneSamples");
        var resolved = compositor.Resources.Single(resource => resource.Name == "SceneColour");

        Assert.Equal(4, samples.SampleCount);
        Assert.Equal(1, resolved.SampleCount);
        Assert.False(samples.Usage.HasFlag(TextureUsage.Sampled));
        Assert.True(resolved.Usage.HasFlag(TextureUsage.Sampled));
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

    ImportedTexture Imported(PixelFormat format, TextureUsage usage, string name, int width = 512, int height = 512) {
        var description = new TextureDescription(format, width, height, usage | TextureUsage.Sampled, Name: name);
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

    static RenderObjectId AddMesh(Harness h, float z, Material material, RenderStageMask stages) {
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
        return id;
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

        // The node's own arithmetic: two cascades at 256 fold 2 × 1, so 512 × 256 — and the node
        // refuses anything else by name now. This import was 512 × 512 for years, which is the
        // same latent mismatch the guard was written to catch in the wild: tiles landing in half
        // the texture while the folded lookup addressed all of it.
        compositor.Imports["ShadowAtlas"] = Imported(
            PixelFormat.Depth32Float,
            TextureUsage.DepthStencilTarget,
            "ShadowAtlas",
            height: 256
        );

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
        // The colours and vectors a document writes as plain scalars, which the generator does not
        // describe on its own — `CompositorImporter`'s static constructor makes the same call on the
        // real path. Without it a pass with a clearColour fails to bind, and the message names the
        // type rather than the missing registration.
        MathScalars.Register();

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

    /// <summary>
    ///     The document's cascade count reaches the shader the shading pass is compiled from.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <strong>The agreement the node's remarks described and nothing made.</strong>
    ///         <c>cascades</c> is sized by a permutation, so the count is a property of the compiled
    ///         variant as much as of the node — and until this call existed the host published two
    ///         matrices into a pass compiled for four. The tiles then fold 2 × 1 against a lookup
    ///         expecting 2 × 2, so the containment test answers "no cascade" across most of the screen
    ///         and the frame has <em>no sun shadow in it at all</em>. Nothing is invalid, nothing is
    ///         refused, and every tier below High ships fewer than four cascades.
    ///     </para>
    ///     <para>
    ///         Both halves are asserted, because either alone is silent. The value is what a variant
    ///         is compiled for; the key is what the effect key is built from, and a value under a key
    ///         the list does not carry reaches no compiler — see
    ///         <c>MaterialRenderFeature.SetPermutation</c>.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_documents_cascade_count_selects_the_shading_variant() {
        var asset = YamlSerializer.Parse<GraphicsCompositorAsset>(Document);
        using var h = Build();

        Compose(h, asset);

        var key = ShadowMapRenderer.CascadeCountKey("ForwardPlus");

        Assert.Equal(2, h.Materials.Permutations.Get(key));
        Assert.Contains(key, h.Materials.PermutationKeys["ForwardPlus"]);
    }

    /// <summary>
    ///     And it lands in the effect key, which is the only place it can change anything.
    /// </summary>
    /// <remarks>
    ///     What the assertions above are <em>for</em>, stated where a rename cannot quietly unpick
    ///     it. A permutation the feature holds and the effect key omits looks identical from the
    ///     collection's side and is the whole failure: the compiler is never told, so it produces the
    ///     variant the shader's own <c>= 4</c> describes.
    /// </remarks>
    [Fact]
    public void The_count_is_in_the_key_the_shading_variant_was_resolved_from() {
        var asset = YamlSerializer.Parse<GraphicsCompositorAsset>(Document);
        using var h = Build();

        var compositor = Compose(h, asset);
        var opaque = h.Builder.Stages["Opaque"];

        var mesh = AddMesh(h, -10f, new Material("ForwardPlus"), opaque.Mask);

        Frame(h, compositor);

        var effect = h.Materials.EffectOf(h.System, mesh);

        Assert.NotNull(effect);
        Assert.Contains(new KeyValuePair<string, string>("ForwardPlus.CascadeCount", "2"), effect!.Key.Values);
    }

    /// <summary>
    ///     The host's per-object light budget reaches the shader the shading pass is compiled from.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <strong>The cascade defect above, one array along, and it was live for the same
    ///         reason.</strong> <c>ClusteredShading.rvn</c> sizes <c>lights[MaxLights]</c> from a
    ///         permutation declared sixteen,
    ///         <see cref="ForwardLightingRenderFeature.MaxLightsPerObject" /> sizes the block the
    ///         feature writes and ships eight, the quality tiers ask for four on Low — and no line of
    ///         code carried any of the three to a compiler.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What it costs is not what the comments said.</b> The shader's loop breaks at
    ///         <c>lightCount</c> and the feature never writes a count longer than the block it sized,
    ///         so nothing reads a slot nobody wrote in either direction. What happens is that the
    ///         shorter of the two wins in silence: a tier asking for four got eight, and the frame
    ///         bound a 768-byte per-draw range at a block the variant declares 1296 bytes of.
    ///     </para>
    ///     <para>
    ///         Both halves are asserted, for the reason the cascade pair states: the value is what a
    ///         variant is compiled for, the key is what the effect key is built from, and a value
    ///         under a key the list does not carry reaches no compiler at all.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_hosts_light_budget_selects_the_shading_variant() {
        var asset = YamlSerializer.Parse<GraphicsCompositorAsset>(Document);
        using var h = Build();

        // Four, which is the Low tier's number and neither side's default — so an assertion that
        // passes cannot be passing on eight or on sixteen.
        using var lighting = new ForwardLightingRenderFeature { MaxLightsPerObject = 4 };

        h.Meshes.Add(lighting);
        Compose(h, asset);

        var key = ForwardLightingRenderFeature.MaxLightsKey("ForwardPlus");

        Assert.Equal(4, h.Materials.Permutations.Get(key));
        Assert.Contains(key, h.Materials.PermutationKeys["ForwardPlus"]);
    }

    /// <summary>
    ///     And it lands in the effect key, which is the only place it can change anything.
    /// </summary>
    /// <remarks>
    ///     The same claim <see cref="The_count_is_in_the_key_the_shading_variant_was_resolved_from" />
    ///     makes about the cascades, and the same silent failure it guards against: a permutation the
    ///     feature holds and the effect key omits is invisible from the collection's side, and the
    ///     compiler produces the variant the <c>.rvn</c>'s own <c>= 16</c> describes.
    /// </remarks>
    [Fact]
    public void The_light_budget_is_in_the_key_the_shading_variant_was_resolved_from() {
        var asset = YamlSerializer.Parse<GraphicsCompositorAsset>(Document);
        using var h = Build();
        using var lighting = new ForwardLightingRenderFeature { MaxLightsPerObject = 4 };

        h.Meshes.Add(lighting);

        var compositor = Compose(h, asset);
        var opaque = h.Builder.Stages["Opaque"];

        var mesh = AddMesh(h, -10f, new Material("ForwardPlus"), opaque.Mask);

        Frame(h, compositor);

        var effect = h.Materials.EffectOf(h.System, mesh);

        Assert.NotNull(effect);
        Assert.Contains(new KeyValuePair<string, string>("ForwardPlus.MaxLights", "4"), effect!.Key.Values);
    }

    /// <summary>
    ///     A document's four colour targets are what makes the shading pass write four.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <strong>The other half of the ambient split, which a document could always declare and
    ///         nothing ever acted on.</strong> <c>ForwardPlus.SplitOutputs</c> is a permutation and no
    ///         production code in the engine set it: two sample projects did, by hand, beside their
    ///         own frames. A third project that declared the planes and the combine and forgot the
    ///         permutation got a frame that was <em>pixel-identical</em> to an unsplit one — the
    ///         single-target variant writes location 0, the other three planes stay at the clear, and
    ///         <c>AmbientCombine.rvn</c> reads a zero-length normal as sky and hands the direct target
    ///         straight back. Nothing is invalid and nothing is refused.
    ///     </para>
    ///     <para>
    ///         Both halves are asserted for the reason the cascade pair above states: the value is
    ///         what a variant is compiled for, and the key is what the effect key is built from.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_documents_split_targets_select_the_shading_variant() {
        var asset = YamlSerializer.Parse<GraphicsCompositorAsset>(SplitDocument);
        using var h = Build();

        h.Builder.Build(asset);

        var key = RenderPassRenderer.SplitOutputsKey("ForwardPlus");

        Assert.True(h.Materials.Permutations.Get(key));
        Assert.Contains(key, h.Materials.PermutationKeys["ForwardPlus"]);
    }

    /// <summary>
    ///     And a one-target pass under the same shader name does not take it back off.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The or, which the document's velocity pass exists for.</b>
    ///     <see cref="RenderPassRenderer.ShaderName" /> qualifies the permutation and several passes
    ///     leave it at its default — sample 13's <c>Main</c>, <c>Velocity</c> and <c>Sparks</c> all
    ///     do, and so does everything <c>StandardFrame</c> emits. A builder that assigned rather than
    ///     or-ed would answer with whichever pass it walked last, which is a split frame whose
    ///     shading pass writes one plane because a velocity pass exists somewhere after it.
    /// </remarks>
    [Fact]
    public void A_one_target_pass_beside_a_split_one_does_not_unsplit_the_frame() {
        var asset = YamlSerializer.Parse<GraphicsCompositorAsset>(SplitDocument);
        using var h = Build();

        var compositor = h.Builder.Build(asset);
        var children = Assert.IsType<SceneRendererSequence>(compositor.Game).Children;

        // The pass that would have won an assignment: last in the tree, one target, same name.
        var velocity = Assert.IsType<RenderPassRenderer>(children[1]);

        Assert.Equal("ForwardPlus", velocity.ShaderName);
        Assert.Single(velocity.ColourTargets);
        Assert.True(h.Materials.Permutations.Get(RenderPassRenderer.SplitOutputsKey("ForwardPlus")));
    }

    /// <summary>
    ///     An unsplit document says so, rather than leaving whatever was there.
    /// </summary>
    /// <remarks>
    ///     <c>Permutations</c> belongs to the feature and outlives a build, so a host that reloads
    ///     from a split document to an unsplit one has to be told — writing the value only when it is
    ///     true would leave the shading pass compiled for four targets in a pass that declares one,
    ///     which is a pipeline the device refuses rather than a frame that looks wrong. The key is
    ///     registered either way, so the effect key carries the answer rather than the absence of
    ///     one.
    /// </remarks>
    [Fact]
    public void An_unsplit_document_turns_the_split_back_off() {
        using var h = Build();
        var key = RenderPassRenderer.SplitOutputsKey("ForwardPlus");

        h.Builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(SplitDocument));
        Assert.True(h.Materials.Permutations.Get(key));

        h.Builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(Document));

        Assert.False(h.Materials.Permutations.Get(key));
        Assert.Contains(key, h.Materials.PermutationKeys["ForwardPlus"]);
    }

    /// <summary>
    ///     And it lands in the effect key, which is the only place it can change anything.
    /// </summary>
    /// <remarks>
    ///     What the assertions above are <em>for</em>, on
    ///     <see cref="The_count_is_in_the_key_the_shading_variant_was_resolved_from" />'s terms: a
    ///     permutation the feature holds and the effect key omits looks identical from the
    ///     collection's side, and the compiler is never told — so it produces the variant the
    ///     shader's own <c>= false</c> describes, which is the whole of the failure this closes.
    /// </remarks>
    [Fact]
    public void The_split_is_in_the_key_the_shading_variant_was_resolved_from() {
        using var h = Build();

        var compositor = h.Builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(SplitDocument));
        compositor.FrameSize = new(512, 512);

        // The frame's last target has to belong to somebody outside the graph, or the pass that
        // writes it is one the graph is right to cull — see Compose.
        compositor.Imports["SceneColour"] =
            Imported(PixelFormat.Rgba16Float, TextureUsage.ColourTarget, "SceneColour");

        var mesh = AddMesh(h, -10f, new Material("ForwardPlus"), h.Builder.Stages["Opaque"].Mask);

        Frame(h, compositor);

        var effect = h.Materials.EffectOf(h.System, mesh);

        Assert.NotNull(effect);
        Assert.Contains(new KeyValuePair<string, string>("ForwardPlus.SplitOutputs", "true"), effect!.Key.Values);
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

    /// <summary>A volume is a resource the document can name, extent and shape and all.</summary>
    /// <remarks>
    ///     ⚠ The depth takes no <see cref="RenderResourceAsset.Scale" />: a froxel grid's slice count
    ///     is how finely the frustum is cut, which does not change when the window does. The other
    ///     two axes still scale, so a volume authored against the frame stays proportional.
    /// </remarks>
    [Fact]
    public void A_declared_volume_keeps_its_depth_and_its_shape() {
        var declared = new RenderResourceAsset {
            Name = "FogMedia",
            Format = PixelFormat.Rgba16Float,
            Usage = TextureUsage.Storage | TextureUsage.Sampled,
            Width = 160,
            Height = 90,
            Depth = 64,
            Dimension = TextureDimension.Texture3D
        };

        var description = declared.Describe(new(512, 512));

        Assert.Equal(160, description.Width);
        Assert.Equal(90, description.Height);
        Assert.Equal(64, description.Depth);
        Assert.Equal(TextureDimension.Texture3D, description.Dimension);

        // A plane is what it always was: one deep, and 2D.
        var plane = new RenderResourceAsset { Name = "Bloom" }.Describe(new(512, 512));

        Assert.Equal(1, plane.Depth);
        Assert.Equal(TextureDimension.Texture2D, plane.Dimension);
    }

    /// <summary>
    ///     And a node can read the extent back, rather than carrying its own copy of the numbers.
    /// </summary>
    /// <remarks>
    ///     Two derivations of one quantity is how a dispatch ends up covering part of a volume and
    ///     leaving the rest at whatever the previous frame put there.
    /// </remarks>
    [Fact]
    public void The_frame_reports_what_a_resource_was_declared_as() {
        using var h = Build();

        var declared = new RenderResourceAsset {
            Name = "FogMedia",
            Format = PixelFormat.Rgba16Float,
            Usage = TextureUsage.Storage | TextureUsage.Sampled,
            Width = 160,
            Height = 90,
            Depth = 64,
            Dimension = TextureDimension.Texture3D
        }.Describe(new(512, 512));

        var frame = new CompositorFrame { Graph = h.Graph, Effects = effects, Device = device };

        frame.Add("FogMedia", h.Graph.CreateTexture(declared), declared);
        frame.Add("Plain", h.Graph.CreateTexture(declared), declared.Format);

        var description = frame.DescriptionOf("test", "FogMedia");

        Assert.NotNull(description);
        Assert.Equal(160, description!.Value.Width);
        Assert.Equal(90, description.Value.Height);
        Assert.Equal(64, description.Value.Depth);
        Assert.Equal(TextureDimension.Texture3D, description.Value.Dimension);

        // ⚠ Null and not a 1×1×1 stand-in. "Nobody recorded one" and "it is one texel" are different
        // answers, and a dispatch sized from the second covers one froxel.
        Assert.Null(frame.DescriptionOf("test", "Plain"));
        Assert.Equal(PixelFormat.Rgba16Float, frame.FormatOf("test", "Plain"));
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

        using var compositor = h.Builder.Build(asset);

        // ⚠ Not `using`, and it used to be. The block is per-build state the compositor owns — it
        // holds a uniform buffer and a set layout, and nothing disposed either until the compositor
        // did — so releasing it here would be releasing it out from under the tree above.
        var block = h.Builder.ViewBlock;

        Assert.NotNull(block);
        Assert.True(block.IsConfigured);
        Assert.Equal(DescriptorSetSlot.PerView, block.Slot);

        // Declared with no members, so the standard block: the view-projection, the view position,
        // the view matrix and last frame's view-projection. The first three are what `ForwardPlus.rvn`
        // declares for set 1 and the fourth is what `MotionVectors.rvn` adds — a shader reading a
        // prefix of a longer block is fine, and the reverse is the one that faults.
        Assert.Equal(4, block.Members.Count);
        Assert.Equal(ViewConstants.ViewProjection, block.Members[0].Key);
        Assert.Equal(ViewConstants.View, block.Members[2].Key);
        Assert.Equal(ViewConstants.PreviousViewProjection, block.Members[3].Key);

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

    // --- The sun's static cache ---------------------------------------------

    /// <summary>
    ///     A document turns the sun's static cache on: the stage, the slack and a device to keep it in.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="ShadowMapRenderer.StaticCasterStage" /> has been complete since doc 06 and
    ///         nothing but a test ever reached it — there was no field on the asset and no arm in the
    ///         builder, so a <c>.vxcompositor</c> could not turn it on at all.
    ///     </para>
    ///     <para>
    ///         All three arrive together because any one alone is a slower frame. The stage without
    ///         <see cref="ShadowMapRenderer.Slack" /> re-fits the moment the camera moves, which
    ///         invalidates the cache every frame and leaves the frame paying for a whole-atlas copy it
    ///         gets nothing for; and the node with no device has nowhere to keep depth that outlives a
    ///         frame, because every <c>!Resource</c> a document can declare is transient.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_document_turns_the_suns_static_cache_on() {
        var asset = YamlSerializer.Parse<GraphicsCompositorAsset>(
            """
            version: 2
            stages:
              - name: ShadowCaster
              - name: ShadowStatic
            game: !Sequence
              name: Frame
              children:
                - !ShadowMap
                  name: Sun
                  stage: ShadowCaster
                  staticStage: ShadowStatic
                  slack: 0.25
                  atlas: ShadowAtlas
            """
        );

        using var h = Build();

        h.Builder.Device = device;

        var compositor = h.Builder.Build(asset);
        var sequence = Assert.IsType<SceneRendererSequence>(compositor.Game);
        var sun = Assert.IsType<ShadowMapRenderer>(sequence.Children[0]);

        Assert.Same(h.Builder.Stages["ShadowStatic"], sun.StaticCasterStage);
        Assert.Equal(0.25f, sun.Slack);
        Assert.Same(device, sun.Device);

        // Empty, which is what says the node owns the texture rather than resolving one the document
        // declared. A document cannot declare one: the graph's pool exists to recycle exactly the
        // memory a cache has to keep.
        Assert.Equal(string.Empty, sun.StaticAtlas);
    }

    /// <summary>A document that names no static stage is the uncached node it always was.</summary>
    [Fact]
    public void A_document_with_no_static_stage_leaves_the_cache_off() {
        var asset = YamlSerializer.Parse<GraphicsCompositorAsset>(Document);

        using var h = Build();

        var compositor = h.Builder.Build(asset);
        var sequence = Assert.IsType<SceneRendererSequence>(compositor.Game);
        var sun = Assert.IsType<ShadowMapRenderer>(sequence.Children[0]);

        Assert.Null(sun.StaticCasterStage);
        Assert.Equal(0f, sun.Slack);
    }
}
