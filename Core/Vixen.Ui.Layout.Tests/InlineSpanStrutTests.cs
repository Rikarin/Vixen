// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Layout.Tests;

/// <summary>
///     A flattened <c>span</c>'s own strut: CSS 2.1 §10.8.1's <i>parent inline box</i> for the boxes
///     inside it, and an inline box of its own on every line it reaches (#249).
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The refusal this replaces said a span "has no font of its own" and that supplying one
///         was <c>Vixen.Ui</c>'s job. The second half was already done.</b>
///         <c>UiDocument</c> writes <c>StrutOf</c> onto EVERY element's layout node after
///         <c>SetStyle</c>, a span's included; what was missing was this store reading it. So the
///         whole change is on this side of the bridge, and an all-zero strut on a span still means
///         "no font", which falls back to the nearest one above it and finally to the container's.
///     </para>
///     <para>
///         Closed-form, in <see cref="InlineStrutTests" />'s made-up 20-point face (21/9 line box,
///         16/4 content area, x-height 10) and a second, 40-point one at <c>line-height: 60</c>
///         (content 32/8, half-leading 10, so 42/18; x-height 20). Each number is derived in its
///         test's remark. A browser reading would be a reading of a font, so none was taken.
///     </para>
/// </remarks>
public class InlineSpanStrutTests {
    const float Tolerance = 0.0001f;

    static readonly StrutMetrics Face = new(21f, 9f, 16f, 4f, 10f, 4f, 8f);

    static readonly StrutMetrics Big = new(42f, 18f, 32f, 8f, 20f, 8f, 16f);

    /// <summary>
    ///     ⚠ <b>A span's inline box is on the line, so its line height is too.</b> §10.8.1: the line
    ///     box encloses every inline box on it, and a non-replaced inline box is exactly its
    ///     <c>line-height</c> tall about its baseline. The 40-point span's 42/18 beats the container's
    ///     21/9, so the line is 60 and the 10-point box inside hangs from 42, at 32. It was 30 and 11:
    ///     the container's strut alone.
    /// </summary>
    [Fact]
    public void A_spans_line_height_grows_the_line_it_is_on() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f, Face);
        var span = Span(tree, root, Big);
        var box = Item(tree, span, 10f, 40f);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(60f, tree.GetHeight(root), Tolerance);
        Assert.Equal(32f, tree.GetTop(box), Tolerance);
    }

    /// <summary>
    ///     <c>middle</c> inside a span takes half the SPAN's x-height. The span's line box is the
    ///     container's (21/9) so the line cannot move, and only the x-height differs: 16 instead of
    ///     10. The 20-point box's midpoint goes to 21 − 8 = 13, top 3. Against the container's
    ///     x-height it was 16, top 6; refused to <c>baseline</c> it would be 1. ⚠ The x-height is 16
    ///     and not a rounder 20 because 20 is exactly the x-height at which <c>middle</c> and
    ///     <c>baseline</c> coincide for a 20-point box, and a test that cannot tell honouring from
    ///     refusing is not testing the fallback.
    /// </summary>
    [Fact]
    public void Middle_inside_a_span_is_measured_against_the_spans_x_height() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f, Face);
        var span = Span(tree, root, Face with { XHeight = 16f });
        var box = Item(tree, span, 20f, 40f);
        tree.SetVerticalAlign(box, VerticalAlign.Middle);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(30f, tree.GetHeight(root), Tolerance);
        Assert.Equal(3f, tree.GetTop(box), Tolerance);
    }

    /// <summary>
    ///     <c>text-top</c> inside a span aligns to the SPAN's content area: a 12-point text ascent puts
    ///     the box's top at 21 − 12 = 9. Against the container's 16 it was 5.
    /// </summary>
    [Fact]
    public void Text_top_inside_a_span_is_the_spans_content_area() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f, Face);
        var span = Span(tree, root, Face with { TextAscent = 12f });
        var box = Item(tree, span, 10f, 40f);
        tree.SetVerticalAlign(box, VerticalAlign.TextTop);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(9f, tree.GetTop(box), Tolerance);
    }

    /// <summary>
    ///     A span that was given no font falls back to the container's, so <c>middle</c> is honoured
    ///     rather than refused and lands where it did before: top 6, line 30.
    /// </summary>
    [Fact]
    public void A_span_with_no_font_uses_the_containers_strut() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f, Face);
        var span = Span(tree, root, default);
        var box = Item(tree, span, 20f, 40f);
        tree.SetVerticalAlign(box, VerticalAlign.Middle);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(30f, tree.GetHeight(root), Tolerance);
        Assert.Equal(6f, tree.GetTop(box), Tolerance);
    }

    /// <summary>
    ///     A fontless span inside a span with one falls back to the OUTER span, not past it: the
    ///     outer's x-height of 16 puts the 20-point box at top 3, as in the single-span case. Skipping
    ///     to the container would give 6, and taking the inner span's empty strut as a refusal 1.
    /// </summary>
    [Fact]
    public void A_fontless_span_inside_a_span_falls_back_to_the_nearest_font() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f, Face);
        var outer = Span(tree, root, Face with { XHeight = 16f });
        var inner = Span(tree, outer, default);
        var box = Item(tree, inner, 20f, 40f);
        tree.SetVerticalAlign(box, VerticalAlign.Middle);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(3f, tree.GetTop(box), Tolerance);
    }

    /// <summary>
    ///     ⚠ <b>Every line a span reaches has a fragment of its inline box on it</b>, including a line
    ///     whose <c>Open</c> was on an earlier one — which is why the span is found from each item's
    ///     ancestry rather than from the items between its <c>Open</c> and <c>Close</c>. Three 40-wide
    ///     boxes in a 100-wide container break two and one; both lines are the span's 60, so the
    ///     third box hangs from 60 + 42 at 92 and the container is 120. Reading only an <c>Open</c> on
    ///     the line would leave the second line at 30, the third box at 71 and the container at 90.
    /// </summary>
    [Fact]
    public void A_span_continued_onto_a_second_line_grows_that_line_too() {
        using var tree = new LayoutTree();
        var root = Root(tree, 100f, Face);
        var span = Span(tree, root, Big);
        Item(tree, span, 10f, 40f);
        Item(tree, span, 10f, 40f);
        var third = Item(tree, span, 10f, 40f);

        tree.CalculateLayout(root, 100f, float.NaN, Direction.Ltr);

        Assert.Equal(120f, tree.GetHeight(root), Tolerance);
        Assert.Equal(92f, tree.GetTop(third), Tolerance);
    }

    /// <summary>
    ///     An ATOMIC item's own strut is still inert — the boxes on a line that are not inline boxes
    ///     contribute their margin boxes and nothing else. Pinned beside the span cases because the
    ///     change reads a strut off a node that is not the container, and must read it off no other.
    /// </summary>
    [Fact]
    public void An_atomic_items_own_strut_is_still_inert_inside_a_span() {
        using var tree = new LayoutTree();
        var root = Root(tree, 300f, Face);
        var span = Span(tree, root, default);
        var box = Item(tree, span, 10f, 40f);
        tree.SetStrut(box, Big);

        tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

        Assert.Equal(30f, tree.GetHeight(root), Tolerance);
        Assert.Equal(11f, tree.GetTop(box), Tolerance);
    }

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
