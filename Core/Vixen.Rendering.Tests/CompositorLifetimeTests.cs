// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
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
///     Who frees a compositor node's device memory, and what happens on the reload that replaces it.
/// </summary>
/// <remarks>
///     <para>
///         <b>The defect this locks down is a mechanism that existed and nothing called.</b> Two
///         shadow nodes own device textures rather than renting them from the graph's pool — a cached
///         atlas has to survive the frame, and every <c>!Resource</c> in a document is transient —
///         and both implemented <c>IDisposable</c>, and both were tested for it. Nothing disposed
///         them. Rebuilding a <c>.vxcompositor</c> made a new tree and dropped the old one on the
///         floor: 4096² and 2816² of <c>Depth32Float</c> in sample 13, which is 94 MiB, per reload,
///         for as long as the process runs.
///     </para>
///     <para>
///         So the rule these assert is: <b>a compositor owns what was built for it</b>. Every node
///         <see cref="CompositorBuilder" /> makes passes through one method and is claimed there, so
///         a node kind that does not exist yet is covered by the same line as the two that forced the
///         question — including the kinds a factory in another assembly builds. What a
///         <em>host</em> made and appended is not the compositor's, which is the half a walk over
///         <see cref="SceneRenderer.Nested" /> would have got wrong.
///     </para>
///     <para>
///         <see cref="NullDevice.LiveResourceCount" /> is the instrument, and it is the one that file
///         documents itself for: run the cycle a hundred times and the count comes back to where it
///         started, or something is not returning what it took.
///     </para>
/// </remarks>
public class CompositorLifetimeTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });
    readonly EffectSystem effects = new();

    /// <summary>
    ///     A frame whose nodes own device memory: two cached shadow atlases and a per-view block.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>staticStage:</c> on the cascades and <c>cached: true</c> on the lamps are the two
    ///         lines that make the nodes own a texture each rather than draw into the transient the
    ///         document declares — which is exactly sample 13's arrangement, at a size a test can
    ///         afford.
    ///     </para>
    ///     <para>
    ///         The <c>viewBlock:</c> section is here because it leaks on the same path and was found
    ///         with the nodes: it is a uniform buffer and a descriptor set layout per build, made by
    ///         the builder rather than by any node, and <c>ViewConstants.Dispose</c> frees the first
    ///         and not the second.
    ///     </para>
    /// </remarks>
    const string Document = """
        version: 2
        viewBlock:
          set: 1
          binding: 0
          size: 256
        resources:
          - name: SceneColour
            format: Rgba16Float
            usage: ColourTarget, Sampled
        stages:
          - name: ShadowCaster
          - name: StaticCaster
          - name: LampCaster
        game: !Sequence
          name: Frame
          children:
            - !ShadowMap
              name: Sun
              stage: ShadowCaster
              staticStage: StaticCaster
              atlas: ShadowAtlas
              cascadeCount: 2
              resolution: 256
            - !PunctualShadows
              name: Lamps
              stage: LampCaster
              atlas: PunctualShadowAtlas
              resolution: 64
              tilesPerSide: 4
              cached: true
        """;

    /// <summary>
    ///     Reloading a document a dozen times leaves the device holding exactly what one build holds.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The measurement, and the shape of the answer is the point rather than the number.</b>
    ///         Before the ownership existed this read 12, 18, 24, 30, 36 — six device objects per
    ///         reload, growing without a ceiling: a texture and a view for each of the two caches, the
    ///         view block's buffer, and the set layout nothing destroyed. Now every round ends where
    ///         the first one did.
    ///     </para>
    ///     <para>
    ///         A frame is drawn before each reload because that is when a cache allocates — a node
    ///         that never built never took anything, so a reload loop with no frame in it would pass
    ///         against the leak it exists to catch.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Reloading_a_document_does_not_grow_the_devices_resource_count() {
        using var h = Harness();

        var asset = YamlSerializer.Parse<GraphicsCompositorAsset>(Document);
        var counts = new List<int>();

        for (var round = 0; round < 12; round++) {
            var compositor = Compose(h, asset);

            Frame(h, compositor);
            counts.Add(device.LiveResourceCount);
            compositor.Dispose();
        }

        Assert.All(counts, count => Assert.Equal(counts[0], count));

        // And the frame did own something, so the flat line above is not a document that built no
        // nodes at all: the two shadow nodes, the view block and its layout.
        Assert.Equal(4, Compose(h, asset).OwnedCount);
    }

    /// <summary>Disposing the compositor gives every one of those objects back.</summary>
    /// <remarks>
    ///     The other half of the assertion above, which on its own could be satisfied by a build that
    ///     allocated nothing: the count returns to what it was before the document was ever built,
    ///     rather than merely stopping where it stopped.
    /// </remarks>
    [Fact]
    public void Disposing_a_compositor_returns_everything_its_nodes_took() {
        using var h = Harness();

        var before = device.LiveResourceCount;
        var compositor = Compose(h, asset: YamlSerializer.Parse<GraphicsCompositorAsset>(Document));

        Frame(h, compositor);
        Assert.True(device.LiveResourceCount > before, "the frame's nodes took nothing, so this proves nothing");

        compositor.Dispose();
        Assert.Equal(before, device.LiveResourceCount);

        // Idempotent, because a host that disposes a compositor it has also replaced should not have
        // to remember which.
        compositor.Dispose();
        Assert.Equal(before, device.LiveResourceCount);
    }

    /// <summary>
    ///     A node kind this assembly has never heard of is owned on exactly the same terms.
    /// </summary>
    /// <remarks>
    ///     <b>The assertion that says the rule covers the next node rather than the last two.</b>
    ///     <c>Vixen.Rendering.PostFx</c>, the water set and a game's own effect all reach the builder
    ///     through <see cref="ISceneRendererFactory" />, which is the one arm of the switch this
    ///     assembly cannot enumerate — and half of the disposable nodes in the engine arrive that way.
    ///     The claim is made where every arm meets, so there is nothing for a factory's author to
    ///     remember.
    /// </remarks>
    [Fact]
    public void A_node_a_factory_built_is_owned_like_any_other() {
        using var h = Harness();

        var node = new Counted();

        h.Builder.Factories.Add(new CountedFactory(node));

        var compositor = h.Builder.Build(new() { Game = new CountedAsset { Name = "Counted" } });

        Assert.Equal(1, compositor.OwnedCount);
        Assert.Equal(0, node.Disposals);

        compositor.Dispose();
        Assert.Equal(1, node.Disposals);
    }

    /// <summary>A node the host appended is the host's, and survives the reload that replaces the tree.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Why ownership is claimed at construction rather than by walking the built tree.</b>
    ///         <c>SceneRenderHost.Debug</c> is put into every tree the host builds and is meant to
    ///         outlive each of them, so a compositor that disposed everything it could reach through
    ///         <see cref="SceneRenderer.Nested" /> would hand the next frame a dead node — and the
    ///         symptom would be a diagnostic overlay that silently stopped drawing, which is the
    ///         hardest possible thing to notice.
    ///     </para>
    ///     <para>
    ///         Reachable and not owned is the whole distinction, so it is asserted both ways.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_node_the_host_appended_is_not_the_compositors_to_free() {
        using var h = Harness();

        var appended = new Counted();
        var compositor = h.Builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(Document));

        ((SceneRendererSequence)compositor.Game!).Children.Add(appended);

        compositor.Dispose();

        Assert.Contains(appended, compositor.Game!.Nested);
        Assert.Equal(0, appended.Disposals);
    }

    /// <summary>A build that throws half way frees the half it made.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>An editor opens documents that do not bind, and does it repeatedly.</b> The tree is
    ///         made before the compositor exists — it is that object's <c>Game</c> — so a document
    ///         that fails on its fourth node has already made three, and the caller's correct response
    ///         to the exception is to keep drawing the frame it already had. Nothing then holds a
    ///         reference to the partial tree at all, which makes it the one leak that cannot even be
    ///         found afterwards.
    ///     </para>
    ///     <para>
    ///         The document below binds a stage the <c>stages:</c> section does not declare, which is
    ///         a typo rather than a contrivance — and the node before it is a cached shadow node that
    ///         has already taken its texture.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_build_that_fails_releases_what_it_had_already_made() {
        using var h = Harness();

        var before = device.LiveResourceCount;

        var broken = YamlSerializer.Parse<GraphicsCompositorAsset>(
            Document.Replace("stage: LampCaster", "stage: NoSuchStage", StringComparison.Ordinal)
        );

        Assert.Throws<CompositorBindingException>(() => h.Builder.Build(broken));
        Assert.Equal(before, device.LiveResourceCount);
    }

    // --- The rig ------------------------------------------------------------

    sealed class Counted : SceneRenderer, IDisposable {
        public int Disposals { get; private set; }

        public void Dispose() => Disposals++;
    }

    sealed record CountedAsset : ISceneRendererAsset {
        public string Name { get; init; } = string.Empty;
        public bool Enabled { get; init; } = true;
    }

    sealed class CountedFactory(SceneRenderer node) : ISceneRendererFactory {
        public SceneRenderer? Create(ISceneRendererAsset declared, CompositorBuilder builder) =>
            declared is CountedAsset ? node : null;
    }

    sealed class Rig : IDisposable {
        public required RenderSystem System { get; init; }
        public required CompositorBuilder Builder { get; init; }
        public required RenderGraph Graph { get; init; }
        public required DescriptorAllocator Descriptors { get; init; }

        public void Dispose() {
            Graph.DisposePool();
            Descriptors.Dispose();
            System.Dispose();
        }
    }

    Rig Harness() {
        var system = new RenderSystem();

        var meshes = new MeshRenderFeature {
            Pipelines = new(device),
            Describer = new EffectPipelineDescriber(device)
        };

        meshes.Add(new MaterialRenderFeature { Effects = effects });
        system.AddFeature(meshes);

        var view = Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, -1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 1000f);
        var descriptors = new DescriptorAllocator(device);

        var builder = new CompositorBuilder(system) { Device = device, Descriptors = descriptors };

        builder.Views["Camera"] = new("camera") { Position = Vector3.Zero, Frustum = new(view * projection) };

        return new() {
            System = system,
            Builder = builder,
            Graph = new(device),
            Descriptors = descriptors
        };
    }

    /// <summary>Builds the tree and lends it the three textures a host would own.</summary>
    /// <remarks>
    ///     Made once per test rather than per round, so the imports themselves cannot be what the
    ///     count is measuring. The atlas extents are each node's own arithmetic — two 256 cascades
    ///     fold 2 × 1, and four 64-texel tiles a side is 256 — and both nodes refuse a mismatch by
    ///     name.
    /// </remarks>
    GraphicsCompositor Compose(Rig h, GraphicsCompositorAsset asset) {
        var compositor = h.Builder.Build(asset);

        compositor.FrameSize = new(512, 512);
        compositor.Imports["ShadowAtlas"] = imports.ShadowAtlas;
        compositor.Imports["PunctualShadowAtlas"] = imports.LampAtlas;
        compositor.Imports["SceneColour"] = imports.Colour;

        return compositor;
    }

    void Frame(Rig h, GraphicsCompositor compositor) {
        var list = device.BeginCommandList();

        h.Graph.Reset();
        compositor.Build(h.Graph, effects, device);
        h.Graph.Execute(list);
        list.Finish();
        device.GraphicsQueue.Submit([list]);
    }

    ImportedTexture Imported(PixelFormat format, TextureUsage usage, string name, int width, int height) {
        var description = new TextureDescription(format, width, height, usage | TextureUsage.Sampled, Name: name);
        var texture = device.CreateTexture(description);

        return new(texture, device.CreateTextureView(texture), description);
    }

    readonly (ImportedTexture ShadowAtlas, ImportedTexture LampAtlas, ImportedTexture Colour) imports;

    /// <summary>Makes the host's three textures once, before any document is built.</summary>
    public CompositorLifetimeTests() =>
        imports = (
            Imported(PixelFormat.Depth32Float, TextureUsage.DepthStencilTarget, "ShadowAtlas", 512, 256),
            Imported(PixelFormat.Depth32Float, TextureUsage.DepthStencilTarget, "PunctualShadowAtlas", 256, 256),
            Imported(PixelFormat.Rgba16Float, TextureUsage.ColourTarget, "SceneColour", 512, 512)
        );

    /// <inheritdoc />
    public void Dispose() {
        device.Dispose();
        GC.SuppressFinalize(this);
    }
}
