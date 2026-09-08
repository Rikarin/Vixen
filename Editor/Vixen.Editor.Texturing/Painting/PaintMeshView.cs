// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.Texturing.Painting;

/// <summary>
///     Doc 48 § D13's <em>first</em> front end at last: the model on screen, and a pointer that
///     lands on it.
/// </summary>
/// <remarks>
///     <para>
///         <b>The place to stand that
///         <a href="https://github.com/Rikarin/Vixen/issues/1063">#1063</a> is about.</b>
///         <c>PaintProjection</c>, <c>PaintFootprint</c>, <c>PaintSymmetry</c>,
///         <c>PaintProjector</c> and <c>LayerStackMesh.Projection</c> all landed across two batches
///         with no caller between them, because the missing piece was a viewport rather than more
///         arithmetic. This is that viewport.
///     </para>
///     <para>
///         ⚠ <b>The plugin's own pane and deliberately not the scene viewport, which is the issue's
///         own recommendation and its reasons are worth keeping here.</b> A <c>.vxlayers</c> names a
///         model <em>asset path</em>; nothing maps that to an entity in the open scene, and an
///         artist opening one has no reason to have the model in a scene at all. The scene
///         viewport's ray comes back in <b>world</b> space while <c>PaintProjection</c> works in the
///         mesh's own, so that route needs an entity's inverse transform it cannot get. And what the
///         artist would be looking at there is the entity's <em>material</em> rather than the
///         stack's composite — so the stroke would be invisible until a bake, which is the one thing
///         a paint view exists not to be.
///     </para>
///     <para>
///         ⚠ <b>The brush radius means <em>pane pixels</em> here and atlas texels in
///         <c>PaintUvView</c>, and that is the whole difference between the two front ends rather
///         than an inconsistency.</b> <c>PaintBrush.Radius</c> is authored in texels because the 2D
///         pane paints in texels; a 3D view has a disc on the screen and the texels it covers are a
///         property of the chart under it, which is exactly what <c>PaintFootprint</c> computes.
///         <c>PaintProjector.Begin</c> is where the conversion happens and the ellipse it hands back
///         is what the session's brush is built from — so a stroke across a stretched chart keeps
///         its size on screen and changes its size in the atlas, which is what an artist means by a
///         brush.
///     </para>
///     <para>
///         ⚠ <b>There are two pixel sizes here and the conversion between them is one number,
///         <see cref="Scale" />.</b> The pointer, the drag deltas and the brush's authored radius are
///         in <em>layout</em> pixels; the picture, the camera's rays and the footprint are in the
///         <em>picture's</em> pixels, which <see cref="RasterSize" /> caps at
///         <see cref="RasterLimit" />. Below the cap the two are the same number and every one of
///         those conversions is the identity, which is what they were before
///         <a href="https://github.com/Rikarin/Vixen/issues/1107">#1107</a> — so the cap is a
///         magnification the compositor performs and not a change of coordinate system.
///     </para>
///     <para>
///         ⚠ <b>Which of the two a quantity is in is not a detail: it is the whole of what
///         <c>PaintEye.Perspective</c>'s remarks warn about.</b> An eye built from a layout height
///         over a picture rasterised at another reports a brush of the wrong size, silently, and
///         reads as a broken brush rather than as a mismatched unit. <see cref="Begin" /> converts
///         the authored radius into the picture's pixels for exactly that reason, and
///         <see cref="Turned" /> converts nothing because a drag is measured against the pane the
///         hand is moving over.
///     </para>
/// </remarks>
sealed class PaintMeshView {
    /// <summary>The axis names the symmetry picker offers, in the order it offers them.</summary>
    /// <remarks>
    ///     ⚠ <b>Named planes and not an arbitrary one, because the model is in its own space and its
    ///     own space is where a rig is symmetric.</b> <c>PaintSymmetry.Across</c> takes any plane and
    ///     nothing in a <c>.vxlayers</c> says where a model's mirror is; the three axes through the
    ///     origin are what a character exported down its own axis actually needs, and an arbitrary
    ///     plane is a gizmo rather than a picker.
    /// </remarks>
    public static IReadOnlyList<string> Axes { get; } = ["None", "X", "Y", "Z"];

    /// <summary>The longest side the model is rasterised at, in the picture's own pixels.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/1107">#1107</a>, and the number is
    ///         measured rather than chosen.</b> <c>PaintMeshCostTests</c> times one geometry pass at
    ///         both sizes on the same machine in the same second: a maximised 4K pane costs about
    ///         two and a half times what a 1280×720 one does over an eighteen-thousand-triangle
    ///         model, and an orbit pays that <em>per pointer move</em> on one thread. A cap at 1600
    ///         across is most of that difference back.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A softness and not a lie, which is the whole reason a cap is allowed here at
    ///         all.</b> The pane is a viewport onto a model rather than an image being authored — the
    ///         thing an artist is judging is the atlas, which the 2D pane shows at its own
    ///         resolution — so a picture magnified by the compositor loses sharpness and nothing
    ///         else. It would not be allowed one pane across.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What it is <em>not</em> is the whole answer.</b> Even at the cap a geometry pass
    ///         is tens of milliseconds on a model-sized mesh, which is a visibly coarse orbit; the
    ///         issue's other two options — a parallel raster and a reduced draw while a gesture is in
    ///         flight — are still owed, and the measurement above is what decides between them.
    ///     </para>
    /// </remarks>
    public const int RasterLimit = 1600;

    readonly PaintTool tool;
    readonly UiElement status;
    readonly Select symmetry;
    readonly PaintMeshRaster raster = new();

    /// <summary>Each stamp's own rectangle, for the move being processed.</summary>
    /// <remarks>Kept and reused, for <c>PaintUvView</c>'s reason: a move is every frame of a drag.</remarks>
    readonly List<PaintRect> dirtied = [];

    /// <summary>The mesh, or null when the stack binds none.</summary>
    PaintProjection? mesh;

    /// <summary>What the mesh is textured with between strokes.</summary>
    PaintImage? atlas;

    /// <summary>What <see cref="mesh" /> was framed for, so a redraw does not move the camera.</summary>
    /// <remarks>
    ///     ⚠ <b>The object and not its triangle count.</b> <c>TexturingModule</c> caches one resolved
    ///     mesh and hands back the same instance until its key moves, so reference equality is
    ///     exactly "the artist is looking at the same model" — where a count would re-frame on a
    ///     different model of the same size and, worse, <em>not</em> re-frame when a re-export
    ///     changed the model's scale.
    /// </remarks>
    PaintProjection? framed;

    PaintSession? session;
    PaintProjector? projector;

    /// <summary>Which camera gesture the pointer is in the middle of.</summary>
    PaintMeshDrag drag;

    /// <summary>Where the pointer was on the previous event of that gesture, in document space.</summary>
    Vector2 held;

    /// <summary>Builds the pane into a host element.</summary>
    /// <param name="host">Where it goes. A dock panel, or anything inside one.</param>
    /// <param name="tool">The brush and the mode. Held, not copied — it is the same one the 2D pane has.</param>
    /// <exception cref="ArgumentNullException">Either is null.</exception>
    public PaintMeshView(UiElement host, PaintTool tool) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(tool);

        this.tool = tool;

        DockPanel.Fills(host);

        var root = host.Add("paint-mesh");

        root.SetStyle("display", "flex");
        root.SetStyle("flex-direction", "column");
        root.SetStyle("flex-grow", "1");

        var title = root.Add("world-title");

        title.Text = "Paint (3D)";

        var bar = root.Add("paint-mesh-bar");

        bar.SetStyle("display", "flex");
        bar.SetStyle("flex-direction", "row");

        var label = bar.Add("paint-mesh-symmetry-label");

        label.Text = "Symmetry";

        symmetry = bar.Add<Select>(null, null, "paint-mesh-symmetry");

        foreach (var axis in Axes) {
            symmetry.AddOption(axis);
        }

        symmetry.Value = tool.SymmetryAxis;

        // ⚠ On the tool rather than on this view, so it survives the panel being closed — the same
        // argument `TexturingModule` makes for holding the brush. A mirror an artist sets is a
        // property of how they are working and not of a dock panel's lifetime.
        symmetry.SelectionChanged += (_, chosen) => {
            tool.SetSymmetry(chosen);
            Say(Describe());
        };

        Image = root.Add<ImageView>();

        // The picture is a render of the model rather than an authored image, so a chequerboard
        // under it would say "transparent" about pixels that are the pane's own background.
        Image.ShowCheckerboard = false;

        status = root.Add("paint-mesh-status");

        // ⚠ Capture, not Bubble, and for `PaintUvView`'s reason exactly: `ImageView` marks its own
        // pointer events handled, so an ordinary registration is registered, looks right and never
        // runs. Here it matters twice over, because the camera gestures and `ImageView`'s own pan
        // are the same buttons and exactly one of them may win.
        Image.AddHandler<PointerEvent>((_, args) => Pointed(args), RoutingStrategy.Capture);
        Image.AddHandler<WheelEvent>((_, args) => Wheeled(args), RoutingStrategy.Capture);

        Status = "";
    }

    /// <summary>What size to rasterise a pane at, capped at <see cref="RasterLimit" />.</summary>
    /// <param name="paneWidth">How wide the pane is, in layout pixels.</param>
    /// <param name="paneHeight">How tall.</param>
    /// <returns>The picture's size. Zero in either axis when the pane has none.</returns>
    /// <remarks>
    ///     ⚠ <b>The aspect is kept, and that is not a nicety.</b> <c>ImageView.Fit</c> scales by the
    ///     <em>smaller</em> of the two ratios, so a picture of a different shape from the pane is
    ///     letterboxed — and every pane pixel in the letterbox converts, through
    ///     <c>ImageView.ToImage</c>, to a picture pixel outside the picture. The brush would then
    ///     aim at a part of the model that is not under the pointer. Truncation leaves the two
    ///     aspects within a pixel of each other, which is a letterbox under a pixel wide.
    /// </remarks>
    public static (int Width, int Height) RasterSize(int paneWidth, int paneHeight) {
        var longest = Math.Max(paneWidth, paneHeight);

        if (paneWidth <= 0 || paneHeight <= 0 || longest <= RasterLimit) {
            return (paneWidth, paneHeight);
        }

        var scale = (float)RasterLimit / longest;

        // ⚠ The long side is written rather than scaled, because `1600f / w * w` is not 1600 for
        // every w — and a picture one pixel under the cap would make every assertion about the cap
        // an assertion about float rounding.
        return paneWidth >= paneHeight
            ? (RasterLimit, Math.Max((int)(paneHeight * scale), 1))
            : (Math.Max((int)(paneWidth * scale), 1), RasterLimit);
    }

    /// <summary>The rendered model, as the interface draws it.</summary>
    public ImageView Image { get; }

    /// <summary>Where the model is seen from. The artist's, and it outlives a redraw.</summary>
    public PaintCamera Camera { get; } = new();

    /// <summary>What draws it, for a caller that wants the counters.</summary>
    public PaintMeshRaster Raster => raster;

    /// <summary>What the line under the pane says.</summary>
    public string Status { get; private set; }

    /// <summary>The composite the last drag built, kept after pointer-up.</summary>
    /// <remarks>
    ///     Not cleared at pointer-up, for <c>PaintUvView.Live</c>'s reason: the undo of a stroke
    ///     re-resolves it, and a field cleared with the session would leave the first undo redrawing
    ///     nothing.
    /// </remarks>
    public PaintComposite? Live { get; private set; }

    /// <summary>Asked at pointer-down for what to paint into, or null when nothing can be.</summary>
    public Func<PaintTarget?>? Target { get; set; }

    /// <summary>Told what a move, an undo or a redo dirtied <b>in the atlas</b>.</summary>
    /// <remarks>
    ///     ⚠ <b>In atlas texels and not in pane pixels, which is what makes the two panes agree.</b>
    ///     The other subscriber to this is the 2D view's own upload — a stroke made here has to show
    ///     up there, because they are two pictures of one layer — and the pane rectangle this view
    ///     re-uploads for itself goes out through <see cref="Patched" /> instead.
    /// </remarks>
    public Action<PaintRect>? Painted { get; set; }

    /// <summary>Told when an undo or a redo has moved texels, so a caller can persist them.</summary>
    public Action? Reverted { get; set; }

    /// <summary>Told at pointer-up what the drag was, for a caller to put on the undo stack.</summary>
    public Action<IEditorCommand>? Finished { get; set; }

    /// <summary>Told there is a whole new picture, for a caller to upload.</summary>
    /// <remarks>
    ///     ⚠ <b>An upload and not a rectangle, because the picture is a different set of pixels
    ///     everywhere.</b> This fires for a camera move, a resize and a new mesh — the three things
    ///     that change what every pane pixel shows — and never during a stroke, which is
    ///     <see cref="Patched" />'s whole reason for existing beside it.
    /// </remarks>
    public Action<PaintImage>? Presented { get; set; }

    /// <summary>Told which rectangle <b>of the pane</b> a stamp moved, for a caller to patch.</summary>
    public Action<PaintRect>? Patched { get; set; }

    /// <summary>Puts a mesh and an atlas in the pane and draws them.</summary>
    /// <param name="bound">The mesh, or null when the stack binds none.</param>
    /// <param name="picture">What to texture it with, or null when there is nothing to show.</param>
    /// <param name="text">What to say under it.</param>
    /// <remarks>
    ///     ⚠ <b>The camera is re-framed only for a mesh that is a different object.</b> A refresh
    ///     runs at every pointer-up and at every binding change; a framing per refresh would throw
    ///     away the artist's viewpoint each time they finished a stroke, which is the same class of
    ///     defect as <c>PaintUvView</c>'s fit landing between two stamps and is worse, because it
    ///     happens every time rather than occasionally.
    /// </remarks>
    public void Show(PaintProjection? bound, PaintImage? picture, string text) {
        var moved = !ReferenceEquals(bound, mesh) || !ReferenceEquals(picture, atlas);

        mesh = bound;
        atlas = picture;

        if (bound is not null && !ReferenceEquals(bound, framed)) {
            framed = bound;

            Camera.Frame(bound.Bounds);
        }

        if (bound is null) {
            framed = null;
        }

        // ⚠ Nothing at all when neither object has changed, and that is what keeps a stroke off
        // the geometry pass. `TexturingModule.RefreshPaint` runs at every pointer-up and hands
        // back the *same* resolved mesh and the *same* cached canvas — so a `Show` that redrew
        // unconditionally would rasterise the model and re-upload the whole pane once per stroke,
        // which is what doc 48's exit criterion 8 is about one gesture up from the stamp.
        //
        // ⚠ It is object identity and not contents, and the risk is stated rather than hidden: a
        // canvas whose texels change under an unchanged instance would leave the pane stale. Every
        // route that does that — a stamp, an undo, a redo — goes through `Repaint` with the
        // rectangle it moved, which is the finer-grained half of the same job.
        if (moved) {
            Render();
        }

        Say(text);
    }

    /// <summary>Changes the sentence under the pane and nothing else.</summary>
    /// <param name="text">What to say.</param>
    public void Say(string text) {
        Status = text;
        status.Text = text;
    }

    /// <summary>Puts one dirtied rectangle of an atlas back on the model.</summary>
    /// <param name="picture">
    ///     What the model wears now — during a drag, <c>PaintComposite.Result</c> and not the
    ///     layer's own image.
    /// </param>
    /// <param name="rect">What changed in it, in atlas texels.</param>
    /// <exception cref="ArgumentNullException"><paramref name="picture" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Driven by whoever owns the stroke rather than from inside this view's own
    ///         pointer handler, and that is what makes a stroke made in the <em>other</em> pane show
    ///         up on the model.</b> The two panes are two pictures of one layer; a 3D view that only
    ///         repainted for its own drags would go stale the moment an artist fixed a seam in the
    ///         2D one, which is exactly the pane pairing doc 48 § D13 asks for.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What is handed back through <see cref="Patched" /> is the <em>bounding box of
    ///         the pane pixels that moved</em>, which has no relation to the atlas rectangle that
    ///         came in.</b> A stamp on a chart the model shows twice moves two clumps of pixels far
    ///         apart, and a stamp on a chart nothing is showing moves none at all — which is an
    ///         empty answer and not a failure.
    ///     </para>
    /// </remarks>
    public void Repaint(PaintImage picture, PaintRect rect) {
        ArgumentNullException.ThrowIfNull(picture);

        var moved = raster.Retexture(picture, rect);

        if (!moved.IsEmpty) {
            Patched?.Invoke(moved);
        }
    }

    /// <summary>Redraws if the pane has changed size, and otherwise does nothing at all.</summary>
    /// <returns>Whether anything was redrawn.</returns>
    /// <remarks>
    ///     ⚠ <b>Called once a frame and it must therefore be a comparison rather than a draw.</b> A
    ///     pane that rasterised the model per frame would put the triangle count into the frame loop
    ///     — worse than the per-stamp version doc 48's criterion 8 is about, because it is paid with
    ///     nobody touching the pointer. <c>PaintMeshRaster.Renders</c> is what says which of the two
    ///     this is.
    /// </remarks>
    public bool Tick() {
        if (session is not null || mesh is null) {
            return false;
        }

        var (width, height) = Extent();

        if (width <= 0 || height <= 0 || (width == raster.Width && height == raster.Height)) {
            return false;
        }

        Render();

        return true;
    }

    /// <summary>What the pane would say about itself, for the status line.</summary>
    /// <returns>The sentence.</returns>
    public string Describe() =>
        mesh is null
            ? "No mesh is bound, so there is nothing to aim at. Bind a model in the layers pane."
            : $"{mesh.Triangles} triangles · brush {tool.Brush.Radius:F0} px on screen · symmetry "
            + $"{tool.SymmetryAxis}. Drag to paint, right-drag to orbit, middle-drag to pan, wheel to zoom.";

    /// <summary>How big the picture should be, in its own pixels.</summary>
    /// <returns>The width and height, either of which is zero before the first layout.</returns>
    (int Width, int Height) Extent() => RasterSize((int)Image.Width, (int)Image.Height);

    /// <summary>How many of the picture's pixels one of the pane's layout pixels is worth.</summary>
    /// <remarks>
    ///     One below the cap, and under one above it. ⚠ <b>Read off the rasteriser rather than
    ///     recomputed from <see cref="RasterSize" />, because what a coordinate has to agree with is the
    ///     picture that was actually drawn</b> — a pane resized between a draw and a press would
    ///     otherwise convert against a size nothing on screen has yet.
    /// </remarks>
    float Scale => Image.Height > 0f && raster.Height > 0 ? raster.Height / Image.Height : 1f;

    /// <summary>Rasterises the mesh and hands the whole picture up to be uploaded.</summary>
    void Render() {
        var (width, height) = Extent();

        if (mesh is null || width <= 0 || height <= 0) {
            // ⚠ The extent is cleared rather than left, so the pane stops drawing the previous
            // model. An unbound stack showing the last one's silhouette is the same defect
            // `TexturingModule.Islands` names for the outlines: it looks like information.
            Image.Image = 0ul;
            Image.ImageWidth = 0;
            Image.ImageHeight = 0;

            return;
        }

        raster.Draw(mesh, Camera, width, height);

        if (atlas is not null) {
            raster.Texture(atlas);
        }

        if (raster.Picture is not { } picture) {
            return;
        }

        Presented?.Invoke(picture);

        Image.ImageWidth = picture.Width;
        Image.ImageHeight = picture.Height;

        // ⚠ At one pane pixel per picture pixel *below the cap*, and magnified above it — which is
        // why `ToImage` is what every coordinate goes through rather than the identity it used to
        // be. `Fit` is what makes the two agree either way: it is the inverse of the same zoom and
        // pan `ToImage` divides by.
        Image.Fit();
    }

    /// <summary>The wheel dollies the camera.</summary>
    /// <param name="args">The event.</param>
    /// <remarks>
    ///     ⚠ <b>Never while a stroke is in flight.</b> The footprint is measured at pointer-down and
    ///     cannot move — <c>PaintProjector</c>'s own remarks say why — so a dolly between two stamps
    ///     would leave the brush painting the size it was at a depth it no longer is, and the ray
    ///     would land somewhere else besides.
    /// </remarks>
    void Wheeled(WheelEvent args) {
        // ⚠ Handled before the bail, not after it. The handler is on the capture leg because
        // `ImageView` marks its own pointer events handled — so a wheel this pane declines to
        // act on falls through to `ImageView.Wheeled`, which zooms and pans the *picture*. The
        // pointer-up path never re-fits, so a wheel mid-stroke left the model at a scale and
        // offset nothing puts back.
        args.Handled = true;

        if (session is not null) {
            return;
        }

        Camera.Zoom(-args.DeltaY);
        Render();
        Say(Describe());
    }

    /// <summary>The pointer, on the capture leg, before <c>ImageView</c>'s pan sees it.</summary>
    /// <remarks>
    ///     ⚠ <b>The gesture in flight is checked before the mode is</b>, for the reason
    ///     <c>PaintUvView.Pointed</c> gives: the paint mode is a verb with a shortcut, so it can be
    ///     toggled off mid-drag, and a release that fell through would leave the stroke on the canvas
    ///     with nothing on the undo stack to take it off.
    /// </remarks>
    void Pointed(PointerEvent args) {
        var at = Image.ToImage(args.X, args.Y);

        switch (args.Action) {
            case PointerAction.Pressed when session is null && drag == PaintMeshDrag.None:
                Pressed(args, at);

                break;

            case PointerAction.Moved when session is not null:
                Advance(at);

                break;

            case PointerAction.Moved when drag != PaintMeshDrag.None:
                Turned(args);

                break;

            case PointerAction.Released when session is not null:
                Image.Document.ReleasePointer();
                End();

                break;

            case PointerAction.Released when drag != PaintMeshDrag.None:
                Image.Document.ReleasePointer();

                drag = PaintMeshDrag.None;

                break;

            default:
                return;
        }

        args.Handled = true;
    }

    /// <summary>Pointer-down: a stroke, or a camera gesture.</summary>
    /// <param name="args">The event.</param>
    /// <param name="at">Where it is, in the picture's own pixels.</param>
    /// <remarks>
    ///     ⚠ <b>A primary press that cannot paint becomes an orbit rather than nothing.</b> Every
    ///     reason a stroke is refused — no mesh, no layer, the pointer off the model — leaves an
    ///     artist holding a button over a viewport, and a pane that answered by doing nothing at all
    ///     reads as frozen. It is the same judgement <c>PaintUvView</c> makes by leaving the event
    ///     unhandled so the pane still pans; here there is no second handler to fall through to, so
    ///     the fallback is explicit.
    /// </remarks>
    void Pressed(PointerEvent args, Vector2 at) {
        held = new(args.X, args.Y);

        var orbiting = args.Button != PointerButton.Primary
            || !tool.IsPainting
            || (args.Modifiers & ModifierKeys.Alt) != 0;

        if (!orbiting && Begin(at)) {
            Image.Document.CapturePointer(Image);
            Advance(at);

            return;
        }

        drag = args.Button == PointerButton.Middle || (args.Modifiers & ModifierKeys.Shift) != 0
            ? PaintMeshDrag.Pan
            : PaintMeshDrag.Orbit;

        Image.Document.CapturePointer(Image);
    }

    /// <summary>Pointer-move during a camera gesture.</summary>
    /// <param name="args">The event.</param>
    void Turned(PointerEvent args) {
        var now = new Vector2(args.X, args.Y);
        var moved = now - held;

        held = now;

        // ⚠ The pane's height and not the rasteriser's, and the two differ once `RasterSize` caps the
        // picture. Both of these take a delta the hand made in *layout* pixels and both mean "a drag
        // of the pane's own height is half a turn" — measured against the capped picture instead,
        // an orbit on a maximised 4K pane would turn two and a half times as far as the same gesture
        // on a docked one, which reads as a viewport with a mind of its own.
        var pane = (int)Image.Height;

        if (drag == PaintMeshDrag.Pan) {
            Camera.Pan(moved.X, -moved.Y, pane);
        } else {
            Camera.Orbit(moved.X, -moved.Y, pane);
        }

        Render();
    }

    /// <summary>Pointer-down that means a stroke: the ray, the footprint and the session.</summary>
    /// <param name="at">Where the pointer is, in the picture's own pixels.</param>
    /// <returns>Whether a stroke started.</returns>
    /// <remarks>
    ///     ⚠ <b>The footprint is put on the brush before the session is begun and never after.</b>
    ///     <c>PaintSession.Begin</c> takes the brush by value, so a radius written onto
    ///     <c>PaintTool.Brush</c> afterwards would be a number the stroke never saw — and the stroke
    ///     would paint at whatever the 2D pane's texel radius happened to be, which on a stretched
    ///     chart is the defect <a href="https://github.com/Rikarin/Vixen/issues/1064">#1064</a> is
    ///     about with no symptom to find it by.
    /// </remarks>
    bool Begin(Vector2 at) {
        if (mesh is null || atlas is null) {
            Say(Describe());

            return false;
        }

        var aimed = new PaintProjector(mesh, atlas.Width, atlas.Height) { Symmetry = tool.Symmetry };
        var ray = Camera.Ray(at.X, at.Y, raster.Width, raster.Height);

        // ⚠ Into the picture's pixels, because that is the resolution the eye and the ray are in.
        // `PaintBrush.Radius` is what the artist set and means pixels *on screen*; a capped picture
        // is magnified to the pane, so the same disc on screen is fewer of its pixels. Handing the
        // unconverted number over would grow the brush with the pane's size, which is a brush that
        // changes when the panel is undocked.
        if (!aimed.Begin(Camera.Eye(raster.Height), ray, tool.Brush.Radius * Scale, out var footprint)
            || !footprint.IsMeasurable) {
            Say("The pointer is not on the model, or the triangle under it carries no usable layout.");

            return false;
        }

        if (Target?.Invoke() is not { } target) {
            return false;
        }

        projector = aimed;
        session = PaintSession.Begin(
            target,
            tool.Brush with {
                Radius = footprint.Radius,
                Aspect = footprint.Aspect,
                AspectAngle = footprint.Angle
            },
            tool.Colour,
            tool.Smoothing
        );

        Live = session.Composite;

        Say($"Painting: {footprint.Radius:F1} texels from {tool.Brush.Radius:F0} px, "
            + $"aspect {footprint.Aspect:F2}. {Describe()}");

        return true;
    }

    /// <summary>Pointer-move during a stroke: recast, stamp, and repaint what that moved.</summary>
    /// <param name="at">Where the pointer is, in the picture's own pixels.</param>
    void Advance(Vector2 at) {
        if (session is null || projector is null) {
            return;
        }

        var ray = Camera.Ray(at.X, at.Y, raster.Width, raster.Height);

        // ⚠ The overload that hands back each stamp's own rectangle rather than their union, for
        // `PaintUvView`'s reason and one of its own: a mirrored pair lands on opposite sides of the
        // atlas, and their bounding box is a square spanning everything between — which through this
        // pane's index is every pane pixel showing anything in between as well.
        session.MoveAll(projector.Resolve(ray), dirtied);

        foreach (var rect in dirtied) {
            Painted?.Invoke(rect);
        }
    }

    /// <summary>Pointer-up: the drag becomes one undo entry.</summary>
    void End() {
        if (session is null) {
            return;
        }

        var finished = session;

        session = null;
        projector = null;

        if (finished.End(
                "Paint stroke",
                rect => {
                    Painted?.Invoke(rect);
                    Reverted?.Invoke();
                }
            ) is not { } command) {
            Say(Describe());

            return;
        }

        Finished?.Invoke(command);
        Say(Describe());
    }
}

/// <summary>Which camera gesture a pointer drag in the 3D pane is.</summary>
enum PaintMeshDrag {
    /// <summary>None — the pointer is up, or it is painting.</summary>
    None,

    /// <summary>Turning the camera around the model.</summary>
    Orbit,

    /// <summary>Sliding the model across the view plane.</summary>
    Pan
}
