// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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
        Assert.Equal(frame.Opaque.Mask, frame.Stages);

        foreach (ref var live in frame.Renderer.Host.System.Objects.All) {
            Assert.Equal(frame.Opaque.Mask, live.Stages);
        }
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

        using var presenter = Presenter(session);

        var sequence = Assert.IsType<SceneRendererSequence>(session.Application.Frame!.Compositor.Game);
        var pass = Assert.IsType<RenderPassRenderer>(sequence.Children[^1]);

        Assert.Equal(LoadAction.Load, pass.Load);
        Assert.Equal(LoadAction.Load, pass.DepthLoad);
        Assert.Equal(FramePresenter.DepthTarget, pass.DepthTarget);
        Assert.Contains(FramePresenter.ColourTarget, pass.ColourTargets);

        // ⚠ Read-only, which is what keeps the attachment readable by anything after it — and is
        // true because every pipeline the pass records is depth-tested and never depth-writing.
        Assert.True(pass.ReadOnlyDepth, "the tool pass claims to write depth, so nothing after it may read it");
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
