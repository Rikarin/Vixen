// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
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

    /// <summary>Builds the pane into a host element.</summary>
    /// <param name="host">Where it goes. A dock panel, or anything inside one.</param>
    /// <param name="tool">The brush and the mode. Held, not copied.</param>
    /// <exception cref="ArgumentNullException">Either is null.</exception>
    public PaintUvView(UiElement host, PaintTool tool) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(tool);

        this.tool = tool;

        DockPanel.Fills(host);

        var root = host.Add("paint-uv");

        root.SetStyle("display", "flex");
        root.SetStyle("flex-direction", "column");
        root.SetStyle("flex-grow", "1");

        var title = root.Add("world-title");

        title.Text = "Paint (UV)";

        Image = root.Add<ImageView>();
        status = root.Add("paint-uv-status");

        // ⚠ Capture, not Bubble. See the type's remarks: `ImageView` marks its own pointer events
        // handled, so the ordinary registration would never run — and running is only half of it,
        // because a paint drag and a pan drag are the same gesture and exactly one of them may win.
        Image.AddHandler<PointerEvent>(
            (_, args) => Pointed(args),
            RoutingStrategy.Capture
        );

        Status = "";
    }

    /// <summary>The atlas at zoom, with the islands over it.</summary>
    public ImageView Image { get; }

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
    ///     <c>.vxpaint</c>, and the layers pane, which resolves a paint layer by opening that file,
    ///     goes on showing the stroke the artist just took back.
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
        // nothing. `Fit` answers false before the first layout and is asked again on the next show.
        if (resized) {
            fitted = false;
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
    ///     ⚠ <b>In texels with a screen-pixel thickness, which is what makes it read as a cursor.</b>
    ///     A ring whose radius were in screen pixels would be the same size at every zoom and would
    ///     therefore lie about what the stamp covers — and that lie is invisible until the artist
    ///     zooms, which is precisely when they are trying to place a small stroke exactly.
    /// </remarks>
    public void ShowCursor(Vector2 at) {
        Image.Overlay.RemoveRange(outlines, Image.Overlay.Count - outlines);

        var radius = tool.Brush.Radius;
        var previous = at + new Vector2(radius, 0f);

        for (var step = 1; step <= CursorSegments; step++) {
            var angle = step * (MathF.Tau / CursorSegments);
            var point = at + new Vector2(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);

            Image.Overlay.Add(new(previous, point));
            previous = point;
        }
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
        if (session is null && !tool.IsPainting) {
            return;
        }

        switch (args.Action) {
            case PointerAction.Moved when session is null:
                // Hover: the ring follows, and the event is left alone so nothing else changes.
                ShowCursor(ToTexels(args.X, args.Y));

                return;

            case PointerAction.Pressed when session is null && args.Button == PointerButton.Primary:
                if (Begin() is not { } started) {
                    // Nothing to paint into. The event is deliberately not handled, so the pane
                    // still pans — a pointer that did nothing at all would read as a frozen panel.
                    return;
                }

                session = started;
                Live = started.Composite;

                Image.Document.Focus(Image);
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

    PaintSession? Begin() {
        if (Target?.Invoke() is not { } target) {
            return null;
        }

        return PaintSession.Begin(target, tool.Brush, tool.Colour, tool.Smoothing);
    }

    void Stamp(PointerEvent args) {
        if (session is null) {
            return;
        }

        var at = ToTexels(args.X, args.Y);

        ShowCursor(at);

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
