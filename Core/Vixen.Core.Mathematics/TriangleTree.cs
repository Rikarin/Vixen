// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Mathematics;

/// <summary>Everything a closest-point query found, rather than only how far away it was.</summary>
/// <remarks>
///     <para>
///         <c>docs/plan/41-automatic-retopology.md</c> § D12 interpolates normals, texture
///         coordinates, vertex colours and skin weights from the source mesh at the point a
///         retopologised vertex lands on. A scalar distance cannot do that: the interpolation needs
///         to know <em>which</em> triangle and <em>where</em> on it, which is
///         <see cref="Triangle" /> and <see cref="Barycentric" />.
///     </para>
///     <para>
///         ⚠ <b><see cref="Barycentric" />'s three components always sum to one, including when the
///         nearest point is on an edge or a vertex or the triangle has no area.</b> Those are the
///         cases where the interior solution divides by zero, and a transfer that read a
///         <c>NaN</c> weight there would write a <c>NaN</c> normal into the output mesh. Each
///         degenerate region produces its weights directly instead of dividing.
///     </para>
/// </remarks>
/// <param name="Triangle">
///     Which triangle it is, indexed as the caller's indices were, or <c>-1</c> over an empty tree.
/// </param>
/// <param name="Point">The point on that triangle nearest the query, or the query itself when empty.</param>
/// <param name="DistanceSquared">
///     How far the query was from it, squared, or <see cref="float.PositiveInfinity" /> when empty.
/// </param>
/// <param name="Barycentric">
///     The weights of the triangle's first, second and third vertex at <paramref name="Point" />.
/// </param>
public readonly record struct ClosestTriangle(int Triangle, Vector3 Point, float DistanceSquared, Vector3 Barycentric);

/// <summary>Everything a ray found, rather than only whether it found anything.</summary>
/// <remarks>
///     <para>
///         <c>docs/plan/41-automatic-retopology.md</c> § D12's bake casts along the output's
///         interpolated normal and writes what it struck into an atlas texel: the source normal
///         there, and how far along the ray it was. Both of those are
///         <see cref="Barycentric" /> and <see cref="Distance" />, and the
///         <see cref="TriangleTree.Raycast(Vector3, Vector3, out bool)" /> that predates this
///         reports neither — it exists for a sign vote, which asks only whether the nearest face
///         was struck from behind.
///     </para>
///     <para>
///         ⚠ <b><see cref="Distance" /> is in units of the direction handed in, not of the
///         model.</b> A caller that passes a unit normal reads a length; one that passes an
///         un-normalised direction reads a fraction of it. The bake passes a direction whose
///         length <em>is</em> its search radius, so the answer it wants is the fraction.
///     </para>
/// </remarks>
/// <param name="Triangle">Which triangle, indexed as the caller's indices were, or <c>-1</c> for a miss.</param>
/// <param name="Point">Where on it the ray struck, or the origin for a miss.</param>
/// <param name="Distance">
///     How far along the ray, in units of the direction, or <see cref="float.PositiveInfinity" />
///     for a miss.
/// </param>
/// <param name="Barycentric">The weights of the triangle's three vertices at <paramref name="Point" />.</param>
/// <param name="Backface">Whether the face was struck from behind.</param>
public readonly record struct TriangleHit(
    int Triangle,
    Vector3 Point,
    float Distance,
    Vector3 Barycentric,
    bool Backface
);

/// <summary>A bounding-volume hierarchy over a triangle soup, and the three questions asked of it.</summary>
/// <remarks>
///     <para>
///         <b>Every query is branch-and-bound, and that is the whole point.</b> A distance field of
///         32³ samples over a mesh of ten thousand triangles is 32 768 closest-point queries and about
///         a million rays; done against every triangle each time it is ten billion triangle tests, and
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
///         ⚠ <b>It lived in <c>Vixen.Rendering.DistanceFields</c>, whose own remarks said it belonged
///         here as soon as a second caller existed.</b> That caller is
///         <c>docs/plan/41-automatic-retopology.md</c> § D12: attribute transfer drives closest-point
///         queries against the conditioned source mesh, and the geometry assembly cannot reference the
///         distance-field one. Nothing about the structure changed in the move —
///         <em>especially</em> not the tie-break above, which a rebake's byte-identity rests on.
///         ⚠ § D12 credits <c>MeshCollision</c> with "already has the shape of one"; it does not, and
///         never did — it is shell labelling and one axis-aligned box per shell, with no tree and no
///         query. This is the acceleration structure that claim was reaching for.
///     </para>
/// </remarks>
public sealed class TriangleTree {
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
    ///     Squared, because a distance-field bake compares distances far more often than it reports
    ///     one and a square root per comparison is a square root per triangle. This is
    ///     <see cref="Closest" /> with everything but the distance discarded: one traversal, rather
    ///     than a second one that would have to be kept agreeing with the first about which triangle
    ///     wins a tie.
    /// </remarks>
    public float DistanceSquared(Vector3 point) => Closest(point).DistanceSquared;

    /// <summary>The nearest triangle to a point, where on it the nearest point lies, and how far.</summary>
    /// <param name="point">The point.</param>
    /// <returns>The nearest triangle, or a <see cref="ClosestTriangle" /> of <c>-1</c> over an empty tree.</returns>
    /// <remarks>
    ///     <para>
    ///         The near child is descended first, so the bound tightens before the far child is
    ///         tested against it — depth-first in the wrong order visits the same nodes with a bound
    ///         that has not improved yet.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Ties go to the triangle the traversal reached first, and that is a deterministic
    ///         answer rather than an arbitrary one</b> — the comparison is a strict improvement, and
    ///         the build's own tie-break on triangle index (see the type's remarks) is what makes the
    ///         traversal order a function of the input alone. A point equidistant from two triangles
    ///         is the common case, not the exotic one: it is every shared edge of every mesh.
    ///     </para>
    /// </remarks>
    public ClosestTriangle Closest(Vector3 point) {
        // ⚠ The triangle count, not the node count. A tree over an empty soup still builds its
        // root — `Build` adds a node before it decides whether to split — so `nodes` is never empty
        // and a guard on it lets the traversal reach a node whose `Count` is zero and whose `Left`
        // is therefore the default zero, which is the root. It then descends into itself until the
        // fixed-size stack overruns. `Closest` survives it only because an empty node's box is at an
        // infinite distance and its bound test happens to reject it; the ray overloads have no such
        // luck, and the boolean one has carried this since before the move out of `DistanceFields`.
        if (order.Length == 0) {
            return new(-1, point, float.PositiveInfinity, Vector3.Zero);
        }

        Span<int> stack = stackalloc int[MaxDepth];
        var depth = 0;
        stack[depth++] = 0;
        var best = float.PositiveInfinity;
        var found = -1;
        var nearest = point;
        var weights = Vector3.Zero;

        while (depth > 0) {
            var node = nodes[stack[--depth]];

            if (DistanceSquaredToBox(point, node.Bounds) >= best) {
                continue;
            }

            if (node.Count > 0) {
                for (var slot = node.Start; slot < node.Start + node.Count; slot++) {
                    var triangle = order[slot];
                    Triangle(triangle, out var a, out var b, out var c);
                    var closest = ClosestPointOnTriangle(point, a, b, c, out var barycentric);
                    var distance = Vector3.DistanceSquared(point, closest);

                    if (distance < best) {
                        best = distance;
                        found = triangle;
                        nearest = closest;
                        weights = barycentric;
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

        return new(found, nearest, best, weights);
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

        // ⚠ The triangle count, not the node count. A tree over an empty soup still builds its
        // root — `Build` adds a node before it decides whether to split — so `nodes` is never empty
        // and a guard on it lets the traversal reach a node whose `Count` is zero and whose `Left`
        // is therefore the default zero, which is the root. It then descends into itself until the
        // fixed-size stack overruns. `Closest` survives it only because an empty node's box is at an
        // infinite distance and its bound test happens to reject it; the ray overloads have no such
        // luck, and the boolean one has carried this since before the move out of `DistanceFields`.
        if (order.Length == 0) {
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

    /// <summary>The nearest triangle a ray strikes, and everything about the hit.</summary>
    /// <param name="origin">Where the ray starts.</param>
    /// <param name="direction">
    ///     Which way it goes, and how far. ⚠ <b>Its length is the search radius</b> — a hit is kept
    ///     only where <see cref="TriangleHit.Distance" /> lands in <c>(0, 1]</c>, so a caller bounds
    ///     the search by scaling the direction rather than by passing a separate limit that could
    ///     disagree with it.
    /// </param>
    /// <returns>The nearest hit, or a <see cref="TriangleHit" /> of <c>-1</c> for a miss.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>This is the query a bake asks and the boolean overload cannot answer.</b>
    ///         <c>docs/plan/41-automatic-retopology.md</c> § D12 casts along the output's normal and
    ///         writes the source's normal at the hit into an atlas texel; that needs the triangle and
    ///         the barycentric weights on it, and <see cref="Raycast(Vector3, Vector3, out bool)" />
    ///         reports a sign vote's worth of the same traversal.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Bounding by the direction's length rather than by an absolute distance is the
    ///         scale rule this file already applies twice.</b> A bake's search radius is a fraction
    ///         of the model's diagonal, and a limit expressed in metres is a claim about how big the
    ///         model is — the same claim that let rays pass straight through geometry at 1/1024
    ///         scale.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Ties go to the triangle the traversal reached first</b>, exactly as
    ///         <see cref="Closest" />'s do, and for the same reason: the build's tie-break on
    ///         triangle index makes the traversal order a function of the input alone.
    ///     </para>
    /// </remarks>
    public TriangleHit Raycast(Vector3 origin, Vector3 direction) {
        var miss = new TriangleHit(-1, origin, float.PositiveInfinity, Vector3.Zero, false);

        // ⚠ The triangle count, not the node count. A tree over an empty soup still builds its
        // root — `Build` adds a node before it decides whether to split — so `nodes` is never empty
        // and a guard on it lets the traversal reach a node whose `Count` is zero and whose `Left`
        // is therefore the default zero, which is the root. It then descends into itself until the
        // fixed-size stack overruns. `Closest` survives it only because an empty node's box is at an
        // infinite distance and its bound test happens to reject it; the ray overloads have no such
        // luck, and the boolean one has carried this since before the move out of `DistanceFields`.
        if (order.Length == 0) {
            return miss;
        }

        // The same finite stand-in the boolean overload uses, and for the same reason: a true
        // infinity makes 0 × ∞ a NaN for a ray lying exactly in a slab plane it travels parallel to.
        const float ParallelInverse = 1e30f;

        var inverse = new Vector3(
            direction.X == 0 ? ParallelInverse : 1f / direction.X,
            direction.Y == 0 ? ParallelInverse : 1f / direction.Y,
            direction.Z == 0 ? ParallelInverse : 1f / direction.Z
        );

        Span<int> stack = stackalloc int[MaxDepth];
        var depth = 0;
        stack[depth++] = 0;

        // One, because the direction's length is the radius. Every box and every triangle is tested
        // against the bound that is already the best, so the search shrinks as it goes.
        var nearest = 1f;
        var best = miss;

        while (depth > 0) {
            var node = nodes[stack[--depth]];

            if (!IntersectsBox(origin, inverse, node.Bounds, nearest)) {
                continue;
            }

            if (node.Count > 0) {
                for (var slot = node.Start; slot < node.Start + node.Count; slot++) {
                    var triangle = order[slot];
                    Triangle(triangle, out var a, out var b, out var c);

                    if (!IntersectsTriangle(origin, direction, a, b, c, out var distance, out var behind, out var weights)
                        || distance >= nearest) {
                        continue;
                    }

                    nearest = distance;
                    best = new(triangle, origin + (direction * distance), distance, weights, behind);
                }

                continue;
            }

            stack[depth++] = node.Left;
            stack[depth++] = node.Right;
        }

        return best;
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
    ) =>
        IntersectsTriangle(origin, direction, a, b, c, out distance, out backface, out _);

    /// <summary>The same, and where on the triangle the ray struck.</summary>
    /// <param name="origin">Where the ray starts.</param>
    /// <param name="direction">Which way it goes.</param>
    /// <param name="a">The triangle's first vertex.</param>
    /// <param name="b">Its second.</param>
    /// <param name="c">Its third.</param>
    /// <param name="distance">How far along the ray the hit is, in units of <paramref name="direction" />.</param>
    /// <param name="backface">Whether the ray struck the face from behind.</param>
    /// <param name="barycentric">
    ///     The weights of the three vertices at the hit. ⚠ <b>Summing to one by construction</b> —
    ///     Möller–Trumbore's <c>u</c> and <c>v</c> are already two of the three, so the first is
    ///     their complement and no division normalises it.
    /// </param>
    /// <returns>Whether it hit at all.</returns>
    static bool IntersectsTriangle(
        Vector3 origin,
        Vector3 direction,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        out float distance,
        out bool backface,
        out Vector3 barycentric
    ) {
        distance = 0;
        backface = false;
        barycentric = Vector3.Zero;

        var edge1 = b - a;
        var edge2 = c - a;
        var across = Vector3.Cross(direction, edge2);
        var determinant = Vector3.Dot(edge1, across);

        // ⚠ Against the triangle's and the ray's own scale, never against an absolute epsilon. The
        // determinant is edge1 · (direction × edge2), so it carries the model's units *squared* and
        // the direction's once — and a fixed threshold is therefore a statement about how big a
        // model has to be before its triangles can be hit at all. Measured: at a 1/1024 scale, five
        // of nine shapes charted differently because rays passed straight through geometry.
        // Cauchy–Schwarz bounds |determinant| by |edge1|·|across|, so the ratio below is a squared
        // cosine and the comparison is dimensionless. Squared throughout to keep the square roots
        // out of a loop that runs per triangle per ray.
        var span = edge1.LengthSquared() * across.LengthSquared();

        if (determinant * determinant <= MathUtil.ZeroTolerance * MathUtil.ZeroTolerance * span) {
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

        // ⚠ The same lesson on the other side of the intersection. `distance` is in units of
        // `direction`, so `distance × |direction|` is a length and an absolute epsilon on it says
        // how close to a surface a ray may start — in metres, whatever the model is measured in.
        // Relative to the triangle's own edge instead. The sign is tested separately because
        // squaring loses it, and a hit behind the origin is not a hit.
        if (distance <= 0f
            || distance * distance * direction.LengthSquared()
            <= MathUtil.ZeroTolerance * MathUtil.ZeroTolerance * edge1.LengthSquared()) {
            return false;
        }

        // The determinant is −dot(direction, normal) — the scalar triple product reassociates
        // edge1·(direction × edge2) into direction·(edge2 × edge1), and edge1 × edge2 is the
        // counter-clockwise normal. So a positive determinant is a ray opposing the normal, which is
        // the front face, and the sign falls out of the intersection rather than costing a second
        // dot product. Getting this round the wrong way inverts every field the vote produces.
        backface = determinant < 0;
        barycentric = new(1f - u - v, u, v);

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
    ///     edge. The overload taking a <c>barycentric</c> output does the same work and reports which
    ///     region won; this one discards that.
    /// </remarks>
    public static Vector3 ClosestPointOnTriangle(Vector3 point, Vector3 a, Vector3 b, Vector3 c) =>
        ClosestPointOnTriangle(point, a, b, c, out _);

    /// <summary>The point on a triangle nearest another point, and its barycentric coordinates.</summary>
    /// <param name="point">The point.</param>
    /// <param name="a">The triangle's first vertex.</param>
    /// <param name="b">Its second.</param>
    /// <param name="c">Its third.</param>
    /// <param name="barycentric">
    ///     The weights of <paramref name="a" />, <paramref name="b" /> and <paramref name="c" /> at
    ///     the returned point. Always sums to one.
    /// </param>
    /// <returns>The nearest point, on a vertex, an edge or the face.</returns>
    /// <remarks>
    ///     ⚠ <b>Each of the seven regions states its own weights instead of solving for them.</b>
    ///     Recovering barycentrics from the returned point afterwards would divide by the area, which
    ///     is exactly the division the region test exists to avoid — so the degenerate cases, the
    ///     ones a real mesh is full of, would be the ones that came back <c>NaN</c>. A vertex is
    ///     <c>(1, 0, 0)</c>, an edge is <c>(1 − t, t, 0)</c>, and neither needs an area to be known.
    /// </remarks>
    public static Vector3 ClosestPointOnTriangle(
        Vector3 point,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        out Vector3 barycentric
    ) {
        var ab = b - a;
        var ac = c - a;
        var ap = point - a;

        var d1 = Vector3.Dot(ab, ap);
        var d2 = Vector3.Dot(ac, ap);

        if (d1 <= 0 && d2 <= 0) {
            barycentric = new(1f, 0f, 0f);

            return a;
        }

        var bp = point - b;
        var d3 = Vector3.Dot(ab, bp);
        var d4 = Vector3.Dot(ac, bp);

        if (d3 >= 0 && d4 <= d3) {
            barycentric = new(0f, 1f, 0f);

            return b;
        }

        var vc = (d1 * d4) - (d3 * d2);

        if (vc <= 0 && d1 >= 0 && d3 <= 0) {
            // d1 − d3 is |ab|², so it is zero exactly when the edge has no length. Every one of
            // these three denominators is an edge length, and every one of them is zero on some
            // triangle a real mesh contains — a pole fan, a collapsed quad, a welded seam.
            var length = d1 - d3;

            if (length <= 0) {
                barycentric = new(1f, 0f, 0f);

                return a;
            }

            var along = d1 / length;
            barycentric = new(1f - along, along, 0f);

            return a + (ab * along);
        }

        var cp = point - c;
        var d5 = Vector3.Dot(ab, cp);
        var d6 = Vector3.Dot(ac, cp);

        if (d6 >= 0 && d5 <= d6) {
            barycentric = new(0f, 0f, 1f);

            return c;
        }

        var vb = (d5 * d2) - (d1 * d6);

        if (vb <= 0 && d2 >= 0 && d6 <= 0) {
            var length = d2 - d6;

            if (length <= 0) {
                barycentric = new(1f, 0f, 0f);

                return a;
            }

            var along = d2 / length;
            barycentric = new(1f - along, 0f, along);

            return a + (ac * along);
        }

        var va = (d3 * d6) - (d5 * d4);

        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) {
            var length = (d4 - d3) + (d5 - d6);

            if (length <= 0) {
                barycentric = new(0f, 1f, 0f);

                return b;
            }

            var along = (d4 - d3) / length;
            barycentric = new(0f, 1f - along, along);

            return b + ((c - b) * along);
        }

        var area = va + vb + vc;

        // A triangle with no area has no interior to project onto, and dividing by its area is the
        // NaN that poisons every distance computed from it. Meshes are full of them — every UV
        // sphere has a fan of them at each pole, where a whole ring of vertices is one point — so
        // this is the common case dressed as the exceptional one.
        //
        // ⚠ And "no area" is relative to the triangle, not to a constant. An absolute epsilon sends
        // every triangle of a small model down the edge path — where the answer is still a point on
        // the triangle, but pinned to its rim, which is silently wrong for anything interpolating an
        // interior quantity there. Measured before this: a query landing at x = -0.4 inside the fan
        // at unit scale landed at -0.387 on its rim at 1/1024.
        //
        // ⚠ The normalisation is the squared extent and not the extent, and getting that wrong is
        // how this was first written. `va`, `vb` and `vc` are differences of *products of dot
        // products* — degree four in the model's units, not degree two — so dividing by a squared
        // length leaves the comparison scaling as the model squared and moves the bug rather than
        // removing it.
        var extent = MathF.Max(ab.LengthSquared(), ac.LengthSquared());

        if (area <= MathUtil.ZeroTolerance * extent * extent) {
            return NearestOnEdges(point, a, b, c, out barycentric);
        }

        var denominator = 1f / area;
        var second = vb * denominator;
        var third = vc * denominator;

        barycentric = new(1f - second - third, second, third);

        return a + (ab * second) + (ac * third);
    }

    /// <summary>The nearest point on a triangle's three edges, for when it has no interior.</summary>
    /// <param name="point">The point.</param>
    /// <param name="a">The triangle's first vertex.</param>
    /// <param name="b">Its second.</param>
    /// <param name="c">Its third.</param>
    /// <param name="barycentric">The weights of the three vertices at the returned point.</param>
    /// <returns>The nearest of the three.</returns>
    static Vector3 NearestOnEdges(Vector3 point, Vector3 a, Vector3 b, Vector3 c, out Vector3 barycentric) {
        var best = ClosestOnSegment(point, a, b, out var along);
        var bestDistance = Vector3.DistanceSquared(point, best);
        var edge = 0;

        foreach (var (index, from, to) in ((int Index, Vector3 From, Vector3 To)[]) [(1, b, c), (2, c, a)]) {
            var candidate = ClosestOnSegment(point, from, to, out var parameter);
            var distance = Vector3.DistanceSquared(point, candidate);

            if (distance < bestDistance) {
                best = candidate;
                bestDistance = distance;
                along = parameter;
                edge = index;
            }
        }

        barycentric = edge switch {
            0 => new(1f - along, along, 0f),
            1 => new(0f, 1f - along, along),
            _ => new(along, 0f, 1f - along)
        };

        return best;
    }

    /// <summary>The nearest point on a segment, its endpoints included.</summary>
    /// <param name="point">The point.</param>
    /// <param name="from">The segment's start.</param>
    /// <param name="to">Its end.</param>
    /// <param name="along">How far along the segment it is, from zero to one.</param>
    /// <returns>The nearest point.</returns>
    static Vector3 ClosestOnSegment(Vector3 point, Vector3 from, Vector3 to, out float along) {
        var direction = to - from;
        var lengthSquared = direction.LengthSquared();

        // ⚠ Exactly zero, and not an epsilon. A squared length is the model's units squared, so any
        // fixed threshold here collapses real segments on a small model onto their first endpoint;
        // and the only value that actually breaks the division below is zero itself.
        if (lengthSquared <= 0f) {
            along = 0f;

            return from;
        }

        along = Math.Clamp(Vector3.Dot(point - from, direction) / lengthSquared, 0f, 1f);

        return from + (direction * along);
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
