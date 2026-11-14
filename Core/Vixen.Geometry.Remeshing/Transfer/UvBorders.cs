// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Geometry.Remeshing;

/// <summary>How far each texel of an atlas is from the border of its UV island.</summary>
/// <remarks>
///     <para>
///         <b>A border texel is one beside a texel of another island, of no island, or the atlas's edge</b>, and it reads
///         zero. Every other texel of an island reads its distance to the nearest border texel, so the nearest is always
///         its own island's: another island's border lies past its own.
///     </para>
///     <para>
///         ⚠ <b>Exact, and in UV.</b> Two passes of the lower envelope of parabolas (Felzenszwalb and Huttenlocher),
///         columns then rows, give the Euclidean distance with no chamfer's octagons, and a step across is a share of
///         the atlas's width and a step down a share of its height, so a non-square atlas measures in the same unit
///         square its coordinates are in.
///     </para>
/// </remarks>
static class UvBorders {
    /// <summary>The squared distance a texel with no border anywhere in its line starts at.</summary>
    /// <remarks>Finite, so two of them subtract to nothing rather than to a NaN.</remarks>
    const double Far = 1e20;

    /// <summary>The distance of every texel to its island's border, over a band, one at the band's width and past it.</summary>
    /// <param name="islands">The island per texel, <c>-1</c> for none, row-major.</param>
    /// <param name="columns">The atlas's width in texels.</param>
    /// <param name="rows">Its height in texels.</param>
    /// <param name="band">The distance that reads one, as a share of the atlas.</param>
    /// <param name="options">How many threads the passes may use.</param>
    /// <returns>Zero on a border and off every island, rising to one at <paramref name="band" /> inside.</returns>
    public static float[] Of(int[] islands, int columns, int rows, float band, ParallelOptions options) {
        var squared = new double[islands.Length];

        for (var index = 0; index < islands.Length; index++) {
            squared[index] = Border(islands, columns, rows, index) ? 0d : Far;
        }

        var across = 1d / columns;
        var down = 1d / rows;

        Parallel.For(0, columns, options, x => Line(squared, x, columns, rows, down));
        Parallel.For(0, rows, options, y => Line(squared, y * columns, 1, columns, across));

        var values = new float[islands.Length];

        for (var index = 0; index < values.Length; index++) {
            values[index] = islands[index] < 0 ? 0f : MathF.Min(1f, (float)Math.Sqrt(squared[index]) / band);
        }

        return values;
    }

    /// <summary>Whether a texel is off every island, or beside a texel of another one or the atlas's edge.</summary>
    static bool Border(int[] islands, int columns, int rows, int index) {
        var island = islands[index];

        if (island < 0) {
            return true;
        }

        var x = index % columns;
        var y = index / columns;

        return x == 0
            || y == 0
            || x == columns - 1
            || y == rows - 1
            || islands[index - 1] != island
            || islands[index + 1] != island
            || islands[index - columns] != island
            || islands[index + columns] != island;
    }

    /// <summary>One line of the transform, in place: each value becomes the least of every other's plus its distance squared.</summary>
    /// <param name="squared">The squared distances.</param>
    /// <param name="first">The line's first index.</param>
    /// <param name="stride">The step from one of its texels to the next.</param>
    /// <param name="count">How many texels it has.</param>
    /// <param name="spacing">How far apart two neighbours are.</param>
    static void Line(double[] squared, int first, int stride, int count, double spacing) {
        var values = new double[count];
        var parabolas = new int[count];
        var bounds = new double[count + 1];

        for (var at = 0; at < count; at++) {
            values[at] = squared[first + at * stride];
        }

        var top = 0;
        bounds[0] = double.NegativeInfinity;
        bounds[1] = double.PositiveInfinity;

        for (var at = 1; at < count; at++) {
            var meet = Meet(values, parabolas[top], at, spacing);

            // ⚠ The first bound is minus infinity, so this stops at the first parabola however low the new one is.
            while (meet <= bounds[top]) {
                top--;
                meet = Meet(values, parabolas[top], at, spacing);
            }

            top++;
            parabolas[top] = at;
            bounds[top] = meet;
            bounds[top + 1] = double.PositiveInfinity;
        }

        var lowest = 0;

        for (var at = 0; at < count; at++) {
            var place = at * spacing;

            while (bounds[lowest + 1] < place) {
                lowest++;
            }

            var offset = place - parabolas[lowest] * spacing;
            squared[first + at * stride] = offset * offset + values[parabolas[lowest]];
        }
    }

    /// <summary>Where the parabolas over two texels of a line cross.</summary>
    static double Meet(double[] values, int one, int two, double spacing) {
        var a = one * spacing;
        var b = two * spacing;

        return (values[two] + b * b - (values[one] + a * a)) / (2d * (b - a));
    }
}
