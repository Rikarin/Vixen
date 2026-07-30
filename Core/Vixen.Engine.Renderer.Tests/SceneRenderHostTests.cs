// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;
using Vixen.Engine.Renderer;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Features;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     A document, a device and a scene become a recorded frame.
/// </summary>
/// <remarks>
///     <para>
///         <b>The claim is that the stack runs, which nothing had ever asserted.</b> Every piece was
///         tested — features extract, a document builds a compositor, a graph orders passes — and
///         <c>new RenderSystem()</c> appeared only inside test projects. A renderer whose parts all pass
///         and whose whole has never run is a renderer with an unknown number of missing lines in it.
///     </para>
///     <para>
///         Recorded rather than submitted, because that is the host's contract: it fills a list the
///         caller owns and has no opinion about when a frame is presented.
///     </para>
/// </remarks>
public sealed class SceneRenderHostTests : IDisposable {
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
          - name: Opaque
        game: !Sequence
          name: Frame
          children:
            - !RenderPass
              name: Main
              colourTargets: [SceneColour]
              depthTarget: SceneDepth
              children:
                - !SingleStage
                  name: OpaqueDraw
                  view: Camera
                  stage: Opaque
        """;

    /// <summary>
    ///     A host with a document and a target records a frame.
    /// </summary>
    /// <remarks>
    ///     The end-to-end shape in nine lines: build the host, register a feature, name the swapchain
    ///     image, draw. Everything a sample does beyond this is content.
    /// </remarks>
    [Fact]
    public void A_document_and_a_target_make_a_frame() {
        using var host = Build();

        Assert.Equal(0, host.FrameCount);

        var list = device.BeginCommandList();

        Assert.True(host.Draw(list));

        list.Finish();
        device.GraphicsQueue.Submit([list]);

        Assert.Equal(1, host.FrameCount);

        // A render pass was opened, which is the graph having compiled the document's one pass and the
        // list having executed it. Without the import there is nothing to draw into and the graph is
        // right to cull the pass, so this also asserts the import took.
        Assert.True(
            device.Recorder?.CountOf(RecordedCommandKind.BeginRenderPass) > 0,
            "The frame recorded no render pass, so the document's pass reached no command list."
        );
    }

    /// <summary>
    ///     Every feature is extracted once a frame, not twice.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The mistake this host exists to not make.</b> The obvious frame is "run the render
    ///         system's phases, then build the graph", and <c>GraphicsCompositor.Build</c> already does
    ///         both — it collects the frame's views from its own nodes and then calls
    ///         <c>RenderSystem.Draw</c>, in that order, because culling before the views are collected
    ///         culls against the previous frame's.
    ///     </para>
    ///     <para>
    ///         A host that ran the phases itself would therefore extract everything twice per frame and
    ///         cull the first pass against stale views: a correct-looking picture, and a renderer that
    ///         profiles as half as fast for no visible reason. Counting is the only way to see it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_feature_is_extracted_once_a_frame() {
        using var host = Build();

        var counting = new CountingFeature();
        host.System.AddFeature(counting);

        var first = device.BeginCommandList();
        host.Draw(first);
        first.Finish();

        Assert.Equal(1, counting.Extractions);
        Assert.Equal(1, counting.Preparations);

        // And the counts rise per frame rather than per build, so nothing is being cached across one.
        var second = device.BeginCommandList();
        host.Draw(second);
        second.Finish();

        Assert.Equal(2, counting.Extractions);
        Assert.Equal(2, counting.Preparations);
    }

    /// <summary>
    ///     A host with no document draws nothing and says so, rather than throwing.
    /// </summary>
    /// <remarks>
    ///     A host that has not finished starting is ordinary — an application loads its compositor
    ///     asynchronously and renders before it lands. The same reading every node in the compositor
    ///     gives a resource nobody supplied.
    /// </remarks>
    [Fact]
    public void A_host_with_no_document_draws_nothing() {
        using var host = new SceneRenderHost(device, effects);

        var list = device.BeginCommandList();

        Assert.False(host.Draw(list));
        Assert.Equal(0, host.FrameCount);

        list.Finish();

        Assert.Throws<InvalidOperationException>(() => host.Import("SceneColour", default));
    }

    /// <summary>
    ///     The virtualized stack supplies the two nodes a document names, in one call.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Five references, and a node built without any one of them does nothing quietly — which is
    ///         right for a project with no virtualized geometry and is a bug in one that has some. The
    ///         point of <c>Supply</c> is that a host cannot do four of the five.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_virtualized_stack_supplies_the_documents_nodes() {
        using var host = new SceneRenderHost(device, effects);
        using var geometry = new VirtualGeometrySystem(device, slots: 8, pageSize: 8 * 1024);

        geometry.Effects = effects;
        geometry.Register(host.System);
        geometry.Supply(host.Builder);

        host.Load(YamlSerializer.Parse<GraphicsCompositorAsset>(ClusterDocument));

        Assert.NotNull(host.Compositor);

        var children = Assert.IsType<SceneRendererSequence>(host.Compositor.Game).Children;

        Assert.Same(geometry.Visibility, Assert.IsType<ClusterCullingRenderer>(children[0]).Visibility);
        Assert.Same(geometry.Pages, Assert.IsType<ClusterCullingRenderer>(children[0]).Pages);
        Assert.Same(geometry.Raster, Assert.IsType<VisibilityBufferRenderer>(children[1]).Raster);
        Assert.Same(geometry.Tiles, Assert.IsType<VisibilityBufferRenderer>(children[1]).Tiles);
        Assert.Same(geometry.Resolve, Assert.IsType<VisibilityBufferRenderer>(children[1]).Resolve);

        // And the feature is in the system, so an object can name a mesh through it.
        Assert.Contains(host.System.Features, feature => ReferenceEquals(feature, geometry.Feature));

        // One assignment reaches all four passes, which is the property that makes a partly-wired
        // system impossible rather than merely unlikely.
        Assert.Same(effects, geometry.Visibility.Effects);
        Assert.Same(effects, geometry.Raster.Effects);
        Assert.Same(effects, geometry.Tiles.Effects);
        Assert.Same(effects, geometry.Resolve.Effects);
    }

    const string ClusterDocument = """
        version: 2
        resources:
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
              depth: SceneDepth
              colour: SceneColour
        """;

    SceneRenderHost Build() {
        var host = new SceneRenderHost(device, effects);

        host.Builder.Views["Camera"] = new("camera");
        host.Load(YamlSerializer.Parse<GraphicsCompositorAsset>(Document));
        host.FrameSize = new(64, 64);

        var description = new TextureDescription(
            PixelFormat.Rgba16Float,
            64,
            64,
            TextureUsage.ColourTarget | TextureUsage.Sampled,
            Name: "SceneColour"
        );

        var texture = device.CreateTexture(description);

        host.Import("SceneColour", new(texture, device.CreateTextureView(texture), description));

        return host;
    }

    /// <summary>A feature that does nothing but count what it was asked.</summary>
    sealed class CountingFeature : RootRenderFeature {
        public int Extractions { get; private set; }
        public int Preparations { get; private set; }

        public override string Name => "Counting";

        protected override void Extract(RenderSystem system) => Extractions++;

        protected override void Prepare(RenderSystem system) => Preparations++;
    }

    /// <inheritdoc />
    public void Dispose() => device.Dispose();
}
