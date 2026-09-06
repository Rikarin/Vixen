// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Engine.Renderer;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Shaders;
using Vixen.Ui;
using Vixen.Ui.Renderer;
using Vixen.Ui.Rendering;
using Vixen.Ui.Text.Rasterizing;
using Xunit;

namespace Tests;

/// <summary>
///     A game's renderer can draw a user interface over its world.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every assertion here is about a caller, not about a class.</b>
///         <c>UiRenderFeature</c> compiled, was documented, was named in three other files' prose,
///         and nothing in the tree ever constructed one — so whether the composition it was written
///         for worked had never been observed. A test that built a feature by hand and drove it
///         would have been green on that tree and is therefore not the test to write; what these ask
///         is whether <em>the renderer a game gets</em> comes with one.
///     </para>
///     <para>
///         Nothing here needs a device beyond a null one. The render system's phases are CPU-side
///         and deterministic by design, and the half that was missing — an object in the store with
///         the right feature's index on it, surviving the cull — is entirely in them.
///     </para>
/// </remarks>
public sealed class InterfaceInAWorldTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });
    readonly EffectSystem effects = new();

    /// <summary>The renderer a game gets has the interface feature in its render system.</summary>
    /// <remarks>
    ///     The grep that would have caught the original defect was for callers of the type, so this
    ///     asserts a caller's result: that the system holds this feature, with an index of its own
    ///     and a back-reference — which is what <c>AddFeature</c> and only <c>AddFeature</c> gives
    ///     it, and what <c>Mount</c> refuses to work without.
    /// </remarks>
    [Fact]
    public void TheRendererAGameGetsCarriesTheInterfaceFeature() {
        using var renderer = new WorldRenderer(device, effects, vertexCapacity: 4096, indexCapacity: 8192);

        Assert.Contains(renderer.Ui, renderer.Host.System.Features);
        Assert.Same(renderer.Host.System, renderer.Ui.System);
        Assert.True(renderer.Ui.Index >= 0);
    }

    /// <summary>
    ///     A mounted interface reaches the stage's work list, and is drawn by the feature that
    ///     mounted it.
    /// </summary>
    /// <remarks>
    ///     The second half is the one worth stating. <c>RenderSystem.Record</c> hands a run of nodes
    ///     to <c>features[object.FeatureIndex]</c>, so an object mounted with the wrong index is a
    ///     surface some other feature is asked to draw — which is not a failure, it is a feature
    ///     skipping an object it does not recognise, and nothing says so.
    /// </remarks>
    [Fact]
    public void AMountedInterfaceIsCollectedForTheFeatureThatMountedIt() {
        using var renderer = new WorldRenderer(device, effects, vertexCapacity: 4096, indexCapacity: 8192);

        var system = renderer.Host.System;
        var stage = system.AddStage(new("Ui", RenderSortMode.ByGroup));
        var id = renderer.Ui.Mount(stage.Mask);

        var camera = Camera(stage.Mask);
        system.SetViews([camera]);
        system.Draw();

        var nodes = system.Nodes(camera, stage);
        Assert.Equal([id], nodes.Select(node => node.Object));
        Assert.Equal(renderer.Ui.Index, system.Objects[id].FeatureIndex);
    }

    /// <summary>
    ///     An interface is drawn wherever the camera is pointing, and a thing with a place in the
    ///     world is not.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The control object is the whole test.</b> "The interface survived the cull" is
    ///         also what a cull that rejects nothing prints, and a renderer whose visibility group
    ///         had quietly stopped testing anything would pass the assertion above. So a second
    ///         object goes into the same stage with the same feature and a metre-wide sphere behind
    ///         the camera, and the frame has to keep one and drop the other.
    ///     </para>
    ///     <para>
    ///         What it pins is <c>Mount</c>'s bounds. An interface is in screen space and has no
    ///         position in the world, so anything finite there is a HUD that appears and disappears
    ///         as the player turns around — which is a picture rather than a failure, and reads as a
    ///         fault in the interface rather than in the renderer.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AnInterfaceIsNotCulledByWhereTheCameraLooks() {
        using var renderer = new WorldRenderer(device, effects, vertexCapacity: 4096, indexCapacity: 8192);

        var system = renderer.Host.System;
        var stage = system.AddStage(new("Ui", RenderSortMode.ByGroup));

        var mounted = renderer.Ui.Mount(stage.Mask);

        // Behind the camera, which looks down +z from the origin.
        var behind = system.Objects.Add(
            new() {
                Bounds = new(new Vector3(0f, 0f, -500f), 1f),
                Stages = stage.Mask,
                FeatureIndex = renderer.Ui.Index,
                IsAlive = true
            }
        );

        var camera = Camera(stage.Mask);
        system.SetViews([camera]);
        system.Draw();

        var drawn = system.Nodes(camera, stage).Select(node => node.Object).ToList();

        Assert.Contains(mounted, drawn);
        Assert.DoesNotContain(behind, drawn);
    }

    /// <summary>Surfaces sort against each other by the order they were mounted with.</summary>
    /// <remarks>
    ///     ⚠ Asserted on a surface that has been mounted and not yet <c>Set</c>, which is the state
    ///     the first frame is in: <c>SortGroupOf</c> falls back to the render object's own group
    ///     there, so the group <c>Mount</c> writes and the order <c>Set</c> carries have to be the
    ///     same number. A tooltip under its modal for exactly one frame is the shape of them
    ///     disagreeing.
    /// </remarks>
    [Fact]
    public void SurfacesAreOrderedAgainstEachOtherBeforeTheFirstSet() {
        using var renderer = new WorldRenderer(device, effects, vertexCapacity: 4096, indexCapacity: 8192);

        var system = renderer.Host.System;
        var stage = system.AddStage(new("Ui", RenderSortMode.ByGroup));

        // Mounted back to front, so the answer cannot be the order they were added in.
        var tooltip = renderer.Ui.Mount(stage.Mask, order: 2);
        var document = renderer.Ui.Mount(stage.Mask, order: 0);
        var modal = renderer.Ui.Mount(stage.Mask, order: 1);

        var camera = Camera(stage.Mask);
        system.SetViews([camera]);
        system.Draw();

        Assert.Equal(
            [document, modal, tooltip],
            system.Nodes(camera, stage).Select(node => node.Object)
        );
    }

    /// <summary>Unmounting takes the object out of the frame as well as the surface.</summary>
    /// <remarks>
    ///     The failure this is about is quiet: an object left alive is one every view still culls
    ///     and every stage still collects, handed to a feature that will not find a surface for it
    ///     and will draw nothing — one more of them per window closed, for ever.
    /// </remarks>
    [Fact]
    public void UnmountingLeavesNothingInTheFrame() {
        using var renderer = new WorldRenderer(device, effects, vertexCapacity: 4096, indexCapacity: 8192);

        var system = renderer.Host.System;
        var stage = system.AddStage(new("Ui", RenderSortMode.ByGroup));
        var id = renderer.Ui.Mount(stage.Mask);

        renderer.Ui.Unmount(id);

        var camera = Camera(stage.Mask);
        system.SetViews([camera]);
        system.Draw();

        Assert.Empty(system.Nodes(camera, stage));
        Assert.False(system.Objects[id].IsAlive);
    }

    /// <summary>Mounting before the feature is in a system says so rather than writing a bad index.</summary>
    /// <remarks>
    ///     <c>RootRenderFeature.Index</c> is -1 until <c>AddFeature</c> assigns one, and an object
    ///     carrying -1 is one <c>Record</c> silently drops — so the honest answer to "mount before
    ///     registering" is a refusal at the call rather than a frame that draws everything except
    ///     the interface.
    /// </remarks>
    [Fact]
    public void MountingBeforeRegisteringIsRefused() {
        var loose = new UiRenderFeature();

        Assert.Throws<InvalidOperationException>(() => loose.Mount(RenderStageMask.All));
    }

    /// <summary>
    ///     The frame a mounted interface was given reaches the GPU, geometry and glyph atlas
    ///     together.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The half that had no reachable caller after the registration was fixed.</b>
    ///         <c>UiRenderFeature.Draw</c> runs inside a render pass, so it can only <c>Record</c>;
    ///         <c>UiRenderer.Upload</c> is what writes the vertices and copies the atlas, and it
    ///         cannot be called from there — a texture copy is the one thing a Vulkan command list
    ///         may not do inside a pass. Nothing called it, and the giveaway was
    ///         <c>UiInterface.Atlas</c>: every surface carried the atlas and no line read the field.
    ///     </para>
    ///     <para>
    ///         Both counters are the renderer's own work, not a wall clock. <c>Region</c> advances
    ///         only in <c>UploadGeometry</c>, and only for a frame with indices in it;
    ///         <c>AtlasUploads</c> counts the copies. A feature that recorded without uploading
    ///         leaves both where they started and draws from a buffer nothing has ever written,
    ///         which is a HUD of undefined memory rather than an error.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AMountedInterfacesFrameReachesTheGpu() {
        using var renderer = new WorldRenderer(device, effects, vertexCapacity: 4096, indexCapacity: 8192);
        using var ui = UiRendererFor(device);

        var system = renderer.Host.System;
        var stage = system.AddStage(new("Ui", RenderSortMode.ByGroup));

        renderer.Ui.Renderer = ui;

        var id = renderer.Ui.Mount(stage.Mask);
        var atlas = new GlyphAtlas(64, 64);

        renderer.Ui.Set(id, new(Geometry(atlas), atlas, new Int2(400, 300), 0));

        var region = ui.Region;

        Assert.Equal(0, ui.AtlasUploads);

        using var commands = device.BeginCommandList(QueueKind.Graphics, "ui");

        renderer.Ui.Upload(commands);

        Assert.Equal(1, ui.AtlasUploads);
        Assert.NotEqual(region, ui.Region);
    }

    /// <summary>A feature with nothing mounted uploads nothing, and one with no renderer says nothing.</summary>
    /// <remarks>
    ///     The pair the assertion above needs to mean anything. <c>AtlasUploads</c> reaching one is
    ///     only evidence that <em>this</em> surface was uploaded if a feature holding no surface
    ///     leaves it at zero — otherwise an upload of some default would read the same. And the
    ///     null-renderer case is the arrangement the constructor leaves behind: <c>WorldRenderer</c>
    ///     registers the feature whether or not the application has an interface, so a game with no
    ///     HUD calls this every frame and must not be told off for it.
    /// </remarks>
    [Fact]
    public void NothingMountedUploadsNothing() {
        using var renderer = new WorldRenderer(device, effects, vertexCapacity: 4096, indexCapacity: 8192);
        using var ui = UiRendererFor(device);
        using var commands = device.BeginCommandList(QueueKind.Graphics, "ui");

        renderer.Ui.Upload(commands);

        Assert.Equal(0, ui.AtlasUploads);

        renderer.Ui.Renderer = ui;
        renderer.Ui.Upload(commands);

        Assert.Equal(0, ui.AtlasUploads);
    }

    /// <summary>
    ///     Two mounted interfaces are uploaded through two renderers, each into its own ring region.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>"Both drew" is what the broken arrangement prints too, so the assertion is about
    ///         each renderer having consumed exactly one region.</b> A <c>UiRenderer</c> advances its
    ///         region inside <c>Upload</c> and <c>Record</c> reads whatever the <em>last</em> upload
    ///         wrote, so two interfaces through one renderer consume two regions in a frame and both
    ///         come out drawn from the second one's geometry — the modal and the document behind it
    ///         as two copies of the modal. Nothing reports it: not an error, not a validation
    ///         warning, a picture.
    ///     </para>
    ///     <para>
    ///         This is also what makes <c>Mount</c>'s <c>order</c> parameter mean anything. The
    ///         ordering exists so that a modal can be drawn over a document and a tooltip over the
    ///         modal, which is more than one mounted interface by definition.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TwoMountedInterfacesUploadThroughTheirOwnRenderers() {
        using var renderer = new WorldRenderer(device, effects, vertexCapacity: 4096, indexCapacity: 8192);
        using var behind = UiRendererFor(device);
        using var front = UiRendererFor(device);

        var system = renderer.Host.System;
        var stage = system.AddStage(new("Ui", RenderSortMode.ByGroup));

        var document = renderer.Ui.Mount(stage.Mask, order: 0, renderer: behind);
        var modal = renderer.Ui.Mount(stage.Mask, order: 1, renderer: front);

        Assert.Same(behind, renderer.Ui.RendererOf(document));
        Assert.Same(front, renderer.Ui.RendererOf(modal));

        var atlas = new GlyphAtlas(64, 64);

        renderer.Ui.Set(document, new(Geometry(atlas), atlas, new Int2(400, 300), 0));
        renderer.Ui.Set(modal, new(Geometry(atlas), atlas, new Int2(400, 300), 1));

        var (first, second) = (behind.Region, front.Region);

        using var commands = device.BeginCommandList(QueueKind.Graphics, "ui");

        renderer.Ui.Upload(commands);

        // One region each. Two on either of them is the bug: the second write would land in the
        // region the first surface is being drawn out of, and one region further round is a region
        // an unfinished frame may still be reading.
        Assert.Equal((first + 1) % behind.Regions, behind.Region);
        Assert.Equal((second + 1) % front.Regions, front.Region);

        Assert.Equal(1, behind.AtlasUploads);
        Assert.Equal(1, front.AtlasUploads);
    }

    /// <summary>
    ///     Two interfaces through one renderer is refused, on the frame rather than in the picture.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A refusal and not a best effort, because the alternative is not a missing HUD but a
    ///     convincing wrong one.</b> The failure has no error and no validation warning in it — the
    ///     descriptor sets are valid, the draws are the right draws, and the vertices they read are
    ///     the other surface's. A frame lost to an exception that names the cause is worth more than
    ///     a frame spent drawing the modal twice.
    /// </remarks>
    [Fact]
    public void TwoInterfacesThroughOneRendererAreRefusedRatherThanDrawnTwice() {
        using var renderer = new WorldRenderer(device, effects, vertexCapacity: 4096, indexCapacity: 8192);
        using var shared = UiRendererFor(device);

        var system = renderer.Host.System;
        var stage = system.AddStage(new("Ui", RenderSortMode.ByGroup));

        renderer.Ui.Renderer = shared;

        // Neither names a renderer, so both fall back to the one the feature holds. This is the
        // arrangement a host reaches by mounting a second interface and changing nothing else.
        var document = renderer.Ui.Mount(stage.Mask, order: 0);
        var modal = renderer.Ui.Mount(stage.Mask, order: 1);

        var atlas = new GlyphAtlas(64, 64);

        renderer.Ui.Set(document, new(Geometry(atlas), atlas, new Int2(400, 300), 0));
        renderer.Ui.Set(modal, new(Geometry(atlas), atlas, new Int2(400, 300), 1));

        using var commands = device.BeginCommandList(QueueKind.Graphics, "ui");

        var refused = Assert.Throws<InvalidOperationException>(() => renderer.Ui.Upload(commands));
        Assert.Contains("UiRenderer", refused.Message, StringComparison.Ordinal);

        // ⚠ And the same arrangement is refused when it is *recorded* rather than uploaded. A host
        // that never uploaded is already drawing from a buffer nothing wrote, and that path must not
        // be the one on which a shared renderer is quietly tolerated.
        var camera = Camera(stage.Mask);
        system.SetViews([camera]);
        system.Draw();

        var context = new RenderDrawContext(commands, effects);

        Assert.Throws<InvalidOperationException>(() => system.Record(camera, stage, context));
    }

    /// <summary>An interface that names no renderer keeps using the feature's, which is one
    /// interface's arrangement and stays free of ceremony.</summary>
    /// <remarks>
    ///     The half that makes the refusal above a rule about sharing rather than a rule about
    ///     counting: mounting one and setting <c>Renderer</c> is what every host does today and must
    ///     go on working untouched, and a third interface with a renderer of its own is fine.
    /// </remarks>
    [Fact]
    public void AnInterfaceThatNamesNoRendererUsesTheFeaturesOwn() {
        using var renderer = new WorldRenderer(device, effects, vertexCapacity: 4096, indexCapacity: 8192);
        using var shared = UiRendererFor(device);
        using var own = UiRendererFor(device);

        var system = renderer.Host.System;
        var stage = system.AddStage(new("Ui", RenderSortMode.ByGroup));

        renderer.Ui.Renderer = shared;

        var document = renderer.Ui.Mount(stage.Mask, order: 0);
        var modal = renderer.Ui.Mount(stage.Mask, order: 1, renderer: own);

        Assert.Same(shared, renderer.Ui.RendererOf(document));
        Assert.Same(own, renderer.Ui.RendererOf(modal));

        var atlas = new GlyphAtlas(64, 64);

        renderer.Ui.Set(document, new(Geometry(atlas), atlas, new Int2(400, 300), 0));
        renderer.Ui.Set(modal, new(Geometry(atlas), atlas, new Int2(400, 300), 1));

        using var commands = device.BeginCommandList(QueueKind.Graphics, "ui");

        renderer.Ui.Upload(commands);

        Assert.Equal(1, shared.AtlasUploads);
        Assert.Equal(1, own.AtlasUploads);

        // ⚠ And unmounting gives the renderer back rather than leaving it bound to a dense index the
        // store will hand out again.
        renderer.Ui.Unmount(modal);
        Assert.Same(shared, renderer.Ui.RendererOf(modal));
    }


    /// <summary>A mounted interface's composited groups are rendered into surfaces of their own.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The third outside-the-pass half, and the one whose absence is a picture rather
    ///         than an absence of one.</b> <c>UiRenderer.Compose</c> documents itself as optional and
    ///         is, read alone: <c>Record</c> skips a group's own draws only where a surface exists,
    ///         so a host that never composes still draws every one of them. What it draws is the
    ///         defect. <c>UiGeometryBuilder</c> emits a group's contents at alpha <em>one</em>
    ///         precisely so the group's surface can carry the fade, so the flat walk over a
    ///         half-transparent panel is not an approximation of it — it is an opaque panel.
    ///     </para>
    ///     <para>
    ///         <c>Composited</c> is the only observer the arrangement has, which is why the frame is
    ///         asserted to have a group in it before anything is asked of it: a geometry that opened
    ///         none makes both halves below read zero and one, and a test that skipped that line
    ///         would pass against a feature whose <c>Compose</c> was empty.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AMountedInterfacesGroupsAreComposedIntoSurfacesOfTheirOwn() {
        using var renderer = new WorldRenderer(device, effects, vertexCapacity: 4096, indexCapacity: 8192);
        using var ui = UiRendererFor(device);

        var system = renderer.Host.System;
        var stage = system.AddStage(new("Ui", RenderSortMode.ByGroup));

        renderer.Ui.Renderer = ui;

        var id = renderer.Ui.Mount(stage.Mask);
        var atlas = new GlyphAtlas(64, 64);
        var geometry = Grouped(atlas);

        // The instrument, before the measurement.
        Assert.Single(geometry.Layers);

        renderer.Ui.Set(id, new(geometry, atlas, new Int2(400, 300), 0));

        using var commands = device.BeginCommandList(QueueKind.Graphics, "ui");

        renderer.Ui.Upload(commands);

        // The control the assertion below needs: uploading a frame with a group in it is not
        // composing it, and a counter that was already non-zero here would prove nothing after.
        Assert.Equal(0, ui.Composited);

        renderer.Ui.Compose(commands);

        Assert.Equal(1, ui.Composited);
    }

    /// <summary>A feature with nothing mounted composes nothing, and one with no renderer says nothing.</summary>
    /// <remarks>
    ///     <c>NothingMountedUploadsNothing</c>'s pair, and for its reason: the constructor registers
    ///     the feature whether or not the application has an interface, so a game with no HUD calls
    ///     this every frame and must not be told off for it.
    /// </remarks>
    [Fact]
    public void NothingMountedComposesNothing() {
        using var renderer = new WorldRenderer(device, effects, vertexCapacity: 4096, indexCapacity: 8192);
        using var ui = UiRendererFor(device);
        using var commands = device.BeginCommandList(QueueKind.Graphics, "ui");

        renderer.Ui.Compose(commands);

        Assert.Equal(0, ui.Composited);

        renderer.Ui.Renderer = ui;
        renderer.Ui.Compose(commands);

        Assert.Equal(0, ui.Composited);
    }

    /// <remarks>
    ///     ⚠ <c>Image</c> is set, and without it <c>Compose</c> returns having done nothing — there
    ///     is no shader to composite a surface back with, so rendering the surfaces would cost a pass
    ///     each and put nothing on screen. It is an <c>init</c> property rather than a fifth
    ///     positional argument, which is why the four-argument construction is still the shape here.
    /// </remarks>
    static UiRenderer UiRendererFor(NullDevice device) =>
        new(
            device,
            new UiShaders(
                device.CreateShader(ShaderStage.Vertex, [1, 2, 3, 4], "ui vertex"),
                device.CreateShader(ShaderStage.Fragment, [1, 2, 3, 4], "ui box"),
                device.CreateShader(ShaderStage.Fragment, [1, 2, 3, 4], "ui text"),
                device.CreateShader(ShaderStage.Fragment, [1, 2, 3, 4], "ui solid")
            ) {
                // ⚠ Without an image shader `UiRenderer.Compose` returns before it composes anything
                // — no pipeline means no way to composite a surface back, so the groups are left to
                // draw in place — and `Composited` reads zero for a reason that is not the one under
                // test.
                Image = device.CreateShader(ShaderStage.Fragment, [1, 2, 3, 4], "ui image")
            },
            new RenderOutput([PixelFormat.Bgra8UNorm])
        );

    static UiGeometry Geometry(GlyphAtlas atlas) {
        var list = new DrawList();

        list.BeginFrame();

        // ⚠ Qualified: `Vixen.Rendering` has a `DrawCommand` of its own, and the two mean different
        // things — one is an element's paint, the other an indirect draw's arguments.
        list.Add(new Vixen.Ui.DrawCommand(DrawCommandKind.Rectangle, 8f, 8f, 120f, 40f, Color4.White, 0f, 0f));
        list.EndFrame();

        return new UiGeometryBuilder().Build(list, new GlyphFieldCache(atlas), new Rectangle(0, 0, 400, 300));
    }


    /// <summary>The same frame with its rectangle inside a half-transparent group.</summary>
    /// <remarks>
    ///     The alpha is on the <c>LayerPush</c>'s colour, which is where <c>UiGeometryBuilder</c>
    ///     reads a group's opacity from — and it is what makes the group a layer worth compositing
    ///     rather than one the builder collapses. A group whose ink is clipped to nothing keeps no
    ///     layer at all, so the rectangle is inside the push's box.
    /// </remarks>
    static UiGeometry Grouped(GlyphAtlas atlas) {
        var list = new DrawList();

        list.BeginFrame();

        list.Add(
            new Vixen.Ui.DrawCommand(
                DrawCommandKind.LayerPush,
                8f,
                8f,
                160f,
                80f,
                new Color4(1f, 1f, 1f, 0.5f),
                0f,
                0f
            )
        );

        list.Add(new Vixen.Ui.DrawCommand(DrawCommandKind.Rectangle, 16f, 16f, 120f, 40f, Color4.White, 0f, 0f));
        list.Add(new Vixen.Ui.DrawCommand(DrawCommandKind.LayerPop, 0f, 0f, 0f, 0f, Color4.White, 0f, 0f));
        list.EndFrame();

        return new UiGeometryBuilder().Build(list, new GlyphFieldCache(atlas), new Rectangle(0, 0, 400, 300));
    }
    static RenderView Camera(RenderStageMask stages) {
        var view = Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, 1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 1000f);

        return new("camera") { Stages = stages, Position = Vector3.Zero, Frustum = new(view * projection) };
    }

    public void Dispose() => device.Dispose();
}
