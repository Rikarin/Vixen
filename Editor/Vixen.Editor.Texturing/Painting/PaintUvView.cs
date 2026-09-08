// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.Texturing.Painting;

/// <summary>
///     Doc 48 § D13's second front end: the atlas at zoom, the islands over it, and a pointer that
///     lands on a texel.
/// </summary>
/// <remarks>
///     <para>
///         <b>The first surface in this tree that paints, and the three obligations it discharges are
///         <see cref="PaintSession" />'s.</b> Until this existed the whole paint model — brush,
///         stroke, spacing, dilation, the cached composite, the undo entry, the <c>.vxpaint</c> —
///         was reachable only from xunit, which is
///         <a href="https://github.com/Rikarin/Vixen/issues/852">#852</a> and this repository's
///         commonest defect.
///     </para>
///     <list type="number">
///         <item>
///             <b>Pointer to texels is <see cref="ImageView.ToImage" />, and it already existed.</b>
///             The control doc 48 § B6 asked for carries the pan, the zoom and the inverse; a second
///             opinion about that arithmetic here would be a view whose cursor and whose picture
///             disagreed about where a texel is.
///         </item>
///         <item>
///             ⚠ <b>Screen radius to texels is the <em>identity</em> here, and finding that out
///             refutes the obvious reading.</b> <see cref="PaintBrush.Radius" /> is authored in
///             texels of the atlas — its own remarks say why — so a 2D view has nothing to convert
///             on the way in, and nothing on the way out either: <see cref="ShowCursor" /> draws the
///             ring in texels, and <c>ImageView</c>'s own pan and zoom are what put it on the screen
///             at the size of the stamp that would land. A 3D view is where the hit triangle's texel
///             density comes in, because there a screen radius is what the artist is actually
///             holding. ⚠ There <em>was</em> a <c>ScreenRadius</c> here saying that in arithmetic and
///             nothing called it — <a href="https://github.com/Rikarin/Vixen/issues/928">#928</a> —
///             so the claim is stated where the ring is drawn instead of in a member that could stop
///             being true with nothing to notice.
///         </item>
///         <item>
///             ⚠ <b>There are no mirrors, and that is a refusal rather than an omission.</b> Planar
///             symmetry mirrors a point in <em>object</em> space and the mirrored point lands on a
///             different triangle in a different island — <see cref="PaintSession" />'s remarks — so
///             the surface that can supply one is the surface holding the mesh. This one holds an
///             atlas. It calls <see cref="PaintSession.MoveAll(ReadOnlySpan{Vector2}, List{PaintRect})" />
///             with one position rather than pretending an atlas-space flip is symmetry.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>The pointer handler is registered on the <see cref="RoutingStrategy.Capture" /> leg
///         and that is load-bearing.</b> <c>ImageView</c> pans on a primary drag and marks every
///         pointer event handled; a handler added the ordinary way — <c>Bubble</c>, with
///         <c>handledEventsToo</c> defaulting to false — would be registered, would look correct, and
///         would never once run. Capture also gives the right behaviour rather than merely a running
///         handler: in <see cref="PaintToolMode.Paint" /> the drag is swallowed before the pan sees
///         it, and in <see cref="PaintToolMode.Select" /> nothing is swallowed and the pane pans as
///         it always did.
///     </para>
///     <para>
///         ⚠ <b>Shift-click lays a straight stroke from where the last one ended, and it needed no
///         new arithmetic at all.</b> Doc 48 § D13 lists "curve/path strokes" beside symmetry and
///         smoothing as stroke-level work that does not touch the kernel, and the straight case
///         turns out to be nothing whatever: <c>BrushStroke.MoveTo</c> already walks the segment
///         between two positions laying evenly spaced stamps and carrying the leftover distance, so
///         a line is two <see cref="PaintSession.MoveAll(ReadOnlySpan{Vector2}, List{PaintRect})" />
///         calls and one undo entry.
///     </para>
///     <para>
///         ⚠ <b>A <em>curved</em> path needed a front end and not a sampler, which is what
///         <a href="https://github.com/Rikarin/Vixen/issues/1084">#1084</a> found.</b> Those two
///         calls interpolate straight, so a curve fed to them as its endpoints paints its chord —
///         and a sampler built before there was anywhere to author control points would have been a
///         finished thing nothing called. <see cref="PaintToolMode.Path" /> is that somewhere: left
///         click places a point, Enter or a right click lays the whole curve as one stroke, Escape
///         drops it and Backspace takes a point back. <see cref="PaintPath" /> is the curve, and
///         <see cref="StrokePath" /> is the six lines that hand its positions to the same
///         <c>MoveAll</c> a drag uses.
///     </para>
///     <para>
///         ⚠ <b>What the pane shows during a drag is <see cref="PaintComposite.Result" />, which is
///         an approximation whose size is stated.</b> The composite is straight-alpha source-over
///         between the two cached halves; a compiled stack composites through <c>Colour/Blend</c>'s
///         sixteen operators. Whoever supplies the <see cref="PaintTarget" /> decides how good the
///         halves are, and today the module supplies empty ones —
///         <a href="https://github.com/Rikarin/Vixen/issues/849">#849</a> is the seam that makes them
///         the plan's, and <see cref="PaintStackImages" /> is the shape it will arrive in.
///     </para>
/// </remarks>
sealed class PaintUvView {
    /// <summary>How many segments the cursor ring is drawn with.</summary>
    /// <remarks>
    ///     A constant rather than a function of the radius: the ring is a label on the picture, and
    ///     twenty-four segments is already smoother than a one-pixel stroke can show.
    /// </remarks>
    const int CursorSegments = 24;

    readonly PaintTool tool;
    readonly UiElement status;

    /// <summary>Each stamp's own rectangle, for the move being processed — #894's overload.</summary>
    /// <remarks>
    ///     ⚠ <b>Kept and reused rather than allocated per move.</b> A pointer move is every frame the
    ///     artist is dragging, and the list is at most one rectangle per stamp the move earned.
    /// </remarks>
    readonly List<PaintRect> dirtied = [];

    /// <summary>The curve an artist is placing, in <see cref="PaintToolMode.Path" />.</summary>
    /// <remarks>
    ///     ⚠ <b>On the view and not on the tool, because its points are texels of <em>this</em>
    ///     atlas.</b> The tool survives the panel being closed and is shared with whatever 3D surface
    ///     arrives; a half-placed path is a gesture in one pane at one resolution, and carrying it
    ///     across either would be a curve through points nobody clicked. It is the same argument
    ///     <see cref="anchor" /> already makes, which is why both are cleared together.
    /// </remarks>
    readonly PaintPath path = new();

    /// <summary>Where the path's curve goes, kept rather than allocated per pointer move.</summary>
    readonly List<Vector2> sampled = [];

    /// <summary>Which placed point the pointer is dragging, or -1.</summary>
    /// <remarks>
    ///     ⚠ <b>An index and not the point itself, because the drag has to survive the list moving
    ///     under it.</b> Inserting a point is followed straight away by dragging the one that was
    ///     just inserted, which is one gesture from the artist's side — so what the drag holds is a
    ///     place in the order rather than a value that would go stale the moment anything shifted.
    /// </remarks>
    int held = -1;

    /// <summary>Where the pointer last was, in texels, whether or not anything was painted.</summary>
    /// <remarks>
    ///     ⚠ <b>Beside <see cref="last" /> rather than instead of it, because they answer different
    ///     questions.</b> <see cref="last" /> is where the last <em>stamp</em> went and is what a
    ///     shift-click line starts from; this is where the pointer is, which is what a key pressed
    ///     over a placed point has to be measured against. Using the stamp for that would delete a
    ///     point near wherever the previous stroke happened to end.
    /// </remarks>
    Vector2 hovered;

    /// <summary>How many overlay segments belong to the islands rather than to the cursor.</summary>
    /// <remarks>
    ///     ⚠ <b>One list for both, split by an index, because <c>ImageView.Overlay</c> is one list.</b>
    ///     The islands change when a mesh does and the ring changes every pointer move, so the ring
    ///     is the tail and only the tail is rewritten.
    /// </remarks>
    int outlines;

    /// <summary>Whether a fit has succeeded since the atlas last changed size.</summary>
    /// <remarks>
    ///     ⚠ <b>Retried rather than done once, because the first <see cref="Show" /> is before the
    ///     first layout.</b> A panel's factory runs while its box is still zero-sized, and
    ///     <c>ImageView.Fit</c> answers false for exactly that case rather than computing a zoom of
    ///     zero — so a view that fitted once, at build, would open every stack at 100% in a corner.
    /// </remarks>
    bool fitted;

    PaintSession? session;

    /// <summary>Where the last stroke ended, in texels, for a shift-click line to start from.</summary>
    /// <remarks>
    ///     ⚠ <b>Cleared when the atlas changes size, because it is in texels of one.</b> An anchor
    ///     kept across a resolution change is a point of an atlas that no longer exists, and the
    ///     line drawn from it would start somewhere the artist never clicked — silently, because a
    ///     shift-click looks the same either way.
    /// </remarks>
    Vector2? anchor;

    /// <summary>Where the stroke in flight last stamped, which becomes the anchor at pointer-up.</summary>
    Vector2 last;

    /// <summary>Builds the pane into a host element.</summary>
    /// <param name="host">Where it goes. A dock panel, or anything inside one.</param>
    /// <param name="tool">The brush and the mode. Held, not copied.</param>
    /// <exception cref="ArgumentNullException">Either is null.</exception>
    public PaintUvView(UiElement host, PaintTool tool) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(tool);

        this.tool = tool;

        DockPanel.Fills(host);

        // ⚠ Here as well as in `LayerStackView`, and finding out why is what #1106 cost. The sheet
        // was installed by the layers panel alone, which was invisible while these panes styled
        // their own hosts inline — and became a pane laid out as a strip the moment the layout moved
        // into the sheet. A docked workspace restoring this panel without the layers one is a real
        // state, and `Install` is idempotent per document, so the honest fix is for every view that
        // depends on the sheet to ask for it.
        TexturingTheme.Install(host.Document);

        // ⚠ Styled by `TexturingTheme.vcss` and not from here — #1106. See `PaintMeshView`: the
        // two panes were in the same position and are fixed on the same pass.
        var root = host.Add("paint-uv");
        var title = root.Add("world-title");

        title.Text = "Paint (UV)";

        // ⚠ Added *before* the viewer and pointed at it *after*, and both halves are deliberate.
        // `Add` appends, so the strip has to be built first to sit above the picture; and
        // `ImageViewBar.View` adopts what the viewer already holds, so the assignment must come
        // after the viewer exists rather than the strip pushing its first segment at construction.
        Channels = root.Add<ImageViewBar>();

        Image = root.Add<ImageView>();

        Channels.View = Image;

        status = root.Add("paint-uv-status");

        // ⚠ Capture, not Bubble. See the type's remarks: `ImageView` marks its own pointer events
        // handled, so the ordinary registration would never run — and running is only half of it,
        // because a paint drag and a pan drag are the same gesture and exactly one of them may win.
        Image.AddHandler<PointerEvent>(
            (_, args) => Pointed(args),
            RoutingStrategy.Capture
        );

        // ⚠ Bubble and not Capture, unlike the pointer above, and the asymmetry is the point.
        // `ImageView` does not handle key events, so the ordinary registration runs; taking them on
        // the capture leg would take Enter off anything the pane ever comes to contain.
        Image.AddHandler<KeyEvent>((_, args) => Keyed(args));

        Status = "";
    }

    /// <summary>The atlas at zoom, with the islands over it.</summary>
    public ImageView Image { get; }

    /// <summary>The channel and transfer-function pickers over that atlas.</summary>
    /// <remarks>
    ///     ⚠ <b>The isolate is what a paint pane is <em>for</em>, more than either other panel</b> —
    ///     <a href="https://github.com/Rikarin/Vixen/issues/1012">#1012</a>. A stroke's coverage
    ///     lives in alpha, and a composite drawn as RGB shows an artist the colour they painted and
    ///     not how much of it landed; the alpha segment is the only way to see a mask's own edge, and
    ///     the islands overlay draws over whichever answer is chosen.
    /// </remarks>
    public ImageViewBar Channels { get; }

    /// <summary>What the line under the pane says.</summary>
    public string Status { get; private set; }

    /// <summary>The composite the last drag built, kept after pointer-up.</summary>
    /// <remarks>
    ///     ⚠ <b>Not cleared at pointer-up, and that is what an undo needs.</b>
    ///     <c>PaintStrokeCommand</c> re-resolves the composite when the drag is undone or redone —
    ///     it is the picture the pane is showing — so a field cleared with the session would leave
    ///     the first undo after a stroke redrawing nothing.
    /// </remarks>
    public PaintComposite? Live { get; private set; }

    /// <summary>Asked at pointer-down for what to paint into, or null when nothing can be.</summary>
    /// <remarks>
    ///     ⚠ <b>A factory rather than a held target, because the answer changes between drags.</b>
    ///     The selected layer, the canvas behind it and the picture the pane is showing are all
    ///     things an artist changes with the pointer up, and a target captured when the pane was
    ///     built would paint into whichever layer was selected first.
    /// </remarks>
    public Func<PaintTarget?>? Target { get; set; }

    /// <summary>Told what a move, an undo or a redo dirtied, so a caller can re-upload it.</summary>
    /// <remarks>
    ///     ⚠ <b>Once per stamp during a drag, and once for the whole stroke on an undo or a redo.</b>
    ///     The two are different shapes for a reason a caller has to know: a move's rectangles are
    ///     each a stamp's own footprint — <a href="https://github.com/Rikarin/Vixen/issues/871">#871</a>
    ///     — so a fast drag reports several small ones rather than the box spanning them, while an
    ///     undo genuinely moves every texel the stroke ever touched and has nothing smaller to say.
    /// </remarks>
    public Action<PaintRect>? Painted { get; set; }

    /// <summary>Told when an undo or a redo has moved texels, so a caller can persist them.</summary>
    /// <remarks>
    ///     ⚠ <b>Separate from <see cref="Painted" /> because the two have different costs and
    ///     different audiences.</b> <see cref="Painted" /> fires per dirtied region, including once
    ///     per pointer move during a drag, and re-uploads a rectangle. This fires once per undo or
    ///     redo of a whole stroke, and its caller writes a 64 MB canvas to disk — which is affordable
    ///     at that rate and ruinous at the other. Without it an undone stroke stays in the
    ///     <c>.vxpaint</c>, and a session reopened later brings it back. ⚠ This used to say the
    ///     layers pane would go on showing the stroke, which is no longer true: both panes read the
    ///     canvas through <see cref="PaintCanvasStore" /> rather than the file
    ///     (<a href="https://github.com/Rikarin/Vixen/issues/885">#885</a>), so the redraw the caller
    ///     does beside the save is what mends the picture. The save is about the next session.
    /// </remarks>
    public Action? Reverted { get; set; }

    /// <summary>Told at pointer-up what the drag was, for a caller to put on the undo stack.</summary>
    /// <remarks>
    ///     Not raised for a drag that painted nothing — <see cref="PaintSession.End" /> answers null
    ///     for one, and pushing an empty entry makes the artist's next undo do nothing visible.
    /// </remarks>
    public Action<IEditorCommand>? Finished { get; set; }

    /// <summary>Puts a picture and a sentence in the pane.</summary>
    /// <param name="image">The renderer's name for the texture, or zero for none.</param>
    /// <param name="width">The atlas width in texels, whether or not there is a picture.</param>
    /// <param name="height">Its height.</param>
    /// <param name="text">What to say under it.</param>
    /// <remarks>
    ///     ⚠ <b>The extent is set whether or not there is a picture</b>, which is
    ///     <c>LayerStackPicture</c>'s decision for its reason: the zoom, the fit and every pointer
    ///     position are about the texels being authored, so a pane that lost them when a compile
    ///     failed would rescale itself every time somebody typed a bad number into a layer.
    /// </remarks>
    public void Show(ulong image, int width, int height, string text) {
        var resized = Image.ImageWidth != width || Image.ImageHeight != height;

        Image.Image = image;
        Image.ImageWidth = width;
        Image.ImageHeight = height;

        // A different atlas is a different coordinate space, so the old pan and zoom describe
        // nothing — and neither does the anchor a shift-click would draw a line from, nor the path a
        // pen gesture has half placed. `Fit` answers false before the first layout and is asked
        // again on the next show.
        if (resized) {
            fitted = false;
            anchor = null;

            path.Clear();

            Drop();
        }

        // ⚠ Never while a stroke is in flight. `Fit` writes both `Zoom` and `Pan`, which are the
        // whole of `ToImage` — so a fit that lands between two stamps puts every later stamp of that
        // drag somewhere other than under the pointer. It is reachable because the first fit is
        // attempted while the panel's box is still zero-sized and answers false, leaving `fitted`
        // false for whatever `Show` comes next — and during a drag that is `Painted`'s redraw.
        if (!fitted && session is null) {
            fitted = Image.Fit();
        }

        Say(text);
    }

    /// <summary>Changes the sentence under the pane and nothing else.</summary>
    /// <param name="text">What to say.</param>
    /// <remarks>
    ///     ⚠ <b>Separate from <see cref="Show" /> because a refusal must not resize the pane.</b>
    ///     Pointer-down can fail — no stack open, no paint layer, a canvas at the wrong resolution —
    ///     and reporting that through <see cref="Show" /> would set the extent to whatever the caller
    ///     happened to pass and throw away the artist's pan and zoom on a failed click.
    /// </remarks>
    public void Say(string text) {
        Status = text;
        status.Text = text;
    }

    /// <summary>Draws a mesh's UV islands under the brush.</summary>
    /// <param name="coordinates">Three UV coordinates per triangle, in the unit square.</param>
    /// <exception cref="ArgumentException">The coordinate count is not a multiple of three.</exception>
    /// <remarks>
    ///     ⚠ <b>The islands are the point of the 2D view</b> — doc 48 § D13 calls it "the only way to
    ///     fix the places the 3D view cannot reach", and a pane showing an atlas with no islands on
    ///     it cannot say which of its texels are surface. The segments are in texels because
    ///     <c>ImageOverlaySegment</c> is, so they survive a pan and a zoom without being rebuilt.
    /// </remarks>
    public void ShowIslands(IReadOnlyList<Vector2> coordinates) {
        ArgumentNullException.ThrowIfNull(coordinates);

        if (coordinates.Count % 3 != 0) {
            throw new ArgumentException(
                $"UV triangles come three coordinates at a time and this is {coordinates.Count}.",
                nameof(coordinates)
            );
        }

        // ⚠ The whole list, unlike the cursor's own removal below. New islands are a new mesh, so the
        // ring that was under the pointer describes a texel of an atlas that no longer exists.
        Image.Overlay.Clear();

        for (var triangle = 0; triangle < coordinates.Count; triangle += 3) {
            var a = Texels(coordinates[triangle]);
            var b = Texels(coordinates[triangle + 1]);
            var c = Texels(coordinates[triangle + 2]);

            Image.Overlay.Add(new(a, b));
            Image.Overlay.Add(new(b, c));
            Image.Overlay.Add(new(c, a));
        }

        outlines = Image.Overlay.Count;
    }

    /// <summary>Where a pointer is, in texels.</summary>
    /// <param name="x">Its x, in document space.</param>
    /// <param name="y">Its y.</param>
    /// <returns>The texel position, which is outside the atlas when the pointer is off it.</returns>
    public Vector2 ToTexels(float x, float y) => Image.ToImage(x, y);

    /// <summary>Puts the brush's ring under a pointer position.</summary>
    /// <param name="at">Where, in texels.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>In texels with a screen-pixel thickness, which is what makes it read as a
    ///         cursor.</b> A ring whose radius were in screen pixels would be the same size at every
    ///         zoom and would therefore lie about what the stamp covers — and that lie is invisible
    ///         until the artist zooms, which is precisely when they are trying to place a small
    ///         stroke exactly.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An ellipse and not a ring, because the stamp is one</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1064">#1064</a>. A brush aimed at a
    ///         stretched chart covers an ellipse in the atlas, and a cursor drawn round over it lies
    ///         about the stamp in exactly the direction the artist is trying to judge. It is a circle
    ///         for every stroke made in this pane — <c>PaintBrush.Aspect</c> is one unless a 3D
    ///         surface measured it — so this is the same picture it always was until something aims
    ///         at a model.
    ///     </para>
    /// </remarks>
    public void ShowCursor(Vector2 at) {
        Image.Overlay.RemoveRange(outlines, Image.Overlay.Count - outlines);

        ShowPath();

        var brush = tool.Brush;

        // The same two semi-axes `PaintBrush.Circularised` divides by, so the ring is the stamp's own
        // boundary rather than a second opinion about its shape.
        var stretch = new PaintStamp(at, 0f, brush.Radius, 1f, brush.Aspect, brush.AspectAngle).Stretch;
        var half = MathF.Sqrt(stretch);
        var (sin, cos) = MathF.SinCos(brush.AspectAngle);
        var along = brush.Radius * half;
        var across = brush.Radius / half;

        Span<Vector2> outline = stackalloc Vector2[tool.IsMasked ? 4 : CursorSegments];

        Boundary(outline, tool.IsMasked, tool.IsAngled ? brush.Angle : 0f);

        var previous = Rim(at, along, across, sin, cos, outline[^1]);

        foreach (var unit in outline) {
            var point = Rim(at, along, across, sin, cos, unit);

            Image.Overlay.Add(new(previous, point));
            previous = point;
        }
    }

    /// <summary>How near a placed point or the curve a click counts as aiming at it, in texels.</summary>
    /// <remarks>
    ///     ⚠ <b>Eight pixels on screen converted into texels, and never eight texels.</b> A grab
    ///     radius in texels is a target the size of a fingernail on an atlas viewed at 800% and a
    ///     target smaller than the cursor at 12%, so an artist zoomed out could not pick up a point
    ///     at all — which is the scale trap this repository keeps shipping, in the one place where a
    ///     zoom rather than a model puts three orders of magnitude between the two ends.
    /// </remarks>
    float Grab() => Image.Zoom > 0f ? 8f / Image.Zoom : 8f;

    /// <summary>The curve being placed, drawn under the cursor.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Sampled at the same tolerance the stroke will be laid at, so the preview is the
    ///         stroke's own path and not a smoother drawing of it</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1084">#1084</a>. A preview drawn from
    ///         a second sampler is how a curve comes to look right in the pane and paint faceted,
    ///         which is a defect an artist can only find by painting.
    ///     </para>
    ///     <para>
    ///         Drawn from inside <see cref="ShowCursor" /> because that method owns the overlay's
    ///         tail — the islands are the head — so one call rewrites both and neither can take the
    ///         other off.
    ///     </para>
    /// </remarks>
    void ShowPath() {
        if (path.Points.Count == 0) {
            return;
        }

        path.Sample(sampled);

        for (var step = 1; step < sampled.Count; step++) {
            Image.Overlay.Add(new(sampled[step - 1], sampled[step]));
        }

        // A tick at each placed point, so an artist can tell a point they clicked from the curve
        // through it — which is what says whether the next click will extend or correct.
        foreach (var point in path.Points) {
            Image.Overlay.Add(new(point - new Vector2(3f, 0f), point + new Vector2(3f, 0f)));
            Image.Overlay.Add(new(point - new Vector2(0f, 3f), point + new Vector2(0f, 3f)));
        }
    }

    /// <summary>The keys a half-placed path answers to.</summary>
    /// <remarks>
    ///     ⚠ <b>Only while there is a path, and that is what keeps them out of everybody's way.</b>
    ///     Enter, Escape and Backspace all belong to something else in an editor — a dialog, a
    ///     field, a dock — and a pane that swallowed them whenever it had the focus would break
    ///     those. With no points placed nothing here is handled, so the keys route on exactly as
    ///     they did before this existed.
    /// </remarks>
    void Keyed(KeyEvent args) {
        if (args.Action != KeyAction.Pressed || path.Points.Count == 0) {
            return;
        }

        switch (args.Key) {
            case InputKey.Enter when args.Has(ModifierKeys.None):
                StrokePath();

                break;

            case InputKey.Escape when args.Has(ModifierKeys.None):
                path.Clear();

                Drop();

                Say("Path cleared.");
                ShowCursor(hovered);

                break;

            case InputKey.Backspace when args.Has(ModifierKeys.None):
                // ⚠ Before the removal, because it reads the index the removal is about to take.
                Removing(path.Points.Count - 1);

                path.Undo();
                Say(Placed());
                ShowCursor(hovered);

                break;

            // ⚠ A different key from Backspace and deliberately so — #1084's remainder. Backspace
            // takes the *last* point back, which is the pen gesture every tool has and is what an
            // artist reaches for while still placing; this takes the point they are pointing at,
            // which is what they reach for having seen the curve. One key doing both would have to
            // guess which, and would guess wrong exactly when the pointer happened to be near the
            // end of the path.
            case InputKey.Delete when args.Has(ModifierKeys.None):
                if (path.Nearest(hovered, Grab()) is var picked && picked < 0) {
                    // Not over a point. Left unhandled, so Delete goes on meaning whatever it means
                    // to whatever else has the focus.
                    return;
                }

                Removing(picked);

                path.RemoveAt(picked);
                Say(Placed());
                ShowCursor(hovered);

                break;

            default:
                return;
        }

        args.Handled = true;
    }

    /// <summary>What the status line says while a path is being placed.</summary>
    /// <returns>The sentence.</returns>
    /// <remarks>
    ///     ⚠ <b>The verbs an artist cannot see are the ones worth naming.</b> Placing a point is
    ///     discoverable — it is the click they already made — while dragging one, inserting into the
    ///     curve and deleting the one under the pointer are three gestures with nothing on screen to
    ///     announce them. A path tool whose corrections are undiscoverable is #1084's own complaint
    ///     one level up: the artist does not know the path <em>can</em> be corrected.
    /// </remarks>
    string Placed() =>
        $"{path.Points.Count} point(s). Drag one to move it, click the curve to add one, Delete "
        + "takes the one under the pointer, Backspace the last. Enter or right-click lays the curve.";

    /// <summary>Lays the placed curve as one stroke, and forgets it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>At zero smoothing whatever the tool's slider says, for the shift-click line's
    ///         reason</b> — <c>PaintStroke</c> lags the input points, so a path laid from sampled
    ///         positions would be pulled off the curve by exactly the smoothing fraction, and the
    ///         painting would miss the drawing the artist was looking at. #1084 names this half
    ///         explicitly.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One session and therefore one undo entry</b>, however many positions the curve
    ///         was sampled at: the tolerance decides how often the stroke is told where the pointer
    ///         is and not how many stamps it lays, which is <c>BrushStroke</c>'s spacing's job.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A path that cannot be painted is kept rather than dropped.</b> Pointer-down fails
    ///         when no stack is open or no layer is selected, and an artist who has clicked eleven
    ///         points would lose all of them to one refusal they can fix and retry.
    ///     </para>
    /// </remarks>
    void StrokePath() {
        // ⚠ Enter reaches `Keyed` while a point is still being dragged — the pointer is down and the
        // keyboard is not — so the drag is ended here rather than left holding an index into a list
        // this method is about to clear. And the *capture* goes with it: clearing the index alone
        // strands the pointer on this element, which is the defect the release arm's own remark
        // describes.
        Drop();

        if (!path.IsStrokeable) {
            path.Clear();
            Say("A path needs two points before it is a stroke.");

            return;
        }

        if (Begin(0f) is not { } started) {
            return;
        }

        session = started;
        Live = started.Composite;

        // ⚠ Its own list rather than the preview's. `Stamp` can redraw the cursor, which resamples
        // into `sampled` — so walking that one here would be mutating the collection being walked,
        // for a saving of one allocation per commit gesture.
        List<Vector2> positions = [];

        path.Sample(positions);

        foreach (var at in positions) {
            Stamp(at, false);
        }

        End();
        path.Clear();
        ShowCursor(last);
    }

    /// <summary>The stamp's boundary, on the unit circle or the unit square.</summary>
    /// <param name="outline">Where the points go. Its length is how many there are.</param>
    /// <param name="square">Whether the stamp is a masked one, whose boundary is its square.</param>
    /// <param name="angle">How far the square is turned, in radians.</param>
    /// <remarks>
    ///     ⚠ <b>A square for a masked brush, and it is the difference between a settable angle and
    ///     an unsettable one</b> — <a href="https://github.com/Rikarin/Vixen/issues/1083">#1083</a>.
    ///     A masked stamp covers its square and not the disc inside it, so a ring drawn over one lies
    ///     about the stamp by the corners — and, worse, it is the same picture at every angle, which
    ///     leaves an artist setting a rotation they cannot see. The corners are at <c>(±1, ±1)</c>
    ///     because that is where <c>TerrainBrush</c>'s alpha uv reaches one.
    ///     <para>
    ///         The turn is <see cref="PaintTool.IsAngled" />'s and not the brush's angle outright:
    ///         under <c>BrushRotation.Random</c> or <c>AlongStroke</c> the next stamp's angle is not
    ///         known until it is laid, so an upright square is the honest drawing of it.
    ///     </para>
    /// </remarks>
    static void Boundary(Span<Vector2> outline, bool square, float angle) {
        if (square) {
            var (sin, cos) = MathF.SinCos(angle);

            ReadOnlySpan<Vector2> corners = [new(1f, 1f), new(-1f, 1f), new(-1f, -1f), new(1f, -1f)];

            for (var corner = 0; corner < outline.Length; corner++) {
                var (x, y) = (corners[corner].X, corners[corner].Y);

                outline[corner] = new((x * cos) - (y * sin), (x * sin) + (y * cos));
            }

            return;
        }

        for (var step = 0; step < outline.Length; step++) {
            var (y, x) = MathF.SinCos(step * (MathF.Tau / outline.Length));

            outline[step] = new(x, y);
        }
    }

    /// <summary>One point of the stamp's boundary, in the atlas.</summary>
    /// <param name="at">Where the stamp is, in texels.</param>
    /// <param name="along">Its long semi-axis, in texels.</param>
    /// <param name="across">Its short one.</param>
    /// <param name="sin">The sine of the long axis's angle in the atlas.</param>
    /// <param name="cos">Its cosine.</param>
    /// <param name="unit">The point of the boundary, on the unit circle or the unit square.</param>
    /// <returns>The point, in texels.</returns>
    static Vector2 Rim(Vector2 at, float along, float across, float sin, float cos, Vector2 unit) {
        var u = unit.X * along;
        var v = unit.Y * across;

        return at + new Vector2((u * cos) - (v * sin), (u * sin) + (v * cos));
    }

    Vector2 Texels(Vector2 uv) => new(uv.X * Image.ImageWidth, uv.Y * Image.ImageHeight);

    /// <summary>The pointer, on the capture leg, before the pan sees it.</summary>
    /// <remarks>
    ///     ⚠ <b>The drag in flight is checked before the mode is.</b> Toggling paint off mid-drag —
    ///     the keyboard shortcut is a verb, so it can happen — would otherwise strand the session:
    ///     the release would fall through to the pan, no command would be made, and the stroke would
    ///     be on the canvas with nothing on the undo stack to take it off.
    /// </remarks>
    void Pointed(PointerEvent args) {
        if (session is null && tool.Mode == PaintToolMode.Select) {
            return;
        }

        switch (args.Action) {
            // ⚠ Before the hover case, which returns rather than breaking — a moved point that fell
            // through to it would redraw the ring and leave the point where it was, which reads as a
            // handle that cannot be picked up rather than as a missing case.
            case PointerAction.Moved when held >= 0:
                hovered = ToTexels(args.X, args.Y);

                path.Move(held, hovered);
                ShowCursor(hovered);

                break;

            case PointerAction.Released when held >= 0:
                Drop();

                Say(Placed());

                break;

            case PointerAction.Moved when session is null:
                // Hover: the ring follows, and the event is left alone so nothing else changes.
                hovered = ToTexels(args.X, args.Y);

                ShowCursor(hovered);

                return;

            // ⚠ Before the painting cases, because a press in `Path` mode is not a stroke and the
            // case below it would swallow one. Left places a point and right lays the curve: the
            // pen gesture every polygon tool uses. Enter does it too — see `Keyed` — and the two are
            // both here because the keyboard one needs the pane to hold the focus and a pointer
            // gesture never does.
            case PointerAction.Pressed when session is null && tool.Mode == PaintToolMode.Path:
                switch (args.Button) {
                    case PointerButton.Primary:
                        hovered = ToTexels(args.X, args.Y);

                        // So that Enter, Escape, Backspace and Delete reach `Keyed`. Taken on the
                        // first point rather than at build, because a pane that stole the focus when
                        // it opened would take it off whatever the artist was typing in.
                        Image.Document.Focus(Image);
                        Pick(hovered);
                        ShowCursor(hovered);
                        Say(Placed());

                        break;

                    case PointerButton.Secondary:
                        StrokePath();

                        break;

                    default:
                        return;
                }

                break;

            case PointerAction.Pressed when session is null && args.Button == PointerButton.Primary:
                var line = anchor is not null && (args.Modifiers & ModifierKeys.Shift) != 0;

                // ⚠ A line stroke takes no smoothing whatever the tool's slider says. Smoothing is
                // a lag on the *input points* — `PaintStroke` lerps towards each one — so a line
                // laid from two points would stop short of the second by exactly the smoothing
                // fraction, which is a line that does not reach where the artist clicked and looks
                // like a broken gesture rather than like a setting.
                if (Begin(line ? 0f : tool.Smoothing) is not { } started) {
                    // Nothing to paint into. The event is deliberately not handled, so the pane
                    // still pans — a pointer that did nothing at all would read as a frozen panel.
                    return;
                }

                session = started;
                Live = started.Composite;

                Image.Document.Focus(Image);

                if (line && anchor is { } from) {
                    // ⚠ The whole stroke inside the press, and the pointer is never captured. There
                    // is no drag to follow: `BrushStroke.MoveTo` lays evenly spaced stamps along the
                    // segment between two positions, so a line is two moves and one undo entry —
                    // which is why doc 48 § D13 says a path stroke does not touch the kernel.
                    Stamp(from, false);
                    Stamp(ToTexels(args.X, args.Y), true);
                    End();

                    break;
                }

                Image.Document.CapturePointer(Image);
                Stamp(args);

                break;

            case PointerAction.Moved:
                Stamp(args);

                break;

            // ⚠ `when session is not null`, or a refused press strands the pan. A press with nothing
            // to paint into is deliberately left unhandled so the pane still pans — which means
            // `ImageView` has taken the pointer and is dragging. Handling the release here anyway
            // stopped it ever seeing the release, so the pane panned for the rest of the session.
            case PointerAction.Released when session is not null:
                Image.Document.ReleasePointer();
                End();

                break;

            default:
                return;
        }

        args.Handled = true;
    }

    /// <summary>Ends a drag of a placed point, capture and all.</summary>
    /// <remarks>
    ///     ⚠ <b>One place, because clearing <c>held</c> without releasing the capture is worse than
    ///     leaving both.</b> <c>UiDocument.Captured</c> routes every later pointer event to this
    ///     element until something clears it, so a drag ended by Escape, by Enter or by a secondary
    ///     press — none of which reaches the <c>Released</c> arm, since that arm is guarded on
    ///     <c>held</c> — left the pane swallowing the pointer for the rest of the session. That is
    ///     the same class as the stranded pan the release arm's own remark describes, one level up.
    /// </remarks>
    void Drop() {
        if (held < 0) {
            return;
        }

        held = -1;

        Image.Document.ReleasePointer();
    }

    /// <summary>Keeps a drag's index honest across a point being taken out from under it.</summary>
    /// <param name="index">The point about to be removed.</param>
    /// <remarks>
    ///     ⚠ <b>An index survives an insert and not a removal.</b> <c>held</c> is an index rather
    ///     than the point itself so that the drag survives the list moving underneath it — which is
    ///     true of <see cref="Pick" />'s insert, where everything moves <em>up</em> from a point at
    ///     or after the held one. A removal below it moves everything down, and the drag then
    ///     silently moved its neighbour: Delete and Backspace both reach this while the pointer is
    ///     still down, because the press took the focus.
    /// </remarks>
    void Removing(int index) {
        if (held < 0 || index < 0) {
            return;
        }

        if (index == held) {
            Drop();

            return;
        }

        if (index < held) {
            held--;
        }
    }

    /// <summary>What a primary press in the path mode means: move a point, insert one, or add one.</summary>
    /// <param name="at">Where the press was, in texels.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Three meanings for one gesture and no modifier between them, which is what every
    ///         pen tool does and is the only shape that fits this pane.</b> #1084 records that the
    ///         2D pane is one pointer handler on the capture leg; a correction verb behind a
    ///         modifier is a verb an artist has to be told about, and the alternative — a second
    ///         tool mode for editing — would put the artist in a mode where placing a point no
    ///         longer works.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The order is the whole of it: a point first, then the curve, then the
    ///         canvas.</b> A placed point is <em>on</em> the curve, so a test that asked about the
    ///         curve first would answer "insert" for every click on an existing point and the path
    ///         would grow a duplicate point each time an artist tried to correct one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An insert is followed by a drag of the point it inserted</b>, so placing one and
    ///         putting it where it belongs is one press rather than two. That is why the drag holds
    ///         an index: the list has just moved underneath it.
    ///     </para>
    /// </remarks>
    void Pick(Vector2 at) {
        var grab = Grab();

        if (path.Nearest(at, grab) is var point && point >= 0) {
            held = point;

            Image.Document.CapturePointer(Image);

            return;
        }

        if (path.Nearest(at, grab, out var on) is var segment && segment >= 0) {
            path.Insert(segment + 1, on);

            held = segment + 1;

            Image.Document.CapturePointer(Image);

            return;
        }

        path.Add(at);
    }

    PaintSession? Begin(float smoothing) {
        if (Target?.Invoke() is not { } target) {
            return null;
        }

        return PaintSession.Begin(target, tool.Brush, tool.Colour, smoothing);
    }

    void Stamp(PointerEvent args) => Stamp(ToTexels(args.X, args.Y), true);

    /// <summary>Moves the stroke to a texel, and tells the caller what that dirtied.</summary>
    /// <param name="at">Where, in texels.</param>
    /// <param name="cursor">Whether the ring follows — false for a point the pointer was never at.</param>
    void Stamp(Vector2 at, bool cursor) {
        if (session is null) {
            return;
        }

        last = at;

        if (cursor) {
            ShowCursor(at);
        }

        Span<Vector2> one = [at];

        // ⚠ The overload that hands back the rectangles, not the one that hands back their union —
        // #871 and #894. It has had no caller since it was written, so this is the first thing that
        // can show whether it works.
        session.MoveAll(one, dirtied);

        // ⚠ One call per stamp and not one for their union, so that what is uploaded is exactly what
        // `PaintComposite.Resolve` recomputed — these are the same rectangles it was just given.
        // Outside them `Result` still holds the seed, so the union would hand the host texels that
        // did not change.
        //
        // ⚠ It is not uniformly *fewer bytes*, and saying so is the honest form of #871's argument.
        // At the default spacing consecutive stamps overlap by most of their footprint, so their
        // union is the smaller number; what the union cannot bound is the case the rectangles were
        // bought for — a diagonal jump between two frames, or a mirrored pair on opposite sides of
        // the atlas, whose bounding box is a square spanning everything between the ends. Matching
        // the composite makes the upload the composite's own cost, which is a counter
        // `PaintCostTests` already gates; the union makes it a function of how far the pointer moved.
        foreach (var rect in dirtied) {
            Painted?.Invoke(rect);
        }
    }

    void End() {
        if (session is null) {
            return;
        }

        var finished = session;

        session = null;
        anchor = last;

        // ⚠ Both, and the second is what reaches the disk. This lambda is the command's own
        // callback, so it runs on the execute and on every later undo and redo — the three moments
        // the canvas in memory stops agreeing with the canvas in the file.
        if (finished.End(
                "Paint stroke",
                rect => {
                    Painted?.Invoke(rect);
                    Reverted?.Invoke();
                }
            ) is not { } command) {
            return;
        }

        Finished?.Invoke(command);
    }
}
