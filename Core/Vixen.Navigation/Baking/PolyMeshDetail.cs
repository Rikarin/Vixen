// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Navigation.Baking;

/// <summary>Where one polygon's extra vertices and its triangles live in the detail arrays.</summary>
internal readonly record struct PolyDetail(int FirstVertex, int VertexCount, int FirstTriangle, int TriangleCount);

/// <summary>
///     The height of the ground inside each polygon, sampled back off the heightfield.
/// </summary>
/// <remarks>
///     <para>
///         A navmesh polygon is flat, and it is flat at the height of its corners — which the contour
///         tracer took as the <i>highest</i> of the four spans meeting there, because a corner that
///         took the lowest would sink below the floor it belongs to. The two together mean the surface
///         an agent is placed on sits up to a cell height above the ground, and on anything that is
///         not a flat floor it also cuts corners: a polygon spanning a hump is a plane across the top
///         of it, and a polygon spanning a dip is a lid over it.
///     </para>
///     <para>
///         So each polygon gets its own little triangulation, with vertices at heights read back out
///         of the compact heightfield. Nothing about connectivity changes — the detail triangles are
///         never searched, never linked and never crossed. They answer one question: given a point
///         over this polygon, how high is the ground.
///     </para>
///     <para>
///         <b>The polygons are convex, and that is what makes this small.</b> The starting
///         triangulation is a fan; refining it is inserting a point and splitting the one triangle
///         that contains it. Recast reaches for a Delaunay hull here and gets rounder triangles for
///         it; nothing downstream looks at the shape of these triangles, only at which one a point is
///         in, so the rounder ones would be four hundred lines buying nothing.
///     </para>
/// </remarks>
internal sealed class PolyMeshDetail {
    /// <summary>How many vertices one polygon's outline may gain.</summary>
    const int MaxEdgeVertices = 32;

    /// <summary>How many vertices one polygon's interior may gain.</summary>
    /// <remarks>
    ///     <b>Two budgets rather than one, and this is the reason.</b> A polygon over open ground is
    ///     large — a hill with nothing on it partitions into a handful of polygons metres across — and
    ///     its outline is long enough to want a sample every couple of metres all the way round. With
    ///     a single budget the outline spent all of it and the interior, which is where an agent
    ///     actually walks, got none: the middle of a hill stayed exactly as wrong as the flat polygon
    ///     had been. The edges are the cheaper thing to be approximate about, so they are the ones
    ///     with the smaller share.
    /// </remarks>
    const int MaxInteriorVertices = 96;

    /// <summary>How many times the flip pass may sweep the triangles before it gives up.</summary>
    /// <remarks>
    ///     A backstop, not a budget: Lawson's flip terminates, and a sweep that is still flipping
    ///     after this many is two nearly-cocircular triangles whose in-circle sign is disagreeing with
    ///     itself. Stopping leaves a triangulation that is valid and slightly worse shaped, which is
    ///     the right way to lose that argument.
    /// </remarks>
    const int MaxFlipSweeps = 64;

    /// <summary>Vertices added beyond the polygons' own corners, in voxel coordinates.</summary>
    public List<Vector3> Vertices { get; } = [];

    /// <summary>
    ///     Three indices per triangle. An index below the polygon's own vertex count names one of its
    ///     corners; anything above names <see cref="Vertices" />, offset by the polygon's first.
    /// </summary>
    /// <remarks>
    ///     Recast's encoding, and it is worth the small awkwardness: the corners are already stored on
    ///     the polygon, and a detail mesh that repeated them would be a second copy that can drift
    ///     from the first — the one place a crack between two tiles would be invisible until an agent
    ///     fell through it.
    /// </remarks>
    public List<int> Triangles { get; } = [];

    /// <summary>One entry per polygon of the source mesh, in the same order.</summary>
    public List<PolyDetail> Polys { get; } = [];

    /// <summary>Samples the ground under every polygon of a mesh.</summary>
    /// <param name="mesh">The polygons, in voxel coordinates.</param>
    /// <param name="field">The surface they were built from.</param>
    /// <param name="sampleDistance">How far apart to sample, in voxel columns. Zero or less builds nothing.</param>
    /// <param name="maxError">How far the flat polygon may be from the ground before a vertex is added, in voxels of height.</param>
    /// <param name="walkableHeight">
    ///     The agent's height in voxels, which is how far a sample may look for its own surface. See
    ///     <see cref="TryGroundHeight" /> — this is not a tolerance, it is a proof.
    /// </param>
    /// <returns>The detail mesh, or an empty one if sampling was switched off.</returns>
    public static PolyMeshDetail Build(PolyMesh mesh, CompactHeightfield field, float sampleDistance, float maxError, int walkableHeight) {
        var detail = new PolyMeshDetail();

        if (sampleDistance <= 0) {
            return detail;
        }

        var hull = new List<Vector3>();
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        var samples = new List<Vector3>();

        for (var poly = 0; poly < mesh.PolyCount; poly++) {
            var offset = poly * mesh.MaxVerticesPerPoly;
            var count = PolyMesh.CountVertices(mesh.Polys, offset, mesh.MaxVerticesPerPoly);

            hull.Clear();

            for (var slot = 0; slot < count; slot++) {
                var vertex = mesh.Polys[offset + slot];

                hull.Add(new(mesh.Vertices[vertex * 3], mesh.Vertices[(vertex * 3) + 1], mesh.Vertices[(vertex * 3) + 2]));
            }

            vertices.Clear();
            vertices.AddRange(hull);

            triangles.Clear();

            // A fan. Every polygon out of the merge is convex, so every diagonal from the first
            // corner is inside it and there is nothing to decide.
            for (var slot = 2; slot < count; slot++) {
                triangles.Add(0);
                triangles.Add(slot - 1);
                triangles.Add(slot);
            }

            Legalise(vertices, triangles);

            RefineEdges(field, vertices, triangles, count, sampleDistance, maxError, walkableHeight);
            RefineInterior(field, vertices, triangles, hull, samples, sampleDistance, maxError, walkableHeight);

            // Counted in triangles, not in indices — the reader multiplies by three, and doing it
            // twice is a read three polygons further down the array.
            detail.Polys.Add(new(detail.Vertices.Count, vertices.Count - count, detail.Triangles.Count / 3, triangles.Count / 3));

            for (var index = count; index < vertices.Count; index++) {
                detail.Vertices.Add(vertices[index]);
            }

            detail.Triangles.AddRange(triangles);
        }

        return detail;
    }

    /// <summary>The height of the ground at a column, for a caller that already knows roughly where.</summary>
    /// <remarks>
    ///     <para>
    ///         A column can hold several walkable surfaces — a walkway over a floor — and picking the
    ///         wrong one would put a polygon's interior on a different storey from its corners. The
    ///         rule here is to take the surface nearest the polygon's own plane, and to refuse
    ///         anything further than an agent's height from it.
    ///     </para>
    ///     <para>
    ///         <b>That window is exact rather than generous.</b> The low-ceiling filter has already
    ///         removed every span with less than an agent's headroom above it, so two walkable
    ///         surfaces in one column are at least an agent's height apart — which means a window that
    ///         size can contain only one of them. Recast floods out from the polygon's own region
    ///         instead, which also handles a polygon whose plane is a poor guess; this handles the
    ///         case that a polygon's corners are on the surface it is describing, which is what the
    ///         bake guarantees.
    ///     </para>
    /// </remarks>
    static bool TryGroundHeight(CompactHeightfield field, int x, int z, float expected, int walkableHeight, out float height) {
        height = expected;

        if (x < 0 || z < 0 || x >= field.Width || z >= field.Depth) {
            return false;
        }

        ref var cell = ref field.Cells[x + (z * field.Width)];
        var best = (float)walkableHeight;
        var found = false;

        for (var index = cell.Index; index < cell.Index + cell.Count; index++) {
            if (field.Areas[index] == NavArea.Null) {
                continue;
            }

            var difference = MathF.Abs(field.Spans[index].Y - expected);

            if (difference < best) {
                best = difference;
                height = field.Spans[index].Y;
                found = true;
            }
        }

        return found;
    }

    /// <summary>Adds vertices along the polygon's edges where the straight edge misses the ground.</summary>
    /// <remarks>
    ///     <para>
    ///         Each edge is sampled from its lexicographically smaller end, at a fixed fraction of its
    ///         length. That is not tidiness: two polygons sharing an edge share its endpoints exactly,
    ///         so sampling it in a direction decided by the endpoints rather than by the winding makes
    ///         both of them test the same points against the same segment and reach the same answer.
    ///         Sampling in winding order would have the two disagree, and the two detail surfaces
    ///         would part company along the edge they share.
    ///     </para>
    ///     <para>
    ///         Every sample over the tolerance is taken, rather than the worst one and then a
    ///         recursion. Recursing is fewer vertices; taking them all is symmetric by construction,
    ///         which is the property that matters here.
    ///     </para>
    /// </remarks>
    static void RefineEdges(
        CompactHeightfield field,
        List<Vector3> vertices,
        List<int> triangles,
        int hullCount,
        float sampleDistance,
        float maxError,
        int walkableHeight
    ) {
        for (var edge = 0; edge < hullCount; edge++) {
            var first = vertices[edge];
            var second = vertices[(edge + 1) % hullCount];

            var (from, to) = first.X < second.X || (first.X == second.X && first.Z < second.Z)
                ? (first, second)
                : (second, first);

            var length = MathF.Sqrt(((to.X - from.X) * (to.X - from.X)) + ((to.Z - from.Z) * (to.Z - from.Z)));
            var steps = (int)MathF.Ceiling(length / sampleDistance);

            if (steps < 2) {
                continue;
            }

            for (var step = 1; step < steps; step++) {
                if (vertices.Count - hullCount >= MaxEdgeVertices) {
                    return;
                }

                var fraction = step / (float)steps;

                var point = new Vector3(
                    from.X + ((to.X - from.X) * fraction),
                    from.Y + ((to.Y - from.Y) * fraction),
                    from.Z + ((to.Z - from.Z) * fraction)
                );

                if (!TryGroundHeight(field, (int)MathF.Floor(point.X), (int)MathF.Floor(point.Z), point.Y, walkableHeight, out var ground)) {
                    continue;
                }

                if (MathF.Abs(ground - point.Y) <= maxError) {
                    continue;
                }

                SplitEdge(vertices, triangles, new(point.X, ground, point.Z));
                Legalise(vertices, triangles);
            }
        }
    }

    /// <summary>Adds vertices inside the polygon, worst first, until nothing is out by more than the tolerance.</summary>
    static void RefineInterior(
        CompactHeightfield field,
        List<Vector3> vertices,
        List<int> triangles,
        List<Vector3> hull,
        List<Vector3> samples,
        float sampleDistance,
        float maxError,
        int walkableHeight
    ) {
        samples.Clear();

        var minimumX = float.MaxValue;
        var minimumZ = float.MaxValue;
        var maximumX = float.MinValue;
        var maximumZ = float.MinValue;

        foreach (var corner in hull) {
            minimumX = MathF.Min(minimumX, corner.X);
            minimumZ = MathF.Min(minimumZ, corner.Z);
            maximumX = MathF.Max(maximumX, corner.X);
            maximumZ = MathF.Max(maximumZ, corner.Z);
        }

        // Anchored to a multiple of the spacing rather than to the polygon's own corner, so that two
        // polygons over the same ground sample the same places and describe it the same way.
        for (var z = MathF.Ceiling(minimumZ / sampleDistance) * sampleDistance; z <= maximumZ; z += sampleDistance) {
            for (var x = MathF.Ceiling(minimumX / sampleDistance) * sampleDistance; x <= maximumX; x += sampleDistance) {
                var point = new Vector3(x, 0, z);

                if (!Contains(hull, point)) {
                    continue;
                }

                if (TryGroundHeight(field, (int)MathF.Floor(x), (int)MathF.Floor(z), Height(vertices, triangles, point), walkableHeight, out var ground)) {
                    samples.Add(new(x, ground, z));
                }
            }
        }

        var budget = MaxInteriorVertices;

        while (budget-- > 0) {
            var worst = -1;
            var worstError = maxError;

            for (var index = 0; index < samples.Count; index++) {
                var current = Height(vertices, triangles, samples[index]);

                if (float.IsNaN(current)) {
                    continue;
                }

                var error = MathF.Abs(samples[index].Y - current);

                if (error > worstError) {
                    worstError = error;
                    worst = index;
                }
            }

            if (worst < 0) {
                return;
            }

            var sample = samples[worst];
            samples.RemoveAt(worst);
            SplitInterior(vertices, triangles, sample);
            Legalise(vertices, triangles);
        }
    }

    /// <summary>Replaces the triangle a point falls in with three that meet at it.</summary>
    static void SplitInterior(List<Vector3> vertices, List<int> triangles, Vector3 point) {
        for (var triangle = 0; triangle < triangles.Count; triangle += 3) {
            var a = vertices[triangles[triangle]];
            var b = vertices[triangles[triangle + 1]];
            var c = vertices[triangles[triangle + 2]];

            if (Side(a, b, point) < 0 || Side(b, c, point) < 0 || Side(c, a, point) < 0) {
                continue;
            }

            var (first, second, third) = (triangles[triangle], triangles[triangle + 1], triangles[triangle + 2]);
            var added = vertices.Count;
            vertices.Add(point);

            triangles[triangle + 2] = added;
            triangles.AddRange([second, third, added, third, first, added]);

            return;
        }
    }

    /// <summary>Replaces the one triangle owning the boundary edge a point sits on with two.</summary>
    /// <remarks>
    ///     Only a boundary edge is ever split. An interior edge belongs to two triangles and splitting
    ///     it in one of them would leave the other with a vertex in the middle of an edge it does not
    ///     know about — a crack inside a single polygon's detail, which is the one place point
    ///     location can fall through.
    /// </remarks>
    static void SplitEdge(List<Vector3> vertices, List<int> triangles, Vector3 point) {
        for (var triangle = 0; triangle < triangles.Count; triangle += 3) {
            for (var edge = 0; edge < 3; edge++) {
                var from = triangles[triangle + edge];
                var to = triangles[triangle + ((edge + 1) % 3)];

                if (!IsBoundary(triangles, from, to) || !OnSegment(vertices[from], vertices[to], point)) {
                    continue;
                }

                var opposite = triangles[triangle + ((edge + 2) % 3)];
                var added = vertices.Count;
                vertices.Add(point);

                // The triangle keeps its winding: the slot that held the far end of the split edge
                // now holds the new vertex, and the other half is appended.
                triangles[triangle + ((edge + 1) % 3)] = added;
                triangles.AddRange([added, to, opposite]);

                return;
            }
        }
    }

    /// <summary>Whether an edge is on the polygon's outline: nothing traverses it the other way.</summary>
    static bool IsBoundary(List<int> triangles, int from, int to) {
        for (var triangle = 0; triangle < triangles.Count; triangle += 3) {
            for (var edge = 0; edge < 3; edge++) {
                if (triangles[triangle + edge] == to && triangles[triangle + ((edge + 1) % 3)] == from) {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>The height of the detail surface over a point, or NaN if it is not over it.</summary>
    static float Height(List<Vector3> vertices, List<int> triangles, Vector3 point) {
        for (var triangle = 0; triangle < triangles.Count; triangle += 3) {
            var a = vertices[triangles[triangle]];
            var b = vertices[triangles[triangle + 1]];
            var c = vertices[triangles[triangle + 2]];

            if (Side(a, b, point) < 0 || Side(b, c, point) < 0 || Side(c, a, point) < 0) {
                continue;
            }

            var area = Side(a, b, c);

            if (MathF.Abs(area) < 1e-6f) {
                continue;
            }

            var alpha = Side(b, c, point) / area;
            var beta = Side(c, a, point) / area;
            var gamma = 1f - alpha - beta;

            return (a.Y * alpha) + (b.Y * beta) + (c.Y * gamma);
        }

        return float.NaN;
    }

    /// <summary>Flips interior edges until every triangle's circumcircle is empty.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is not a quality nicety, and finding that out took a measurement.</b> Splitting
    ///         a triangle at an interior point leaves all three of its long edges in place, so a fan
    ///         over a large polygon keeps its enormous spokes however many points are inserted. The
    ///         result was a detail mesh that was exact at every point it sampled and, halfway between
    ///         two of them, still interpolating between two corners of the polygon a metre out — the
    ///         middle of a hill read as 0.9 m below where it is, with the error concentrated exactly
    ///         along the fan's diagonals.
    ///     </para>
    ///     <para>
    ///         Lawson's flip: an edge shared by two triangles is illegal when the far vertex of one
    ///         lies inside the other's circumcircle, and flipping it to the other diagonal of the quad
    ///         is always an improvement. Sweeping until nothing is illegal gives the Delaunay
    ///         triangulation of the points, whose triangles are as close to equilateral as the points
    ///         allow — which is what makes interpolation over them mean anything.
    ///     </para>
    ///     <para>
    ///         Only interior edges are ever flipped: an edge with no triangle on the far side is the
    ///         polygon's own outline and moving it would change the polygon.
    ///     </para>
    /// </remarks>
    static void Legalise(List<Vector3> vertices, List<int> triangles) {
        for (var sweep = 0; sweep < MaxFlipSweeps; sweep++) {
            var flipped = false;

            for (var triangle = 0; triangle < triangles.Count; triangle += 3) {
                for (var slot = 0; slot < 3; slot++) {
                    var apex = triangles[triangle + slot];
                    var from = triangles[triangle + ((slot + 1) % 3)];
                    var to = triangles[triangle + ((slot + 2) % 3)];

                    var other = FindEdge(triangles, to, from, out var otherSlot);

                    if (other < 0) {
                        continue;
                    }

                    var far = triangles[other + ((otherSlot + 2) % 3)];

                    if (!InCircumcircle(vertices[apex], vertices[from], vertices[to], vertices[far])) {
                        continue;
                    }

                    // The quadrilateral has to be convex, or the flip produces two triangles that
                    // overlap instead of two that tile it.
                    if (Side(vertices[apex], vertices[from], vertices[far]) <= 0 ||
                        Side(vertices[apex], vertices[far], vertices[to]) <= 0) {
                        continue;
                    }

                    triangles[triangle] = apex;
                    triangles[triangle + 1] = from;
                    triangles[triangle + 2] = far;

                    triangles[other] = apex;
                    triangles[other + 1] = far;
                    triangles[other + 2] = to;

                    flipped = true;

                    break;
                }
            }

            if (!flipped) {
                return;
            }
        }
    }

    /// <summary>The triangle traversing an edge in the given direction, or -1.</summary>
    static int FindEdge(List<int> triangles, int from, int to, out int slot) {
        for (var triangle = 0; triangle < triangles.Count; triangle += 3) {
            for (slot = 0; slot < 3; slot++) {
                if (triangles[triangle + slot] == from && triangles[triangle + ((slot + 1) % 3)] == to) {
                    return triangle;
                }
            }
        }

        slot = -1;

        return -1;
    }

    /// <summary>Whether a point is inside the circle through three counter-clockwise ones.</summary>
    /// <remarks>
    ///     In double precision, because the determinant is a difference of fourth powers of the
    ///     coordinates and the coordinates are voxel indices in the hundreds. In single precision the
    ///     sign of a nearly-cocircular quadrilateral is noise, and a sign that flickers is two
    ///     triangles that flip each other back and forth until the sweep limit.
    /// </remarks>
    static bool InCircumcircle(Vector3 a, Vector3 b, Vector3 c, Vector3 point) {
        double ax = a.X - point.X;
        double az = a.Z - point.Z;
        double bx = b.X - point.X;
        double bz = b.Z - point.Z;
        double cx = c.X - point.X;
        double cz = c.Z - point.Z;

        var determinant = (((ax * ax) + (az * az)) * ((bx * cz) - (cx * bz)))
            - (((bx * bx) + (bz * bz)) * ((ax * cz) - (cx * az)))
            + (((cx * cx) + (cz * cz)) * ((ax * bz) - (bx * az)));

        return determinant > 1e-9;
    }

    /// <summary>Twice the signed area of a triangle in XZ. Positive when it turns counter-clockwise.</summary>
    static float Side(Vector3 a, Vector3 b, Vector3 c) =>
        ((b.X - a.X) * (c.Z - a.Z)) - ((c.X - a.X) * (b.Z - a.Z));

    static bool Contains(List<Vector3> hull, Vector3 point) {
        for (var index = 0; index < hull.Count; index++) {
            if (Side(hull[index], hull[(index + 1) % hull.Count], point) <= 0) {
                return false;
            }
        }

        return true;
    }

    static bool OnSegment(Vector3 from, Vector3 to, Vector3 point) {
        if (MathF.Abs(Side(from, to, point)) > 1e-3f) {
            return false;
        }

        var along = ((point.X - from.X) * (to.X - from.X)) + ((point.Z - from.Z) * (to.Z - from.Z));
        var length = ((to.X - from.X) * (to.X - from.X)) + ((to.Z - from.Z) * (to.Z - from.Z));

        return along > 1e-4f && along < length - 1e-4f;
    }
}
