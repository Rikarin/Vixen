// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.DistanceFields;

/// <summary>A bounding-volume hierarchy over a triangle soup, for the two questions a bake asks it.</summary>
/// <remarks>
///     <para>
///         <b>Both queries are branch-and-bound, and that is the whole point.</b> A field of 32³
///         samples over a mesh of ten thousand triangles is 32 768 closest-point queries and about a
///         million rays; done against every triangle each time it is ten billion triangle tests, and
///         done against a hierarchy it is a few hundred million. The tree is not an optimisation of
///         the bake, it is what makes the bake finish.
///     </para>
///     <para>
///         <b>Built by median split on the longest axis, not by SAH.</b> A surface-area heuristic
///         builds a better tree and takes longer to build, and this tree is queried a million times
///         and thrown away — the build is not the cost. Median splitting is also trivially
///         deterministic, which the byte-identical-rebake test depends on: ties in a centroid
///         comparison break on triangle index, so the tree does not depend on the sort's stability.
///     </para>
///     <para>
///         Internal, because it is a shape this assembly reasons in. A general acceleration structure
///         belongs in <c>Vixen.Core.Mathematics</c> if anything else ever needs one, and nothing does
///         yet.
///     </para>
/// </remarks>
sealed class TriangleTree {
    /// <summary>How many triangles a node may hold before it is split.</summary>
    const int LeafSize = 4;

    /// <summary>How deep the traversal stack is. A median split cannot exceed this for any real mesh.</summary>
    const int MaxDepth = 64;

    readonly Vector3[] vertices;
    readonly int[] indices;

    /// <summary>Triangle ids, permuted so every node's triangles are one contiguous range.</summary>
    readonly int[] order;

    readonly Node[] nodes;

    /// <summary>The box every triangle fits inside.</summary>
    public BoundingBox Bounds => nodes.Length > 0 ? nodes[0].Bounds : BoundingBox.Empty;

    /// <summary>How many triangles the tree holds.</summary>
    public int TriangleCount => order.Length;

    /// <summary>Builds a tree over a triangle soup.</summary>
    /// <param name="vertices">The positions.</param>
    /// <param name="indices">Three indices per triangle.</param>
    /// <exception cref="ArgumentException">The indices are not a whole number of triangles.</exception>
    public TriangleTree(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices) {
        if (indices.Length % 3 != 0) {
            throw new ArgumentException($"{indices.Length} indices is not a whole number of triangles.", nameof(indices));
        }

        this.vertices = vertices.ToArray();
        this.indices = indices.ToArray();

        var count = indices.Length / 3;
        order = new int[count];
        var centroids = new Vector3[count];
        var boxes = new BoundingBox[count];

        for (var triangle = 0; triangle < count; triangle++) {
            order[triangle] = triangle;
            Triangle(triangle, out var a, out var b, out var c);
            centroids[triangle] = (a + b + c) / 3f;
            boxes[triangle] = new(Vector3.Min(a, Vector3.Min(b, c)), Vector3.Max(a, Vector3.Max(b, c)));
        }

        // Two nodes per leaf is the worst a binary tree can want, and one more for the root's own
        // slot. Sizing it up front means the build never resizes and node indices never move.
        var built = new List<Node>(Math.Max(1, (2 * count / LeafSize) + 1));
        Build(built, centroids, boxes, 0, count, 0);
        nodes = [.. built];
    }

    /// <summary>The squared distance from a point to the nearest triangle.</summary>
    /// <param name="point">The point.</param>
    /// <returns>The squared distance, or <see cref="float.PositiveInfinity" /> over an empty tree.</returns>
    /// <remarks>
    ///     Squared, because the bake compares distances far more often than it reports one and a
    ///     square root per comparison is a square root per triangle. The near child is descended
    ///     first, so the bound tightens before the far child is tested against it — depth-first in
    ///     the wrong order visits the same nodes with a bound that has not improved yet.
    /// </remarks>
    public float DistanceSquared(Vector3 point) {
        if (nodes.Length == 0) {
            return float.PositiveInfinity;
        }

        Span<int> stack = stackalloc int[MaxDepth];
        var depth = 0;
        stack[depth++] = 0;
        var best = float.PositiveInfinity;

        while (depth > 0) {
            var node = nodes[stack[--depth]];

            if (DistanceSquaredToBox(point, node.Bounds) >= best) {
                continue;
            }

            if (node.Count > 0) {
                for (var slot = node.Start; slot < node.Start + node.Count; slot++) {
                    Triangle(order[slot], out var a, out var b, out var c);
                    var closest = ClosestPointOnTriangle(point, a, b, c);
                    var distance = Vector3.DistanceSquared(point, closest);

                    if (distance < best) {
                        best = distance;
                    }
                }

                continue;
            }

            var left = node.Left;
            var right = node.Right;

            // Push the far child first so the near one is popped first.
            if (DistanceSquaredToBox(point, nodes[left].Bounds) < DistanceSquaredToBox(point, nodes[right].Bounds)) {
                stack[depth++] = right;
                stack[depth++] = left;
            } else {
                stack[depth++] = left;
                stack[depth++] = right;
            }
        }

        return best;
    }

    /// <summary>The nearest triangle a ray strikes, and which of its faces.</summary>
    /// <param name="origin">Where the ray starts.</param>
    /// <param name="direction">Which way it goes. Need not be normalised.</param>
    /// <param name="backface">Whether the nearest hit was struck from behind.</param>
    /// <returns>Whether anything was hit at all.</returns>
    /// <remarks>
    ///     <b>The whole ray is traced, not the first hit found.</b> A sign vote asks whether the
    ///     <i>nearest</i> face is a backface, and stopping at the first intersection any traversal
    ///     order happens to reach would answer a different question — one whose answer depends on the
    ///     tree's shape.
    /// </remarks>
    public bool Raycast(Vector3 origin, Vector3 direction, out bool backface) {
        backface = false;

        if (nodes.Length == 0) {
            return false;
        }

        // Large but finite, deliberately. A true infinity here produces 0 × ∞ — a NaN — for a ray
        // whose origin lies exactly on a slab plane it travels parallel to, and a NaN fails every
        // comparison in the slab test, so the box is skipped and the hit behind it is lost.
        const float ParallelInverse = 1e30f;

        var inverse = new Vector3(
            direction.X == 0 ? ParallelInverse : 1f / direction.X,
            direction.Y == 0 ? ParallelInverse : 1f / direction.Y,
            direction.Z == 0 ? ParallelInverse : 1f / direction.Z
        );

        Span<int> stack = stackalloc int[MaxDepth];
        var depth = 0;
        stack[depth++] = 0;
        var nearest = float.PositiveInfinity;
        var hit = false;

        while (depth > 0) {
            var node = nodes[stack[--depth]];

            if (!IntersectsBox(origin, inverse, node.Bounds, nearest)) {
                continue;
            }

            if (node.Count > 0) {
                for (var slot = node.Start; slot < node.Start + node.Count; slot++) {
                    Triangle(order[slot], out var a, out var b, out var c);

                    if (!IntersectsTriangle(origin, direction, a, b, c, out var distance, out var behind)
                        || distance >= nearest) {
                        continue;
                    }

                    nearest = distance;
                    backface = behind;
                    hit = true;
                }

                continue;
            }

            stack[depth++] = node.Left;
            stack[depth++] = node.Right;
        }

        return hit;
    }

    /// <summary>Recursively builds a node over one range of the triangle order.</summary>
    /// <param name="built">The nodes so far.</param>
    /// <param name="centroids">Every triangle's centroid, indexed by triangle id.</param>
    /// <param name="boxes">Every triangle's box, indexed by triangle id.</param>
    /// <param name="start">Where this node's range starts in <see cref="order" />.</param>
    /// <param name="count">How long it is.</param>
    /// <param name="depth">How deep this node is, so the split can be given up on.</param>
    /// <returns>The new node's index.</returns>
    int Build(List<Node> built, Vector3[] centroids, BoundingBox[] boxes, int start, int count, int depth) {
        var bounds = BoundingBox.Empty;

        for (var slot = start; slot < start + count; slot++) {
            bounds = BoundingBox.Merge(bounds, boxes[order[slot]]);
        }

        var self = built.Count;
        built.Add(new() { Bounds = bounds, Start = start, Count = count });

        // A leaf, either because it is small enough or because the stack says it has to be. The
        // depth guard is what lets the traversal stackalloc a fixed size and never check it.
        if (count <= LeafSize || depth >= MaxDepth - 2) {
            return self;
        }

        var axis = LongestAxis(bounds);
        var slice = order.AsSpan(start, count);
        var keys = new float[count];

        for (var slot = 0; slot < count; slot++) {
            keys[slot] = Component(centroids[slice[slot]], axis);
        }

        // Sorting the keys alongside the ids orders the range by centroid; the id is the tie-break,
        // so two triangles with the same centroid always land the same way round.
        var ids = slice.ToArray();
        Array.Sort(keys, ids);
        StabiliseTies(keys, ids);
        ids.CopyTo(slice);

        var middle = count / 2;
        var left = Build(built, centroids, boxes, start, middle, depth + 1);
        var right = Build(built, centroids, boxes, start + middle, count - middle, depth + 1);

        built[self] = built[self] with { Count = 0, Left = left, Right = right };

        return self;
    }

    /// <summary>Reorders equal-key runs by triangle id, so the sort's own instability cannot show.</summary>
    /// <param name="keys">The sorted keys.</param>
    /// <param name="ids">The ids that moved with them.</param>
    static void StabiliseTies(float[] keys, int[] ids) {
        for (var start = 0; start < keys.Length;) {
            var end = start + 1;

            while (end < keys.Length && keys[end] == keys[start]) {
                end++;
            }

            if (end - start > 1) {
                Array.Sort(ids, start, end - start);
            }

            start = end;
        }
    }

    /// <summary>Reads one triangle's three positions.</summary>
    /// <param name="triangle">The triangle's id.</param>
    /// <param name="a">Its first vertex.</param>
    /// <param name="b">Its second.</param>
    /// <param name="c">Its third.</param>
    void Triangle(int triangle, out Vector3 a, out Vector3 b, out Vector3 c) {
        a = vertices[indices[triangle * 3]];
        b = vertices[indices[(triangle * 3) + 1]];
        c = vertices[indices[(triangle * 3) + 2]];
    }

    /// <summary>Which axis a box is longest along.</summary>
    /// <param name="bounds">The box.</param>
    /// <returns>0 for X, 1 for Y, 2 for Z.</returns>
    static int LongestAxis(BoundingBox bounds) {
        var size = bounds.Size;

        if (size.X >= size.Y && size.X >= size.Z) {
            return 0;
        }

        return size.Y >= size.Z ? 1 : 2;
    }

    /// <summary>One component of a vector, by axis index.</summary>
    /// <param name="value">The vector.</param>
    /// <param name="axis">0 for X, 1 for Y, 2 for Z.</param>
    /// <returns>The component.</returns>
    static float Component(Vector3 value, int axis) => axis switch {
        0 => value.X,
        1 => value.Y,
        _ => value.Z
    };

    /// <summary>The squared distance from a point to a box, zero inside it.</summary>
    /// <param name="point">The point.</param>
    /// <param name="bounds">The box.</param>
    /// <returns>The squared distance.</returns>
    static float DistanceSquaredToBox(Vector3 point, BoundingBox bounds) {
        var clamped = Vector3.Min(Vector3.Max(point, bounds.Minimum), bounds.Maximum);

        return Vector3.DistanceSquared(point, clamped);
    }

    /// <summary>Whether a ray reaches a box before a distance it has already bettered.</summary>
    /// <param name="origin">Where the ray starts.</param>
    /// <param name="inverse">The reciprocal of its direction, component-wise.</param>
    /// <param name="bounds">The box.</param>
    /// <param name="limit">The nearest hit so far.</param>
    /// <returns>Whether the box is worth descending.</returns>
    static bool IntersectsBox(Vector3 origin, Vector3 inverse, BoundingBox bounds, float limit) {
        var low = (bounds.Minimum - origin) * inverse;
        var high = (bounds.Maximum - origin) * inverse;
        var near = Vector3.Min(low, high);
        var far = Vector3.Max(low, high);
        var entry = MathF.Max(MathF.Max(near.X, near.Y), near.Z);
        var exit = MathF.Min(MathF.Min(far.X, far.Y), far.Z);

        return exit >= MathF.Max(entry, 0f) && entry <= limit;
    }

    /// <summary>Möller–Trumbore, with the face the ray struck.</summary>
    /// <param name="origin">Where the ray starts.</param>
    /// <param name="direction">Which way it goes.</param>
    /// <param name="a">The triangle's first vertex.</param>
    /// <param name="b">Its second.</param>
    /// <param name="c">Its third.</param>
    /// <param name="distance">How far along the ray the hit is, in units of <paramref name="direction" />.</param>
    /// <param name="backface">Whether the ray struck the face from behind.</param>
    /// <returns>Whether it hit at all.</returns>
    /// <remarks>
    ///     Two-sided deliberately: culling backfaces here would throw away the only observation the
    ///     sign vote is made of.
    /// </remarks>
    static bool IntersectsTriangle(
        Vector3 origin,
        Vector3 direction,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        out float distance,
        out bool backface
    ) {
        distance = 0;
        backface = false;

        var edge1 = b - a;
        var edge2 = c - a;
        var across = Vector3.Cross(direction, edge2);
        var determinant = Vector3.Dot(edge1, across);

        if (MathF.Abs(determinant) < MathUtil.ZeroTolerance) {
            return false;
        }

        var inverse = 1f / determinant;
        var toOrigin = origin - a;
        var u = Vector3.Dot(toOrigin, across) * inverse;

        if (u is < 0f or > 1f) {
            return false;
        }

        var along = Vector3.Cross(toOrigin, edge1);
        var v = Vector3.Dot(direction, along) * inverse;

        if (v < 0f || u + v > 1f) {
            return false;
        }

        distance = Vector3.Dot(edge2, along) * inverse;

        if (distance <= MathUtil.ZeroTolerance) {
            return false;
        }

        // The determinant is −dot(direction, normal) — the scalar triple product reassociates
        // edge1·(direction × edge2) into direction·(edge2 × edge1), and edge1 × edge2 is the
        // counter-clockwise normal. So a positive determinant is a ray opposing the normal, which is
        // the front face, and the sign falls out of the intersection rather than costing a second
        // dot product. Getting this round the wrong way inverts every field the vote produces.
        backface = determinant < 0;

        return true;
    }

    /// <summary>The point on a triangle nearest another point.</summary>
    /// <param name="point">The point.</param>
    /// <param name="a">The triangle's first vertex.</param>
    /// <param name="b">Its second.</param>
    /// <param name="c">Its third.</param>
    /// <returns>The nearest point, on a vertex, an edge or the face.</returns>
    /// <remarks>
    ///     Ericson's barycentric-region test: seven cases, each one a handful of dot products, and no
    ///     division until the region is known. Projecting onto the plane and clamping is the tempting
    ///     shortcut and it is wrong — the projection of a point beyond a corner clamps to the wrong
    ///     edge.
    /// </remarks>
    internal static Vector3 ClosestPointOnTriangle(Vector3 point, Vector3 a, Vector3 b, Vector3 c) {
        var ab = b - a;
        var ac = c - a;
        var ap = point - a;

        var d1 = Vector3.Dot(ab, ap);
        var d2 = Vector3.Dot(ac, ap);

        if (d1 <= 0 && d2 <= 0) {
            return a;
        }

        var bp = point - b;
        var d3 = Vector3.Dot(ab, bp);
        var d4 = Vector3.Dot(ac, bp);

        if (d3 >= 0 && d4 <= d3) {
            return b;
        }

        var vc = (d1 * d4) - (d3 * d2);

        if (vc <= 0 && d1 >= 0 && d3 <= 0) {
            // d1 − d3 is |ab|², so it is zero exactly when the edge has no length. Every one of
            // these three denominators is an edge length, and every one of them is zero on some
            // triangle a real mesh contains — a pole fan, a collapsed quad, a welded seam.
            var length = d1 - d3;

            return length > 0 ? a + (ab * (d1 / length)) : a;
        }

        var cp = point - c;
        var d5 = Vector3.Dot(ab, cp);
        var d6 = Vector3.Dot(ac, cp);

        if (d6 >= 0 && d5 <= d6) {
            return c;
        }

        var vb = (d5 * d2) - (d1 * d6);

        if (vb <= 0 && d2 >= 0 && d6 <= 0) {
            var length = d2 - d6;

            return length > 0 ? a + (ac * (d2 / length)) : a;
        }

        var va = (d3 * d6) - (d5 * d4);

        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) {
            var length = (d4 - d3) + (d5 - d6);

            return length > 0 ? b + ((c - b) * ((d4 - d3) / length)) : b;
        }

        var area = va + vb + vc;

        // A triangle with no area has no interior to project onto, and dividing by its area is the
        // NaN that poisons every distance computed from it. Meshes are full of them — every UV
        // sphere has a fan of them at each pole, where a whole ring of vertices is one point — so
        // this is the common case dressed as the exceptional one.
        if (area <= MathUtil.ZeroTolerance) {
            return NearestOnEdges(point, a, b, c);
        }

        var denominator = 1f / area;

        return a + (ab * (vb * denominator)) + (ac * (vc * denominator));
    }

    /// <summary>The nearest point on a triangle's three edges, for when it has no interior.</summary>
    /// <param name="point">The point.</param>
    /// <param name="a">The triangle's first vertex.</param>
    /// <param name="b">Its second.</param>
    /// <param name="c">Its third.</param>
    /// <returns>The nearest of the three.</returns>
    static Vector3 NearestOnEdges(Vector3 point, Vector3 a, Vector3 b, Vector3 c) {
        var best = ClosestOnSegment(point, a, b);
        var bestDistance = Vector3.DistanceSquared(point, best);

        foreach (var (from, to) in ((Vector3 From, Vector3 To)[]) [(b, c), (c, a)]) {
            var candidate = ClosestOnSegment(point, from, to);
            var distance = Vector3.DistanceSquared(point, candidate);

            if (distance < bestDistance) {
                best = candidate;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>The nearest point on a segment, its endpoints included.</summary>
    /// <param name="point">The point.</param>
    /// <param name="from">The segment's start.</param>
    /// <param name="to">Its end.</param>
    /// <returns>The nearest point.</returns>
    static Vector3 ClosestOnSegment(Vector3 point, Vector3 from, Vector3 to) {
        var along = to - from;
        var lengthSquared = along.LengthSquared();

        if (lengthSquared <= MathUtil.ZeroTolerance) {
            return from;
        }

        return from + (along * Math.Clamp(Vector3.Dot(point - from, along) / lengthSquared, 0f, 1f));
    }

    /// <summary>One node: a box, and either a range of triangles or two children.</summary>
    /// <remarks>
    ///     <see cref="Count" /> is zero exactly when the node is internal, which is why a leaf with no
    ///     triangles is never built — the build only splits ranges it has, so an empty range cannot
    ///     arise.
    /// </remarks>
    readonly record struct Node {
        /// <summary>Everything below this node fits inside.</summary>
        public BoundingBox Bounds { get; init; }

        /// <summary>Where a leaf's triangles start in <see cref="order" />.</summary>
        public int Start { get; init; }

        /// <summary>How many a leaf has, or zero when the node is internal.</summary>
        public int Count { get; init; }

        /// <summary>An internal node's first child.</summary>
        public int Left { get; init; }

        /// <summary>Its second.</summary>
        public int Right { get; init; }
    }
}
