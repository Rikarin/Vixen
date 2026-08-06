// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Numerics;

namespace Vixen.Geometry.Uv.Packing;

/// <summary>One atlas tile as an occupancy bitmap, with the skyline of it kept beside it.</summary>
/// <remarks>
///     <para>
///         The bitmap holds <i>grown</i> shapes and every test is against a <i>raw</i> one. That is
///         the whole of the margin rule (docs/plan/42 § D8): the empty gap between any two placed
///         islands, and between any island and the tile's edge, is exactly <c>Margin</c> texels —
///         never half of it, never twice it.
///     </para>
///     <para>
///         ⚠ <b>The skyline is an accelerator and not the model.</b> It records one past the highest
///         occupied row per column, so anything at or above it is free and a placement derived from it
///         is valid without a bitmap test at all. What it cannot see is a cave under an overhang,
///         which is what <see cref="LowestFree" /> is for — and why the bitmap is kept even though the
///         skyline answers most placements.
///     </para>
/// </remarks>
sealed class AtlasGrid {
    const int WordShift = 6;

    readonly ulong[] words;
    readonly int stride;

    /// <summary>Opens an empty tile.</summary>
    /// <param name="resolution">The tile's edge length in texels.</param>
    /// <param name="margin">How many texels of empty space every island keeps around it.</param>
    public AtlasGrid(int resolution, int margin) {
        Resolution = resolution;
        Margin = margin;
        stride = IslandMask.StrideOf(resolution);
        words = new ulong[stride * resolution];
        Skyline = new int[resolution];
        Array.Fill(Skyline, margin);
    }

    /// <summary>The tile's edge length in texels.</summary>
    public int Resolution { get; }

    /// <summary>The margin, in texels.</summary>
    public int Margin { get; }

    /// <summary>One past the highest occupied row per column, floored at <see cref="Margin" />.</summary>
    public int[] Skyline { get; }

    /// <summary>How many islands have been committed here.</summary>
    public int Count { get; private set; }

    /// <summary>How many texels of the tile are spoken for — island and margin together.</summary>
    /// <remarks>
    ///     This is <see cref="UvReport.EffectiveEfficiency" />'s numerator, and counting the bitmap
    ///     rather than summing the grown areas is what keeps two neighbours' overlapping margin bands
    ///     from being charged twice.
    /// </remarks>
    public long Consumed {
        get {
            var total = 0L;

            foreach (var word in words) {
                total += BitOperations.PopCount(word);
            }

            return total;
        }
    }

    /// <summary>The lowest row a raw shape may sit at in the given column, per the skyline.</summary>
    /// <param name="mask">The shape.</param>
    /// <param name="x">Where its left edge goes.</param>
    /// <returns>The row, or <see cref="int.MaxValue" /> when it cannot go there at all.</returns>
    /// <remarks>
    ///     ⚠ Every column of the shape is lifted until its <i>own</i> lowest texel clears that
    ///     column's skyline, so a concave underside settles onto a bump instead of resting a bounding
    ///     box on top of it. That single difference is most of what separates the irregular rung from
    ///     the rectangle one.
    /// </remarks>
    public int Rest(IslandMask mask, int x) {
        var y = Margin;
        var bottom = mask.Bottom;

        for (var column = 0; column < bottom.Length; column++) {
            var lowest = bottom[column];

            if (lowest < 0) {
                continue;
            }

            var need = Skyline[x + column] - lowest;

            if (need > y) {
                y = need;
            }
        }

        return y;
    }

    /// <summary>How many texels are stranded under the shape when it rests at a row.</summary>
    /// <param name="mask">The shape.</param>
    /// <param name="x">Where its left edge goes.</param>
    /// <param name="y">Where its bottom edge goes.</param>
    /// <returns>The count.</returns>
    public int Waste(IslandMask mask, int x, int y) {
        var waste = 0;
        var bottom = mask.Bottom;

        for (var column = 0; column < bottom.Length; column++) {
            var lowest = bottom[column];

            if (lowest >= 0) {
                waste += y + lowest - Skyline[x + column];
            }
        }

        return waste;
    }

    /// <summary>Whether a raw shape placed here touches anything already committed.</summary>
    /// <param name="mask">The shape.</param>
    /// <param name="x">Where its left edge goes.</param>
    /// <param name="y">Where its bottom edge goes.</param>
    /// <returns><c>true</c> when nothing is in the way.</returns>
    public bool Fits(IslandMask mask, int x, int y) {
        if (x < Margin || y < Margin || x + mask.Width > Resolution - Margin
            || y + mask.Height > Resolution - Margin) {
            return false;
        }

        var raw = mask.Raw;
        var maskStride = mask.RawStride;
        var shift = x & 63;
        var baseWord = x >> WordShift;

        for (var row = 0; row < mask.Height; row++) {
            var source = row * maskStride;
            var target = ((y + row) * stride) + baseWord;

            for (var word = 0; word < maskStride; word++) {
                var bits = raw[source + word];

                if (bits == 0) {
                    continue;
                }

                if ((words[target + word] & (bits << shift)) != 0) {
                    return false;
                }

                if (shift != 0 && (words[target + word + 1] & (bits >> (64 - shift))) != 0) {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>The lowest row at or below a starting one where the shape fits, searching a bounded window.</summary>
    /// <param name="mask">The shape.</param>
    /// <param name="x">Where its left edge goes.</param>
    /// <param name="y">The skyline's answer, which is always valid.</param>
    /// <param name="descent">How far below it to look.</param>
    /// <returns>The lowest row found.</returns>
    /// <remarks>
    ///     ⚠ <b>Bounded, and the bound is the honesty in this rung.</b> An unbounded descent is a full
    ///     bitmap test at every row of the atlas for every candidate column, which is the cost that
    ///     makes irregular packers unusable at the island counts they are bought for. The window is
    ///     spent where it pays — on the small islands, which are the ones a cave can actually hold.
    /// </remarks>
    public int LowestFree(IslandMask mask, int x, int y, int descent) {
        var floor = Math.Max(Margin, y - descent);

        for (var candidate = floor; candidate < y; candidate++) {
            if (Fits(mask, x, candidate)) {
                return candidate;
            }
        }

        return y;
    }

    /// <summary>Writes the grown shape in and lifts the skyline over it.</summary>
    /// <param name="mask">The shape.</param>
    /// <param name="x">Where its raw left edge goes.</param>
    /// <param name="y">Where its raw bottom edge goes.</param>
    public void Commit(IslandMask mask, int x, int y) {
        var grown = mask.Grown;
        var grownStride = mask.GrownStride;
        var originX = x - Margin;
        var originY = y - Margin;
        var shift = originX & 63;
        var baseWord = originX >> WordShift;

        for (var row = 0; row < mask.GrownHeight; row++) {
            var target = originY + row;

            if (target < 0 || target >= Resolution) {
                continue;
            }

            var source = row * grownStride;
            var destination = (target * stride) + baseWord;

            for (var word = 0; word < grownStride; word++) {
                var bits = grown[source + word];

                if (bits == 0) {
                    continue;
                }

                words[destination + word] |= bits << shift;

                if (shift != 0) {
                    words[destination + word + 1] |= bits >> (64 - shift);
                }
            }
        }

        var top = mask.GrownTop;

        for (var column = 0; column < top.Length; column++) {
            if (top[column] == 0) {
                continue;
            }

            var atlasColumn = originX + column;

            if (atlasColumn < 0 || atlasColumn >= Resolution) {
                continue;
            }

            var lifted = originY + top[column];

            if (lifted > Skyline[atlasColumn]) {
                Skyline[atlasColumn] = lifted;
            }
        }

        Count++;
    }

    /// <summary>The columns where the skyline changes, which is where a cheap placement is worth trying.</summary>
    /// <returns>The breakpoints, ascending, always including the left margin.</returns>
    /// <remarks>
    ///     docs/plan/42 § D7's tail. Scanning every column is what makes the core expensive; a tiny
    ///     island only ever wants to sit against a step, so the tail scans the steps. The count of them
    ///     is bounded by the islands already placed rather than by the resolution.
    /// </remarks>
    public int[] Breakpoints() {
        var breaks = new List<int>(Math.Min(Resolution, (Count * 2) + 2)) { Margin };

        for (var column = Margin + 1; column < Resolution - Margin; column++) {
            if (Skyline[column] != Skyline[column - 1]) {
                breaks.Add(column);
            }
        }

        return [.. breaks];
    }
}
