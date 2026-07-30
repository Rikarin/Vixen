// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.RayTracing;

/// <summary>What a ray found in a triangle mesh, or did not.</summary>
/// <param name="Hit">Whether it reached a surface.</param>
/// <param name="Distance">How far along the ray, when it did.</param>
/// <param name="Position">Where it stopped.</param>
/// <param name="Normal">The triangle's geometric normal, facing the ray.</param>
/// <param name="Triangle">Which triangle, as an index into the build's list.</param>
/// <param name="Visited">How many nodes the traversal touched — the cost, and worth seeing.</param>
public readonly record struct RayHit(
    bool Hit,
    float Distance,
    Vector3 Position,
    Vector3 Normal,
    int Triangle,
    int Visited
);

/// <summary>A bounding-volume hierarchy over triangles — doc 19 § L6's reference half.</summary>
/// <remarks>
///     <para>
///         <b>Why this exists before any RHI concept does.</b> § L6 puts acceleration structures
///         into the RHI as an alternative tracer behind L1's interface, and everything above it —
///         the fillers, the gathers, the reflections — stays unchanged because a tracer answers
///         with a hit, a distance and a normal whatever produced them. A hardware ray query cannot
///         be checked against arithmetic; this can, and the day <c>HasRayTracing</c> stops being a
///         declared-and-unimplemented flag, the query's answers are held against this build over
///         the same triangles — the arrangement every capture and march in this engine has with
///         its reference.
///     </para>
///     <para>
///         <b>Median split over the longest axis, exactly.</b> Not surface-area heuristic, and
///         that is a choice about testability rather than ignorance of one: a median build is
///         deterministic from the input order alone, two builds agree structurally, and the
///         traversal's node count has a closed bound the tests can hold. SAH is a quality
///         optimisation with this as its baseline and its referee, the shelf atlas's own argument.
///     </para>
///     <para>
///         <b>The traversal answers the nearest hit, front-to-back.</b> Children are visited near
///         first, the far child is skipped when the ray already hit nearer — which is where the
///         logarithm comes from — and <see cref="RayHit.Visited" /> counts what it touched, so the
///         claim is measured against the brute force rather than asserted.
///     </para>
/// </remarks>
public sealed class TriangleBvh {
    /// <summary>One node: a box, and either children or a run of triangles.</summary>
    readonly struct Node(Vector3 minimum, Vector3 maximum, int start, int count, int right) {
        public readonly Vector3 Minimum = minimum;
        public readonly Vector3 Maximum = maximum;

        /// <summary>First triangle of a leaf's run, or the left child's index.</summary>
        public readonly int Start = start;

        /// <summary>How many triangles a leaf holds — zero marks an interior node.</summary>
        public readonly int Count = count;

        /// <summary>The right child, for an interior node.</summary>
        public readonly int Right = right;
    }

    const float Epsilon = 1e-7f;

    readonly Vector3[] vertices;
    readonly int[] indices;
    readonly int[] order;
    readonly Node[] nodes;
    readonly int nodeCount;

    /// <summary>Builds a hierarchy over a triangle list.</summary>
    /// <param name="vertices">The positions.</param>
    /// <param name="indices">Three per triangle.</param>
    /// <param name="leafSize">The largest run a leaf may hold.</param>
    /// <exception cref="ArgumentException">The indices are not triples, or there are none.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An empty leaf size.</exception>
    public TriangleBvh(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices, int leafSize = 4) {
        ArgumentOutOfRangeException.ThrowIfLessThan(leafSize, 1);

        if (indices.Length == 0 || indices.Length % 3 != 0) {
            throw new ArgumentException("triangles are triples, and a hierarchy over none referees nothing", nameof(indices));
        }

        this.vertices = vertices.ToArray();
        this.indices = indices.ToArray();

        TriangleCount = indices.Length / 3;
        order = new int[TriangleCount];

        for (var triangle = 0; triangle < TriangleCount; triangle++) {
            order[triangle] = triangle;
        }

        var centroids = new Vector3[TriangleCount];

        for (var triangle = 0; triangle < TriangleCount; triangle++) {
            centroids[triangle] = (Vertex(triangle, 0) + Vertex(triangle, 1) + Vertex(triangle, 2)) / 3f;
        }

        nodes = new Node[(2 * TriangleCount) - 1];
        nodeCount = 0;
        Build(0, TriangleCount, leafSize, centroids, ref nodeCount);
    }

    /// <summary>How many triangles the build holds.</summary>
    public int TriangleCount { get; }

    /// <summary>How many nodes the hierarchy has.</summary>
    public int NodeCount => nodeCount;

    /// <summary>The nearest hit along a ray, or a miss.</summary>
    /// <param name="origin">Where the ray starts.</param>
    /// <param name="direction">Which way it goes. Normalised for you, because a distance along a
    ///     ray of any other length is not a distance.</param>
    /// <param name="maxDistance">How far it looks.</param>
    public RayHit Trace(Vector3 origin, Vector3 direction, float maxDistance = float.PositiveInfinity) {
        var ray = Vector3.Normalize(direction);
        var inverse = new Vector3(1f / ray.X, 1f / ray.Y, 1f / ray.Z);

        var nearest = maxDistance;
        var best = -1;
        var visited = 0;

        Span<int> stack = stackalloc int[64];
        var depth = 0;

        stack[depth++] = 0;

        while (depth > 0) {
            var node = nodes[stack[--depth]];

            visited++;

            if (!Intersects(node, origin, inverse, nearest)) {
                continue;
            }

            if (node.Count > 0) {
                for (var offset = 0; offset < node.Count; offset++) {
                    var triangle = order[node.Start + offset];

                    if (Moller(triangle, origin, ray, nearest) is { } distance) {
                        nearest = distance;
                        best = triangle;
                    }
                }

                continue;
            }

            // Near child first, far child second on the stack — which is what lets the nearest
            // hit close the far subtree without ever opening it.
            var left = node.Start;
            var right = node.Right;
            var leftEntry = Entry(nodes[left], origin, inverse);
            var rightEntry = Entry(nodes[right], origin, inverse);

            if (leftEntry > rightEntry) {
                (left, right) = (right, left);
            }

            stack[depth++] = right;
            stack[depth++] = left;
        }

        if (best < 0) {
            return new(false, maxDistance, origin + (ray * MathF.Min(maxDistance, 1e6f)), Vector3.Zero, -1, visited);
        }

        var normal = Vector3.Normalize(
            Vector3.Cross(Vertex(best, 1) - Vertex(best, 0), Vertex(best, 2) - Vertex(best, 0))
        );

        // Geometric, facing the ray: a tracer's caller biases off the side it stands on, and a
        // normal pointing away is a bias into the surface.
        if (Vector3.Dot(normal, ray) > 0f) {
            normal = -normal;
        }

        return new(true, nearest, origin + (ray * nearest), normal, best, visited);
    }

    /// <summary>Every triangle, no hierarchy — the referee the traversal is held against.</summary>
    public RayHit BruteForce(Vector3 origin, Vector3 direction, float maxDistance = float.PositiveInfinity) {
        var ray = Vector3.Normalize(direction);
        var nearest = maxDistance;
        var best = -1;

        for (var triangle = 0; triangle < TriangleCount; triangle++) {
            if (Moller(triangle, origin, ray, nearest) is { } distance) {
                nearest = distance;
                best = triangle;
            }
        }

        if (best < 0) {
            return new(false, maxDistance, origin + (ray * MathF.Min(maxDistance, 1e6f)), Vector3.Zero, -1, TriangleCount);
        }

        var normal = Vector3.Normalize(
            Vector3.Cross(Vertex(best, 1) - Vertex(best, 0), Vertex(best, 2) - Vertex(best, 0))
        );

        if (Vector3.Dot(normal, ray) > 0f) {
            normal = -normal;
        }

        return new(true, nearest, origin + (ray * nearest), normal, best, TriangleCount);
    }

    Vector3 Vertex(int triangle, int corner) => vertices[indices[(triangle * 3) + corner]];

    /// <summary>Möller–Trumbore, the published formulation — re-derived and credited, not copied.</summary>
    float? Moller(int triangle, Vector3 origin, Vector3 ray, float nearest) {
        var a = Vertex(triangle, 0);
        var edge1 = Vertex(triangle, 1) - a;
        var edge2 = Vertex(triangle, 2) - a;
        var h = Vector3.Cross(ray, edge2);
        var determinant = Vector3.Dot(edge1, h);

        // Parallel, from either side: no crossing. Two-sided deliberately — a tracer that culls
        // back faces is the cube capture's brightest-possible-wrong-answer warning all over again.
        if (MathF.Abs(determinant) < Epsilon) {
            return null;
        }

        var inverse = 1f / determinant;
        var s = origin - a;
        var u = inverse * Vector3.Dot(s, h);

        if (u is < 0f or > 1f) {
            return null;
        }

        var q = Vector3.Cross(s, edge1);
        var v = inverse * Vector3.Dot(ray, q);

        if (v < 0f || u + v > 1f) {
            return null;
        }

        var distance = inverse * Vector3.Dot(edge2, q);

        return distance > Epsilon && distance < nearest ? distance : null;
    }

    static bool Intersects(in Node node, Vector3 origin, Vector3 inverse, float nearest) {
        var t1 = (node.Minimum - origin) * inverse;
        var t2 = (node.Maximum - origin) * inverse;
        var near = Vector3.Min(t1, t2);
        var far = Vector3.Max(t1, t2);
        var entry = MathF.Max(MathF.Max(near.X, near.Y), MathF.Max(near.Z, 0f));
        var exit = MathF.Min(MathF.Min(far.X, far.Y), far.Z);

        return entry <= exit && entry < nearest;
    }

    static float Entry(in Node node, Vector3 origin, Vector3 inverse) {
        var t1 = (node.Minimum - origin) * inverse;
        var t2 = (node.Maximum - origin) * inverse;
        var near = Vector3.Min(t1, t2);

        return MathF.Max(MathF.Max(near.X, near.Y), MathF.Max(near.Z, 0f));
    }

    int Build(int start, int count, int leafSize, Vector3[] centroids, ref int next) {
        var index = next++;
        var low = new Vector3(float.MaxValue);
        var high = new Vector3(float.MinValue);

        for (var offset = 0; offset < count; offset++) {
            for (var corner = 0; corner < 3; corner++) {
                var vertex = Vertex(order[start + offset], corner);

                low = Vector3.Min(low, vertex);
                high = Vector3.Max(high, vertex);
            }
        }

        if (count <= leafSize) {
            nodes[index] = new(low, high, start, count, 0);

            return index;
        }

        // The longest axis of the centroid spread, split at the median — deterministic from the
        // input alone, so two builds agree and a test can hold the shape.
        var spreadLow = new Vector3(float.MaxValue);
        var spreadHigh = new Vector3(float.MinValue);

        for (var offset = 0; offset < count; offset++) {
            var centroid = centroids[order[start + offset]];

            spreadLow = Vector3.Min(spreadLow, centroid);
            spreadHigh = Vector3.Max(spreadHigh, centroid);
        }

        var spread = spreadHigh - spreadLow;
        var axis = spread.X >= spread.Y && spread.X >= spread.Z ? 0 : spread.Y >= spread.Z ? 1 : 2;
        var half = count / 2;

        var slice = order.AsSpan(start, count);

        slice.Sort(
            (first, second) => Component(centroids[first], axis).CompareTo(Component(centroids[second], axis))
        );

        var left = Build(start, half, leafSize, centroids, ref next);
        var right = Build(start + half, count - half, leafSize, centroids, ref next);

        nodes[index] = new(low, high, left, 0, right);

        return index;
    }

    static float Component(Vector3 value, int index) => index == 0 ? value.X : index == 1 ? value.Y : value.Z;
}
