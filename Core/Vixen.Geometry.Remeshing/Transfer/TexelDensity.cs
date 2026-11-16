// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>How many texels of the atlas each unit of the target gets, against the layout as a whole.</summary>
/// <remarks>
///     <para>
///         <b>A triangle's density is the square root of its area on the atlas, in texels, over its area on the model</b>:
///         texels per model unit along a side. A texel takes its owner's, as its island does.
///     </para>
///     <para>
///         ⚠ <b>The mean is the layout's, not the texels' average.</b> Each covered texel stands for one over its density
///         squared of the model's area, so the layout's density is its texels over the area they cover, and a small
///         island packed densely cannot pull the mean as far as its count of texels would.
///     </para>
/// </remarks>
static class TexelDensity {
    /// <summary>The ratio either end of the map stands for: a quarter of the mean reads nought and four times it one.</summary>
    const float Reach = 4f;

    /// <summary>The least a covered texel reads, one step of eight bits, so nought is left for the gutter alone.</summary>
    /// <remarks>
    ///     ⚠ <b>A texel no island reaches reads nought, and one far sparser than the layout must not read the same.</b>
    ///     A generator that colours sparse texels would otherwise colour every gap between islands as the sparsest part
    ///     of the model (Patina #306). One step of eight bits survives the editor's 8-bit bake as well as a 16-bit one.
    /// </remarks>
    public const float Least = 1f / 255f;

    /// <summary>A triangle's texels per model unit.</summary>
    /// <param name="triangle">A chart triangle, corners in atlas texels and on the target.</param>
    /// <returns>Its density, or zero for a triangle with no area on the model.</returns>
    public static float Of(ChartTriangle triangle) {
        var (a, b, c) = (triangle.Scaled[0], triangle.Scaled[1], triangle.Scaled[2]);
        var flat = MathF.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X));
        var (p, q, r) = (triangle.Points[0], triangle.Points[1], triangle.Points[2]);
        var solid = Vector3.Cross(q - p, r - p).Length();

        return solid > 0f ? MathF.Sqrt(flat / solid) : 0f;
    }

    /// <summary>
    ///     Turns densities into the map, in place: <c>0.5 + log2(density / mean) / 4</c>, held to <see cref="Least" /> and
    ///     one, and nought where there is no density.
    /// </summary>
    /// <param name="densities">Each texel's density, zero where it has none.</param>
    /// <param name="covered">Which texels a chart claimed, which alone set the mean.</param>
    /// <returns>The layout's density, or zero when nothing covered had one.</returns>
    public static float Relative(float[] densities, bool[] covered) {
        var count = 0d;
        var area = 0d;

        for (var index = 0; index < densities.Length; index++) {
            if (covered[index] && densities[index] > 0f) {
                count++;
                area += 1d / ((double)densities[index] * densities[index]);
            }
        }

        var mean = area > 0d ? (float)Math.Sqrt(count / area) : 0f;
        var span = 2f * MathF.Log2(Reach);

        for (var index = 0; index < densities.Length; index++) {
            var density = densities[index];

            densities[index] = density > 0f && mean > 0f
                ? Math.Clamp(0.5f + MathF.Log2(density / mean) / span, Least, 1f)
                : 0f;
        }

        return mean;
    }
}
