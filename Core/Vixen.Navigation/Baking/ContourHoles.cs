// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Navigation.Baking;

/// <summary>
///     Cuts a region's holes into its outer outline, so that what reaches the polygoniser is a simple
///     polygon.
/// </summary>
/// <remarks>
///     <para>
///         A watershed region can grow all the way round a pillar and meet itself. The tracer then
///         emits two outlines for it: the outside, and — wound the other way, because the tracer keeps
///         the region on the same hand throughout — the pillar. Nothing downstream knows they belong
///         together. Ear clipping would take the second one on its own terms and produce a solid
///         polygon over the pillar, which is a navmesh that says an agent can walk through it.
///     </para>
///     <para>
///         The fix is the standard one and it is older than any of this: join the hole to the outline
///         with a <b>bridge</b> — a diagonal traversed in both directions — turning the annulus into a
///         single ring with a zero-width slit in it. The slit's two edges are geometrically identical
///         and the polygon is degenerate along them, which is exactly why it works: ear clipping sees
///         one closed loop, and the two polygons either side of the slit end up adjacent, so no path
///         is blocked by it.
///     </para>
///     <para>
///         <b>Finding the bridge is the whole difficulty.</b> It must leave the hole through the
///         region's interior, arrive at the outline through the outline's interior, and cross neither
///         outline nor any hole not yet merged. The search is: from the hole's leftmost vertex, take
///         every outline vertex that vertex can see, shortest first, and keep the first that crosses
///         nothing. If every candidate is blocked, try the next vertex of the hole.
///     </para>
///     <para>
///         Recast's <c>mergeRegionHoles</c>, re-derived, with the winding turned round to the
///         counter-clockwise convention the rest of this assembly uses.
///     </para>
/// </remarks>
internal static class ContourHoles {
    /// <summary>Merges every region's holes into its outline, in place.</summary>
    /// <param name="contours">The traced and simplified outlines. Holes are removed from the list.</param>
    public static void Merge(List<Contour> contours) {
        var byRegion = new Dictionary<ushort, List<int>>();

        for (var index = 0; index < contours.Count; index++) {
            if (!byRegion.TryGetValue(contours[index].Region, out var group)) {
                group = [];
                byRegion[contours[index].Region] = group;
            }

            group.Add(index);
        }

        var merged = new HashSet<int>();

        foreach (var group in byRegion.Values) {
            if (group.Count < 2) {
                continue;
            }

            // The outer outline is the one enclosing the most: it contains the others by definition.
            var outer = group[0];

            foreach (var index in group) {
                if (Math.Abs(SignedArea(contours[index].Vertices)) > Math.Abs(SignedArea(contours[outer].Vertices))) {
                    outer = index;
                }
            }

            var sign = Math.Sign(SignedArea(contours[outer].Vertices));
            var holes = new List<int>();

            foreach (var index in group) {
                // Wound the same way as the outline and not inside it: two separate pieces of one
                // region rather than a hole in it, which a region spanning two storeys produces. They
                // are already simple polygons and are left alone.
                if (index != outer && Math.Sign(SignedArea(contours[index].Vertices)) != sign) {
                    holes.Add(index);
                }
            }

            if (holes.Count == 0) {
                continue;
            }

            contours[outer] = Merge(contours, outer, holes);
            merged.UnionWith(holes);
        }

        if (merged.Count == 0) {
            return;
        }

        for (var index = contours.Count - 1; index >= 0; index--) {
            if (merged.Contains(index)) {
                contours.RemoveAt(index);
            }
        }
    }

    static Contour Merge(List<Contour> contours, int outerIndex, List<int> holeIndices) {
        var outline = Wind(contours[outerIndex].Vertices, counterClockwise: true);

        var holes = new List<int[]>(holeIndices.Count);

        foreach (var index in holeIndices) {
            holes.Add(Wind(contours[index].Vertices, counterClockwise: false));
        }

        // Left to right. A hole is bridged to the outline on its left-hand side, so merging the
        // leftmost first means every later bridge has the whole of the outline still available to
        // it — merging right to left would step over holes that are not yet part of anything.
        holes.Sort(CompareByLeftmost);

        for (var index = 0; index < holes.Count; index++) {
            var hole = holes[index];
            var count = hole.Length / 4;
            var start = Leftmost(hole);
            var outlineVertex = -1;
            var holeVertex = start;

            for (var attempt = 0; attempt < count && outlineVertex < 0; attempt++) {
                holeVertex = (start + attempt) % count;
                outlineVertex = FindBridge(outline, hole, holeVertex, holes, index);
            }

            if (outlineVertex < 0) {
                // Every vertex of the hole is boxed in. This should not happen for a contour set that
                // came out of the tracer, and the honest failure is to leave the hole out rather than
                // to splice a bridge that crosses something: an unreachable pillar-shaped gap in the
                // mesh is a navigation bug, and a polygon over the pillar is a physics one.
                continue;
            }

            outline = Splice(outline, hole, outlineVertex, holeVertex);
        }

        return new() { Vertices = outline, Region = contours[outerIndex].Region, Area = contours[outerIndex].Area };
    }

    /// <summary>The outline vertex a hole vertex should bridge to, or -1 if it can reach none.</summary>
    static int FindBridge(int[] outline, int[] hole, int holeVertex, List<int[]> holes, int firstUnmerged) {
        var outlineCount = outline.Length / 4;
        var cornerX = hole[holeVertex * 4];
        var cornerZ = hole[(holeVertex * 4) + 2];

        var candidates = new List<(int Vertex, long Distance)>();

        for (var vertex = 0; vertex < outlineCount; vertex++) {
            if (!InCone(outline, vertex, cornerX, cornerZ)) {
                continue;
            }

            long dx = outline[vertex * 4] - cornerX;
            long dz = outline[(vertex * 4) + 2] - cornerZ;

            candidates.Add((vertex, (dx * dx) + (dz * dz)));
        }

        // Shortest first: a long bridge is more likely to cross something, and a short one leaves the
        // two slivers either side of the slit closer to the shape the ear clipper wants.
        candidates.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));

        foreach (var (vertex, _) in candidates) {
            var toX = outline[vertex * 4];
            var toZ = outline[(vertex * 4) + 2];

            if (Crosses(outline, vertex, toX, toZ, cornerX, cornerZ)) {
                continue;
            }

            var blocked = false;

            // Against the holes not yet merged, including this one — a bridge that leaves the hole
            // and comes straight back in through its far side is a diagonal outside the region.
            for (var index = firstUnmerged; index < holes.Count && !blocked; index++) {
                blocked = Crosses(holes[index], -1, toX, toZ, cornerX, cornerZ);
            }

            if (!blocked) {
                return vertex;
            }
        }

        return -1;
    }

    /// <summary>The merged ring: the outline from the bridge round to itself, then the hole likewise.</summary>
    /// <remarks>
    ///     Both loops are closed — the vertex they start at is repeated at the end — and that is what
    ///     makes the slit. The result has two vertices more than the two inputs together, and the two
    ///     duplicated pairs are the slit's ends.
    /// </remarks>
    static int[] Splice(int[] outline, int[] hole, int outlineVertex, int holeVertex) {
        var outlineCount = outline.Length / 4;
        var holeCount = hole.Length / 4;
        var merged = new int[(outlineCount + holeCount + 2) * 4];
        var next = 0;

        for (var step = 0; step <= outlineCount; step++) {
            Copy(outline, (outlineVertex + step) % outlineCount, merged, next++);
        }

        for (var step = 0; step <= holeCount; step++) {
            Copy(hole, (holeVertex + step) % holeCount, merged, next++);
        }

        return merged;
    }

    static void Copy(int[] source, int sourceVertex, int[] destination, int destinationVertex) {
        Array.Copy(source, sourceVertex * 4, destination, destinationVertex * 4, 4);
    }

    /// <summary>Twice the signed area of an outline. Positive when it winds counter-clockwise in XZ.</summary>
    static long SignedArea(int[] vertices) {
        var count = vertices.Length / 4;
        long total = 0;

        for (int index = 0, previous = count - 1; index < count; previous = index++) {
            total += ((long)vertices[previous * 4] * vertices[(index * 4) + 2])
                - ((long)vertices[index * 4] * vertices[(previous * 4) + 2]);
        }

        return total;
    }

    /// <summary>A copy wound the way it is wanted.</summary>
    /// <remarks>
    ///     The fourth component of a vertex — the region across the edge <i>leaving</i> it — does not
    ///     survive a reversal, because reversing changes which edge that is. Nothing reads it after
    ///     this point: simplification, which is the only stage that cares, has already run.
    /// </remarks>
    static int[] Wind(int[] vertices, bool counterClockwise) {
        var copy = (int[])vertices.Clone();

        if (SignedArea(vertices) > 0 == counterClockwise) {
            return copy;
        }

        var count = copy.Length / 4;

        for (var index = 0; index < count / 2; index++) {
            var mirror = count - 1 - index;

            for (var component = 0; component < 4; component++) {
                (copy[(index * 4) + component], copy[(mirror * 4) + component]) =
                    (copy[(mirror * 4) + component], copy[(index * 4) + component]);
            }
        }

        return copy;
    }

    static int Leftmost(int[] vertices) {
        var best = 0;

        for (var index = 1; index < vertices.Length / 4; index++) {
            if (vertices[index * 4] < vertices[best * 4] ||
                (vertices[index * 4] == vertices[best * 4] && vertices[(index * 4) + 2] < vertices[(best * 4) + 2])) {
                best = index;
            }
        }

        return best;
    }

    static int CompareByLeftmost(int[] first, int[] second) {
        var a = Leftmost(first);
        var b = Leftmost(second);
        var byX = first[a * 4].CompareTo(second[b * 4]);

        return byX != 0 ? byX : first[(a * 4) + 2].CompareTo(second[(b * 4) + 2]);
    }

    /// <summary>Whether a point lies in the wedge of interior at an outline vertex.</summary>
    static bool InCone(int[] vertices, int vertex, int x, int z) {
        var count = vertices.Length / 4;
        var previous = (vertex + count - 1) % count;
        var next = (vertex + 1) % count;

        var (px, pz) = (vertices[previous * 4], vertices[(previous * 4) + 2]);
        var (cx, cz) = (vertices[vertex * 4], vertices[(vertex * 4) + 2]);
        var (nx, nz) = (vertices[next * 4], vertices[(next * 4) + 2]);

        // A convex corner opens outwards, so the diagonal has to be inside both of its edges. A
        // reflex corner opens inwards, and the test is the negation of the wedge it does not open
        // into — the same pair of cases the ear clipper's own cone test has.
        if (Area2(px, pz, cx, cz, nx, nz) >= 0) {
            return Area2(cx, cz, x, z, px, pz) > 0 && Area2(x, z, cx, cz, nx, nz) > 0;
        }

        return !(Area2(cx, cz, x, z, nx, nz) >= 0 && Area2(x, z, cx, cz, px, pz) >= 0);
    }

    /// <summary>Whether a segment crosses any edge of an outline, ignoring the edges at one vertex.</summary>
    static bool Crosses(int[] vertices, int skipVertex, int fromX, int fromZ, int toX, int toZ) {
        var count = vertices.Length / 4;

        for (var index = 0; index < count; index++) {
            var next = (index + 1) % count;

            if (index == skipVertex || next == skipVertex) {
                continue;
            }

            var (ax, az) = (vertices[index * 4], vertices[(index * 4) + 2]);
            var (bx, bz) = (vertices[next * 4], vertices[(next * 4) + 2]);

            // An edge that ends where the segment does touches it rather than crossing it. Every
            // bridge does this at both ends, so without this the search would never find one.
            if ((ax == fromX && az == fromZ) || (ax == toX && az == toZ) ||
                (bx == fromX && bz == fromZ) || (bx == toX && bz == toZ)) {
                continue;
            }

            if (Intersects(fromX, fromZ, toX, toZ, ax, az, bx, bz)) {
                return true;
            }
        }

        return false;
    }

    static long Area2(int ax, int az, int bx, int bz, int cx, int cz) =>
        ((long)(bx - ax) * (cz - az)) - ((long)(cx - ax) * (bz - az));

    static bool Intersects(int ax, int az, int bx, int bz, int cx, int cz, int dx, int dz) {
        var abc = Area2(ax, az, bx, bz, cx, cz);
        var abd = Area2(ax, az, bx, bz, dx, dz);
        var cda = Area2(cx, cz, dx, dz, ax, az);
        var cdb = Area2(cx, cz, dx, dz, bx, bz);

        if (abc == 0 || abd == 0 || cda == 0 || cdb == 0) {
            // Collinear. Touching at a point is not crossing; overlapping along a stretch is.
            return Between(ax, az, bx, bz, cx, cz) || Between(ax, az, bx, bz, dx, dz) ||
                Between(cx, cz, dx, dz, ax, az) || Between(cx, cz, dx, dz, bx, bz);
        }

        return abc > 0 != abd > 0 && cda > 0 != cdb > 0;
    }

    static bool Between(int ax, int az, int bx, int bz, int cx, int cz) {
        if (Area2(ax, az, bx, bz, cx, cz) != 0) {
            return false;
        }

        if (ax != bx) {
            return (ax <= cx && cx <= bx) || (ax >= cx && cx >= bx);
        }

        return (az <= cz && cz <= bz) || (az >= cz && cz >= bz);
    }
}
