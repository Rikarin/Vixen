// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.SceneView;
using Vixen.Editor.Testing;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.PostFx;
using Vixen.Ui.Renderer;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The pane a compositor draws, and the two silent ways it would not draw.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Nothing here asserts that a method was called.</b> A pane that constructs, resizes,
///         uploads and declares without a surviving fragment is this engine's commonest failure and
///         passes every structural test — so what is asserted is that set 0 <em>bound</em>, that the
///         frame's objects carry the stage the document declared, and that the texture handed back to
///         the interface is the one the pane lent the frame.
///     </para>
///     <para>
///         On <see cref="NullDevice" />, which records rather than draws. The Raven compilation, the
///         descriptor writing, the suballocation and the set-completeness bookkeeping are all the ones
///         a real frame uses; only the submission is not. The picture itself is a thing to look at
///         rather than a thing to assert, and this file does not pretend otherwise.
///     </para>
/// </remarks>
public sealed class FramePresenterTests : IDisposable {
    readonly NullDevice device = new();
    readonly List<IDisposable> owned = [];

    public void Dispose() {
        for (var index = owned.Count - 1; index >= 0; index--) {
            owned[index].Dispose();
        }

        device.Dispose();
    }

    /// <summary>An editor whose host has handed it a device, one frame in.</summary>
    EditorSession Running() {
        var session = EditorSession.Start();

        owned.Add(session);

        session.Application.GraphicsDevice = device;
        session.Frame();

        return session;
    }

    // ------------------------------------------------------------------ the frame document

    /// <summary>
    ///     The document is built before anything extracts, so the scene that was already there is in
    ///     the stage the document declared.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The ordering this whole increment turns on, asserted on the entities the application's
    ///     own loop extracted rather than on ones the test placed.</b> A stage's index is assigned when
    ///     a document declares it, and the mask is copied into each render object as it is created —
    ///     so a document built by whichever pane opens first is a document built after the first
    ///     <c>ExtractFrame</c>, and every object already in the scene carries a mask of none. That
    ///     draws nothing, reports two objects, one light and zero waiting, and fails no other test in
    ///     this suite. It is why the build is in a constructor.
    /// </remarks>
    [Fact]
    public void The_scene_that_was_already_there_is_in_the_stage_the_document_declared() {
        var frame = Running().Application.Frame!;

        Assert.Equal(2, frame.ObjectCount);
        Assert.NotEqual(default, frame.Opaque.Mask);
        Assert.True(frame.Stages.Contains(frame.Opaque.Index), "the shaded stage is not in the extraction mask");

        foreach (ref var live in frame.Renderer.Host.System.Objects.All) {
            Assert.Equal(frame.Stages, live.Stages);
        }
    }

    /// <summary>Every mode's stage is in the mask, not only the mode the pane happens to open in.</summary>
    /// <remarks>
    ///     ⚠ <b>The union, and it is the assertion the view-mode switch turns on.</b> A mask is copied
    ///     into a render object as it is created and a settled entity is never extracted again — so a
    ///     mask carrying only the shaded bit is a pane that draws until somebody picks Wireframe and
    ///     then draws nothing, while still reporting its two objects, its one light, zero waiting and
    ///     zero dropped. Every stage the builder made, because a stage a mode might resolve to and a
    ///     stage the mask covers have to be the same set.
    /// </remarks>
    [Fact]
    public void The_extraction_mask_is_the_union_of_every_modes_stage() {
        var frame = Running().Application.Frame!;
        var stages = frame.Renderer.Host.Builder.Stages.Values.ToList();

        // The document declares two: the shaded one and the wireframe one.
        Assert.True(stages.Count >= 2, $"the document declared {stages.Count} stage(s), so there is no union to make");

        foreach (var stage in stages) {
            Assert.True(
                frame.Stages.Contains(stage.Index),
                $"stage '{stage.Name}' is one a mode can resolve to and no extracted object carries it"
            );
        }

        // And the objects carry it, which is the half that a mask set after the first extract fails.
        foreach (ref var live in frame.Renderer.Host.System.Objects.All) {
            foreach (var stage in stages) {
                Assert.True(live.Stages.Contains(stage.Index), $"an object is not in stage '{stage.Name}'");
            }
        }
    }

    /// <summary>A mode with a tree is the compositor's; one without is left to the tool renderer.</summary>
    /// <remarks>
    ///     ⚠ <b><c>Registered</c> and not <c>Resolve</c>, and the difference is the whole choice.</b>
    ///     <c>Resolve</c> falls back to the shaded tree for any mode, which is right for picking a tree
    ///     to draw and wrong for asking whether this mode is a compositor's — read that way, every mode
    ///     would compose and Albedo would draw the shaded picture.
    /// </remarks>
    [Fact]
    public void A_mode_is_the_compositors_only_when_a_tree_was_registered_for_it() {
        var session = Running();
        var modes = session.Application.Viewports[0].Modes;

        Assert.Contains(ViewMode.Shaded, modes.Registered);

        // Nothing has authored a tree for these, and they are what the tool renderer draws.
        Assert.DoesNotContain(ViewMode.Albedo, modes.Registered);
        Assert.DoesNotContain(ViewMode.Normal, modes.Registered);
        Assert.DoesNotContain(ViewMode.Roughness, modes.Registered);

        // ⚠ And `Resolve` still answers for all of them, which is why the host may not ask it.
        Assert.NotNull(modes.Resolve(ViewMode.Albedo));
        Assert.Same(modes.Resolve(ViewMode.Shaded), modes.Resolve(ViewMode.Albedo));
    }

    /// <summary>A frame document edited while the editor is open reaches the pane, objects and all.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The stage index is what makes this more than a rebuild.</b> A document naming a
    ///         stage the old one did not gets a fresh index, and every object already in the store
    ///         carries a mask without that bit — so a reload that only rebuilt would leave a pane
    ///         reporting its two objects, its one light, nothing waiting and nothing dropped, drawing
    ///         an empty frame. The objects have to be resettled and extracted again.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the sky node has to be handed its cube again</b>, because the nodes are made
    ///         by the builder — one that never got a cube draws black, which is exactly what a missing
    ///         background looks like.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_frame_document_reloaded_reaches_the_objects_that_were_already_extracted() {
        var session = Running();
        var frame = session.Application.Frame!;

        var before = frame.Stages;

        Assert.Equal(2, frame.ObjectCount);

        // A frame of somebody else's, naming a stage the editor's document does not.
        frame.Reload(
            new GraphicsCompositorAsset {
                Version = CompositorBuilder.SupportedVersion,
                Stages = [new() { Name = "Authored" }],
                Resources = [
                    new() {
                        Name = FramePresenter.DepthTarget,
                        Format = FramePresenter.DepthFormat,
                        Usage = TextureUsage.DepthStencilTarget
                    }
                ],
                Game = new SequenceAsset {
                    Name = "Authored frame",
                    Children = [
                        new SkyAsset { Name = "Sky", Output = FramePresenter.ColourTarget, View = EditorWorldRenderer.CameraView },
                        new RenderPassAsset {
                            Name = "Shade",
                            ColourTargets = [FramePresenter.ColourTarget],
                            Load = LoadAction.Load,
                            DepthTarget = FramePresenter.DepthTarget,
                            Children = [
                                new SingleStageAsset {
                                    Name = "Authored draw",
                                    View = EditorWorldRenderer.CameraView,
                                    Stage = "Authored"
                                }
                            ]
                        }
                    ]
                }
            },
            session.Application.Scene.World
        );

        var stages = frame.Renderer.Host.Builder.Stages;

        Assert.True(stages.TryGetValue("Authored", out var authored), "the document's own stage was not built");
        Assert.NotEqual(before, frame.Stages);
        Assert.True(frame.Stages.Contains(authored!.Index), "the new stage is not in the extraction mask");

        // ⚠ The half with no symptom of its own: the objects were already settled when the document
        // changed, so a reload that did not resettle leaves them in stages this frame does not draw.
        Assert.Equal(2, frame.ObjectCount);

        foreach (ref var live in frame.Renderer.Host.System.Objects.All) {
            Assert.True(live.Stages.Contains(authored.Index), "an object still carries the old document's mask");
        }

        // The whole frame is the shaded mode, because a project's document knows no mode names.
        Assert.Same(frame.Compositor.Game, frame.Trees[ViewMode.Shaded]);

        // And a sky node the builder just made has the cube rather than nothing.
        foreach (var node in frame.Renderer.Host.Builder.Nodes.Values) {
            if (node is SkyRenderer background) {
                Assert.True(background.Environment.IsValid, "the rebuilt sky node never got its cube");
            }
        }
    }

    /// <summary>Wireframe is a stage of its own rather than the shaded stage mutated.</summary>
    /// <remarks>
    ///     ⚠ <b>Because <c>PipelineKey</c> is <c>(Effect, Stage.Index, VertexLayout, Output)</c> and
    ///     <c>PipelineCache</c> never evicts.</b> A stage's rasterizer is read once, by
    ///     <c>EffectPipelineDescriber.Describe</c> on the first draw that misses the cache, and baked
    ///     into a pipeline the key cannot tell from any other state on that stage — so
    ///     <c>ViewModes.ApplyTo</c> against a stage that has already drawn changes the mode and not the
    ///     picture. Two indices is what makes the two modes two pipelines.
    /// </remarks>
    [Fact]
    public void Wireframe_is_a_second_stage_and_the_shaded_one_is_left_filled() {
        var frame = Running().Application.Frame!;
        var stages = frame.Renderer.Host.Builder.Stages;

        Assert.True(stages.TryGetValue("Wireframe", out var wires), "there is no wireframe stage to draw one with");
        Assert.NotEqual(frame.Opaque.Index, wires!.Index);

        Assert.Equal(FillMode.Solid, frame.Opaque.Rasterizer.Fill);
        Assert.Equal(FillMode.Wireframe, wires.Rasterizer.Fill);

        // ⚠ Both faces, because a wireframe view of a closed mesh with the back faces culled is half
        // the edges — and the half that is missing is the half a modeller is looking for.
        Assert.Equal(CullMode.None, wires.Rasterizer.Cull);
    }

    /// <summary>The host's own graph stays empty, because the pane draws into the window's.</summary>
    /// <remarks>
    ///     ⚠ <b>A <c>Host.Load</c> here would be a second graph and a second execution.</b>
    ///     <c>WorldRenderer.Draw</c> ends in <c>SceneRenderHost.Draw</c>, which resets the host's own
    ///     graph, builds the compositor into it and executes it — so a compositor parked there would
    ///     draw the frame somewhere the editor's interface pass cannot declare a read against, and the
    ///     pane would then draw it a second time. Null is what makes <c>Draw</c> the prologue alone.
    /// </remarks>
    [Fact]
    public void The_renderers_own_host_holds_no_compositor_so_its_draw_is_only_the_prologue() {
        var frame = Running().Application.Frame!;

        Assert.Null(frame.Renderer.Host.Compositor);
        Assert.NotNull(frame.Compositor);

        using var commands = device.BeginCommandList(QueueKind.Graphics, "prologue");

        Assert.False(frame.Renderer.Host.Draw(commands));
        Assert.Equal(0, frame.Renderer.Host.FrameCount);
    }

    // ------------------------------------------------------------------ the picture

    /// <summary>Three frames of the seeded scene bind set 0 whole and record draws.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>IsComplete</c> is the assertion, and it is the one a black pane fails.</b>
    ///         <c>ForwardPlus</c> declares <c>shadowMap</c>, <c>environment</c>, the four probes,
    ///         their three samplers and <c>clusters</c> whatever its permutations say — a permutation
    ///         folds code, not bindings — and <c>EffectSetWriter</c> writes a set whole or not at all.
    ///         The default frame produces none of them, so before
    ///         <see cref="EditorWorldRenderer" /> supplied them every draw in the pass was refused
    ///         while every counter in the frame reported success.
    ///     </para>
    ///     <para>
    ///         Three frames rather than one, because the set-1 layout is adopted off the first shader
    ///         to resolve and nothing has resolved on the frame before the first build.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_frame_binds_set_zero_whole_and_draws_the_scene() {
        var session = Running();
        var frame = session.Application.Frame!;
        var graph = new RenderGraph(device);

        var colour = device.CreateTexture(
            new(PixelFormat.Rgba8UNorm, 256, 256, TextureUsage.ColourTarget | TextureUsage.Sampled, Name: "colour")
        );

        var view = device.CreateTextureView(colour);

        frame.Compositor.Imports[FramePresenter.ColourTarget] = new(
            colour,
            view,
            new(PixelFormat.Rgba8UNorm, 256, 256, TextureUsage.ColourTarget | TextureUsage.Sampled, Name: "colour"),
            ResourceState.Undefined,
            ResourceState.ShaderRead
        );

        frame.Aim(new EditorCamera { Distance = 8f }, 1f);

        for (var pass = 0; pass < 3; pass++) {
            using var commands = device.BeginCommandList(QueueKind.Graphics, "frame");

            frame.Upload(commands);
            frame.Renderer.Draw(commands);

            graph.Reset();
            frame.Compositor.Build(graph, frame.Renderer.Host.Effects, device);
            graph.Execute(commands);

            commands.Finish();
        }

        Assert.True(frame.IsComplete, $"set 0 never bound; nothing filled: {frame.MissingBinding}");
        Assert.Null(frame.MissingBinding);

        // ⚠ And that something was actually recorded. A complete set over an empty draw list is a
        // frame of clear colour that satisfies every line above.
        Assert.True(frame.Renderer.Meshes.DrawCount > 0, "the frame recorded no draws at all");
        Assert.True(frame.Renderer.Materials.BoundCount > 0, "set 2 never bound, so every draw was refused");

        // The set-1 layout, which is adopted off the first resolved shader and without which the
        // camera's set is never bound either.
        Assert.True(frame.Renderer.ViewBlock.Layout.IsValid, "the view block never adopted a set-1 layout");

        device.Destroy(view);
        device.Destroy(colour);
    }

    /// <summary>Every descriptor the frame's set 0 wrote names something.</summary>
    /// <remarks>
    ///     ⚠ <b>Because a descriptor that names nothing counts as filled.</b>
    ///     <c>EffectSetWriter</c> resolves a name without asking whether the handle behind it is
    ///     valid, so a stand-in that was created but never assigned produces a set that is
    ///     <em>complete</em> and points at nothing — which is a bound set, a healthy counter and an
    ///     empty picture. This reads the writes back and asks what each one names.
    /// </remarks>
    [Fact]
    public void No_descriptor_in_the_frames_set_zero_names_nothing() {
        using var recording = new NullDevice(new() { Record = true });
        var session = EditorSession.Start();

        owned.Add(session);

        session.Application.GraphicsDevice = recording;
        session.Frame();

        var frame = session.Application.Frame!;
        var graph = new RenderGraph(recording);

        frame.Aim(new EditorCamera { Distance = 8f }, 1f);

        for (var pass = 0; pass < 3; pass++) {
            using var commands = recording.BeginCommandList(QueueKind.Graphics, "frame");

            frame.Upload(commands);
            frame.Renderer.Draw(commands);

            graph.Reset();
            frame.Compositor.Build(graph, frame.Renderer.Host.Effects, recording);
            graph.Execute(commands);

            commands.Finish();
        }

        var writes = recording.RecordedWrites;

        Assert.NotNull(writes);
        Assert.NotEmpty(writes);

        foreach (var write in writes) {
            var named = write.TextureView.IsValid || write.Buffer.IsValid || write.Sampler.IsValid
                || write.Structure.IsValid;

            Assert.True(named, $"binding {write.Binding} was written naming nothing at all");
        }
    }

    // ------------------------------------------------------------------ the pane

    /// <summary>The pane's tool pass is the last thing in the frame, and it loads what it draws over.</summary>
    /// <remarks>
    ///     ⚠ <b>Both load actions, because they are what make the tools a viewport rather than a
    ///     decal.</b> A pass that cleared the colour would wipe the composition the frame spent itself
    ///     producing; one that cleared — or never declared — the depth would draw the grid straight
    ///     through the geometry standing on it.
    ///     <c>Platform/Vixen.Graphics.Golden.Tests/ViewportOverlayImageTests</c> renders both frames
    ///     so the difference is a picture rather than an argument.
    /// </remarks>
    [Fact]
    public void The_tool_pass_loads_the_frames_colour_and_its_depth() {
        var session = Running();
        var viewport = session.Application.Viewports[0];
        var frame = session.Application.Frame!;

        using var presenter = Presenter(session);

        var renderer = new UiRenderer(device, Shaders(), new RenderOutput([PixelFormat.Bgra8UNorm]));

        owned.Add(renderer);

        Assert.True(presenter.Resize(viewport, renderer), "the pane never got a target");

        // ⚠ Both modes, because the tool pass follows whichever tree the mode resolved to. A pass
        // appended to one tree at construction is a wireframe pane with no grid, no markers and no
        // gizmo — the modes would differ in a way the mode's name does not mean.
        foreach (var mode in (ViewMode[]) [ViewMode.Shaded, ViewMode.Wireframe]) {
            viewport.Modes.Current = mode;

            var graph = new RenderGraph(device);

            using var commands = device.BeginCommandList(QueueKind.Graphics, "pane");

            presenter.Upload(commands, session.Application.Scene, viewport);

            Assert.True(presenter.Declare(graph, viewport, out _), $"the pane declared no frame in {mode}");

            var sequence = Assert.IsType<SceneRendererSequence>(frame.Compositor.Game);

            // The mode's own tree first, this pane's tools after it — and nothing else.
            Assert.Equal(2, sequence.Children.Count);
            Assert.Same(viewport.Modes.Resolve(), sequence.Children[0]);

            var pass = Assert.IsType<RenderPassRenderer>(sequence.Children[^1]);

            Assert.Equal(LoadAction.Load, pass.Load);
            Assert.Equal(LoadAction.Load, pass.DepthLoad);
            Assert.Equal(FramePresenter.DepthTarget, pass.DepthTarget);
            Assert.Contains(FramePresenter.ColourTarget, pass.ColourTargets);

            // ⚠ Read-only, which is what keeps the attachment readable by anything after it — and is
            // true because every pipeline the pass records is depth-tested and never depth-writing.
            Assert.True(pass.ReadOnlyDepth, "the tool pass claims to write depth, so nothing after it may read it");

            commands.Finish();
        }

        // ⚠ And the two modes are two different frames rather than one tree with a flag on it.
        viewport.Modes.Current = ViewMode.Shaded;

        var shaded = viewport.Modes.Resolve();

        viewport.Modes.Current = ViewMode.Wireframe;

        Assert.NotSame(shaded, viewport.Modes.Resolve());
    }

    /// <summary>The texture handed to the interface is the one the pane lent the frame.</summary>
    /// <remarks>
    ///     ⚠ <b>An import wins over a same-named declaration, and this is that rule being relied
    ///     on.</b> The default frame declares <c>SceneColour</c> and <c>SceneDepth</c> itself; if the
    ///     declaration won instead, the frame would draw into a transient the interface has no view
    ///     of and the pane would sample a target nothing ever wrote.
    /// </remarks>
    [Fact]
    public void The_interface_is_handed_the_panes_own_target_rather_than_a_transient() {
        var session = Running();
        var viewport = session.Application.Viewports[0];

        using var presenter = Presenter(session);

        var renderer = new UiRenderer(device, Shaders(), new RenderOutput([PixelFormat.Bgra8UNorm]));

        owned.Add(renderer);

        Assert.True(presenter.Resize(viewport, renderer), "the pane never got a target");

        var graph = new RenderGraph(device);

        using var commands = device.BeginCommandList(QueueKind.Graphics, "pane");

        presenter.Upload(commands, session.Application.Scene, viewport);

        Assert.True(presenter.Declare(graph, viewport, out var target), "the pane declared no frame");

        graph.AddPass(
            "ui",
            pass => {
                pass.SideEffect();
                pass.Reads(target);
                pass.Execute(_ => { });
            }
        );

        graph.Execute(commands);
        commands.Finish();

        Assert.Equal(viewport.Control.RenderTarget, presenter.Image);
        Assert.Equal(presenter.Width, viewport.Control.RenderWidth);

        // Nothing in the default frame declines to run, so the pane has nothing to warn about — and
        // an empty list here is what makes a non-empty one worth showing.
        Assert.Empty(presenter.Degradations);
    }

    // ------------------------------------------------------------------ helpers

    FramePresenter Presenter(EditorSession session) =>
        new(
            device,
            session.Application.Frame!,
            new LineShaders(Stage(ShaderStage.Vertex, "line.vs"), Stage(ShaderStage.Fragment, "line.fs")),
            new MeshShaders(Stage(ShaderStage.Vertex, "mesh.vs"), Stage(ShaderStage.Fragment, "mesh.fs")),
            PixelFormat.Rgba8UNorm,
            1024
        );

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
