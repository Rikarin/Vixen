// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>The generalised winding number of a triangle soup — an inside for input that has no inside.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/41 § D3 step 7, and it is why the shrinkwrap can exist at all.</b> A signed
///         distance field needs a closed, non-self-intersecting surface to be signed <i>by</i>. The
///         input this library exists for is neither: marching-cubes extraction self-intersects per
///         cell, and a generated mesh routinely has holes in it. The generalised winding number is
///         defined for any soup — it is the solid angle the surface subtends at a point, over 4π —
///         and it degrades gracefully rather than failing: near one deep inside, near zero outside,
///         and something in between near a hole, which is exactly where the answer genuinely is in
///         between. Jacobson et al., <i>Robust Inside-Outside Segmentation using Generalized Winding
///         Numbers</i> (2013).
///     </para>
///     <para>
///         ⚠ <b>Evaluated through a tree with a per-node dipole, not as a sum over every triangle.</b>
///         The exact sum is one <c>atan2</c> per triangle per query, and a shrinkwrap is a grid of
///         queries — a hundred thousand of them against twenty thousand triangles is two billion
///         transcendental calls, which is not a stage, it is an outage. Barill et al., <i>Fast
///         Winding Numbers for Soups and Clouds</i> (2018): a cluster far from the query is a single
///         dipole whose moment is the cluster's area-weighted normal sum, and only clusters the query
///         is inside the influence radius of are opened.
///     </para>
///     <para>
///         Ours carries the zeroth-order term of that expansion and not the Taylor terms above it,
///         which trades a few percent of accuracy for a great deal less code. The threshold this
///         feeds is one half — the furthest possible point from both saturating values — and the
///         caller is the escape hatch of last resort, whose own remarks say it destroys thin
///         features. A percent of winding number is not what will be wrong with the result.
///     </para>
/// </remarks>
sealed class WindingNumberField {
    /// <summary>How many node radii away a query has to be before the dipole stands in for the node.</summary>
    /// <remarks>Barill's β. Two is their figure with the higher-order terms present; with only the
    ///     dipole, three buys the accuracy back for a modest amount of extra traversal.</remarks>
    public const float Beta = 3f;

    /// <summary>The most triangles a node holds before it splits.</summary>
    public const int LeafSize = 8;

    readonly Vector3[] positions;
    readonly int[] triangles;
    readonly int[] order;

    readonly int[] first;
    readonly int[] count;
    readonly int[] left;
    readonly int[] right;

    readonly Vector3[] moment;
    readonly Vector3[] centre;
    readonly float[] radius;

    WindingNumberField(
        Vector3[] positions,
        int[] triangles,
        int[] order,
        int[] first,
        int[] count,
        int[] left,
        int[] right,
        Vector3[] moment,
        Vector3[] centre,
        float[] radius
    ) {
        this.positions = positions;
        this.triangles = triangles;
        this.order = order;
        this.first = first;
        this.count = count;
        this.left = left;
        this.right = right;
        this.moment = moment;
        this.centre = centre;
        this.radius = radius;
    }

    /// <summary>Whether there is any surface to be inside of.</summary>
    public bool IsEmpty => triangles.Length == 0;

    /// <summary>The winding number at a point: about one deep inside, about zero outside.</summary>
    /// <param name="query">Where.</param>
    /// <returns>The number. Not clamped — a doubly-wound region legitimately reads two.</returns>
    public float At(Vector3 query) {
        if (triangles.Length == 0) {
            return 0f;
        }

        var total = 0d;
        var stack = new Stack<int>();

        stack.Push(0);

        while (stack.Count > 0) {
            var node = stack.Pop();
            var distance = Vector3.Distance(query, centre[node]);

            if (distance > Beta * radius[node] && distance > 0f) {
                var offset = centre[node] - query;

                total += Vector3.Dot(moment[node], offset) / (distance * distance * distance);
                continue;
            }

            if (left[node] < 0) {
                for (var at = first[node]; at < first[node] + count[node]; at++) {
                    total += SolidAngle(query, order[at]);
                }

                continue;
            }

            stack.Push(left[node]);
            stack.Push(right[node]);
        }

        return (float) (total / (4d * Math.PI));
    }

    /// <summary>Whether a point is inside, by the half threshold.</summary>
    /// <param name="query">Where.</param>
    /// <returns>Whether the winding number is at least one half.</returns>
    /// <remarks>
    ///     ⚠ One half rather than a small epsilon above zero, and Jacobson et al. § 4 is why: a point
    ///     just outside a closed surface reads a little above zero and a point just inside reads a
    ///     little below one, so the value furthest from both errors is the one exactly between them.
    /// </remarks>
    public bool IsInside(Vector3 query) => At(query) >= 0.5f;

    /// <summary>Builds the tree.</summary>
    /// <param name="positions">The vertices. Retained.</param>
    /// <param name="triangles">Three indices per triangle. Retained.</param>
    /// <returns>The field.</returns>
    public static WindingNumberField Build(Vector3[] positions, int[] triangles) {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(triangles);

        var total = triangles.Length / 3;
        var order = new int[total];

        for (var index = 0; index < total; index++) {
            order[index] = index;
        }

        List<int> first = [];
        List<int> count = [];
        List<int> left = [];
        List<int> right = [];
        List<Vector3> moment = [];
        List<Vector3> centre = [];
        List<float> radius = [];

        var field = new WindingNumberField(
            positions,
            triangles,
            order,
            [],
            [],
            [],
            [],
            [],
            [],
            []
        );

        if (total > 0) {
            field.Split(0, total, first, count, left, right, moment, centre, radius);
        } else {
            first.Add(0);
            count.Add(0);
            left.Add(-1);
            right.Add(-1);
            moment.Add(Vector3.Zero);
            centre.Add(Vector3.Zero);
            radius.Add(0f);
        }

        return new(
            positions,
            triangles,
            order,
            [.. first],
            [.. count],
            [.. left],
            [.. right],
            [.. moment],
            [.. centre],
            [.. radius]
        );
    }

    /// <summary>Builds one node over <c>order[start .. start + length]</c> and returns its index.</summary>
    /// <remarks>
    ///     ⚠ <b>Median split on the longest axis of the centroid bounds, by a full sort rather than by
    ///     a partition on the midpoint.</b> docs/plan/41 § D14 makes byte-identical output a gate, and
    ///     a spatial-median partition is the one place a BVH build can depend on the order the
    ///     triangles happened to be listed in. A sort by centroid with the triangle index as the
    ///     tie-break has one answer.
    /// </remarks>
    int Split(
        int start,
        int length,
        List<int> first,
        List<int> count,
        List<int> left,
        List<int> right,
        List<Vector3> moment,
        List<Vector3> centre,
        List<float> radius
    ) {
        var node = first.Count;

        first.Add(start);
        count.Add(length);
        left.Add(-1);
        right.Add(-1);
        moment.Add(Vector3.Zero);
        centre.Add(Vector3.Zero);
        radius.Add(0f);

        var sum = Vector3.Zero;
        var weighted = Vector3.Zero;
        var area = 0f;

        for (var at = start; at < start + length; at++) {
            var triangle = order[at];
            var (a, b, c) = Corners(triangle);
            var cross = Vector3.Cross(b - a, c - a) * 0.5f;
            var size = cross.Length();

            sum += cross;
            weighted += (a + b + c) / 3f * size;
            area += size;
        }

        var middle = area > 0f ? weighted / area : Centroid(start, length);

        var extent = 0f;

        for (var at = start; at < start + length; at++) {
            var (a, b, c) = Corners(order[at]);

            extent = MathF.Max(extent, Vector3.Distance(a, middle));
            extent = MathF.Max(extent, Vector3.Distance(b, middle));
            extent = MathF.Max(extent, Vector3.Distance(c, middle));
        }

        moment[node] = sum;
        centre[node] = middle;
        radius[node] = extent;

        if (length <= LeafSize) {
            return node;
        }

        var low = Centroid(start, 1);
        var high = low;

        for (var at = start; at < start + length; at++) {
            var point = Centroid(at, 1);

            low = Vector3.Min(low, point);
            high = Vector3.Max(high, point);
        }

        var size2 = high - low;
        var axis = size2.X >= size2.Y && size2.X >= size2.Z ? 0 : size2.Y >= size2.Z ? 1 : 2;

        Array.Sort(
            order,
            start,
            length,
            Comparer<int>.Create(
                (one, two) => {
                    var a = Axis(Middle(one), axis);
                    var b = Axis(Middle(two), axis);

                    return a != b ? a.CompareTo(b) : one.CompareTo(two);
                }
            )
        );

        var half = length / 2;

        left[node] = Split(start, half, first, count, left, right, moment, centre, radius);
        right[node] = Split(start + half, length - half, first, count, left, right, moment, centre, radius);

        return node;
    }

    Vector3 Centroid(int at, int length) {
        var total = Vector3.Zero;

        for (var index = at; index < at + length; index++) {
            total += Middle(order[index]);
        }

        return length == 0 ? Vector3.Zero : total / length;
    }

    Vector3 Middle(int triangle) {
        var (a, b, c) = Corners(triangle);

        return (a + b + c) / 3f;
    }

    (Vector3 A, Vector3 B, Vector3 C) Corners(int triangle) => (
        positions[triangles[(triangle * 3) + 0]],
        positions[triangles[(triangle * 3) + 1]],
        positions[triangles[(triangle * 3) + 2]]
    );

    /// <summary>The signed solid angle one triangle subtends at a point.</summary>
    /// <remarks>
    ///     Van Oosterom and Strackee (1983). ⚠ <c>Atan2</c> rather than <c>Acos</c> of a normalised
    ///     dot: the arc-cosine form loses every bit of precision as the angle approaches zero, which
    ///     is the case for all but a handful of the triangles in any given query — so the error that
    ///     matters is exactly the one it is worst at.
    /// </remarks>
    double SolidAngle(Vector3 query, int triangle) {
        var (pa, pb, pc) = Corners(triangle);

        var a = pa - query;
        var b = pb - query;
        var c = pc - query;

        var la = a.Length();
        var lb = b.Length();
        var lc = c.Length();

        if (la <= 0f || lb <= 0f || lc <= 0f) {
            // The query is exactly on a corner. The solid angle is undefined there and the caller is
            // sampling a grid, so the neighbouring samples answer for it.
            return 0d;
        }

        var numerator = (double) Vector3.Dot(a, Vector3.Cross(b, c));

        var denominator = ((double) la * lb * lc)
            + (Vector3.Dot(a, b) * (double) lc)
            + (Vector3.Dot(b, c) * (double) la)
            + (Vector3.Dot(c, a) * (double) lb);

        return 2d * Math.Atan2(numerator, denominator);
    }

    static float Axis(Vector3 value, int axis) => axis == 0 ? value.X : axis == 1 ? value.Y : value.Z;
}
