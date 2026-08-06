// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>Conservative coverage of a texel grid by a triangle in texture space.</summary>
/// <remarks>
///     <para>
///         <b>This exists because there is no CPU mesh-to-texture rasterizer in this repository and
///         the two things that bake a mesh into a texture cannot be reused.</b>
///         <c>SurfaceCardCapture</c> and <c>ImpostorBake</c> both run on the GPU and both project
///         along an <i>axis</i> rather than through a parameterization, so neither answers "which
///         texels does this chart cover". The half-space rule that <i>is</i> reusable —
///         <c>SoftwareRaster.Edge</c>, <c>IsTopLeft</c> and <c>Inside</c> — lives in
///         <c>Vixen.Rendering</c>, which is one layer above this assembly and which
///         <c>RemeshingLayeringTests</c> forbids referencing. So the arithmetic is here.
///     </para>
///     <para>
///         ⚠ <b>And it could not have been that rule anyway, because that rule is pixel-centre.</b>
///         A half-space test at the centre of a texel asks whether the triangle contains the centre,
///         which is the correct question for a framebuffer and the wrong one for an atlas: a chart
///         two texels wide, or the last row of texels along any chart's edge, covers texels whose
///         centres it misses. Those texels then read as background, and background at a chart's edge
///         is exactly what a gutter exists to prevent — so the hole survives dilation, because
///         dilation only fills what nothing claimed. Conservative coverage is
///         <see cref="Overlaps" />: does the triangle touch the texel's <i>square</i> at all.
///     </para>
///     <para>
///         <b>Separating axes rather than clipping.</b> <c>Heightfield.RasterizeTriangle</c> is the
///         repository's reference for conservative coverage and it clips the triangle to each cell,
///         because it needs the clipped polygon's own height range. A bake needs no such thing — only
///         a yes or no per texel — and five dot products answer that with no polygon, no seven-vertex
///         scratch buffer and no case analysis.
///     </para>
/// </remarks>
static class AtlasRaster {
    /// <summary>Whether a triangle touches an axis-aligned box at all.</summary>
    /// <param name="a">The triangle's first corner.</param>
    /// <param name="b">Its second.</param>
    /// <param name="c">Its third.</param>
    /// <param name="minimum">The box's lower corner.</param>
    /// <param name="maximum">Its upper corner.</param>
    /// <returns>Whether they overlap, edges and corners included.</returns>
    /// <remarks>
    ///     Five axes: the box's two, on which the test is a bounding-box rejection, and the
    ///     triangle's three edge normals. A convex pair is disjoint exactly when some axis separates
    ///     them, and for a triangle against a box those five are the only candidates.
    /// </remarks>
    public static bool Overlaps(Vector2 a, Vector2 b, Vector2 c, Vector2 minimum, Vector2 maximum) {
        if (MathF.Min(a.X, MathF.Min(b.X, c.X)) > maximum.X
            || MathF.Max(a.X, MathF.Max(b.X, c.X)) < minimum.X
            || MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) > maximum.Y
            || MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) < minimum.Y) {
            return false;
        }

        return !Separates(a, b, c, minimum, maximum)
            && !Separates(b, c, a, minimum, maximum)
            && !Separates(c, a, b, minimum, maximum);
    }

    /// <summary>Whether the normal of one triangle edge separates the triangle from the box.</summary>
    /// <param name="p">The edge's start.</param>
    /// <param name="q">Its end.</param>
    /// <param name="r">The triangle's third corner.</param>
    /// <param name="minimum">The box's lower corner.</param>
    /// <param name="maximum">Its upper corner.</param>
    /// <returns>Whether this axis proves them disjoint.</returns>
    /// <remarks>
    ///     ⚠ <b>Nothing is normalised, and that is what makes the test scale-free.</b> Both
    ///     projections are taken on the same un-normalised axis, so the axis's own length is a
    ///     common factor of every number compared and cancels out of the comparison. Normalising
    ///     would introduce a division by a length that is zero for a degenerate edge — a chart
    ///     triangle with two coincident corners is a real input, and it must read as "covers
    ///     nothing", not as a <c>NaN</c> that covers everything.
    /// </remarks>
    static bool Separates(Vector2 p, Vector2 q, Vector2 r, Vector2 minimum, Vector2 maximum) {
        var axis = new Vector2(p.Y - q.Y, q.X - p.X);

        if (axis.X == 0f && axis.Y == 0f) {
            return false;
        }

        var edge = (axis.X * p.X) + (axis.Y * p.Y);
        var apex = (axis.X * r.X) + (axis.Y * r.Y);

        var centre = (minimum + maximum) * 0.5f;
        var extent = (maximum - minimum) * 0.5f;
        var middle = (axis.X * centre.X) + (axis.Y * centre.Y);
        var radius = (MathF.Abs(axis.X) * extent.X) + (MathF.Abs(axis.Y) * extent.Y);

        return MathF.Min(edge, apex) > middle + radius || MathF.Max(edge, apex) < middle - radius;
    }

    /// <summary>Where a point sits on a triangle, clamped onto it when it is outside.</summary>
    /// <param name="point">The point — a texel centre, which conservative coverage allows to be outside.</param>
    /// <param name="a">The triangle's first corner.</param>
    /// <param name="b">Its second.</param>
    /// <param name="c">Its third.</param>
    /// <returns>Weights summing to one.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Conservative coverage means the texel centre is regularly <i>not</i> inside the
    ///         triangle</b>, so the edge functions' ratio is not a barycentric coordinate there — it
    ///         is an extrapolation, and extrapolating a position along a chart's outermost row puts
    ///         the bake's ray origin off the surface. Clamping to the nearest point on the triangle
    ///         is what makes the outermost row of texels sample the chart's own edge.
    ///     </para>
    ///     <para>
    ///         <b>Answered by the shared 3D routine at <c>z = 0</c>, deliberately.</b>
    ///         <c>TriangleTree.ClosestPointOnTriangle</c> is Ericson's seven-region test, it is unit
    ///         tested, and its weights sum to one on an edge, on a vertex and on a triangle with no
    ///         area — which are precisely the cases a second 2D copy of it would get wrong.
    ///     </para>
    /// </remarks>
    public static Vector3 Barycentric(Vector2 point, Vector2 a, Vector2 b, Vector2 c) {
        TriangleTree.ClosestPointOnTriangle(
            new(point.X, point.Y, 0f),
            new(a.X, a.Y, 0f),
            new(b.X, b.Y, 0f),
            new(c.X, c.Y, 0f),
            out var barycentric
        );

        return barycentric;
    }
}
