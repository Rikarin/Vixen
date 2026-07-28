// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Navigation.Baking;

/// <summary>
///     Convex polygons in voxel coordinates, with their adjacency — the last stage before a tile.
/// </summary>
/// <remarks>
///     <para>
///         Contours become triangles, and triangles become polygons of up to six vertices by merging
///         neighbours whose union is still convex. Convexity is the property everything downstream
///         relies on: a straight line between two points of a convex polygon stays inside it, which is
///         what makes the funnel correct and what makes a raycast one half-plane clip per polygon.
///     </para>
///     <para>
///         Merging is worth the trouble because polygon count is what a path costs. A room that is
///         four hundred triangles is a search over four hundred nodes; the same room as forty hexagons
///         is a search over forty, and the funnel across it has fewer corners to consider.
///     </para>
/// </remarks>
internal sealed class PolyMesh {
    /// <summary>Three ints per vertex: X, Y and Z in voxels.</summary>
    public List<int> Vertices { get; } = [];

    /// <summary><see cref="MaxVerticesPerPoly" /> ints per polygon: vertex indices, then -1 padding.</summary>
    public List<int> Polys { get; } = [];

    /// <summary>Parallel to <see cref="Polys" />: the polygon across each edge, or -1.</summary>
    public List<int> Neighbours { get; } = [];

    /// <summary>One region id per polygon.</summary>
    public List<ushort> Regions { get; } = [];

    /// <summary>One area id per polygon.</summary>
    public List<byte> Areas { get; } = [];

    /// <summary>How many vertices a polygon may have.</summary>
    public int MaxVerticesPerPoly { get; private set; } = NavMesh.MaxVerticesPerPoly;

    /// <summary>How many polygons there are.</summary>
    public int PolyCount => Polys.Count / MaxVerticesPerPoly;

    /// <summary>How many vertices there are.</summary>
    public int VertexCount => Vertices.Count / 3;

    /// <summary>Turns contours into convex polygons.</summary>
    /// <param name="contours">The simplified outlines.</param>
    /// <param name="maxVerticesPerPoly">The most vertices a polygon may have.</param>
    /// <returns>The polygon mesh.</returns>
    public static PolyMesh Build(ContourSet contours, int maxVerticesPerPoly) {
        var mesh = new PolyMesh { MaxVerticesPerPoly = maxVerticesPerPoly };
        var buckets = new Dictionary<long, List<int>>();
        var indices = new List<int>();
        var triangles = new List<int>();
        var polys = new List<int>();

        foreach (var contour in contours.Contours) {
            var count = contour.VertexCount;

            if (count < 3) {
                continue;
            }

            indices.Clear();

            // A contour can arrive with a vertex repeated — two corners of the voxel outline that
            // simplification pulled onto the same column. Ear clipping has no answer for a
            // zero-length edge, so they go before it is asked.
            for (var index = 0; index < count; index++) {
                var previous = indices.Count > 0 ? indices[^1] : -1;

                if (previous >= 0 &&
                    contour.Vertices[index * 4] == contour.Vertices[previous * 4] &&
                    contour.Vertices[(index * 4) + 2] == contour.Vertices[(previous * 4) + 2]) {
                    continue;
                }

                indices.Add(index);
            }

            if (indices.Count > 2 &&
                contour.Vertices[indices[0] * 4] == contour.Vertices[indices[^1] * 4] &&
                contour.Vertices[(indices[0] * 4) + 2] == contour.Vertices[(indices[^1] * 4) + 2]) {
                indices.RemoveAt(indices.Count - 1);
            }

            if (indices.Count < 3) {
                continue;
            }

            // Counter-clockwise from here on, whichever way the trace happened to run. The funnel,
            // the segment clip and the convexity test in the merge all read the winding, and this is
            // the one place it is decided.
            if (SignedArea(contour, indices) < 0) {
                indices.Reverse();
            }

            triangles.Clear();

            if (!Triangulate(contour, indices, triangles)) {
                continue;
            }

            polys.Clear();

            foreach (var vertex in triangles) {
                polys.Add(vertex);
            }

            Merge(contour, polys, maxVerticesPerPoly);

            for (var poly = 0; poly < polys.Count; poly += maxVerticesPerPoly) {
                for (var slot = 0; slot < maxVerticesPerPoly; slot++) {
                    var local = polys[poly + slot];
                    mesh.Polys.Add(local < 0 ? -1 : mesh.AddVertex(contour, local, buckets));
                    mesh.Neighbours.Add(-1);
                }

                mesh.Regions.Add(contour.Region);
                mesh.Areas.Add(contour.Area);
            }
        }

        mesh.BuildAdjacency();

        return mesh;
    }

    /// <summary>How many vertices a polygon actually has, before the -1 padding.</summary>
    /// <param name="polys">The polygon array.</param>
    /// <param name="offset">Where the polygon starts.</param>
    /// <param name="maxVerticesPerPoly">The stride.</param>
    /// <returns>The count.</returns>
    public static int CountVertices(List<int> polys, int offset, int maxVerticesPerPoly) {
        for (var slot = 0; slot < maxVerticesPerPoly; slot++) {
            if (polys[offset + slot] < 0) {
                return slot;
            }
        }

        return maxVerticesPerPoly;
    }

    int AddVertex(Contour contour, int local, Dictionary<long, List<int>> buckets) {
        var x = contour.Vertices[local * 4];
        var y = contour.Vertices[(local * 4) + 1];
        var z = contour.Vertices[(local * 4) + 2];
        var key = ((long)x << 32) | (uint)z;

        if (!buckets.TryGetValue(key, out var bucket)) {
            bucket = [];
            buckets[key] = bucket;
        }

        // Matched on the column and a loose height, which is Recast's rule and is not a shortcut: two
        // regions that trace the same corner can compute its height from different sets of spans, and
        // a vertex that failed to match would leave two polygons meeting along an edge that shares no
        // endpoints — a crack the adjacency pass cannot see and a path can fall through.
        //
        // Two voxels, expressed in the sub-voxel units heights are traced in. Widening the tolerance
        // with the unit is the point: the corners being matched are the same corners as before, and
        // they now differ by fractions where they used to be equal.
        foreach (var candidate in bucket) {
            if (Math.Abs(Vertices[(candidate * 3) + 1] - y) <= 2 * HeightfieldSpan.DropScale) {
                return candidate;
            }
        }

        Vertices.Add(x);
        Vertices.Add(y);
        Vertices.Add(z);

        var index = (Vertices.Count / 3) - 1;
        bucket.Add(index);

        return index;
    }

    /// <summary>Matches every pair of polygon edges that run between the same two vertices.</summary>
    /// <remarks>
    ///     Two polygons are neighbours when they share an edge, and — because they are both wound
    ///     counter-clockwise — they traverse it in opposite directions. Keying on the pair of vertices
    ///     in a fixed order finds them in one pass without comparing every polygon to every other.
    /// </remarks>
    void BuildAdjacency() {
        var edges = new Dictionary<long, (int Poly, int Edge)>();

        for (var poly = 0; poly < PolyCount; poly++) {
            var offset = poly * MaxVerticesPerPoly;
            var count = CountVertices(Polys, offset, MaxVerticesPerPoly);

            for (var edge = 0; edge < count; edge++) {
                var from = Polys[offset + edge];
                var to = Polys[offset + ((edge + 1) % count)];
                var key = from < to ? ((long)from << 32) | (uint)to : ((long)to << 32) | (uint)from;

                if (edges.TryGetValue(key, out var other)) {
                    Neighbours[(other.Poly * MaxVerticesPerPoly) + other.Edge] = poly;
                    Neighbours[offset + edge] = other.Poly;
                    edges.Remove(key);
                } else {
                    edges[key] = (poly, edge);
                }
            }
        }
    }

    static int VertexX(Contour contour, int index) => contour.Vertices[index * 4];

    static int VertexZ(Contour contour, int index) => contour.Vertices[(index * 4) + 2];

    static long SignedArea(Contour contour, List<int> indices) {
        long total = 0;

        for (int index = 0, previous = indices.Count - 1; index < indices.Count; previous = index++) {
            total += ((long)VertexX(contour, indices[previous]) * VertexZ(contour, indices[index]))
                - ((long)VertexX(contour, indices[index]) * VertexZ(contour, indices[previous]));
        }

        return total;
    }

    /// <summary>Twice the signed area of a triangle. Positive when it turns counter-clockwise.</summary>
    static long Area2(Contour contour, int a, int b, int c) =>
        ((long)(VertexX(contour, b) - VertexX(contour, a)) * (VertexZ(contour, c) - VertexZ(contour, a)))
        - ((long)(VertexX(contour, c) - VertexX(contour, a)) * (VertexZ(contour, b) - VertexZ(contour, a)));

    /// <summary>Ear clipping, taking the shortest diagonal available each time.</summary>
    /// <remarks>
    ///     <para>
    ///         The shortest diagonal rather than the first ear found, because the merge step that
    ///         follows can only join triangles that share an edge — long slivers give it nothing to
    ///         work with, and the mesh keeps the shape of the order the ears happened to be clipped
    ///         in.
    ///     </para>
    ///     <para>
    ///         <b>A contour that has had a hole merged into it is not a simple polygon</b>, and this
    ///         is where that shows. The bridge is a slit of zero width, so the polygon touches itself
    ///         along two coincident edges and at two pairs of coincident vertices. The strict
    ///         diagonal test calls every ear near the slit blocked — correctly, by the letter of it —
    ///         and would leave the whole region untriangulated. So there is a second pass with the
    ///         test loosened to <i>proper</i> crossings only: touching is allowed, cutting is not.
    ///         Recast reaches for the same fallback for the same reason.
    ///     </para>
    ///     <para>
    ///         Even that can fail, on a contour that simplification has folded over itself. It
    ///         returns false rather than producing nonsense: one region is dropped and the rest of
    ///         the tile is still correct.
    ///     </para>
    /// </remarks>
    static bool Triangulate(Contour contour, List<int> indices, List<int> triangles) {
        var working = new List<int>(indices);

        while (working.Count > 3) {
            var best = FindEar(contour, working, strict: true);

            if (best < 0) {
                best = FindEar(contour, working, strict: false);
            }

            if (best < 0) {
                return false;
            }

            var earPrevious = (best + working.Count - 1) % working.Count;
            var earNext = (best + 1) % working.Count;

            triangles.Add(working[earPrevious]);
            triangles.Add(working[best]);
            triangles.Add(working[earNext]);
            working.RemoveAt(best);
        }

        triangles.Add(working[0]);
        triangles.Add(working[1]);
        triangles.Add(working[2]);

        return true;
    }

    /// <summary>The vertex whose removal leaves the shortest new edge, or -1 if none may be removed.</summary>
    static int FindEar(Contour contour, List<int> working, bool strict) {
        var best = -1;
        var bestLength = long.MaxValue;

        for (var index = 0; index < working.Count; index++) {
            var previous = (index + working.Count - 1) % working.Count;
            var next = (index + 1) % working.Count;

            if (!IsDiagonal(contour, working, previous, next, strict)) {
                continue;
            }

            long dx = VertexX(contour, working[next]) - VertexX(contour, working[previous]);
            long dz = VertexZ(contour, working[next]) - VertexZ(contour, working[previous]);
            var length = (dx * dx) + (dz * dz);

            if (length < bestLength) {
                bestLength = length;
                best = index;
            }
        }

        return best;
    }

    /// <summary>Whether the segment between two vertices of a polygon stays inside it.</summary>
    static bool IsDiagonal(Contour contour, List<int> working, int from, int to, bool strict) =>
        IsInCone(contour, working, from, to, strict) && IsClear(contour, working, from, to, strict);

    /// <summary>Whether a diagonal leaves its start vertex through the polygon's interior.</summary>
    static bool IsInCone(Contour contour, List<int> working, int from, int to, bool strict) {
        var a = working[from];
        var b = working[to];
        var before = working[(from + working.Count - 1) % working.Count];
        var after = working[(from + 1) % working.Count];

        // A convex corner opens outwards, so the diagonal has to be inside both of its edges. A
        // reflex corner opens inwards, and the test is the negation of the cone it does not open into.
        if (Area2(contour, a, after, before) >= 0) {
            return strict
                ? Area2(contour, a, b, before) > 0 && Area2(contour, b, a, after) > 0
                : Area2(contour, a, b, before) >= 0 && Area2(contour, b, a, after) >= 0;
        }

        return !(Area2(contour, a, b, after) >= 0 && Area2(contour, b, a, before) >= 0);
    }

    /// <summary>Whether a diagonal crosses any edge of the polygon.</summary>
    static bool IsClear(Contour contour, List<int> working, int from, int to, bool strict) {
        var a = working[from];
        var b = working[to];

        for (var index = 0; index < working.Count; index++) {
            var next = (index + 1) % working.Count;

            if (index == from || index == to || next == from || next == to) {
                continue;
            }

            // By position, not by index. A merged contour holds the same corner twice — once for
            // each side of the bridge — and an edge that merely ends where the diagonal does is
            // touching it, whichever of the two copies it was written against.
            if (Coincident(contour, a, working[index]) || Coincident(contour, b, working[index]) ||
                Coincident(contour, a, working[next]) || Coincident(contour, b, working[next])) {
                continue;
            }

            var crosses = strict
                ? Intersects(contour, a, b, working[index], working[next])
                : IntersectsProperly(contour, a, b, working[index], working[next]);

            if (crosses) {
                return false;
            }
        }

        return true;
    }

    static bool Coincident(Contour contour, int a, int b) =>
        VertexX(contour, a) == VertexX(contour, b) && VertexZ(contour, a) == VertexZ(contour, b);

    /// <summary>Whether two segments cross at a point interior to both. Touching does not count.</summary>
    static bool IntersectsProperly(Contour contour, int a, int b, int c, int d) {
        if (Collinear(contour, a, b, c) || Collinear(contour, a, b, d) ||
            Collinear(contour, c, d, a) || Collinear(contour, c, d, b)) {
            return false;
        }

        return Area2(contour, a, b, c) > 0 != Area2(contour, a, b, d) > 0 &&
            Area2(contour, c, d, a) > 0 != Area2(contour, c, d, b) > 0;
    }

    static bool Intersects(Contour contour, int a, int b, int c, int d) {
        if (Collinear(contour, a, b, c) || Collinear(contour, a, b, d) ||
            Collinear(contour, c, d, a) || Collinear(contour, c, d, b)) {
            return Between(contour, a, b, c) || Between(contour, a, b, d) ||
                Between(contour, c, d, a) || Between(contour, c, d, b);
        }

        return Area2(contour, a, b, c) > 0 != Area2(contour, a, b, d) > 0 &&
            Area2(contour, c, d, a) > 0 != Area2(contour, c, d, b) > 0;
    }

    static bool Collinear(Contour contour, int a, int b, int c) => Area2(contour, a, b, c) == 0;

    static bool Between(Contour contour, int a, int b, int c) {
        if (!Collinear(contour, a, b, c)) {
            return false;
        }

        if (VertexX(contour, a) != VertexX(contour, b)) {
            return (VertexX(contour, a) <= VertexX(contour, c) && VertexX(contour, c) <= VertexX(contour, b)) ||
                (VertexX(contour, a) >= VertexX(contour, c) && VertexX(contour, c) >= VertexX(contour, b));
        }

        return (VertexZ(contour, a) <= VertexZ(contour, c) && VertexZ(contour, c) <= VertexZ(contour, b)) ||
            (VertexZ(contour, a) >= VertexZ(contour, c) && VertexZ(contour, c) >= VertexZ(contour, b));
    }

    /// <summary>Joins neighbouring polygons while the result stays convex and small enough.</summary>
    static void Merge(Contour contour, List<int> triangles, int maxVerticesPerPoly) {
        // Repack the triangles into the padded stride the merge and everything after it work in.
        var polys = new List<int>();

        for (var triangle = 0; triangle < triangles.Count; triangle += 3) {
            polys.Add(triangles[triangle]);
            polys.Add(triangles[triangle + 1]);
            polys.Add(triangles[triangle + 2]);

            for (var pad = 3; pad < maxVerticesPerPoly; pad++) {
                polys.Add(-1);
            }
        }

        if (maxVerticesPerPoly > 3) {
            while (true) {
                var bestValue = 0L;
                var bestFirst = -1;
                var bestSecond = -1;
                var bestFirstEdge = -1;
                var bestSecondEdge = -1;

                for (var first = 0; first < polys.Count; first += maxVerticesPerPoly) {
                    for (var second = first + maxVerticesPerPoly; second < polys.Count; second += maxVerticesPerPoly) {
                        var value = MergeValue(contour, polys, first, second, maxVerticesPerPoly, out var firstEdge, out var secondEdge);

                        if (value > bestValue) {
                            bestValue = value;
                            bestFirst = first;
                            bestSecond = second;
                            bestFirstEdge = firstEdge;
                            bestSecondEdge = secondEdge;
                        }
                    }
                }

                if (bestValue <= 0) {
                    break;
                }

                MergeInto(polys, bestFirst, bestSecond, bestFirstEdge, bestSecondEdge, maxVerticesPerPoly);
                polys.RemoveRange(bestSecond, maxVerticesPerPoly);
            }
        }

        triangles.Clear();
        triangles.AddRange(polys);
    }

    /// <summary>
    ///     How good a merge of two polygons would be: the squared length of the edge they share, or
    ///     zero if they cannot be merged.
    /// </summary>
    /// <remarks>
    ///     The longest shared edge first, because merging across it removes the most boundary. The
    ///     convexity test is at the two vertices where the shared edge used to end, which are the only
    ///     corners the merge can turn concave.
    /// </remarks>
    static long MergeValue(
        Contour contour,
        List<int> polys,
        int first,
        int second,
        int maxVerticesPerPoly,
        out int firstEdge,
        out int secondEdge
    ) {
        firstEdge = -1;
        secondEdge = -1;

        var firstCount = CountVertices(polys, first, maxVerticesPerPoly);
        var secondCount = CountVertices(polys, second, maxVerticesPerPoly);

        if (firstCount + secondCount - 2 > maxVerticesPerPoly) {
            return 0;
        }

        for (var a = 0; a < firstCount && firstEdge < 0; a++) {
            var a0 = polys[first + a];
            var a1 = polys[first + ((a + 1) % firstCount)];

            for (var b = 0; b < secondCount; b++) {
                var b0 = polys[second + b];
                var b1 = polys[second + ((b + 1) % secondCount)];

                if ((a0 == b0 && a1 == b1) || (a0 == b1 && a1 == b0)) {
                    firstEdge = a;
                    secondEdge = b;

                    break;
                }
            }
        }

        if (firstEdge < 0) {
            return 0;
        }

        // The merged polygon runs from the far end of the shared edge round the first polygon, then
        // round the second. Only the two vertices the shared edge used to end at can have turned
        // concave — every other corner is untouched — and those are the two corners tested here.
        var firstCorner = polys[first + ((firstEdge + 1) % firstCount)];
        var firstBefore = polys[second + ((secondEdge + secondCount - 1) % secondCount)];
        var firstAfter = polys[first + ((firstEdge + 2) % firstCount)];

        if (Area2(contour, firstBefore, firstCorner, firstAfter) <= 0) {
            return 0;
        }

        var secondCorner = polys[second + ((secondEdge + 1) % secondCount)];
        var secondBefore = polys[first + ((firstEdge + firstCount - 1) % firstCount)];
        var secondAfter = polys[second + ((secondEdge + 2) % secondCount)];

        if (Area2(contour, secondBefore, secondCorner, secondAfter) <= 0) {
            return 0;
        }

        var start = polys[first + firstEdge];
        var end = polys[first + ((firstEdge + 1) % firstCount)];

        long dx = VertexX(contour, start) - VertexX(contour, end);
        long dz = VertexZ(contour, start) - VertexZ(contour, end);

        return Math.Max(1, (dx * dx) + (dz * dz));
    }

    static void MergeInto(List<int> polys, int first, int second, int firstEdge, int secondEdge, int maxVerticesPerPoly) {
        var firstCount = CountVertices(polys, first, maxVerticesPerPoly);
        var secondCount = CountVertices(polys, second, maxVerticesPerPoly);

        Span<int> merged = stackalloc int[NavMesh.MaxVerticesPerPoly];
        merged.Fill(-1);

        var count = 0;

        for (var index = 0; index < firstCount - 1; index++) {
            merged[count++] = polys[first + ((firstEdge + 1 + index) % firstCount)];
        }

        for (var index = 0; index < secondCount - 1; index++) {
            merged[count++] = polys[second + ((secondEdge + 1 + index) % secondCount)];
        }

        for (var slot = 0; slot < maxVerticesPerPoly; slot++) {
            polys[first + slot] = slot < merged.Length ? merged[slot] : -1;
        }
    }
}
