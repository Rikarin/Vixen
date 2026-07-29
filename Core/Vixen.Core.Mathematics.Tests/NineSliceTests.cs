// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Core.Mathematics.Tests;

/// <summary>
///     The cut a stretched panel and a stretched sprite both make, and the properties everything
///     downstream leans on: the cells tile, the corners keep their size, and a box too small for its
///     own borders shrinks them without distorting them.
/// </summary>
public class NineSliceTests {
    static Rectangle[] Split(Rectangle box, NineSlice border) {
        var cells = new Rectangle[NineSlice.CellCount];
        border.Split(box, cells);

        return cells;
    }

    [Fact]
    public void The_nine_cells_tile_the_box_exactly() {
        var box = new Rectangle(10f, 20f, 100f, 60f);
        var cells = Split(box, new(12f, 8f, 16f, 4f));

        // Every seam is shared to the bit, which is what keeps a stretched panel from showing a
        // hairline between its corner and the edge beside it. Computed as differenced positions
        // rather than summed widths for exactly this reason.
        Assert.Equal(box.Left, cells[0].Left);
        Assert.Equal(box.Top, cells[0].Top);
        Assert.Equal(box.Right, cells[8].Right);
        Assert.Equal(box.Bottom, cells[8].Bottom);

        for (var row = 0; row < 3; row++) {
            Assert.Equal(cells[row * 3].Right, cells[(row * 3) + 1].Left);
            Assert.Equal(cells[(row * 3) + 1].Right, cells[(row * 3) + 2].Left);
        }

        for (var column = 0; column < 3; column++) {
            Assert.Equal(cells[column].Bottom, cells[column + 3].Top);
            Assert.Equal(cells[column + 3].Bottom, cells[column + 6].Top);
        }
    }

    [Fact]
    public void The_corners_are_the_border_and_the_middle_is_what_is_left() {
        var cells = Split(new(0f, 0f, 100f, 60f), new(12f, 8f, 16f, 4f));

        Assert.Equal(new Rectangle(0f, 0f, 12f, 8f), cells[0]);
        Assert.Equal(new Rectangle(84f, 0f, 16f, 8f), cells[2]);
        Assert.Equal(new Rectangle(0f, 56f, 12f, 4f), cells[6]);
        Assert.Equal(new Rectangle(84f, 56f, 16f, 4f), cells[8]);

        // The one cell that carries the stretch, and the one a caller may want to leave out.
        Assert.Equal(new Rectangle(12f, 8f, 72f, 48f), cells[NineSlice.Centre]);
    }

    [Fact]
    public void An_empty_inset_leaves_the_box_whole_in_the_middle_cell() {
        var cells = Split(new(5f, 5f, 40f, 30f), NineSlice.None);

        Assert.True(NineSlice.None.IsEmpty);
        Assert.Equal(new Rectangle(5f, 5f, 40f, 30f), cells[NineSlice.Centre]);

        // ⚠ Still nine cells, eight of them empty. Compacting would move the middle away from index
        // four, and every caller indexes rather than searches.
        Assert.Equal(NineSlice.CellCount, cells.Length);
        Assert.All(cells[..NineSlice.Centre], cell => Assert.True(cell.IsEmpty));
    }

    [Fact]
    public void A_missing_edge_leaves_its_row_flat_rather_than_dropping_it() {
        var cells = Split(new(0f, 0f, 50f, 50f), new(10f, 0f, 10f, 10f));

        Assert.True(cells[0].IsEmpty);
        Assert.True(cells[1].IsEmpty);
        Assert.True(cells[2].IsEmpty);

        // The middle row starts at the top edge, because the top border is nothing.
        Assert.Equal(0f, cells[3].Top);
        Assert.Equal(new Rectangle(10f, 0f, 30f, 40f), cells[NineSlice.Centre]);
    }

    [Fact]
    public void Borders_that_do_not_fit_shrink_by_one_factor_so_the_corners_keep_their_aspect() {
        // 40 + 40 of horizontal border in a box 40 wide: everything scales by a half. ⚠ And the
        // *vertical* borders scale by the same half even though they fit, because scaling the axes
        // independently is what squashes a corner into a different shape than it was drawn at.
        var border = new NineSlice(40f, 20f, 40f, 20f).Fit(40f, 200f);

        Assert.Equal(20f, border.Left, 4);
        Assert.Equal(20f, border.Right, 4);
        Assert.Equal(10f, border.Top, 4);
        Assert.Equal(10f, border.Bottom, 4);
    }

    [Fact]
    public void An_inset_that_fits_is_left_exactly_alone() {
        var border = new NineSlice(8f, 6f, 8f, 6f);

        Assert.Equal(border, border.Fit(100f, 100f));

        // The boundary case: borders that exactly fill the box are not shrunk, so a box drawn at its
        // own minimum size still shows both corners at full size and no middle.
        Assert.Equal(border, border.Fit(16f, 12f));
    }

    [Fact]
    public void A_box_with_no_room_at_all_shrinks_the_border_to_nothing() {
        var border = new NineSlice(10f, 10f, 10f, 10f).Fit(0f, 40f);
        var cells = Split(new(0f, 0f, 0f, 40f), border);

        Assert.Equal(0f, border.Left, 4);
        Assert.All(cells, cell => Assert.True(cell.IsEmpty));
    }

    [Fact]
    public void Scaling_moves_an_inset_between_units_per_axis() {
        // Texels to UVs on a 256×64 sheet: the two axes divide by different numbers, which is the
        // whole reason this takes two.
        var uv = new NineSlice(16f, 8f, 16f, 8f).Scaled(1f / 256f, 1f / 64f);

        Assert.Equal(0.0625f, uv.Left, 5);
        Assert.Equal(0.125f, uv.Top, 5);
        Assert.Equal(0.0625f, uv.Right, 5);
        Assert.Equal(0.125f, uv.Bottom, 5);
    }

    [Fact]
    public void A_source_region_and_a_destination_box_cut_the_same_way() {
        // What makes a nine-slice work at all: cell *i* of the destination is drawn with cell *i* of
        // the source, so the two splits have to agree about which cell is which whatever the units.
        var source = Split(new(0.25f, 0f, 0.5f, 1f), new NineSlice(0.05f, 0.1f, 0.05f, 0.1f));
        var destination = Split(new(0f, 0f, 200f, 80f), new NineSlice(10f, 10f, 10f, 10f));

        Assert.Equal(0.25f, source[0].Left, 5);
        Assert.Equal(0f, destination[0].Left, 5);

        // Both middles sit at index four with border on every side of them.
        Assert.Equal(0.3f, source[NineSlice.Centre].Left, 5);
        Assert.Equal(10f, destination[NineSlice.Centre].Left, 5);
    }

    [Fact]
    public void Negative_edges_are_clamped_rather_than_refused() {
        // Insets come from authored data, and a mistyped sprite should draw oddly rather than throw
        // in the middle of a frame.
        var cells = Split(new(0f, 0f, 40f, 40f), new(-10f, 5f, 10f, 5f));

        Assert.Equal(0f, cells[0].Width);
        Assert.Equal(new Rectangle(0f, 5f, 30f, 30f), cells[NineSlice.Centre]);
    }

    [Fact]
    public void A_span_too_short_to_hold_the_cells_is_refused() {
        var border = NineSlice.Uniform(4f);

        Assert.Throws<ArgumentException>(() => {
            Span<Rectangle> cells = stackalloc Rectangle[NineSlice.CellCount - 1];
            border.Split(new(0f, 0f, 10f, 10f), cells);
        });
    }
}
