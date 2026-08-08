// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Layout;
using Vixen.Ui.Styling;

namespace Vixen.Ui;

/// <summary>Turns a laid-out, styled tree into a list of things to draw.</summary>
/// <remarks>
///     <para>
///         The last step of the chain this assembly exists to complete: the cascade said what
///         applies, the bridge turned that into lengths, flexbox turned those into rectangles, and
///         this turns the rectangles into commands. Nothing here decides anything — it reads.
///     </para>
///     <para>
///         <b>Painting order is <see cref="UiElement.PaintOrder" /></b>, parent before its children
///         and siblings in the order they were added unless a <c>z-index</c> says otherwise. That is
///         the same property hit testing walks in reverse, and the two have to agree: an element
///         drawn on top must be the one a click lands on, and any rule that made them disagree would
///         be a UI where things are not where they look. Neither of them having its own opinion is
///         what guarantees it.
///     </para>
/// </remarks>
public sealed class DrawListBuilder {
    /// <summary>How far past a viewport an edge is pushed when its axis is not clipped.</summary>
    /// <remarks>
    ///     ⚠ <b>A stand-in for infinity, chosen so that the sums stay exact.</b> A million pixels is
    ///     more than a hundred times the widest display anyone has, and two million is still well
    ///     inside the range where a <c>float</c> counts whole numbers one at a time — where
    ///     <c>float.MaxValue</c> would give a right edge of infinity and an infinite width a right edge
    ///     of NaN, and a NaN in the clip stack silently unclips everything below it.
    /// </remarks>
    internal const float UnboundedClip = 1_000_000f;

    readonly List<PositionedGlyph> placed = [];
    readonly StyleValueParser parser;
    readonly int backgroundColor;

    /// <summary>The four <c>border-*-color</c> longhands, clockwise from the top.</summary>
    readonly int[] borderColors;

    /// <summary>The four <c>border-*-radius</c> longhands, clockwise from the top left.</summary>
    readonly int[] borderRadii;

    readonly int backgroundImage;
    readonly GradientReader gradients;
    readonly int textColor;
    readonly OverflowReader overflow;
    readonly int visibility;
    readonly int hidden;
    readonly int opacity;
    readonly int textAlign;
    readonly int direction;
    readonly int boxShadow;
    readonly int currentColor;
    readonly int alignedCenter;
    readonly int alignedLeft;
    readonly int alignedRight;
    readonly int alignedEnd;
    readonly int rtl;

    /// <summary>Creates a builder over a style engine's name tables.</summary>
    /// <param name="properties">The table property names are interned in.</param>
    /// <param name="values">The table declaration values are interned in.</param>
    /// <param name="keywords">The table identifiers are interned in.</param>
    public DrawListBuilder(NameTable properties, NameTable values, NameTable keywords) {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(keywords);

        parser = new StyleValueParser(values, keywords);

        backgroundColor = properties.Intern("background-color");

        // ⚠ <b>The image is a second layer and not an alternative to the colour.</b> CSS paints
        // `background-image` over `background-color`, so an element with both draws twice — which
        // matters the moment a gradient's near stop is `transparent`, as every `bg-linear-*` with no
        // `from-*` is. Reading one and skipping the other would be a coin toss dressed as a choice.
        backgroundImage = properties.Intern("background-image");
        gradients = new GradientReader(values, parser);

        // ⚠ The longhands, never the shorthands, and *all* of them. A shorthand is expanded before
        // it is interned — by ExCSS while parsing when the value is literal, and by
        // `ShorthandExpansion` at load when it holds a `var()`, which ExCSS is obliged to hand back
        // whole — so the cascade never carries a `border-color` or a `border-radius` for anything to
        // read. Written against the shorthands, every border and every rounded corner in the
        // document silently disappears.
        //
        // ⚠ <b>And it used to intern only the first of each set, which is not a smaller version of
        // the same thing.</b> Reading `border-top-color` alone made `border-b-accent` inert, as
        // expected — but it also made `border-top-width` paint a ring on all four edges, made the
        // other three widths paint nothing at all, and made `border-bottom-right-radius` disappear
        // while `border-top-left-radius` silently rounded the whole box. Twenty-one rules in the
        // editor's own themes were written against the three that draw nothing.
        borderColors = [
            properties.Intern("border-top-color"),
            properties.Intern("border-right-color"),
            properties.Intern("border-bottom-color"),
            properties.Intern("border-left-color")
        ];

        borderRadii = [
            properties.Intern("border-top-left-radius"),
            properties.Intern("border-top-right-radius"),
            properties.Intern("border-bottom-right-radius"),
            properties.Intern("border-bottom-left-radius")
        ];
        textColor = properties.Intern("color");
        overflow = new OverflowReader(properties, values);

        visibility = properties.Intern("visibility");
        this.hidden = values.Intern("hidden");
        opacity = properties.Intern("opacity");

        textAlign = properties.Intern("text-align");
        direction = properties.Intern("direction");
        boxShadow = properties.Intern("box-shadow");
        // ⚠ The <i>keywords</i> table, not <c>values</c>. `StyleValueParser` interns an identifier it
        // does not recognise as a colour into the one it was handed for keywords, and the two tables
        // are separate — interning here from the wrong one gives an id that can never compare equal
        // and a `currentcolor` that silently refuses the declaration instead of resolving it.
        currentColor = keywords.Intern("currentcolor");

        alignedCenter = values.Intern("center");
        alignedLeft = values.Intern("left");
        alignedRight = values.Intern("right");
        alignedEnd = values.Intern("end");
        this.rtl = values.Intern("rtl");
    }

    /// <summary>Walks a document and fills a draw list.</summary>
    /// <param name="document">The document, already updated.</param>
    /// <param name="into">The list to fill.</param>
    /// <returns>Whether the drawing differs from the previous frame's.</returns>
    public bool Build(UiDocument document, DrawList into) {
        ArgumentNullException.ThrowIfNull(document);
        return Build(document, document.Root, into);
    }

    /// <summary>Walks one surface of a document and fills a draw list.</summary>
    /// <param name="document">The document, already updated.</param>
    /// <param name="root">The surface's root — <see cref="UiSurface.Root" />.</param>
    /// <param name="into">The list to fill.</param>
    /// <returns>Whether the drawing differs from the previous frame's.</returns>
    /// <remarks>
    ///     One list per window, because one window's frame is not another's. The walk stops at any
    ///     <i>other</i> surface's root it meets: a torn-off panel is still a child of this tree, and
    ///     drawing it here would put a copy of it in the main window at whatever coordinates its own
    ///     window happens to use.
    /// </remarks>
    public bool Build(UiDocument document, UiElement root, DrawList into) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(into);

        into.BeginFrame();
        Emit(document, root, into, 1f);
        return into.EndFrame();
    }

    /// <summary>Emits one element and its subtree.</summary>
    /// <param name="document">The document.</param>
    /// <param name="element">The element.</param>
    /// <param name="into">The list being filled.</param>
    /// <param name="inherited">
    ///     The <c>opacity</c> of everything above this element, multiplied together.
    /// </param>
    /// <remarks>
    ///     ⚠ <b>Opacity is carried down as a multiplier rather than composited as a group, and the
    ///     difference is visible.</b> CSS renders a translucent element's subtree into its own
    ///     surface and then blends that surface once, so two overlapping children of a half-opaque
    ///     panel do <i>not</i> show through each other. Multiplying each element's alpha instead
    ///     makes them show through, and the two answers agree exactly whenever the subtree does not
    ///     overlap itself — which is most interfaces, and all of the ones a fade-in is applied to.
    ///     The correct version needs an offscreen target per translucent subtree, which is a
    ///     compositor decision rather than a draw list's, so it is <b>owed</b>. Said here because a
    ///     half-right opacity reads as a bug in the renderer rather than a gap in the model.
    /// </remarks>
    void Emit(UiDocument document, UiElement element, DrawList into, float inherited) {
        var width = element.Width;
        var height = element.Height;

        // A zero-sized element draws nothing and clips nothing, and skipping it early keeps
        // `display: none` — which flexbox reports as a zero box — out of the list entirely rather
        // than in it as a stack of invisible commands.
        if (width <= 0f || height <= 0f) {
            return;
        }

        var alpha = inherited * Opacity(element);

        // ⚠ Fully transparent is skipped outright rather than emitted with a zero alpha, and the
        // subtree with it — `opacity: 0` is not inherited, but it multiplies, so nothing below can
        // bring it back. A frame full of invisible commands costs a batch and a draw each and is
        // indistinguishable in the picture from having emitted nothing.
        if (alpha <= 0f) {
            return;
        }

        var x = element.AbsoluteLeft;
        var y = element.AbsoluteTop;

        var corners = Corners(element);

        // ⚠ The scalar every command still carries, and it is the *uniform* radius or nothing. A box
        // whose corners differ carries its radii in the side buffer and a zero here — putting the
        // top-left corner in the scalar instead would leave a consumer that reads only `Radius`
        // rounding all four corners by one of them, which is precisely the bug this file is fixing.
        var radius = corners.IsUniformCircular(out var uniform) ? uniform : 0f;

        // ⚠ `visibility: hidden` hides the element and *not* its subtree, which is what separates it
        // from `display: none`. It is an inherited property, so a child is hidden by having
        // inherited the value rather than by being skipped here — and a child that declares
        // `visibility: visible` reappears inside a hidden parent, which is the whole reason CSS has
        // two properties for this.
        var shown = !element.Style.TryGet(visibility, out var mode) || mode != hidden;

        if (shown) {
            // Before the background, which is where CSS paints it: a shadow is cast *by* the box and
            // therefore lies under it, and an element with a translucent background shows its own
            // shadow through itself.
            EmitShadow(document, element, into, x, y, width, height, corners, radius, alpha);

            if (Color(element, backgroundColor) is { } fill) {
                into.Add(
                    Styled(
                        new DrawCommand(
                            DrawCommandKind.Rectangle,
                            x,
                            y,
                            width,
                            height,
                            Fade(fill, alpha),
                            radius,
                            0f
                        ),
                        into,
                        corners
                    )
                );
            }

            EmitGradient(element, into, x, y, width, height, corners, radius, alpha);

            // The border is drawn after the background and before the children, which is the order
            // CSS paints them in — a child overlapping the edge covers the border, and a background
            // never covers its own.
            EmitBorder(document, element, into, x, y, width, height, corners, radius, alpha);
        }

        var axes = overflow.Of(element.Style);
        if (axes.Any) {
            // ⚠ An unclipped axis is a pair of edges at infinity, and `UnboundedClip` stands in for
            // infinity because the arithmetic that consumes this cannot hold it: the clip stack
            // intersects rectangles, and an infinite width gives `X + Width` as a NaN that swallows
            // every clip below it. A finite stand-in is not an approximation here — the stack starts
            // from the viewport and only ever narrows, so an edge past the viewport is bounded by the
            // viewport, which is exactly what "not clipped on this axis" means.
            var left = axes.Horizontal ? x : -UnboundedClip;
            var top = axes.Vertical ? y : -UnboundedClip;
            var across = axes.Horizontal ? width : 2f * UnboundedClip;
            var down = axes.Vertical ? height : 2f * UnboundedClip;

            into.Add(new DrawCommand(DrawCommandKind.ClipPush, left, top, across, down, default, radius, 0f));
        }

        if (shown) {
            // Between the border and the children, which is where CSS puts an element's own content:
            // a child overlaps its parent's text, and its parent's text overlaps its parent's border.
            //
            // ⚠ <b>Inside the clip, and it used to be outside it.</b> `overflow` clips an element's
            // *content*, and an element's own text is content — the background and the border are
            // the two things it does not clip, which is why the push is below them and not above.
            // Emitting the text first meant `overflow: hidden` clipped an element's children and
            // never its own string, so a label too long for a fixed column drew straight across
            // whatever was beside it. Five places in the editor had written `overflow: hidden` on a
            // text-bearing element believing otherwise, and every one of them was a column that
            // silently overdrew its neighbour. It survived because a clip is invisible to the
            // element tree: every rectangle was the right size and the glyphs went somewhere else.
            EmitText(document, element, into, alpha);
            element.OnDraw(new DrawContext(element, into, alpha));
        }

        // Paint order rather than document order, which are the same list unless some child carries
        // a `z-index`. Hit testing walks the same property backwards, and that is the whole reason
        // it is a property of the element rather than a loop written twice.
        foreach (var child in element.PaintOrder) {
            // Another window's tree, which this frame is not. It is walked by its own surface's
            // build, against its own size and its own pixel grid.
            if (child.SurfaceRoot is not null) {
                continue;
            }

            Emit(document, child, into, alpha);
        }

        // ⚠ Popped only if it was pushed, and popped after the children rather than at the end of
        // the frame. A list whose pushes and pops do not pair is not a drawing with a mistake in it,
        // it is a clip stack that never unwinds — everything after the offending element stays
        // clipped to it for the rest of the frame.
        if (axes.Any) {
            into.Add(new DrawCommand(DrawCommandKind.ClipPop, x, y, width, height, default, radius, 0f));
        }
    }

    /// <summary>Emits an element's border: one ring when it is uniform, one band per edge when not.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The uniform case is the one that already worked, and it comes out byte for byte the
    ///         same.</b> Four equal widths and four equal colours are a single
    ///         <see cref="DrawCommandKind.Border" /> command — one quad, one distance field, one
    ///         antialiased outer edge shared by the border and the fill it sits on. Every box in every
    ///         theme in this repository that draws a border at all takes this path, which is what makes
    ///         the change safe: the fast path is not a new fast path, it is the old only path.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The non-uniform case is bands, and it is not a ring with extra colours.</b> The box
    ///         shader resolves a border as the difference of two coverages — the outline and the same
    ///         outline pushed <c>thickness</c> inwards — so the thickness and the colour are properties
    ///         of the <i>shape</i>, not of a side of it. There is no per-pixel notion of which edge a
    ///         fragment belongs to, and adding one means four more colours and four more thicknesses in
    ///         <see cref="Rendering.UiShape" />: eighty more bytes on a record every box in the frame
    ///         writes, to describe something almost none of them have. So an element whose edges differ
    ///         is drawn as up to four plain rectangles instead, which cost nothing anywhere else and
    ///         batch with the backgrounds around them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The horizontal edges take the corners and the vertical ones are inset between
    ///         them</b>, which is the join CSS draws only when the two edges meeting at a corner are
    ///         the same colour — otherwise it mitres them diagonally. A mitre is a triangle and this
    ///         emits rectangles, so the difference shows exactly when two adjacent edges are both thick
    ///         <i>and</i> differently coloured; at the one pixel every such rule in this repository
    ///         actually uses, the mitre is a single pixel and there is nothing to see. Said here rather
    ///         than fixed because the fix is the eighty bytes above.
    ///     </para>
    /// </remarks>
    void EmitBorder(
        UiDocument document,
        UiElement element,
        DrawList into,
        float x,
        float y,
        float width,
        float height,
        CornerRadii corners,
        float radius,
        float alpha
    ) {
        // Clockwise from the top, which is the order CSS lists the edges in and the order the colour
        // table above is interned in. The two agreeing is what lets one index mean one edge.
        var top = document.Layout.GetComputedBorder(element.LayoutNode, Edge.Top);
        var right = document.Layout.GetComputedBorder(element.LayoutNode, Edge.Right);
        var bottom = document.Layout.GetComputedBorder(element.LayoutNode, Edge.Bottom);
        var left = document.Layout.GetComputedBorder(element.LayoutNode, Edge.Left);

        if (top <= 0f && right <= 0f && bottom <= 0f && left <= 0f) {
            return;
        }

        var topColor = Color(element, borderColors[0]);
        var rightColor = Color(element, borderColors[1]);
        var bottomColor = Color(element, borderColors[2]);
        var leftColor = Color(element, borderColors[3]);

        var square = top == right && right == bottom && bottom == left;
        var oneColour = topColor == rightColor && rightColor == bottomColor && bottomColor == leftColor;

        if (square && oneColour) {
            if (topColor is { } stroke) {
                into.Add(
                    Styled(
                        new DrawCommand(
                            DrawCommandKind.Border,
                            x,
                            y,
                            width,
                            height,
                            Fade(stroke, alpha),
                            radius,
                            top
                        ),
                        into,
                        corners
                    )
                );
            }

            return;
        }

        // ⚠ The vertical bands are inset by the horizontal thicknesses and not the other way round,
        // so the corner square belongs to the top and bottom edges. Giving it to both would draw it
        // twice — which is invisible for an opaque colour and a doubled alpha for a translucent one,
        // and a border at 50% opacity is exactly what a focus ring is made of.
        var middle = MathF.Max(height - top - bottom, 0f);

        Band(topColor, top, x, y, width, top, corners.TopLeft, corners.TopRight, default, default);
        Band(bottomColor, bottom, x, y + height - bottom, width, bottom, default, default, corners.BottomRight, corners.BottomLeft);
        Band(leftColor, left, x, y + top, left, middle, default, default, default, default);
        Band(rightColor, right, x + width - right, y + top, right, middle, default, default, default, default);

        void Band(
            Color4? colour,
            float thickness,
            float bandX,
            float bandY,
            float bandWidth,
            float bandHeight,
            Vector2 topLeft,
            Vector2 topRight,
            Vector2 bottomRight,
            Vector2 bottomLeft
        ) {
            if (thickness <= 0f || bandWidth <= 0f || bandHeight <= 0f || colour is not { } fill) {
                return;
            }

            // ⚠ A band is a *filled* rectangle, not a border one. Its thickness is already its height
            // or its width, and asking the shader for a ring as well would hollow out a strip one
            // pixel tall into nothing at all.
            into.Add(
                Styled(
                    new DrawCommand(
                        DrawCommandKind.Rectangle,
                        bandX,
                        bandY,
                        bandWidth,
                        bandHeight,
                        Fade(fill, alpha),
                        0f,
                        0f
                    ),
                    into,
                    new CornerRadii(topLeft, topRight, bottomRight, bottomLeft)
                )
            );
        }
    }

    /// <summary>Emits an element's text, if it has any and there is a font for it.</summary>
    /// <remarks>
    ///     <para>
    ///         Positioned against the <b>content box</b> — inside the border and the padding — rather
    ///         than against the element's edge, because that is what those two properties mean. Read
    ///         from the layout results rather than from the style, so a percentage padding is the
    ///         number flexbox resolved rather than a percentage this would have to resolve again.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The y is the baseline, not the top.</b> Glyph origins sit on the baseline, so the
    ///         run's origin is the content box's top plus the font's ascender. Putting the top there
    ///         instead draws every line one ascender too low, which for a single line looks like a
    ///         padding mistake and for two lines looks like nothing at all.
    ///     </para>
    /// </remarks>
    void EmitText(UiDocument document, UiElement element, DrawList into, float alpha) {
        if (element.Block() is not { } block) {
            return;
        }

        var borderLeft = document.Layout.GetComputedBorder(element.LayoutNode, Edge.Left);
        var paddingLeft = document.Layout.GetComputedPadding(element.LayoutNode, Edge.Left);
        var left = element.AbsoluteLeft + borderLeft + paddingLeft;

        var top = element.AbsoluteTop
            + document.Layout.GetComputedBorder(element.LayoutNode, Edge.Top)
            + document.Layout.GetComputedPadding(element.LayoutNode, Edge.Top);

        // Against the content box, which is what the run was positioned against — using the border
        // box here would push centred text off by half the padding, in the direction that looks like
        // the padding is uneven.
        var content = element.Width
            - borderLeft
            - paddingLeft
            - document.Layout.GetComputedBorder(element.LayoutNode, Edge.Right)
            - document.Layout.GetComputedPadding(element.LayoutNode, Edge.Right);

        var color = Fade(Color(element, textColor) ?? Color4.Black, alpha);

        // ⚠ One command per run *per line*, because a command names one font and lies on one
        // baseline. A wrapped paragraph in two faces is four commands, and each of them carries its
        // own origin — which is also why a run's glyphs are placed from zero rather than the whole
        // block being placed once and sliced: a slice would put every later command's glyphs at
        // coordinates relative to the first one's origin.
        foreach (var line in block.Lines) {
            // ⚠ The alignment is per line, not per block. A centred paragraph centres each of its
            // lines within the content box; centring the block and laying the lines out inside it
            // would left-align every line but the widest.
            var x = left + Indent(element, content - line.Width);
            var y = top + block.TopOf(block.Lines.IndexOf(line)) + line.Baseline;

            for (var i = 0; i < line.Runs.Length; i++) {
                var run = line.Runs[i];

                placed.Clear();
                run.Place(placed);

                if (placed.Count == 0) {
                    continue;
                }

                // The glyphs are placed relative to the start of the run and the command carries
                // where that is, rather than each glyph carrying an absolute position. Two identical
                // labels in different places then hold identical glyph runs, which is what will let
                // the batcher notice.
                into.Add(
                    new DrawCommand(
                        DrawCommandKind.Text,
                        x + line.PenOf(i),
                        y,
                        run.Width,
                        line.Height,
                        color,
                        0f,
                        0f
                    ) {
                        Offset = into.AddGlyphs(placed),
                        Length = placed.Count,
                        Font = into.AddFont(run.Font),
                        FontSize = run.Size
                    }
                );
            }
        }
    }

    /// <summary>Emits an element's <c>box-shadow</c>, if it has one this can read.</summary>
    /// <remarks>
    ///     <para>
    ///         <c>box-shadow: &lt;x&gt; &lt;y&gt; &lt;blur&gt; [spread] &lt;colour&gt;</c>. The offset
    ///         and the spread are folded into the command's rectangle and the spread into its radius,
    ///         so what reaches the geometry is an ordinary rounded box that happens to be blurred —
    ///         which is why a shadow needs no fields on <c>DrawCommand</c> that a box does not have.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One shadow, and an outer one.</b> CSS takes a comma-separated list and an
    ///         <c>inset</c> keyword; a list would be a command each, which is easy, and <c>inset</c>
    ///         is a different distance field, which is not. Both are refused rather than
    ///         half-applied — the first shadow of a list being drawn and the rest silently dropped
    ///         is worse than nothing being drawn, because it looks like it worked.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it is not clipped to outside the border box.</b> CSS punches the box out of
    ///         its own shadow, so a translucent background does not darken over its own; here the
    ///         blurred box is drawn whole and the background sits on top of it. Visible only under a
    ///         background that is not opaque, and it needs a stencil or a second field to fix.
    ///     </para>
    /// </remarks>
    void EmitShadow(
        UiDocument document,
        UiElement element,
        DrawList into,
        float x,
        float y,
        float width,
        float height,
        CornerRadii corners,
        float radius,
        float alpha
    ) {
        if (!element.Style.TryGet(boxShadow, out var id)) {
            return;
        }

        var value = parser.Parse(id);

        // A shadow is at least two lengths and a colour, so anything that is not a list of values is
        // `none`, `inset` on its own, or a mistake — all of which draw nothing.
        if (value.Kind != StyleValueKind.List) {
            return;
        }

        var context = document.Viewport.WithFontSize(element.FontSize);
        Span<float> lengths = [0f, 0f, 0f, 0f];
        var count = 0;
        Color4? shade = null;

        foreach (var item in value.Items) {
            switch (item.Kind) {
                case StyleValueKind.Color:
                    shade = item.Color;
                    continue;

                // ⚠ <b><c>currentcolor</c> is the one keyword that is a colour</b>, and it is here
                // because a ring needs it. CSS Color 4 § 6.2 defines it as the computed <c>color</c>,
                // which is exactly what `ForegroundOf` answers — the same resolution an icon's
                // `IconPaintKind.Foreground` already gets. It matters rather than being a nicety:
                // `UtilityComposition.RingColor`'s initial value is `currentcolor`, so `ring-2`
                // written on its own — much the commonest way the class appears, on a focused control
                // — resolves through this branch. Without it the fallback would have had to be some
                // concrete colour nobody chose, or `transparent`, which would make `ring-2` cascade
                // perfectly and paint nothing.
                case StyleValueKind.Keyword when item.Keyword == currentColor:
                    shade = document.ForegroundOf(element);
                    continue;

                // ⚠ Every other keyword refuses the whole declaration, and `inset` is the one that
                // matters: an inset shadow drawn as an outer one is not a near miss, it is a shadow
                // on the wrong side of the box.
                case StyleValueKind.Keyword:
                    return;

                // A bare `0` is a length and only that one, which is the same rule `LengthContext`
                // applies — `box-shadow: 0 2px 4px #000` is how everybody writes it.
                case StyleValueKind.Number when item.Number == 0f && count < lengths.Length:
                    lengths[count++] = 0f;
                    continue;

                case StyleValueKind.Length when count < lengths.Length:
                    lengths[count++] = item.Number * context.PixelsPer(item.Unit);
                    continue;

                default:
                    return;
            }
        }

        if (count < 2 || shade is not { } colour) {
            return;
        }

        // ⚠ Half the CSS blur radius. CSS's blur is the *total* distance the edge fades over, and the
        // shader's is the half-extent either side of the boundary — passing the whole radius makes
        // every shadow twice as soft as it was asked to be, which reads as a blurry renderer rather
        // than as a unit mistake.
        var falloff = lengths[2] / 2f;

        // The spread grows the box in every direction, and the corner radius with it: a spread that
        // kept the original corner would give a shadow visibly squarer than the thing casting it.
        var spread = lengths[3];
        var wide = width + (spread * 2f);
        var tall = height + (spread * 2f);

        if (wide <= 0f || tall <= 0f) {
            return;
        }

        into.Add(
            Styled(
                new DrawCommand(
                    DrawCommandKind.Shadow,
                    x + lengths[0] - spread,
                    y + lengths[1] - spread,
                    wide,
                    tall,
                    Fade(colour, alpha),
                    MathF.Max(radius + spread, 0f),
                    falloff
                ),
                into,
                Grow(corners, spread)
            )
        );
    }

    /// <summary>Every corner grown by a shadow's spread, never below square.</summary>
    /// <remarks>
    ///     The same argument the uniform path makes about <c>radius + spread</c>, applied per corner:
    ///     a spread that kept the original radii would give a shadow visibly squarer than the thing
    ///     casting it. Both axes of each ellipse grow by the same amount, because the spread is a
    ///     distance outwards rather than a scale.
    /// </remarks>
    static CornerRadii Grow(CornerRadii corners, float spread) {
        if (spread == 0f) {
            return corners;
        }

        return new CornerRadii(
            Grow(corners.TopLeft, spread),
            Grow(corners.TopRight, spread),
            Grow(corners.BottomRight, spread),
            Grow(corners.BottomLeft, spread)
        );

        static Vector2 Grow(Vector2 corner, float spread) =>
            new(MathF.Max(corner.X + spread, 0f), MathF.Max(corner.Y + spread, 0f));
    }

    /// <summary>How far <c>text-align</c> moves a run along the slack it has.</summary>
    /// <param name="element">The element.</param>
    /// <param name="slack">The content box's width less the run's, which may be negative.</param>
    /// <returns>What to add to the run's left.</returns>
    /// <remarks>
    ///     <para>
    ///         <c>start</c> and <c>end</c> are resolved against <c>direction</c>, the same property
    ///         the layout resolves its logical edges with — so a label written <c>text-end</c> lands
    ///         on the same side as the padding <c>pe-2</c> gave it.
    ///     </para>
    ///     <para>
    ///         <c>justify</c> falls through to the start, which is not a shortcut: CSS aligns the
    ///         <i>last</i> line of a justified block to the start, and a single-line run is its own
    ///         last line. Stretching one would be wrong rather than unimplemented.
    ///     </para>
    ///     <para>
    ///         ⚠ Negative slack is left alone. Text wider than its box overflows to the right of the
    ///         start edge whatever the alignment says, because centring it would hide the beginning
    ///         of the string — and the beginning is the part a reader needs to recognise what has
    ///         been cut off.
    ///     </para>
    /// </remarks>
    float Indent(UiElement element, float slack) {
        if (slack <= 0f || !element.Style.TryGet(textAlign, out var alignment)) {
            return 0f;
        }

        // The physical keywords first, because they mean a side whatever the direction is — that is
        // the whole difference between them and the logical ones.
        if (alignment == alignedCenter) {
            return slack * 0.5f;
        }

        if (alignment == alignedRight) {
            return slack;
        }

        if (alignment == alignedLeft) {
            return 0f;
        }

        // `start`, `end`, and anything unrecognised — which lands on the start edge, the same place
        // an element with no `text-align` at all sits.
        var mirrored = element.Style.TryGet(direction, out var flow) && flow == rtl;
        return mirrored != (alignment == alignedEnd) ? slack : 0f;
    }

    /// <summary>An element's own <c>opacity</c>, before anything above it is multiplied in.</summary>
    /// <remarks>
    ///     One when nothing said, and clamped — CSS clamps to 0–1 rather than treating <c>1.5</c> as
    ///     an error, and a value outside the range that silently drew nothing would be a stylesheet
    ///     bug nobody could find. Not inherited, which is why it has to be threaded through
    ///     <see cref="Emit" /> rather than read off the computed style of each element alone.
    /// </remarks>
    float Opacity(UiElement element) {
        if (!element.Style.TryGet(opacity, out var id)) {
            return 1f;
        }

        var value = parser.Parse(id);
        return value.Kind == StyleValueKind.Number ? Math.Clamp(value.Number, 0f, 1f) : 1f;
    }

    /// <summary>A colour with the accumulated opacity multiplied into its alpha.</summary>
    /// <remarks>
    ///     ⚠ Not <c>colour * alpha</c>, which the operator would read as scaling all four
    ///     components — right in premultiplied space and wrong here, where it would darken the
    ///     colour towards black as well as fading it. Internal because <see cref="DrawContext" />
    ///     fades what a custom-drawn control hands it, and the two must agree.
    /// </remarks>
    internal static Color4 Fade(Color4 colour, float alpha) =>
        alpha >= 1f ? colour : new Color4(colour.R, colour.G, colour.B, colour.A * alpha);

    Color4? Color(UiElement element, int property) {
        if (!element.Style.TryGet(property, out var id)) {
            return null;
        }

        var value = parser.Parse(id);
        return value.Kind == StyleValueKind.Color ? value.Color : null;
    }

    /// <summary>An element's four corner radii, each elliptical.</summary>
    /// <remarks>
    ///     ⚠ <b>A corner arrives as <i>two</i> lengths — <c>8px 8px</c> — even when the stylesheet
    ///     wrote one</b>, because that is what the shorthand expands to. Both are read now: the pair
    ///     is the horizontal and vertical radius of an ellipse, which is CSS's
    ///     <c>border-radius: 40px / 20px</c> and what a pill-shaped button whose height is not its
    ///     width actually needs. Taking the first and dropping the second drew every such corner as a
    ///     circle.
    /// </remarks>
    CornerRadii Corners(UiElement element) =>
        new(
            Radius(element, borderRadii[0]),
            Radius(element, borderRadii[1]),
            Radius(element, borderRadii[2]),
            Radius(element, borderRadii[3])
        );

    /// <summary>One corner's horizontal and vertical radius.</summary>
    /// <remarks>
    ///     A single length means a circle, which is the one-value form written out. Absolute lengths
    ///     only: a percentage radius resolves against the box's own size, which is a rule this
    ///     builder would have to know rather than read.
    /// </remarks>
    Vector2 Radius(UiElement element, int property) {
        if (!element.Style.TryGet(property, out var id)) {
            return Vector2.Zero;
        }

        var value = parser.Parse(id);

        if (value.Kind != StyleValueKind.List) {
            var single = Pixels(value);
            return new Vector2(single, single);
        }

        if (value.Items.Length == 0) {
            return Vector2.Zero;
        }

        var horizontal = Pixels(value.Items[0]);
        var vertical = value.Items.Length > 1 ? Pixels(value.Items[1]) : horizontal;

        return new Vector2(horizontal, vertical);

        static float Pixels(StyleValue value) =>
            value.Kind == StyleValueKind.Length && value.Unit == StyleUnit.Pixels ? value.Number : 0f;
    }

    /// <summary>Attaches a box style to a command, unless the cheap uniform path covers it.</summary>
    /// <remarks>
    ///     ⚠ <b>The whole reason the side buffer is worth having is that this usually returns the
    ///     command unchanged.</b> <see cref="DrawList.Boxes" /> is compared entry by entry every frame
    ///     alongside the commands, so an entry written for a box whose four corners are the same
    ///     circle would be pure cost — the scalar <see cref="DrawCommand.Radius" /> already says
    ///     everything about it, and <see cref="Rendering.UiGeometryBuilder" /> expands that scalar back
    ///     into four equal corners on the way to the shader. Only the boxes that are genuinely more
    ///     than a colour, a size and one radius go in.
    /// </remarks>
    static DrawCommand Styled(DrawCommand command, DrawList into, CornerRadii corners) =>
        corners.IsUniformCircular(out _)
            ? command
            : command with { Offset = into.AddBox(BoxStyle.Rounded(corners)), Length = 1 };

    /// <summary>Paints the <c>background-image</c> layer, when it is a gradient this engine draws.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This emits a command where the background colour did not, and that is deliberate.</b>
    ///         <c>bg-linear-to-r from-accent to-surface-3</c> sets no <c>background-color</c> at all, so
    ///         an element whose only background is a gradient has no colour for the caller above to
    ///         find — and a gradient that painted only over an existing fill would be invisible on
    ///         exactly the elements Tailwind's own gradient utilities produce.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A refused gradient paints nothing rather than falling back to one of its stops.</b>
    ///         The near colour is right along one edge and wrong everywhere else, which is a picture
    ///         somebody has to squint at; an absent gradient is a question they ask immediately. See
    ///         <see cref="GradientRefusal" /> for the whole argument.
    ///     </para>
    /// </remarks>
    void EmitGradient(
        UiElement element,
        DrawList into,
        float x,
        float y,
        float width,
        float height,
        CornerRadii corners,
        float radius,
        float alpha
    ) {
        if (!element.Style.TryGet(backgroundImage, out var id)) {
            return;
        }

        var gradient = gradients.Read(id);

        if (!gradient.IsPaintable) {
            return;
        }

        var axis = gradient.Axis(width, height);

        // ⚠ A degenerate box has no direction to run a linear ramp along, and there is nothing to see
        // at this size either way; not emitting says so honestly. Tested on the *shape* rather than on
        // the axis, because a radial gradient's axis is legitimately zero — it has no direction at all
        // — and the old sentinel would have erased every one of them.
        if (gradient.Shape == GradientShape.Linear && axis == Vector2.Zero) {
            return;
        }

        // ⚠ Unconditionally into the side buffer, unlike `Styled`. The cheap path exists because a
        // uniformly rounded box needs nothing but its scalar radius — and a gradient is precisely a
        // box that needs more than that, so the test that skips the record has to be skipped here.
        var offset = into.AddBox(
            new BoxStyle(corners, Fade(gradient.End, alpha), axis) {
                Shape = gradient.Shape,
                Space = gradient.Space,
                GradientVia = Fade(gradient.Via, alpha),
                HasVia = gradient.HasVia,
                Stops = gradient.Stops
            }
        );

        into.Add(
            new DrawCommand(
                DrawCommandKind.Rectangle,
                x,
                y,
                width,
                height,
                Fade(gradient.Start, alpha),
                radius,
                0f
            ) {
                Offset = offset,
                Length = 1
            }
        );
    }
}
