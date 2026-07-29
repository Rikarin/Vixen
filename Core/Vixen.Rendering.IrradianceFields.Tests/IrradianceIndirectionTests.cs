// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Rendering.IrradianceFields.Tests;

/// <summary>Divide, floor, fetch, divide — the whole lookup, and every way it can be off by one.</summary>
public class IrradianceIndirectionTests {
    static IrradianceIndirection Grid() =>
        new(new BoundingBox(new(0f), new(8f, 4f, 2f)), new(4, 2, 1));

    /// <summary>
    ///     <b>A cell is a box, not a grid point.</b> <c>MeshDistanceField</c>'s samples sit on the
    ///     lattice and its cell size divides by one less than the count; this divides by the count.
    ///     Both are right for what they hold, and confusing them is half a cell of error everywhere.
    /// </summary>
    [Fact]
    public void ACellIsTheBoxItCovers() {
        var grid = Grid();

        Assert.Equal(new Vector3(2f), grid.CellSize);
        Assert.Equal(new Vector3(4f, 0f, 0f), grid.CellBounds(new(2, 0, 0)).Minimum);
        Assert.Equal(new Vector3(6f, 2f, 2f), grid.CellBounds(new(2, 0, 0)).Maximum);
    }

    [Fact]
    public void APositionFallsInTheCellCoveringIt() {
        var grid = Grid();

        Assert.True(grid.TryCell(new(5f, 3f, 1f), out var cell));
        Assert.Equal(new Int3(2, 1, 0), cell);
    }

    /// <summary>
    ///     A position exactly on the far face belongs to the last cell, rather than to a cell that does
    ///     not exist. Every grid gets this wrong once.
    /// </summary>
    [Fact]
    public void TheFarFaceBelongsToTheLastCell() {
        var grid = Grid();

        Assert.True(grid.TryCell(new(8f, 4f, 2f), out var cell));
        Assert.Equal(new Int3(3, 1, 0), cell);

        grid.Assign(new(9, new(0, 0, 0), 4));

        Assert.True(grid.TryLocate(new(8f, 4f, 2f), out var brick, out var local));
        Assert.Equal(9, brick.Slot);

        // A brick of four cells over a grid of four: the far face is a local of one, not of zero,
        // which is what taking the position from the brick's own origin buys over a fractional part.
        Assert.Equal(1f, local.X, 5);
    }

    [Fact]
    public void APositionOutsideIsOutside() {
        var grid = Grid();

        Assert.False(grid.TryCell(new(-0.001f, 1f, 1f), out _));
        Assert.False(grid.TryCell(new(1f, 1f, 2.001f), out _));
    }

    [Fact]
    public void ACellWithNoBrickLocatesNothing() {
        var grid = Grid();

        Assert.False(grid.TryLocate(new(5f, 3f, 1f), out _, out _));

        grid.Assign(new(9, new(2, 1, 0), 1));

        Assert.True(grid.TryLocate(new(5f, 3f, 1f), out var brick, out var local));
        Assert.Equal(9, brick.Slot);
        Assert.Equal(1, brick.Size);
        Assert.Equal(0.5f, local.X, 5);
        Assert.Equal(1, grid.BrickCount);
    }

    /// <summary>
    ///     <b>A coarse brick writes itself into every cell it covers.</b> The alternative is a lookup
    ///     that searches or climbs a tree; this way the cost of being coarse is one integer pair per
    ///     cell of memory, and the saving is sixty-four probes instead of four thousand.
    /// </summary>
    [Fact]
    public void ACoarseBrickNamesItselfInEveryCellItCovers() {
        var grid = Grid();

        grid.Assign(new(3, new(0, 0, 0), 2));

        foreach (var cell in (Int3[]) [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(1, 1, 0)]) {
            Assert.Equal(new IrradianceCell(3, 2), grid[cell]);
            Assert.True(grid.TryBrick(cell, out var brick));
            Assert.Equal(new Int3(0, 0, 0), brick.Cell);
        }

        Assert.Equal(4, grid.Covered);

        // Once, not four times — which is what an origin test is for.
        Assert.Equal(1, grid.BrickCount);
        Assert.True(grid.IsOrigin(new(0, 0, 0)));
        Assert.False(grid.IsOrigin(new(1, 0, 0)));
    }

    /// <summary>A coarse brick's local coordinate spans the whole of it, not one of its cells.</summary>
    [Fact]
    public void ALocalCoordinateSpansTheBrickAndNotACell() {
        var grid = Grid();

        grid.Assign(new(3, new(0, 0, 0), 2));

        Assert.True(grid.TryLocate(new(1f, 1f, 1f), out _, out var near));
        Assert.True(grid.TryLocate(new(3f, 3f, 1f), out _, out var far));

        Assert.Equal(0.25f, near.X, 5);
        Assert.Equal(0.75f, far.X, 5);
    }

    /// <summary>
    ///     A brick has to start at a multiple of its own size, because dividing a cell coordinate by
    ///     the size only gives a position inside the brick when it does.
    /// </summary>
    [Fact]
    public void ABrickHasToBeAlignedToItsOwnSize() {
        var grid = Grid();

        Assert.Throws<ArgumentException>(() => grid.Assign(new(0, new(1, 0, 0), 2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.Assign(new(0, new(0, 0, 0), 3)));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.Assign(new(0, new(0, 0, 0), 0)));
    }

    /// <summary>
    ///     A brick may hang over the edge of the grid, because a resolution is not always a multiple
    ///     of a brick size. What hangs over is outside the bounds, where nothing samples.
    /// </summary>
    [Fact]
    public void ABrickMayHangOverTheEdge() {
        var grid = Grid();

        grid.Assign(new(5, new(0, 0, 0), 4));

        Assert.Equal(8, grid.Covered);
        Assert.Equal(1, grid.BrickCount);
    }

    [Fact]
    public void RevokingEmptiesEveryCellABrickCovered() {
        var grid = Grid();
        var brick = new IrradianceBrick(3, new(0, 0, 0), 2);

        grid.Assign(brick);
        grid.Revoke(brick);

        Assert.Equal(0, grid.Covered);
        Assert.False(grid[new(1, 1, 0)].HasBrick);
    }

    [Fact]
    public void CellsOutsideTheGridAreRefused() {
        var grid = Grid();

        Assert.False(grid.Holds(new(4, 0, 0)));
        Assert.False(grid.Holds(new(-1, 0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid[new(4, 0, 0)] = IrradianceCell.Empty);
    }

    [Fact]
    public void AnEmptyBoxCoversNothing() {
        Assert.Throws<ArgumentException>(() => new IrradianceIndirection(BoundingBox.Empty, new(2)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new IrradianceIndirection(new BoundingBox(new(0f), new(1f)), new(2, 0, 2))
        );
    }
}
