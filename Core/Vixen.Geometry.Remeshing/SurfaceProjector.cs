// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>The closest point on a fixed triangle set — what the pre-remesh reprojects onto every round.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/41 § D3 step 6: the relaxation is projected back onto the original surface each
///         iteration, so the shape does not drift.</b> Tangential relaxation moves a vertex toward the
///         centroid of its ring, and the centroid of a ring on a curved surface is <i>inside</i> the
///         surface. Left alone, five rounds of it shrink a sphere visibly and round every corner off a
///         boolean result — the mesh becomes beautifully isotropic and stops being the model.
///     </para>
///     <para>
///         ⚠ <b>Built once, over the input as it stood before step 6, and never rebuilt.</b>
///         Reprojecting onto the previous iteration's mesh is a random walk with no fixed point: each
///         round the target has already drifted, so the drift compounds instead of being corrected.
///         The reference surface is the one the caller handed in.
///     </para>
///     <para>
///         A uniform grid rather than a tree. The input to this stage is marching-cubes output whose
///         triangles are all within a factor of a few of one another in size — the case a uniform grid
///         is exactly right for and the case a BVH's build cost buys nothing on. The winding-number
///         field in <see cref="VoxelShrinkwrap" /> has the opposite requirement and builds a tree.
///     </para>
/// </remarks>
sealed class SurfaceProjector {
    readonly Vector3[] positions;
    readonly int[] triangles;

    readonly int[] cells;
    readonly int[] cellStarts;

    readonly Vector3 origin;
    readonly float spacing;
    readonly int resolution;

    SurfaceProjector(
        Vector3[] positions,
        int[] triangles,
        int[] cells,
        int[] cellStarts,
        Vector3 origin,
        float spacing,
        int resolution
    ) {
        this.positions = positions;
        this.triangles = triangles;
        this.cells = cells;
        this.cellStarts = cellStarts;
        this.origin = origin;
        this.spacing = spacing;
        this.resolution = resolution;
    }

    /// <summary>Whether there is any surface to project onto.</summary>
    public bool IsEmpty => triangles.Length == 0;

    /// <summary>The nearest point of the reference surface to a query point.</summary>
    /// <param name="query">Where from.</param>
    /// <returns>The nearest point, or the query itself when the reference surface is empty.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Shells outward from the query's own cell, and the stopping rule is a distance
    ///         rather than a hit count.</b> Stopping on the first shell that contains a triangle is
    ///         the bug this exists to avoid: a triangle found in the shell at radius <c>r</c> can be
    ///         further away than one in the shell at <c>r + 1</c>, because a cell's far corner is
    ///         further from the query than the next cell's near face. Anything in the shell at
    ///         <c>r</c> is at least <c>(r − 1)</c> cells away, so the search is exact once that
    ///         bound passes the best distance found — and it is exact rather than nearly exact,
    ///         which matters because this is what the reprojection test measures deviation with.
    ///     </para>
    ///     <para>
    ///         The query's cell is <i>not</i> clamped into the grid. Clamping it is one line shorter
    ///         and breaks the bound above for any query outside the box — which after a relaxation
    ///         step is every vertex that moved outward.
    ///     </para>
    /// </remarks>
    public Vector3 Project(Vector3 query) {
        if (triangles.Length == 0) {
            return query;
        }

        var cell = Cell(query);

        var best = float.PositiveInfinity;
        var closest = query;

        // How far the shells have to reach to have covered every cell of the grid, which for a query
        // inside the box is at most the resolution and for one outside it is further.
        var reach = 0;

        foreach (var axis in (int[]) [cell.X, cell.Y, cell.Z]) {
            reach = Math.Max(reach, Math.Max(Math.Abs(axis), Math.Abs(axis - (resolution - 1))));
        }

        for (var radius = 0; radius <= reach; radius++) {
            var bound = (radius - 1) * spacing;

            if (bound > 0f && bound * bound >= best) {
                break;
            }

            for (var x = cell.X - radius; x <= cell.X + radius; x++) {
                for (var y = cell.Y - radius; y <= cell.Y + radius; y++) {
                    for (var z = cell.Z - radius; z <= cell.Z + radius; z++) {
                        // Only the shell, not the solid block: the interior was covered by the
                        // smaller radii, and walking it again is the difference between linear and
                        // cubic in the radius.
                        var onShell = Math.Abs(x - cell.X) == radius
                            || Math.Abs(y - cell.Y) == radius
                            || Math.Abs(z - cell.Z) == radius;

                        if (!onShell || !Inside(x, y, z)) {
                            continue;
                        }

                        var index = (((z * resolution) + y) * resolution) + x;

                        for (var at = cellStarts[index]; at < cellStarts[index + 1]; at++) {
                            var triangle = cells[at];

                            var point = ClosestOnTriangle(
                                query,
                                positions[triangles[(triangle * 3) + 0]],
                                positions[triangles[(triangle * 3) + 1]],
                                positions[triangles[(triangle * 3) + 2]]
                            );

                            var distance = Vector3.DistanceSquared(point, query);

                            if (distance < best) {
                                best = distance;
                                closest = point;
                            }
                        }
                    }
                }
            }
        }

        return closest;
    }

    /// <summary>How far a point is from the reference surface.</summary>
    /// <param name="query">Where from.</param>
    /// <returns>The distance, in world units.</returns>
    public float Distance(Vector3 query) => Vector3.Distance(query, Project(query));

    /// <summary>Indexes a triangle set into a uniform grid.</summary>
    /// <param name="positions">The vertices. Retained.</param>
    /// <param name="triangles">Three indices per triangle. Retained.</param>
    /// <returns>The projector.</returns>
    public static SurfaceProjector Build(Vector3[] positions, int[] triangles) {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(triangles);

        var count = triangles.Length / 3;

        if (count == 0 || positions.Length == 0) {
            return new(positions, triangles, [], [0], Vector3.Zero, 1f, 1);
        }

        var box = BoundingBox.FromPoints(positions);
        var size = box.Maximum - box.Minimum;
        var diagonal = size.Length();

        // ⚠ The cell size is derived from the mesh, never fixed. Roughly one triangle per cell is
        // the target, so the resolution is the cube root of the triangle count clamped into a range
        // that keeps the table small on a two-triangle mesh and useful on a two-million one.
        var steps = Math.Clamp((int) MathF.Cbrt(count) + 1, 1, 128);
        var spacing = diagonal > 0f ? diagonal / steps : 1f;

        if (spacing <= 0f) {
            spacing = 1f;
        }

        var resolution = steps + 1;
        var total = resolution * resolution * resolution;
        var starts = new int[total + 1];
        var origin = box.Minimum;

        // Two passes and a counting sort, so a grid of a hundred thousand cells is two integer arrays
        // rather than a hundred thousand lists.
        for (var pass = 0; pass < 2; pass++) {
            for (var triangle = 0; triangle < count; triangle++) {
                var a = positions[triangles[(triangle * 3) + 0]];
                var b = positions[triangles[(triangle * 3) + 1]];
                var c = positions[triangles[(triangle * 3) + 2]];

                var low = Grid(Vector3.Min(Vector3.Min(a, b), c), origin, spacing, resolution);
                var high = Grid(Vector3.Max(Vector3.Max(a, b), c), origin, spacing, resolution);

                for (var x = low.X; x <= high.X; x++) {
                    for (var y = low.Y; y <= high.Y; y++) {
                        for (var z = low.Z; z <= high.Z; z++) {
                            var index = (((z * resolution) + y) * resolution) + x;

                            if (pass == 0) {
                                starts[index + 1]++;
                            }
                        }
                    }
                }
            }

            if (pass == 0) {
                for (var index = 1; index <= total; index++) {
                    starts[index] += starts[index - 1];
                }
            }
        }

        var cells = new int[starts[total]];
        var cursor = new int[total];

        for (var triangle = 0; triangle < count; triangle++) {
            var a = positions[triangles[(triangle * 3) + 0]];
            var b = positions[triangles[(triangle * 3) + 1]];
            var c = positions[triangles[(triangle * 3) + 2]];

            var low = Grid(Vector3.Min(Vector3.Min(a, b), c), origin, spacing, resolution);
            var high = Grid(Vector3.Max(Vector3.Max(a, b), c), origin, spacing, resolution);

            for (var x = low.X; x <= high.X; x++) {
                for (var y = low.Y; y <= high.Y; y++) {
                    for (var z = low.Z; z <= high.Z; z++) {
                        var index = (((z * resolution) + y) * resolution) + x;

                        cells[starts[index] + cursor[index]] = triangle;
                        cursor[index]++;
                    }
                }
            }
        }

        return new(positions, triangles, cells, starts, origin, spacing, resolution);
    }

    /// <summary>The closest point of a triangle to a query, by the standard region test.</summary>
    /// <param name="query">Where from.</param>
    /// <param name="a">The triangle's first corner.</param>
    /// <param name="b">Its second.</param>
    /// <param name="c">Its third.</param>
    /// <returns>The closest point, which is on an edge or at a corner when the projection falls outside.</returns>
    /// <remarks>
    ///     ⚠ <b>The seven-region form, not "project onto the plane and clamp".</b> Clamping barycentric
    ///     coordinates gives the wrong point whenever the query projects outside two of the three
    ///     edges at once — which is every query near a corner, which on a sliver-heavy mesh is most of
    ///     them. Ericson, <i>Real-Time Collision Detection</i> § 5.1.5.
    /// </remarks>
    public static Vector3 ClosestOnTriangle(Vector3 query, Vector3 a, Vector3 b, Vector3 c) {
        var ab = b - a;
        var ac = c - a;
        var ap = query - a;

        var d1 = Vector3.Dot(ab, ap);
        var d2 = Vector3.Dot(ac, ap);

        if (d1 <= 0f && d2 <= 0f) {
            return a;
        }

        var bp = query - b;
        var d3 = Vector3.Dot(ab, bp);
        var d4 = Vector3.Dot(ac, bp);

        if (d3 >= 0f && d4 <= d3) {
            return b;
        }

        var vc = (d1 * d4) - (d3 * d2);

        if (vc <= 0f && d1 >= 0f && d3 <= 0f) {
            var denominator = d1 - d3;

            return denominator != 0f ? a + (ab * (d1 / denominator)) : a;
        }

        var cp = query - c;
        var d5 = Vector3.Dot(ab, cp);
        var d6 = Vector3.Dot(ac, cp);

        if (d6 >= 0f && d5 <= d6) {
            return c;
        }

        var vb = (d5 * d2) - (d1 * d6);

        if (vb <= 0f && d2 >= 0f && d6 <= 0f) {
            var denominator = d2 - d6;

            return denominator != 0f ? a + (ac * (d2 / denominator)) : a;
        }

        var va = (d3 * d6) - (d5 * d4);

        if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f) {
            var denominator = d4 - d3 + (d5 - d6);

            return denominator != 0f ? b + ((c - b) * ((d4 - d3) / denominator)) : b;
        }

        var total = va + vb + vc;

        if (total <= 0f) {
            return a;
        }

        return a + (ab * (vb / total)) + (ac * (vc / total));
    }

    bool Inside(int x, int y, int z) =>
        (uint) x < (uint) resolution && (uint) y < (uint) resolution && (uint) z < (uint) resolution;

    /// <summary>Which cell a query point falls in, unclamped — see <see cref="Project" />.</summary>
    (int X, int Y, int Z) Cell(Vector3 point) {
        var offset = (point - origin) / spacing;

        return ((int) MathF.Floor(offset.X), (int) MathF.Floor(offset.Y), (int) MathF.Floor(offset.Z));
    }

    /// <summary>Which cell a point of the reference surface is stored in, clamped into the grid.</summary>
    static (int X, int Y, int Z) Grid(Vector3 point, Vector3 origin, float spacing, int resolution) {
        var offset = (point - origin) / spacing;

        return (
            Math.Clamp((int) MathF.Floor(offset.X), 0, resolution - 1),
            Math.Clamp((int) MathF.Floor(offset.Y), 0, resolution - 1),
            Math.Clamp((int) MathF.Floor(offset.Z), 0, resolution - 1)
        );
    }
}
