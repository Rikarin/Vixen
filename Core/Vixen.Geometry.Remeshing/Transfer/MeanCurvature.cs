// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>Mean curvature at every source vertex, from the cotangent Laplacian.</summary>
/// <remarks>
///     <para>
///         <b>Not <see cref="CurvatureField" />, and the difference is the question being asked.</b>
///         That class fits the second fundamental form per vertex to get κ₁, κ₂ and the principal
///         <i>directions</i>, because the field solve needs a direction and needs the anisotropy that
///         says how much to trust it. A curvature <i>map</i> wants one signed number an artist can
///         drive edge wear from, and the discrete mean-curvature normal gives it in one pass over the
///         triangles with no eigen-decomposition.
///     </para>
///     <para>
///         Meyer et al.'s operator: <c>K(v) = (1 / 2A) · Σ (cot α + cot β)(v − v_j)</c> over the
///         one-ring, whose magnitude is twice the mean curvature and whose direction is the normal.
///         So <c>H = ½|K|</c>, signed by which side of the surface <c>K</c> points at — positive
///         where the surface is convex along its own outward normal, which is the sign an artist
///         means by "an edge".
///     </para>
///     <para>
///         ⚠ <b>It is a curvature, so it is one over a length and it is deliberately not
///         normalised.</b> A sphere of radius <i>r</i> reads <c>1/r</c> — that is the test — and the
///         same sphere modelled a thousand times larger reads a thousandth of it, correctly.
///         Everything else in this library that reports a curvature multiplies by the diagonal to
///         make a dimensionless number for a threshold to compare against; a map is quantized rather
///         than compared, and <c>BakedMaps.CurvatureRange</c> is the scale that goes beside the
///         pixels for the same reason <c>DisplacementRange</c> does.
///     </para>
///     <para>
///         ⚠ <b>A boundary vertex reads zero, and that is a refusal rather than a measurement.</b>
///         The operator assumes a closed one-ring; on an open rim the missing half of the ring is not
///         a flat half, it is nothing, and the sum comes back as a large value pointing into the
///         mesh. Left in, every open edge of every source — a sheet, a cut-out, a plane — would bake
///         a bright rim that no generator keyed off curvature could tell from a real crease.
///     </para>
/// </remarks>
static class MeanCurvature {
    /// <summary>Mean curvature at each of the source's positions.</summary>
    /// <param name="surface">The triangulated source.</param>
    /// <returns>One signed curvature per position, in reciprocal model units.</returns>
    public static float[] Build(SourceSurface surface) {
        var positions = surface.Positions;
        var count = positions.Length;
        var laplacian = new Vector3[count];
        var area = new float[count];
        var normals = new Vector3[count];
        var boundary = Boundary(surface, count);

        for (var triangle = 0; triangle < surface.TriangleCount; triangle++) {
            var slots = surface.PositionsOf(triangle);
            var a = positions[slots[0]];
            var b = positions[slots[1]];
            var c = positions[slots[2]];

            var cross = Vector3.Cross(b - a, c - a);
            var twice = cross.Length();

            // ⚠ Relative, because a cross product carries the model's units squared. An absolute
            // floor here is the bug ScaleSafe exists to stop: a millimetre-scale mesh has cross
            // products of 1e-9 and every one of its triangles would read as degenerate.
            if (twice <= MathUtil.ZeroTolerance * (b - a).Length() * (c - a).Length()) {
                continue;
            }

            var face = cross / twice;
            var weight = twice * 0.5f;

            Span<float> cotangents = [
                Cotangent(b - a, c - a),
                Cotangent(c - b, a - b),
                Cotangent(a - c, b - c)
            ];

            // The angle at one corner weights the edge opposite it, which is what makes the sum over
            // a vertex's ring the two angles subtending each of its edges.
            Accumulate(laplacian, positions, slots[1], slots[2], cotangents[0]);
            Accumulate(laplacian, positions, slots[2], slots[0], cotangents[1]);
            Accumulate(laplacian, positions, slots[0], slots[1], cotangents[2]);

            var obtuse = cotangents[0] < 0f ? 0 : cotangents[1] < 0f ? 1 : cotangents[2] < 0f ? 2 : -1;

            for (var corner = 0; corner < 3; corner++) {
                normals[slots[corner]] += face * weight;

                area[slots[corner]] += obtuse >= 0
                    ? weight * (corner == obtuse ? 0.5f : 0.25f)
                    : Voronoi(positions, slots, cotangents, corner);
            }
        }

        var curvature = new float[count];

        for (var vertex = 0; vertex < count; vertex++) {
            if (boundary[vertex] || area[vertex] <= 0f) {
                continue;
            }

            var operated = laplacian[vertex] / (2f * area[vertex]);
            var magnitude = operated.Length();

            if (magnitude <= 0f) {
                continue;
            }

            // Half, because |K| is twice the mean curvature; signed by which way it points, because
            // a dent and a bump have the same magnitude and are not the same thing.
            curvature[vertex] = magnitude * 0.5f * (Vector3.Dot(operated, normals[vertex]) < 0f ? -1f : 1f);
        }

        return curvature;
    }

    /// <summary>Adds one cotangent-weighted edge to both of its ends.</summary>
    static void Accumulate(Vector3[] laplacian, ReadOnlySpan<Vector3> positions, int i, int j, float cotangent) {
        var edge = (positions[i] - positions[j]) * cotangent;

        laplacian[i] += edge;
        laplacian[j] -= edge;
    }

    /// <summary>The cotangent of the angle between two edges leaving one corner.</summary>
    /// <remarks>
    ///     ⚠ <b>Scale-free by construction.</b> The dot product and the cross product both carry the
    ///     model's units squared, so their ratio does not — which is why this needs no tolerance of
    ///     its own and why a degenerate triangle is rejected by its caller instead.
    /// </remarks>
    static float Cotangent(Vector3 one, Vector3 two) {
        var cross = Vector3.Cross(one, two).Length();

        return cross > 0f ? Vector3.Dot(one, two) / cross : 0f;
    }

    /// <summary>The Voronoi area a non-obtuse triangle gives one of its corners.</summary>
    static float Voronoi(
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<int> slots,
        ReadOnlySpan<float> cotangents,
        int corner
    ) {
        var next = (corner + 1) % 3;
        var previous = (corner + 2) % 3;

        var toNext = (positions[slots[corner]] - positions[slots[next]]).LengthSquared();
        var toPrevious = (positions[slots[corner]] - positions[slots[previous]]).LengthSquared();

        return ((toNext * cotangents[previous]) + (toPrevious * cotangents[next])) / 8f;
    }

    /// <summary>Which vertices sit on an open rim, by counting each undirected edge's triangles.</summary>
    static bool[] Boundary(SourceSurface surface, int count) {
        var counts = new Dictionary<(int Low, int High), int>();
        var boundary = new bool[count];

        for (var triangle = 0; triangle < surface.TriangleCount; triangle++) {
            var slots = surface.PositionsOf(triangle);

            for (var corner = 0; corner < 3; corner++) {
                var a = slots[corner];
                var b = slots[(corner + 1) % 3];
                var key = a < b ? (a, b) : (b, a);

                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }

        foreach (var ((low, high), seen) in counts) {
            if (seen != 2) {
                boundary[low] = true;
                boundary[high] = true;
            }
        }

        return boundary;
    }
}
