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
    readonly StyleValueParser parser;
    readonly int backgroundColor;
    readonly int borderColor;
    readonly int borderRadius;
    readonly int overflow;
    readonly int visible;

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
        overflow = properties.Intern("overflow");
        this.visible = values.Intern("visible");
    }

    /// <summary>Walks a document and fills a draw list.</summary>
    /// <param name="document">The document, already updated.</param>
    /// <param name="into">The list to fill.</param>
    /// <returns>Whether the drawing differs from the previous frame's.</returns>
    public bool Build(UiDocument document, DrawList into) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(into);

        into.BeginFrame();
        Emit(document, document.Root, into);
        return into.EndFrame();
    }

    void Emit(UiDocument document, UiElement element, DrawList into) {
        var width = element.Width;
        var height = element.Height;

        // A zero-sized element draws nothing and clips nothing, and skipping it early keeps
        // `display: none` — which flexbox reports as a zero box — out of the list entirely rather
        // than in it as a stack of invisible commands.
        if (width <= 0f || height <= 0f) {
            return;
        }

        var x = element.AbsoluteLeft;
        var y = element.AbsoluteTop;
        var radius = Length(element, borderRadius);

        if (Color(element, backgroundColor) is { } fill) {
            into.Add(new DrawCommand(DrawCommandKind.Rectangle, x, y, width, height, fill, radius, 0f));
        }

        // The border is drawn after the background and before the children, which is the order CSS
        // paints them in — a child overlapping the edge covers the border, and a background never
        // covers its own.
        var thickness = document.Layout.GetComputedBorder(element.LayoutNode, Edge.Top);
        if (thickness > 0f && Color(element, borderColor) is { } stroke) {
            into.Add(new DrawCommand(DrawCommandKind.Border, x, y, width, height, stroke, radius, thickness));
        }

        var clips = element.Style.TryGet(overflow, out var value) && value != visible;
        if (clips) {
            into.Add(new DrawCommand(DrawCommandKind.ClipPush, x, y, width, height, default, radius, 0f));
        }

        foreach (var child in element.Children) {
            Emit(document, child, into);
        }

        // ⚠ Popped only if it was pushed, and popped after the children rather than at the end of
        // the frame. A list whose pushes and pops do not pair is not a drawing with a mistake in it,
        // it is a clip stack that never unwinds — everything after the offending element stays
        // clipped to it for the rest of the frame.
        if (clips) {
            into.Add(new DrawCommand(DrawCommandKind.ClipPop, x, y, width, height, default, radius, 0f));
        }
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
