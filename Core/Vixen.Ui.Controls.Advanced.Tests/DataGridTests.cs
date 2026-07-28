// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Input;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>Virtualisation in both directions, frozen columns, sorting, grouping and inline edits.</summary>
public class DataGridTests {
    sealed class Unit {
        public Unit(string name, int level, string faction) {
            Name = name;
            Level = level;
            Faction = faction;
        }

        public string Name { get; set; }

        public int Level { get; set; }

        public string Faction { get; }
    }

    static DataGrid Grid(AdvancedFixture fixture, IEnumerable<Unit> units, int columns = 3) {
        var grid = fixture.Add<DataGrid>();

        grid.AddColumn("Name", static item => ((Unit) item).Name);
        grid.AddColumn("Level", static item => ((Unit) item).Level);
        grid.AddColumn("Faction", static item => ((Unit) item).Faction);

        for (var i = 3; i < columns; i++) {
            var index = i;
            grid.AddColumn($"Extra {index}", item => ((Unit) item).Level + index);
        }

        grid.SetItems(units);

        fixture.Update();
        grid.Refresh();
        fixture.Update();

        return grid;
    }

    static Unit[] Sample() => [
        new Unit("Ada", 12, "Blue"),
        new Unit("Bob", 3, "Red"),
        new Unit("Cy", 12, "Blue"),
        new Unit("Dee", 7, "Red")
    ];

    [Fact]
    public void A_huge_table_realises_only_the_rows_that_fit() {
        using var fixture = new AdvancedFixture();

        var grid = Grid(
            fixture,
            Enumerable.Range(0, 100_000).Select(static i => new Unit($"u{i}", i % 40, i % 2 == 0 ? "Blue" : "Red"))
        );

        Assert.Equal(100_000, grid.RowCount);

        // Doc 09's claim about this control by name: a hundred thousand rows, thirty-odd elements.
        Assert.True(grid.Rows.Count < 40, $"realised {grid.Rows.Count} rows");
    }

    [Fact]
    public void A_wide_table_realises_only_the_columns_that_fit() {
        using var fixture = new AdvancedFixture();
        var grid = Grid(fixture, Sample(), columns: 200);

        Assert.Equal(200, grid.Columns.Count);

        var live = grid.Rows[0].Cells.Count(static cell => cell.Column is not null);

        // ⚠ The half doc 09 asks for and a tree does not have. Twenty thousand cells exist as data
        // and a dozen exist as elements.
        Assert.True(live < 20, $"realised {live} cells");
        Assert.True(grid.Headers.Count(static header => header.Column is not null) < 20);
    }

    [Fact]
    public void Scrolling_sideways_rebinds_the_cells_of_the_rows_that_are_already_there() {
        using var fixture = new AdvancedFixture();
        var grid = Grid(fixture, Sample(), columns: 60);

        var element = grid.Rows[0];
        var before = element.Cells[0].Column;

        grid.Scroller.ScrollLeft = 2_000f;
        fixture.Update();

        Assert.Same(element, grid.Rows[0]);
        Assert.NotSame(before, grid.Rows[0].Cells[0].Column);
    }

    [Fact]
    public void A_frozen_column_stays_put_while_the_rest_scrolls() {
        using var fixture = new AdvancedFixture();
        var grid = Grid(fixture, Sample(), columns: 60);

        grid.FrozenColumns = 1;
        fixture.Update();

        var frozen = grid.Rows[0].Cells[0];

        Assert.Same(grid.Columns[0], frozen.Column);
        Assert.Equal("0px", frozen.GetStyle("left"));

        grid.Scroller.ScrollLeft = 500f;
        fixture.Update();

        // ⚠ Plus the scroll offset, because the row it is in has been moved left by exactly that
        // much. One line, and it is the entire freezing mechanism — there is no second scroller.
        Assert.Same(grid.Columns[0], grid.Rows[0].Cells[0].Column);
        Assert.Equal("500px", grid.Rows[0].Cells[0].GetStyle("left"));

        // And the first scrolling column is realised after the band rather than under it.
        Assert.NotSame(grid.Columns[0], grid.Rows[0].Cells[1].Column);
    }

    [Fact]
    public void Sorting_is_a_view_and_leaves_the_items_where_they_were() {
        using var fixture = new AdvancedFixture();

        var units = Sample();
        var grid = Grid(fixture, units);

        grid.SortBy(grid.Columns[1]);

        Assert.Equal([1, 3, 0, 2], Enumerable.Range(0, 4).Select(grid.ItemAt));

        grid.SortBy(grid.Columns[1], descending: true);
        Assert.Equal([0, 2, 3, 1], Enumerable.Range(0, 4).Select(grid.ItemAt));

        // ⚠ The caller's list is untouched. A grid that sorted in place would silently reorder a
        // game's entity list because somebody clicked a heading.
        Assert.Equal(["Ada", "Bob", "Cy", "Dee"], units.Select(static unit => unit.Name));
    }

    [Fact]
    public void Equal_rows_keep_the_order_they_were_in() {
        using var fixture = new AdvancedFixture();
        var grid = Grid(fixture, Sample());

        // Ada and Cy are both level 12, and they must stay in that order both ways round — which is
        // what makes "sort by one column, then another" a two-key sort rather than a shuffle.
        grid.SortBy(grid.Columns[1]);
        Assert.Equal([0, 2], new[] { grid.ItemAt(2), grid.ItemAt(3) });

        grid.SortBy(grid.Columns[1], descending: true);
        Assert.Equal([0, 2], new[] { grid.ItemAt(0), grid.ItemAt(1) });
    }

    [Fact]
    public void Clicking_a_heading_sorts_and_clicking_it_again_reverses() {
        using var fixture = new AdvancedFixture();
        var grid = Grid(fixture, Sample());

        var header = grid.Headers.First(header => ReferenceEquals(header.Column, grid.Columns[1]));

        fixture.Click(header.Label);
        Assert.Same(grid.Columns[1], grid.SortColumn);
        Assert.False(grid.SortDescending);

        fixture.Click(header.Label);
        Assert.True(grid.SortDescending);
    }

    [Fact]
    public void Grouping_puts_a_header_above_each_run_and_collapsing_hides_it() {
        using var fixture = new AdvancedFixture();
        var grid = Grid(fixture, Sample());

        grid.GroupBy(grid.Columns[2]);

        // Two groups of two, each with a header above it.
        Assert.Equal(6, grid.RowCount);
        Assert.Equal(-1, grid.ItemAt(0));
        Assert.Equal(0, grid.ItemAt(1));

        grid.ToggleGroup("Blue");

        Assert.Equal(4, grid.RowCount);
        Assert.Equal(-1, grid.ItemAt(0));
        Assert.Equal(-1, grid.ItemAt(1));
    }

    [Fact]
    public void A_group_header_shows_how_many_are_in_it() {
        using var fixture = new AdvancedFixture();
        var grid = Grid(fixture, Sample());

        grid.GroupBy(grid.Columns[2]);
        fixture.Update();

        var header = grid.Rows.First(static row => row.Item is null && row.Index == 0);
        Assert.Equal("Blue (2)", header.GroupLabel.Text);
    }

    [Fact]
    public void Clicking_a_row_selects_it_and_control_adds_to_it() {
        using var fixture = new AdvancedFixture();
        var grid = Grid(fixture, Sample());

        var changes = 0;
        grid.SelectionChanged += _ => changes++;

        fixture.Click(grid.Rows[0].Cells[0]);
        Assert.Equal([0], grid.Selection);

        fixture.Click(grid.Rows[2].Cells[0], ModifierKeys.Control);
        Assert.Equal([0, 2], grid.Selection.Order());

        Assert.Equal(2, changes);
    }

    [Fact]
    public void Shift_selects_the_rows_between_two_that_are_on_screen() {
        using var fixture = new AdvancedFixture();
        var grid = Grid(fixture, Sample());

        grid.SortBy(grid.Columns[1]);
        fixture.Update();

        // Sorted, the view is Bob(1), Dee(3), Ada(0), Cy(2). Shift from the first to the third must
        // take the three that are adjacent *on screen*, not items 1, 2 and 3.
        grid.Select(1);
        grid.Select(0, ModifierKeys.Shift);

        Assert.Equal([0, 1, 3], grid.Selection.Order());
    }

    [Fact]
    public void Down_steps_past_a_group_header_rather_than_onto_it() {
        using var fixture = new AdvancedFixture();
        var grid = Grid(fixture, Sample());

        grid.GroupBy(grid.Columns[2]);
        fixture.Document.Focus(grid);

        grid.Select(2);
        fixture.Type(InputKey.Down);

        // The row after Cy is the "Red" header, and a header is not something anybody selects.
        Assert.Equal(1, Assert.Single(grid.Selection));
    }

    [Fact]
    public void A_double_click_opens_an_editor_and_committing_writes_the_item() {
        using var fixture = new AdvancedFixture();

        var units = Sample();
        var grid = Grid(fixture, units);

        grid.Columns[0].Commit = static (item, text) => ((Unit) item).Name = text;

        var edited = 0;
        grid.CellEdited += (_, _, _) => edited++;

        var cell = grid.Rows[0].Cells[0];

        Assert.True(grid.BeginEdit(cell));
        Assert.NotNull(cell.Editor);

        cell.Editor.Value = "Ada Lovelace";
        fixture.Type(InputKey.Enter);

        Assert.Equal("Ada Lovelace", units[0].Name);
        Assert.Equal(1, edited);
        Assert.Null(cell.Editor);
    }

    [Fact]
    public void A_column_with_no_commit_refuses_to_be_edited() {
        using var fixture = new AdvancedFixture();
        var grid = Grid(fixture, Sample());

        // ⚠ Refused rather than opened and thrown away, which is the honest way to say read-only.
        Assert.False(grid.BeginEdit(grid.Rows[0].Cells[2]));
        Assert.Null(grid.Rows[0].Cells[2].Editor);
    }

    [Fact]
    public void An_escaped_edit_leaves_the_item_alone() {
        using var fixture = new AdvancedFixture();

        var units = Sample();
        var grid = Grid(fixture, units);

        grid.Columns[0].Commit = static (item, text) => ((Unit) item).Name = text;

        var cell = grid.Rows[0].Cells[0];
        grid.BeginEdit(cell);

        cell.Editor!.Value = "nope";
        fixture.Type(InputKey.Escape);

        Assert.Equal("Ada", units[0].Name);
        Assert.Null(cell.Editor);
    }

    [Fact]
    public void A_template_fills_the_cell_and_the_default_text_gets_out_of_the_way() {
        using var fixture = new AdvancedFixture();
        var grid = fixture.Add<DataGrid>();

        var column = grid.AddColumn("Health", static item => ((Unit) item).Level);

        column.Template = static (cell, item) => {
            if (cell.Children.OfType<ProgressBar>().FirstOrDefault() is not { } bar) {
                bar = cell.Add<ProgressBar>();
            }

            bar.Maximum = 40f;
            bar.Value = ((Unit) item).Level;
        };

        grid.SetItems(Sample());

        fixture.Update();
        grid.Refresh();
        fixture.Update();

        var cell = grid.Rows[0].Cells[0];

        Assert.True(cell.Label.HasClass("hidden"));
        Assert.Equal(12f, Assert.Single(cell.Children.OfType<ProgressBar>()).Value);
    }

    [Fact]
    public void Dragging_a_grip_resizes_the_column() {
        using var fixture = new AdvancedFixture();
        var grid = Grid(fixture, Sample());

        var header = grid.Headers.First(header => ReferenceEquals(header.Column, grid.Columns[0]));
        var grip = AdvancedFixture.Centre(header.Grip);

        fixture.Press(grip.X, grip.Y);
        fixture.Move(header.AbsoluteLeft + 200f, grip.Y);
        fixture.Release(header.AbsoluteLeft + 200f, grip.Y);

        Assert.Equal(200f, grid.Columns[0].Width, 1);

        // ⚠ And it did not sort, which is the reason a heading sorts on release rather than on press.
        Assert.Null(grid.SortColumn);
    }

    [Fact]
    public void A_column_cannot_be_dragged_narrower_than_its_minimum() {
        using var fixture = new AdvancedFixture();
        var grid = Grid(fixture, Sample());

        grid.Columns[0].MinimumWidth = 60f;

        var header = grid.Headers.First(header => ReferenceEquals(header.Column, grid.Columns[0]));
        var grip = AdvancedFixture.Centre(header.Grip);

        fixture.Press(grip.X, grip.Y);
        fixture.Move(header.AbsoluteLeft - 40f, grip.Y);
        fixture.Release(header.AbsoluteLeft - 40f, grip.Y);

        Assert.Equal(60f, grid.Columns[0].Width);
    }

    [Fact]
    public void Dragging_a_heading_moves_the_column() {
        using var fixture = new AdvancedFixture();
        var grid = Grid(fixture, Sample());

        var first = grid.Columns[0];
        var header = grid.Headers.First(header => ReferenceEquals(header.Column, first));
        var label = AdvancedFixture.Centre(header.Label);

        // Into the third column's band, which starts two widths along.
        fixture.Press(label.X, label.Y);
        fixture.Move(grid.Header.AbsoluteLeft + 260f, label.Y);
        fixture.Release(grid.Header.AbsoluteLeft + 260f, label.Y);

        Assert.Equal(2, grid.IndexOf(first));
    }

    [Fact]
    public void Setting_items_again_drops_the_selection() {
        using var fixture = new AdvancedFixture();
        var grid = Grid(fixture, Sample());

        grid.Select(2);
        Assert.Single(grid.Selection);

        grid.SetItems(Sample());

        // Otherwise index 2 means a different object and the highlight is on a stranger.
        Assert.Empty(grid.Selection);
    }

    [Fact]
    public void A_mixed_column_sorts_as_text_rather_than_throwing() {
        using var fixture = new AdvancedFixture();
        var grid = fixture.Add<DataGrid>();

        var column = grid.AddColumn("Mixed", static item => ((Unit) item).Level % 2 == 0 ? item : ((Unit) item).Name);
        grid.SetItems(Sample());

        fixture.Update();
        grid.Refresh();

        // A bug in the caller, and it must not become an exception at a mouse click.
        grid.SortBy(column);
        Assert.Equal(4, grid.RowCount);
    }

    [Fact]
    public void A_number_column_sorts_numerically_rather_than_as_text() {
        using var fixture = new AdvancedFixture();

        var grid = Grid(
            fixture,
            [new Unit("a", 2, "x"), new Unit("b", 10, "x"), new Unit("c", 1, "x")]
        );

        grid.SortBy(grid.Columns[1]);

        // ⚠ 1, 2, 10 — not "1", "10", "2", which is what a comparer that stringified would give.
        Assert.Equal(
            [1, 2, 10],
            Enumerable.Range(0, 3).Select(row => int.Parse(Cell(grid, row, 1), CultureInfo.InvariantCulture))
        );
    }

    static string Cell(DataGrid grid, int row, int column) =>
        grid.Columns[column].TextOf(grid.Items[grid.ItemAt(row)]);
}
