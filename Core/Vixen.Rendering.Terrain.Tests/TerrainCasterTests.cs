// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;
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
///     Terrain shadow casting: the transformer's splice, the caster pass's place in the frame, and
///     the flag that turns it off.
/// </summary>
/// <remarks>
///     What a headless device can hold is the structure — the pass exists exactly when the frame
///     has an atlas and a casting terrain, it lands between the cascade pass and the passes that
///     sample the atlas, it loads rather than clears, and it draws one tile per cascade. Whether
///     the hills' shadows look like hills stays the golden image's job.
/// </remarks>
public sealed class TerrainCasterTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });
    readonly EffectSystem effects = new();

    public TerrainCasterTests() => effects.AddProvider(new AlwaysCompiles());

    public void Dispose() => device.Dispose();

    /// <summary>A frame with a sun: the shadow stage, the atlas, the cascade node, the ground.</summary>
    /// <remarks>
    ///     The atlas is 64×64 for four cascades at 32, which is <c>ShadowCascades.AtlasSize</c>'s
    ///     own arithmetic — any other extent is the shadow node's extent guard throwing, which is
    ///     its own test's business.
    /// </remarks>
    const string SunDocument = """
        version: 2
        stages:
          - name: Shadow
            shader: ShadowCaster
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
            - !ShadowMap
              name: Sun
              stage: Shadow
              atlas: ShadowAtlas
              view: Camera
              cascadeCount: 4
              resolution: 32
            - !Terrain
              name: Ground
        """;

    // ------------------------------------------------------------------ the splice

    /// <summary>The transformer inserts the caster node directly after the shadow node.</summary>
    [Fact]
    public void TheTransformSplicesACasterAfterTheShadowNode() {
        var (builder, factory) = Builder();
        var compositor = builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(SunDocument));

        var children = Assert.IsType<SceneRendererSequence>(compositor.Game).Children;
        var names = children.Select(child => child.Name).ToArray();

        Assert.Equal(Array.IndexOf(names, "Sun") + 1, Array.IndexOf(names, "Ground.Casters"));

        // And the pairing: the caster node borrows the surface node's draw sets.
        var casters = Assert.IsType<TerrainCasterRenderer>(children.Single(child => child.Name == "Ground.Casters"));
        var ground = Assert.IsType<TerrainSceneRenderer>(children.Single(child => child.Name == "Ground"));

        Assert.Same(ground, casters.Surfaces);
        Assert.Same(factory.Scene, casters.Scene);
    }

    /// <summary>A document with no shadow node gets no caster node — there is no atlas to write.</summary>
    [Fact]
    public void NoShadowNodeMeansNoCasterNode() {
        const string sunless = """
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

        var (builder, _) = Builder();
        var compositor = builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(sunless));
        var children = Assert.IsType<SceneRendererSequence>(compositor.Game).Children;

        Assert.DoesNotContain(children, child => child is TerrainCasterRenderer);
    }

    /// <summary>The standard frame's expansion carries the splice: sun, casters, main, ground.</summary>
    [Fact]
    public void TheStandardFrameSplicesTheCasterBetweenSunAndMain() {
        var (builder, _) = Builder();

        builder.Factories.Insert(0, new PostEffectFactory());

        var compositor = builder.Build(
            new GraphicsCompositorAsset {
                Game = new StandardFrameAsset {
                    Quality = QualityTier.Low,
                    Shadows = ShadowMode.Cascades,
                    Gi = GiMode.Off,
                    Reflections = ReflectionsMode.Off,
                    Antialiasing = AntialiasingMode.Off,
                    Exposure = ExposureMode.Fixed,
                    Particles = false,
                    Extensions = new() { AfterOpaque = [new TerrainNodeAsset { Name = "Ground" }] }
                }
            }
        );

        var names = Assert.IsType<SceneRendererSequence>(compositor.Game).Children
            .Select(child => child.Name)
            .ToArray();

        var sun = Array.IndexOf(names, "Sun");
        var casters = Array.IndexOf(names, "Ground.Casters");
        var main = Array.IndexOf(names, "Main");
        var ground = Array.IndexOf(names, "Ground");

        // The whole point of the sibling node: the terrain's depths are in the atlas before the
        // Main pass samples it, so the hills shadow the scene and not only themselves.
        Assert.True(sun >= 0 && casters == sun + 1, "the caster node does not follow the sun");
        Assert.True(casters < main, "the caster node builds after the Main pass that samples the atlas");
        Assert.True(main < ground, "the afterOpaque splice moved");
    }

    // ------------------------------------------------------------------ the pass

    /// <summary>
    ///     A casting terrain draws into the atlas: one pass after the cascades, a tile per cascade,
    ///     loaded rather than cleared.
    /// </summary>
    [Fact]
    public void ACastingTerrainDrawsATilePerCascade() {
        var (builder, factory) = Builder();
        var constants = LitFrame(builder);
        var compositor = builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(SunDocument));

        factory.Scene!.Terrains.Add(new(Map(), Vector3.Zero, 32f, 0, true, null, 0f));

        var children = Assert.IsType<SceneRendererSequence>(compositor.Game).Children;
        var casters = Assert.IsType<TerrainCasterRenderer>(children.Single(child => child.Name == "Ground.Casters"));

        // Frame one: the draw set is born during the surface node's build, after the caster's, and
        // its heightmap is not copied until the surface's own upload pass — so the caster skips it.
        Draw(compositor);

        Assert.Equal(0, casters.CastersDrawn);

        // Frame two: the set has uploaded once, and the caster draws it.
        device.Recorder!.Clear();

        var graph = Draw(compositor);

        Assert.Equal(4, casters.CascadeCount);
        Assert.Equal(1, casters.CastersDrawn);
        Assert.False(casters.WaitingForShaders);

        // A tile per cascade in the caster pass and a tile per cascade in the sun's own — eight
        // viewports, eight scissors, each 32 texels square.
        Assert.Equal(8, device.Recorder.OfKind(RecordedCommandKind.SetViewport).Count);
        Assert.Equal(8, device.Recorder.OfKind(RecordedCommandKind.SetScissor).Count);

        // One caster draw per cascade, and the surface's own draw beside them.
        Assert.Equal(5, device.Recorder.OfKind(RecordedCommandKind.DrawIndexed).Count);

        // Ordered: the cascades, then the terrain's depths on top of them, then the passes that
        // sample the atlas. Insertion order is execution order, and this is the recorded proof.
        var passes = device.Recorder.OfKind(RecordedCommandKind.BeginRenderPass)
            .Select(command => command.Text)
            .ToArray();

        Assert.True(
            Array.IndexOf(passes, "Sun") < Array.IndexOf(passes, "Ground.Casters"),
            "the caster pass ran before the cascades it merges into"
        );

        Assert.True(
            Array.IndexOf(passes, "Ground.Casters") < Array.IndexOf(passes, "Ground"),
            "the caster pass ran after the surface that samples the atlas"
        );

        // Loaded, never cleared — a caster that cleared would discard the sun's own casters, which
        // is exactly the frame the graph's discard lint describes. No warning is the assertion.
        Assert.Empty(graph.Warnings);

        children.OfType<TerrainSceneRenderer>().Single().Dispose();
        constants.Dispose();
    }

    /// <summary>A terrain whose component says no casts nothing — and the pass is never declared.</summary>
    [Fact]
    public void CastShadowsFalseDeclaresNoCasterPass() {
        var (builder, factory) = Builder();
        var constants = LitFrame(builder);
        var compositor = builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(SunDocument));

        factory.Scene!.Terrains.Add(new(Map(), Vector3.Zero, 32f, 0, false, null, 0f));

        var children = Assert.IsType<SceneRendererSequence>(compositor.Game).Children;
        var casters = Assert.IsType<TerrainCasterRenderer>(children.Single(child => child.Name == "Ground.Casters"));

        Draw(compositor);
        device.Recorder!.Clear();
        Draw(compositor);

        Assert.Equal(0, casters.CascadeCount);
        Assert.Equal(0, casters.CastersDrawn);

        var passes = device.Recorder.OfKind(RecordedCommandKind.BeginRenderPass)
            .Select(command => command.Text)
            .ToArray();

        // No pass at all: a declared-but-empty pass would still load and store the whole atlas.
        Assert.DoesNotContain("Ground.Casters", passes);

        // And the surface still draws — casting is the terrain's choice, being drawn is not.
        Assert.Contains("Ground", passes);

        children.OfType<TerrainSceneRenderer>().Single().Dispose();
        constants.Dispose();
    }

    /// <summary>A disabled sun takes its casters with it — there is no atlas being drawn to merge into.</summary>
    [Fact]
    public void DisablingTheSunDisablesTheCasters() {
        var (builder, factory) = Builder();
        var constants = LitFrame(builder);
        var compositor = builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(SunDocument));

        factory.Scene!.Terrains.Add(new(Map(), Vector3.Zero, 32f, 0, true, null, 0f));

        var children = Assert.IsType<SceneRendererSequence>(compositor.Game).Children;
        var casters = Assert.IsType<TerrainCasterRenderer>(children.Single(child => child.Name == "Ground.Casters"));

        children.Single(child => child.Name == "Sun").Enabled = false;

        Draw(compositor);
        Draw(compositor);

        Assert.Equal(0, casters.CascadeCount);
        Assert.Equal(0, casters.CastersDrawn);

        children.OfType<TerrainSceneRenderer>().Single().Dispose();
        constants.Dispose();
    }

    /// <summary>On a frame nothing lights, the unread atlas is culled and the casters with it.</summary>
    /// <remarks>
    ///     No SceneConstants means the surface stays on the preview and samples no atlas — so
    ///     nothing in the frame reads what the sun and the casters write, and the graph culls both
    ///     passes whole. The caster pass is declared and never runs, which is the cheapest kind of
    ///     wrong-frame there is.
    /// </remarks>
    [Fact]
    public void AnUnlitFrameCullsTheCasterPass() {
        var (builder, factory) = Builder();
        var compositor = builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(SunDocument));

        factory.Scene!.Terrains.Add(new(Map(), Vector3.Zero, 32f, 0, true, null, 0f));

        var children = Assert.IsType<SceneRendererSequence>(compositor.Game).Children;
        var casters = Assert.IsType<TerrainCasterRenderer>(children.Single(child => child.Name == "Ground.Casters"));

        Draw(compositor);
        device.Recorder!.Clear();
        Draw(compositor);

        Assert.Equal(0, casters.CastersDrawn);

        var passes = device.Recorder.OfKind(RecordedCommandKind.BeginRenderPass)
            .Select(command => command.Text)
            .ToArray();

        Assert.DoesNotContain("Ground.Casters", passes);
        Assert.DoesNotContain("Sun", passes);

        children.OfType<TerrainSceneRenderer>().Single().Dispose();
    }

    // ------------------------------------------------------------------ the conventions

    /// <summary>The caster rasterises on the engine's terms: reverse-Z, back-culled, zero raster bias.</summary>
    /// <remarks>
    ///     The source-level half of the parity the atlas depends on. The cascade pass's mesh
    ///     casters and the terrain merge through one depth test, so the caster's compare must be
    ///     the engine's <c>Greater</c> — near is 1, the clear is 0 — and its biases must be absent,
    ///     because the frame's biases are added in the sampling, in metres, per cascade.
    /// </remarks>
    [Fact]
    public void TheCasterKeepsTheReverseZAndCullConventions() {
        Assert.True(TerrainCasterPass.DepthState.DepthTest);
        Assert.True(TerrainCasterPass.DepthState.DepthWrite);
        Assert.Equal(CompareFunction.Greater, TerrainCasterPass.DepthState.DepthCompare);

        Assert.Equal(CullMode.Back, TerrainCasterPass.Raster.Cull);
        Assert.Equal(0f, TerrainCasterPass.Raster.DepthBias);
        Assert.Equal(0f, TerrainCasterPass.Raster.DepthBiasSlope);
        Assert.True(TerrainCasterPass.Raster.DepthClamp);
    }

    /// <summary>The caster shader honours the holes: a fragment stage exists, and it discards.</summary>
    /// <remarks>
    ///     Source-read, <c>ForwardFrameTests</c>' idiom for what reflection cannot see: a caster
    ///     whose fragment stage was dropped would cast a solid shadow out of every cave mouth, and
    ///     nothing else in the frame would fail.
    /// </remarks>
    [Fact]
    public void TheCasterShaderDiscardsHoles() {
        var source = File.ReadAllText(RavenSource("Terrain", "Terrain.rvn"));
        var caster = source[source.IndexOf("shader TerrainCaster", StringComparison.Ordinal)..];

        caster = caster[..caster.IndexOf("\nshader ", StringComparison.Ordinal)];

        Assert.Contains("[FragmentShader]", caster, StringComparison.Ordinal);
        Assert.Contains("if (Hole(sampleCoord))", caster, StringComparison.Ordinal);
        Assert.Contains("discard", caster, StringComparison.Ordinal);

        // And no colour output — a target with no attachment behind it warns once per draw.
        Assert.DoesNotContain("[Semantic(\"SV_Target\")]", caster, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ fixture

    static string RavenSource(string package, string file) {
        var root = AppContext.BaseDirectory;

        while (root is not null && !Directory.Exists(Path.Combine(root, "Raven", "Library"))) {
            root = Path.GetDirectoryName(root);
        }

        Assert.NotNull(root);

        return Path.Combine(root, "Raven", "Library", package, file);
    }

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

    /// <summary>Wires the frame the lit path detects: a scene camera for the block's values.</summary>
    /// <remarks>
    ///     Only the camera — the cascades themselves are published by the document's own <c>Sun</c>
    ///     node during collect, folded and unfolded both, which is the path a real frame takes.
    /// </remarks>
    SceneConstants LitFrame(CompositorBuilder builder) {
        var constants = new SceneConstants(device) {
            Lighting = new() {
                Camera = new RenderCamera(new(16f, 40f, 16f), new(0f, -0.5f, 0.5f), new(0f, 1f, 0f), MathF.PI / 3f, 1f, 0.1f, 1000f)
            }
        };

        builder.SceneConstants = constants;

        return constants;
    }

    /// <summary>Builds the frame and submits it, handing back the graph for its warnings.</summary>
    RenderGraph Draw(GraphicsCompositor compositor) {
        compositor.Imports["SceneHdr"] = Imported(PixelFormat.Rgba16Float, TextureUsage.ColourTarget | TextureUsage.Sampled, "hdr");
        compositor.Imports["SceneDepth"] = Imported(PixelFormat.Depth32Float, TextureUsage.DepthStencilTarget, "depth");

        var graph = new RenderGraph(device);
        var commands = device.BeginCommandList();

        try {
            compositor.Build(graph, effects, device);
            graph.Execute(commands);
        } finally {
            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        return graph;
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
