// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Vixen.Editor.Texturing.Layers;
using Vixen.Editor.Texturing.Painting;
using Vixen.Editor.Ui;
using Vixen.Input;
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

    /// <summary>⚠ A pointer move uploads its own rectangle, not the atlas it landed in.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/912">#912</a>, and it is the last
    ///         step of an argument the three layers below it had already won.</b> The stroke hands
    ///         back one rectangle per stamp, the composite resolves per rectangle — and the pane then
    ///         handed the whole picture to <c>IEditorGraphics.Upload</c> on every pointer move,
    ///         because that was the only shape there was. At 4K a 96-texel disc cost 67 MB, a texture
    ///         and a descriptor-set write, per frame of the drag.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The assertion is bytes and not calls, because a call count cannot see the
    ///         defect.</b> One <c>Update</c> per move handing over the whole atlas would satisfy any
    ///         count of them; what #912 is about is that a drag now moves less than a single
    ///         re-upload used to, so the sum of what was patched is compared against one picture.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Measured with the pointer still down.</b> Pointer-up saves the canvas and
    ///         refreshes the pane, which is one honest whole-picture upload per stroke and not per
    ///         move — folding it in would make the count a claim about the release instead.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_pointer_move_uploads_its_own_rectangle_rather_than_the_atlas() {
        const int Side = 512;

        using var fixture = new TexturingFixture(graphics: true);

        OpenPaintable(fixture, "Hull", side: Side);

        var image = ImageIn(OpenPaintPane(fixture));
        var graphics = fixture.Graphics!;
        var start = image.ToScreen(new Vector2(64f, 64f));
        var end = image.ToScreen(new Vector2(96f, 96f));
        var uploads = graphics.Uploads.Count;

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

            Move(fixture, at.X, at.Y);
        }

        Assert.Equal(uploads, graphics.Uploads.Count);
        Assert.NotEmpty(graphics.Updates);

        var atlas = (long)Side * Side * 4;
        var patched = graphics.Updates.Sum(one => (long)one.Pixels.Length);

        Assert.True(
            patched < atlas,
            $"{patched} bytes patched over the whole drag against {atlas} for one upload of the atlas. "
            + "The move is paying for the picture rather than for the stamp."
        );

        // ⚠ And the bytes are the rectangle's own rows. A caller that handed over a window onto the
        // atlas would be refused by the host — a length check is all it can do — and the pane would
        // silently stop redrawing, so this is asserted here rather than left to the refusal.
        foreach (var patch in graphics.Updates) {
            Assert.Equal(patch.Width * patch.Height * 4, patch.Pixels.Length);
            Assert.True(patch.Width < Side && patch.Height < Side, "a stamp's rectangle is the whole atlas");
        }

        // The paint really is in what was sent: the texel the press landed on, read out of the
        // rectangle that covers it, at that rectangle's own coordinates.
        var covering = graphics.Updates.First(
            one => one.X <= 64 && 64 < one.X + one.Width && one.Y <= 64 && 64 < one.Y + one.Height
        );

        Assert.NotEqual(0, covering.Pixels[((((64 - covering.Y) * covering.Width) + (64 - covering.X)) * 4) + 3]);

        fixture.Shell.Document.Dispatch(
            new PointerEvent { X = end.X, Y = end.Y, Action = PointerAction.Released, Button = PointerButton.Primary }
        );
    }

    /// <summary>⚠ A host that refuses the rectangle gets the whole picture, not a stale pane.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The instrument check for the test above.</b> <c>IEditorGraphics.Update</c> answers
    ///         false for a host with no surface, for an image it did not make, and for a rectangle
    ///         outside one — and a caller that read the refusal as "done" would leave the pane showing
    ///         the picture from before the stroke, which reads exactly like a brush that does not
    ///         paint. Without this the fallback is a branch no test enters.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Asserted with the pointer still down, and the first version of this test was not
    ///         — it passed against a <c>Redraw</c> with the fallback deleted.</b> Pointer-up saves the
    ///         canvas and refreshes the pane, which uploads the layer's own pixels with the stroke in
    ///         them; a test that looked at the last upload of a finished drag was therefore reading
    ///         the refresh and would have been green whatever happened during the moves.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_refused_rectangle_falls_back_to_uploading_the_picture() {
        using var fixture = new TexturingFixture(graphics: true);

        OpenPaintable(fixture, "Hull");

        var image = ImageIn(OpenPaintPane(fixture));
        var graphics = fixture.Graphics!;

        graphics.Patches = false;

        var start = image.ToScreen(new Vector2(16f, 16f));
        var uploads = graphics.Uploads.Count;

        fixture.Shell.Document.Dispatch(
            new PointerEvent {
                X = start.X,
                Y = start.Y,
                Action = PointerAction.Pressed,
                Button = PointerButton.Primary
            }
        );

        Move(fixture, start.X + 4f, start.Y + 4f);

        Assert.Empty(graphics.Updates);

        Assert.True(
            graphics.Uploads.Count > uploads,
            "the host refused every rectangle and the pane uploaded nothing, so it is showing the picture "
            + "from before the press."
        );

        var last = graphics.Uploads[^1];

        Assert.NotEqual(0, last.Pixels[(((16 * last.Width) + 16) * 4) + 3]);

        fixture.Shell.Document.Dispatch(
            new PointerEvent {
                X = start.X + 4f,
                Y = start.Y + 4f,
                Action = PointerAction.Released,
                Button = PointerButton.Primary
            }
        );
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

    /// <summary>A masked brush's cursor is its square, turned by the angle it will stamp at.</summary>
    /// <remarks>
    ///     ⚠ <b>An angle an artist cannot see is an angle they cannot set —
    ///     <a href="https://github.com/Rikarin/Vixen/issues/1083">#1083</a>.</b> A masked stamp covers
    ///     its square and not the disc inside it, so a ring over one is the same picture at every
    ///     angle: the control moves, the readout changes, and the pane shows nothing. The round
    ///     brush's ring in the second half is the instrument — it must <em>not</em> move — so this
    ///     cannot pass by drawing anything that happens to change.
    /// </remarks>
    [Fact]
    public void A_masked_brushs_cursor_is_a_turned_square_and_a_round_ones_is_not() {
        using var fixture = new TexturingFixture();

        var host = fixture.Shell.Document.Root.Add<UiElement>();
        PaintTool tool = new() { Mode = PaintToolMode.Paint };
        PaintUvView view = new(host, tool);

        view.Show(0ul, 64, 64, "");
        fixture.Shell.Document.Update();

        tool.SetRadius(8f);
        tool.SetAlpha(PaintAlphas.Square);
        view.ShowCursor(new Vector2(32f, 32f));

        var upright = view.Image.Overlay.Select(segment => segment.To).ToList();

        Assert.Equal(4, upright.Count);

        // The corners of an eight-texel square: √2 × 8 from the centre, which a ring never reaches.
        Assert.All(upright, corner => Assert.Equal(8f * MathF.Sqrt(2f), (corner - new Vector2(32f, 32f)).Length(), 3));

        tool.SetAngle(45f);
        view.ShowCursor(new Vector2(32f, 32f));

        var turned = view.Image.Overlay.Select(segment => segment.To).ToList();

        Assert.Contains(turned, corner => (corner - new Vector2(32f, 43.3f)).Length() < 0.1f);
        Assert.DoesNotContain(turned, corner => upright.Any(was => (corner - was).Length() < 0.5f));

        // ⚠ The instrument: a round brush's ring is the same picture at every angle, because a disc
        // turned is a disc. If this moved, what moved above was not the stamp's rotation.
        tool.SetAlpha(PaintAlphas.Round);
        view.ShowCursor(new Vector2(32f, 32f));

        var ring = view.Image.Overlay.Select(segment => segment.To).ToList();

        tool.SetAngle(137f);
        view.ShowCursor(new Vector2(32f, 32f));

        Assert.Equal(ring, view.Image.Overlay.Select(segment => segment.To));
    }

    /// <summary>Clicked points make a curve, and the curve paints where its chord does not.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/1084">#1084</a>, and the assertion
    ///         is the whole of what that issue is about.</b> <c>BrushStroke.MoveTo</c> interpolates
    ///         straight, so a curve fed to it as its two endpoints paints its <em>chord</em> — which
    ///         is a stroke that looks right in the pane and lands somewhere else. The straight line
    ///         through the same two ends is therefore the instrument: it must miss the texel the
    ///         curve hits, or this test would pass against a path tool that painted chords.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And one undo entry for the whole path however many positions it was sampled
    ///         at</b>, which is doc 48 § M9's exit criterion applied to the gesture that most looks
    ///         like several strokes.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_placed_path_paints_its_curve_rather_than_its_chord_in_one_entry() {
        using var fixture = new TexturingFixture();

        var host = fixture.Shell.Document.Root.Add<UiElement>();
        PaintTool tool = new() { Mode = PaintToolMode.Path };
        PaintUvView view = new(host, tool);

        PaintImage layer = new(128, 128);
        List<IEditorCommand> entries = [];

        tool.SetRadius(3f);
        view.Target = () => new(layer, PaintCoverage.Everywhere(128, 128), new EmptyStack(128), Gutter: 0);
        view.Finished = entries.Add;
        view.Show(0ul, 128, 128, "");
        fixture.Shell.Document.Update();

        // A corner: the curve through the middle point bows well away from the chord between the
        // two ends, which is the only arrangement where the two strokes can be told apart.
        Place(fixture, view.Image, new Vector2(20f, 100f));
        Place(fixture, view.Image, new Vector2(64f, 24f));
        Place(fixture, view.Image, new Vector2(108f, 100f));

        Assert.Contains("3 point(s)", view.Status, StringComparison.Ordinal);

        Commit(fixture, view.Image, new Vector2(108f, 100f));

        var command = Assert.Single(entries);

        Assert.True(Painted(layer, new Vector2(20f, 100f)), "the path painted nothing at all.");
        Assert.True(
            Painted(layer, new Vector2(64f, 25f)),
            "the path missed its own apex, so it painted something other than the curve."
        );

        // ⚠ The instrument: the chord between the same two ends runs sixty texels below the apex, so
        // a path tool that interpolated straight would leave it untouched.
        PaintImage chord = new(128, 128);
        PaintStroke straight = new(chord, PaintCoverage.Everywhere(128, 128), tool.Brush, 0xFF0000FFu, gutter: 0);

        straight.MoveTo(new(20f, 100f));
        straight.MoveTo(new(108f, 100f));

        Assert.False(
            Painted(chord, new Vector2(64f, 25f)),
            "the straight line reached the apex too, so this test cannot tell a curve from a chord."
        );

        // And one entry undoes the whole thing.
        command.Undo(null!);

        Assert.False(Painted(layer, new Vector2(64f, 25f)));
        Assert.False(Painted(layer, new Vector2(20f, 100f)), "one undo left the start of the path behind.");

        // The preview's ticks are gone — the ring the cursor draws is what is left.
        Assert.DoesNotContain(view.Image.Overlay, segment => segment.To == new Vector2(23f, 100f));
    }

    /// <summary>Escape drops a half-placed path, and Enter lays one.</summary>
    /// <remarks>
    ///     ⚠ <b>The keys are handled only while there is a path.</b> Enter, Escape and Backspace all
    ///     belong to something else in an editor, so a pane that took them whenever it had the focus
    ///     would break a dialog — which is why the second half asserts an unhandled Escape.
    /// </remarks>
    [Fact]
    public void Enter_lays_the_path_and_escape_drops_it() {
        using var fixture = new TexturingFixture();

        var host = fixture.Shell.Document.Root.Add<UiElement>();
        PaintTool tool = new() { Mode = PaintToolMode.Path };
        PaintUvView view = new(host, tool);

        PaintImage layer = new(64, 64);
        List<IEditorCommand> entries = [];

        tool.SetRadius(2f);
        view.Target = () => new(layer, PaintCoverage.Everywhere(64, 64), new EmptyStack(64), Gutter: 0);
        view.Finished = entries.Add;
        view.Show(0ul, 64, 64, "");
        fixture.Shell.Document.Update();

        // Nothing placed: the key is left alone, so whatever else wanted it still gets it.
        var idle = new KeyEvent { Key = InputKey.Escape, Action = KeyAction.Pressed };

        fixture.Shell.Document.Dispatch(idle);

        Assert.False(idle.Handled, "an empty pane swallowed Escape, which belongs to a dialog.");

        Place(fixture, view.Image, new Vector2(10f, 10f));
        Place(fixture, view.Image, new Vector2(50f, 50f));

        fixture.Shell.Document.Dispatch(new KeyEvent { Key = InputKey.Escape, Action = KeyAction.Pressed });

        Assert.Empty(entries);
        Assert.DoesNotContain(view.Image.Overlay, segment => segment.To == new Vector2(13f, 10f));

        Place(fixture, view.Image, new Vector2(10f, 10f));
        Place(fixture, view.Image, new Vector2(50f, 50f));

        fixture.Shell.Document.Dispatch(new KeyEvent { Key = InputKey.Enter, Action = KeyAction.Pressed });

        Assert.Single(entries);
        Assert.True(Painted(layer, new Vector2(30f, 30f)), "Enter did not lay the path.");
    }

    /// <summary>A placed point can be picked up and moved, and the curve follows it.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/1084">#1084</a>'s remainder: the
    ///         pen gesture landed and a placed point could not be corrected.</b> A path an artist
    ///         cannot correct is a path they will not use — every point after a mistake has to be
    ///         taken off with Backspace and placed again.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The count is the assertion that says "moved" rather than "added".</b> Without a
    ///         hit test a press on a placed point is just another press, so the path would grow a
    ///         fourth point in the same texel as the second — which paints an almost identical curve
    ///         and would satisfy any assertion about where the paint went.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_placed_point_can_be_dragged_and_the_curve_follows_it() {
        using var fixture = new TexturingFixture();
        var view = Pen(fixture, out var layer, out var entries);

        Place(fixture, view.Image, new Vector2(20f, 100f));
        Place(fixture, view.Image, new Vector2(64f, 100f));
        Place(fixture, view.Image, new Vector2(108f, 100f));

        Assert.Contains("3 point(s)", view.Status, StringComparison.Ordinal);

        Pull(fixture, view.Image, new Vector2(64f, 100f), new Vector2(64f, 30f));

        Assert.Contains(
            "3 point(s)",
            view.Status,
            StringComparison.Ordinal
        );

        Commit(fixture, view.Image, new Vector2(108f, 100f));

        Assert.Single(entries);

        Assert.True(
            Painted(layer, new Vector2(64f, 31f)),
            "the curve does not pass through where the middle point was dragged to."
        );

        // ⚠ And it no longer passes through where that point was, which is what says the point moved
        // rather than the path gaining a second apex.
        Assert.False(
            Painted(layer, new Vector2(64f, 100f)),
            "the curve still passes through where the middle point started."
        );
    }

    /// <summary>A click on the curve inserts a point there rather than at the end of the path.</summary>
    /// <remarks>
    ///     ⚠ <b>Where the point goes in the <em>order</em> is the whole assertion, and a count cannot
    ///     see it.</b> Appending gives three points too; what it gives is a path that runs to the far
    ///     end and doubles back, so the curve stays along the line between the first two and the new
    ///     point is a tail. The two are told apart by asking whether the paint is still on that line.
    /// </remarks>
    [Fact]
    public void A_click_on_the_curve_inserts_a_point_there_rather_than_at_the_end() {
        using var fixture = new TexturingFixture();
        var view = Pen(fixture, out var layer, out var entries);

        Place(fixture, view.Image, new Vector2(20f, 64f));
        Place(fixture, view.Image, new Vector2(108f, 64f));

        Assert.Contains("2 point(s)", view.Status, StringComparison.Ordinal);

        // On the curve, a long way from either end — and the same press drags the point it inserted,
        // which is one gesture rather than two.
        Pull(fixture, view.Image, new Vector2(64f, 64f), new Vector2(64f, 20f));

        Assert.Contains("3 point(s)", view.Status, StringComparison.Ordinal);

        Commit(fixture, view.Image, new Vector2(108f, 64f));

        Assert.Single(entries);
        Assert.True(Painted(layer, new Vector2(64f, 21f)), "the curve does not reach the inserted point.");

        // ⚠ The discriminator. A point appended instead of inserted leaves the path running
        // (20,64) → (108,64) → (64,20), whose first segment paints straight along y = 64.
        Assert.False(
            Painted(layer, new Vector2(64f, 64f)),
            "the curve still runs straight between the first two points, so the third was appended "
            + "rather than inserted between them."
        );
    }

    /// <summary>Delete takes the point under the pointer; Backspace goes on taking the last.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Two keys because they are two verbs, and a count cannot tell them apart.</b>
    ///         Three points becoming two is true whichever point went, so each half is asserted by
    ///         laying the curve that is left and asking which of the three it still passes through.
    ///         The apex is the middle point and the one Backspace would never take, which is what
    ///         makes this fixture able to distinguish them at all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the case a pane gets wrong by being helpful: Delete over nothing is left
    ///         alone.</b> A pane that swallowed Delete whenever it held a path would take it off
    ///         whatever else in the editor wanted it, which is the judgement <c>Keyed</c> already
    ///         makes for Enter and Escape with no path placed.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Delete_takes_the_point_under_the_pointer_and_backspace_takes_the_last() {
        using var fixture = new TexturingFixture();
        var view = Pen(fixture, out var layer, out _);

        Place(fixture, view.Image, new Vector2(20f, 100f));
        Place(fixture, view.Image, new Vector2(64f, 30f));
        Place(fixture, view.Image, new Vector2(108f, 100f));

        // Over nothing: the key routes on, and the path keeps all three.
        Hover(fixture, view.Image, new Vector2(10f, 10f));

        var idle = new KeyEvent { Key = InputKey.Delete, Action = KeyAction.Pressed };

        fixture.Shell.Document.Dispatch(idle);

        Assert.False(idle.Handled, "the pane swallowed Delete with the pointer over no point of the path.");
        Assert.Contains("3 point(s)", view.Status, StringComparison.Ordinal);

        // Over the apex: that one goes, and it is the one in the middle.
        Hover(fixture, view.Image, new Vector2(64f, 30f));
        fixture.Shell.Document.Dispatch(new KeyEvent { Key = InputKey.Delete, Action = KeyAction.Pressed });

        Assert.Contains("2 point(s)", view.Status, StringComparison.Ordinal);

        Commit(fixture, view.Image, new Vector2(108f, 100f));

        Assert.True(Painted(layer, new Vector2(20f, 100f)), "the first point is gone as well.");
        Assert.True(Painted(layer, new Vector2(108f, 100f)), "the last point is gone, so Delete took that one.");

        Assert.False(
            Painted(layer, new Vector2(64f, 31f)),
            "the curve still reaches the apex, so Delete took a point that was not under the pointer."
        );

        layer.Fill(0u);

        // And Backspace, on a path placed the same way, takes the other end.
        Place(fixture, view.Image, new Vector2(20f, 100f));
        Place(fixture, view.Image, new Vector2(64f, 30f));
        Place(fixture, view.Image, new Vector2(108f, 100f));
        fixture.Shell.Document.Dispatch(new KeyEvent { Key = InputKey.Backspace, Action = KeyAction.Pressed });

        Assert.Contains("2 point(s)", view.Status, StringComparison.Ordinal);

        Commit(fixture, view.Image, new Vector2(64f, 30f));

        Assert.True(Painted(layer, new Vector2(64f, 31f)), "Backspace took the apex rather than the last point.");

        Assert.False(
            Painted(layer, new Vector2(108f, 100f)),
            "the curve still reaches the last point, so Backspace took something else."
        );
    }

    /// <summary>A pen view over a 128² layer, with the path mode on.</summary>
    /// <param name="fixture">The host.</param>
    /// <param name="layer">The canvas a laid path paints into.</param>
    /// <param name="entries">Where a laid path's undo entry goes.</param>
    /// <returns>The view.</returns>
    static PaintUvView Pen(TexturingFixture fixture, out PaintImage layer, out List<IEditorCommand> entries) {
        var host = fixture.Shell.Document.Root.Add<UiElement>();
        PaintTool tool = new() { Mode = PaintToolMode.Path };
        PaintUvView view = new(host, tool);

        PaintImage canvas = new(128, 128);
        List<IEditorCommand> laid = [];

        tool.SetRadius(3f);
        view.Target = () => new(canvas, PaintCoverage.Everywhere(128, 128), new EmptyStack(128), Gutter: 0);
        view.Finished = laid.Add;
        view.Show(0ul, 128, 128, "");
        fixture.Shell.Document.Update();

        layer = canvas;
        entries = laid;

        return view;
    }

    /// <summary>Moves the pointer over a texel without pressing anything.</summary>
    /// <param name="fixture">The host.</param>
    /// <param name="image">The pane's viewer.</param>
    /// <param name="texel">Where, in texels.</param>
    static void Hover(TexturingFixture fixture, ImageView image, Vector2 texel) {
        var at = image.ToScreen(texel);

        fixture.Shell.Document.Dispatch(new PointerEvent { X = at.X, Y = at.Y, Action = PointerAction.Moved });
    }

    /// <summary>Presses on a texel, drags to another and releases.</summary>
    /// <param name="fixture">The host.</param>
    /// <param name="image">The pane's viewer.</param>
    /// <param name="from">Where the press is, in texels.</param>
    /// <param name="to">Where the release is.</param>
    /// <remarks>
    ///     ⚠ Its own rather than <c>Drag</c>, which samples the pane's last upload and needs a
    ///     fixture with graphics published; a pen test has no picture and asserts against the
    ///     canvas.
    /// </remarks>
    static void Pull(TexturingFixture fixture, ImageView image, Vector2 from, Vector2 to) {
        var start = image.ToScreen(from);
        var end = image.ToScreen(to);

        fixture.Shell.Document.Dispatch(
            new PointerEvent { X = start.X, Y = start.Y, Action = PointerAction.Pressed, Button = PointerButton.Primary }
        );

        fixture.Shell.Document.Dispatch(new PointerEvent { X = end.X, Y = end.Y, Action = PointerAction.Moved });

        fixture.Shell.Document.Dispatch(
            new PointerEvent { X = end.X, Y = end.Y, Action = PointerAction.Released, Button = PointerButton.Primary }
        );
    }

    static void Place(TexturingFixture fixture, ImageView image, Vector2 texel) {
        var at = image.ToScreen(texel);

        fixture.Shell.Document.Dispatch(
            new PointerEvent { X = at.X, Y = at.Y, Action = PointerAction.Pressed, Button = PointerButton.Primary }
        );
    }

    static void Commit(TexturingFixture fixture, ImageView image, Vector2 texel) {
        var at = image.ToScreen(texel);

        fixture.Shell.Document.Dispatch(
            new PointerEvent { X = at.X, Y = at.Y, Action = PointerAction.Pressed, Button = PointerButton.Secondary }
        );
    }

    static bool Painted(PaintImage image, Vector2 texel) => image.At((int)texel.X, (int)texel.Y) >> 24 != 0u;

    /// <summary>A stack with nothing under or over the layer, which is what a pane test wants.</summary>
    sealed class EmptyStack(int size) : IPaintStack {
        public PaintImage Evaluate(PaintStackSlice slice) => new(size, size);
    }

    // ── The harness ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Opens a stack with a paint layer in it, small enough for a test to be quick.</summary>
    /// <remarks>
    ///     ⚠ <b>64 square rather than the 1024 a starter stack declares.</b> A paint canvas is four
    ///     bytes a texel per channel and every upload here copies one, so the default would move four
    ///     megabytes per pointer event for no assertion's benefit.
    ///     <para>
    ///         ⚠ <b>Except where the atlas is the subject.</b> A brush is 32 texels across, so at 64
    ///         square a stamp's footprint <em>is</em> the whole picture and "the move uploads its own
    ///         rectangle" would be true of a pane that uploaded everything. That test asks for 512.
    ///     </para>
    /// </remarks>
    static LayerStackDocument OpenPaintable(
        TexturingFixture fixture,
        string name,
        bool paintable = true,
        int side = 64
    ) {
        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(LayerStackPanelTests.AddStack(fixture, name));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        var document = Assert.IsType<LayerStackDocument>(fixture.Project.Documents.Single());
        var stack = LayerStackDocument.Starter(name) with { BaseWidth = side, BaseHeight = side };

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

    /// <summary>⚠ Shift-click lays a straight stroke from where the last one ended.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 48 § D13's "curve/path strokes", in the half that has a gesture.</b> The
    ///         assertion is the <em>middle</em> of the segment and not either end, because both ends
    ///         are painted by a plain click too — a pane that ignored the modifier entirely would
    ///         pass any assertion made where the artist clicked. The midpoint is 148 texels from
    ///         both, which is four brush radii.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the plain click in the middle of it is the instrument.</b> It moves the
    ///         anchor and paints, so the shift-click after it is drawing a line the previous click
    ///         did not, and the midpoint being clean between the two is what says the line came from
    ///         the modifier rather than from either stroke's own footprint.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_shift_click_lays_a_line_from_where_the_last_stroke_ended() {
        const int Side = 512;

        using var fixture = new TexturingFixture(graphics: true);

        OpenPaintable(fixture, "Hull", side: Side);

        var image = ImageIn(OpenPaintPane(fixture));

        // A short drag, so the pane has an anchor: the texel the release left it on.
        Drag(fixture, image, new Vector2(100f, 100f), new Vector2(104f, 104f));

        // A plain click at the far end. It paints there and moves the anchor, and it must leave the
        // ground between the two alone.
        Click(fixture, image, new Vector2(400f, 400f), ModifierKeys.None);

        Assert.NotEqual(0u, Sample(fixture, 400, 400) >> 24);
        Assert.Equal(0u, Sample(fixture, 252, 252) >> 24);

        Click(fixture, image, new Vector2(104f, 104f), ModifierKeys.Shift);

        Assert.NotEqual(0u, Sample(fixture, 252, 252) >> 24);
    }

    /// <summary>A press and a release at one point, with modifiers, in texels of the atlas.</summary>
    /// <param name="fixture">The shell.</param>
    /// <param name="image">The pane's viewer, for the texel-to-screen conversion.</param>
    /// <param name="at">Where, in texels.</param>
    /// <param name="modifiers">What is held down.</param>
    static void Click(TexturingFixture fixture, ImageView image, Vector2 at, ModifierKeys modifiers) {
        var point = image.ToScreen(at);

        fixture.Shell.Document.Dispatch(
            new PointerEvent {
                X = point.X,
                Y = point.Y,
                Action = PointerAction.Pressed,
                Button = PointerButton.Primary,
                Modifiers = modifiers
            }
        );

        fixture.Shell.Document.Dispatch(
            new PointerEvent {
                X = point.X,
                Y = point.Y,
                Action = PointerAction.Released,
                Button = PointerButton.Primary,
                Modifiers = modifiers
            }
        );
    }

    /// <summary>One texel of the picture the pane last uploaded, packed <c>0xAABBGGRR</c>.</summary>
    /// <param name="fixture">The shell.</param>
    /// <param name="x">Which column.</param>
    /// <param name="y">Which row.</param>
    /// <returns>The texel.</returns>
    static uint Sample(TexturingFixture fixture, int x, int y) {
        var uploads = fixture.Graphics!.Uploads;

        Assert.NotEmpty(uploads);

        var last = uploads[^1];
        var index = (((y * last.Width) + x) * 4);

        return last.Pixels[index]
            | ((uint)last.Pixels[index + 1] << 8)
            | ((uint)last.Pixels[index + 2] << 16)
            | ((uint)last.Pixels[index + 3] << 24);
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
