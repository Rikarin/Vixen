// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Xunit;

namespace Vixen.Rendering.Tests;

/// <summary>
///     The streaming policy over <see cref="PageResidency" /> — [docs/plan/31 § D13] and [§ B6].
/// </summary>
/// <remarks>
///     What is under test is the <em>decision</em>: which cells a frame's sources want, and which of
///     those are in use versus merely coming. The loading, placing and evicting are
///     <see cref="PageResidency" />'s and are tested there.
/// </remarks>
public sealed class StreamingGridTests {
    /// <summary>A store that records what it was asked for and answers immediately.</summary>
    sealed class InstantStore : IPageStore {
        readonly Dictionary<PageKey, int> placed = [];

        public int PageSize { get; init; } = 256;
        public int SlotCount { get; init; } = 4096;

        public List<PageKey> Loaded { get; } = [];
        public List<PageKey> Evicted { get; } = [];

        public ValueTask<int> LoadAsync(PageKey key, Memory<byte> destination, CancellationToken cancellation) {
            lock (Loaded) {
                Loaded.Add(key);
            }

            return ValueTask.FromResult(PageSize);
        }

        public bool Place(PageKey key, int slot, ReadOnlySpan<byte> bytes) {
            placed[key] = slot;
            return true;
        }

        public void Evict(PageKey key, int slot) {
            placed.Remove(key);
            Evicted.Add(key);
        }
    }

    /// <summary>A 10 × 10 grid of 100 m cells with its corner at the origin.</summary>
    static StreamingGrid Grid(int source = 7) => new(source, Vector2.Zero, 100f, 10, 10);

    static (PageResidency Residency, InstantStore Store) Service(int slots = 4096) {
        var store = new InstantStore { SlotCount = slots };
        return (new PageResidency(store, (long)store.PageSize * slots), store);
    }

    /// <summary>Runs frames until everything asked for has arrived, or the patience runs out.</summary>
    /// <remarks>
    ///     A load is asynchronous even when the store answers immediately — `Service` starts it and a
    ///     later `Service` places it — so a test that serviced once and asserted residency would be
    ///     asserting a race. This is `PageResidencyTests.Settle`, for its reasons.
    /// </remarks>
    static void Settle(PageResidency residency) {
        var waited = Stopwatch.StartNew();

        while (waited.Elapsed < TimeSpan.FromSeconds(30)) {
            residency.Service(64);

            if (residency.PendingRequests == 0 && residency.Loading == 0) {
                residency.Service(64);
                return;
            }

            Thread.Sleep(1);
        }
    }

    [Fact]
    public void AGridWithNoExtentIsRefused() {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StreamingGrid(0, Vector2.Zero, 0f, 4, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StreamingGrid(0, Vector2.Zero, 100f, 0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StreamingGrid(0, Vector2.Zero, 100f, 4, -1));
    }

    [Fact]
    public void KeysCarryTheGridsOwnSourceAndRoundTripToTheirCell() {
        var grid = Grid(source: 7);

        Assert.Equal(100, grid.CellCount);

        for (var z = 0; z < grid.CountZ; z++) {
            for (var x = 0; x < grid.CountX; x++) {
                var key = grid.KeyOf(x, z);

                Assert.Equal(7, key.Source);
                Assert.Equal((x, z), grid.CellOf(key.Index));
            }
        }
    }

    [Fact]
    public void BoundsTileTheGridWithoutGapsOrOverlap() {
        var grid = Grid();

        var (low, high) = grid.BoundsOf(3, 4);

        Assert.Equal(new(300f, 400f), low);
        Assert.Equal(new(400f, 500f), high);

        // The next cell along starts exactly where this one stopped.
        Assert.Equal(high.X, grid.BoundsOf(4, 4).Minimum.X);
        Assert.Equal(high.Y, grid.BoundsOf(3, 5).Minimum.Y);
    }

    /// <summary>
    ///     Distance is measured to the cell, not to its centre.
    /// </summary>
    /// <remarks>
    ///     Measuring to the centre makes the radius mean different things depending on where in a cell
    ///     somebody stands, which shows up as a load boundary that moves when the player strafes.
    /// </remarks>
    [Fact]
    public void DistanceIsZeroInsideACellAndPerpendicularOutsideIt() {
        var grid = Grid();

        Assert.Equal(0f, grid.DistanceTo(0, 0, new(50f, 0f, 50f)));
        Assert.Equal(0f, grid.DistanceTo(0, 0, new(1f, 0f, 99f)));

        // Straight out along one axis.
        Assert.Equal(50f, grid.DistanceTo(0, 0, new(150f, 0f, 50f)), 4);

        // Diagonally off a corner: a 3-4-5 triangle from (0,0)'s far corner at (100, 100).
        Assert.Equal(5f, grid.DistanceTo(0, 0, new(103f, 0f, 104f)), 4);
    }

    // --- The policy ---------------------------------------------------------

    [Fact]
    public void CellsInsideTheRadiusAreTouchedAndTheLeadRingIsOnlyRequested() {
        var grid = Grid();
        grid.Lead = 100f;

        var (residency, _) = Service();

        // At the middle of cell (5, 5) with a 10 m radius: only that cell is in use, and the eight
        // around it are within the 110 m reach.
        grid.Update([new(new(550f, 0f, 550f), 10f)], residency);

        Assert.Equal(1, grid.TouchedCells);
        Assert.Equal(8, grid.RequestedCells);
        Assert.Equal(9, residency.PendingRequests);

        residency.Dispose();
    }

    [Fact]
    public void ACellInUseIsRequestedAsWellAsTouchedSoItArrivesAtAll() {
        // Touch does nothing for a page that is not resident, so a policy that only touched the
        // in-use ring would wait for the lead ring to arrive and never ask for the middle.
        var grid = Grid();
        grid.Lead = 0f;

        var (residency, store) = Service();

        grid.Update([new(new(550f, 0f, 550f), 10f)], residency);
        Settle(residency);

        Assert.Contains(grid.KeyOf(5, 5), store.Loaded);

        residency.Dispose();
    }

    [Fact]
    public void OnlyTheCellsASourceCanReachAreVisited() {
        // A 10 × 10 grid with a 200 m radius plus a 100 m lead reaches three cells each way from the
        // one it stands in, not all a hundred — which is what keeps this affordable per frame.
        var grid = Grid();
        grid.Lead = 100f;

        var (residency, _) = Service();

        grid.Update([new(new(550f, 0f, 550f), 200f)], residency);

        Assert.True(grid.TouchedCells + grid.RequestedCells < 60, "the whole grid was visited.");
        Assert.True(grid.TouchedCells >= 9, "the near cells were missed.");

        residency.Dispose();
    }

    [Fact]
    public void CellsOffTheEdgeOfTheGridAreNotAskedFor() {
        var grid = Grid();
        grid.Lead = 500f;

        var (residency, _) = Service();

        // Standing in the corner with a reach that goes well outside the grid.
        grid.Update([new(new(10f, 0f, 10f), 400f)], residency);

        for (var i = 0; i < residency.PendingRequests; i++) {
            // Every requested index is a real cell of this grid.
            Assert.InRange(i, 0, grid.CellCount - 1);
        }

        Assert.True(grid.TouchedCells + grid.RequestedCells <= grid.CellCount);

        residency.Dispose();
    }

    [Fact]
    public void TwoSourcesUnionRatherThanDoubleCount() {
        var grid = Grid();
        grid.Lead = 0f;

        var (residency, _) = Service();

        // Both standing in the same cell.
        grid.Update([new(new(540f, 0f, 540f), 10f), new(new(560f, 0f, 560f), 10f)], residency);

        Assert.Equal(1, grid.TouchedCells);

        residency.Dispose();
    }

    [Fact]
    public void ACellInOneSourcesRadiusAndAnothersLeadIsInUse() {
        // The reason the two sets are resolved rather than filled independently: touched must win,
        // or a cell somebody is standing in gets treated as merely upcoming and is first to evict.
        var grid = Grid();
        grid.Lead = 200f;

        var (residency, _) = Service();

        grid.Update([
            new(new(550f, 0f, 550f), 10f),   // in cell (5, 5)
            new(new(250f, 0f, 550f), 10f)    // in cell (2, 5), whose lead covers (5, 5)
        ], residency);

        Assert.Equal(2, grid.TouchedCells);

        residency.Dispose();
    }

    [Fact]
    public void AZeroRadiusStillLoadsTheCellASourceStandsIn() {
        var grid = Grid();
        grid.Lead = 0f;

        var (residency, _) = Service();

        grid.Update([new(new(550f, 0f, 550f), 0f)], residency);

        Assert.Equal(1, grid.TouchedCells);

        residency.Dispose();
    }

    [Fact]
    public void NoSourcesWantsNothing() {
        var grid = Grid();
        var (residency, _) = Service();

        Assert.Equal(0, grid.Update([], residency));
        Assert.Equal(0, grid.RequestedCells);
        Assert.Equal(0, residency.PendingRequests);

        residency.Dispose();
    }

    // --- Eviction happens by not asking -------------------------------------

    /// <summary>
    ///     A cell that stops being wanted is not evicted, it is merely not refreshed.
    /// </summary>
    /// <remarks>
    ///     The whole reason this class never calls anything that removes a page. Evicting on the way
    ///     out would empty the pool whenever a source turned around and refill it on the way back;
    ///     letting <see cref="PageResidency" />'s LRU reclaim the room means a page survives right up
    ///     until something else needs it.
    /// </remarks>
    [Fact]
    public void MovingAwayLeavesTheOldCellsResidentUntilTheRoomIsNeeded() {
        var grid = Grid();
        grid.Lead = 0f;

        // Room for four pages only, so the pool has to make choices.
        var (residency, store) = Service(slots: 4);

        grid.Update([new(new(50f, 0f, 50f), 10f)], residency);
        Settle(residency);

        Assert.True(residency.IsResident(grid.KeyOf(0, 0)));
        Assert.Empty(store.Evicted);

        // Walk east one cell at a time. The first cell stays resident while there is room.
        grid.Update([new(new(150f, 0f, 50f), 10f)], residency);
        Settle(residency);

        Assert.True(residency.IsResident(grid.KeyOf(0, 0)));
        Assert.True(residency.IsResident(grid.KeyOf(1, 0)));
        Assert.Empty(store.Evicted);

        residency.Dispose();
    }

    [Fact]
    public void TheCellInUseIsTheLastOneEvicted() {
        // Touch is what makes this true, and getting it wrong is the failure PageResidency's own
        // remarks describe: a pool that thrashes hardest on what is closest to the camera.
        var grid = Grid();
        grid.Lead = 0f;

        var (residency, store) = Service(slots: 3);

        for (var cell = 0; cell < 8; cell++) {
            grid.Update([new(new((cell * 100f) + 50f, 0f, 50f), 10f)], residency);
            Settle(residency);

            Assert.True(
                residency.IsResident(grid.KeyOf(cell, 0)),
                $"cell {cell} was not resident on the frame it was being used."
            );
        }

        Assert.NotEmpty(store.Evicted);
        Assert.DoesNotContain(grid.KeyOf(7, 0), store.Evicted);

        residency.Dispose();
    }

    [Fact]
    public void ADefaultLeadIsOneCell() {
        var grid = Grid();
        var (residency, _) = Service();

        // Lead left at its default. A 10 m radius in the middle of a cell reaches the eight
        // neighbours and no further.
        grid.Update([new(new(550f, 0f, 550f), 10f)], residency);

        Assert.Equal(1, grid.TouchedCells);
        Assert.Equal(8, grid.RequestedCells);

        residency.Dispose();
    }

    [Fact]
    public void GridsOnOneResidencyServiceDoNotCollide() {
        // PageKey.Source is what keeps a terrain's heights, its weights and a mesh's clusters apart
        // in one pool — docs/plan/22's improvement 6, which this is the second customer of.
        var heights = new StreamingGrid(1, Vector2.Zero, 100f, 4, 4);
        var weights = new StreamingGrid(2, Vector2.Zero, 100f, 4, 4);

        var (residency, store) = Service();

        heights.Lead = 0f;
        weights.Lead = 0f;

        heights.Update([new(new(50f, 0f, 50f), 10f)], residency);
        weights.Update([new(new(50f, 0f, 50f), 10f)], residency);
        Settle(residency);

        Assert.Equal(2, residency.ResidentPages);
        Assert.Contains(new PageKey(1, 0), store.Loaded);
        Assert.Contains(new PageKey(2, 0), store.Loaded);

        residency.Dispose();
    }
}
