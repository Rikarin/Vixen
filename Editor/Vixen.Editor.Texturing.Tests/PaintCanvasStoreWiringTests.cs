// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Texturing.Layers;
using Vixen.Editor.Ui;
using Vixen.Ui;
using Vixen.Ui.Controls.Advanced;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>How many times a stroke reads a <c>.vxpaint</c>, and what the map shows while it lasts.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/948">#948</a> and
///         <a href="https://github.com/Rikarin/Vixen/issues/885">#885</a>, end to end through a real
///         drag.</b> Three things read a paint layer's canvas —
///         <c>TexturingModule.BeginStroke</c> at pointer-down, <c>RefreshPaint</c> at pointer-up, and
///         the layers pane on the way to the map — and each of them opened the file. At 4K that is
///         three times 67 MB of read and of allocation per channel per stroke.
///     </para>
///     <para>
///         ⚠ <b>The two halves are one fix and are asserted as one, because either alone is
///         wrong.</b> A cache the pane owned would serve the picture from before the stroke, which is
///         why #885 refused to write one; a store wired only to the surface would leave the pane
///         reading the disk. So this suite asserts the count <em>and</em> that the map shows a stroke
///         which is in no file anywhere — and no arrangement of caches can satisfy both except a
///         store of open canvases.
///     </para>
///     <para>
///         ⚠ <b>A real adapter or a loud skip, because the third read is the pane's and the pane
///         needs a device.</b> Counting on a host with none would count two of the three and call it
///         the answer.
///     </para>
/// </remarks>
public class PaintCanvasStoreWiringTests {
    const int Side = 64;

    /// <summary>A session that made a canvas never reads it back off the disk.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Zero, and the pre-fix number for the same session is five.</b> The first drag
    ///         creates the canvas in memory, so it reads nothing; every open after it — pointer-up's
    ///         refresh, the second drag's pointer-down, its refresh, and the layers pane on each of
    ///         the two saves — used to be a full read of the file the save had just written.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Zero is also what a session that opened nothing reports, and worse than that it
    ///         is what a session that <em>stopped</em> asking reports</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/978">#978</a>. Every read this
    ///         counter sees is a read through the store, so un-wiring a call site back to
    ///         <c>File.OpenRead</c> lowers it and an assertion wanting zero passes: it could only
    ///         fail in the harmless direction. What is asserted instead is <c>CanvasOpens</c>,
    ///         exactly, at three points of a scripted session — the questions asked, which any
    ///         reader that stops asking makes fewer of.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_stroke_reads_the_canvas_off_the_disk_no_times_rather_than_twice() {
        using var device = TexturingDevice.Open();
        using var fixture = new TexturingFixture(device);

        TexturingModule module = new();

        var document = Painting(fixture, module, "Hull");
        var image = ImageIn(OpenPaintPane(fixture));

        // ⚠ The instrument, and the assertion it replaced could only fail in the harmless direction
        // — #978. `CanvasReads` counts reads made *through* the store, so a call site restored to
        // `File.OpenRead` makes it smaller and an assertion wanting zero passes; a threshold on
        // `CanvasHits` passes too, as soon as the readers still wired clear it. `CanvasOpens` counts
        // the questions asked, so any reader that stops asking lowers it and an exact expectation
        // goes red. Traced per event, opening the pane is two, a pointer-down is one, a pointer-move
        // is none and a pointer-up is fifteen: the paint pane's refresh, plus two evaluations of the
        // map, each of which asks about this one canvas once per channel of the set — seven.
        //
        // ⚠ These numbers are the shape of this scripted session and not the claim. A change to the
        // module's refresh policy moves them; what must not change is that they are exact, and a run
        // that updates them has to show `CanvasReads` still zero and the second test in this file
        // still green.
        Assert.Equal(2, module.CanvasOpens);

        Drag(fixture, image, new Vector2(16f, 16f), new Vector2(40f, 40f));

        Assert.Equal(18, module.CanvasOpens);

        Drag(fixture, image, new Vector2(20f, 44f), new Vector2(44f, 20f));

        Assert.Equal(34, module.CanvasOpens);

        // And every one of them was answered from memory: one miss, which is the pane's first look
        // at a layer that had never been painted, and no reads at all.
        Assert.Equal(0, module.CanvasReads);
        Assert.Equal(33, module.CanvasHits);

        // And the stroke really reached the disk, so "no reads" is not "no painting". The file is
        // opened here rather than through the store, which is the only reading of it in this test.
        var layer = Assert.Single(document.Document.Sets[0].Layers, one => one.Kind == LayerKind.Paint);
        var file = Path.Combine(Path.GetDirectoryName(document.AssetPath)!, layer.Paint);

        using var stream = File.OpenRead(file);

        Assert.NotEqual(0u, Vixen.Editor.Texturing.Painting.PaintCanvas.Read(stream).Channel("baseColor").At(16, 16));
    }

    /// <summary>⚠ And the map redraws with a stroke that is in no file, while the pointer is down.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The half <a href="https://github.com/Rikarin/Vixen/issues/885">#885</a> is really
    ///         about, and the one a cache cannot have.</b> A paint session writes
    ///         <c>PaintImage.Texels</c> in memory and does not touch the file until pointer-up, so
    ///         anything that resolves a <c>vxpaint:</c> by reading the file shows the picture from
    ///         before the stroke. Here the file does not exist at all — the first stroke of a fresh
    ///         paint layer has not been saved yet — and the map has the stroke in it, because the
    ///         pane and the drag hold the same <c>PaintCanvas</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Asserted mid-drag, which is the only moment the claim can be made.</b> After
    ///         pointer-up the file exists and holds the stroke, so a pane reading the disk would look
    ///         identical — which is exactly why the old behaviour survived a suite this size.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_map_shows_a_stroke_that_has_not_reached_the_disk() {
        using var device = TexturingDevice.Open();
        using var fixture = new TexturingFixture(device);

        TexturingModule module = new();

        var document = Painting(fixture, module, "Hull");
        var image = ImageIn(OpenPaintPane(fixture));

        // Pointer down and a few moves — and no release, so nothing is saved and nothing is named.
        var start = image.ToScreen(new Vector2(16f, 16f));
        var end = image.ToScreen(new Vector2(40f, 40f));

        fixture.Shell.Document.Dispatch(
            new PointerEvent {
                X = start.X,
                Y = start.Y,
                Action = PointerAction.Pressed,
                Button = PointerButton.Primary
            }
        );

        for (var step = 1; step <= 4; step++) {
            var at = Vector2.Lerp(start, end, step / 4f);

            fixture.Shell.Document.Dispatch(new PointerEvent { X = at.X, Y = at.Y, Action = PointerAction.Moved });
        }

        // The instrument, and it is the whole premise: there is no file, so nothing that reads one
        // could produce the texel below.
        var named = Assert.Single(document.Document.Sets[0].Layers, one => one.Kind == LayerKind.Paint);
        var derived = LayerPaint.NameFor(
            Path.GetFileNameWithoutExtension(document.AssetPath),
            document.Document.Sets[0].Name,
            named.Id
        );

        Assert.Equal("", named.Paint);
        Assert.False(
            File.Exists(Path.Combine(Path.GetDirectoryName(document.AssetPath)!, derived)),
            "the drag wrote its canvas before pointer-up, so this test is no longer about an unsaved stroke"
        );

        // The layer stack the pane compiles has to name the canvas for the map to read it, and only
        // pointer-up writes that name down. Naming it here is what makes the *file* the thing under
        // test rather than the reference: the layer points at a path nothing has written.
        var stack = document.Document;
        var layers = stack.Sets[0].Layers;

        layers[layers.IndexOf(named)] = named with { Paint = derived };
        document.Document = stack;

        var before = fixture.Graphics!.Uploads.Count;

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));
        Assert.True(
            fixture.Graphics.Uploads.Count > before,
            $"{TexturingDevice.Adapter(device)}: the layers pane drew nothing, so there is no map."
        );

        // ⚠ The *first* upload after the command and not the last, which is the trap this test fell
        // into and passed under its own sabotage. `OpenStack` refreshes the layers pane and then the
        // paint pane, and both upload a 64² picture — so `Uploads[^1]` is the paint pane's, which
        // holds the live stroke whatever the map did. Reading it made a preview that had refused the
        // canvas outright look like a preview that had baked it.
        var map = fixture.Graphics.Uploads[before];

        Assert.Equal(Side, map.Width);

        // The instrument, and it is what tells the two panes apart: the starter stack's Base fill is
        // opaque grey everywhere, and the paint pane shows the layer *alone* — transparent where the
        // drag has not been. So an opaque corner is proof this is the map. A preview that refused the
        // unsaved canvas draws nothing at all, and this reads the paint pane's picture and fails here.
        Assert.Equal(255, Alpha(map, Side - 4, 4));

        // And the stroke is in it: the painted texels differ from an untouched one, which for a map
        // composited from one flat fill they could not unless the paint layer contributed.
        var untouched = Texel(map, Side - 4, 4);

        Assert.NotEqual(untouched, Texel(map, 16, 16));
        Assert.NotEqual(untouched, Texel(map, 40, 40));
    }

    static byte Alpha(RecordingGraphics.Uploaded picture, int x, int y) =>
        picture.Pixels[((((y * picture.Width) + x) * 4) + 3)];

    static uint Texel(RecordingGraphics.Uploaded picture, int x, int y) {
        var at = (((y * picture.Width) + x) * 4);

        return picture.Pixels[at]
            | ((uint)picture.Pixels[at + 1] << 8)
            | ((uint)picture.Pixels[at + 2] << 16)
            | ((uint)picture.Pixels[at + 3] << 24);
    }

    /// <summary>Activates the module over a stack with one paint layer, and puts the brush down.</summary>
    /// <remarks>
    ///     ⚠ <b>Its own rather than <c>PaintUvViewTests</c>' equivalent, because this suite needs the
    ///     <em>module</em>.</b> That one builds its module inside the helper and returns the document;
    ///     the counters this suite reads are the module's, and widening a neighbour's private helper
    ///     for one caller is how a test harness grows a parameter nothing else passes.
    /// </remarks>
    static LayerStackDocument Painting(TexturingFixture fixture, TexturingModule module, string name) {
        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, module);
        fixture.Project.Selection.Set(LayerStackPanelTests.AddStack(fixture, name));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        var document = Assert.IsType<LayerStackDocument>(fixture.Project.Documents.Single());
        var stack = LayerStackDocument.Starter(name) with { BaseWidth = Side, BaseHeight = Side };

        stack.Sets[0].Layers.Add(new() { Id = "rust", Name = "Rust", Kind = LayerKind.Paint });

        document.Document = stack;

        return document;
    }

    static UiElement OpenPaintPane(TexturingFixture fixture) {
        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.PaintCommand));

        var panel = fixture.Shell.Workspace.Open(TexturingModule.PaintPanel);

        Assert.NotNull(panel);

        // ⚠ Laid out before anything is dispatched: a control with no box has no absolute position,
        // so a dispatch before this hits the root and reaches no handler — which looks exactly like a
        // handler that does not work. `PaintUvViewTests` says the same at more length.
        fixture.Shell.Document.Update();

        return panel;
    }

    static ImageView ImageIn(UiElement panel) {
        foreach (var child in panel.Children) {
            if (Find(child) is { } found) {
                return found;
            }
        }

        throw new InvalidOperationException("the paint pane holds no ImageView");

        static ImageView? Find(UiElement element) {
            if (element is ImageView view) {
                return view;
            }

            foreach (var child in element.Children) {
                if (Find(child) is { } found) {
                    return found;
                }
            }

            return null;
        }
    }

    static void Drag(TexturingFixture fixture, ImageView image, Vector2 from, Vector2 to) {
        var start = image.ToScreen(from);
        var end = image.ToScreen(to);

        fixture.Shell.Document.Dispatch(
            new PointerEvent {
                X = start.X,
                Y = start.Y,
                Action = PointerAction.Pressed,
                Button = PointerButton.Primary
            }
        );

        for (var step = 1; step <= 4; step++) {
            var at = Vector2.Lerp(start, end, step / 4f);

            fixture.Shell.Document.Dispatch(new PointerEvent { X = at.X, Y = at.Y, Action = PointerAction.Moved });
        }

        fixture.Shell.Document.Dispatch(
            new PointerEvent { X = end.X, Y = end.Y, Action = PointerAction.Released, Button = PointerButton.Primary }
        );
    }
}
