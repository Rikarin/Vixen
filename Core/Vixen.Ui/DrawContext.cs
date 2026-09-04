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

    /// <summary>Fills a small path as a cached distance field, rather than tessellating it.</summary>
    /// <param name="path">The path, <b>in its own local coordinates</b>.</param>
    /// <param name="origin">Where those coordinates are, on the surface.</param>
    /// <param name="color">What to fill it with.</param>
    /// <param name="rule">How to decide what is inside it.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The path is local and the position is separate, and that is not a convenience —
    ///         it is what makes the cache work.</b> The field is keyed on the geometry, so a path
    ///         handed over in absolute coordinates is a different path every time whatever is drawing
    ///         it moves: a tree scrolled by a fraction of a pixel would re-encode every icon in it,
    ///         every frame, at milliseconds each. This is the arrangement a glyph run already uses —
    ///         the command carries where the line starts and the glyphs carry offsets along it — and
    ///         it is why two identical labels in different places are recognised as the same.
    ///     </para>
    ///     <para>
    ///         <b>For vector art: an icon, a glyph a control draws itself, a marker repeated down a
    ///         list.</b> The path is rasterised into the glyph atlas once and drawn as four vertices
    ///         thereafter, where <see cref="Fill" /> tessellates it into as many triangles as its shape
    ///         needs — which for a hand-drawn icon is hundreds, every frame, per instance.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A claim about the path, not a performance switch.</b> What this asserts is that the
    ///         path is <i>small artwork</i>: something an atlas cell can hold at a resolution nobody
    ///         will look past. A chart's area fill or a spline overlay is neither, and asking for one
    ///         here gets it refused and tessellated — no worse than <see cref="Fill" />, but the
    ///         refusal is the honest answer rather than a blurry picture.
    ///     </para>
    ///     <para>
    ///         Identical output either way, to within the antialiasing: a filled path's edge is
    ///         resolved by a fringe the tessellator builds, and a field's by the shader.
    ///     </para>
    /// </remarks>
    public void FillField(
        PathBuilder path,
        Vector2 origin,
        Color4 color,
        PathFillRule rule = PathFillRule.NonZero
    ) {
        ArgumentNullException.ThrowIfNull(path);

        if (path.Count == 0) {
            return;
        }

        List.Add(
            new DrawCommand(
                DrawCommandKind.Field,
                origin.X,
                origin.Y,
                0f,
                0f,
                DrawListBuilder.Fade(color, alpha),
                0f,
                0f
            ) {
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
    /// <param name="origin">
    ///     Where the path's coordinates are, for a caller drawing in a local space. Zero — the default
    ///     — means the path is already where it is going, which is what every caller before
    ///     <see cref="FillField" /> existed meant.
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
        float miterLimit = 0f,
        Vector2 origin = default
    ) {
        ArgumentNullException.ThrowIfNull(path);

        if (path.Count == 0 || thickness <= 0f) {
            return;
        }

        List.Add(
            new DrawCommand(DrawCommandKind.PathStroke, origin.X, origin.Y, 0f, 0f, DrawListBuilder.Fade(color, alpha), 0f, thickness) {
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

    /// <summary>Draws a texture over a rectangle with its borders held at their own size.</summary>
    /// <param name="rectangle">Where.</param>
    /// <param name="image">The renderer's name for the texture. Zero draws nothing.</param>
    /// <param name="border">How far in the corners reach, in document pixels.</param>
    /// <param name="sourceBorder">The same cut of the texture, in UVs.</param>
    /// <param name="tint">What to multiply it by. White leaves it alone.</param>
    /// <param name="source">Which part of the texture, in UVs. The whole of it by default.</param>
    /// <param name="hollowCentre">Whether to leave the middle cell undrawn.</param>
    /// <remarks>
    ///     <para>
    ///         The one thing a stretched image cannot do: a panel, a button and a tooltip drawn from
    ///         one small texture at any size, with the rounded corners the same size in every one of
    ///         them. Nine quads rather than one, in the same batch as the images around them — see
    ///         <see cref="DrawCommand.Slice" /> for why it is not a command kind of its own.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two insets, and the second one is in UVs.</b> A border is a distance on the
    ///         screen and a distance into the texture at once, and those are the same number only
    ///         when the image is drawn at exactly its own pixel size. Converting from one to the other
    ///         needs the texture's dimensions, which is the one thing <c>Vixen.Ui</c> refuses to know
    ///         — so the caller who registered the texture is the one who divides.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Stretched, never tiled.</b> A tiled edge repeats the source a whole number of
    ///         times, and how many is a function of the texture's size in texels — the same thing
    ///         this layer does not know. Tiling belongs where the pixel size does, which is
    ///         <c>Vixen.Rendering</c>'s <c>SpriteGeometry</c>.
    ///     </para>
    /// </remarks>
    public void DrawNineSlice(
        Rectangle rectangle,
        ulong image,
        NineSlice border,
        NineSlice sourceBorder,
        Color4 tint = default,
        Rectangle source = default,
        bool hollowCentre = false
    ) =>
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
                Source = source == default ? new Rectangle(0f, 0f, 1f, 1f) : source,
                Slice = border,
                SourceSlice = sourceBorder,
                HollowCentre = hollowCentre
            }
        );

    /// <summary>Fills a rectangle with per-corner radii, a gradient, or both.</summary>
    /// <param name="rectangle">Where.</param>
    /// <param name="color">Its colour — the near end of the gradient, if it has one.</param>
    /// <param name="style">The corners and the gradient.</param>
    /// <remarks>
    ///     A separate overload rather than an optional parameter on the common one, because this is
    ///     the path that <i>can</i> cost a side-buffer entry and the other never does. A caller that
    ///     wants one radius should not be paying for a record it filled with zeroes — ⚠ and for a
    ///     long time it did anyway: the body wrote an entry unconditionally while this sentence said
    ///     it did not. See <see cref="Styled" />, which is where the intent is now honoured.
    /// </remarks>
    public void FillRectangle(Rectangle rectangle, Color4 color, BoxStyle style) =>
        List.Add(
            Styled(
                new DrawCommand(
                    DrawCommandKind.Rectangle,
                    rectangle.X,
                    rectangle.Y,
                    rectangle.Width,
                    rectangle.Height,
                    DrawListBuilder.Fade(color, alpha),
                    0f,
                    0f
                ),
                style
            )
        );

    /// <summary>Draws a border inside a rectangle's edges, with per-corner radii.</summary>
    /// <param name="rectangle">Where.</param>
    /// <param name="color">What colour.</param>
    /// <param name="thickness">How wide the band is, drawn inwards from the edge.</param>
    /// <param name="style">The corners. A gradient on a border runs along the same axis as a fill's.</param>
    public void StrokeRectangle(Rectangle rectangle, Color4 color, float thickness, BoxStyle style = default) =>
        List.Add(
            Styled(
                new DrawCommand(
                    DrawCommandKind.Border,
                    rectangle.X,
                    rectangle.Y,
                    rectangle.Width,
                    rectangle.Height,
                    DrawListBuilder.Fade(color, alpha),
                    0f,
                    thickness
                ),
                style
            )
        );

    /// <summary>Attaches a box style to a command, unless the cheap uniform path covers it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The same test <see cref="DrawListBuilder" /> applies to the declarative path, and
    ///         until it was applied here the imperative one paid for a record full of zeroes on every
    ///         box it drew.</b> <see cref="StrokeRectangle" />'s style parameter <i>defaults</i>, so
    ///         every <c>StrokeRectangle(rect, colour, thickness)</c> in the tree — the surfaces, the
    ///         node canvas, the colour picker, the curve and gradient editors — wrote an entry saying
    ///         nothing. <see cref="DrawList.Boxes" /> is compared entry by entry every frame beside
    ///         the commands, so those entries were not merely memory: they were frame-diff work.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The test is "nothing but uniform circular corners", not "no gradient".</b> Four
    ///         equal <i>elliptical</i> corners are not one <c>float</c> — see
    ///         <see cref="CornerRadii.IsUniformCircular" /> — and every other member of
    ///         <see cref="BoxStyle" /> is a lane the scalar cannot carry, so the comparison is
    ///         against <see cref="BoxStyle.Rounded" /> of the box's own corners rather than a list of
    ///         fields somebody has to remember to extend.
    ///     </para>
    /// </remarks>
    DrawCommand Styled(DrawCommand command, BoxStyle style) =>
        Plain(style, out var radius)
            ? command with { Radius = radius }
            : command with { Offset = List.AddBox(style), Length = 1 };

    /// <summary>Whether one <c>float</c> says everything this style says.</summary>
    /// <param name="style">The style.</param>
    /// <param name="radius">The radius that says it, or zero.</param>
    static bool Plain(BoxStyle style, out float radius) {
        if (style.Corners.IsUniformCircular(out radius) && style == BoxStyle.Rounded(style.Corners)) {
            return true;
        }

        radius = 0f;

        return false;
    }
}
