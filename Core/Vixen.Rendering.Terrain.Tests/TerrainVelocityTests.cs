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
///     The ground stack's motion vectors: the transformer's splice, the reprojection's place in the
///     frame, and the availability detection that turns it on.
/// </summary>
/// <remarks>
///     What a headless device can hold is the structure — the node exists exactly when the frame
///     draws a <c>Motion</c> stage, its pass lands after the frame's velocity pass (whose clear
///     would otherwise wipe the ground's vectors), it loads rather than clears, and it draws what
///     the surface staged. Whether the reprojection stops TAA's ghosting stays a real device's job.
/// </remarks>
public sealed class TerrainVelocityTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });
    readonly EffectSystem effects = new();

    public TerrainVelocityTests() => effects.AddProvider(new AlwaysCompiles());

    public void Dispose() => device.Dispose();

    /// <summary>A frame with a velocity pass: the Motion stage, the motion plane, the ground.</summary>
    /// <remarks>The velocity pass is <c>!StandardFrame</c>'s, transliterated: the motion plane
    ///     cleared, the scene depth loaded read-only, the Motion stage under the camera.</remarks>
    const string MotionDocument = """
        version: 2
        stages:
          - name: Motion
            shader: MotionVectors
            depth: TestOnly
        resources:
          - name: SceneHdr
            format: Rgba16Float
            usage: ColourTarget, Sampled
          - name: SceneDepth
            format: Depth32Float
            usage: DepthStencilTarget
          - name: SceneMotion
            format: Rg16Float
            usage: ColourTarget, Sampled
        game: !Sequence
          name: Frame
          children:
            - !Terrain
              name: Ground
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

    /// <summary>The same frame with no velocity pass — the ordinary, motionless case.</summary>
    const string StillDocument = """
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

    // ------------------------------------------------------------------ the splice

    /// <summary>The transformer inserts the velocity node directly after the frame's velocity pass.</summary>
    [Fact]
    public void TheTransformSplicesAVelocityNodeAfterTheVelocityPass() {
        var (builder, factory) = Builder();
        var compositor = builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(MotionDocument));

        var children = Assert.IsType<SceneRendererSequence>(compositor.Game).Children;
        var names = children.Select(child => child.Name).ToArray();

        Assert.Equal(Array.IndexOf(names, "Velocity") + 1, Array.IndexOf(names, "Ground.Velocity"));

        // And the pairing, both ways: the velocity node borrows the surface node's staged passes,
        // and the surface node learns which plane the frame's velocity pass writes.
        var velocity = Assert.IsType<TerrainVelocityRenderer>(children.Single(child => child.Name == "Ground.Velocity"));
        var ground = Assert.IsType<TerrainSceneRenderer>(children.Single(child => child.Name == "Ground"));

        Assert.Same(ground, velocity.Surfaces);
        Assert.Same(velocity, ground.VelocitySibling);
        Assert.Equal("SceneMotion", velocity.Motion);
        Assert.Equal("SceneDepth", velocity.Depth);
        Assert.Same(factory.Scene, ground.Scene);
    }

    /// <summary>A frame with no Motion stage gets no velocity node — there is no plane to join.</summary>
    [Fact]
    public void NoVelocityPassMeansNoVelocityNode() {
        var (builder, _) = Builder();
        var compositor = builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(StillDocument));
        var children = Assert.IsType<SceneRendererSequence>(compositor.Game).Children;

        Assert.DoesNotContain(children, child => child is TerrainVelocityRenderer);
    }

    /// <summary>The standard frame under TAA carries the splice: main, ground, velocity, reprojection.</summary>
    [Fact]
    public void TheStandardFrameSplicesTheVelocityNodeUnderTaa() {
        var (builder, _) = Builder();

        builder.Factories.Insert(0, new PostEffectFactory());

        var compositor = builder.Build(
            new GraphicsCompositorAsset {
                Game = new StandardFrameAsset {
                    Quality = QualityTier.Low,
                    Shadows = ShadowMode.Off,
                    Gi = GiMode.Off,
                    Reflections = ReflectionsMode.Off,
                    Antialiasing = AntialiasingMode.Taa,
                    Exposure = ExposureMode.Fixed,
                    Particles = false,
                    Extensions = new() { AfterOpaque = [new TerrainNodeAsset { Name = "Ground" }] }
                }
            }
        );

        var names = Assert.IsType<SceneRendererSequence>(compositor.Game).Children
            .Select(child => child.Name)
            .ToArray();

        var ground = Array.IndexOf(names, "Ground");
        var velocity = Array.IndexOf(names, "Velocity");
        var reprojection = Array.IndexOf(names, "Ground.Velocity");

        // The whole point of the sibling node: the frame's velocity pass clears the motion plane,
        // so the ground's vectors must land after it — however early the ground itself drew.
        Assert.True(ground >= 0 && ground < velocity, "the afterOpaque splice moved");
        Assert.True(reprojection == velocity + 1, "the reprojection does not follow the velocity pass");
    }

    /// <summary>The same frame without TAA emits no velocity pass and therefore no reprojection.</summary>
    [Fact]
    public void TheStandardFrameWithoutTaaSplicesNothing() {
        var (builder, _) = Builder();

        builder.Factories.Insert(0, new PostEffectFactory());

        var compositor = builder.Build(
            new GraphicsCompositorAsset {
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
            }
        );

        Assert.DoesNotContain(
            Assert.IsType<SceneRendererSequence>(compositor.Game).Children,
            child => child is TerrainVelocityRenderer
        );
    }

    // ------------------------------------------------------------------ the pass

    /// <summary>
    ///     A terrain reprojects: one pass after the frame's velocity pass, loaded rather than
    ///     cleared, drawing the patches the surface staged.
    /// </summary>
    [Fact]
    public void ATerrainReprojectsAfterTheFramesVelocityPass() {
        var (builder, factory) = Builder();
        var compositor = builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(MotionDocument));

        factory.Scene!.Terrains.Add(new(Map(), Vector3.Zero, 32f, 0, true, null, 0f));

        var children = Assert.IsType<SceneRendererSequence>(compositor.Game).Children;
        var velocity = Assert.IsType<TerrainVelocityRenderer>(children.Single(child => child.Name == "Ground.Velocity"));
        var ground = Assert.IsType<TerrainSceneRenderer>(children.Single(child => child.Name == "Ground"));

        var graph = Draw(compositor);

        Assert.True(ground.MotionVectors);
        Assert.Equal(1, velocity.VelocityDraws);

        // Ordered: the ground's colour, the frame's velocity pass with its clear, and only then
        // the ground's vectors — recorded there because the clear would wipe them anywhere earlier.
        var passes = device.Recorder!.OfKind(RecordedCommandKind.BeginRenderPass)
            .Select(command => command.Text)
            .ToArray();

        Assert.True(
            Array.IndexOf(passes, "Ground") < Array.IndexOf(passes, "Velocity"),
            "the ground drew after the frame's velocity pass"
        );

        Assert.True(
            Array.IndexOf(passes, "Velocity") < Array.IndexOf(passes, "Ground.Velocity"),
            "the reprojection recorded before the clear that would wipe it"
        );

        // Loaded, never cleared — a reprojection that cleared would discard the extracted meshes'
        // own vectors. No warning is the assertion, the caster test's own idiom.
        Assert.Empty(graph.Warnings);

        ground.Dispose();
    }

    /// <summary>A frame with no motion plane stages nothing and the detection stays off.</summary>
    [Fact]
    public void AStillFrameStagesNoVelocity() {
        var (builder, factory) = Builder();
        var compositor = builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(StillDocument));

        factory.Scene!.Terrains.Add(new(Map(), Vector3.Zero, 32f, 0, true, null, 0f));

        Draw(compositor, motion: false);

        var ground = Assert.IsType<TerrainSceneRenderer>(
            Assert.Single(Assert.IsType<SceneRendererSequence>(compositor.Game).Children)
        );

        Assert.False(ground.MotionVectors);
        Assert.Equal(1, ground.TerrainsDrawn);

        ground.Dispose();
    }

    // ------------------------------------------------------------------ the conventions

    /// <summary>The reprojection rasterises on its own terms: tested, never written, equal wins.</summary>
    /// <remarks>
    ///     The source-level half of the depth contract. The pass re-rasterises geometry that is
    ///     already the nearest thing in the depth buffer, so its fragments arrive <em>at</em> the
    ///     stored depth — the frame stages' strict <c>Greater</c> would reject every one and the
    ///     pass would silently write nothing.
    /// </remarks>
    [Fact]
    public void TheVelocityPassTestsButNeverWritesDepth() {
        Assert.True(TerrainVelocityPass.DepthState.DepthTest);
        Assert.False(TerrainVelocityPass.DepthState.DepthWrite);
        Assert.Equal(CompareFunction.GreaterEqual, TerrainVelocityPass.DepthState.DepthCompare);
    }

    /// <summary>The three velocity shaders keep the conventions the pass depends on.</summary>
    /// <remarks>
    ///     Source-read, <c>TerrainCasterTests</c>' idiom for what reflection cannot see: the divide
    ///     deferred to the fragment stage, the discards matching the colour passes' exactly, and
    ///     the grass evaluating its wind at the previous clock — each one a silent wrong picture
    ///     rather than an error if it drifts.
    /// </remarks>
    [Fact]
    public void TheVelocityShadersKeepTheReprojectionConventions() {
        var terrain = ShaderSource("Terrain.rvn", "TerrainVelocity");

        // Static reprojection through both matrices, holes discarded like every terrain fragment.
        Assert.Contains("previousViewProjection * float4(positionWS, 1f)", terrain, StringComparison.Ordinal);
        Assert.Contains("if (Hole(sampleCoord))", terrain, StringComparison.Ordinal);

        var grass = ShaderSource("Grass.rvn", "GrassVelocity");

        // The wind at last frame's clock — the sway's own motion — and the colour pass's coverage.
        // The stipple moved inside `GrassBlade.Cutout` when the alpha test joined it, so what is
        // asserted here is that this stage discards through the shared predicate at all;
        // `TerrainShaderParityTests` is what holds the predicate itself to one copy.
        Assert.Contains("Displacement.WindPhased(scaled, world, wind, previousTime, blade.windPhase)", grass, StringComparison.Ordinal);
        Assert.Contains("if (Cutout(", grass, StringComparison.Ordinal);

        var foliage = ShaderSource("Foliage.rvn", "FoliageVelocity");

        // Static reprojection, and the same cutout the foliage colour pass takes.
        Assert.Contains("previousViewProjection * float4(positionWS, 1f)", foliage, StringComparison.Ordinal);
        Assert.Contains("if (Cutout(", foliage, StringComparison.Ordinal);

        // All three write the from-here-to-there offset in UV — Taa.rvn samples history at
        // `uv + motion`, so the sign convention is MotionVectors.rvn's, verbatim.
        foreach (var source in (string[])[terrain, grass, foliage]) {
            Assert.Contains("(there - here) * float2(0.5f, -0.5f)", source, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------------ fixture

    static string ShaderSource(string file, string shader) {
        var root = AppContext.BaseDirectory;

        while (root is not null && !Directory.Exists(Path.Combine(root, "Raven", "Library"))) {
            root = Path.GetDirectoryName(root);
        }

        Assert.NotNull(root);

        var source = File.ReadAllText(Path.Combine(root, "Raven", "Library", "Terrain", file));
        var section = source[source.IndexOf($"shader {shader}", StringComparison.Ordinal)..];
        var next = section.IndexOf("\nshader ", StringComparison.Ordinal);

        return next < 0 ? section : section[..next];
    }

    (CompositorBuilder Builder, TerrainFactory Factory) Builder() {
        var system = new RenderSystem();
        var builder = new CompositorBuilder(system);

        var view = Matrix4x4.LookAt(new(16f, 40f, 16f), new(32f, 0f, 32f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 10_000f);
        var camera = new RenderView("camera") { Position = new(16f, 40f, 16f) };

        camera.ViewProjection = view * projection;

        // A frame of history, so the reprojection compares two real matrices — the extraction
        // system's own Advance, made by the host a test stands in for.
        camera.Advance();

        builder.Views["Camera"] = camera;
        builder.Device = device;
        builder.Modules = new(device);

        var factory = new TerrainFactory { Scene = new() };

        builder.Factories.Add(factory);

        return (builder, factory);
    }

    /// <summary>Builds the frame and submits it, handing back the graph for its warnings.</summary>
    /// <remarks>The motion plane is imported like the two scene targets: in a real frame TAA reads
    ///     it, and a test frame with no resolve stands in for the reader the way a host stands in
    ///     for a swapchain — without it the graph would cull every velocity pass as unread.</remarks>
    RenderGraph Draw(GraphicsCompositor compositor, bool motion = true) {
        compositor.Imports["SceneHdr"] = Imported(PixelFormat.Rgba16Float, TextureUsage.ColourTarget | TextureUsage.Sampled, "hdr");
        compositor.Imports["SceneDepth"] = Imported(PixelFormat.Depth32Float, TextureUsage.DepthStencilTarget, "depth");

        if (motion) {
            compositor.Imports["SceneMotion"] = Imported(PixelFormat.Rg16Float, TextureUsage.ColourTarget | TextureUsage.Sampled, "motion");
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
