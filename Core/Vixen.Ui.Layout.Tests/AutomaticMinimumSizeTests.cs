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

    /// <summary>
    ///     A scroll container's contents are excluded from its own automatic minimum and from
    ///     nothing else — its intrinsic size is still the size of what is inside it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This test used to assert 50 and 50, and the number was never measured.</b> It was
    ///         written from the editor's docking chain, on the reading that CSS Sizing §5.2.2's
    ///         exclusion of scrollable overflow applies to a box's min-content CONTRIBUTION as well
    ///         as to §4.5's automatic minimum. Measured in Chrome, this markup gives 40 and 60: the
    ///         500-point row inside the scroll container does reach the item's §4.5 floor, the
    ///         floor is capped at the item's own <c>width: 60px</c> specified size suggestion, and
    ///         the sibling absorbs all twenty points of overflow rather than half. The same numbers
    ///         come back with <c>overflow: hidden</c> instead of <c>scroll</c>, and with no scroll
    ///         container at all — which is the point: the container changes nothing here.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The direct question, also measured: a <c>width: min-content</c> box wrapped
    ///         around a scroll container holding a 500-point box is 500 wide in Chrome</b> (515 with
    ///         a classic scrollbar), not zero. A scroll container being allowed to be smaller than
    ///         its contents is §4.5's sentence about its OWN automatic minimum, which
    ///         <c>ComputeAutoMinMainSize</c> and <c>LayoutTree.Grid</c>'s <c>AutomaticMinimumIsZero</c>
    ///         each say for themselves. Saying it a second time in the probe cost 24 grid fixtures.
    ///         <c>Rikarin/Vixen#259</c>.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_clipping_descendant_still_contributes_what_is_inside_it() {
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

        var viewport = tree.CreateNode();
        tree.SetOverflow(viewport, Overflow.Scroll);
        tree.AddChild(clipping, viewport);

        var wide = tree.CreateNode();
        tree.SetDimension(wide, Dimension.Width, StyleLength.Points(500f));
        tree.SetDimension(wide, Dimension.Height, StyleLength.Points(10f));
        tree.AddChild(viewport, wide);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(40f, tree.GetWidth(plain), Tolerance);
        Assert.Equal(60f, tree.GetWidth(clipping), Tolerance);
    }

    /// <summary>
    ///     The editor's docking chain, which is what the deleted exclusion was written for: every box
    ///     clips, so every one of them opts out of §4.5 on its own account.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The symptom this stands in for is a picture and it never had a test.</b> Once
    ///         descendants began contributing their real sizes the hierarchy tree's rows propagated
    ///         to the shell, which came out 2 385 points wide inside a 1 100-point window with the
    ///         inspector pushed off the side; four committed screenshots caught what 2 742
    ///         browser-derived fixtures could not, and the answer was a clause in the min-content
    ///         probe that turned out to be wrong about a browser. This is the property that actually
    ///         protects the chain, and it holds without that clause: a box whose own overflow is not
    ///         visible has NO automatic minimum — <c>ComputeAutoMinMainSize</c> returns zero before
    ///         it ever asks what is inside — so a chain of clipping boxes shrinks to its window
    ///         however wide its contents are.
    ///     </para>
    ///     <para>
    ///         The oracle is the window: the shell is exactly as wide as the room it was given, at
    ///         every depth, and the 2 385-point leaf is still 2 385 points wide inside it. A test
    ///         that only asserted the shell could be satisfied by a store that had lost the content.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    public void A_chain_of_clipping_boxes_shrinks_to_its_window(int depth) {
        using var tree = new LayoutTree();

        var window = tree.CreateNode();
        tree.SetFlexDirection(window, FlexDirection.Row);
        tree.SetDimension(window, Dimension.Width, StyleLength.Points(1100f));
        tree.SetDimension(window, Dimension.Height, StyleLength.Points(700f));

        var parent = window;
        var shell = window;

        for (var i = 0; i < depth; i++) {
            var box = tree.CreateNode();
            tree.SetFlexDirection(box, FlexDirection.Row);
            tree.SetFlexShrink(box, 1f);
            tree.SetOverflow(box, Overflow.Hidden);
            tree.AddChild(parent, box);
            parent = box;

            if (i == 0) {
                shell = box;
            }
        }

        var content = tree.CreateNode();
        tree.SetDimension(content, Dimension.Width, StyleLength.Points(2385f));
        tree.SetDimension(content, Dimension.Height, StyleLength.Points(20f));
        tree.AddChild(parent, content);

        tree.CalculateLayout(window, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(1100f, tree.GetWidth(shell), Tolerance);
        Assert.Equal(2385f, tree.GetWidth(content), Tolerance);
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

    /// <summary>
    ///     The one rule in <c>ComputeAutoMinMainSize</c> that is a decision rather than a
    ///     specification sentence: §4.5's floor is held down to what the item was measured at when
    ///     its content cannot shrink.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Chrome does not do this, and the divergence is deliberate.</b> One unbreakable
    ///         word under <c>overflow-wrap: break-word</c> has an intrinsic minimum that CSS Sizing
    ///         §5.2 specifies NOT to shrink, so a browser's <c>flex-direction: row</c> item keeps the
    ///         whole word and overflows its container — measured, and the table is in
    ///         <c>Taffy/KnownGaps.txt</c>. The wrapping picture a browser draws for the same markup
    ///         comes from the CROSS axis, where there is no §4.5 floor at all. This engine's initial
    ///         <c>Display</c> is <c>Flex</c> and its initial <c>FlexDirection</c> is <c>Row</c> where
    ///         a browser's initial display is <c>block</c>, so every plain element here is the row
    ///         case and would get the overflowing picture for markup whose author wrote no flex
    ///         container at all. The cap is what keeps the block-ish answer.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Nothing in this project asserted that until now, which is why it is here.</b>
    ///         Deleting the cap leaves the whole layout suite green — all eight corpora included —
    ///         and costs three <c>TextWrappingPixelTests</c> in <i>Vixen.Ui.Controls.Tests</i>, a
    ///         different assembly two layers out. A rule whose only witness is a pixel test in
    ///         another project is one an agent deletes in good faith while every layout fixture
    ///         agrees with them. So this test is not evidence that the cap is right; it is the
    ///         record that removing it is a framework call about the initial display, to be made
    ///         deliberately and by whoever owns that divergence. <c>Rikarin/Vixen#265</c>.
    ///     </para>
    ///     <para>
    ///         The oracle is a contradiction rather than a recorded number. The measurer is an
    ///         unbreakable word: it answers the whole word's width at every available width, EXCEPT
    ///         that a definite offer breaks it across lines at line layout — which is what a browser
    ///         does with an overflowing <c>break-word</c> run. So the item's min-content size and its
    ///         max-content size are the same number, the floor is the whole word, and the only thing
    ///         that can put the item inside its container is the cap. The height moves with it: an
    ///         item held to a third of the word is three lines tall, and one that keeps the word is
    ///         one line tall. Both halves are arithmetic in the container's width.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(240f, 10f)]
    [InlineData(120f, 20f)]
    [InlineData(80f, 30f)]
    public void An_item_whose_content_refuses_to_shrink_is_still_floored_at_what_it_was_measured_at(
        float containerWidth,
        float expectedHeight
    ) {
        using var tree = new LayoutTree();

        var root = tree.CreateNode();
        tree.SetFlexDirection(root, FlexDirection.Row);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(containerWidth));
        tree.SetDimension(root, Dimension.Height, StyleLength.Points(100f));

        // Not stretched, so the item keeps the height its own content takes and the second half of
        // the oracle is about the word rather than about the container.
        tree.SetAlignItems(root, Align.FlexStart);

        // No declared basis: the basis is MEASURED, which is the only case the cap applies to.
        var word = tree.CreateNode();
        tree.SetFlexShrink(word, 1f);
        tree.SetMeasureFunction(word, MeasureUnbreakableWord);
        tree.AddChild(root, word);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        // The decision: the item is inside its container at every width, where §4.5 read literally
        // would leave it 240 wide in all three columns.
        Assert.Equal(containerWidth, tree.GetWidth(word), Tolerance);
        Assert.Equal(expectedHeight, tree.GetHeight(word), Tolerance);
    }

    static LayoutSize MeasureFixedContent(in MeasureRequest request) =>
        new((float) (request.Context ?? 0f), 20f);

    /// <summary>
    ///     Two hundred and forty points of one unbreakable word, ten points to the line.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The intrinsic sizes are equal on purpose: this run's min-content size IS its
    ///     max-content size.</b> That is <c>overflow-wrap: break-word</c>'s own rule — the breaking
    ///     keyword changes where line layout may break a run that already overflows, and CSS Sizing
    ///     §5.2 says it does not change the intrinsic minimum. A definite offer is the one thing that
    ///     wraps it, so the height is the ink over the width the item is finally given.
    /// </remarks>
    static LayoutSize MeasureUnbreakableWord(in MeasureRequest request) {
        const float word = 240f;
        const float line = 10f;

        if (request.WidthMode == MeasureMode.Undefined || float.IsNaN(request.AvailableWidth) || request.AvailableWidth <= 0f) {
            return new LayoutSize(word, line);
        }

        var available = MathF.Min(request.AvailableWidth, word);

        return new LayoutSize(available, MathF.Ceiling(word / available) * line);
    }

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
