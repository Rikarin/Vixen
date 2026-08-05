// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Foliage;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.PostFx;
using Vixen.Shaders;
using Vixen.Terrain;
using Xunit;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Rendering.Terrain.Tests;

/// <summary>
///     The runtime reachability of the terrain stack: a document names <c>!Terrain</c>, a world
///     carries <see cref="TerrainComponent" />, and the two meet in a drawn frame.
/// </summary>
/// <remarks>
///     Everything below the node was already tested; what these check is the wiring nothing
///     exercised — the factory, the extraction bridge, and the node's two passes over a recording
///     device. What a headless device cannot say is whether the result looks like ground, which
///     stays the golden image's job.
/// </remarks>
public sealed class TerrainNodeTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });
    readonly EffectSystem effects = new();

    public TerrainNodeTests() => effects.AddProvider(new AlwaysCompiles());

    public void Dispose() => device.Dispose();

    const string Document = """
        version: 2
        resources:
          - name: SceneHdr
            format: Rgba16Float
            usage: ColourTarget, Sampled
          - name: SceneDepth
            format: Depth32Float
            usage: DepthStencilTarget
        game: !Sequence
          name: Frame
          children:
            - !Terrain
              name: Ground
        """;

    // ------------------------------------------------------------------ the factory

    /// <summary>A document containing the node builds through the factory.</summary>
    [Fact]
    public void ADocumentNamingTerrainBuildsThroughTheFactory() {
        var (builder, factory) = Builder();

        var compositor = builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(Document));
        var children = Assert.IsType<SceneRendererSequence>(compositor.Game).Children;
        var node = Assert.IsType<TerrainSceneRenderer>(Assert.Single(children));

        Assert.Equal("Ground", node.Name);
        Assert.Equal("SceneHdr", node.Output);
        Assert.Equal("SceneDepth", node.Depth);
        Assert.Same(factory.Scene, node.Scene);
        Assert.Same(builder.Views["Camera"], node.View);
        Assert.True(node.Grass);
    }

    /// <summary>A node whose view nothing bound refuses loudly rather than drawing from the origin.</summary>
    [Fact]
    public void AViewNothingBoundIsARefusalNamingIt() {
        var (builder, _) = Builder();
        builder.Views.Clear();

        var refusal = Assert.Throws<CompositorBindingException>(
            () => builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(Document))
        );

        Assert.Equal("Camera", refusal.Name);
    }

    /// <summary>The factory's tier numbers reach the node, and a document scalar out-votes them.</summary>
    [Fact]
    public void QualityFoldsFactoryUnderDocument() {
        var (builder, factory) = Builder();

        factory.Vegetation = new() { GrassDensityScale = 0.5f, GrassResidentCells = 64 };

        var compositor = builder.Build(
            new GraphicsCompositorAsset {
                Game = new TerrainNodeAsset { GrassDensityScale = 0.25f }
            }
        );

        var node = Assert.IsType<TerrainSceneRenderer>(compositor.Game);

        // The document said 0.25 and the factory said 0.5: the document decided.
        Assert.Equal(0.25f, node.Vegetation.GrassDensityScale);

        // The document said nothing about the cells, so the factory's tier flows through.
        Assert.Equal(64, node.Vegetation.GrassResidentCells);

        // And a field neither touched keeps the engine default.
        Assert.Equal(4096, node.Vegetation.GrassBladesPerCell);
    }

    // ------------------------------------------------------------------ the bridge

    /// <summary>A world's terrain component reaches the frame list, placed by its transform.</summary>
    [Fact]
    public void AWorldsTerrainComponentReachesTheFrameList() {
        using var world = new World();
        var scene = new TerrainSceneSource();
        var system = new TerrainExtractionSystem(scene) { Assets = new OneOfEach(Map()) };

        var entity = world.Create(
            TerrainComponent.Of("ground.vxterrain"),
            new WorldTransform { Value = Matrix4x4.FromTranslation(new(64f, 0f, 32f)) }
        );

        system.Extract(world);

        var entry = Assert.Single(scene.Terrains);

        Assert.Equal(new Vector3(64f, 0f, 32f), entry.Origin);
        Assert.Equal(64f, entry.NearRange);
        Assert.Null(entry.Grass);
        Assert.Equal(1, system.TerrainCount);

        // Refilled rather than appended, and a destroyed entity takes its ground with it.
        system.Extract(world);
        Assert.Single(scene.Terrains);

        world.Destroy(entity);
        system.Extract(world);
        Assert.Empty(scene.Terrains);
    }

    /// <summary>A grass component's rule rides its terrain's entry.</summary>
    [Fact]
    public void AGrassComponentRidesItsTerrain() {
        using var world = new World();
        var scene = new TerrainSceneSource();
        var system = new TerrainExtractionSystem(scene) { Assets = new OneOfEach(Map()) };

        var entity = world.Create(
            TerrainComponent.Of("ground.vxterrain"),
            new WorldTransform { Value = Matrix4x4.Identity }
        );

        world.Add(entity, TerrainGrassComponent.Of("meadow.vxgrass"));

        system.Extract(world);

        var entry = Assert.Single(scene.Terrains);

        Assert.NotNull(entry.Grass);
        Assert.Equal(160f, entry.GrassRange);
    }

    /// <summary>A reference that has not resolved waits quietly; a broken rule is refused loudly.</summary>
    [Fact]
    public void WaitingAndRefusedAreDifferentAnswers() {
        using var world = new World();
        var scene = new TerrainSceneSource();
        var empty = new OneOfEach(null);
        var system = new TerrainExtractionSystem(scene) { Assets = empty };

        var entity = world.Create(
            TerrainComponent.Of("ground.vxterrain"),
            new WorldTransform { Value = Matrix4x4.Identity }
        );

        system.Extract(world);

        Assert.Empty(scene.Terrains);
        Assert.Equal(1, system.Waiting);

        // The terrain arrives, and beside it a grass rule whose own validation refuses it — a
        // backwards weight range. The terrain draws; the rule is dropped where a person can see it
        // rather than thrown from the scatter mid-frame.
        world.Add(entity, TerrainGrassComponent.Of("meadow.vxgrass"));

        var broken = GrassType.Of("meadow") with { MinWeight = 0.9f, MaxWeight = 0.1f };
        var system2 = new TerrainExtractionSystem(scene) { Assets = new OneOfEach(Map(), broken) };

        system2.Extract(world);

        var entry = Assert.Single(scene.Terrains);

        Assert.Null(entry.Grass);
        Assert.Equal(1, system2.RefusedGrass);
    }

    // ------------------------------------------------------------------ the frame

    /// <summary>The whole path: a document, a world's terrain, two passes, and a recorded draw.</summary>
    [Fact]
    public void ATerrainInTheWorldDrawsThroughTheDocument() {
        var (builder, factory) = Builder();
        var compositor = builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(Document));

        factory.Scene!.Terrains.Add(new(Map(), Vector3.Zero, 32f, 0, true, null, 0f));

        var node = Assert.IsType<TerrainSceneRenderer>(
            Assert.Single(Assert.IsType<SceneRendererSequence>(compositor.Game).Children)
        );

        Draw(compositor);

        Assert.Equal(1, node.TerrainsDrawn);
        Assert.False(node.WaitingForShaders);

        // One indexed instanced call for the surface, whatever the patch count — § D3's claim,
        // read back through the document path this time.
        Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.DrawIndexed));

        node.Dispose();
    }

    /// <summary>A terrain with a grass rule dispatches the scatter and draws the field.</summary>
    [Fact]
    public void GrassScattersAndDrawsThroughTheDocument() {
        var (builder, factory) = Builder();
        var compositor = builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(Document));

        // Unbound to any weight layer, so the rule needs no painted terrain to grow.
        var meadow = GrassType.Of("meadow") with { Layer = "" };

        factory.Scene!.Terrains.Add(new(Map(), Vector3.Zero, 32f, 0, true, meadow, 96f));

        var node = Assert.IsType<TerrainSceneRenderer>(
            Assert.Single(Assert.IsType<SceneRendererSequence>(compositor.Game).Children)
        );

        Draw(compositor);

        Assert.Equal(1, node.TerrainsDrawn);
        Assert.Equal(1, node.GrassFieldsDrawn);

        // The scatter and its argument phase dispatched, and the field drew indirect.
        Assert.NotEmpty(device.Recorder!.OfKind(RecordedCommandKind.Dispatch));
        Assert.NotEmpty(device.Recorder.OfKind(RecordedCommandKind.DrawIndexedIndirect));

        node.Dispose();
    }

    /// <summary>A world with no terrain in it draws nothing, quietly.</summary>
    [Fact]
    public void NoTerrainInTheWorldIsQuietNothing() {
        var (builder, factory) = Builder();
        var compositor = builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(Document));

        Assert.NotNull(factory.Scene);

        Draw(compositor);

        var node = Assert.IsType<TerrainSceneRenderer>(
            Assert.Single(Assert.IsType<SceneRendererSequence>(compositor.Game).Children)
        );

        Assert.Equal(0, node.TerrainsDrawn);
        Assert.Empty(device.Recorder!.OfKind(RecordedCommandKind.DrawIndexed));
    }

    /// <summary>A node whose colour target nothing declared refuses, naming node and target.</summary>
    [Fact]
    public void AMissingTargetIsARefusalNamingIt() {
        var (builder, factory) = Builder();

        const string missing = """
            version: 2
            game: !Terrain
              name: Ground
              output: SceneColour
            """;

        var compositor = builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(missing));

        factory.Scene!.Terrains.Add(new(Map(), Vector3.Zero, 32f, 0, true, null, 0f));

        var refusal = Assert.Throws<CompositorBindingException>(() => Draw(compositor));

        Assert.Equal("Ground", refusal.Node);
        Assert.Equal("SceneColour", refusal.Name);
    }

    // ------------------------------------------------------------------ the lit path

    const string LitDocument = """
        version: 2
        resources:
          - name: SceneHdr
            format: Rgba16Float
            usage: ColourTarget, Sampled
          - name: SceneDepth
            format: Depth32Float
            usage: DepthStencilTarget
          - name: ShadowAtlas
            format: Depth32Float
            usage: DepthStencilTarget, Sampled
            width: 64
            height: 64
        game: !Sequence
          name: Frame
          children:
            - !Terrain
              name: Ground
        """;

    /// <summary>Wires the frame the lit path detects: a scene camera and published cascades.</summary>
    SceneConstants LitFrame(CompositorBuilder builder) {
        var constants = new SceneConstants(device) {
            Lighting = new() {
                Camera = new RenderCamera(new(16f, 40f, 16f), new(0f, -0.5f, 0.5f), new(0f, 1f, 0f), MathF.PI / 3f, 1f, 0.1f, 1000f)
            }
        };

        // What ShadowMapRenderer.Publish writes, reduced to the one key detection reads and the
        // scalars the block copies.
        constants.Parameters.Set(
            ParameterKeys.New<Matrix4x4>("ForwardPlus.cascades[0].viewProjection"),
            Matrix4x4.Identity
        );

        constants.Parameters.Set(ParameterKeys.New<float>("ForwardPlus.cascades[0].split"), 60f);

        builder.SceneConstants = constants;

        return constants;
    }

    /// <summary>A frame that publishes its lighting gets the lit shaders; one that does not, the preview.</summary>
    [Fact]
    public void AFramePublishingItsLightingGetsTheLitShaders() {
        var (builder, factory) = Builder();
        var constants = LitFrame(builder);

        var compositor = builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(LitDocument));

        factory.Scene!.Terrains.Add(new(Map(), Vector3.Zero, 32f, 0, true, null, 0f));

        var node = Assert.IsType<TerrainSceneRenderer>(
            Assert.Single(Assert.IsType<SceneRendererSequence>(compositor.Game).Children)
        );

        Assert.Same(constants, node.Frame);

        Draw(compositor, shadowAtlas: true);

        Assert.True(node.Lit);
        Assert.False(node.Split);
        Assert.False(node.ClusteredLights);
        Assert.Equal(1, node.TerrainsDrawn);
        Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.DrawIndexed));

        node.Dispose();
        constants.Dispose();
    }

    /// <summary>Without a frame — the editor's case — the same document stays on the preview.</summary>
    [Fact]
    public void AFramePublishingNothingStaysOnThePreview() {
        var (builder, factory) = Builder();
        var compositor = builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(LitDocument));

        factory.Scene!.Terrains.Add(new(Map(), Vector3.Zero, 32f, 0, true, null, 0f));

        Draw(compositor, shadowAtlas: true);

        var node = Assert.IsType<TerrainSceneRenderer>(
            Assert.Single(Assert.IsType<SceneRendererSequence>(compositor.Game).Children)
        );

        Assert.False(node.Lit);
        Assert.Equal(1, node.TerrainsDrawn);

        node.Dispose();
    }

    /// <summary>A frame whose document declares no atlas is not lit, however much it publishes.</summary>
    [Fact]
    public void AFrameWithoutTheAtlasResourceStaysOnThePreview() {
        var (builder, factory) = Builder();
        var constants = LitFrame(builder);
        var compositor = builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(Document));

        factory.Scene!.Terrains.Add(new(Map(), Vector3.Zero, 32f, 0, true, null, 0f));

        Draw(compositor);

        var node = Assert.IsType<TerrainSceneRenderer>(
            Assert.Single(Assert.IsType<SceneRendererSequence>(compositor.Game).Children)
        );

        Assert.False(node.Lit);
        Assert.Equal(1, node.TerrainsDrawn);

        node.Dispose();
        constants.Dispose();
    }

    /// <summary>The split planes' presence is what binds them, and the ground still draws once.</summary>
    [Fact]
    public void ASplitFrameBindsTheSplitPlanes() {
        const string split = """
            version: 2
            resources:
              - name: SceneHdr
                format: Rgba16Float
                usage: ColourTarget, Sampled
              - name: SceneDepth
                format: Depth32Float
                usage: DepthStencilTarget
              - name: ShadowAtlas
                format: Depth32Float
                usage: DepthStencilTarget, Sampled
                width: 64
                height: 64
              - name: SceneAlbedo
                format: Rgba16Float
                usage: ColourTarget, Sampled
              - name: SceneNormals
                format: Rgba16Float
                usage: ColourTarget, Sampled
            game: !Sequence
              name: Frame
              children:
                - !Terrain
                  name: Ground
            """;

        var (builder, factory) = Builder();
        var constants = LitFrame(builder);
        var compositor = builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(split));

        factory.Scene!.Terrains.Add(new(Map(), Vector3.Zero, 32f, 0, true, null, 0f));

        Draw(compositor, shadowAtlas: true, splitPlanes: true);

        var node = Assert.IsType<TerrainSceneRenderer>(
            Assert.Single(Assert.IsType<SceneRendererSequence>(compositor.Game).Children)
        );

        Assert.True(node.Lit);
        Assert.True(node.Split);
        Assert.Equal(1, node.TerrainsDrawn);
        Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.DrawIndexed));

        node.Dispose();
        constants.Dispose();
    }

    /// <summary>Published cluster buffers turn the clustered variant on.</summary>
    [Fact]
    public void PublishedClusterBuffersTurnTheClusteredVariantOn() {
        var (builder, factory) = Builder();
        var constants = LitFrame(builder);

        // What the lighting feature and the Main pass's sceneBuffers line publish — the frame's
        // whole light list and the culled per-cluster lists.
        var lights = device.CreateBuffer(new(64, BufferUsage.Storage, MemoryAccess.HostUpload, "lights"));
        var clusters = device.CreateBuffer(new(64, BufferUsage.Storage, MemoryAccess.HostUpload, "clusters"));

        constants.Parameters.Set(ParameterKeys.New<BufferHandle>("ForwardPlus.lightBuffer"), lights);
        constants.Parameters.Set(ParameterKeys.New<BufferHandle>("ForwardPlus.clusters"), clusters);

        var compositor = builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(LitDocument));

        factory.Scene!.Terrains.Add(new(Map(), Vector3.Zero, 32f, 0, true, null, 0f));

        Draw(compositor, shadowAtlas: true);

        var node = Assert.IsType<TerrainSceneRenderer>(
            Assert.Single(Assert.IsType<SceneRendererSequence>(compositor.Game).Children)
        );

        Assert.True(node.Lit);
        Assert.True(node.ClusteredLights);
        Assert.Equal(1, node.TerrainsDrawn);

        node.Dispose();
        constants.Dispose();
    }

    // ------------------------------------------------------------------ the standard frame

    /// <summary>The node splices at the standard frame's afterOpaque seam and the result builds.</summary>
    /// <remarks>
    ///     The seam's contract: after the Main pass, sharing its depth, before the velocity and
    ///     particle passes — which is exactly where opaque ground that must occlude and be occluded
    ///     belongs. The second half builds the expanded document whole, so the splice is not just a
    ///     list position but a frame the builder accepts.
    /// </remarks>
    [Fact]
    public void TheNodeSplicesAtAfterOpaqueAndBuilds() {
        var expanded = PostEffectFactory.Transform(
            new() {
                Game = new StandardFrameAsset {
                    Quality = QualityTier.Low,
                    Shadows = ShadowMode.Off,
                    Gi = GiMode.Off,
                    Reflections = ReflectionsMode.Off,
                    Antialiasing = AntialiasingMode.Off,
                    Exposure = ExposureMode.Fixed,
                    Particles = false,
                    Extensions = new() { AfterOpaque = [new TerrainNodeAsset { Name = "Ground" }] }
                }
            },
            out _
        );

        var names = Assert.IsType<SequenceAsset>(expanded.Game).Children.Select(child => child.Name).ToArray();

        Assert.Equal(Array.IndexOf(names, "Main") + 1, Array.IndexOf(names, "Ground"));

        // And the expanded document builds: the terrain factory answers !Terrain, the effect
        // factory answers everything the expansion emitted.
        var (builder, factory) = Builder();

        builder.Factories.Insert(0, new PostEffectFactory());

        var compositor = builder.Build(expanded);
        var children = Assert.IsType<SceneRendererSequence>(compositor.Game).Children;
        var node = Assert.IsType<TerrainSceneRenderer>(children.Single(child => child.Name == "Ground"));

        Assert.Same(factory.Scene, node.Scene);
        Assert.Equal("SceneHdr", node.Output);
        Assert.Equal("SceneDepth", node.Depth);
    }

    // ------------------------------------------------------------------ fixture

    (CompositorBuilder Builder, TerrainFactory Factory) Builder() {
        var system = new RenderSystem();
        var builder = new CompositorBuilder(system);

        var view = Matrix4x4.LookAt(new(16f, 40f, 16f), new(32f, 0f, 32f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 10_000f);
        var camera = new RenderView("camera") { Position = new(16f, 40f, 16f) };

        camera.ViewProjection = view * projection;

        builder.Views["Camera"] = camera;
        builder.Device = device;
        builder.Modules = new(device);

        var factory = new TerrainFactory { Scene = new() };

        builder.Factories.Add(factory);

        return (builder, factory);
    }

    /// <summary>Builds the frame the compositor describes and submits it, so the recorder sees it.</summary>
    /// <remarks>
    ///     The two targets are imported rather than declared, because the node loads them: in a real
    ///     frame the Main pass wrote both before the ground joined, and a test frame with no Main
    ///     pass has to stand in for it the way a host stands in for a swapchain.
    /// </remarks>
    void Draw(GraphicsCompositor compositor, bool shadowAtlas = false, bool splitPlanes = false) {
        compositor.Imports["SceneHdr"] = Imported(PixelFormat.Rgba16Float, TextureUsage.ColourTarget | TextureUsage.Sampled, "hdr");
        compositor.Imports["SceneDepth"] = Imported(PixelFormat.Depth32Float, TextureUsage.DepthStencilTarget, "depth");

        if (shadowAtlas) {
            // Imported because the test frame has no shadow pass: in a real frame the cascades are
            // rendered before the ground reads them, and an import is how a test stands in for a
            // producer — the same trade the two targets above make for the Main pass.
            compositor.Imports["ShadowAtlas"] = Imported(
                PixelFormat.Depth32Float,
                TextureUsage.DepthStencilTarget | TextureUsage.Sampled,
                "atlas"
            );
        }

        if (splitPlanes) {
            compositor.Imports["SceneAlbedo"] = Imported(PixelFormat.Rgba16Float, TextureUsage.ColourTarget | TextureUsage.Sampled, "albedo");
            compositor.Imports["SceneNormals"] = Imported(PixelFormat.Rgba16Float, TextureUsage.ColourTarget | TextureUsage.Sampled, "normals");
        }

        var graph = new RenderGraph(device);
        var commands = device.BeginCommandList();

        try {
            compositor.Build(graph, effects, device);
            graph.Execute(commands);
        } finally {
            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }
    }

    ImportedTexture Imported(PixelFormat format, TextureUsage usage, string name) {
        var description = new TextureDescription(format, 64, 64, usage, Name: name);
        var texture = device.CreateTexture(description);

        return new(texture, device.CreateTextureView(texture), description);
    }

    static TerrainMap Map() =>
        new(
            TerrainDescription.Default with {
                TileSamples = 32, TilesX = 2, TilesZ = 2,
                MetresPerQuad = 1f, MinHeight = -100f, MaxHeight = 100f
            }
        );

    /// <summary>An asset source with one terrain and one grass rule, or neither.</summary>
    sealed class OneOfEach(TerrainMap? map, GrassType? grass = null) : ITerrainAssetSource {
        public TerrainMap? Terrain(string reference) => map;

        public GrassType? Grass(string reference) => grass ?? (map is null ? null : GrassType.Of("meadow"));
    }

    /// <summary>Answers every key, with the stages the shader's name implies.</summary>
    sealed class AlwaysCompiles : IEffectProvider {
        public Effect? TryGet(EffectKey key) =>
            new() {
                Key = key,
                Stages = key.ShaderName == "GrassScatter"
                    ? [new(ShaderStage.Compute, [1, 2, 3, 4], "main")]
                    : [
                        new(ShaderStage.Vertex, [1, 2, 3, 4], "main"),
                        new(ShaderStage.Fragment, [5, 6, 7, 8], "main")
                    ]
            };
    }
}
