// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Layout;
using Vixen.Ui.Styling;

namespace Vixen.Ui;

/// <summary>The rectangle a sticky box is held inside: CSS Position §3.3's <i>scrollport</i>.</summary>
/// <remarks>
///     ⚠ <b>The border box rather than the padding box, which is a deliberate approximation and the
///     same one the clip already makes.</b> <c>UiDocument.Cut</c> clips a scrolling ancestor's
///     descendants against <c>AbsoluteLeft .. AbsoluteLeft + Width</c>, so a sticky header held to a
///     padding box would come unstuck one padding-width before it was clipped. Two rectangles that
///     disagree by the padding is a worse answer than one rectangle that is the clip.
/// </remarks>
readonly record struct Scrollport(float Left, float Top, float Right, float Bottom) {
    /// <summary>The unbounded port, which is what an element with no scrolling ancestor gets.</summary>
    public static Scrollport None =>
        new(float.NegativeInfinity, float.NegativeInfinity, float.PositiveInfinity, float.PositiveInfinity);
}

/// <summary>
///     CSS Position §3.3's <c>position: sticky</c>, read off the computed style and applied where
///     absolute positions are assembled.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Sticky cannot live in <c>Vixen.Ui.Layout</c>, and that is a fact about the store
///         rather than a preference.</b> Doc 43 sized this as "<c>sticky</c> in the layout's position
///         handling", which reads naturally and is not available: a sticky box's offset is a function
///         of a <i>scroll offset</i>, and <c>LayoutTree</c> has no scroll offsets at all.
///         <c>ScrollView</c> scrolls by writing <see cref="UiElement.OffsetY" /> on its content —
///         explicitly "an offset, not a layout" — and that value never reaches the layout tree.
///     </para>
///     <para>
///         ⚠ <b>This paragraph used to end "a <c>PositionType.Sticky</c> would be a keyword the
///         layout could store and could not act on", and that was true of the offset and false of
///         the box.</b> CSS Position 3 § 2 lists <c>sticky</c> among the positioned values, so a
///         sticky box is the containing block of its absolutely positioned descendants — a fact that
///         needs no scroll offset to act on, and one the store was getting wrong until
///         <see cref="PositionType.Sticky" /> existed. The member is deliberately half a keyword: a
///         containing block that ignores its own inset. Everything below still runs here, because
///         everything below is the offset.
///     </para>
///     <para>
///         ⚠ <b>Where it does belong is the one walk that already assembles a position from more than
///         one contribution.</b> <c>UiDocument.Accumulate</c> turns parent-relative layout results
///         into document-space rectangles and folds in <see cref="UiElement.OffsetX" /> — the scroll
///         — and <c>translate</c>. Both consumers of a position, the draw list and the hit test, read
///         the result, so a stuck header cannot be drawn in one place and clicked in another: there
///         is no second copy of the arithmetic to get out of step. That is the same argument
///         <c>translate</c>'s own remark makes, arriving for a third contributor.
///     </para>
///     <para>
///         ⚠ <b>Anything unreadable sticks to nothing rather than to a guess</b>, and the miss is the
///         fast path: this runs once per element per pass and almost no element carries
///         <c>position: sticky</c>, so the <see cref="ComputedStyle.TryGet" /> that fails is the whole
///         cost for them.
///     </para>
/// </remarks>
sealed class StickyReader {
    readonly int position;
    readonly int sticky;
    readonly int auto;
    readonly int[] edges;
    readonly StyleValueParser parser;

    /// <summary>Interns the property names and the two keywords that decide whether to look further.</summary>
    /// <param name="properties">The table property names are interned in.</param>
    /// <param name="values">The table declaration values are interned in.</param>
    /// <param name="keywords">The table identifiers are interned in.</param>
    public StickyReader(NameTable properties, NameTable values, NameTable keywords) {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(keywords);

        position = properties.Intern("position");
        sticky = values.Intern("sticky");
        auto = values.Intern("auto");
        edges = [properties.Intern("top"), properties.Intern("right"), properties.Intern("bottom"), properties.Intern("left")];
        parser = new StyleValueParser(values, keywords);
    }

    /// <summary>Whether this element is sticky at all.</summary>
    /// <remarks>
    ///     Separate from <see cref="Of" /> so that the overwhelming majority of elements pay one
    ///     failed lookup and nothing else — no length resolution, no scrollport arithmetic, and in
    ///     particular none of the four layout-tree reads a rectangle costs.
    /// </remarks>
    public bool Is(UiElement element) {
        ArgumentNullException.ThrowIfNull(element);

        return element.Style.TryGet(position, out var id) && id == sticky;
    }

    /// <summary>
    ///     How far a sticky element has to move to stay in view, in points.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Two clamps per axis, and dropping the second one is the failure that looks
    ///         right.</b> The first holds the box inside the scrollport: a header with <c>top: 0</c>
    ///         may not be drawn above the port's top edge. The second holds it inside its own
    ///         <i>containing block</i>, so a section heading stops at the bottom of its section
    ///         instead of following the reader down the whole document — which is the entire
    ///         difference between <c>sticky</c> and <c>fixed</c>, and the reason a table of sections
    ///         reads as one heading handing over to the next.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An <c>auto</c> inset means that edge does not stick, rather than sticking at
    ///         zero.</b> §3.3 is explicit and the difference is observable: <c>position: sticky</c>
    ///         with no inset at all is an ordinary in-flow box, and treating a missing <c>top</c> as
    ///         <c>top: 0</c> would pin every sticky box in the document to the top of its scroller.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The shift is never against the direction of travel.</b> A <c>top</c> inset can
    ///         only move a box <i>down</i> — <c>MathF.Max</c> — because a box already below the port's
    ///         top edge has not been scrolled past yet and must stay where the flow put it. Using the
    ///         inset as an assignment rather than as a floor is what turns a sticky header into a
    ///         fixed one that ignores its own section.
    ///     </para>
    /// </remarks>
    /// <param name="element">The sticky element, already at its natural accumulated position.</param>
    /// <param name="metrics">The lengths <c>em</c>, <c>rem</c> and the viewport units resolve against.</param>
    /// <param name="port">The nearest scrolling ancestor's box, in document coordinates.</param>
    /// <param name="x">Receives the horizontal shift, zero when there is none.</param>
    /// <param name="y">Receives the vertical shift.</param>
    public void Of(UiElement element, LengthContext metrics, in Scrollport port, out float x, out float y) {
        ArgumentNullException.ThrowIfNull(element);

        x = 0f;
        y = 0f;

        var parent = element.Parent;

        if (parent is null) {
            return;
        }

        var width = element.Width;
        var height = element.Height;

        // The containing block, which is the second clamp. A sticky box may leave its parent's box
        // only as far as the flow already carried it, so the parent's own accumulated rectangle is
        // both bounds.
        var boundLeft = parent.AbsoluteLeft;
        var boundTop = parent.AbsoluteTop;
        var boundRight = boundLeft + parent.Width;
        var boundBottom = boundTop + parent.Height;

        var top = Inset(element, 0, metrics, parent.Height);
        var right = Inset(element, 1, metrics, parent.Width);
        var bottom = Inset(element, 2, metrics, parent.Height);
        var left = Inset(element, 3, metrics, parent.Width);

        var wantedTop = element.AbsoluteTop;

        if (!float.IsNaN(top)) {
            wantedTop = MathF.Max(wantedTop, port.Top + top);
        }

        if (!float.IsNaN(bottom)) {
            wantedTop = MathF.Min(wantedTop, port.Bottom - bottom - height);
        }

        var wantedLeft = element.AbsoluteLeft;

        if (!float.IsNaN(left)) {
            wantedLeft = MathF.Max(wantedLeft, port.Left + left);
        }

        if (!float.IsNaN(right)) {
            wantedLeft = MathF.Min(wantedLeft, port.Right - right - width);
        }

        // ⚠ Clamped rather than skipped when the containing block is shorter than the box: the
        // `Min` runs first so a box taller than its own section is held at the section's top edge,
        // which is where a browser puts it too.
        wantedTop = MathF.Max(boundTop, MathF.Min(wantedTop, boundBottom - height));
        wantedLeft = MathF.Max(boundLeft, MathF.Min(wantedLeft, boundRight - width));

        x = wantedLeft - element.AbsoluteLeft;
        y = wantedTop - element.AbsoluteTop;
    }

    /// <summary>One inset in points, or NaN when the edge does not stick.</summary>
    float Inset(UiElement element, int edge, LengthContext metrics, float against) {
        if (!element.Style.TryGet(edges[edge], out var id) || id == auto) {
            return float.NaN;
        }

        var length = metrics.ToLength(parser.Parse(id));

        return length.Unit switch {
            LayoutUnit.Point => length.Value,
            LayoutUnit.Percent => length.Value / 100f * against,
            _ => float.NaN
        };
    }
}
