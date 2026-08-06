// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Uv.Flattening;

/// <summary>One triangle laid flat in its own plane, which is where every rung of the ladder starts.</summary>
/// <param name="X1">The second corner's abscissa. The first sits at the origin and the second on the axis.</param>
/// <param name="X2">The third corner's abscissa.</param>
/// <param name="Y2">Its ordinate, which is never negative.</param>
/// <remarks>
///     <para>
///         Every energy in docs/plan/42 § D5 — LSCM's conformality condition, ARAP's fitted rotation,
///         § D6's singular values — is a statement about the map from the triangle's <i>own</i> plane
///         to the parameter plane. Computing that frame once, in <see langword="double" />, is what
///         keeps the three of them agreeing about what the triangle was before it was flattened.
///     </para>
///     <para>
///         ⚠ <b><paramref name="Y2" /> is a length and therefore non-negative, so the frame is always
///         counter-clockwise.</b> That is what makes <c>Orient2D &lt; 0</c> in the parameter plane mean
///         <i>flipped</i> without a per-triangle reference orientation to compare against — see
///         <see cref="Distortion" />.
///     </para>
/// </remarks>
readonly record struct TriangleFrame(double X1, double X2, double Y2) {
    /// <summary>Twice the triangle's area, in world units squared.</summary>
    public double DoubleArea => X1 * Y2;

    /// <summary>
    ///     Whether the triangle has no area in three dimensions, so no frame exists and every
    ///     cotangent in it is infinite.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Against zero rather than against an epsilon, and <c>EditMesh.Normal</c> records the
    ///     same decision for the same reason.</b> Twice the area carries the mesh's units squared, so a
    ///     fixed threshold is a claim about how big the model is — and a hemisphere's pole triangles
    ///     are genuinely small. A model arriving in millimetres would have had its whole cap declared
    ///     degenerate.
    /// </remarks>
    public bool IsDegenerate => !(DoubleArea > 0d);

    /// <summary>Lays one triangle flat.</summary>
    /// <param name="a">Its first corner, which becomes the origin.</param>
    /// <param name="b">Its second, which becomes a point on the abscissa.</param>
    /// <param name="c">Its third.</param>
    /// <returns>The frame, which is <see cref="IsDegenerate" /> when the triangle has no area.</returns>
    public static TriangleFrame Build(Vector3 a, Vector3 b, Vector3 c) {
        double ux = b.X - (double)a.X, uy = b.Y - (double)a.Y, uz = b.Z - (double)a.Z;
        double vx = c.X - (double)a.X, vy = c.Y - (double)a.Y, vz = c.Z - (double)a.Z;

        var length = Math.Sqrt((ux * ux) + (uy * uy) + (uz * uz));

        if (!(length > 0d)) {
            return default;
        }

        var along = ((ux * vx) + (uy * vy) + (uz * vz)) / length;

        double nx = (uy * vz) - (uz * vy), ny = (uz * vx) - (ux * vz), nz = (ux * vy) - (uy * vx);
        var across = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz)) / length;

        return new(length, along, across);
    }
}
