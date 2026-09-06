// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Texturing.Layers;
using Vixen.Editor.Texturing.Painting;
using Vixen.Editor.Ui;
using Vixen.Ui;
using Vixen.Ui.Controls.Advanced;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>
///     The pointer reaches a texel: doc 48 § D13's 2D UV view, driven the way a person drives it.
/// </summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/852">#852</a>'s last half.</b> The
///         brush, the stroke, the spacing, the dilation, the composite, the undo entry and the
///         <c>.vxpaint</c> all existed and nothing turned a pointer position into a texel, so every
///         one of the fifty-two tests behind them exercised a model no artist could reach.
///     </para>
///     <para>
///         ⚠ <b>Every stroke here is dispatched into the document rather than raised on the
///         control, and that is the difference between this suite and one that proves nothing.</b>
///         <c>UiElement.AddHandler</c> defaults to <c>handledEventsToo: false</c> and
///         <c>ImageView</c> marks every pointer event it sees handled — so a paint handler
///         registered the ordinary way is registered, looks right, and never runs. A test that
///         called a method on the view directly would be green against exactly that.
///         <c>UiDocument.Dispatch</c> is the real route: hit test, capture leg, target, bubble.
///     </para>
/// </remarks>
public class PaintUvViewTests {
    /// <summary>A press, a drag and a release put pixels in the layer's canvas and on the screen.</summary>
    /// <remarks>
    ///     ⚠ <b>Three separate places, because each of them has been the missing one.</b> The canvas
    ///     in memory is what the stroke wrote; the <c>.vxpaint</c> on disk is what the preview reads
    ///     to redraw the map, and a stroke that never reached it would leave the layers pane showing
    ///     the picture from before the drag; and the upload is what the artist is looking at.
    /// </remarks>
    [Fact]
    public void A_drag_in_the_paint_pane_paints_texels() {
        using var fixture = new TexturingFixture(graphics: true);
        var document = OpenPaintable(fixture, "Hull");
        var pane = OpenPaintPane(fixture);
        var image = ImageIn(pane);

        var painted = Drag(fixture, image, new Vector2(16f, 16f), new Vector2(40f, 40f));

        Assert.NotEqual(0u, painted);

        // The canvas beside the stack, named by the edit the first stroke made.
        var layer = Assert.Single(document.Document.Sets[0].Layers, one => one.Kind == LayerKind.Paint);

        Assert.NotEmpty(layer.Paint);

        var file = Path.Combine(Path.GetDirectoryName(document.AssetPath)!, layer.Paint);

        Assert.True(File.Exists(file), "the stroke did not reach a .vxpaint");

        using var stream = File.OpenRead(file);
        var canvas = PaintCanvas.Read(stream);

        Assert.Contains("baseColor", canvas.Channels);
        Assert.NotEqual(0u, canvas.Channel("baseColor").At(16, 16));

        // And the pane is showing it: the last upload carries the same texel.
        var upload = fixture.Graphics!.Uploads[^1];

        Assert.NotEqual(0, upload.Pixels[(((16 * upload.Width) + 16) * 4) + 3]);
    }

    /// <summary>The drag is exactly one undo entry, and undoing it takes the paint off.</summary>
    /// <remarks>
    ///     ⚠ <b>Two entries and not one, which is the honest count.</b> A paint layer that named no
    ///     canvas gets one written down, and that is a change to the <c>.vxlayers</c> rather than to
    ///     the pixels — so the first stroke into a fresh layer is "name the canvas" and then "paint
    ///     stroke", in that order, and one undo takes the stroke off and leaves the name. Doc 48
    ///     § D13's "a stroke is exactly one undo entry" is about the stroke, and this is what makes
    ///     it checkable rather than a claim about a number that happens to be one.
    /// </remarks>
    [Fact]
    public void The_drag_is_one_undo_entry_and_undoing_it_removes_the_paint() {
        using var fixture = new TexturingFixture(graphics: true);
        var document = OpenPaintable(fixture, "Hull");
        var pane = OpenPaintPane(fixture);
        var image = ImageIn(pane);

        Drag(fixture, image, new Vector2(16f, 16f), new Vector2(40f, 40f));

        Assert.Equal("Paint stroke", document.Stack.UndoName.Value);

        var before = document.Stack.Depth.Value;

        // ⚠ The half this test is named for, and it was missing. Every assertion here was about the
        // stack's own bookkeeping, which `CommandStack`'s tests already cover; nothing looked at a
        // texel, so an undo that mended the image in memory and left the `.vxpaint` alone passed —
        // and the layers pane resolves a paint layer by opening that file.
        var layer = Assert.Single(document.Document.Sets[0].Layers, one => one.Kind == LayerKind.Paint);
        var file = Path.Combine(Path.GetDirectoryName(document.AssetPath)!, layer.Paint);

        Assert.True(document.Stack.Undo());
        Assert.Equal(before - 1, document.Stack.Depth.Value);

        using (var undone = File.OpenRead(file)) {
            Assert.Equal(0u, PaintCanvas.Read(undone).Channel("baseColor").At(16, 16));
        }

        // And back again, on disk, so the redo is as real as the undo was.
        Assert.True(document.Stack.Redo());

        using (var redone = File.OpenRead(file)) {
            Assert.NotEqual(0u, PaintCanvas.Read(redone).Channel("baseColor").At(16, 16));
        }

        // The second drag is one entry on its own — a stroke never merges with the one before it.
        Drag(fixture, image, new Vector2(48f, 16f), new Vector2(56f, 24f));

        Assert.Equal(before + 1, document.Stack.Depth.Value);
    }

    /// <summary>⚠ In Select mode the same drag pans the pane and paints nothing.</summary>
    /// <remarks>
    ///     <b>The half that makes the mode mean something.</b> A view that painted whatever the mode
    ///     said would make <c>texturing.toggle-paint</c> decorative, and a view that swallowed the
    ///     drag in both modes would take the pan away — which is how an artist loses the ability to
    ///     look at the part of the atlas they want to paint.
    /// </remarks>
    [Fact]
    public void A_drag_with_the_brush_down_pans_instead_of_painting() {
        using var fixture = new TexturingFixture(graphics: true);
        var document = OpenPaintable(fixture, "Hull");
        var pane = OpenPaintPane(fixture, painting: false);
        var image = ImageIn(pane);
        var pan = image.Pan;

        Drag(fixture, image, new Vector2(16f, 16f), new Vector2(40f, 40f));

        Assert.Equal(0, document.Stack.Depth.Value);
        Assert.NotEqual(pan, image.Pan);
    }

    /// <summary>A press with nothing to paint into says why rather than throwing out of the frame.</summary>
    /// <remarks>
    ///     ⚠ <b>The paint layer is taken away <em>after</em> the pane is showing it, which is what
    ///     makes this a test of pointer-down.</b> A stack that never had one gets the same sentence
    ///     from the pane's own refresh, so a drag would be asserting nothing: the message would be on
    ///     the screen whether or not the press ever reached a handler. Removing it mid-session is
    ///     also a real state — undoing the edit that added the layer does exactly this.
    /// </remarks>
    [Fact]
    public void A_press_with_no_paint_layer_says_so_rather_than_throwing() {
        using var fixture = new TexturingFixture(graphics: true);
        var document = OpenPaintable(fixture, "Hull");
        var pane = OpenPaintPane(fixture);
        var image = ImageIn(pane);

        Assert.DoesNotContain("no paint layer", Status(pane), StringComparison.OrdinalIgnoreCase);

        document.Document.Sets[0].Layers.RemoveAll(one => one.Kind == LayerKind.Paint);

        Drag(fixture, image, new Vector2(16f, 16f), new Vector2(40f, 40f));

        Assert.Equal(0, document.Stack.Depth.Value);
        Assert.Contains("no paint layer", Status(pane), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Each stamp's own rectangle reaches the caller, not the bounding box of the move.</summary>
    /// <remarks>
    ///     ⚠ <b><a href="https://github.com/Rikarin/Vixen/issues/894">#894</a>: the overload that
    ///     hands the rectangles out had no callers at all, so nothing could show whether it
    ///     worked.</b> A move long enough to earn several stamps is the input where a union and a
    ///     list of rectangles differ — the union spans the whole move, and every one of the
    ///     rectangles is a stamp's own footprint, which is what
    ///     <a href="https://github.com/Rikarin/Vixen/issues/871">#871</a> bought.
    /// </remarks>
    [Fact]
    public void A_move_hands_back_one_rectangle_per_stamp() {
        using var fixture = new TexturingFixture();
        PaintImage layer = new(64, 64);
        var session = PaintSession.Begin(
            new(layer, PaintCoverage.Everywhere(64, 64), PaintStackImages.Empty(64, 64), 0, layer),
            PaintBrush.Default with { Radius = 3f, Spacing = 1f },
            0xFFFFFFFFu
        );

        List<PaintRect> dirtied = [];

        session.Move(new Vector2(4f, 4f));

        var union = session.MoveAll([new Vector2(40f, 4f)], dirtied);

        // Six texels of travel per stamp over a 36-texel move: several stamps, and a union that
        // spans all of them.
        Assert.True(dirtied.Count > 1, $"one move earned {dirtied.Count} stamp(s); the input is too short");
        Assert.True(union.Width > dirtied[0].Width, "the union is no wider than one stamp");

        foreach (var rect in dirtied) {
            Assert.True(rect.Width <= union.Width, "a stamp's rectangle is wider than the union of them all");
        }
    }

    /// <summary>⚠ The seed is what the untouched atlas is, and it is not what a resolve would write.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/853">#853</a>'s replacement, with
    ///         a caller at last.</b> <c>PaintComposite</c>'s constructor stopped compositing the
    ///         whole atlas — 1.9 s at 4K — and seeds <c>Result</c> from the picture the view already
    ///         has instead. Nothing called <c>Seed</c> but the constructor and nothing passed
    ///         <c>PaintTarget.Shown</c>, so the mechanism's correctness was unobservable.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The halves are deliberately <em>not</em> empty here.</b> With
    ///         <c>PaintStackImages.Empty</c> the composite of a layer between two transparent halves
    ///         is the layer, so a seed and a resolve agree whatever either does — a test built on
    ///         that input could not fail. An opaque upper half makes the two differ, which is what
    ///         lets the assertion see the seam.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_seeded_atlas_is_the_picture_the_view_had_and_the_resolved_part_is_the_composite() {
        PaintImage layer = new(32, 32);
        PaintImage shown = new(32, 32, 0xFF0000FFu);
        PaintImage below = new(32, 32, 0xFF000000u);
        PaintImage above = new(32, 32, 0x8000FF00u);

        var session = PaintSession.Begin(
            new(layer, PaintCoverage.Everywhere(32, 32), new PaintStackImages(below, above), 0, shown),
            PaintBrush.Default with { Radius = 2f },
            0xFFFFFFFFu
        );

        session.Move(new Vector2(4f, 4f));

        // Untouched: exactly the seed, and not the composite of a blank layer — which with this
        // upper half would be a green-tinted black rather than the red the view was showing.
        Assert.Equal(0xFF0000FFu, session.Composite.Result.At(28, 28));

        // Touched: the composite, which is neither the seed nor the layer.
        var painted = session.Composite.Result.At(4, 4);

        Assert.NotEqual(0xFF0000FFu, painted);
        Assert.Equal(PaintComposite.Over(PaintComposite.Over(below.At(4, 4), layer.At(4, 4)), above.At(4, 4)), painted);
    }

    /// <summary>A 2D view converts a brush radius the other way: to screen pixels, for the cursor.</summary>
    /// <remarks>
    ///     ⚠ <b><c>PaintSession</c>'s second obligation is the identity here, and that is a result
    ///     rather than a shortcut.</b> <c>PaintBrush.Radius</c> is authored in texels of the atlas,
    ///     so a 2D view has nothing to convert on the way in — a 3D view does, because there the
    ///     artist is holding a screen radius and the hit triangle's texel density is what relates
    ///     the two. What this view owes is the inverse, so the ring under the pointer is the size of
    ///     the stamp that would land.
    /// </remarks>
    [Fact]
    public void The_cursor_ring_is_the_brush_radius_at_the_panes_zoom() {
        using var fixture = new TexturingFixture(graphics: true);

        OpenPaintable(fixture, "Hull");

        var pane = OpenPaintPane(fixture);
        var image = ImageIn(pane);

        image.Zoom = 4f;
        fixture.Shell.Document.Update();

        // The overlay's ring is in texels, so its extent is the brush and its screen size is the
        // brush times the zoom. Both are asserted: a ring drawn in screen pixels would keep its
        // extent when the zoom moved, which is the lie that only shows once an artist zooms in.
        var at = image.ToImage(image.AbsoluteLeft + 30f, image.AbsoluteTop + 30f);

        Move(fixture, image.AbsoluteLeft + 30f, image.AbsoluteTop + 30f);

        var reach = 0f;

        foreach (var segment in image.Overlay) {
            reach = MathF.Max(reach, (segment.From - at).Length());
        }

        Assert.Equal(PaintBrush.Default.Radius, reach, 1);
        Assert.Equal(PaintBrush.Default.Radius * 4f, (image.ToScreen(at + new Vector2(reach, 0f)) - image.ToScreen(at)).Length(), 1);
    }

    /// <summary>The paint panel is registered, and unloading takes it and its View entry out.</summary>
    /// <remarks>
    ///     ⚠ <b>Here rather than in <c>TexturingModuleTests</c>' roll call, which names two panels and
    ///     there are now three.</b> That file's own remark says why a roll call has to name rather
    ///     than count — <a href="https://github.com/Rikarin/Vixen/issues/806">#806</a> was a second
    ///     document registered nowhere, and a count grown by one says nothing about which one. It
    ///     belongs in the roll call and the roll call is another slice's file, so the property is
    ///     asserted here and the fold-in is left to the merge.
    ///     <para>
    ///         The <em>command</em> half is the one that is easy to leave behind:
    ///         <c>RegisterPanel</c> makes two registrations, and a View-menu line that toggles a panel
    ///         nobody can open is a lambda holding the plugin's assembly for the session.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_paint_panel_is_registered_and_unloading_takes_it_out() {
        using var fixture = new TexturingFixture();

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());

        Assert.Contains(fixture.Shell.Workspace.Panels, panel => panel.Id == TexturingModule.PaintPanel);
        Assert.NotNull(fixture.Shell.Commands[TexturingModule.PaintCommand]);

        Assert.True(fixture.Host.Unload(TexturingModule.ModuleId));

        Assert.DoesNotContain(fixture.Shell.Workspace.Panels, panel => panel.Id == TexturingModule.PaintPanel);
        Assert.Null(fixture.Shell.Commands[EditorShell.PanelCommand(TexturingModule.PaintPanel)]);
        Assert.Null(fixture.Shell.Commands[TexturingModule.PaintCommand]);
    }

    /// <summary>UV islands are drawn in texels, so a pan and a zoom do not move them off the atlas.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 48 § D13 calls the 2D view "the only way to fix the places the 3D view cannot
    ///         reach", and it is the islands that make that true</b> — a pane showing an atlas with no
    ///         outlines on it cannot say which of its texels are surface.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="PaintUvView.ShowIslands" /> has no production caller yet and that is
    ///         said rather than hidden.</b> A stack names no mesh — <c>LayerStackPreview</c> refuses a
    ///         mesh-map layer with exactly that sentence — so nothing in this plugin has UV triangles
    ///         to hand it. The binding that would is
    ///         <a href="https://github.com/Rikarin/Vixen/issues/920">#920</a>, and it is the same
    ///         thing that turns <c>PaintCoverage.Everywhere</c> into a real coverage map. This test is
    ///         what stops it being a finished thing nobody has run.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_islands_are_drawn_in_texels_of_the_atlas() {
        using var fixture = new TexturingFixture();
        var host = fixture.Shell.Document.Root.Add<UiElement>();
        // ⚠ In Paint mode, because the ring is not drawn in Select mode and that is deliberate: with
        // the brush up a drag pans, and a brush cursor over a pane that will not paint is a control
        // lying about what the next gesture does.
        PaintUvView view = new(host, new PaintTool { Mode = PaintToolMode.Paint });

        view.Show(0ul, 64, 64, "");
        fixture.Shell.Document.Update();

        var image = view.Image;

        // One triangle over the top-left quarter of the unit square. At 64 texels that is (0,0),
        // (32,0), (0,32) — three segments, and every endpoint inside the atlas.
        view.ShowIslands([new Vector2(0f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 0.5f)]);

        Assert.Equal(3, image.Overlay.Count);
        Assert.Contains(image.Overlay, segment => segment.To == new Vector2(32f, 0f));
        Assert.Contains(image.Overlay, segment => segment.To == new Vector2(0f, 32f));

        // ⚠ And the ring is appended after them rather than replacing them: one list holds both, and
        // a cursor that cleared it would take the islands off on the first pointer move. Called
        // directly here — that the pointer reaches it at all is
        // `The_cursor_ring_is_the_brush_radius_at_the_panes_zoom`, through the document's own route.
        view.ShowCursor(new Vector2(20f, 20f));
        view.ShowCursor(new Vector2(24f, 24f));

        Assert.True(image.Overlay.Count > 3, "the cursor ring replaced the islands instead of following them");
        Assert.Contains(image.Overlay, segment => segment.To == new Vector2(32f, 0f));
    }

    // ── The harness ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Opens a stack with a paint layer in it, small enough for a test to be quick.</summary>
    /// <remarks>
    ///     ⚠ <b>64 square rather than the 1024 a starter stack declares.</b> A paint canvas is four
    ///     bytes a texel per channel and every upload here copies one, so the default would move four
    ///     megabytes per pointer event for no assertion's benefit.
    /// </remarks>
    static LayerStackDocument OpenPaintable(TexturingFixture fixture, string name, bool paintable = true) {
        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(LayerStackPanelTests.AddStack(fixture, name));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        var document = Assert.IsType<LayerStackDocument>(fixture.Project.Documents.Single());
        var stack = LayerStackDocument.Starter(name) with { BaseWidth = 64, BaseHeight = 64 };

        if (paintable) {
            stack.Sets[0].Layers.Add(new() { Id = "rust", Name = "Rust", Kind = LayerKind.Paint });
        }

        document.Document = stack;

        return document;
    }

    /// <summary>Opens the paint pane, lays it out, and puts the brush down.</summary>
    static UiElement OpenPaintPane(TexturingFixture fixture, bool painting = true) {
        if (painting) {
            Assert.True(fixture.Shell.Commands.Execute(TexturingModule.PaintCommand));
        }

        var panel = fixture.Shell.Workspace.Open(TexturingModule.PaintPanel);

        Assert.NotNull(panel);

        // ⚠ Laid out before anything is dispatched. Every coordinate below is in document space and
        // a control with no box has no absolute position, so a dispatch before this would hit the
        // root and reach no handler at all — which looks exactly like a handler that does not work.
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

    static string Status(UiElement panel) {
        var text = "";

        Walk(panel);

        return text;

        void Walk(UiElement element) {
            if (string.Equals(element.Tag, "paint-uv-status", StringComparison.Ordinal)) {
                text = element.Text ?? "";
            }

            foreach (var child in element.Children) {
                Walk(child);
            }
        }
    }

    /// <summary>A press, some moves and a release, in texels of the atlas.</summary>
    /// <returns>The texel the drag started on, after the drag.</returns>
    static uint Drag(TexturingFixture fixture, ImageView image, Vector2 from, Vector2 to) {
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

        // Several moves rather than one, because a drag is reported per frame and the carried
        // spacing distance between them is what `BrushStroke` exists to get right.
        for (var step = 1; step <= 4; step++) {
            var at = Vector2.Lerp(start, end, step / 4f);

            Move(fixture, at.X, at.Y);
        }

        fixture.Shell.Document.Dispatch(
            new PointerEvent { X = end.X, Y = end.Y, Action = PointerAction.Released, Button = PointerButton.Primary }
        );

        var uploads = fixture.Graphics?.Uploads;

        if (uploads is null || uploads.Count == 0) {
            return 0u;
        }

        var last = uploads[^1];
        var index = (((int)from.Y * last.Width) + (int)from.X) * 4;

        return last.Pixels[index]
            | ((uint)last.Pixels[index + 1] << 8)
            | ((uint)last.Pixels[index + 2] << 16)
            | ((uint)last.Pixels[index + 3] << 24);
    }

    static void Move(TexturingFixture fixture, float x, float y) =>
        fixture.Shell.Document.Dispatch(new PointerEvent { X = x, Y = y, Action = PointerAction.Moved });
}
