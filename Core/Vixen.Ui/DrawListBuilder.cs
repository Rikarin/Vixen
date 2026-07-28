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
///         <b>Painting order is document order</b>, parent before its children, siblings in the
///         order they were added. That is the same order hit testing walks in reverse, and the two
///         have to agree: an element drawn on top must be the one a click lands on, and any rule
///         that made them disagree would be a UI where things are not where they look.
///     </para>
/// </remarks>
public sealed class DrawListBuilder {
    readonly List<PositionedGlyph> placed = [];
    readonly StyleValueParser parser;
    readonly int backgroundColor;
    readonly int borderColor;
    readonly int borderRadius;
    readonly int textColor;
    readonly int overflow;
    readonly int opacity;
    readonly int textAlign;
    readonly int direction;
    readonly int boxShadow;
    readonly int visible;
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

        // ⚠ The longhands, because ExCSS expands `border-color` and `border-radius` while parsing
        // and the cascade never sees the shorthand — the same thing the styling-to-layout bridge
        // found about `margin`. Written against the shorthands, every border and every rounded
        // corner in the document silently disappears.
        borderColor = properties.Intern("border-top-color");
        borderRadius = properties.Intern("border-top-left-radius");
        textColor = properties.Intern("color");
        overflow = properties.Intern("overflow");
        opacity = properties.Intern("opacity");
        textAlign = properties.Intern("text-align");
        direction = properties.Intern("direction");
        boxShadow = properties.Intern("box-shadow");

        this.visible = values.Intern("visible");
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
        ArgumentNullException.ThrowIfNull(into);

        into.BeginFrame();
        Emit(document, document.Root, into, 1f);
        return into.EndFrame();
    }

    /// <summary>Emits one element and everything under it.</summary>
    /// <param name="document">The document.</param>
    /// <param name="element">The element.</param>
    /// <param name="into">The list to fill.</param>
    /// <param name="inherited">
    ///     The alpha every colour below here is multiplied by, which is the product of the
    ///     <c>opacity</c> of every ancestor.
    /// </param>
    /// <remarks>
    ///     ⚠ <b><c>opacity</c> is applied per command, not per group.</b> CSS composites an element
    ///     and its descendants into a layer and fades that <i>once</i>, so two overlapping children
    ///     of a half-transparent parent show the background through both of them together; here each
    ///     command carries the multiplied alpha and the overlap is drawn twice, so it comes out
    ///     darker than a browser would draw it. Doing it properly needs an offscreen target per
    ///     element that has an opacity, which is a renderer feature rather than a builder one. Said
    ///     plainly because the difference is invisible until something overlaps, and then it looks
    ///     like a blending bug rather than a known limit.
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

        // Multiplied down the walk rather than read from the cascade, because `opacity` does not
        // inherit — it makes a group, and every descendant is in it whatever its own value says.
        // A fully transparent subtree is skipped whole: it is the one case where the cheapest thing
        // to do is also exactly right, since nothing under it can be visible.
        var alpha = inherited * Alpha(element);
        if (alpha <= 0f) {
            return;
        }

        var x = element.AbsoluteLeft;
        var y = element.AbsoluteTop;
        var radius = Length(element, borderRadius);

        // Before the background, which is where CSS paints it: a shadow is cast *by* the box and
        // therefore lies under it, and an element with a translucent background shows its own shadow
        // through itself.
        EmitShadow(document, element, into, x, y, width, height, radius, alpha);

        if (Color(element, backgroundColor) is { } fill) {
            into.Add(new DrawCommand(DrawCommandKind.Rectangle, x, y, width, height, Fade(fill, alpha), radius, 0f));
        }

        // The border is drawn after the background and before the children, which is the order CSS
        // paints them in — a child overlapping the edge covers the border, and a background never
        // covers its own.
        var thickness = document.Layout.GetComputedBorder(element.LayoutNode, Edge.Top);
        if (thickness > 0f && Color(element, borderColor) is { } stroke) {
            into.Add(new DrawCommand(DrawCommandKind.Border, x, y, width, height, Fade(stroke, alpha), radius, thickness));
        }

        // Between the border and the children, which is where CSS puts an element's own content:
        // a child overlaps its parent's text, and its parent's text overlaps its parent's border.
        EmitText(document, element, into, alpha);
        element.OnDraw(new DrawContext(element, into, alpha));

        var clips = element.Style.TryGet(overflow, out var value) && value != visible;
        if (clips) {
            into.Add(new DrawCommand(DrawCommandKind.ClipPush, x, y, width, height, default, radius, 0f));
        }

        // Paint order rather than document order, which are the same list unless some child carries
        // a `z-index`. Hit testing walks the same property backwards, and that is the whole reason
        // it is a property of the element rather than a loop written twice.
        foreach (var child in element.PaintOrder) {
            Emit(document, child, into, alpha);
        }

        // ⚠ Popped only if it was pushed, and popped after the children rather than at the end of
        // the frame. A list whose pushes and pops do not pair is not a drawing with a mistake in it,
        // it is a clip stack that never unwinds — everything after the offending element stays
        // clipped to it for the rest of the frame.
        if (clips) {
            into.Add(new DrawCommand(DrawCommandKind.ClipPop, x, y, width, height, default, radius, 0f));
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
    ///     <para>
    ///         <c>text-align</c> is an offset applied to the run's origin rather than anything the
    ///         layout knows about, which works precisely because <see cref="TextRun" /> is one line:
    ///         there is one origin to move and its width is already measured. It stops working the
    ///         day text wraps, and at that point alignment belongs to whatever breaks the lines.
    ///     </para>
    /// </remarks>
    void EmitText(UiDocument document, UiElement element, DrawList into, float alpha) {
        if (element.Run() is not { } run) {
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

        left += Indent(element, content - run.Width);

        placed.Clear();
        run.Place(placed);

        if (placed.Count == 0) {
            return;
        }

        // The glyphs are placed relative to the start of the line and the command carries where that
        // is, rather than each glyph carrying an absolute position. Two identical labels in different
        // places then hold identical glyph runs, which is what will let the batcher notice.
        into.Add(
            new DrawCommand(
                DrawCommandKind.Text,
                left,
                top + run.Baseline,
                run.Width,
                run.Height,
                Fade(Color(element, textColor) ?? Color4.Black, alpha),
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

                // ⚠ `inset` lands here, and refusing the whole declaration is the point: an inset
                // shadow drawn as an outer one is not a near miss, it is a shadow on the wrong side
                // of the box.
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
            new DrawCommand(
                DrawCommandKind.Shadow,
                x + lengths[0] - spread,
                y + lengths[1] - spread,
                wide,
                tall,
                Fade(colour, alpha),
                MathF.Max(radius + spread, 0f),
                falloff
            )
        );
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

    /// <summary>Scales a colour's alpha, leaving its channels alone.</summary>
    /// <remarks>
    ///     ⚠ Not <c>colour * alpha</c>, which the operator would read as scaling all four components
    ///     — right in premultiplied space and wrong here, where it would darken the colour towards
    ///     black as well as fading it. The draw list is not premultiplied.
    /// </remarks>
    internal static Color4 Fade(Color4 color, float alpha) =>
        alpha >= 1f ? color : new Color4(color.R, color.G, color.B, color.A * alpha);

    /// <summary>Reads an element's own <c>opacity</c>, which is one when it has none.</summary>
    /// <remarks>
    ///     A percentage is accepted as well as a number, because CSS allows both and a stylesheet
    ///     written by hand is as likely to say <c>50%</c> as <c>0.5</c>. Clamped, since a value
    ///     outside the range is a mistake with an obvious intent rather than something to drop.
    /// </remarks>
    float Alpha(UiElement element) {
        if (!element.Style.TryGet(opacity, out var id)) {
            return 1f;
        }

        var value = parser.Parse(id);

        return value.Kind switch {
            StyleValueKind.Number => Math.Clamp(value.Number, 0f, 1f),
            StyleValueKind.Length when value.Unit == StyleUnit.Percent => Math.Clamp(value.Number / 100f, 0f, 1f),
            _ => 1f
        };
    }

    Color4? Color(UiElement element, int property) {
        if (!element.Style.TryGet(property, out var id)) {
            return null;
        }

        var value = parser.Parse(id);
        return value.Kind == StyleValueKind.Color ? value.Color : null;
    }

    /// <summary>Reads a corner radius.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ A corner radius arrives as <i>two</i> lengths — <c>8px 8px</c>, the horizontal and
    ///         vertical radii of an ellipse — even when the stylesheet wrote one. The horizontal one
    ///         is taken and the vertical one is dropped, which is right for every circular corner and
    ///         wrong for an elliptical one.
    ///     </para>
    ///     <para>
    ///         Likewise, <see cref="DrawCommand" /> carries a single radius where CSS has four
    ///         corners, so this reads the top-left and applies it to all of them. Both limits are
    ///         owed rather than approximated further, because a half-right rounded corner reads as a
    ///         bug in the renderer rather than a gap in the model.
    ///     </para>
    ///     <para>
    ///         Absolute lengths only. A percentage radius resolves against the box's own size, which
    ///         is a rule this builder would have to know rather than read.
    ///     </para>
    /// </remarks>
    float Length(UiElement element, int property) {
        if (!element.Style.TryGet(property, out var id)) {
            return 0f;
        }

        var value = parser.Parse(id);
        if (value.Kind == StyleValueKind.List && value.Items.Length > 0) {
            value = value.Items[0];
        }

        return value.Kind == StyleValueKind.Length && value.Unit == StyleUnit.Pixels ? value.Number : 0f;
    }
}
