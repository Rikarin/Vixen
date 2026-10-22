// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Layout.Tests;

/// <summary>
///     A flattened <c>span</c>'s own <c>vertical-align</c> (#1478), and an EMPTY span's inline box
///     (#1479) — the two rows <see cref="InlineSpanStrutTests" /> left owed.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>CSS 2.1 §10.8.1 aligns every box against its PARENT inline box's baseline, and a
///         span is a box.</b> A <c>super</c> span is raised by its parent's superscript offset, and
///         everything inside it hangs from the raised baseline — so a box in a <c>super</c> span
///         inside a <c>sub</c> span is moved by both. <c>top</c> and <c>bottom</c> make the span the
///         root of an <i>aligned subtree</i>: the span and what hangs from it are measured together
///         and the whole is pinned to the line box's edge.
///     </para>
///     <para>
///         ⚠ <b>The trap the issue named is real, and every test here was chosen to step around
///         it</b>: when the shifted span is the only thing on the line, the line simply wraps it and
///         the box inside often lands at the same top it had before. So each case either asserts the
///         line height, or puts a 50-tall baseline sibling beside the span to pin the line's
///         baseline where the shift is visible.
///     </para>
///     <para>
///         Closed-form, in <see cref="InlineSpanStrutTests" />' faces: the container's 21/9 line box
///         (content 16/4, x-height 10, sub 4, super 8) and the 40-point 42/18 one. Every number is
///         derived in its test's summary, with the old code's answer beside it.
///     </para>
/// </remarks>
public class InlineSpanAlignmentTests {
    const float Tolerance = 0.0001f;

    static readonly StrutMetrics Face = new(21f, 9f, 16f, 4f, 10f, 4f, 8f);

    static readonly StrutMetrics Big = new(42f, 18f, 32f, 8f, 20f, 8f, 16f);

    // ── #1478: a span's own vertical-align ───────────────────────────────────────────────────────

    /// <summary>
    ///     The issue's own oracle. The span's inline box is raised 8, so it reaches 21 + 8 = 29 above
    ///     the line's baseline and the line is 29 + 9 = 38. The box hangs from the span's baseline at
    ///     29 − 8 = 21, so its top is 11 — which is ALSO the old answer, the trap; the line was 30.
    ///     The span's own box is its line height about its raised baseline: 21 − 21 = 0, 30 tall.
    /// </summary>
    [Fact]
    public void A_super_span_raises_its_inline_box_and_grows_the_line() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f, Face);
        var span = Span(tree, root, Face);
        tree.SetVerticalAlign(span, VerticalAlign.Super);
        var box = Item(tree, span, 10f, 40f);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(38f, tree.GetHeight(root), Tolerance);
        Assert.Equal(11f, AbsoluteTop(tree, span, box), Tolerance);
        Assert.Equal(0f, tree.GetTop(span), Tolerance);
        Assert.Equal(30f, tree.GetHeight(span), Tolerance);
    }

    /// <summary>
    ///     <c>sub</c> lowers the span 4: the line's descent becomes 9 + 4 = 13, so it is 34; the span's
    ///     baseline is at 25, its box 4..34, and the box inside it at 15. Old: line 30, box 11, span 0.
    /// </summary>
    [Fact]
    public void A_sub_span_lowers_its_inline_box_and_the_box_inside_it() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f, Face);
        var span = Span(tree, root, Face);
        tree.SetVerticalAlign(span, VerticalAlign.Sub);
        var box = Item(tree, span, 10f, 40f);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(34f, tree.GetHeight(root), Tolerance);
        Assert.Equal(15f, AbsoluteTop(tree, span, box), Tolerance);
        Assert.Equal(4f, tree.GetTop(span), Tolerance);
        Assert.Equal(30f, tree.GetHeight(span), Tolerance);
    }

    /// <summary>
    ///     <c>middle</c> on a 30/10 span: its midpoint goes to the parent's baseline plus half the
    ///     PARENT's x-height, a shift of 30 − 20 − 5 = 5 down. A 50-tall baseline sibling pins the
    ///     line's baseline at 50, so the span's is at 55, its box 25..65, the box inside it at 45, and
    ///     the line 50 + max(9, 10 + 5) = 65. Old: the span was a 30/10 box ON the baseline — line
    ///     60, box 40.
    /// </summary>
    [Fact]
    public void A_middle_span_is_centred_on_its_parents_x_height() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f, Face);
        Item(tree, root, 50f, 20f);
        var span = Span(tree, root, Face with { Ascent = 30f, Descent = 10f });
        tree.SetVerticalAlign(span, VerticalAlign.Middle);
        var box = Item(tree, span, 10f, 40f);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(65f, tree.GetHeight(root), Tolerance);
        Assert.Equal(45f, AbsoluteTop(tree, span, box), Tolerance);
        Assert.Equal(25f, tree.GetTop(span), Tolerance);
        Assert.Equal(40f, tree.GetHeight(span), Tolerance);
    }

    /// <summary>
    ///     ⚠ <b>Shifts accumulate down the nesting.</b> A <c>super</c> span inside a <c>sub</c> span:
    ///     +4 then −8, so the inner baseline is 4 ABOVE the line's. A 50-tall sibling pins the line's
    ///     baseline at 50, so the box hangs from 46 at 36, and the line's descent is the outer span's
    ///     9 + 4 = 13: 63. Reading only the inner span's shift would put the box at 32; the old code
    ///     put it at 40 on a 59 line.
    /// </summary>
    [Fact]
    public void A_super_span_inside_a_sub_span_is_shifted_by_both() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f, Face);
        Item(tree, root, 50f, 20f);
        var outer = Span(tree, root, Face);
        tree.SetVerticalAlign(outer, VerticalAlign.Sub);
        var inner = Span(tree, outer, Face);
        tree.SetVerticalAlign(inner, VerticalAlign.Super);
        var box = Item(tree, inner, 10f, 40f);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(63f, tree.GetHeight(root), Tolerance);
        Assert.Equal(36f, tree.GetTop(outer) + tree.GetTop(inner) + tree.GetTop(box), Tolerance);
    }

    /// <summary>
    ///     <c>top</c> makes the span an aligned subtree pinned to the line's top. Round one: the
    ///     50-tall sibling and the strut give 50/9. Round two: the subtree is the span's 42/18 = 60,
    ///     one more than 59, so the line grows DOWN by 1 to 60 and its baseline stays at 50 (sibling at
    ///     0). The span's baseline is at 42 and the box at 32. Old: the span's 42/18 joined round one
    ///     on the baseline — line 50 + 18 = 68, box 40.
    /// </summary>
    [Fact]
    public void A_top_span_is_an_aligned_subtree_pinned_to_the_lines_top() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f, Face);
        var sibling = Item(tree, root, 50f, 20f);
        var span = Span(tree, root, Big);
        tree.SetVerticalAlign(span, VerticalAlign.Top);
        var box = Item(tree, span, 10f, 40f);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(60f, tree.GetHeight(root), Tolerance);
        Assert.Equal(0f, tree.GetTop(sibling), Tolerance);
        Assert.Equal(32f, AbsoluteTop(tree, span, box), Tolerance);
        Assert.Equal(0f, tree.GetTop(span), Tolerance);
        Assert.Equal(60f, tree.GetHeight(span), Tolerance);
    }

    /// <summary>
    ///     <c>bottom</c>: the same 60-tall subtree grows the line UP by 1, so the baseline moves to 51
    ///     and the sibling to 1 — which is what tells <c>bottom</c> from <c>top</c> here. The span's
    ///     baseline is 60 − 18 = 42 and the box 32.
    /// </summary>
    [Fact]
    public void A_bottom_span_is_pinned_to_the_lines_bottom() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f, Face);
        var sibling = Item(tree, root, 50f, 20f);
        var span = Span(tree, root, Big);
        tree.SetVerticalAlign(span, VerticalAlign.Bottom);
        var box = Item(tree, span, 10f, 40f);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(60f, tree.GetHeight(root), Tolerance);
        Assert.Equal(1f, tree.GetTop(sibling), Tolerance);
        Assert.Equal(32f, AbsoluteTop(tree, span, box), Tolerance);
    }

    /// <summary>
    ///     A baseline span is untouched: its fragment is still the whole line box, which is what every
    ///     background and selection highlight in the tree was drawn against before this change.
    /// </summary>
    [Fact]
    public void A_baseline_span_keeps_the_line_box_as_its_fragment() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f, Face);
        Item(tree, root, 50f, 20f);
        var span = Span(tree, root, Face);
        var box = Item(tree, span, 10f, 40f);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(59f, tree.GetHeight(root), Tolerance);
        Assert.Equal(0f, tree.GetTop(span), Tolerance);
        Assert.Equal(59f, tree.GetHeight(span), Tolerance);
        Assert.Equal(40f, AbsoluteTop(tree, span, box), Tolerance);
    }

    // ── #1479: an empty span's inline box ────────────────────────────────────────────────────────

    /// <summary>
    ///     The issue's oracle: an EMPTY span is an inline box on the line all the same, exactly its
    ///     line height about the baseline. Its 42/18 beats the container's 21/9, so the line is 60
    ///     and the 10-point box beside it hangs from 42 at 32. Old: the span was an atomic zero-size
    ///     box with an inert strut — 30 and 11.
    /// </summary>
    [Fact]
    public void An_empty_spans_line_height_grows_the_line() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f, Face);
        var span = Span(tree, root, Big);
        var box = Item(tree, root, 10f, 40f);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(60f, tree.GetHeight(root), Tolerance);
        Assert.Equal(32f, tree.GetTop(box), Tolerance);
        Assert.Equal(0f, tree.GetWidth(span), Tolerance);
        Assert.Equal(1, tree.GetFragmentCount(span));
    }

    /// <summary>
    ///     An empty span whose own <c>vertical-align</c> is <c>super</c> is raised like a full one:
    ///     21 + 8 = 29 above the baseline, so the line is 29 + 9 = 38 beside a 10-point box at 19.
    /// </summary>
    [Fact]
    public void An_empty_super_span_is_raised_like_a_full_one() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f, Face);
        var span = Span(tree, root, Face);
        tree.SetVerticalAlign(span, VerticalAlign.Super);
        var box = Item(tree, root, 10f, 40f);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(38f, tree.GetHeight(root), Tolerance);
        Assert.Equal(19f, tree.GetTop(box), Tolerance);
    }

    /// <summary>
    ///     ⚠ <b>CSS 2.1 §9.4.2: a line holding nothing but an empty inline box with no margin, border
    ///     or padding is a ZERO-height line box.</b> So a container whose only child is a bare empty
    ///     span is 0 tall — and the span is still laid out, at the content corner, zero wide. Old: an
    ///     atomic zero-size box counted as content and the line was the strut's 30.
    /// </summary>
    [Fact]
    public void A_line_holding_only_a_bare_empty_span_is_zero_height() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f, Face);
        tree.SetPadding(root, Edge.Left, StyleLength.Points(7f));
        tree.SetPadding(root, Edge.Top, StyleLength.Points(3f));
        var span = Span(tree, root, Big);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(3f, tree.GetHeight(root), Tolerance);
        Assert.Equal(7f, tree.GetLeft(span), Tolerance);
        Assert.Equal(3f, tree.GetTop(span), Tolerance);
        Assert.Equal(0f, tree.GetWidth(span), Tolerance);
        Assert.Equal(1, tree.GetFragmentCount(span));
    }

    /// <summary>
    ///     The other half of §9.4.2: an empty span WITH horizontal padding is not nothing, so its line
    ///     is a real one — the strut's 30 beside the span's own 42/18 is 60 — and the span is the
    ///     padding wide.
    /// </summary>
    [Fact]
    public void A_line_holding_only_a_padded_empty_span_is_a_real_line() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f, Face);
        var span = Span(tree, root, Big);
        tree.SetPadding(span, Edge.Left, StyleLength.Points(5f));

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(60f, tree.GetHeight(root), Tolerance);
        Assert.Equal(5f, tree.GetWidth(span), Tolerance);
    }

    static float AbsoluteTop(LayoutTree tree, LayoutNodeId span, LayoutNodeId box) => tree.GetTop(span) + tree.GetTop(box);

    static LayoutNodeId Root(LayoutTree tree, float width, StrutMetrics strut) {
        var root = tree.CreateNode();
        tree.SetDisplay(root, Display.Block);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(width));
        tree.SetStrut(root, strut);

        return root;
    }

    static LayoutNodeId Span(LayoutTree tree, LayoutNodeId parent, StrutMetrics strut) {
        var span = tree.CreateNode();
        tree.SetDisplay(span, Display.Inline);
        tree.SetStrut(span, strut);
        tree.AddChild(parent, span);

        return span;
    }

    static LayoutNodeId Item(LayoutTree tree, LayoutNodeId parent, float height, float width) {
        var node = tree.CreateNode();
        tree.SetDisplay(node, Display.InlineBlock);
        tree.SetDimension(node, Dimension.Height, StyleLength.Points(height));
        tree.SetDimension(node, Dimension.Width, StyleLength.Points(width));
        tree.AddChild(parent, node);

        return node;
    }
}
