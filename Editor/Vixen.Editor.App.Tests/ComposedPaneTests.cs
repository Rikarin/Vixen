// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.SceneView;
using Vixen.Editor.Testing;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Ui.Renderer;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Every pane of a split layout drawing its own camera through the one compositor.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Nothing here asserts that a method was called.</b> The arrangement this replaced
///         composed <em>one</em> pane and left the other three to the tool renderer, and every
///         structural claim a four-pane frame could make was already true of it: four presenters
///         exist, four targets are registered, four <c>Declare</c>s return true. So what is asserted
///         is that the render system holds a <em>separate work list per view index</em>, that the four
///         views hold four different matrices when the graph executes, and that the texture each pane
///         hands the interface is the texture that pane lent the frame.
///     </para>
///     <para>
///         ⚠ <b>The loop under test is <c>EditorHost.Record</c>'s and it is reproduced here rather
///         than called.</b> <c>Vixen.Editor.Host</c> is an executable with no test project — it holds
///         the swapchain, the window and the platform — so the order <see cref="Compose" /> runs in is
///         a copy of that method's, kept honest by being the only order that works: resize every pane,
///         run the frame's prologue <em>once</em>, upload and prepare every pane, then one
///         <c>Compose</c> for all of them. Anything that regresses in the host and not here is
///         reported at the end of this file's suite rather than caught by it.
///     </para>
/// </remarks>
public sealed class ComposedPaneTests : IDisposable {
    readonly NullDevice device;
    readonly List<IDisposable> owned = [];

    public ComposedPaneTests() => device = new(new() { Record = true });

    public void Dispose() {
        for (var index = owned.Count - 1; index >= 0; index--) {
            owned[index].Dispose();
        }

        device.Dispose();
    }

    // ------------------------------------------------------------------ the harness

    /// <summary>An editor with a device, four panes, and a camera per pane that is nobody else's.</summary>
    /// <remarks>
    ///     ⚠ <b>The cameras are made to disagree on purpose.</b> Four panes that open at the same
    ///     orbit are four panes whose views hold the same matrix, which is exactly the state a
    ///     single-view frame is indistinguishable from — so every claim about "its own camera" would
    ///     be vacuously true.
    /// </remarks>
    EditorSession Quad() {
        var session = EditorSession.Start();

        owned.Add(session);

        session.Application.GraphicsDevice = device;
        session.Frame();
        session.Run("scene.panes-quad");
        session.Settle();

        var panes = session.Application.Viewports;

        Assert.Equal(EditorWorldRenderer.MaxPanes, panes.Count);

        for (var index = 0; index < panes.Count; index++) {
            panes[index].Camera.Yaw = 0.25f * (index + 1);
            panes[index].Camera.Pitch = -0.1f * (index + 1);
            panes[index].Camera.Distance = 6f + index;
        }

        return session;
    }

    /// <summary>One presenter per pane, on the ids the host allocates.</summary>
    /// <remarks>
    ///     ⚠ <b>The ids are <c>EditorHost.FrameImage</c>'s and the spacing is the point.</b> An image
    ///     id shared between two panes is two registrations of one number, and the interface shows
    ///     whichever target was registered last — a quad layout in which all four panes show pane
    ///     three. The tool presenters take the odd numbers from one upwards, so these take the even
    ///     ones.
    /// </remarks>
    List<FramePresenter> Presenters(EditorSession session) {
        var world = session.Application.Frame!;
        var made = new List<FramePresenter>();

        for (var index = 0; index < session.Application.Viewports.Count; index++) {
            var presenter = new FramePresenter(
                device,
                world,
                new LineShaders(Stage(ShaderStage.Vertex, "line.vs"), Stage(ShaderStage.Fragment, "line.fs")),
                new MeshShaders(Stage(ShaderStage.Vertex, "mesh.vs"), Stage(ShaderStage.Fragment, "mesh.fs")),
                FramePresenter.ColourFormat,
                ((ulong) index * 2) + 2,
                index
            );

            owned.Add(presenter);
            made.Add(presenter);
        }

        return made;
    }

    /// <summary>What one frame of <c>EditorHost.Record</c> did, for a test to assert against.</summary>
    /// <param name="Sampled">The textures the interface pass would declare that it reads.</param>
    /// <param name="Trees">How many panes contributed a subtree to the build.</param>
    /// <param name="Frame">The build itself, or null when no pane contributed.</param>
    readonly record struct Recorded(List<GraphTexture> Sampled, int Trees, CompositorFrame? Frame);

    /// <summary>Records one frame the way the host records one, and executes the graph.</summary>
    /// <remarks>
    ///     ⚠ <b>Three frames before anything is asserted, in every caller.</b> The set-1 layout is
    ///     adopted off the first shader to resolve and nothing has resolved on the frame before the
    ///     first build, so a suite that ran one frame would assert against a renderer that has not
    ///     bound a per-view set yet — and "no pane drew" would be true for a reason that is not the
    ///     one under test.
    /// </remarks>
    Recorded Compose(EditorSession session, RenderGraph graph, IReadOnlyList<FramePresenter> presenters) {
        var world = session.Application.Frame!;
        var panes = session.Application.Viewports;
        var renderer = new UiRenderer(device, Shaders(), new RenderOutput([PixelFormat.Bgra8UNorm]));

        owned.Add(renderer);

        using var commands = device.BeginCommandList(QueueKind.Graphics, "frame");

        var composing = new List<(FramePresenter Presenter, SceneViewport Viewport)>();

        for (var index = 0; index < panes.Count && index < presenters.Count; index++) {
            if (presenters[index].Resize(panes[index], renderer)) {
                composing.Add((presenters[index], panes[index]));
            }
        }

        // ⚠ Once, before any pane. `MaterialDescriptors.BeginFrame` recycles every set handed out
        // since the last call, so a second call between two panes hands the second pane sets the
        // first pane's passes are still going to bind when the graph executes.
        world.Begin(commands);

        var trees = new List<SceneRenderer>();
        var reference = Int2.Zero;

        foreach (var (presenter, viewport) in composing) {
            presenter.Upload(commands, session.Application.Scene, viewport);

            if (presenter.Prepare(viewport, out var tree)) {
                trees.Add(tree);
            }

            reference = new(Math.Max(reference.X, presenter.Width), Math.Max(reference.Y, presenter.Height));
        }

        var sampled = new List<GraphTexture>();
        CompositorFrame? built = null;

        if (trees.Count > 0) {
            built = world.Compose(graph, trees, reference, device.WaitIdle);

            foreach (var (presenter, _) in composing) {
                if (presenter.Take(built, out var target)) {
                    sampled.Add(target);
                }
            }
        }

        graph.Execute(commands);
        commands.Finish();

        return new(sampled, trees.Count, built);
    }

    /// <summary>Three frames of it, which is what the first assertion in every test needs.</summary>
    Recorded Settled(EditorSession session, IReadOnlyList<FramePresenter> presenters) {
        var recorded = default(Recorded);

        for (var pass = 0; pass < 3; pass++) {
            var graph = new RenderGraph(device);

            recorded = Compose(session, graph, presenters);
            graph.Reset();
        }

        return recorded;
    }

    // ------------------------------------------------------------------ the views

    /// <summary>Four panes are four views, and the frame's collect gives each of them an index.</summary>
    /// <remarks>
    ///     ⚠ <b>Four <em>distinct</em> indices out of one collect, which is the whole reason the build
    ///     may not be split.</b> <c>RenderSystem.SetViews</c> assigns <c>RenderView.Index</c> and is
    ///     called once per <c>GraphicsCompositor.Collect</c>, clearing the list first — so a build per
    ///     pane would give each pane's view index 0 in turn, and every pass records at <em>execute</em>
    ///     time against whichever view held its index last. Four panes, four cameras, one visible set,
    ///     and every counter in the frame healthy.
    /// </remarks>
    [Fact]
    public void Every_pane_is_its_own_view_and_the_one_collect_gives_each_an_index() {
        var session = Quad();
        var world = session.Application.Frame!;

        Settled(session, Presenters(session));

        var views = world.Compositor.Views;

        Assert.Equal(EditorWorldRenderer.MaxPanes, views.Count);

        for (var pane = 0; pane < EditorWorldRenderer.MaxPanes; pane++) {
            Assert.Contains(world.ViewOf(pane), views);
        }

        // ⚠ Distinct indices, not merely four entries. `SetViews` numbers them as it walks, so a
        // frame that collected one view four times would have four entries of one index.
        Assert.Equal(
            EditorWorldRenderer.MaxPanes,
            views.Select(view => view.Index).Distinct().Count()
        );
    }

    /// <summary>Each pane's view holds that pane's camera, and no two of them agree.</summary>
    /// <remarks>
    ///     ⚠ <b>The failure this is about draws a picture.</b> One <c>RenderView</c> aimed by whichever
    ///     pane uploaded last is four panes showing one camera — every target written, every count
    ///     healthy, and a split that looks like a bug in the splitter rather than in the renderer. The
    ///     matrices are read after the graph has executed, because that is when a pass binds set 1.
    /// </remarks>
    [Fact]
    public void Each_panes_view_holds_that_panes_camera_and_no_two_agree() {
        var session = Quad();
        var world = session.Application.Frame!;
        var panes = session.Application.Viewports;

        Settled(session, Presenters(session));

        var matrices = new List<Matrix4x4>();

        for (var pane = 0; pane < EditorWorldRenderer.MaxPanes; pane++) {
            var view = world.ViewOf(pane);
            var aspect = (float) panes[pane].Control.RenderWidth / panes[pane].Control.RenderHeight;

            Assert.Equal(panes[pane].Camera.Position, view.Position);

            // The pane's own aspect, which in a quad layout is not the frame's.
            AssertClose(panes[pane].Camera.ViewProjection(aspect), view.ViewProjection, pane);

            matrices.Add(view.ViewProjection);
        }

        Assert.Equal(matrices.Count, matrices.Distinct().Count());
    }

    /// <summary>Every view has work of its own in the stage its pane's mode draws.</summary>
    /// <remarks>
    ///     ⚠ <b><c>RenderSystem.Nodes</c> is keyed by <c>(view.Index, stage.Index)</c>, and this is the
    ///     one assertion a frame that draws nothing cannot pass.</b> A pane that composed, resized,
    ///     uploaded and declared without contributing a view has an empty work list — and reports its
    ///     objects, its lights, zero waiting and zero dropped while it does.
    /// </remarks>
    [Fact]
    public void Every_panes_view_has_work_of_its_own_in_the_stage_it_draws() {
        var session = Quad();
        var world = session.Application.Frame!;

        Settled(session, Presenters(session));

        for (var pane = 0; pane < EditorWorldRenderer.MaxPanes; pane++) {
            var view = world.ViewOf(pane);
            var work = world.Renderer.Host.System.Nodes(view, world.Opaque);

            Assert.True(
                work.Count > 0,
                $"pane {pane}'s view has no work in the shaded stage, so nothing it declared draws"
            );

            Assert.Equal(world.ObjectCount, work.Count);
        }
    }

    /// <summary>A pane switched to wireframe collects the wireframe stage and its neighbours do not.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A stage belongs to the mode and a view belongs to the pane, and this is both halves
    ///         at once.</b> <c>PipelineKey</c> is <c>(Effect, Stage.Index, VertexLayout, Output)</c> and
    ///         <c>PipelineCache</c> never evicts, so a stage's state is baked in on the first cache miss
    ///         — which is why two panes in wireframe share one stage index and are still two views.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the shaded panes must <em>not</em> have collected it.</b> A frame in which every
    ///         view carries every stage is a frame where the mode switch is a no-op that costs four
    ///         extra sorts — and it would pass a test that only asked whether the wireframe pane has
    ///         wireframe work.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_pane_in_wireframe_collects_the_wireframe_stage_and_the_shaded_panes_do_not() {
        var session = Quad();
        var world = session.Application.Frame!;
        var panes = session.Application.Viewports;

        Assert.True(
            world.Renderer.Host.Builder.Stages.TryGetValue("Wireframe", out var wires),
            "the document declared no wireframe stage, so there are no two modes to tell apart"
        );

        panes[1].Modes.Current = ViewMode.Wireframe;
        panes[2].Modes.Current = ViewMode.Wireframe;

        Settled(session, Presenters(session));

        for (var pane = 0; pane < EditorWorldRenderer.MaxPanes; pane++) {
            var view = world.ViewOf(pane);
            var wireframe = pane is 1 or 2;

            Assert.Equal(wireframe, view.Stages.Contains(wires!.Index));
            Assert.Equal(!wireframe, view.Stages.Contains(world.Opaque.Index));

            var work = world.Renderer.Host.System.Nodes(view, wireframe ? wires : world.Opaque);

            Assert.True(work.Count > 0, $"pane {pane} collected its stage and has no work in it");
        }
    }

    // ------------------------------------------------------------------ the targets

    /// <summary>Every pane hands the interface the texture that pane lent the frame.</summary>
    /// <remarks>
    ///     ⚠ <b>Four names, four imports and four image ids, and each of the three is a way to make a
    ///     quad layout show one picture four times.</b> A shared target name is three panes drawing
    ///     into one texture; a shared image id is two registrations of one number and the interface
    ///     showing whichever was registered last.
    /// </remarks>
    [Fact]
    public void Every_pane_hands_the_interface_its_own_target() {
        var session = Quad();
        var world = session.Application.Frame!;
        var presenters = Presenters(session);

        var recorded = Settled(session, presenters);

        Assert.Equal(EditorWorldRenderer.MaxPanes, recorded.Trees);
        Assert.Equal(EditorWorldRenderer.MaxPanes, recorded.Sampled.Count);
        Assert.Equal(recorded.Sampled.Count, recorded.Sampled.Distinct().Count());

        // The image ids the interface samples through.
        Assert.Equal(
            presenters.Count,
            presenters.Select(presenter => presenter.Image).Distinct().Count()
        );

        for (var pane = 0; pane < presenters.Count; pane++) {
            Assert.Equal(presenters[pane].Image, session.Application.Viewports[pane].Control.RenderTarget);
        }

        // ⚠ And the frame's imports are four different textures rather than one written four times.
        var imported = new List<TextureHandle>();

        for (var pane = 0; pane < EditorWorldRenderer.MaxPanes; pane++) {
            Assert.True(
                world.Compositor.Imports.TryGetValue(FramePresenter.Colour(pane), out var colour),
                $"pane {pane} lent the frame no colour"
            );

            Assert.True(
                world.Compositor.Imports.TryGetValue(FramePresenter.Depth(pane), out var depth),
                $"pane {pane} lent the frame no depth"
            );

            Assert.True(depth.View.IsValid);
            imported.Add(colour.Texture);
        }

        Assert.Equal(imported.Count, imported.Distinct().Count());
    }

    /// <summary>A pane's linear target is sized to that pane, not to the frame.</summary>
    /// <remarks>
    ///     ⚠ <b>A framebuffer whose attachments disagree about their extent is one the driver refuses,
    ///     and this is the arrangement that produces one.</b> A resource declared with no size is
    ///     <c>Scale</c> of <c>FrameSize</c> — and a compositor has one of those where four panes have
    ///     four extents — so the linear target between the shading pass and the grade has to be given
    ///     the pane's own numbers. Left to the reference size, three of the four panes would attach a
    ///     colour of one size beside a depth of another.
    /// </remarks>
    [Fact]
    public void Each_panes_transient_target_is_sized_to_that_pane_rather_than_to_the_frame() {
        var session = Quad();
        var world = session.Application.Frame!;
        var presenters = Presenters(session);

        Settled(session, presenters);

        for (var pane = 0; pane < presenters.Count; pane++) {
            var name = pane == 0 ? "SceneHdr" : $"SceneHdr{pane}";
            var declared = world.Compositor.Resources.FirstOrDefault(resource => resource.Name == name);

            Assert.NotNull(declared);

            Assert.Equal(presenters[pane].Width, declared!.Width);
            Assert.Equal(presenters[pane].Height, declared.Height);

            // And the pane's own colour, which is what the grade writes into and the interface reads.
            Assert.True(
                world.Compositor.Imports.TryGetValue(FramePresenter.Colour(pane), out var colour),
                $"pane {pane} lent no colour"
            );

            Assert.Equal(presenters[pane].Width, colour.Description.Width);
            Assert.Equal(presenters[pane].Height, colour.Description.Height);
        }
    }

    // ------------------------------------------------------------------ the draws

    /// <summary>Four panes record four panes' worth of draws, and every set they bind is whole.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>IsComplete</c> is the assertion a black pane fails.</b> <c>EffectSetWriter</c>
    ///         writes a descriptor set whole or not at all, so a set 0 short one binding is not a frame
    ///         without shadows — it is every draw in every pass refused, while <c>DrawCount</c> climbs.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the count is a <em>ratio</em> rather than a floor.</b> "More than zero draws"
    ///         is true of the single-pane arrangement this replaced. Four panes over one scene is four
    ///         times the work of one, and nothing else in the frame says so:
    ///         <c>MeshRenderFeature.DrawCount</c> is a lifetime total, which is why this is measured as
    ///         a difference across one frame.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Four_panes_record_four_panes_worth_of_draws_and_bind_every_set_whole() {
        var session = Quad();
        var world = session.Application.Frame!;
        var presenters = Presenters(session);

        Settled(session, presenters);

        var before = world.Renderer.Meshes.DrawCount;
        var graph = new RenderGraph(device);

        Compose(session, graph, presenters);

        var drawn = world.Renderer.Meshes.DrawCount - before;

        Assert.True(world.IsComplete, $"set 0 never bound; nothing filled: {world.MissingBinding}");
        Assert.Null(world.MissingBinding);
        Assert.True(world.Renderer.Materials.BoundCount > 0, "set 2 never bound, so every draw was refused");

        Assert.Equal(EditorWorldRenderer.MaxPanes * world.ObjectCount, drawn);

        graph.Reset();
    }

    /// <summary>Nothing the frame's descriptor sets were written with names nothing.</summary>
    /// <remarks>
    ///     ⚠ <b>A descriptor that names nothing counts as filled.</b> <c>EffectSetWriter</c> resolves a
    ///     name without asking whether the handle behind it is valid, so a pane whose target was
    ///     released by a resize and never re-imported produces a set that is <em>complete</em> and
    ///     points at nothing — a bound set, a healthy counter and an empty picture.
    /// </remarks>
    [Fact]
    public void No_descriptor_any_pane_wrote_names_nothing() {
        var session = Quad();

        Settled(session, Presenters(session));

        var writes = device.RecordedWrites;

        Assert.NotNull(writes);
        Assert.NotEmpty(writes);

        foreach (var write in writes) {
            Assert.True(
                write.TextureView.IsValid || write.Buffer.IsValid || write.Sampler.IsValid
                || write.Structure.IsValid,
                $"binding {write.Binding} was written naming nothing at all"
            );
        }
    }

    // ------------------------------------------------------------------ the registration

    /// <summary>Each pane's registered tree is that pane's, rather than one tree registered four times.</summary>
    /// <remarks>
    ///     ⚠ <b>The registration is what the whole arrangement rests on.</b> A tree names a view and a
    ///     pair of targets, and both belong to the pane — so four panes registered against one tree is
    ///     four panes aiming one view into one texture, which is what a composed quad looked like from
    ///     the outside before this: four panes nominally in the same mode that do not match.
    /// </remarks>
    [Fact]
    public void Each_pane_is_registered_against_its_own_tree() {
        var session = Quad();
        var world = session.Application.Frame!;
        var panes = session.Application.Viewports;

        var shaded = new List<SceneRenderer>();
        var wireframe = new List<SceneRenderer>();

        for (var pane = 0; pane < panes.Count; pane++) {
            var registered = panes[pane].Modes;

            Assert.Contains(ViewMode.Shaded, registered.Registered);
            Assert.Same(world.Trees(pane)[ViewMode.Shaded], registered.Resolve(ViewMode.Shaded));

            shaded.Add(registered.Resolve(ViewMode.Shaded)!);

            if (registered.Registered.Contains(ViewMode.Wireframe)) {
                wireframe.Add(registered.Resolve(ViewMode.Wireframe)!);
            }
        }

        Assert.Equal(shaded.Count, shaded.Distinct().Count());
        Assert.Equal(wireframe.Count, wireframe.Distinct().Count());

        // ⚠ And no tree is shared *between* the modes either, which is what makes the switch a
        // different frame rather than the same one under another name.
        Assert.Empty(shaded.Intersect(wireframe));
    }

    /// <summary>A pane past the document's slots keeps the tool renderer rather than failing.</summary>
    /// <remarks>
    ///     ⚠ <b>Four is <c>ViewportArrangement.Quad</c> and the document is built in a constructor</b>,
    ///     so a fifth pane has no view bound by name and no sub-frame to draw into. Empty modes is what
    ///     <c>EditorHost.Composes</c> reads as "this pane is the tool renderer's", which draws — where a
    ///     presenter built for it would lend the frame an import naming a target no node writes.
    /// </remarks>
    [Fact]
    public void A_pane_past_the_documents_slots_has_no_tree_rather_than_a_broken_one() {
        var world = Quad().Application.Frame!;

        Assert.Empty(world.Trees(EditorWorldRenderer.MaxPanes));
        Assert.Empty(world.Trees(-1));

        Assert.Throws<ArgumentOutOfRangeException>(() => world.ViewOf(EditorWorldRenderer.MaxPanes));
        Assert.Throws<ArgumentOutOfRangeException>(() => world.ViewOf(-1));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new FramePresenter(
                device,
                world,
                new LineShaders(Stage(ShaderStage.Vertex, "line.vs"), Stage(ShaderStage.Fragment, "line.fs")),
                new MeshShaders(Stage(ShaderStage.Vertex, "mesh.vs"), Stage(ShaderStage.Fragment, "mesh.fs")),
                FramePresenter.ColourFormat,
                image: 64,
                pane: EditorWorldRenderer.MaxPanes
            )
        );
    }

    // ------------------------------------------------------------------ helpers

    static void AssertClose(Matrix4x4 expected, Matrix4x4 actual, int pane) {
        for (var row = 1; row <= 4; row++) {
            for (var column = 1; column <= 4; column++) {
                Assert.True(
                    Math.Abs(expected[row, column] - actual[row, column]) < 1e-4f,
                    $"pane {pane}'s view is not looking through its own camera at [{row},{column}]: "
                    + $"{expected[row, column]} vs {actual[row, column]}"
                );
            }
        }
    }

    UiShaders Shaders() =>
        new(
            Stage(ShaderStage.Vertex, "ui.vs"),
            Stage(ShaderStage.Fragment, "ui.box"),
            Stage(ShaderStage.Fragment, "ui.text"),
            Stage(ShaderStage.Fragment, "ui.solid")
        ) {
            Image = Stage(ShaderStage.Fragment, "ui.image")
        };

    ShaderHandle Stage(ShaderStage stage, string name) => device.CreateShader(stage, [1, 2, 3, 4], name);
}
