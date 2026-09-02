// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Layout.Tests;

/// <summary>
///     CSS Grid §7.3 — <c>grid-template-areas</c>, and §8.3's placement of an item by an area's name.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Neither conformance corpus can see one line of this, and that is why the feature was
///         left out for as long as it was.</b> Taffy's XML harness leaves
///         <c>grid_template_areas</c> at <c>Default::default()</c>, so not one of the 2 120 grid
///         fixtures sets it and every one of them stays green against a store that ignores the
///         property. `Core/Vixen.Ui.Layout/README.md` recorded that, and recorded the condition for
///         implementing it later: <b>write the oracle first</b>.
///     </para>
///     <para>
///         <b>So the oracle is <c>web-platform-tests</c>, and the parsing half of it is quoted
///         rather than re-expressed.</b>
///         <c>css/css-grid/grid-definition/grid-support-grid-template-areas-001.html</c> drives
///         thirty accepted values and sixteen refused ones through
///         <c>getComputedStyle</c> and asserts the <i>serialisation</i> of each — which is an
///         assertion about the tokenisation and not about formatting, since a parser that read
///         <c>"..a"</c> as three cells round-trips its own mistake and only the canonical form says
///         so. Every case below is that file's, value for value, in its own order.
///     </para>
///     <para>
///         <b>The placement half is re-expressed</b>, the way <see cref="OrderTests" /> re-expresses
///         WPT's <c>order</c> tests: the files are reftests — "passes if there is a filled green
///         square and no red" — so what carries across is the relation each asserts with the geometry
///         restated in fixed sizes, and each test names the file it came from.
///     </para>
///     <para>
///         ⚠ <b>What is <i>not</i> here, because it is not implemented: named lines written into a
///         track list</b> (<c>[col] 50px [col] 50px</c>). Three of WPT's named-area placement files
///         mix the two, and this file takes the area half of them and says so where it does.
///     </para>
/// </remarks>
public class GridTemplateAreasTests {
    const float Tolerance = 0.0001f;

    /// <summary>
    ///     The thirty values <c>grid-support-grid-template-areas-001.html</c> accepts, each with the
    ///     serialisation it asserts.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Six of these differ from what they were written as, and those six are the test.</b>
    ///     A run of full stops is <i>one</i> null cell — <c>".a..."</c> is three columns, not five —
    ///     which follows from CSS Syntax tokenising a <c>&lt;null-cell-token&gt;</c> as a run rather
    ///     than from anything in §7.3's prose. Read per character the grid is wider, and it lays out
    ///     perfectly well at the wrong width.
    ///     <br />
    ///     ⚠ <b>And the fourth case is the one that pins what a name may contain</b>: <c>10</c>,
    ///     <c>-minus</c>, <c>1-st</c>, <c>©copy_right</c> and <c>line¶</c> are all accepted, so the
    ///     test is CSS Syntax §4.2's name code point and not <c>&lt;custom-ident&gt;</c>, which would
    ///     refuse three of them for starting with a digit.
    /// </remarks>
    /// <param name="written">The declaration, as the WPT file writes it.</param>
    /// <param name="serialized">The computed value it asserts.</param>
    [Theory]
    [InlineData("\"a\"", "\"a\"")]
    [InlineData("\".\"", "\".\"")]
    [InlineData("\"lower UPPER 10 -minus _low 1-st ©copy_right line¶\"", "\"lower UPPER 10 -minus _low 1-st ©copy_right line¶\"")]
    [InlineData("\"a b\"", "\"a b\"")]
    [InlineData("\"a b\" \"c d\"", "\"a b\" \"c d\"")]
    [InlineData("\"a   b\"   \"c   d\"", "\"a b\" \"c d\"")]
    [InlineData("\"a b\"\"c d\"", "\"a b\" \"c d\"")]
    [InlineData("\"a b\"\t\"c d\"", "\"a b\" \"c d\"")]
    [InlineData("\"a b\"\n\"c d\"", "\"a b\" \"c d\"")]
    [InlineData("\"a b\" \"a b\"", "\"a b\" \"a b\"")]
    [InlineData("\"a a\" \"b b\"", "\"a a\" \"b b\"")]
    [InlineData("\". a .\" \"b a c\"", "\". a .\" \"b a c\"")]
    [InlineData("\".. a ...\" \"b a c\"", "\". a .\" \"b a c\"")]
    [InlineData("\".a...\" \"b a c\"", "\". a .\" \"b a c\"")]
    [InlineData("\"head head\" \"nav main\" \"foot .\"", "\"head head\" \"nav main\" \"foot .\"")]
    [InlineData("\"head head\" \"nav main\" \"foot ....\"", "\"head head\" \"nav main\" \"foot .\"")]
    [InlineData("\"head head\" \"nav main\" \"foot.\"", "\"head head\" \"nav main\" \"foot .\"")]
    [InlineData("\". header header .\" \"nav main main main\" \"nav footer footer .\"", "\". header header .\" \"nav main main main\" \"nav footer footer .\"")]
    [InlineData("\"... header header ....\" \"nav main main main\" \"nav footer footer ....\"", "\". header header .\" \"nav main main main\" \"nav footer footer .\"")]
    [InlineData("\"...header header....\" \"nav main main main\" \"nav footer footer....\"", "\". header header .\" \"nav main main main\" \"nav footer footer .\"")]
    [InlineData("\"title stats\" \"score stats\" \"board board\" \"ctrls ctrls\"", "\"title stats\" \"score stats\" \"board board\" \"ctrls ctrls\"")]
    [InlineData("\"title board\" \"stats board\" \"score ctrls\"", "\"title board\" \"stats board\" \"score ctrls\"")]
    [InlineData("\". a\" \"b a\" \". a\"", "\". a\" \"b a\" \". a\"")]
    [InlineData("\".. a\" \"b a\" \"... a\"", "\". a\" \"b a\" \". a\"")]
    [InlineData("\"..a\" \"b a\" \".a\"", "\". a\" \"b a\" \". a\"")]
    [InlineData("\"a a a\" \"b b b\"", "\"a a a\" \"b b b\"")]
    [InlineData("\". .\" \"a a\"", "\". .\" \"a a\"")]
    [InlineData("\"... ....\" \"a a\"", "\". .\" \"a a\"")]
    public void An_accepted_value_serialises_the_way_the_conformance_suite_says(string written, string serialized) {
        Assert.True(GridAreaTemplate.TryParse(written, out var template, out var refusal), refusal);
        Assert.NotNull(template);
        Assert.Equal(serialized, template.ToString());
    }

    /// <summary><c>none</c> is the property's initial value written out, not a refusal.</summary>
    [Fact]
    public void None_is_understood_and_names_no_areas() {
        Assert.True(GridAreaTemplate.TryParse("none", out var template, out var refusal), refusal);
        Assert.Null(template);
    }

    /// <summary>
    ///     The sixteen values the same file requires to compute to <c>none</c>, which for a store
    ///     that drops an invalid declaration whole means a refusal.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Eight of the sixteen lay out perfectly well if they are accepted, which is what makes
    ///     them worth having.</b> <c>"a b a"</c>, <c>"a" "b" "a"</c>, <c>"a b" "b b"</c>,
    ///     <c>"b a" "b b"</c>, <c>"a b" "b a"</c> and <c>"a ." ". a"</c> are all areas that are not a
    ///     single filled rectangle — an implementation that took each name's bounding box and asked
    ///     no further question places an item over cells another area owns and nothing complains.
    ///     The three row-count mismatches are the other half: §7.3 invalidates the whole declaration
    ///     rather than the row, so a parser that padded the short row to the widest would build a
    ///     grid the author never wrote.
    /// </remarks>
    /// <param name="written">The declaration.</param>
    [Theory]
    [InlineData("a")]
    [InlineData("\"a\" \"b c\"")]
    [InlineData("\"a b\" \"c\" \"d e\"")]
    [InlineData("\"a b c\" \"d e\"")]
    [InlineData("\"a b\"-\"c d\"")]
    [InlineData("\"a b\" - \"c d\"")]
    [InlineData("\"a b\" . \"c d\"")]
    [InlineData("\"a b a\"")]
    [InlineData("\"a\" \"b\" \"a\"")]
    [InlineData("\"a b\" \"b b\"")]
    [InlineData("\"b a\" \"b b\"")]
    [InlineData("\"a b\" \"b a\"")]
    [InlineData("\"a .\" \". a\"")]
    [InlineData("\",\"")]
    [InlineData("\"10%\"")]
    [InlineData("\"USD$\"")]
    public void A_refused_value_is_refused_with_a_reason(string written) {
        Assert.False(GridAreaTemplate.TryParse(written, out var template, out var refusal));
        Assert.Null(template);
        Assert.False(string.IsNullOrWhiteSpace(refusal));
    }

    /// <summary>An area's four lines are the edges of its rectangle, and a null cell has none.</summary>
    [Fact]
    public void An_area_resolves_to_the_four_lines_it_spans() {
        Assert.True(GridAreaTemplate.TryParse("\"head head\" \"nav main\" \"nav .\"", out var template, out _));
        Assert.NotNull(template);

        Assert.Equal(3, template.Rows);
        Assert.Equal(2, template.Columns);

        Assert.True(template.TryGetArea("head", out var rowStart, out var rowEnd, out var columnStart, out var columnEnd));
        Assert.Equal((0, 1, 0, 2), (rowStart, rowEnd, columnStart, columnEnd));

        Assert.True(template.TryGetArea("nav", out rowStart, out rowEnd, out columnStart, out columnEnd));
        Assert.Equal((1, 3, 0, 1), (rowStart, rowEnd, columnStart, columnEnd));

        Assert.False(template.TryGetArea("sidebar", out _, out _, out _, out _));
        Assert.Null(template.NameAt(2, 1));
        Assert.Equal("main", template.NameAt(1, 1));
    }

    /// <summary>
    ///     What may be <i>written</i> as an area's name and what may <i>refer</i> to one are two
    ///     different grammars, and the second is narrower.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Found by a test that should have failed and did not.</b> A pure name-code-point test
    ///     — the one the template's own tokeniser applies — accepts <c>4px</c>, so
    ///     <c>grid-column: 4px</c> stopped being a refusal and became an item placed in an area
    ///     called <c>4px</c>, which does not exist, which is auto-placement in silence: exactly the
    ///     failure the placement bridge was written to make impossible. A placement value is a
    ///     <c>&lt;custom-ident&gt;</c> token and cannot open with a digit; a cell inside a string is a
    ///     <c>&lt;name&gt;</c> production and can. So an area may legally carry a name that nothing
    ///     is able to refer to.
    /// </remarks>
    /// <param name="value">The value.</param>
    /// <param name="expected">Whether it may refer to an area.</param>
    [Theory]
    [InlineData("header", true)]
    [InlineData("A1", true)]
    [InlineData("-minus", true)]
    [InlineData("_low", true)]
    [InlineData("©copy_right", true)]
    [InlineData("4px", false)]
    [InlineData("10", false)]
    [InlineData("-1", false)]
    [InlineData("10%", false)]
    [InlineData("USD$", false)]
    [InlineData("two words", false)]
    [InlineData("", false)]
    public void A_reference_to_an_area_is_a_custom_ident_and_a_cell_in_a_string_is_not(string value, bool expected) =>
        Assert.Equal(expected, GridAreaTemplate.IsAreaName(value));

    // ── Placement, re-expressed from the reftests ───────────────────────────────────────────────

    /// <summary>
    ///     <c>css/css-grid/placement/grid-placement-using-named-grid-lines-001.html</c> — four items
    ///     placed by <c>grid-area</c> in a two-row template fill the grid exactly, and the one whose
    ///     area covers both rows is twice as tall as the others.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         WPT's grid is <c>25px</c> × 4 columns, <c>grid-auto-rows: 50px</c> and
    ///         <c>"A1 A2 A3 A4" ". A2 A3 A4"</c>, and it passes when the whole 100 × 100 square is
    ///         green — which pins every one of the five boxes, because a hole anywhere shows red.
    ///         The fifth item is <c>grid-column: C</c> against a named <i>line</i> and is left out:
    ///         named lines in a track list are not implemented, and the note at the top of this file
    ///         says so.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The rows come from the areas and not from any track list.</b> This grid states no
    ///         <c>grid-template-rows</c> at all, so if the template did not make the explicit grid two
    ///         rows tall, <c>A2</c> would span one row and half the square would be red.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Four_areas_fill_the_grid_and_a_two_row_area_is_twice_as_tall() {
        using var tree = new LayoutTree();

        var root = Grid(tree, "\"A1 A2 A3 A4\" \". A2 A3 A4\"", columns: 4, columnWidth: 25f);
        tree.SetGridAutoRows(root, [GridTrackSize.Single(GridSizingFunction.Points(50f))]);

        var first = Placed(tree, root, "A1");
        var second = Placed(tree, root, "A2");
        var third = Placed(tree, root, "A3");
        var fourth = Placed(tree, root, "A4");

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(100f, tree.GetWidth(root), Tolerance);
        Assert.Equal(100f, tree.GetHeight(root), Tolerance);

        Assert.Equal((0f, 0f, 25f, 50f), Box(tree, first));
        Assert.Equal((25f, 0f, 25f, 100f), Box(tree, second));
        Assert.Equal((50f, 0f, 25f, 100f), Box(tree, third));
        Assert.Equal((75f, 0f, 25f, 100f), Box(tree, fourth));
    }

    /// <summary>
    ///     §7.1 — the explicit grid is the larger of the track list and the areas, and the tracks the
    ///     areas add are sized by <c>grid-auto-rows</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Both halves are asserted, and the second is the one that a "close enough"
    ///     implementation gets wrong.</b> A three-row template over a one-track
    ///     <c>grid-template-rows</c> makes three <i>explicit</i> rows, so <c>grid-row-start: -1</c>
    ///     is the line after the third and the item it places starts a fourth, implicit row at 70 —
    ///     the three above it being 10 from the track list and 30 twice from <c>grid-auto-rows</c>.
    ///     Treat the two extra rows as implicit instead and <c>-1</c> counts back from the end of a
    ///     one-row explicit grid, so the same item lands at 10 and sits on top of <c>b</c>.
    /// </remarks>
    [Fact]
    public void Areas_make_the_explicit_grid_larger_and_the_extra_tracks_come_from_grid_auto_rows() {
        using var tree = new LayoutTree();

        var root = Grid(tree, "\"a\" \"b\" \"c\"", columns: 1, columnWidth: 40f);
        tree.SetGridTemplateRows(root, [GridTrackSize.Single(GridSizingFunction.Points(10f))]);
        tree.SetGridAutoRows(root, [GridTrackSize.Single(GridSizingFunction.Points(30f))]);

        var third = Placed(tree, root, "c");

        // The line after the last explicit row, written the way a stylesheet would.
        var trailing = tree.CreateNode();
        tree.SetGridPlacement(trailing, Edge.Top, GridPlacement.Line(-1));
        tree.AddChild(root, trailing);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(40f, tree.GetTop(third), Tolerance);
        Assert.Equal(30f, tree.GetHeight(third), Tolerance);

        // The three explicit rows, then the implicit one the trailing item opened.
        Assert.Equal(70f, tree.GetTop(trailing), Tolerance);
        Assert.Equal(100f, tree.GetHeight(root), Tolerance);
    }

    /// <summary>
    ///     §8.3 — a name that matches no area is auto-placed, which is this store's answer and not
    ///     the specification's.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Pinned as a divergence rather than left to drift.</b> §8.3 says the implicit grid
    ///     lines are all assumed to carry a name nothing matches, which would put the item on a line
    ///     the author never wrote; auto-placement is the answer here, and it is the answer that makes
    ///     a typo look like a typo. The assertion is that the item lands in the first free cell —
    ///     which is a different place from every cell the template names, so an implementation that
    ///     quietly resolved the name to line 1 would fail it.
    /// </remarks>
    [Fact]
    public void A_name_no_area_carries_is_auto_placed() {
        using var tree = new LayoutTree();

        var root = Grid(tree, "\"a b\"", columns: 2, columnWidth: 30f);
        tree.SetGridAutoRows(root, [GridTrackSize.Single(GridSizingFunction.Points(20f))]);

        var known = Placed(tree, root, "b");
        var unknown = Placed(tree, root, "sidebar");

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        Assert.Equal(30f, tree.GetLeft(known), Tolerance);
        Assert.Equal(0f, tree.GetTop(known), Tolerance);

        // The first free cell, which is column 1 of row 1 — not column 1 of a line the name invented.
        Assert.Equal(0f, tree.GetLeft(unknown), Tolerance);
        Assert.Equal(0f, tree.GetTop(unknown), Tolerance);
    }

    /// <summary>An out-of-flow child's grid area is its containing block, by name as well.</summary>
    /// <remarks>
    ///     ⚠ <b>§9 reads the same four properties from a different file, and this is the assertion
    ///     that says so.</b> An absolutely positioned child resolves its insets against its grid
    ///     area, and a named placement resolved only on §8's in-flow walk would silently give this
    ///     child the padding box instead — a difference of exactly one area, in a direction that
    ///     looks like a stacking bug rather than a placement one.
    /// </remarks>
    [Fact]
    public void An_out_of_flow_child_takes_its_named_area_as_its_containing_block() {
        using var tree = new LayoutTree();

        var root = Grid(tree, "\"a b\" \"a c\"", columns: 2, columnWidth: 30f);
        tree.SetGridTemplateRows(
            root,
            [GridTrackSize.Single(GridSizingFunction.Points(20f)), GridTrackSize.Single(GridSizingFunction.Points(20f))]
        );

        var child = tree.CreateNode();
        tree.SetPositionType(child, PositionType.Absolute);
        tree.SetGridPlacement(child, Edge.Top, "c");
        tree.SetGridPlacement(child, Edge.Bottom, "c");
        tree.SetGridPlacement(child, Edge.Left, "c");
        tree.SetGridPlacement(child, Edge.Right, "c");
        tree.SetPosition(child, Edge.Left, StyleLength.Points(0f));
        tree.SetPosition(child, Edge.Top, StyleLength.Points(0f));
        tree.SetDimension(child, Dimension.Width, StyleLength.Percent(50f));
        tree.SetDimension(child, Dimension.Height, StyleLength.Points(5f));
        tree.AddChild(root, child);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);

        // `c` is the second column of the second row: x = 30, y = 20, and 50 % of its 30-point area.
        Assert.Equal(30f, tree.GetLeft(child), Tolerance);
        Assert.Equal(20f, tree.GetTop(child), Tolerance);
        Assert.Equal(15f, tree.GetWidth(child), Tolerance);
    }

    /// <summary>A named placement and a numeric one on the same edge are one declaration.</summary>
    /// <remarks>
    ///     ⚠ <b>The store has two places to put an edge and CSS has one declaration for it</b>, so
    ///     writing either has to take the other away. Without that, an element restyled from
    ///     <c>grid-area: b</c> to <c>grid-column-start: 1</c> keeps the name — and the name wins, so
    ///     the new declaration does nothing at all and the element stays where it was.
    /// </remarks>
    [Fact]
    public void Writing_one_kind_of_placement_takes_the_other_away() {
        using var tree = new LayoutTree();

        var root = Grid(tree, "\"a b\"", columns: 2, columnWidth: 30f);
        tree.SetGridAutoRows(root, [GridTrackSize.Single(GridSizingFunction.Points(20f))]);

        var child = tree.CreateNode();
        tree.SetGridPlacement(child, Edge.Left, "b");
        tree.AddChild(root, child);

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);
        Assert.Equal(30f, tree.GetLeft(child), Tolerance);

        tree.SetGridPlacement(child, Edge.Left, GridPlacement.Line(1));
        Assert.Null(tree.GetGridPlacementName(child, Edge.Left));

        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);
        Assert.Equal(0f, tree.GetLeft(child), Tolerance);
    }

    /// <summary>Setting the same template twice does not dirty the node.</summary>
    /// <remarks>
    ///     A stylesheet rewrites every property of every restyled element, so a template compared by
    ///     reference rather than by value would mark a whole panel dirty on any change to any of its
    ///     rules. The store's own <c>Unchanged</c> check for a track list exists for the same reason.
    /// </remarks>
    [Fact]
    public void An_identical_template_written_again_is_not_a_change() {
        using var tree = new LayoutTree();

        var root = Grid(tree, "\"a b\"", columns: 2, columnWidth: 30f);
        tree.CalculateLayout(root, float.NaN, float.NaN, Direction.Ltr);
        Assert.False(tree.IsDirty(root));

        Assert.True(GridAreaTemplate.TryParse("\"a   b\"", out var again, out _));
        tree.SetGridTemplateAreas(root, again);

        Assert.False(tree.IsDirty(root));
    }

    static LayoutNodeId Grid(LayoutTree tree, string areas, int columns, float columnWidth) {
        var root = tree.CreateNode();
        tree.SetDisplay(root, Display.Grid);

        var tracks = new GridTrackSize[columns];
        Array.Fill(tracks, GridTrackSize.Single(GridSizingFunction.Points(columnWidth)));
        tree.SetGridTemplateColumns(root, tracks);

        Assert.True(GridAreaTemplate.TryParse(areas, out var template, out var refusal), refusal);
        tree.SetGridTemplateAreas(root, template);

        return root;
    }

    static LayoutNodeId Placed(LayoutTree tree, LayoutNodeId root, string area) {
        var child = tree.CreateNode();

        // What `grid-area: <name>` expands to: the same name on all four edges.
        tree.SetGridPlacement(child, Edge.Top, area);
        tree.SetGridPlacement(child, Edge.Bottom, area);
        tree.SetGridPlacement(child, Edge.Left, area);
        tree.SetGridPlacement(child, Edge.Right, area);
        tree.AddChild(root, child);

        return child;
    }

    static (float Left, float Top, float Width, float Height) Box(LayoutTree tree, LayoutNodeId node) =>
        (tree.GetLeft(node), tree.GetTop(node), tree.GetWidth(node), tree.GetHeight(node));
}
