// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>A grid of forty thousand items and about sixty elements.</summary>
/// <remarks>
///     <para>
///         <b>The counting is the test</b>, for the reason <see cref="VirtualizingPanelTests" />
///         gives: it is the only thing that distinguishes a virtualised grid from one that works
///         perfectly and allocates forty thousand tiles.
///     </para>
///     <para>
///         ⚠ <b>What a grid has that a list does not is the column count, and most of these are
///         about it.</b> It comes from the <i>measured</i> width, so it changes when the panel is
///         resized — and every position and the whole content height change with it.
///     </para>
/// </remarks>
public class VirtualizingGridTests {
    const int Items = 40_000;

    /// <summary>A grid over forty thousand items, 300 wide and 200 tall, with 50-pixel tiles.</summary>
    /// <remarks>Six columns across, ten lines of viewport — chosen so both are easy to count.</remarks>
    static (ControlFixture Fixture, VirtualizingGrid Grid, List<int> Bound) Gridded(int count = Items, float width = 300f) {
        var fixture = new ControlFixture(
            600f,
            400f,
            // ⚠ The grow has to be turned off as well as the width set. `ControlTheme` gives the
            // grid `flex: 1`, and in the root's row direction that is the *main* axis — so a declared
            // width is overridden by the grow and the panel fills the document instead. Which is
            // fine in the product, where the grid is inside a panel that constrains it, and wrong in
            // a test that is about counting columns.
            $"virtualizing-grid {{ flex-grow: 0; flex-shrink: 0; flex-basis: auto; width: {width.ToString(System.Globalization.CultureInfo.InvariantCulture)}px; height: 200px; --tile-width: 50px; --tile-height: 20px; }}"
        );

        var grid = fixture.Document.Root.Add<VirtualizingGrid>();
        var bound = new List<int>();

        grid.CreateTile = static owner => owner.Scroller.Content.Add<UiElement>("tile");
        grid.BindTile = (_, item) => bound.Add(item);
        grid.Count = count;

        fixture.Update();
        bound.Clear();

        return (fixture, grid, bound);
    }

    [Fact]
    public void Forty_thousand_items_are_a_pool_the_size_of_the_viewport() {
        var (fixture, grid, _) = Gridded();
        using var owned = fixture;

        Assert.Equal(Items, grid.Count);
        Assert.Equal(6, grid.Columns);

        // Ten lines of viewport at 20 a line, plus the overscan at each end and one partial line,
        // times six columns.
        Assert.InRange(grid.Tiles.Count, 60, 120);
        Assert.Equal(grid.Tiles.Count, grid.Scroller.Content.Children.Count);
    }

    /// <summary>
    ///     ⚠ The height is a declaration rather than a measurement, and for a grid it is the item
    ///     count divided by the columns — so it changes when the panel is resized, which a list's
    ///     never does.
    /// </summary>
    [Fact]
    public void The_scrollable_height_is_the_number_of_lines_rather_than_of_items() {
        var (fixture, grid, _) = Gridded();
        using var owned = fixture;

        Assert.Equal(Items / 6f * 20f, grid.Scroller.Content.Height, 20f);
    }

    [Fact]
    public void A_narrower_panel_has_fewer_columns_and_a_taller_content() {
        var (wideFixture, wide, _) = Gridded(width: 300f);
        using var ownedWide = wideFixture;

        var (narrowFixture, narrow, _) = Gridded(width: 150f);
        using var ownedNarrow = narrowFixture;

        Assert.Equal(6, wide.Columns);
        Assert.Equal(3, narrow.Columns);

        // The same items in half the columns is twice the lines, and the content height follows —
        // which is the number a list never has to recompute.
        Assert.True(
            narrow.Scroller.Content.Height > wide.Scroller.Content.Height,
            "halving the width did not make the content taller"
        );
    }

    /// <summary>
    ///     ⚠ At least one column however narrow it gets. Zero would be divided by, and a single
    ///     column that overflows is a readable answer to a panel dragged narrower than one tile.
    /// </summary>
    [Fact]
    public void A_panel_narrower_than_one_tile_still_has_a_column() {
        var (fixture, grid, _) = Gridded(width: 20f);
        using var owned = fixture;

        Assert.Equal(1, grid.Columns);
    }

    [Fact]
    public void An_item_is_placed_at_its_column_and_its_line() {
        var (fixture, grid, _) = Gridded(count: 20);
        using var owned = fixture;

        // Item seven of six columns is line one, column one: 50 across and 20 down.
        var tile = grid.TileOf(7);

        Assert.NotNull(tile);
        Assert.Equal(50f, tile.Left, 0.5f);
        Assert.Equal(20f, tile.Top, 0.5f);
    }

    [Fact]
    public void Scrolling_rebinds_the_tiles_it_already_has() {
        var (fixture, grid, bound) = Gridded();
        using var owned = fixture;

        var before = grid.Tiles.Count;

        grid.Scroller.ScrollTop = 20_000f;
        fixture.Update();

        Assert.Equal(before, grid.Tiles.Count);
        Assert.NotEmpty(bound);
        Assert.All(bound, item => Assert.True(item > 1000, "a scroll to the middle rebound an item near the start"));
    }

    /// <summary>
    ///     ⚠ <b>The clamp is in lines, not in items, and getting it wrong strands the end.</b>
    ///     Clamping to <c>Count - capacity</c> — which is what a list does — lands mid-line; snapping
    ///     that to a line boundary moves the window earlier than the end, and the last few items then
    ///     cannot be reached however far the grid is scrolled.
    /// </summary>
    [Fact]
    public void The_very_last_item_can_be_reached() {
        var (fixture, grid, _) = Gridded();
        using var owned = fixture;

        grid.ScrollIntoView(Items - 1);
        fixture.Update();

        Assert.NotNull(grid.TileOf(Items - 1));
    }

    /// <summary>
    ///     ⚠ A pool beginning mid-line would put item <c>n</c> at a different column every time the
    ///     offset crossed a tile, so the whole grid would shuffle sideways as it scrolled.
    /// </summary>
    [Fact]
    public void The_pool_begins_at_the_start_of_a_line() {
        var (fixture, grid, _) = Gridded();
        using var owned = fixture;

        foreach (var offset in new[] { 0f, 13f, 27f, 400f, 9_999f }) {
            grid.Scroller.ScrollTop = offset;
            fixture.Update();

            Assert.Equal(0, grid.FirstItem % grid.Columns);
        }
    }

    [Fact]
    public void A_surplus_tile_is_parked_rather_than_removed() {
        var (fixture, grid, _) = Gridded();
        using var owned = fixture;

        var pooled = grid.Scroller.Content.Children.Count;

        grid.Count = 3;
        fixture.Update();

        // The pool kept its elements — shrinking it would allocate again on the next scroll — and
        // the ones with no item stopped drawing.
        Assert.Equal(pooled, grid.Scroller.Content.Children.Count);
        Assert.Null(grid.TileOf(3));
        Assert.NotNull(grid.TileOf(2));
    }

    [Fact]
    public void An_empty_grid_realises_nothing_and_does_not_throw() {
        var (fixture, grid, _) = Gridded(count: 0);
        using var owned = fixture;

        Assert.Null(grid.TileOf(0));
        Assert.Equal(0f, grid.Scroller.Content.Height, 0.5f);
    }

    /// <summary>
    ///     ⚠ The subscription to <c>LayoutFinished</c> is what makes a resize re-realise, and one
    ///     left behind is a handler running against a control that has been taken out of the tree.
    /// </summary>
    [Fact]
    public void Removing_the_grid_stops_it_listening() {
        var (fixture, grid, bound) = Gridded();
        using var owned = fixture;

        grid.Remove();
        bound.Clear();

        fixture.Update();

        Assert.Empty(bound);
    }
}
