// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Foliage.Tests;

/// <summary>
///     The ring: which cells hold buffers, when they take one and when they give it back.
/// </summary>
public sealed class GrassResidencyTests {
    static GrassResidency Ring(int capacity = 64) => new(new(32f), capacity);

    [Fact]
    public void ACellUnderTheViewIsResidentAtAnyRange() {
        var ring = Ring();

        ring.Update(new(16f, 0f, 16f), 1f);

        Assert.True(ring.TryGetSlot(new(0, 0), out _));
    }

    /// <summary>Distance is to the cell, not to its centre.</summary>
    /// <remarks>
    ///     ⚠ <b>A 32 m cell whose near edge is under the camera has its centre 22 m away.</b> A
    ///     centre test with a 20 m range would leave the ground the camera is standing on bare, which
    ///     is the one place nobody would look for a range bug.
    /// </remarks>
    [Fact]
    public void TheCellTheViewStandsOnIsNeverMissed() {
        var ring = Ring();

        ring.Update(new(0.5f, 0f, 0.5f), 5f);

        Assert.True(ring.TryGetSlot(new(0, 0), out _));
        Assert.Equal(0f, ring.DistanceTo(new(0, 0), new(0.5f, 0.5f)));
    }

    [Fact]
    public void SlotsAreHandedBackWhenACellLeavesRange() {
        var ring = Ring();

        var arriving = ring.Update(new(0f, 0f, 0f), 40f);

        Assert.NotEmpty(arriving.Created);
        Assert.Empty(arriving.Evicted);

        var count = ring.Count;
        var leaving = ring.Update(new(1000f, 0f, 1000f), 40f);

        Assert.Equal(count, leaving.Evicted.Count);
        Assert.DoesNotContain(leaving.Evicted, gone => ring.TryGetSlot(gone.Cell, out _));
    }

    /// <summary>Standing still changes nothing, and standing on a boundary changes nothing either.</summary>
    /// <remarks>
    ///     [docs/plan/31 § T6]'s second exit criterion, in its cheapest form. A cell created and
    ///     evicted at the same distance re-scatters every frame while a camera stands on the boundary
    ///     — the whole cost of the feature paid for nothing, and a visible flicker where the two
    ///     verdicts alternate.
    /// </remarks>
    [Fact]
    public void StandingOnTheBoundaryDoesNotThrash() {
        var ring = Ring();

        // Cell (2, 0) begins exactly 64 m out, so a 64 m range puts the camera on its boundary: a
        // centimetre one way and it is wanted, a centimetre the other and it is not.
        ring.Update(new(0f, 0f, 0f), 64f);

        var settled = 0;

        for (var frame = 0; frame < 8; frame++) {
            var wobble = (frame % 2) == 0 ? 0.01f : -0.01f;
            var change = ring.Update(new(wobble, 0f, 0f), 64f);

            // Nothing may ever leave. A cell that is out of *range* is still inside the eviction
            // range, which is the whole of what the hysteresis buys: a camera drifting across a
            // boundary keeps what it has rather than dropping and re-scattering it every frame.
            Assert.True(
                change.Evicted.Count == 0,
                $"frame {frame} evicted {change.Evicted.Count} cells for a camera that moved a "
                + "centimetre; the hysteresis is not holding."
            );

            // The first two frames legitimately reach a cell whose near edge has just come inside
            // the range. After that the wanted set is covered and nothing more may arrive.
            if (change.Created.Count == 0) {
                settled++;
            } else {
                Assert.True(
                    frame < 2,
                    $"frame {frame} created {change.Created.Count} cells the frames before it "
                    + "should already have covered."
                );
            }
        }

        Assert.True(settled >= 6, $"only {settled} of eight frames were quiet.");
    }

    /// <summary>A camera that swung round gets the cells in front of it, not the ones behind.</summary>
    /// <remarks>
    ///     ⚠ <b>Every resident cell can be inside the eviction range and still be the wrong set.</b>
    ///     Without reclaiming the furthest, a full ring keeps what is behind the camera until it
    ///     drifts past the hysteresis — which is grass arriving seconds after the view did.
    /// </remarks>
    [Fact]
    public void AFullRingGivesItsSlotsToTheNearerCells() {
        var ring = Ring(9);

        ring.Update(new(0f, 0f, 0f), 96f);
        Assert.Equal(9, ring.Count);

        // Two cells along, still comfortably inside the old eviction range.
        ring.Update(new(64f, 0f, 0f), 96f);

        Assert.True(ring.TryGetSlot(new(2, 0), out _), "the cell the camera moved onto has no slot.");
        Assert.Equal(9, ring.Count);
    }

    /// <summary>A ring too small for its range loses the horizon, never the foreground.</summary>
    [Fact]
    public void ARingThatRunsOutDropsTheFarCells() {
        var ring = Ring(4);

        ring.Update(new(16f, 0f, 16f), 96f);

        Assert.Equal(4, ring.Count);
        Assert.True(ring.Refused > 0, "a four-slot ring covered a 96 m range and said nothing.");
        Assert.True(ring.TryGetSlot(new(0, 0), out _), "the cell under the camera was not kept.");
    }

    /// <summary>And having lost the horizon, it stops: a refusal that repeats is not a refusal.</summary>
    /// <remarks>
    ///     ⚠ <b>The property an LRU does not have, and the reason this ring is not one.</b> A service
    ///     that evicted a resident cell to make room for a further one would hand every slot to a
    ///     different cell every frame while the camera stood still — a full re-scatter of the ring,
    ///     thousands of surface probes, for a set that ends up the same size and further away. Nine
    ///     slots over a range that wants forty-one cells is that case, and standing still is the test.
    /// </remarks>
    [Fact]
    public void ARingThatRanOutStandsStill() {
        var ring = Ring(9);

        ring.Update(new(200f, 0f, 200f), 96f);

        var chosen = ring.Resident.OrderBy(entry => entry.Slot).ToArray();

        Assert.Equal(9, ring.Count);
        Assert.True(ring.Refused > 0, "a nine-slot ring covered a 96 m range and said nothing.");

        for (var frame = 0; frame < 8; frame++) {
            var change = ring.Update(new(200f, 0f, 200f), 96f);

            Assert.True(
                change.IsEmpty,
                $"frame {frame} took {change.Created.Count} cells in and put {change.Evicted.Count} "
                + "out for a camera that did not move; the refusal is churning rather than refusing."
            );

            Assert.True(ring.Refused > 0, $"frame {frame} refused nothing and still holds nine slots.");
        }

        Assert.Equal(chosen, ring.Resident.OrderBy(entry => entry.Slot).ToArray());
    }

    [Fact]
    public void ClearingReturnsEverySlot() {
        var ring = Ring(16);

        ring.Update(new(0f, 0f, 0f), 40f);
        ring.Clear();

        Assert.Equal(0, ring.Count);

        var again = ring.Update(new(0f, 0f, 0f), 40f);

        Assert.Equal(ring.Count, again.Created.Count);
        Assert.True(ring.Count <= ring.Capacity);
    }

    [Fact]
    public void ASlotIsNeverHeldByTwoCells() {
        var ring = Ring(32);

        for (var step = 0; step < 12; step++) {
            ring.Update(new(step * 24f, 0f, step * 7f), 48f);

            var slots = ring.Resident.Select(entry => entry.Slot).ToArray();

            Assert.Equal(slots.Length, slots.Distinct().Count());
            Assert.All(slots, slot => Assert.InRange(slot, 0, ring.Capacity - 1));
        }
    }
}
