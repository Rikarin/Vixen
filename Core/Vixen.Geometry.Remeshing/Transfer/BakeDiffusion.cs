// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>Fills every texel the charts and their gutter left empty from the texels around it, smoothly.</summary>
/// <remarks>
///     <para>
///         <b>Pull, then push.</b> The known texels are summed down a pyramid of halvings until one texel holds them
///         all; then, from the top down, every level becomes a mean, and an empty texel takes its parent's mean
///         interpolated bilinearly. So an empty region far from every chart is a soft blend of the charts around it,
///         one beside a chart is nearly its colour, and a known texel is never changed.
///     </para>
///     <para>
///         ⚠ <b>Past the gutter, never instead of it.</b> The gutter copies each chart's edge outward exactly, which is
///         what the first few mips need; diffusion fills the rest, so a texture filtered down to its last mips does not
///         fade every chart towards the empty atlas's black.
///     </para>
///     <para>
///         ⚠ <b>Labels and borders are left alone.</b> An id or an island is a label, and a blend of two is a third
///         that is nowhere on the model; a border's distance is zero off every island by definition.
///     </para>
/// </remarks>
static class BakeDiffusion {
    /// <summary>Fills the empty texels of every continuous channel the bake holds.</summary>
    /// <param name="buffers">The bake's channels.</param>
    /// <param name="known">Which texels the charts and the gutter wrote.</param>
    /// <param name="columns">The atlas's width.</param>
    /// <param name="rows">Its height.</param>
    /// <returns>How many texels it filled: none when nothing or everything was known.</returns>
    public static int Fill(BakeBuffers buffers, bool[] known, int columns, int rows) {
        var empty = 0;

        foreach (var texel in known) {
            empty += texel ? 0 : 1;
        }

        if (empty == 0 || empty == known.Length) {
            return 0;
        }

        var pyramid = Pyramid.Of(known, columns, rows);

        Channel(pyramid, MemoryMarshal.Cast<Vector3, float>(buffers.Normals.AsSpan()), 3, true);
        Channel(pyramid, buffers.Displacement, 1, false);
        Channel(pyramid, buffers.Occlusion, 1, false);
        Channel(pyramid, buffers.Curvature, 1, false);
        Channel(pyramid, buffers.Thickness, 1, false);
        Channel(pyramid, buffers.Opacity, 1, false);
        Channel(pyramid, buffers.Edges, 1, false);
        Channel(pyramid, Floats(buffers.Bent), 3, true);
        Channel(pyramid, Floats(buffers.Position), 3, false);
        Channel(pyramid, Floats(buffers.WorldNormal), 3, true);

        if (buffers.Colour is { } colour) {
            Channel(pyramid, MemoryMarshal.Cast<Vector4, float>(colour.AsSpan()), 4, false);
        }

        return empty;
    }

    static Span<float> Floats(Vector3[]? channel) =>
        channel is null ? default : MemoryMarshal.Cast<Vector3, float>(channel.AsSpan());

    static void Channel(Pyramid pyramid, Span<float> values, int components, bool directions) {
        if (values.IsEmpty) {
            return;
        }

        var filled = pyramid.Diffuse(values, components);

        for (var index = 0; index < pyramid.Known.Length; index++) {
            if (pyramid.Known[index]) {
                continue;
            }

            var value = filled.AsSpan(index * components, components);

            if (directions) {
                Normalise(value);
            }

            value.CopyTo(values.Slice(index * components, components));
        }
    }

    /// <summary>Makes a direction a unit one, or leaves a sum that cancelled to nothing as it is.</summary>
    static void Normalise(Span<float> value) {
        var length = 0f;

        foreach (var component in value) {
            length += component * component;
        }

        if (length <= 0f) {
            return;
        }

        var scale = 1f / MathF.Sqrt(length);

        for (var at = 0; at < value.Length; at++) {
            value[at] *= scale;
        }
    }

    /// <summary>The halvings' sizes and how many known texels each of their texels holds, which every channel shares.</summary>
    sealed class Pyramid {
        readonly int[] widths;
        readonly int[] heights;
        readonly float[][] weights;

        Pyramid(bool[] known, int[] widths, int[] heights, float[][] weights) {
            Known = known;
            this.widths = widths;
            this.heights = heights;
            this.weights = weights;
        }

        /// <summary>Which texels of the atlas were written.</summary>
        public bool[] Known { get; }

        public static Pyramid Of(bool[] known, int columns, int rows) {
            List<int> widths = [columns];
            List<int> heights = [rows];
            var first = new float[known.Length];

            for (var index = 0; index < known.Length; index++) {
                first[index] = known[index] ? 1f : 0f;
            }

            List<float[]> weights = [first];

            while (widths[^1] > 1 || heights[^1] > 1) {
                var (width, height) = ((widths[^1] + 1) / 2, (heights[^1] + 1) / 2);

                weights.Add(Halved(weights[^1], widths[^1], heights[^1], width, height, 1));
                widths.Add(width);
                heights.Add(height);
            }

            return new(known, [..widths], [..heights], [..weights]);
        }

        /// <summary>A level summed into one half its size: each texel the sum of the up to four below it.</summary>
        static float[] Halved(float[] below, int fromWidth, int fromHeight, int width, int height, int components) {
            var sum = new float[width * height * components];

            for (var y = 0; y < fromHeight; y++) {
                for (var x = 0; x < fromWidth; x++) {
                    var from = (y * fromWidth + x) * components;
                    var to = (y / 2 * width + x / 2) * components;

                    for (var c = 0; c < components; c++) {
                        sum[to + c] += below[from + c];
                    }
                }
            }

            return sum;
        }

        /// <summary>A channel's values everywhere: the known ones as they are, every other one pushed down from above.</summary>
        public float[] Diffuse(ReadOnlySpan<float> values, int components) {
            var sums = new float[weights.Length][];
            sums[0] = new float[values.Length];

            for (var index = 0; index < Known.Length; index++) {
                if (Known[index]) {
                    values.Slice(index * components, components).CopyTo(sums[0].AsSpan(index * components));
                }
            }

            for (var level = 1; level < weights.Length; level++) {
                sums[level] = Halved(
                    sums[level - 1],
                    widths[level - 1],
                    heights[level - 1],
                    widths[level],
                    heights[level],
                    components
                );
            }

            var above = Mean(weights.Length - 1, sums[^1], components, null);

            for (var level = weights.Length - 2; level >= 0; level--) {
                above = Mean(level, sums[level], components, above);
            }

            return above;
        }

        /// <summary>A level's means: a texel with known ones under it their mean, an empty one its parent's.</summary>
        float[] Mean(int level, float[] sum, int components, float[]? parent) {
            var weight = weights[level];
            var width = widths[level];

            for (var index = 0; index < weight.Length; index++) {
                var at = index * components;

                if (weight[index] > 0f) {
                    for (var c = 0; c < components; c++) {
                        sum[at + c] /= weight[index];
                    }
                } else if (parent is not null) {
                    Upsample(parent, level + 1, index % width, index / width, sum.AsSpan(at, components));
                }
            }

            return sum;
        }

        /// <summary>The value of the level above under a texel of the one below, bilinearly.</summary>
        void Upsample(float[] parent, int level, int x, int y, Span<float> into) {
            var (width, height) = (widths[level], heights[level]);
            var u = Math.Clamp((x + 0.5f) / 2f - 0.5f, 0f, width - 1);
            var v = Math.Clamp((y + 0.5f) / 2f - 0.5f, 0f, height - 1);
            var (x0, y0) = ((int)u, (int)v);
            var (x1, y1) = (Math.Min(x0 + 1, width - 1), Math.Min(y0 + 1, height - 1));
            var (fx, fy) = (u - x0, v - y0);
            var components = into.Length;

            for (var c = 0; c < components; c++) {
                var upper = parent[(y0 * width + x0) * components + c] * (1f - fx)
                    + parent[(y0 * width + x1) * components + c] * fx;
                var lower = parent[(y1 * width + x0) * components + c] * (1f - fx)
                    + parent[(y1 * width + x1) * components + c] * fx;

                into[c] = upper * (1f - fy) + lower * fy;
            }
        }
    }
}
