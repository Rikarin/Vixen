// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Input;
using Vixen.Ui.Styling;

namespace Vixen.Ui.Controls.Advanced;

/// <summary>Which channels of an image a view is asking to be shown.</summary>
/// <remarks>
///     <para>
///         <b>One choice rather than a set of flags</b>, unlike the texture importer's
///         <c>TextureChannels</c>. That one answers "what does this look like without its alpha",
///         which is three channels at once; this one answers "what is <i>in</i> the green channel",
///         which is one at a time and is the question a texturing tool is asked all day. A tool that
///         needs both offers two controls rather than one enum meaning two things.
///     </para>
/// </remarks>
public enum ImageChannels {
    /// <summary>Colour, with the alpha showing the chequerboard through it. What a texture is.</summary>
    Rgb,

    /// <summary>Red alone.</summary>
    Red,

    /// <summary>Green alone.</summary>
    Green,

    /// <summary>Blue alone.</summary>
    Blue,

    /// <summary>Alpha alone, as a grey.</summary>
    Alpha
}

/// <summary>Which transfer function a view is asking the pixels be shown through.</summary>
/// <remarks>
///     ⚠ <b>The two commonest "why does this look wrong" moments in a texturing tool are one
///     mislabelled texture apart</b>, and they look like different bugs: an sRGB texture shown as
///     linear is washed out and flat, and a linear one shown as sRGB is dark and contrasty. Neither
///     is a defect in the pixels, and neither can be told from a defect in the pixels by looking at
///     one of them. A toggle settles it in one click, which is the whole reason it is a control's
///     property rather than a fact read off the file.
/// </remarks>
public enum ImageColorSpace {
    /// <summary>Shown as authored: the sRGB transfer function applied on the way to the screen.</summary>
    Srgb,

    /// <summary>Shown as stored, with no transfer function — what a normal map or a roughness map is.</summary>
    Linear
}

/// <summary>What an <see cref="ImageView" /> is asking to be shown.</summary>
/// <param name="Channels">Which channels.</param>
/// <param name="ColorSpace">Through which transfer function.</param>
/// <remarks>
///     A record rather than two loose arguments so a host can key a cache of prepared images on it
///     and compare with <c>==</c>.
/// </remarks>
public readonly record struct ImageViewRequest(ImageChannels Channels, ImageColorSpace ColorSpace);

/// <summary>One line segment drawn over an image, in image space.</summary>
/// <param name="From">One end, in texels from the image's top-left corner.</param>
/// <param name="To">The other.</param>
/// <remarks>
///     ⚠ <b>Texels, not UVs and not pixels.</b> UVs would mean a UV island's outline was right and a
///     brush's footprint was not — a paint stroke is measured in texels and a stroke expressed as a
///     fraction would change shape on a non-square image. Screen pixels would mean every segment had
///     to be recomputed on every pan and every zoom, which is exactly the arithmetic the control
///     already does once.
/// </remarks>
public readonly record struct ImageOverlaySegment(Vector2 From, Vector2 To);

/// <summary>A 2D image at zoom: pan, zoom about the pointer, fit, channels and an overlay.</summary>
/// <remarks>
///     <para>
///         Doc 48 § B6: nothing in the editor views an image at zoom. <c>TexturePreview</c> shows an
///         imported texture at one size and <c>NodePreview</c> draws a swatch under a node; the
///         texture graph, the layer stack and the 2D paint view all need the same pannable,
///         zoomable, channel-isolating pane, and building it three times is how three of them end up
///         disagreeing about which way y goes.
///     </para>
///     <para>
///         <b>The pixels belong to the host, exactly as <c>Viewport</c>'s do.</b>
///         <see cref="Image" /> is a number this assembly cannot interpret — the same number
///         <c>UiRenderer.RegisterImage</c> was given — and <see cref="ImageWidth" /> and
///         <see cref="ImageHeight" /> are what make it a *coordinate space* rather than a rectangle.
///         Without the extent there is no texel to zoom about, no fit, and no image space for an
///         overlay to be in.
///     </para>
///     <para>
///         ⚠ <b><see cref="Channels" /> and <see cref="ColorSpace" /> are a <i>request</i>, not a
///         filter, and this is the one thing about this control worth reading twice.</b> The draw
///         list's image command carries a tint and a source rectangle and nothing else: a tint can
///         multiply, and neither isolating the alpha as a grey nor applying a transfer function is a
///         multiply. So the control does not touch the pixels — it says what it wants through
///         <see cref="ViewChanged" /> and draws whatever <see cref="Image" /> it is then given. A
///         control that quietly tinted red for <see cref="ImageChannels.Red" /> and did nothing at
///         all for <see cref="ImageChannels.Alpha" /> would be worse than one that does neither,
///         because the reader could not tell which of the two they were looking at. It is the same
///         bargain <c>TextureImportView.ViewChanged</c> already makes, one assembly up.
///     </para>
///     <para>
///         ⚠ <b>The chequerboard is in <i>screen</i> pixels and bounded to the visible part of the
///         image.</b> Fixed-size squares are the only thing that reads as transparency at any zoom —
///         squares that scaled with the image would be a pattern an author would take for content —
///         and drawing it only where the image actually is means a transparent texture reads as
///         see-through rather than as a hole, without the surrounding pane pretending to be
///         see-through too.
///     </para>
/// </remarks>
public sealed partial class ImageView : Control {
    readonly PathBuilder path = new();

    int lightId;
    int darkId;
    int overlayId;

    bool dragging;
    Vector2 previous;

    /// <inheritdoc />
    protected override string TagName => "image-view";

    /// <inheritdoc />
    protected override bool AcceptsFocus => true;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>ARIA <c>application</c>, for the reason <c>Viewport</c>, <c>NodeCanvas</c>,
    ///     <c>CurveEditor</c>, <c>GradientEditor</c> and <c>Timeline</c> are.</b> It asks assistive
    ///     technology to stop intercepting the keyboard and pass every key through, which is right
    ///     for a direct-manipulation surface with a keyboard model no widget vocabulary describes,
    ///     and it is a cost — so it is paid only where that is true.
    /// </remarks>
    protected override AccessibleRole NativeRole => AccessibleRole.Application;

    /// <summary>The renderer's name for the texture being viewed. Zero draws nothing.</summary>
    /// <remarks>
    ///     <para>
    ///         Opaque on purpose, and for <c>Viewport.RenderTarget</c>'s reason: a texture view
    ///         belongs to <c>Vixen.Graphics</c> and this assembly does not reference it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A number the renderer does not know draws nothing at all</b> — not a placeholder
    ///         and not another texture. An image view that went blank after a re-import is a texture
    ///         that was recreated and not re-registered.
    ///     </para>
    /// </remarks>
    public ulong Image { get; set; }

    /// <summary>How wide the image is, in texels.</summary>
    /// <remarks>
    ///     ⚠ <b>Zero means "nothing to show", and it is checked rather than assumed.</b> The extent
    ///     is the denominator of every coordinate this control computes, so a view handed a handle
    ///     and no extent would divide by zero on the first fit — and a view handed an extent and no
    ///     handle still draws its chequerboard, which is what an empty layer should look like.
    /// </remarks>
    public int ImageWidth { get; set; }

    /// <summary>How tall it is, in texels.</summary>
    public int ImageHeight { get; set; }

    /// <summary>The image point at the view's top-left corner, in texels.</summary>
    /// <remarks>
    ///     ⚠ <b>The corner rather than the centre</b>, which is <c>NodeCanvas.Pan</c>'s choice and is
    ///     made here for the same reason: a centre-anchored pan has to know the box's size to be
    ///     applied, so a pan written before the first layout would land somewhere else once the box
    ///     existed.
    /// </remarks>
    [UiProperty]
    public partial Vector2 Pan { get; set; }

    /// <summary>How many screen pixels one texel is.</summary>
    [UiProperty(Default = 1f, Coerce = nameof(ClampZoom))]
    public partial float Zoom { get; set; }

    /// <summary>The furthest out the view may get.</summary>
    [UiProperty(Default = 0.02f)]
    public partial float MinimumZoom { get; set; }

    /// <summary>The closest it may get.</summary>
    /// <remarks>
    ///     Deliberately far past one-to-one: inspecting a seam, a mask's edge or a single painted
    ///     texel is what a texturing tool is for, and a view that stopped at 100% would make the
    ///     interesting case the one it refuses.
    /// </remarks>
    [UiProperty(Default = 64f)]
    public partial float MaximumZoom { get; set; }

    /// <summary>Which channels are being asked for.</summary>
    [UiProperty(Default = ImageChannels.Rgb, Changed = nameof(OnRequestChanged))]
    public partial ImageChannels Channels { get; set; }

    /// <summary>Which transfer function is being asked for.</summary>
    [UiProperty(Default = ImageColorSpace.Srgb, Changed = nameof(OnRequestChanged))]
    public partial ImageColorSpace ColorSpace { get; set; }

    /// <summary>Whether the chequerboard is drawn under the image.</summary>
    [UiProperty(Default = true)]
    public partial bool ShowCheckerboard { get; set; }

    /// <summary>How big one chequerboard square is, in screen pixels.</summary>
    [UiProperty(Default = 12f, Coerce = nameof(ClampSquare))]
    public partial float CheckerSize { get; set; }

    /// <summary>How thick an overlay segment is drawn, in screen pixels.</summary>
    /// <remarks>
    ///     Screen pixels rather than texels, because an island outline is a <i>label</i> on the
    ///     picture rather than a part of it: one that thickened with the zoom would swallow the seam
    ///     it is pointing at.
    /// </remarks>
    [UiProperty(Default = 1f)]
    public partial float OverlayThickness { get; set; }

    /// <summary>The line segments drawn over the image, in image space.</summary>
    /// <remarks>
    ///     A plain list an owner mutates rather than a property that replaces one — <c>NodeCanvas</c>
    ///     makes the same choice for the same reason. A UV island is thousands of segments recomputed
    ///     when a mesh changes and not when a pointer moves, so allocating a list per frame would be
    ///     a per-frame cost in a control whose arithmetic exists to avoid them.
    /// </remarks>
    public List<ImageOverlaySegment> Overlay { get; } = [];

    /// <summary>What is being asked for: the pair a host prepares an image from.</summary>
    public ImageViewRequest Requested => new(Channels, ColorSpace);

    /// <summary>Raised when <see cref="Channels" /> or <see cref="ColorSpace" /> moved.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the only way either of those two reaches the picture.</b> See the type's own
    ///     remarks: the draw list can multiply by a tint and cannot swizzle or apply a transfer
    ///     function, so a host that wants the toggles to mean something answers this by preparing a
    ///     different texture and writing <see cref="Image" />. A host that ignores it gets a view
    ///     whose toggles change nothing, which is at least a state the reader can see.
    /// </remarks>
    public event Action<ImageView>? ViewChanged;

    /// <summary>Where the image lands, in document space.</summary>
    /// <remarks>Empty when there is no extent, which is what a view of nothing should measure as.</remarks>
    public Rectangle ImageBounds =>
        ImageWidth <= 0 || ImageHeight <= 0
            ? default
            : new Rectangle(
                AbsoluteLeft - (Pan.X * Zoom),
                AbsoluteTop - (Pan.Y * Zoom),
                ImageWidth * Zoom,
                ImageHeight * Zoom
            );

    /// <summary>Where an image point is, in document space.</summary>
    /// <param name="point">The point, in texels.</param>
    /// <returns>Where it is on screen.</returns>
    public Vector2 ToScreen(Vector2 point) =>
        new(AbsoluteLeft + ((point.X - Pan.X) * Zoom), AbsoluteTop + ((point.Y - Pan.Y) * Zoom));

    /// <summary>Where a document-space point is, in texels.</summary>
    /// <param name="x">Its x, in document space.</param>
    /// <param name="y">Its y.</param>
    /// <returns>The image point. Outside the image's extent when the pointer is off it.</returns>
    public Vector2 ToImage(float x, float y) =>
        new(Pan.X + ((x - AbsoluteLeft) / Zoom), Pan.Y + ((y - AbsoluteTop) / Zoom));

    /// <summary>Zooms and pans so the whole image is visible and centred.</summary>
    /// <returns>Whether anything moved.</returns>
    /// <remarks>
    ///     ⚠ <b>Answers false rather than throwing when there is nothing to fit</b>, which is the
    ///     frame before the first layout, a collapsed dock panel and a hidden tab. A fit computed
    ///     against a zero-sized box is a zoom of zero, and a zoom of zero is a coordinate space with
    ///     no inverse: every later <see cref="ToImage" /> would answer infinity.
    /// </remarks>
    public bool Fit() {
        if (ImageWidth <= 0 || ImageHeight <= 0 || Width <= 0f || Height <= 0f) {
            return false;
        }

        Zoom = MathF.Min(Width / ImageWidth, Height / ImageHeight);

        // Centred: half of whatever the view has left over, expressed in texels so it survives the
        // next zoom unchanged.
        Pan = new Vector2(
            (ImageWidth - (Width / Zoom)) * 0.5f,
            (ImageHeight - (Height / Zoom)) * 0.5f
        );

        return true;
    }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        lightId = Document.PropertyId("--image-checker-light");
        darkId = Document.PropertyId("--image-checker-dark");
        overlayId = Document.PropertyId("--image-overlay-color");

        AddHandler<PointerEvent>(static (element, args) => ((ImageView) element).Pointed(args));
        AddHandler<WheelEvent>(static (element, args) => ((ImageView) element).Wheeled(args));
    }

    /// <inheritdoc />
    protected override void OnDraw(DrawContext context) {
        base.OnDraw(context);

        if (ImageWidth <= 0 || ImageHeight <= 0) {
            return;
        }

        var bounds = context.Bounds;
        var image = ImageBounds;
        var visible = Intersect(bounds, image);

        if (visible.Width <= 0f || visible.Height <= 0f) {
            return;
        }

        if (ShowCheckerboard) {
            Chequer(context, visible);
        }

        if (Image != 0) {
            context.DrawImage(image, Image);
        }

        Segments(context);
    }

    /// <summary>The chequerboard, in screen-space squares, over the visible part of the image.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Not <c>ColorStrip.Chequer</c>, and the difference is a draw-command count.</b>
    ///         That one fills whatever rectangle it is given with five-pixel squares, which is right
    ///         for a 200-pixel alpha band and is nineteen thousand rectangles over a docked pane.
    ///         Here the squares are larger and the region is the part of the <i>image</i> that is on
    ///         screen, so the count is bounded by the pane rather than by the image — a 16k texture
    ///         at 100% costs exactly what a 64-pixel one does.
    ///     </para>
    ///     <para>
    ///         The light half is one rectangle and only the dark squares are drawn, which halves it
    ///         again.
    ///     </para>
    /// </remarks>
    void Chequer(DrawContext context, Rectangle visible) {
        var light = Document.ColorOf(Style, lightId) ?? new Color4(0.32f, 0.32f, 0.34f, 1f);
        var dark = Document.ColorOf(Style, darkId) ?? new Color4(0.24f, 0.24f, 0.26f, 1f);

        context.FillRectangle(visible, light);

        var square = CheckerSize;

        var right = visible.X + visible.Width;
        var bottom = visible.Y + visible.Height;

        // ⚠ Indexed off the *view's* corner rather than the image's, so a pan does not make the
        // pattern crawl under a stationary image. It is a backdrop and not content, and content is
        // exactly what a moving pattern would read as.
        var firstColumn = (int) MathF.Floor((visible.X - AbsoluteLeft) / square);
        var firstRow = (int) MathF.Floor((visible.Y - AbsoluteTop) / square);

        for (var row = firstRow; AbsoluteTop + (row * square) < bottom; row++) {
            for (var column = firstColumn; AbsoluteLeft + (column * square) < right; column++) {
                if (((column + row) & 1) == 0) {
                    continue;
                }

                var x = MathF.Max(AbsoluteLeft + (column * square), visible.X);
                var y = MathF.Max(AbsoluteTop + (row * square), visible.Y);
                var width = MathF.Min(AbsoluteLeft + ((column + 1) * square), right) - x;
                var height = MathF.Min(AbsoluteTop + ((row + 1) * square), bottom) - y;

                if (width > 0f && height > 0f) {
                    context.FillRectangle(new Rectangle(x, y, width, height), dark);
                }
            }
        }
    }

    /// <summary>The overlay's segments, projected through the same arithmetic as the image.</summary>
    /// <remarks>
    ///     ⚠ <b>One path for the whole set rather than one stroke per segment.</b> A UV island is
    ///     thousands of edges, and a command each would be thousands of batches for a picture made of
    ///     lines. The builder is kept and cleared rather than rebuilt, the way <c>ViewportGizmo</c>
    ///     keeps one.
    /// </remarks>
    void Segments(DrawContext context) {
        if (Overlay.Count == 0 || OverlayThickness <= 0f) {
            return;
        }

        var colour = Document.ColorOf(Style, overlayId) ?? new Color4(1f, 0.62f, 0.19f, 1f);

        path.Clear();

        foreach (var segment in Overlay) {
            path.MoveTo(ToScreen(segment.From)).LineTo(ToScreen(segment.To));
        }

        context.Stroke(path, colour, OverlayThickness);
    }

    static Rectangle Intersect(Rectangle left, Rectangle right) {
        var x = MathF.Max(left.X, right.X);
        var y = MathF.Max(left.Y, right.Y);
        var width = MathF.Min(left.X + left.Width, right.X + right.Width) - x;
        var height = MathF.Min(left.Y + left.Height, right.Y + right.Height) - y;

        return width <= 0f || height <= 0f ? default : new Rectangle(x, y, width, height);
    }

    float ClampZoom(float value) => Math.Clamp(value, MathF.Max(0.0001f, MinimumZoom), MathF.Max(0.0001f, MaximumZoom));

    static float ClampSquare(float value) => MathF.Max(1f, value);

    void OnRequestChanged(ImageChannels previous, ImageChannels current) => ViewChanged?.Invoke(this);

    void OnRequestChanged(ImageColorSpace previous, ImageColorSpace current) => ViewChanged?.Invoke(this);

    /// <summary>Zooms about the pointer, so the texel under it stays under it.</summary>
    /// <remarks>
    ///     ⚠ <b>About the pointer rather than the centre</b>, which is <c>NodeCanvas.Wheeled</c>'s
    ///     rule and matters more here: approaching a seam by zooming about the centre is a zoom
    ///     followed by a pan to put the seam back, every time, and that is what makes a view feel
    ///     like it is fighting the user.
    /// </remarks>
    void Wheeled(WheelEvent args) {
        var before = ToImage(args.X, args.Y);

        Zoom *= MathF.Exp(-args.DeltaY * 0.0015f);

        var after = ToImage(args.X, args.Y);
        Pan += before - after;

        args.Handled = true;
    }

    void Pointed(PointerEvent args) {
        switch (args.Action) {
            case PointerAction.Pressed:
                dragging = true;
                previous = new Vector2(args.X, args.Y);

                Document.Focus(this);
                Document.CapturePointer(this);

                break;

            case PointerAction.Moved when dragging:
                // ⚠ Against the pointer's *screen* delta divided by the zoom, not against the
                // difference of two `ToImage` calls. The second reads `Pan`, which this line is about
                // to write, so it measures against a view that has already moved — a drag that
                // accelerates as it goes and never lands where it was let go.
                Pan -= new Vector2(args.X - previous.X, args.Y - previous.Y) / Zoom;
                previous = new Vector2(args.X, args.Y);

                break;

            case PointerAction.Released when dragging:
                dragging = false;
                Document.ReleasePointer();

                break;

            default:
                return;
        }

        args.Handled = true;
    }
}
