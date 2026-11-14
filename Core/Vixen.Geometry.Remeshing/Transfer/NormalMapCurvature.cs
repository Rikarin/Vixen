// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>A curvature map from the baked normals, as <see cref="CurvatureSource.NormalMap" /> asks.</summary>
/// <remarks>
///     <para>
///         <b>Half the divergence of the tangent-space normal across the atlas</b>: how fast its x turns along the
///         tangent plus how fast its y turns along the bitangent, which points up the image while the rows run down
///         it, so the second is minus its slope down the rows. A dome read with the rows' sign reads near nothing,
///         its two halves cancelling. A dome bakes normals leaning away from its middle, so both terms are
///         positive on it and negative in a groove, the sign <see cref="MeanCurvature" /> gives an edge and a crease.
///         Half, because the divergence of a unit normal is the sum of the two principal curvatures and a curvature
///         map holds their mean, so a sphere of radius <i>r</i> still reads about <c>1/r</c>.
///     </para>
///     <para>
///         ⚠ <b>Per texel, then over one texel's length on the model</b>, which is the target's area over the
///         texels its charts claim. That holds where the layout's texel density is even, which a layout means it
///         to be; a stretched chart reads its bends that much stronger along the stretch.
///     </para>
///     <para>
///         ⚠ <b>Only between texels a chart covers.</b> A neighbour in the gutter or the background has no normal of
///         its own, so the difference is taken on the covered side alone, or not at all for a texel with neither.
///         Charts are kept apart by their gutter, so a difference never reaches across a seam into another chart.
///     </para>
///     <para>
///         <b>None of the target's own shape.</b> A tangent-space normal is the source measured against the target's
///         surface, so a low mesh that is a sphere and a high mesh that is the same sphere bake flat. That is the
///         detail an artist wants from it; the mesh's own bends are <see cref="CurvatureSource.Mesh" />.
///     </para>
/// </remarks>
static class NormalMapCurvature {
    /// <summary>Writes the curvature of the baked normals into <paramref name="curvature" />.</summary>
    /// <param name="curvature">The map, which this fills.</param>
    /// <param name="buffers">The bake, with its normals and coverage.</param>
    /// <param name="settings">The atlas's size and the normals' space.</param>
    /// <param name="target">The surface baked onto, whose area gives a texel's length.</param>
    /// <returns>The largest magnitude written, in reciprocal model units.</returns>
    public static float Fill(float[] curvature, BakeBuffers buffers, BakeSettings settings, EditMesh target) {
        var normals = buffers.TangentNormals ?? buffers.Normals;
        var coverage = buffers.Coverage;
        var width = settings.Resolution;
        var rows = settings.Rows;
        var texel = TexelLength(target, buffers.Covered);
        var sharpest = 0f;

        if (texel <= 0f) {
            Array.Clear(curvature);

            return 0f;
        }

        for (var y = 0; y < rows; y++) {
            for (var x = 0; x < width; x++) {
                var index = y * width + x;

                if (!coverage[index]) {
                    continue;
                }

                var across = Slope(normals, coverage, index, x > 0 ? index - 1 : -1, x + 1 < width ? index + 1 : -1);
                var down = Slope(
                    normals,
                    coverage,
                    index,
                    y > 0 ? index - width : -1,
                    y + 1 < rows ? index + width : -1
                );
                // ⚠ Minus the slope down the rows: v runs down the image and the bitangent up it (MikkTSpace).
                var value = 0.5f * (across.X - down.Y) / texel;

                curvature[index] = value;
                sharpest = MathF.Max(sharpest, MathF.Abs(value));
            }
        }

        return sharpest;
    }

    /// <summary>How the normal turns per texel through <paramref name="index" />, from its covered neighbours.</summary>
    static Vector3 Slope(Vector3[] normals, bool[] coverage, int index, int before, int after) {
        var hasBefore = before >= 0 && coverage[before];
        var hasAfter = after >= 0 && coverage[after];

        return (hasBefore, hasAfter) switch {
            (true, true) => (normals[after] - normals[before]) * 0.5f,
            (true, false) => normals[index] - normals[before],
            (false, true) => normals[after] - normals[index],
            _ => Vector3.Zero
        };
    }

    /// <summary>One texel's side on the model: the square root of the target's area over the texels it covers.</summary>
    static float TexelLength(EditMesh target, int covered) {
        if (covered <= 0) {
            return 0f;
        }

        var area = 0.0;

        for (var face = 0; face < target.FaceCount; face++) {
            var loop = target.CornersOf(face);
            var first = target.Positions[loop[0]];

            for (var corner = 1; corner + 1 < loop.Length; corner++) {
                var edge = Vector3.Cross(
                    target.Positions[loop[corner]] - first,
                    target.Positions[loop[corner + 1]] - first
                );
                area += 0.5 * edge.Length();
            }
        }

        return (float)Math.Sqrt(area / covered);
    }
}
