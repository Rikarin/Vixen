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
    readonly int visible;
    readonly int visibility;
    readonly int hidden;
    readonly int opacity;

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
        this.visible = values.Intern("visible");

        visibility = properties.Intern("visibility");
        this.hidden = values.Intern("hidden");
        opacity = properties.Intern("opacity");
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
        var radius = Length(element, borderRadius);

        // ⚠ `visibility: hidden` hides the element and *not* its subtree, which is what separates it
        // from `display: none`. It is an inherited property, so a child is hidden by having
        // inherited the value rather than by being skipped here — and a child that declares
        // `visibility: visible` reappears inside a hidden parent, which is the whole reason CSS has
        // two properties for this.
        var shown = !element.Style.TryGet(visibility, out var mode) || mode != hidden;

        if (shown) {
            if (Color(element, backgroundColor) is { } fill) {
                into.Add(new DrawCommand(DrawCommandKind.Rectangle, x, y, width, height, Fade(fill, alpha), radius, 0f));
            }

            // The border is drawn after the background and before the children, which is the order
            // CSS paints them in — a child overlapping the edge covers the border, and a background
            // never covers its own.
            var thickness = document.Layout.GetComputedBorder(element.LayoutNode, Edge.Top);
            if (thickness > 0f && Color(element, borderColor) is { } stroke) {
                into.Add(new DrawCommand(DrawCommandKind.Border, x, y, width, height, Fade(stroke, alpha), radius, thickness));
            }

            // Between the border and the children, which is where CSS puts an element's own content:
            // a child overlaps its parent's text, and its parent's text overlaps its parent's border.
            EmitText(document, element, into, alpha);
            element.OnDraw(new DrawContext(element, into));
        }

        var clips = element.Style.TryGet(overflow, out var value) && value != visible;
        if (clips) {
            into.Add(new DrawCommand(DrawCommandKind.ClipPush, x, y, width, height, default, radius, 0f));
        }

        foreach (var child in element.Children) {
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
    /// </remarks>
    void EmitText(UiDocument document, UiElement element, DrawList into, float alpha) {
        if (element.Run() is not { } run) {
            return;
        }

        var left = element.AbsoluteLeft
            + document.Layout.GetComputedBorder(element.LayoutNode, Edge.Left)
            + document.Layout.GetComputedPadding(element.LayoutNode, Edge.Left);

        var top = element.AbsoluteTop
            + document.Layout.GetComputedBorder(element.LayoutNode, Edge.Top)
            + document.Layout.GetComputedPadding(element.LayoutNode, Edge.Top);

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
    static Color4 Fade(Color4 colour, float alpha) =>
        alpha >= 1f ? colour : new Color4(colour.R, colour.G, colour.B, colour.A * alpha);

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
