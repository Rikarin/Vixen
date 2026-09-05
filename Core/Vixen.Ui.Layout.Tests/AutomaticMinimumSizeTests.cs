// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Layout.Tests;

/// <summary>
///     CSS Flexbox §4.5 — the automatic minimum main size a flex item has even when nothing
///     declares one.
/// </summary>
/// <remarks>
///     These are hand-written because the ported Yoga suite does not reach this code: sabotaging the
///     floor leaves all 534 fixtures green. That is a fact about the fixtures rather than about the
///     rule — Yoga's generator emits no fixture that shrinks a measured leaf past its content — and
///     an implementation of a specification section with no test over it is the thing doc 14 warns
///     about. So the rule gets a test that fails without it.
/// </remarks>
public class AutomaticMinimumSizeTests {
    const float Tolerance = 0.0001f;

    [Fact]
    public void A_shrinking_item_does_not_go_below_the_size_its_content_needs() {
        // 300 points of text in a 100-point row. Without §4.5 the item shrinks to 100 and the text
        // is clipped; with it the item stops at its min-content size and the row overflows instead,
        // which is what a browser does.
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Row);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(100f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(50f));

        var child = tree.CreateNode();
        tree.SetFlexShrink(child, 1f);
        tree.SetFlexBasis(child, StyleLength.Points(300f));
        tree.SetContext(child, 300f);
        tree.SetMeasureFunction(child, MeasureFixedContent);
        tree.AddChild(root, child);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(300f, tree.GetWidth(child), Tolerance);
    }

    [Fact]
    public void An_explicit_minimum_of_zero_opts_out_of_the_automatic_one() {
        // The specification's own escape hatch: writing any min-width, including 0, means the
        // author has said what the minimum is and the automatic one does not apply.
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Row);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(100f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(50f));

        var child = tree.CreateNode();
        tree.SetFlexShrink(child, 1f);
        tree.SetFlexBasis(child, StyleLength.Points(300f));
        tree.SetMinDimension(child, Dimension.Width, StyleLength.Points(0f));
        tree.SetContext(child, 300f);
        tree.SetMeasureFunction(child, MeasureFixedContent);
        tree.AddChild(root, child);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(100f, tree.GetWidth(child), Tolerance);
    }

    [Fact]
    public void An_item_that_clips_its_own_overflow_opts_out_too() {
        // §4.5 again: an item that handles overflow itself does not need a content-based floor.
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Row);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(100f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(50f));

        var child = tree.CreateNode();
        tree.SetFlexShrink(child, 1f);
        tree.SetFlexBasis(child, StyleLength.Points(300f));
        tree.SetOverflow(child, Overflow.Hidden);
        tree.SetContext(child, 300f);
        tree.SetMeasureFunction(child, MeasureFixedContent);
        tree.AddChild(root, child);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(100f, tree.GetWidth(child), Tolerance);
    }

    [Theory]
    [InlineData(Overflow.Hidden, Overflow.Visible, 100f)]
    [InlineData(Overflow.Visible, Overflow.Hidden, 300f)]
    [InlineData(Overflow.Visible, Overflow.Scroll, 300f)]
    public void The_opt_out_is_the_main_axis_s_own_overflow(Overflow horizontal, Overflow vertical, float expected) {
        // ⚠ §4.5's escape hatch is per axis in the specification — "overflow other than visible in
        // the main axis" — and the container here is a row, so only `overflow-x` can open it. An item
        // that clips what hangs *below* it has said nothing about being squeezed sideways, and a
        // reading that collapsed the two would silently drop the floor from every panel in the editor
        // that scrolls vertically.
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Row);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(100f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(50f));

        var child = tree.CreateNode();
        tree.SetFlexShrink(child, 1f);
        tree.SetFlexBasis(child, StyleLength.Points(300f));
        tree.SetOverflow(child, horizontal, vertical);
        tree.SetContext(child, 300f);
        tree.SetMeasureFunction(child, MeasureFixedContent);
        tree.AddChild(root, child);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(expected, tree.GetWidth(child), Tolerance);
    }

    [Fact]
    public void The_floor_never_exceeds_what_the_item_asked_for() {
        // The floor is min(content, specified): an item with a 150-point width does not get a
        // 300-point floor just because its content is that wide.
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Row);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(100f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(50f));

        var child = tree.CreateNode();
        tree.SetFlexShrink(child, 1f);
        tree.SetDimension(child, Dimension.Width, StyleLength.Points(150f));
        tree.SetContext(child, 300f);
        tree.SetMeasureFunction(child, MeasureFixedContent);
        tree.AddChild(root, child);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(150f, tree.GetWidth(child), Tolerance);
    }

    [Fact]
    public void The_floor_also_applies_to_an_item_that_is_itself_a_container() {
        // ⚠ The largest thing Taffy's corpus found, and the case both hand-written suites missed:
        // every test above gives the item a *measure function*, so §4.5's content size suggestion
        // always came from a leaf. A flex item that is a container has a min-content size too —
        // CSS Sizing §5.2.2, computed from its children's contributions — and it was reported as
        // zero, because ComputeMinContentSizeUncached took its childless branch for the grandchild
        // and a box with no contents needs no room for them.
        //
        // This is align_baseline_child_padding restated in border-box terms. Two items totalling 110
        // in a 100-point row: the second wraps a 50-point child in 5 points of padding, so it cannot
        // go below 60, and Chrome gives the whole 10 points of overflow to the first. Without the
        // floor both shrink proportionally to 45 and 55.
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Row);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(110f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(110f));
        tree.SetPadding(root, Edge.All, StyleLength.Points(5f));

        var plain = tree.CreateNode();
        tree.SetFlexShrink(plain, 1f);
        tree.SetDimension(plain, Dimension.Width, StyleLength.Points(50f));
        tree.SetDimension(plain, Dimension.Height, StyleLength.Points(50f));
        tree.AddChild(root, plain);

        var padded = tree.CreateNode();
        tree.SetFlexShrink(padded, 1f);
        tree.SetFlexDirection(padded, FlexDirection.Column);
        tree.SetDimension(padded, Dimension.Width, StyleLength.Points(60f));
        tree.SetDimension(padded, Dimension.Height, StyleLength.Points(30f));
        tree.SetPadding(padded, Edge.All, StyleLength.Points(5f));
        tree.AddChild(root, padded);

        var grandchild = tree.CreateNode();
        tree.SetDimension(grandchild, Dimension.Width, StyleLength.Points(50f));
        tree.SetDimension(grandchild, Dimension.Height, StyleLength.Points(10f));
        tree.AddChild(padded, grandchild);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(60f, tree.GetWidth(padded), Tolerance);
        Assert.Equal(40f, tree.GetWidth(plain), Tolerance);
    }

    [Fact]
    public void A_wrapping_item_is_floored_at_its_widest_child_not_at_their_sum() {
        // ⚠ CSS Flexbox §9.9.1: the min-content main size of a *single-line* flex container is the
        // sum of its items' contributions, but a multi-line one may break between any two of them,
        // so the smallest it can be is its widest single item. The distinction was unreachable while
        // every childless item contributed zero — sum and maximum were both zero — and reading it
        // wrong is not academic: it fails three of Yoga's own fixtures, which is how it was caught.
        //
        // Three 40-point items inside a wrapping 60-point box. Its floor is 40, not 120, so it is
        // free to shrink to 50 alongside its sibling rather than freezing at 60 and pushing the
        // whole overflow onto it.
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Row);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(100f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(100f));

        var plain = tree.CreateNode();
        tree.SetFlexShrink(plain, 1f);
        tree.SetDimension(plain, Dimension.Width, StyleLength.Points(60f));
        tree.SetDimension(plain, Dimension.Height, StyleLength.Points(20f));
        tree.AddChild(root, plain);

        var wrapping = tree.CreateNode();
        tree.SetFlexShrink(wrapping, 1f);
        tree.SetFlexDirection(wrapping, FlexDirection.Row);
        tree.SetFlexWrap(wrapping, Wrap.Wrap);
        tree.SetDimension(wrapping, Dimension.Width, StyleLength.Points(60f));
        tree.SetDimension(wrapping, Dimension.Height, StyleLength.Points(20f));
        tree.AddChild(root, wrapping);

        for (var i = 0; i < 3; i++) {
            var item = tree.CreateNode();
            tree.SetDimension(item, Dimension.Width, StyleLength.Points(40f));
            tree.SetDimension(item, Dimension.Height, StyleLength.Points(10f));
            tree.AddChild(wrapping, item);
        }

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(50f, tree.GetWidth(plain), Tolerance);
        Assert.Equal(50f, tree.GetWidth(wrapping), Tolerance);
    }

    [Fact]
    public void A_percentage_wide_grandchild_contributes_nothing_to_the_floor() {
        // ⚠ CSS Sizing §5.2.1: while calculating intrinsic contributions a percentage resolved
        // against a containing block whose size is not yet known behaves as `auto`. That is exactly
        // the situation the §4.5 probe is in — it is asking how small the parent may be, so the
        // parent has no size to be a fraction of.
        //
        // Reading `width: 100%` as definite here resolves it against whatever owner size happens to
        // be threaded down from an ancestor, and both corpora catch it at once: Yoga's
        // Percent_within_flex_grow and Taffy's percent_within_flex_grow are the same fixture. The
        // percentage child contributes 0, so its parent's floor stays 0 and the two siblings share
        // the overflow evenly.
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Row);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(100f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(100f));

        var plain = tree.CreateNode();
        tree.SetFlexShrink(plain, 1f);
        tree.SetDimension(plain, Dimension.Width, StyleLength.Points(60f));
        tree.SetDimension(plain, Dimension.Height, StyleLength.Points(20f));
        tree.AddChild(root, plain);

        var holder = tree.CreateNode();
        tree.SetFlexShrink(holder, 1f);
        tree.SetFlexDirection(holder, FlexDirection.Column);
        tree.SetDimension(holder, Dimension.Width, StyleLength.Points(60f));
        tree.SetDimension(holder, Dimension.Height, StyleLength.Points(20f));
        tree.AddChild(root, holder);

        var stretchy = tree.CreateNode();
        tree.SetDimension(stretchy, Dimension.Width, StyleLength.Percent(100f));
        tree.SetDimension(stretchy, Dimension.Height, StyleLength.Points(10f));
        tree.AddChild(holder, stretchy);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(50f, tree.GetWidth(plain), Tolerance);
        Assert.Equal(50f, tree.GetWidth(holder), Tolerance);
    }

    [Fact]
    public void A_clipping_descendant_contributes_nothing_but_its_own_edges() {
        // ⚠ A box that clips or scrolls an axis contributes nothing along it but its padding and
        // border. Its contents are scrollable overflow, which CSS Sizing §5.2.2 excludes from an
        // intrinsic size — being allowed to be smaller than what is inside it is the entire point
        // of a scroll container. §4.5 already says this one level out, where an item with
        // `overflow` other than visible opts out of its own automatic minimum; this is the same
        // sentence applied where the recursion reads it.
        //
        // ⚠ <b>Neither corpus asks for this and the editor found it.</b> Both stayed at exactly
        // 2 818 and 534 green with the rule present and absent. Every box in the docking chain
        // declares `overflow: hidden`, so the moment descendants began contributing their real
        // sizes the hierarchy tree's rows propagated to the shell and it came out 2 385 points wide
        // inside a 1 100-point window, inspector off the side. Four committed screenshots caught
        // what 2 742 browser-derived fixtures could not.
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Row);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(100f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(100f));

        var plain = tree.CreateNode();
        tree.SetFlexShrink(plain, 1f);
        tree.SetDimension(plain, Dimension.Width, StyleLength.Points(60f));
        tree.SetDimension(plain, Dimension.Height, StyleLength.Points(20f));
        tree.AddChild(root, plain);

        var clipping = tree.CreateNode();
        tree.SetFlexShrink(clipping, 1f);
        tree.SetFlexDirection(clipping, FlexDirection.Column);
        tree.SetDimension(clipping, Dimension.Width, StyleLength.Points(60f));
        tree.SetDimension(clipping, Dimension.Height, StyleLength.Points(20f));
        tree.AddChild(root, clipping);

        // A scrolling box between the item and the wide thing inside it. Without the rule the
        // 500-point row reaches the item's floor and freezes it at 60, and its sibling absorbs all
        // 20 points of overflow instead of half.
        var viewport = tree.CreateNode();
        tree.SetOverflow(viewport, Overflow.Scroll);
        tree.AddChild(clipping, viewport);

        var wide = tree.CreateNode();
        tree.SetDimension(wide, Dimension.Width, StyleLength.Points(500f));
        tree.SetDimension(wide, Dimension.Height, StyleLength.Points(10f));
        tree.AddChild(viewport, wide);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(50f, tree.GetWidth(plain), Tolerance);
        Assert.Equal(50f, tree.GetWidth(clipping), Tolerance);
    }

    [Fact]
    public void An_empty_descendant_still_contributes_its_own_padding_and_border() {
        // ⚠ <b>An empty box is not a zero-sized box, and the probe used to say it was.</b> Every
        // other branch of ComputeMinContentSizeUncached reports a BORDER box — the leaf-with-measure
        // branch adds its own padding and border, the clipping branch returns nothing but them — and
        // the childless branch returned a bare zero. So a `padding: 10px` box with nothing inside it
        // contributed 0 to its parent's min-content size instead of 20, and §4.5's floor for that
        // parent came out twenty points short.
        //
        // ⚠ <b>All 2 818 Taffy fixtures and all 534 Yoga ones stay green either way</b>, which is
        // why this is hand-written: the defect UNDER-reports, so it is only visible where the floor
        // it feeds is the thing being squeezed. A single item with a declared basis of 60 in a
        // five-point row shrinks to 5 without the floor and freezes at its child's 20 with it.
        //
        // The basis is DECLARED on purpose. A measured one sets LayoutResult.FlexBasisFromContent,
        // and the cap in ComputeAutoMinMainSize would then hold the floor down to the basis and hide
        // the change — the cap being the thing that has been hiding this defect all along.
        //
        // ⚠ ONE item and not two, and the reason is a second defect rather than economy: with two
        // shrinking siblings and a floor on one of them, §9.7's two passes hand BOTH items their
        // unshrunk flex bases back for every container width strictly between 10 and 40. The first
        // pass charges the whole pool to the frozen items, `line.RemainingFreeSpace` comes out zero
        // or positive, and the second pass then finds no space to shrink into and returns the basis.
        // That is not this rule's defect and is filed on its own.
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Row);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(5f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(50f));

        var container = tree.CreateNode();
        tree.SetFlexShrink(container, 1f);
        tree.SetFlexBasis(container, StyleLength.Points(60f));
        tree.AddChild(root, container);

        // No width, no children, nothing to measure — only ten points of padding on each side.
        var empty = tree.CreateNode();
        tree.SetPadding(empty, Edge.Left, StyleLength.Points(10f));
        tree.SetPadding(empty, Edge.Right, StyleLength.Points(10f));
        tree.AddChild(container, empty);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(20f, tree.GetWidth(container), Tolerance);
        Assert.Equal(20f, tree.GetWidth(empty), Tolerance);
    }

    [Fact]
    public void A_minimum_larger_than_the_maximum_wins() {
        // ⚠ CSS Sizing §5.1: "if the max size is less than the min size, the min size wins" — which
        // the specification expresses not as a special case but as the order of two clamps, max
        // first and min second. Vixen applied the max and returned, so the minimum was never
        // consulted. Taffy's absolute_minmax_bottom_right_min_max is the fixture, restated here.
        //
        // ⚠ The item has to be *absolutely positioned with no width* for this to bite, and a first
        // draft that gave it `width: 100px` passed against the broken clamp. With a width the value
        // arriving at the clamp is 100, which trips the max branch and returns before the min is
        // read — but every other path had already floored it. Sized against its container instead,
        // the value arriving is the whole 100 points of available width, the max clamps it to 40,
        // and only the second clamp can lift it back to 50. The offset is the witness: at 50 wide
        // with `right: 10` the box starts at x=40, and at 40 wide it would start at 50.
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(100f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(100f));

        var child = tree.CreateNode();
        tree.SetPositionType(child, PositionType.Absolute);
        tree.SetMinDimension(child, Dimension.Width, StyleLength.Points(50f));
        tree.SetMaxDimension(child, Dimension.Width, StyleLength.Points(40f));
        tree.SetMinDimension(child, Dimension.Height, StyleLength.Points(60f));
        tree.SetMaxDimension(child, Dimension.Height, StyleLength.Points(30f));
        tree.SetPosition(child, Edge.Right, StyleLength.Points(10f));
        tree.SetPosition(child, Edge.Bottom, StyleLength.Points(10f));
        tree.AddChild(root, child);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(50f, tree.GetWidth(child), Tolerance);
        Assert.Equal(60f, tree.GetHeight(child), Tolerance);
        Assert.Equal(40f, tree.GetLeft(child), Tolerance);
        Assert.Equal(30f, tree.GetTop(child), Tolerance);
    }

    [Fact]
    public void A_grid_item_is_floored_by_its_columns_and_not_by_the_sum_of_its_children() {
        // ⚠ <b>§4.5's probe read every container as a flex line, so a grid was measured as one.</b>
        // Four 20-point boxes over two auto columns occupy two columns of 20 — 40 across, whatever
        // the row count. The probe resolved the grid's main axis from `flex-direction`, which no
        // grid sets and which defaults to COLUMN, so the inline answer was the widest single child:
        // 20, for one column and for two alike. The columns are what a grid is wide by.
        //
        // ⚠ THE ORACLE IS THE GRID'S OWN SHAPE rather than a number chosen to match, and the
        // one-column row is the control: it reads 20 both before and after, so a probe that had
        // merely started SUMMING its children — 80, the mistake in the other direction, and the one
        // `gridflex_row_integration` reports — cannot pass this pair either.
        //
        // The basis is DECLARED for the reason the empty-padding witness gives: a measured one sets
        // LayoutResult.FlexBasisFromContent, and the cap in ComputeAutoMinMainSize — which exists
        // precisely to hold this over-reported floor down — would hide the change.
        Assert.Equal(40f, GridFlooredWidth(columns: 2));
        Assert.Equal(20f, GridFlooredWidth(columns: 1));
    }

    /// <summary>
    ///     The width a shrinking flex item settles at when it is a grid of four 20-point boxes over
    ///     <paramref name="columns" /> columns, squeezed into a row far narrower than any of them.
    /// </summary>
    static float GridFlooredWidth(int columns) {
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Row);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(5f));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(100f));

        var grid = tree.CreateNode();
        tree.SetDisplay(grid, Display.Grid);
        tree.SetFlexShrink(grid, 1f);
        tree.SetFlexBasis(grid, StyleLength.Points(200f));

        Span<GridTrackSize> template = stackalloc GridTrackSize[columns];
        template.Fill(GridTrackSize.Auto);
        tree.SetGridTemplateColumns(grid, template);
        tree.AddChild(root, grid);

        for (var i = 0; i < 4; i++) {
            var box = tree.CreateNode();
            tree.SetDimension(box, Dimension.Width, StyleLength.Points(20f));
            tree.SetDimension(box, Dimension.Height, StyleLength.Points(20f));
            tree.AddChild(grid, box);
        }

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        return tree.GetWidth(grid);
    }

    /// <summary>A leaf whose content is a fixed width however little room it is offered.</summary>
    /// <remarks>
    ///     Standing in for a single unbreakable word, which is the case §4.5 exists for. It answers
    ///     the same width under every measure mode, so what the tests observe is the floor rather
    ///     than the measurer being clever.
    /// </remarks>
    /// <summary>
    ///     A text leaf's block-axis floor is the height it takes at the width it will be given, and
    ///     the box between it and its container declares no width at all.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is <c>Rikarin/Vixen#623</c>, and the number it used to produce was not
    ///         merely wrong but unbounded.</b> §4.5's floor for the row is its min-content height,
    ///         which is a question about the text inside it — and the recursion handed the text the
    ///         row's <i>percentage basis</i>, which CSS Sizing §5.2.1 makes zero for a box with no
    ///         definite width. So the run was measured in a width of nothing, reported a line per
    ///         point of ink, and the item was floored sixty times too tall.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The declared <c>flex-basis</c> on the row is what makes this observable at all,
    ///         and it is the whole reason this test is shaped the way it is.</b>
    ///         <c>ComputeAutoMinMainSize</c> caps the floor at the item's own flex basis whenever
    ///         that basis was MEASURED — an inequality that hides every over-reported floor in this
    ///         method, and which is still load-bearing for #265's second defect. A declaration is
    ///         not a measurement, so this row's floor reaches the algorithm unclamped and the defect
    ///         becomes a number. The corpus fixtures that name the same bug — <c>blitz_issue_88</c>
    ///         and the two <c>bevy_issue_9530</c> families — are all measured leaves and are all
    ///         green either way, which is what kept this open.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(600f, 10f)]
    [InlineData(300f, 20f)]
    [InlineData(150f, 40f)]
    public void A_leaf_below_a_box_that_declares_no_width_is_measured_at_the_width_it_will_have(
        float width,
        float height
    ) {
        using var tree = new LayoutTree();

        // Five points tall, so the row cannot fit its content and its §4.5 floor is what decides
        // the answer rather than the space available.
        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Column);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(width));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(5f));

        var row = tree.CreateNode();
        tree.SetFlexDirection(row, FlexDirection.Row);
        tree.SetFlexGrow(row, 1f);
        tree.SetFlexShrink(row, 1f);
        tree.SetFlexBasis(row, StyleLength.Points(0f));
        tree.AddChild(root, row);

        var text = tree.CreateNode();
        tree.SetFlexGrow(text, 1f);
        tree.SetFlexShrink(text, 1f);
        tree.SetFlexBasis(text, StyleLength.Points(0f));
        tree.SetMeasureFunction(text, MeasureWrappedRun);
        tree.AddChild(row, text);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        // The run fills the width it is given and the height is the ink over that width, so halving
        // the container doubles the height exactly. Both numbers are arithmetic, not a recording.
        Assert.Equal(width, tree.GetWidth(text), Tolerance);
        Assert.Equal(height, tree.GetHeight(row), Tolerance);
        Assert.Equal(height, tree.GetHeight(text), Tolerance);
    }

    /// <summary>
    ///     CSS Sizing §5.1 over the probe: a box is measured at the width it will be USED at, and its
    ///     own <c>min-width</c> is part of that.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The oracle is that the answer does not depend on the container at all.</b> A
    ///         <c>min-width: 600px</c> leaf is laid out 600 wide in a 150-point box exactly as it is
    ///         in a 600-point one — the minimum wins over the room — so the run has one line's worth
    ///         of room in every column of the theory and the floor it produces is one line. A probe
    ///         that measures at the offer instead reports four lines at 150, two at 300 and one at
    ///         600: the number it gives back is a function of a width the box will never be drawn at.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Hand-written because the corpus cannot see this rule at all.</b>
    ///         <c>measure_child_with_min_size_greater_than_available_space</c> is the same arithmetic
    ///         and it is GREEN either way, because §4.5's floor is capped at the item's own measured
    ///         flex basis and the cap hides the over-report — see <c>ComputeAutoMinMainSize</c>. A
    ///         declared <c>flex-basis</c> on the row is what keeps the cap out of this fixture, so
    ///         the probe has to answer for itself.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(150f)]
    [InlineData(300f)]
    [InlineData(600f)]
    public void A_leaf_is_probed_at_the_width_its_own_minimum_guarantees_it(float containerWidth) {
        using var tree = new LayoutTree();

        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Column);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(containerWidth));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(5f));

        // A DECLARED basis, so `FlexBasisFromContent` is false and §4.5's floor for this box is not
        // capped by a measurement — the probe's answer is the answer.
        var row = tree.CreateNode();
        tree.SetFlexDirection(row, FlexDirection.Row);
        tree.SetFlexGrow(row, 1f);
        tree.SetFlexShrink(row, 1f);
        tree.SetFlexBasis(row, StyleLength.Points(0f));
        tree.AddChild(root, row);

        var text = tree.CreateNode();
        tree.SetFlexGrow(text, 1f);
        tree.SetFlexShrink(text, 1f);
        tree.SetFlexBasis(text, StyleLength.Points(0f));
        tree.SetMinDimension(text, Dimension.Width, StyleLength.Points(600f));
        tree.SetMeasureFunction(text, MeasureWrappedRun);
        tree.AddChild(row, text);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        // 600 points of ink in 600 points of room is one line, whatever the box around it says.
        Assert.Equal(600f, tree.GetWidth(text), Tolerance);
        Assert.Equal(10f, tree.GetHeight(row), Tolerance);
        Assert.Equal(10f, tree.GetHeight(text), Tolerance);
    }

    /// <summary>
    ///     A <c>width: 100%</c> box is probed at the width its percentage gives it, not at the room
    ///     left over once its own margins are taken off.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The two are different numbers precisely when the box overflows, which is what a
    ///         percentage width plus a margin always does.</b> <c>ProbeInlineSize</c> subtracts the
    ///         box's margins from the offer, and for a box whose width comes FROM the remaining
    ///         space that is right. A <c>width: 100%</c> box does not take its width from the
    ///         remaining space: it is as wide as its containing block and the margins push its
    ///         margin box out past the edge. Measuring the text inside it at the offer less the
    ///         margins therefore measures it narrower than it will ever be drawn, it wraps to more
    ///         lines than it takes, and §4.5's floor comes out a whole line too tall.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The oracle is an inconsistency rather than a recorded number: the row is floored
    ///         at the height of the very content it is drawn around, so the floor must EQUAL the
    ///         height of the text inside it.</b> The text is laid out at the full container width in
    ///         every column of the theory — asserted, so the premise cannot rot — and the run is
    ///         area-preserving, so its height is the ink over that width and nothing else. A probe
    ///         that measures 50 points narrower reports one more line at every width, and the row
    ///         comes back taller than what it contains.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Hand-written because the corpus cannot witness it.</b> <c>bevy_issue_9530</c> is
    ///         the same arithmetic and is GREEN either way: §4.5's floor is capped at the item's own
    ///         measured basis, and the cap hides the over-report — see
    ///         <c>ComputeAutoMinMainSize</c>. A declared <c>flex-basis</c> on the row is what keeps
    ///         the cap out of this one, so the probe has to answer for itself.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(200f, 30f)]
    [InlineData(300f, 20f)]
    [InlineData(600f, 10f)]
    public void A_percentage_width_is_probed_at_its_percentage_and_not_at_what_its_margins_leave(
        float containerWidth,
        float expectedHeight
    ) {
        using var tree = new LayoutTree();

        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Column);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(containerWidth));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(5f));

        // A DECLARED basis, so `FlexBasisFromContent` is false and §4.5's floor for this box is not
        // capped by a measurement — the probe's answer is the answer. The margins are the whole
        // point: they are 50 points the percentage does not give back.
        var row = tree.CreateNode();
        tree.SetFlexDirection(row, FlexDirection.Column);
        tree.SetDimension(row, Dimension.Width, StyleLength.Percent(100f));
        tree.SetMargin(row, Edge.Left, StyleLength.Points(25f));
        tree.SetMargin(row, Edge.Right, StyleLength.Points(25f));
        tree.SetFlexGrow(row, 1f);
        tree.SetFlexShrink(row, 1f);
        tree.SetFlexBasis(row, StyleLength.Points(0f));
        tree.AddChild(root, row);

        // No grow, so the text keeps the height its own content needs and the row's floor is free to
        // disagree with it.
        var text = tree.CreateNode();
        tree.SetMeasureFunction(text, MeasureWrappedRun);
        tree.AddChild(row, text);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        // The premise: the percentage wins over the room, margins and all.
        Assert.Equal(containerWidth, tree.GetWidth(row), Tolerance);
        Assert.Equal(containerWidth, tree.GetWidth(text), Tolerance);

        // The arithmetic: 600 points of ink over the width it is drawn at.
        Assert.Equal(expectedHeight, tree.GetHeight(text), Tolerance);

        // The property: a box floored by its own contents is exactly as tall as they are.
        Assert.Equal(tree.GetHeight(text), tree.GetHeight(row), Tolerance);
    }

    static LayoutSize MeasureFixedContent(in MeasureRequest request) =>
        new((float) (request.Context ?? 0f), 20f);

    /// <summary>Six hundred points of run that breaks anywhere, ten points to the line.</summary>
    /// <remarks>
    ///     ⚠ <b>Area-preserving on purpose, so every expectation below is arithmetic rather than a
    ///     recorded number.</b> The run has a fixed amount of ink and no unbreakable piece, so the
    ///     height it takes is the ink over the room it is given — halve the width and the height
    ///     doubles, exactly. A measurer that answered a remembered pair of numbers could be
    ///     satisfied by a probe measuring at any width at all.
    /// </remarks>
    static LayoutSize MeasureWrappedRun(in MeasureRequest request) {
        const float ink = 600f;
        const float line = 10f;

        var available = request.WidthMode == MeasureMode.Undefined || float.IsNaN(request.AvailableWidth)
            ? ink
            : MathF.Max(0f, request.AvailableWidth);

        // A room of nothing is a line per point rather than a division by zero — which is what the
        // probe used to ask for, and why the floor it computed was sixty times too tall.
        var lines = available <= 0f ? ink : MathF.Ceiling(ink / available);

        return new LayoutSize(MathF.Min(available, ink), lines * line);
    }
}
