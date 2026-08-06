// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>Islands of the shape a generated mesh actually produces, made without a generated mesh.</summary>
/// <remarks>
///     <para>
///         The realistic case for a packer is not fifty tidy rectangles. A TRELLIS-style atlas over a
///         twenty-five-thousand-triangle mesh comes out at a few hundred islands — measured at 422 on
///         one — distributed heavily towards the small, with irregular and frequently non-convex
///         boundaries. Everything interesting about a packer is a statement about that distribution:
///         a handful of large islands set the atlas's shape and the long tail has to find the gaps
///         between them.
///     </para>
///     <para>
///         ⚠ <b>The generator is a fixed sequence and not a random one.</b> docs/plan/42 § B6 — a test
///         that measures efficiency against a moving corpus cannot tell a regression from a reseed.
///     </para>
/// </remarks>
static class IslandCorpus {
    /// <summary>Builds a corpus of star-shaped, notched, mostly-small islands.</summary>
    /// <param name="count">How many.</param>
    /// <param name="seed">The sequence to walk.</param>
    /// <param name="scale">What every island reports as its coordinates-per-world-unit.</param>
    /// <returns>The islands.</returns>
    public static UvIsland[] Trellis(int count, uint seed = 0xA5F3u, float scale = 1f) {
        var state = seed;
        var islands = new UvIsland[count];

        for (var index = 0; index < count; index++) {
            var corners = 8 + (int)(Next(ref state) % 13u);

            // A fourth power turns a flat draw into the distribution a real atlas has: a few islands
            // that own most of the area, and a great many that have to be swept into what is left.
            var spread = Unit(ref state);
            var radius = 0.02f + (0.34f * spread * spread * spread * spread);
            var points = new Vector2[corners];

            for (var corner = 0; corner < corners; corner++) {
                var angle = MathF.Tau * corner / corners;

                // Every third corner cuts inward, which is what makes the island non-convex — and a
                // non-convex island is the entire reason the irregular rung exists.
                var reach = corner % 3 == 2
                    ? radius * (0.28f + (0.18f * Unit(ref state)))
                    : radius * (0.62f + (0.75f * Unit(ref state)));

                points[corner] = new(reach * MathF.Cos(angle), reach * MathF.Sin(angle));
            }

            var coordinates = new List<Vector2>(corners * 3);
            var indices = new List<int>(corners * 3);
            var minimum = Vector2.Zero;
            var maximum = Vector2.Zero;

            for (var corner = 0; corner < corners; corner++) {
                var a = points[corner];
                var b = points[(corner + 1) % corners];

                coordinates.Add(Vector2.Zero);
                coordinates.Add(a);
                coordinates.Add(b);

                indices.Add(corner * 3);
                indices.Add((corner * 3) + 1);
                indices.Add((corner * 3) + 2);

                minimum = new(MathF.Min(minimum.X, MathF.Min(a.X, b.X)), MathF.Min(minimum.Y, MathF.Min(a.Y, b.Y)));
                maximum = new(MathF.Max(maximum.X, MathF.Max(a.X, b.X)), MathF.Max(maximum.Y, MathF.Max(a.Y, b.Y)));
            }

            islands[index] = new(coordinates, indices, minimum, maximum, scale);
        }

        return islands;
    }

    /// <summary>An axis-aligned square whose corners are exact binary fractions.</summary>
    /// <param name="side">Its edge length.</param>
    /// <param name="scale">Its coordinates per world unit.</param>
    /// <returns>The island.</returns>
    /// <remarks>
    ///     ⚠ Exact coordinates, so that a measured texel gap is a statement about the packer and not
    ///     about where a rasterizer rounded. The margin tests need that; the corpus above does not.
    /// </remarks>
    public static UvIsland Square(float side, float scale = 1f) {
        var a = Vector2.Zero;
        var b = new Vector2(side, 0f);
        var c = new Vector2(side, side);
        var d = new Vector2(0f, side);

        return new([a, b, c, a, c, d], [0, 1, 2, 3, 4, 5], a, c, scale);
    }

    /// <summary>Shuffles a list with a fixed sequence, so "a different order" is a reproducible one.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="items">What to shuffle.</param>
    /// <param name="seed">The sequence.</param>
    /// <returns>A new list, permuted, paired with where each element came from.</returns>
    public static (T[] Items, int[] Origin) Shuffle<T>(IReadOnlyList<T> items, uint seed) {
        var state = seed;
        var order = new int[items.Count];

        for (var index = 0; index < order.Length; index++) {
            order[index] = index;
        }

        for (var index = order.Length - 1; index > 0; index--) {
            var swap = (int)(Next(ref state) % (uint)(index + 1));

            (order[index], order[swap]) = (order[swap], order[index]);
        }

        var shuffled = new T[items.Count];

        for (var index = 0; index < order.Length; index++) {
            shuffled[index] = items[order[index]];
        }

        return (shuffled, order);
    }

    static uint Next(ref uint state) => state = (state * 1664525u) + 1013904223u;

    static float Unit(ref uint state) => (Next(ref state) >> 8) / 16777216f;
}
