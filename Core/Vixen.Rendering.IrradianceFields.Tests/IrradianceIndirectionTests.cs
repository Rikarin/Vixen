// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Rendering.IrradianceFields.Tests;

/// <summary>Divide, floor, fetch — the whole lookup, and every way it can be off by one.</summary>
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

        Assert.True(grid.TryCell(new(5f, 3f, 1f), out var cell, out var local));

        Assert.Equal(new Int3(2, 1, 0), cell);
        Assert.Equal(0.5f, local.X, 5);
        Assert.Equal(0.5f, local.Y, 5);
        Assert.Equal(0.5f, local.Z, 5);
    }

    /// <summary>
    ///     A position exactly on the far face belongs to the last cell at a local of one, rather than
    ///     to a cell that does not exist. Every grid gets this wrong once.
    /// </summary>
    [Fact]
    public void TheFarFaceBelongsToTheLastCell() {
        var grid = Grid();

        Assert.True(grid.TryCell(new(8f, 4f, 2f), out var cell, out var local));

        Assert.Equal(new Int3(3, 1, 0), cell);
        Assert.Equal(new Vector3(1f), local);
    }

    [Fact]
    public void APositionOutsideIsOutside() {
        var grid = Grid();

        Assert.False(grid.TryCell(new(-0.001f, 1f, 1f), out _, out _));
        Assert.False(grid.TryCell(new(1f, 1f, 2.001f), out _, out _));
    }

    [Fact]
    public void ACellWithNoBrickLocatesNothing() {
        var grid = Grid();

        Assert.False(grid.TryLocate(new(5f, 3f, 1f), out var slot, out _));
        Assert.Equal(IrradianceIndirection.Empty, slot);

        grid[new(2, 1, 0)] = 9;

        Assert.True(grid.TryLocate(new(5f, 3f, 1f), out slot, out var local));
        Assert.Equal(9, slot);
        Assert.Equal(0.5f, local.X, 5);
        Assert.Equal(1, grid.Occupancy);
    }

    [Fact]
    public void CellsOutsideTheGridAreRefused() {
        var grid = Grid();

        Assert.False(grid.Holds(new(4, 0, 0)));
        Assert.False(grid.Holds(new(-1, 0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid[new(4, 0, 0)] = 0);
    }

    [Fact]
    public void AnEmptyBoxCoversNothing() {
        Assert.Throws<ArgumentException>(() => new IrradianceIndirection(BoundingBox.Empty, new(2)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new IrradianceIndirection(new BoundingBox(new(0f), new(1f)), new(2, 0, 2))
        );
    }
}
