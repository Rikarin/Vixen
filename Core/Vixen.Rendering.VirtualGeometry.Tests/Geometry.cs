// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.VirtualGeometry;

/// <summary>Measurements the tests make about geometry, written independently of the build.</summary>
/// <remarks>
///     Deliberately a second implementation rather than a call into the library. The point of
///     measuring how far a simplified surface has moved is to check the number the simplifier
///     reported, and asking the simplifier to measure it would check nothing at all.
/// </remarks>
static class Geometry {
    /// <summary>The edges a triangle list uses exactly once.</summary>
    /// <param name="corners">Three vertex indices per triangle.</param>
    /// <param name="welded">The welding.</param>
    /// <returns>The boundary edge keys.</returns>
    public static HashSet<long> Boundary(int[] corners, int[] welded) {
        var all = new int[corners.Length / 3];

        for (var triangle = 0; triangle < all.Length; triangle++) {
            all[triangle] = triangle;
        }

        return Topology.BoundaryEdges(corners, welded, all);
    }

    /// <summary>How far a simplified surface has moved from the mesh it stands in for.</summary>
    /// <param name="positions">The mesh's positions.</param>
    /// <param name="corners">The simplified triangles, as three indices into those positions each.</param>
    /// <param name="points">The points to measure from.</param>
    /// <returns>The greatest distance from any of them to the simplified surface.</returns>
    /// <remarks>
    ///     One-sided Hausdorff, from the fine mesh to the coarse one, which is the direction that
    ///     matters: the question is how far what is being drawn is from what was authored.
    /// </remarks>
    public static float Deviation(Vector3[] positions, int[] corners, IEnumerable<Vector3> points) {
        var worst = 0f;

        foreach (var point in points) {
            var nearest = float.MaxValue;

            for (var triangle = 0; triangle < corners.Length / 3; triangle++) {
                nearest = MathF.Min(
                    nearest,
                    DistanceSquared(
                        point,
                        positions[corners[triangle * 3]],
                        positions[corners[(triangle * 3) + 1]],
                        positions[corners[(triangle * 3) + 2]]
                    )
                );

                if (nearest <= 0) {
                    break;
                }
            }

            worst = MathF.Max(worst, nearest);
        }

        return MathF.Sqrt(worst);
    }

    /// <summary>The squared distance from a point to a triangle.</summary>
    /// <param name="point">The point.</param>
    /// <param name="a">One corner.</param>
    /// <param name="b">Another.</param>
    /// <param name="c">The third.</param>
    /// <returns>The squared distance.</returns>
    public static float DistanceSquared(Vector3 point, Vector3 a, Vector3 b, Vector3 c) {
        var ab = b - a;
        var ac = c - a;
        var ap = point - a;

        var d1 = Vector3.Dot(ab, ap);
        var d2 = Vector3.Dot(ac, ap);

        if (d1 <= 0 && d2 <= 0) {
            return (point - a).LengthSquared();
        }

        var bp = point - b;
        var d3 = Vector3.Dot(ab, bp);
        var d4 = Vector3.Dot(ac, bp);

        if (d3 >= 0 && d4 <= d3) {
            return (point - b).LengthSquared();
        }

        var cp = point - c;
        var d5 = Vector3.Dot(ab, cp);
        var d6 = Vector3.Dot(ac, cp);

        if (d6 >= 0 && d5 <= d6) {
            return (point - c).LengthSquared();
        }

        var vc = (d1 * d4) - (d3 * d2);

        if (vc <= 0 && d1 >= 0 && d3 <= 0) {
            return (point - (a + (ab * (d1 / (d1 - d3))))).LengthSquared();
        }

        var vb = (d5 * d2) - (d1 * d6);

        if (vb <= 0 && d2 >= 0 && d6 <= 0) {
            return (point - (a + (ac * (d2 / (d2 - d6))))).LengthSquared();
        }

        var va = (d3 * d6) - (d5 * d4);

        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) {
            return (point - (b + ((c - b) * ((d4 - d3) / (d4 - d3 + (d5 - d6)))))).LengthSquared();
        }

        var denominator = 1f / (va + vb + vc);

        return (point - (a + (ab * (vb * denominator)) + (ac * (vc * denominator)))).LengthSquared();
    }
}
