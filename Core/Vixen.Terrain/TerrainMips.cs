// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Terrain;

/// <summary>
///     The reduced copies of a tile's heights that a coarse patch reads.
/// </summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § T1]'s last owed item, and [§ T2] is what wants it.</b> A CDLOD patch at
///         level three samples every eighth texel of the heightmap; without a mip chain it reads
///         level 0 and gets a height nothing between the samples ever had, which is the coarse patch
///         shimmering as the camera moves.
///     </para>
///     <para>
///         ⚠ <b>Reduced by the <em>maximum</em>, not the average, and this is the decision the whole
///         file is about.</b> An averaged mip sinks a ridge: four samples of which one is a peak
///         average to a quarter of it, so a mountain gets shorter every level and the silhouette a
///         distant patch draws is not the mountain's. A maximum keeps the ridge and raises the
///         valleys, which errs towards geometry being <em>above</em> where it should be — the
///         direction that hides a crack rather than opening one, and the direction the collision
///         approximation is already conservative in.
///     </para>
///     <para>
///         ⚠ <b>Per tile, not over the whole grid, because a tile is what a texture is.</b> Reducing
///         across a tile boundary would mix two textures' texels into one, which is [§ D2]'s seam
///         arriving through the mip chain — and the shared boundary sample belongs to both tiles, so
///         each reduces its own copy of it and the two agree by construction.
///     </para>
///     <para>
///         ⚠ <b>A tile's sample count is a power of two <em>plus one</em>, so a level is not half
///         its parent.</b> A 129-sample tile reduces to 65, then 33 — <c>(n + 1) / 2</c> — which is
///         what keeps the boundary sample on the boundary at every level. Halving the count instead
///         drops the last row, and the seam it opens is one texel wide and permanent.
///     </para>
/// </remarks>
public static class TerrainMips {
    /// <summary>The smallest level that is still a grid of quads.</summary>
    public const int MinimumSamples = 2;

    /// <summary>How many samples a level of a tile is, on a side.</summary>
    /// <param name="tileSamples">How many the tile is at level 0.</param>
    /// <param name="level">Which level, 0 being the full resolution.</param>
    /// <returns>The count, never below <see cref="MinimumSamples" />.</returns>
    /// <remarks>
    ///     <c>(n + 1) / 2</c> per level rather than <c>n / 2</c> — see the class remarks. For 129
    ///     samples that is 129, 65, 33, 17, 9, 5, 3, 2.
    /// </remarks>
    public static int SamplesAt(int tileSamples, int level) {
        ArgumentOutOfRangeException.ThrowIfNegative(level);

        var samples = tileSamples;

        for (var step = 0; step < level && samples > MinimumSamples; step++) {
            samples = ((samples + 1) / 2) is var next && next >= MinimumSamples ? next : MinimumSamples;
        }

        return Math.Max(samples, MinimumSamples);
    }

    /// <summary>How many levels a tile of this size has, level 0 included.</summary>
    /// <param name="tileSamples">How many samples the tile is, on a side.</param>
    /// <returns>The count, at least one.</returns>
    public static int LevelCount(int tileSamples) {
        var levels = 1;

        for (var samples = tileSamples; samples > MinimumSamples; levels++) {
            samples = (samples + 1) / 2;
        }

        return levels;
    }

    /// <summary>How many samples every level of a tile occupies together.</summary>
    /// <param name="tileSamples">How many samples the tile is, on a side.</param>
    /// <returns>The total.</returns>
    public static long ChainSamples(int tileSamples) {
        var total = 0L;

        for (var level = 0; level < LevelCount(tileSamples); level++) {
            var samples = SamplesAt(tileSamples, level);

            total += (long)samples * samples;
        }

        return total;
    }

    /// <summary>Builds every level of one tile's height mip chain.</summary>
    /// <param name="terrain">The terrain, composited.</param>
    /// <param name="tileX">Which tile, along X.</param>
    /// <param name="tileZ">And along Z.</param>
    /// <param name="destination">
    ///     Where the chain goes, level 0 first and each level packed immediately after the one above.
    ///     At least <see cref="ChainSamples" /> long.
    /// </param>
    /// <returns>How many samples were written.</returns>
    /// <exception cref="ArgumentNullException">There is no terrain.</exception>
    /// <exception cref="ArgumentException">There is not room for the chain.</exception>
    /// <remarks>
    ///     ⚠ <b>Level 0 is read from <see cref="Terrain.Composite" />, the cache, and the caller is
    ///     expected to have resolved it.</b> Building a mip chain is what happens after a stroke, in
    ///     the frame that uploads the tile — reading the definition instead would walk the layer stack
    ///     once per sample per level, which for a 129-sample tile is a hundred and fifty thousand
    ///     walks to answer a question the cache already holds.
    /// </remarks>
    public static long Build(Terrain terrain, int tileX, int tileZ, Span<ushort> destination) {
        ArgumentNullException.ThrowIfNull(terrain);

        var description = terrain.Description;
        var required = ChainSamples(description.TileSamples);

        if (destination.Length < required) {
            throw new ArgumentException(
                $"A {description.TileSamples}-sample tile's chain is {required} samples and "
                + $"{destination.Length} were given.",
                nameof(destination)
            );
        }

        var rect = description.SamplesOf(tileX, tileZ);
        var size = description.TileSamples;
        var at = 0;

        for (var z = 0; z < size; z++) {
            for (var x = 0; x < size; x++) {
                destination[at++] = terrain.Composite[rect.X + x, rect.Z + z];
            }
        }

        var levels = LevelCount(size);
        var parentAt = 0;
        var parentSize = size;

        for (var level = 1; level < levels; level++) {
            var childSize = SamplesAt(size, level);
            var childAt = at;

            Reduce(destination, parentAt, parentSize, childAt, childSize);

            at += childSize * childSize;
            parentAt = childAt;
            parentSize = childSize;
        }

        return at;
    }

    /// <summary>Reduces one level onto the next, by maximum over the samples it covers.</summary>
    /// <remarks>
    ///     ⚠ <b>The window is clamped rather than assumed to be two by two.</b> A level of an odd
    ///     size has a last row whose parent is one sample rather than two, and reading past it would
    ///     take the first sample of the next row — which puts the far edge of a tile into its near
    ///     one, and the result is a heightfield that wraps.
    /// </remarks>
    static void Reduce(Span<ushort> chain, int parentAt, int parentSize, int childAt, int childSize) {
        for (var z = 0; z < childSize; z++) {
            for (var x = 0; x < childSize; x++) {
                var x0 = Math.Min(x * 2, parentSize - 1);
                var z0 = Math.Min(z * 2, parentSize - 1);
                var x1 = Math.Min(x0 + 1, parentSize - 1);
                var z1 = Math.Min(z0 + 1, parentSize - 1);

                var highest = Math.Max(
                    Math.Max(chain[parentAt + (z0 * parentSize) + x0], chain[parentAt + (z0 * parentSize) + x1]),
                    Math.Max(chain[parentAt + (z1 * parentSize) + x0], chain[parentAt + (z1 * parentSize) + x1])
                );

                chain[childAt + (z * childSize) + x] = highest;
            }
        }
    }
}
