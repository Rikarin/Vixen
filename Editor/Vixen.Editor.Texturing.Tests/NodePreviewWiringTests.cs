// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>Doc 48 § M4's per-node previews, from the module that assigns them.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/1015">#1015</a>, which is the oldest
///         unwired thing on this board and was invisible for exactly one reason:</b>
///         <c>TextureGraphPreviews</c> had five device tests of its own and no production caller, so
///         every picture it can make was made in a fixture and none of them was ever drawn. The
///         tests here are about the seam those tests cannot see — that something in the editor
///         constructs one, hands it to the canvas, and runs its expensive tier from outside a draw.
///     </para>
///     <para>
///         ⚠ <b>Through the shell and the plugin host rather than by constructing the module.</b>
///         The claim is that a person who opens the panel gets swatches; a test that called an
///         internal method would pass in an editor where <c>OnUpdate</c> was never registered, which
///         is the state this closes.
///     </para>
/// </remarks>
public class NodePreviewWiringTests {
    /// <summary>One frame, as the host would deliver it.</summary>
    static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(16);

    /// <summary>⚠ The assignment itself: opening the panel gives its canvas a preview source.</summary>
    /// <remarks>
    ///     <b>This is the whole of the issue as one line.</b> Every other test here is about the
    ///     source doing its job; this one is about the canvas having been given one, which is the
    ///     part that was missing while everything else worked.
    /// </remarks>
    [Fact]
    public void Opening_the_graph_panel_gives_its_canvas_a_preview_source() {
        using var fixture = new TexturingFixture(graphics: true);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());

        var canvas = Canvas(fixture);

        Assert.NotNull(canvas.PreviewSource);
    }

    /// <summary>⚠ And a host publishing no graphics gets none, and its per-frame step is still safe.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The state a headless editor and every test of everything else is in.</b> The module
    ///         builds nothing device-shaped without an <c>IEditorGraphics</c>, so there is no source
    ///         to assign — and the assertion is that this is a null rather than a throw out of the
    ///         panel factory.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the frame is driven, because a plugin that throws from its per-frame work is
    ///         <em>unloaded</em>.</b> <c>PluginHost.Update</c> answers an exception by deactivating
    ///         the plugin and recording a diagnostic, so a null-unsafe update would not fail loudly
    ///         here — it would make the whole texturing plugin disappear on the first frame of a
    ///         headless session, which is the quietest failure this module has available to it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_host_with_no_graphics_gets_no_preview_source_and_survives_a_frame() {
        using var fixture = new TexturingFixture();

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());

        var canvas = Canvas(fixture);

        Assert.Null(canvas.PreviewSource);

        fixture.Host.Update(Frame);

        Assert.Empty(fixture.Host.Diagnostics);
    }

    /// <summary>⚠ The per-frame step draws each node's own picture, and a draw draws none.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The two tiers at the seam, on a real adapter.</b> <c>TryGet</c> is called once per
    ///         visible node from the canvas's draw and must never record commands on a device, so
    ///         the first ask answers nothing; the picture arrives on the frame after, through
    ///         <c>PluginContext.OnUpdate</c>. A wiring that ran the expensive tier from the draw
    ///         would pass an "is there a picture" assertion and be wrong in the way that matters.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The pixels are asserted rather than the handle, and that is the difference
    ///         between this and a green test.</b> A source that registered a black square per node
    ///         satisfies every count; what says the swatch is <em>this node's</em> picture is that
    ///         the grey the author typed comes back out of the host's upload. 0.25 is ~64 and 0.75 is
    ///         ~191, which one byte tells apart.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_per_frame_step_draws_the_picture_the_author_typed() {
        using var device = TexturingDevice.Open();
        using var fixture = new TexturingFixture(device);
        var adapter = TexturingDevice.Adapter(device);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(fixture.AddGraph("Bricks"));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenCommand));

        var canvas = Canvas(fixture);
        var source = canvas.PreviewSource;

        Assert.NotNull(source);

        var document = Assert.Single(fixture.Project.Documents.OfType<TextureGraphDocument>());
        var uniform = document.Graph.Nodes.Single(node => node.Type == "Source/Uniform");

        uniform.SetValue("Colour", 0.25f, 0.25f, 0.25f, 1f);
        document.Graph.Touch();

        Assert.True(canvas.Registry.TryGet(uniform.Type, out var definition));

        // ⚠ Counted rather than expected empty: the *pane* has already uploaded the map it is
        // showing, at the document's own resolution, and folding the two together would make this
        // assertion about whichever ran last.
        var before = fixture.Graphics!.Uploads.Count;

        // A draw, and it evaluates nothing. This also subscribes the source to the graph, which is
        // how a canvas that draws a graph nobody has touched still gets pictures.
        Assert.False(source.TryGet(document.Graph, uniform, definition!, out _));
        Assert.Equal(before, fixture.Graphics.Uploads.Count);

        fixture.Host.Update(Frame);

        Assert.True(
            source.TryGet(document.Graph, uniform, definition!, out var preview),
            $"{adapter}: the frame produced no picture for the node the author is looking at"
        );

        Assert.NotEqual(0ul, preview.Image);

        var drawn = Assert.Single(fixture.Graphics.Uploads, upload => upload.Image == preview.Image);

        // Square, because that is what a swatch is, and it is read off the upload rather than
        // transcribed — the size is the preview source's business and this suite has no opinion.
        Assert.Equal(drawn.Width, drawn.Height);
        Assert.True(drawn.Width > 0, $"{adapter}: the picture the canvas was handed has no extent");

        var dark = Middle(drawn);

        Assert.True(dark is > 55 and < 75, $"{adapter}: 0.25 grey came back as {dark}");

        // ⚠ And the other half of "it is this node's picture": change the number and the swatch
        // follows. Without this the assertion above is satisfied by any source that happened to
        // produce a mid-grey — a black square would not, but a graph's default colour might.
        uniform.SetValue("Colour", 0.75f, 0.75f, 0.75f, 1f);
        document.Graph.Touch();
        fixture.Host.Update(Frame);

        Assert.True(source.TryGet(document.Graph, uniform, definition!, out var second));

        // ⚠ The same number, which is the claim about cost rather than about correctness: a rebuild
        // rewrites the texels of the picture that is already on the screen. A sink that uploaded
        // afresh would draw the identical swatch and cost a texture and a descriptor set per node
        // per keystroke — #912's finding with the atlas swapped for a swatch — and nothing on screen
        // would say so.
        Assert.Equal(preview.Image, second.Image);

        var patched = Assert.Single(fixture.Graphics.Updates, update => update.Image == second.Image);
        var light = Middle(patched.Pixels, patched.Width, patched.Height);

        Assert.True(light is > 180 and < 200, $"{adapter}: 0.75 grey came back as {light}");
    }

    /// <summary>⚠ A device that goes takes the swatches with it, and they come back with the next one.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The lifetime half, which is the one a wiring commit forgets.</b> A number handed to
    ///         the canvas names a texture the host made on the device that is going; the source
    ///         outlives that device, because the canvas is still holding it. Keeping the numbers
    ///         would draw a swatch through a handle whose texture has been destroyed — which the
    ///         interface resolves silently rather than reporting.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both halves, because releasing alone is a panel that goes blank for good.</b> The
    ///         pictures are given back <em>and</em> every watched graph is marked for a rebuild, so
    ///         the frame after the device comes back has swatches on it again. A source that only
    ///         released would pass every "no stale handle" assertion and leave an author looking at
    ///         empty nodes until they typed something.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_lost_device_gives_the_swatches_back_and_asks_for_them_again() {
        using var device = TexturingDevice.Open();
        using var fixture = new TexturingFixture(device);
        var adapter = TexturingDevice.Adapter(device);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(fixture.AddGraph("Bricks"));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenCommand));

        var canvas = Canvas(fixture);
        var source = canvas.PreviewSource;

        Assert.NotNull(source);

        var document = Assert.Single(fixture.Project.Documents.OfType<TextureGraphDocument>());
        var uniform = document.Graph.Nodes.Single(node => node.Type == "Source/Uniform");

        Assert.True(canvas.Registry.TryGet(uniform.Type, out var definition));

        source.TryGet(document.Graph, uniform, definition!, out _);
        fixture.Host.Update(Frame);

        Assert.True(
            source.TryGet(document.Graph, uniform, definition!, out _),
            $"{adapter}: nothing was drawn, so this test is about a source that never ran"
        );

        // ⚠ The source's whole registry, read off the source itself. A count of the host's uploads
        // would fold in the pane's own map — which is uploaded per refresh and is not a swatch — and
        // this assertion would then be about whichever of the two churned last.
        HashSet<ulong> swatches = [];

        foreach (var node in document.Graph.Nodes) {
            if (canvas.Registry.TryGet(node.Type, out var type)
                && source.TryGet(document.Graph, node, type!, out var swatch)
                && swatch.Image != 0) {
                swatches.Add(swatch.Image);
            }
        }

        Assert.True(swatches.Count > 0, $"{adapter}: no picture reached the host at all");

        var released = fixture.Graphics!.Released;

        fixture.Host.DeviceLost(device);

        // Every picture, and not merely one, measured across the loss alone.
        Assert.Equal(released + swatches.Count, fixture.Graphics.Released);
        Assert.False(source.TryGet(document.Graph, uniform, definition!, out _));

        // ⚠ And they are asked for again rather than forgotten. The module holds the same source —
        // disposing it would leave the canvas drawing through a disposed object whose `Update`
        // throws, which `PluginHost.Update` answers by unloading the plugin.
        fixture.Host.Update(Frame);

        Assert.True(
            source.TryGet(document.Graph, uniform, definition!, out var after),
            $"{adapter}: the swatches never came back after the device did"
        );

        Assert.NotEqual(0ul, after.Image);
        Assert.Empty(fixture.Host.Diagnostics);
    }

    /// <summary>The canvas in the graph panel, opened the way a person opens it.</summary>
    /// <param name="fixture">The host.</param>
    /// <returns>The canvas.</returns>
    static NodeGraphView Canvas(TexturingFixture fixture) {
        var panel = fixture.Shell.Workspace.Open(TexturingModule.GraphPanel);

        Assert.NotNull(panel);

        var canvas = Find<NodeGraphView>(panel);

        Assert.NotNull(canvas);

        return canvas;
    }

    /// <summary>The middle pixel's red channel of an upload.</summary>
    /// <param name="upload">What the host was handed.</param>
    /// <returns>The byte.</returns>
    static byte Middle(RecordingGraphics.Uploaded upload) => Middle(upload.Pixels, upload.Width, upload.Height);

    /// <summary>The middle pixel's red channel of a rectangle of pixels.</summary>
    /// <param name="pixels">The pixels, four bytes each, top row first.</param>
    /// <param name="width">How wide.</param>
    /// <param name="height">How tall.</param>
    /// <returns>The byte.</returns>
    static byte Middle(byte[] pixels, int width, int height) => pixels[((((height / 2) * width) + (width / 2)) * 4)];

    static T? Find<T>(UiElement element) where T : UiElement {
        if (element is T found) {
            return found;
        }

        foreach (var child in element.Children) {
            if (Find<T>(child) is { } inside) {
                return inside;
            }
        }

        return null;
    }
}
