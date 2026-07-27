// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Serialization;
using Vixen.Core.Yaml;
using Vixen.Graphics;
using Vixen.Graphics.Null;
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
        version: 1
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

    sealed class Harness : IDisposable {
        public required RenderSystem System { get; init; }
        public required CompositorBuilder Builder { get; init; }
        public required RenderView Camera { get; init; }
        public required MeshRenderFeature Meshes { get; init; }
        public required MaterialRenderFeature Materials { get; init; }
        public required BufferHandle Vertices { get; init; }

        public void Dispose() => System.Dispose();
    }

    TextureViewHandle Target(PixelFormat format, TextureUsage usage) =>
        device.CreateTextureView(
            device.CreateTexture(
                new() {
                    Width = 512, Height = 512, Depth = 1,
                    MipLevels = 1, ArrayLayers = 1, SampleCount = 1,
                    Format = format, Usage = usage
                }
            )
        );

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

        builder.ColourTargets["SceneColour"] =
            new(Target(PixelFormat.Rgba16Float, TextureUsage.ColourTarget), PixelFormat.Rgba16Float);

        builder.DepthTargets["SceneDepth"] =
            new(Target(PixelFormat.Depth32Float, TextureUsage.DepthStencilTarget), PixelFormat.Depth32Float);

        builder.DepthTargets["ShadowAtlas"] =
            new(Target(PixelFormat.Depth32Float, TextureUsage.DepthStencilTarget), PixelFormat.Depth32Float);

        return new() {
            System = system,
            Builder = builder,
            Camera = camera,
            Meshes = meshes,
            Materials = materials,
            Vertices = device.CreateBuffer(new() { Size = 1024, Usage = BufferUsage.Vertex })
        };
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

    void Frame(GraphicsCompositor compositor) {
        var list = device.BeginCommandList();
        compositor.Draw(new(list, effects) { Device = device });
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

        Assert.Equal(1, asset.Version);
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

        var compositor = h.Builder.Build(asset);
        var everywhere = h.Builder.Stages.Values.Aggregate(RenderStageMask.None, (mask, stage) => mask | stage.Mask);

        AddMesh(h, -10f, new Material("Lit"), everywhere);

        Frame(compositor);

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

        var compositor = h.Builder.Build(asset);
        var opaque = h.Builder.Stages["Opaque"];
        var transparent = h.Builder.Stages["Transparent"];

        AddMesh(h, -10f, new Material("Lit"), opaque.Mask | transparent.Mask);

        Frame(compositor);

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

        var compositor = h.Builder.Build(asset);
        compositor.Collect();

        Assert.Equal(3, compositor.Views.Count);
        Assert.Contains(h.Camera, compositor.Views);
    }

    // --- Refusals -----------------------------------------------------------

    /// <summary>An unbound name names the node, the kind and the name.</summary>
    /// <remarks>
    ///     Binding what it can and skipping the rest would produce a frame missing a pass and report
    ///     nothing — the failure that takes a day to find.
    /// </remarks>
    [Fact]
    public void An_unbound_name_is_refused_by_name() {
        var asset = YamlSerializer.Parse<GraphicsCompositorAsset>(Document);
        using var h = Build();

        h.Builder.ColourTargets.Remove("SceneColour");

        var thrown = Assert.Throws<CompositorBindingException>(() => h.Builder.Build(asset));

        Assert.Equal("Main", thrown.Node);
        Assert.Equal("colour target", thrown.Kind);
        Assert.Equal("SceneColour", thrown.Name);
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
        var asset = YamlSerializer.Parse<GraphicsCompositorAsset>(Document) with { Version = 2 };
        using var h = Build();

        var thrown = Assert.Throws<NotSupportedException>(() => h.Builder.Build(asset));

        Assert.Contains("version 2", thrown.Message, StringComparison.Ordinal);
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

        Assert.Equal(1, loaded.Version);
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
        var compositor = h.Builder.Build(loaded);
        var everywhere = h.Builder.Stages.Values.Aggregate(RenderStageMask.None, (mask, stage) => mask | stage.Mask);

        AddMesh(h, -10f, new Material("Lit"), everywhere);

        Frame(compositor);

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
}
