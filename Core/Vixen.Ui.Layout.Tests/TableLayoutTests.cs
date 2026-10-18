// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Layout.Tests;

/// <summary>
///     The table formatting context, CSS 2.1 §17, against closed-form answers.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every fixture is one a block or a flex container would answer differently</b>, and
///         most of them are the fixed layout because §17.5.2.1 is the part of the chapter that is
///         normative arithmetic: a first-row cell with a <c>width</c> decides its column, the other
///         columns share what is left, and a cell in a later row decides nothing. The second row's
///         over-wide cell is in the first fixture for that last clause — a box that sized its cells
///         from their own widths puts it at 200.
///     </para>
///     <para>
///         Hand-written for <c>ContainmentTests</c>' reason: neither ported corpus has a table in it,
///         so nothing in <c>Generated/</c> can move. The numbers are §17.5.2.1 and §17.6.1 by hand,
///         and every one is an integer so the pixel grid cannot round an answer into agreement.
///     </para>
/// </remarks>
public class TableLayoutTests {
    sealed class Table : IDisposable {
        public readonly LayoutTree Tree = new();
        public readonly LayoutNodeId Root;
        public readonly LayoutNodeId Node;

        public Table(float spacing, TableLayout layout = TableLayout.Fixed, float width = 300f) {
            Root = Tree.CreateNode();
            Tree.SetDisplay(Root, Display.Block);
            Tree.SetDimension(Root, Dimension.Width, StyleLength.Points(400f));
            Tree.SetDimension(Root, Dimension.Height, StyleLength.Points(400f));

            Node = Tree.CreateNode();
            Tree.SetDisplay(Node, Display.Table);
            Tree.SetTableLayout(Node, layout);
            if (!float.IsNaN(width)) {
                Tree.SetDimension(Node, Dimension.Width, StyleLength.Points(width));
            }

            Tree.SetBorderSpacing(Node, Dimension.Width, StyleLength.Points(spacing));
            Tree.SetBorderSpacing(Node, Dimension.Height, StyleLength.Points(spacing));
            Tree.AddChild(Root, Node);
        }

        public LayoutNodeId Row() {
            var row = Tree.CreateNode();
            Tree.SetDisplay(row, Display.TableRow);
            Tree.AddChild(Node, row);

            return row;
        }

        /// <summary>A cell holding one block of the given size, so its content height is written rather than measured.</summary>
        public LayoutNodeId Cell(LayoutNodeId row, float width = float.NaN, float contentWidth = 0f, float contentHeight = 0f) {
            var cell = Tree.CreateNode();
            Tree.SetDisplay(cell, Display.TableCell);
            if (!float.IsNaN(width)) {
                Tree.SetDimension(cell, Dimension.Width, StyleLength.Points(width));
            }

            Tree.AddChild(row, cell);

            var content = Tree.CreateNode();
            Tree.SetDisplay(content, Display.Block);
            Tree.SetDimension(content, Dimension.Width, StyleLength.Points(contentWidth));
            Tree.SetDimension(content, Dimension.Height, StyleLength.Points(contentHeight));
            Tree.AddChild(cell, content);

            return cell;
        }

        public void Dispose() => Tree.Dispose();

        public void Lay(Direction direction = Direction.Ltr) => Tree.CalculateLayout(Root, 400f, 400f, direction);

        public (float Left, float Top, float Width, float Height) Box(LayoutNodeId node) =>
            (Tree.GetLeft(node), Tree.GetTop(node), Tree.GetWidth(node), Tree.GetHeight(node));
    }

    /// <summary>
    ///     §17.5.2.1: the first row's stated width decides its column, the rest share the remainder,
    ///     and a later row's stated width decides nothing.
    /// </summary>
    /// <remarks>
    ///     300 wide with 10 of spacing: four gutters leave 260 for three columns. Column one is 50, so
    ///     the other two are (260 − 50) / 2 = 105 each. Row one is its tallest cell, 40; row two is
    ///     30. The table is 10 + 40 + 10 + 30 + 10 = 100 tall.
    /// </remarks>
    [Fact]
    public void A_fixed_table_takes_its_columns_from_the_first_row() {
        using var table = new Table(spacing: 10f);
        var first = table.Row();
        var a = table.Cell(first, width: 50f, contentHeight: 20f);
        var b = table.Cell(first, contentHeight: 40f);
        var c = table.Cell(first);
        var second = table.Row();
        var d = table.Cell(second, width: 200f, contentHeight: 30f);
        var e = table.Cell(second);
        table.Lay();

        Assert.Equal((0f, 0f, 300f, 100f), table.Box(table.Node));

        // The row spans the columns and not the outer spacing.
        Assert.Equal((10f, 10f, 280f, 40f), table.Box(first));
        Assert.Equal((10f, 60f, 280f, 30f), table.Box(second));

        // Every cell in a row is the row's height, whatever its content asked for.
        Assert.Equal((0f, 0f, 50f, 40f), table.Box(a));
        Assert.Equal((60f, 0f, 105f, 40f), table.Box(b));
        Assert.Equal((175f, 0f, 105f, 40f), table.Box(c));

        // ⚠ The 200 decides nothing: it is in the second row.
        Assert.Equal((0f, 0f, 50f, 30f), table.Box(d));
        Assert.Equal((60f, 0f, 105f, 30f), table.Box(e));
    }

    /// <summary>When every column is decided and they fall short, the room is shared in proportion.</summary>
    [Fact]
    public void Decided_columns_that_fall_short_of_the_table_grow_in_proportion() {
        using var table = new Table(spacing: 0f);
        var row = table.Row();
        var a = table.Cell(row, width: 50f);
        var b = table.Cell(row, width: 150f);
        table.Lay();

        Assert.Equal(75f, table.Tree.GetWidth(a));
        Assert.Equal(225f, table.Tree.GetWidth(b));
        Assert.Equal(75f, table.Tree.GetLeft(b));
    }

    /// <summary>§17.5.2.1's "the greater of": columns that need more than the table's width widen the table.</summary>
    [Fact]
    public void Decided_columns_wider_than_the_table_widen_it() {
        using var table = new Table(spacing: 10f);
        var row = table.Row();
        table.Cell(row, width: 200f);
        var b = table.Cell(row, width: 200f);
        table.Lay();

        Assert.Equal(430f, table.Tree.GetWidth(table.Node));
        Assert.Equal(210f, table.Tree.GetLeft(b));
    }

    /// <summary>
    ///     A stated height is a minimum, and what it adds is shared by the rows in proportion to
    ///     their heights.
    /// </summary>
    [Fact]
    public void A_taller_table_grows_its_rows_in_proportion() {
        using var table = new Table(spacing: 0f);
        table.Tree.SetDimension(table.Node, Dimension.Height, StyleLength.Points(120f));
        var first = table.Row();
        table.Cell(first, contentHeight: 20f);
        var second = table.Row();
        table.Cell(second, contentHeight: 40f);
        table.Lay();

        Assert.Equal(120f, table.Tree.GetHeight(table.Node));
        Assert.Equal(40f, table.Tree.GetHeight(first));
        Assert.Equal(80f, table.Tree.GetHeight(second));
        Assert.Equal(40f, table.Tree.GetTop(second));
    }

    /// <summary>And a stated height smaller than the rows does not shrink them.</summary>
    [Fact]
    public void A_shorter_table_is_as_tall_as_its_rows() {
        using var table = new Table(spacing: 5f);
        table.Tree.SetDimension(table.Node, Dimension.Height, StyleLength.Points(10f));
        var row = table.Row();
        table.Cell(row, contentHeight: 40f);
        table.Lay();

        Assert.Equal(50f, table.Tree.GetHeight(table.Node));
    }

    /// <summary>CSS 2.1 §17.5: in an <c>rtl</c> table the first column is the rightmost.</summary>
    [Fact]
    public void An_rtl_table_puts_its_first_column_on_the_right() {
        using var table = new Table(spacing: 10f);
        var row = table.Row();
        var a = table.Cell(row, width: 50f);
        var b = table.Cell(row);
        table.Lay(Direction.Rtl);

        // 270 for the columns and 10 between them: a 280-wide row, and column one is its last 50.
        Assert.Equal(230f, table.Tree.GetLeft(a));
        Assert.Equal(0f, table.Tree.GetLeft(b));
        Assert.Equal(220f, table.Tree.GetWidth(b));
    }

    /// <summary>
    ///     ⚠ A row's columns are an input its cache key does not carry, so a change to row one moves
    ///     row two on the next pass.
    /// </summary>
    /// <remarks>
    ///     Row two's width and height are the same before and after, so a row answered from its
    ///     layout cache would keep its cells where the old columns put them. This is the fixture that
    ///     marking the row dirty in <c>LayOutTable</c> exists for.
    /// </remarks>
    [Fact]
    public void Changing_the_first_row_moves_the_columns_of_every_row() {
        using var table = new Table(spacing: 0f);
        var first = table.Row();
        var a = table.Cell(first, width: 100f);
        table.Cell(first);
        var second = table.Row();
        table.Cell(second);
        var d = table.Cell(second);
        table.Lay();

        Assert.Equal(100f, table.Tree.GetLeft(d));

        table.Tree.SetDimension(a, Dimension.Width, StyleLength.Points(60f));
        table.Lay();

        Assert.Equal(60f, table.Tree.GetLeft(d));
        Assert.Equal(240f, table.Tree.GetWidth(d));
    }

    /// <summary>Margins do not apply to a row or a cell, CSS 2.1 §17.</summary>
    [Fact]
    public void A_cells_margin_does_not_apply() {
        using var table = new Table(spacing: 0f);
        var row = table.Row();
        var a = table.Cell(row, width: 100f);
        var b = table.Cell(row);
        table.Tree.SetMargin(a, Edge.All, StyleLength.Points(7f));
        table.Tree.SetMargin(row, Edge.All, StyleLength.Points(7f));
        table.Lay();

        Assert.Equal((0f, 0f, 100f, 0f), table.Box(a));
        Assert.Equal(100f, table.Tree.GetLeft(b));
        Assert.Equal((0f, 0f, 300f, 0f), table.Box(row));
    }

    /// <summary>
    ///     ⚠ An <c>auto</c>-width table is laid out automatically whatever its <c>table-layout</c>
    ///     says, and shrinks to its columns' max-content.
    /// </summary>
    /// <remarks>
    ///     Two columns whose cells hold a 60- and a 30-wide block, three gutters of 10: 120, in a
    ///     400-wide containing block that a block box would have filled.
    /// </remarks>
    [Theory]
    [InlineData(TableLayout.Auto)]
    [InlineData(TableLayout.Fixed)]
    public void An_auto_width_table_shrinks_to_its_content(TableLayout layout) {
        using var table = new Table(spacing: 10f, layout, width: float.NaN);
        var row = table.Row();
        var a = table.Cell(row, contentWidth: 60f, contentHeight: 10f);
        var b = table.Cell(row, contentWidth: 30f, contentHeight: 10f);
        table.Lay();

        Assert.Equal(120f, table.Tree.GetWidth(table.Node));
        Assert.Equal(60f, table.Tree.GetWidth(a));
        Assert.Equal((70f, 0f, 30f, 10f), table.Box(b));
    }

    /// <summary>
    ///     The automatic layout with a stated width shares the extra in proportion to each column's
    ///     max-content.
    /// </summary>
    /// <remarks>300 − 3 × 10 = 270 for columns whose maxima are 60 and 30: 180 and 90.</remarks>
    [Fact]
    public void An_auto_table_with_a_width_shares_the_extra_by_max_content() {
        using var table = new Table(spacing: 10f, TableLayout.Auto);
        var row = table.Row();
        var a = table.Cell(row, contentWidth: 60f);
        var b = table.Cell(row, contentWidth: 30f);
        table.Lay();

        Assert.Equal(300f, table.Tree.GetWidth(table.Node));
        Assert.Equal(180f, table.Tree.GetWidth(a));
        Assert.Equal(90f, table.Tree.GetWidth(b));
        Assert.Equal(190f, table.Tree.GetLeft(b));
    }

    /// <summary>A child of a table that is not a row stands in for a one-cell row in the first column.</summary>
    [Fact]
    public void A_stray_child_is_a_one_cell_row_in_the_first_column() {
        using var table = new Table(spacing: 10f);
        var row = table.Row();
        table.Cell(row, width: 50f, contentHeight: 20f);
        table.Cell(row, contentHeight: 20f);

        var stray = table.Tree.CreateNode();
        table.Tree.SetDisplay(stray, Display.Block);
        table.Tree.SetDimension(stray, Dimension.Height, StyleLength.Points(15f));
        table.Tree.AddChild(table.Node, stray);
        table.Lay();

        Assert.Equal(65f, table.Tree.GetHeight(table.Node));
        Assert.Equal((10f, 40f, 50f, 15f), table.Box(stray));
        Assert.Equal(65f, table.Tree.GetHeight(table.Node));
    }

    /// <summary>
    ///     ⚠ A table in a cell lays its own columns out above its ancestor's, and its ancestor's are
    ///     still where they were afterwards.
    /// </summary>
    [Fact]
    public void A_table_in_a_cell_does_not_disturb_the_columns_around_it() {
        using var table = new Table(spacing: 0f);
        var row = table.Row();
        var outer = table.Cell(row, width: 100f);
        var after = table.Cell(row);

        var inner = table.Tree.CreateNode();
        table.Tree.SetDisplay(inner, Display.Table);
        table.Tree.SetTableLayout(inner, TableLayout.Fixed);
        table.Tree.SetDimension(inner, Dimension.Width, StyleLength.Points(100f));
        table.Tree.AddChild(outer, inner);

        var innerRow = table.Tree.CreateNode();
        table.Tree.SetDisplay(innerRow, Display.TableRow);
        table.Tree.AddChild(inner, innerRow);

        // Enough columns to make the scratch grow while the outer row is being laid out.
        var cells = new LayoutNodeId[20];
        for (var i = 0; i < cells.Length; i++) {
            cells[i] = table.Tree.CreateNode();
            table.Tree.SetDisplay(cells[i], Display.TableCell);
            table.Tree.SetDimension(cells[i], Dimension.Height, StyleLength.Points(5f));
            table.Tree.AddChild(innerRow, cells[i]);
        }

        table.Lay();

        Assert.Equal(5f, table.Tree.GetWidth(cells[0]));
        Assert.Equal(95f, table.Tree.GetLeft(cells[19]));
        Assert.Equal((100f, 0f, 200f, 5f), table.Box(after));
    }
}
