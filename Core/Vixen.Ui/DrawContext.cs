// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Ui;

/// <summary>What an element is given to draw itself with.</summary>
/// <remarks>
///     <para>
///         The escape hatch out of the declarative side. A stylesheet describes boxes, and most of an
///         interface is boxes; a chart, a sparkline, a knob and a hand-drawn icon are not, and there
///         is no <c>font-family</c> for those. <see cref="UiElement.OnDraw" /> is where a control
///         draws whatever it is, and this is the surface it draws on.
///     </para>
///     <para>
///         Everything is in <b>document space</b>, the same as the rest of the draw list.
///         <see cref="Bounds" /> is where the element ended up, so a control that wants to draw
///         inside itself starts from there rather than from an origin it has to be handed.
///     </para>
///     <para>
///         A struct passed by value, not a class handed out per element per frame. It is three
///         references' worth of state and it exists for the length of one virtual call.
///     </para>
/// </remarks>
public readonly struct DrawContext {
    readonly float alpha;

    internal DrawContext(UiElement element, DrawList list, float alpha = 1f) {
        Element = element;
        List = list;
        this.alpha = alpha;
    }

    /// <summary>The element being drawn.</summary>
    public UiElement Element { get; }

    /// <summary>The list being filled, for anything this does not wrap.</summary>
    public DrawList List { get; }

    /// <summary>Where the element is, in document space.</summary>
    public Rectangle Bounds => Element.Bounds;

    /// <summary>The element's <c>color</c>, which is what a control draws itself in by default.</summary>
    /// <remarks>
    ///     <para>
    ///         Here rather than left to the caller because <i>every</i> custom-drawn control needs it
    ///         and the alternative is each of them interning a property name and parsing a value. A
    ///         control that wants a second colour — a slider's track against its fill — reads it
    ///         through <see cref="UiDocument.ColorOf" /> with an identifier it interned once.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not faded by <c>opacity</c>.</b> Every method here fades what it is given on the
    ///         way out, so a colour that arrived already faded would be faded twice — and a control
    ///         is free to hand these methods a colour that never came from this property at all.
    ///     </para>
    /// </remarks>
    public Color4 Foreground => Element.Document.ForegroundOf(Element);

    /// <summary>Fills a path.</summary>
    /// <param name="path">The path.</param>
    /// <param name="color">What to fill it with.</param>
    /// <param name="rule">How to decide what is inside it.</param>
    public void Fill(PathBuilder path, Color4 color, PathFillRule rule = PathFillRule.NonZero) {
        ArgumentNullException.ThrowIfNull(path);

        if (path.Count == 0) {
            return;
        }

        List.Add(
            new DrawCommand(DrawCommandKind.Path, 0f, 0f, 0f, 0f, DrawListBuilder.Fade(color, alpha), 0f, 0f) {
                Offset = List.AddPath(path),
                Length = path.Count,
                FillRule = rule
            }
        );
    }

    /// <summary>Strokes a path.</summary>
    /// <param name="path">The path.</param>
    /// <param name="color">What to draw it in.</param>
    /// <param name="thickness">How wide, in device-independent pixels.</param>
    /// <param name="join">How corners are turned.</param>
    /// <param name="cap">How open ends are finished.</param>
    /// <param name="miterLimit">
    ///     How far a miter may reach, as a multiple of the half width. Zero takes the default of four.
    /// </param>
    /// <remarks>
    ///     A separate command from <see cref="Fill" /> rather than a flag on one, so that a shape
    ///     that is both filled and stroked is two commands over the same range of the path buffer —
    ///     which is what a renderer wants anyway, since the two are different draws.
    /// </remarks>
    public void Stroke(
        PathBuilder path,
        Color4 color,
        float thickness,
        LineJoin join = LineJoin.Miter,
        LineCap cap = LineCap.Butt,
        float miterLimit = 0f
    ) {
        ArgumentNullException.ThrowIfNull(path);

        if (path.Count == 0 || thickness <= 0f) {
            return;
        }

        List.Add(
            new DrawCommand(DrawCommandKind.PathStroke, 0f, 0f, 0f, 0f, DrawListBuilder.Fade(color, alpha), 0f, thickness) {
                Offset = List.AddPath(path),
                Length = path.Count,
                Join = join,
                Cap = cap,
                MiterLimit = miterLimit
            }
        );
    }

    /// <summary>Fills a rectangle, with optional rounded corners.</summary>
    /// <param name="rectangle">Where.</param>
    /// <param name="color">What colour.</param>
    /// <param name="radius">Its corner radius.</param>
    /// <remarks>
    ///     The same command a background produces, because a rectangle is common enough that making
    ///     every control express one as a four-segment path would cost a path buffer entry and a
    ///     tessellation to draw something the renderer already has a fast case for.
    /// </remarks>
    public void FillRectangle(Rectangle rectangle, Color4 color, float radius = 0f) =>
        List.Add(
            new DrawCommand(
                DrawCommandKind.Rectangle,
                rectangle.X,
                rectangle.Y,
                rectangle.Width,
                rectangle.Height,
                DrawListBuilder.Fade(color, alpha),
                radius,
                0f
            )
        );

    /// <summary>Draws a texture over a rectangle.</summary>
    /// <param name="rectangle">Where.</param>
    /// <param name="image">The renderer's name for the texture. Zero draws nothing.</param>
    /// <param name="tint">What to multiply it by. White leaves it alone.</param>
    /// <param name="source">Which part of the texture, in UVs. The whole of it by default.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The image is a number this assembly cannot interpret</b>, exactly as
    ///         <c>Viewport.RenderTarget</c> is — a texture view belongs to <c>Vixen.Graphics</c> and
    ///         <c>Vixen.Ui</c> does not reference it. The renderer registers a number for a texture it
    ///         owns; everything between here and there passes it along.
    ///     </para>
    ///     <para>
    ///         The tint goes through the element's own opacity like every other colour, so an image
    ///         inside a fading panel fades with it rather than staying solid until the panel vanishes.
    ///     </para>
    /// </remarks>
    public void DrawImage(Rectangle rectangle, ulong image, Color4 tint = default, Rectangle source = default) =>
        List.Add(
            new DrawCommand(
                DrawCommandKind.Image,
                rectangle.X,
                rectangle.Y,
                rectangle.Width,
                rectangle.Height,
                DrawListBuilder.Fade(tint == default ? Color4.White : tint, alpha),
                0f,
                0f
            ) {
                Image = image,

                // `default` is an empty rectangle and not the whole texture, so the caller who wrote
                // nothing gets the whole of it rather than a zero-area sample of its top-left texel.
                Source = source == default ? new Rectangle(0f, 0f, 1f, 1f) : source
            }
        );

    /// <summary>Fills a rectangle with per-corner radii, a gradient, or both.</summary>
    /// <param name="rectangle">Where.</param>
    /// <param name="color">Its colour — the near end of the gradient, if it has one.</param>
    /// <param name="style">The corners and the gradient.</param>
    /// <remarks>
    ///     A separate overload rather than an optional parameter on the common one, because this is
    ///     the path that costs a side-buffer entry and the other is not. A caller that wants one
    ///     radius should not be paying for a record it filled with zeroes.
    /// </remarks>
    public void FillRectangle(Rectangle rectangle, Color4 color, BoxStyle style) =>
        List.Add(
            new DrawCommand(
                DrawCommandKind.Rectangle,
                rectangle.X,
                rectangle.Y,
                rectangle.Width,
                rectangle.Height,
                DrawListBuilder.Fade(color, alpha),
                0f,
                0f
            ) {
                Offset = List.AddBox(style),
                Length = 1
            }
        );

    /// <summary>Draws a border inside a rectangle's edges, with per-corner radii.</summary>
    /// <param name="rectangle">Where.</param>
    /// <param name="color">What colour.</param>
    /// <param name="thickness">How wide the band is, drawn inwards from the edge.</param>
    /// <param name="style">The corners. A gradient on a border runs along the same axis as a fill's.</param>
    public void StrokeRectangle(Rectangle rectangle, Color4 color, float thickness, BoxStyle style = default) =>
        List.Add(
            new DrawCommand(
                DrawCommandKind.Border,
                rectangle.X,
                rectangle.Y,
                rectangle.Width,
                rectangle.Height,
                DrawListBuilder.Fade(color, alpha),
                0f,
                thickness
            ) {
                Offset = List.AddBox(style),
                Length = 1
            }
        );
}
