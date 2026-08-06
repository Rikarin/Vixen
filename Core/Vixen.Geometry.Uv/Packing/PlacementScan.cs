// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Threading;

namespace Vixen.Geometry.Uv.Packing;

/// <summary>Where one unit goes: every column, every orientation, scored and reduced to one answer.</summary>
/// <remarks>
///     <para>
///         <b>The scan is a partition and the answer is a merge, which is why a thread count cannot
///         reach it.</b> Each chunk keeps the best <see cref="Keep" /> spots it saw, and the best
///         <c>Keep</c> of a partition's per-part bests <i>is</i> the global best <c>Keep</c> — an
///         identity, not an approximation. So the number of chunks is free to change, the number of
///         workers is free to change, and the batch size is free to change, and none of them moves a
///         single placement. docs/plan/42 § D12.
///     </para>
///     <para>
///         ⚠ <b>The early exit prunes work and never an answer.</b> A column loop that has already
///         pushed the unit above the chunk's worst kept level cannot produce a spot that survives, so
///         it stops — which is a statement about the loop and not about the result.
///     </para>
///     <para>
///         ⚠ <b>Why more than one spot is kept.</b> The skyline hands back a row that is always valid
///         and is never below an overhang. Reclaiming a cave means testing the bitmap, testing the
///         bitmap at every column is the cost that makes irregular packers unusable, and testing it at
///         the sixteen most promising columns is neither. The tail keeps the same shape at a fraction
///         of the columns — docs/plan/42 § D7.
///     </para>
/// </remarks>
sealed class PlacementScan {
    /// <summary>How many spots each chunk carries out of its slice.</summary>
    public const int Keep = 16;

    /// <summary>How many slices the candidate space is cut into. Fixed, and deliberately not a worker count.</summary>
    public const int Chunks = 32;

    /// <summary>Masks at or below this area get the bitmap descent. Larger ones take the skyline's answer.</summary>
    const int DescentArea = 8192;

    /// <summary>How far below the skyline the descent looks.</summary>
    const int DescentRows = 192;

    readonly int[] counts = new int[4];
    readonly int[] offsets = new int[4];
    readonly int[] starts = new int[4];
    readonly Spot[] kept = new Spot[Chunks * Keep];

    AtlasGrid grid = null!;
    IslandMask[] masks = null!;
    int[]? columns;
    int rotations;
    int total;

    /// <summary>Finds the best spot for a unit, or none.</summary>
    /// <param name="atlas">The tile to place into.</param>
    /// <param name="orientations">The unit's mask, one per quarter turn.</param>
    /// <param name="turns">How many of them may be tried.</param>
    /// <param name="candidates">The columns to try, ascending, or <c>null</c> for every column.</param>
    /// <param name="scheduler">The scheduler, or <c>null</c> to scan on the calling thread.</param>
    /// <param name="batch">How many slices one work item covers, or zero to let the scheduler choose.</param>
    /// <returns>The spot, or <see cref="Spot.None" />.</returns>
    public Spot Best(
        AtlasGrid atlas,
        IslandMask[] orientations,
        int turns,
        int[]? candidates,
        JobScheduler? scheduler,
        int batch
    ) {
        grid = atlas;
        masks = orientations;
        columns = candidates;
        rotations = turns;
        total = 0;

        for (var rotation = 0; rotation < turns; rotation++) {
            var mask = orientations[rotation];
            var limit = atlas.Resolution - atlas.Margin;
            var last = limit - mask.Width;
            var count = 0;

            if (mask.Height <= limit - atlas.Margin && last >= atlas.Margin) {
                if (candidates is null) {
                    count = last - atlas.Margin + 1;
                    starts[rotation] = atlas.Margin;
                } else {
                    while (count < candidates.Length && candidates[count] <= last) {
                        count++;
                    }

                    starts[rotation] = 0;
                }
            }

            counts[rotation] = count;
            offsets[rotation] = total;
            total += count;
        }

        if (total == 0) {
            return Spot.None;
        }

        Array.Fill(kept, Spot.None);

        if (scheduler is null || total < Chunks * 8) {
            for (var chunk = 0; chunk < Chunks; chunk++) {
                Slice(chunk);
            }
        } else {
            scheduler.ParallelFor(new Job(this), Chunks, batch);
        }

        Span<Spot> best = stackalloc Spot[Keep + Chunks];

        best.Fill(Spot.None);

        var merged = best[..Keep];

        foreach (var spot in kept) {
            Insert(merged, spot);
        }

        // ⚠ The merged best are all in the same neighbourhood, because they are the same comparison
        // applied to the whole atlas — and a cave three thousand columns away never appears in it. The
        // per-slice bests are spread across the sheet by construction, so probing them as well is what
        // lets the descent reach a hole under an overhang somewhere else entirely.
        for (var chunk = 0; chunk < Chunks; chunk++) {
            best[Keep + chunk] = kept[chunk * Keep];
        }

        return Descend(best);
    }

    void Slice(int chunk) {
        var lo = (int)((long)total * chunk / Chunks);
        var hi = (int)((long)total * (chunk + 1) / Chunks);

        if (lo >= hi) {
            return;
        }

        var buffer = kept.AsSpan(chunk * Keep, Keep);
        var skyline = grid.Skyline;
        var floor = grid.Margin;
        var ceiling = grid.Resolution - grid.Margin;
        var rotation = 0;

        while (rotation + 1 < rotations && offsets[rotation + 1] <= lo) {
            rotation++;
        }

        for (var index = lo; index < hi; index++) {
            while (rotation + 1 < rotations && index >= offsets[rotation + 1]) {
                rotation++;
            }

            if (index >= offsets[rotation] + counts[rotation]) {
                continue;
            }

            var local = index - offsets[rotation];
            var x = columns is null ? starts[rotation] + local : columns[local];
            var mask = masks[rotation];
            var bottom = mask.Bottom;
            var height = mask.Height;
            var worst = buffer[Keep - 1];
            var cap = worst.Exists ? worst.Level : int.MaxValue;
            var y = floor;
            var abandoned = false;

            for (var column = 0; column < bottom.Length; column++) {
                var lowest = bottom[column];

                if (lowest < 0) {
                    continue;
                }

                var need = skyline[x + column] - lowest;

                if (need > y) {
                    y = need;

                    if (y + height > cap) {
                        abandoned = true;

                        break;
                    }
                }
            }

            if (abandoned || y + height > ceiling) {
                continue;
            }

            var waste = 0;

            for (var column = 0; column < bottom.Length; column++) {
                var lowest = bottom[column];

                if (lowest >= 0) {
                    waste += y + lowest - skyline[x + column];
                }
            }

            Insert(buffer, new(y + height, waste, x, y, rotation));
        }
    }

    Spot Descend(Span<Spot> best) {
        var winner = Spot.None;

        foreach (var spot in best) {
            if (!spot.Exists) {
                continue;
            }

            var mask = masks[spot.Rotation];
            var candidate = spot;

            if (mask.Area <= DescentArea) {
                var lowered = grid.LowestFree(mask, spot.X, spot.Y, DescentRows);

                if (lowered != spot.Y) {
                    candidate = new(lowered + mask.Height, grid.Waste(mask, spot.X, lowered), spot.X, lowered, spot.Rotation);
                }
            }

            if (candidate.Beats(winner)) {
                winner = candidate;
            }
        }

        return winner;
    }

    static void Insert(Span<Spot> buffer, in Spot spot) {
        if (!spot.Exists || !spot.Beats(buffer[^1])) {
            return;
        }

        var slot = buffer.Length - 1;

        while (slot > 0 && spot.Beats(buffer[slot - 1])) {
            buffer[slot] = buffer[slot - 1];
            slot--;
        }

        buffer[slot] = spot;
    }

    readonly struct Job(PlacementScan scan) : IJobParallelFor {
        public void Execute(int index) => scan.Slice(index);
    }
}
