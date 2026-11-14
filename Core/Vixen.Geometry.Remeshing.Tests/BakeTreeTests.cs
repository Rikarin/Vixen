// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>The wide tree has to answer exactly what the plain one answers, only faster.</summary>
/// <remarks>
///     ⚠
///     <b>
///         <see cref="TriangleTree" /> is the oracle, and it shares no traversal with the tree under
///         test.
///     </b>
///     A wide tree's bugs are a box test that drops a child and a leaf that loses a triangle,
///     and both look like a few rays quietly missing, so the only check worth having is the same
///     question asked of both on random soups.
/// </remarks>
public sealed class BakeTreeTests {
    /// <summary>Coordinates on a fine lattice.</summary>
    /// <remarks>
    ///     ⚠ <b>Not <c>Gen.Single</c>, which favours zeros, denormals and exact powers.</b> A ray lying
    ///     in a slab plane to the last bit gets a different answer from each tree's rounding and neither
    ///     is wrong: the plain tree's own tests pin the cases it means to keep.
    /// </remarks>
    static readonly Gen<float> Coordinate =
        Gen.Int[-1 << 20, 1 << 20].Select(static value => (value + 0.37f) / (1 << 18));

    static readonly Gen<(Vector3[] Points, Vector3[] Rays)> Soup =
        Gen.Int[1, 120]
            .SelectMany(static count =>
                Coordinate.Array[count * 9]
                    .SelectMany(static corners =>
                        Coordinate.Array[60]
                            .Select(rays =>
                                (Vectors(corners), Vectors(rays))
                            )
                    )
            );

    static Vector3[] Vectors(float[] values) {
        var vectors = new Vector3[values.Length / 3];

        for (var at = 0; at < vectors.Length; at++) {
            vectors[at] = new(values[at * 3], values[at * 3 + 1], values[at * 3 + 2]);
        }

        return vectors;
    }

    static int[] Indices(int count) => [..Enumerable.Range(0, count)];

    /// <summary>
    ///     The nearest hit, on any soup, is the plain tree's nearest hit, or a nearer one the edge tolerance lets in.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The wide tree takes a share of each triangle past its edges and the plain tree does not</b>, which is
    ///     what makes a ray through a shared edge strike one of the two (Patina #287). So on a random soup it may strike
    ///     a triangle the plain tree just misses: 78 rays in about six million, each within the tolerance of an edge.
    ///     Asserting equality failed one run in a few hundred. <see cref="Grazes" /> is what the tolerance allows,
    ///     measured apart from both trees, and the wide tree may never miss what the plain tree strikes.
    /// </remarks>
    [Fact]
    [Trait("Category", "Property")]
    public void Raycast_finds_what_the_plain_tree_finds() =>
        Soup.Sample(
            static sample => {
                var indices = Indices(sample.Points.Length);
                var plain = new TriangleTree(sample.Points, indices);
                var wide = new BakeTree(sample.Points, indices);

                for (var at = 0; at + 1 < sample.Rays.Length; at += 2) {
                    var (origin, direction) = (sample.Rays[at], sample.Rays[at + 1]);
                    var expected = plain.Raycast(origin, direction);
                    var actual = wide.Raycast(origin, direction);

                    // Both missed, or both struck at the same place.
                    if (expected.Triangle < 0
                            ? actual.Triangle < 0
                            : MathF.Abs(expected.Distance - actual.Distance) < 1e-5f) {
                        continue;
                    }

                    Assert.True(actual.Triangle >= 0, "The wide tree missed what the plain tree struck.");
                    Assert.True(expected.Triangle < 0 || actual.Distance < expected.Distance);
                    Assert.True(Grazes(sample.Points, actual.Triangle, origin, direction), Said(origin, direction));
                }
            },
            iter: 300
        );

    /// <summary>
    ///     A ray is blocked when the plain tree finds something on it, and otherwise only where the edge tolerance
    ///     lets a hit in.
    /// </summary>
    [Fact]
    [Trait("Category", "Property")]
    public void Occluded_agrees_with_the_plain_tree() =>
        Soup.Sample(
            static sample => {
                var indices = Indices(sample.Points.Length);
                var plain = new TriangleTree(sample.Points, indices);
                var wide = new BakeTree(sample.Points, indices);

                for (var at = 0; at + 1 < sample.Rays.Length; at += 2) {
                    var (origin, direction) = (sample.Rays[at], sample.Rays[at + 1]);
                    var expected = plain.Raycast(origin, direction).Triangle >= 0;

                    if (expected == wide.Occluded(origin, direction)) {
                        continue;
                    }

                    Assert.False(expected, "The wide tree passed through what the plain tree struck.");
                    var near = wide.Raycast(origin, direction);
                    Assert.True(near.Triangle >= 0 && Grazes(sample.Points, near.Triangle, origin, direction));
                }
            },
            iter: 300
        );

    /// <summary>
    ///     Whether a ray meets a triangle's plane within <see cref="BakeTree.EdgeTolerance" /> of its edges, and
    ///     not inside it: the hit the tolerance lets in, worked out in doubles, apart from both trees.
    /// </summary>
    static bool Grazes(Vector3[] points, int triangle, Vector3 origin, Vector3 direction) {
        var (a, b, c) = (Wide(points[triangle * 3]), Wide(points[triangle * 3 + 1]), Wide(points[triangle * 3 + 2]));
        var (o, d) = (Wide(origin), Wide(direction));
        var (e1, e2) = (Sub(b, a), Sub(c, a));
        var across = Cross(d, e2);
        var determinant = Dot(e1, across);
        if (Math.Abs(determinant) < 1e-12) {
            return false;
        }

        var to = Sub(o, a);
        var u = Dot(to, across) / determinant;
        var v = Dot(d, Cross(to, e1)) / determinant;
        var least = Math.Min(Math.Min(u, v), 1 - u - v);

        // Twice the tolerance, for the float rounding on the wide tree's side; and never deep inside the triangle,
        // where the plain tree would have struck it too.
        return least >= -2 * BakeTree.EdgeTolerance && least <= BakeTree.EdgeTolerance;
    }

    static string Said(Vector3 origin, Vector3 direction) =>
        $"A hit past the tolerance, from {origin} along {direction}.";

    static (double X, double Y, double Z) Wide(Vector3 v) => (v.X, v.Y, v.Z);

    static (double X, double Y, double Z) Sub((double X, double Y, double Z) l, (double X, double Y, double Z) r) =>
        (l.X - r.X, l.Y - r.Y, l.Z - r.Z);

    static double Dot((double X, double Y, double Z) l, (double X, double Y, double Z) r) =>
        l.X * r.X + l.Y * r.Y + l.Z * r.Z;

    static (double X, double Y, double Z) Cross((double X, double Y, double Z) l, (double X, double Y, double Z) r) =>
        (l.Y * r.Z - l.Z * r.Y, l.Z * r.X - l.X * r.Z, l.X * r.Y - l.Y * r.X);

    /// <summary>The closest point is at the plain tree's distance.</summary>
    [Fact]
    [Trait("Category", "Property")]
    public void Closest_is_as_close_as_the_plain_tree() =>
        Soup.Sample(
            static sample => {
                var indices = Indices(sample.Points.Length);
                var plain = new TriangleTree(sample.Points, indices);
                var wide = new BakeTree(sample.Points, indices);

                foreach (var point in sample.Rays) {
                    Assert.Equal(plain.Closest(point).DistanceSquared, wide.Closest(point).DistanceSquared, 4);
                }
            },
            iter: 300
        );

    /// <summary>A ray leaving a triangle is not stopped by it, at any scale.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the rule the scalar test exists for.</b> The vector filter has no tolerance, so
    ///     a tree that trusted it would report a hit at a distance of nothing, and at a small scale
    ///     every bake would be the closest-point fallback or solid black.
    /// </remarks>
    [Theory]
    [InlineData(1f)]
    [InlineData(1f / 1024f)]
    [InlineData(1024f)]
    public void A_ray_starting_on_a_triangle_is_not_stopped_by_it(float scale) {
        Vector3[] points =
            [new Vector3(-1f, -1f, 0f) * scale, new Vector3(3f, -1f, 0f) * scale, new Vector3(-1f, 3f, 0f) * scale];
        var tree = new BakeTree(points, Indices(3));

        Assert.False(tree.Occluded(Vector3.Zero, Vector3.UnitZ * scale));
        Assert.True(tree.Occluded(Vector3.UnitZ * scale, -Vector3.UnitZ * 2f * scale));
    }

    /// <summary>An empty tree answers rather than falling into its own root.</summary>
    [Fact]
    public void An_empty_tree_finds_nothing() {
        var tree = new BakeTree([], []);

        Assert.False(tree.Occluded(Vector3.Zero, Vector3.UnitX));
        Assert.Equal(-1, tree.Raycast(Vector3.Zero, Vector3.UnitX).Triangle);
        Assert.Equal(-1, tree.Closest(Vector3.Zero).Triangle);
    }

    /// <summary>Every triangle on one point leaves the heuristic nothing to split, and still builds.</summary>
    [Fact]
    public void A_pile_of_coincident_triangles_builds_and_traverses() {
        var points = new Vector3[3000];

        for (var at = 0; at < points.Length; at += 3) {
            points[at] = new(-1f, -1f, 0f);
            points[at + 1] = new(1f, -1f, 0f);
            points[at + 2] = new(0f, 1f, 0f);
        }

        var tree = new BakeTree(points, Indices(points.Length));

        Assert.True(tree.Occluded(new(0f, 0f, 1f), new(0f, 0f, -2f)));
    }

    /// <summary>A tree over some triangles strikes only those, and answers in the whole mesh's numbers.</summary>
    [Fact]
    public void A_tree_over_members_strikes_only_them_and_keeps_their_numbers() {
        Vector3[] points = [
            new(-1f, -1f, 0f), new(1f, -1f, 0f), new(0f, 1f, 0f),
            new(-1f, -1f, 1f), new(1f, -1f, 1f), new(0f, 1f, 1f)
        ];

        var tree = new BakeTree(points, Indices(6), [1]);
        var hit = tree.Raycast(new(0f, 0f, -1f), new(0f, 0f, 3f));

        Assert.Equal(1, hit.Triangle);
        Assert.Equal(1, tree.TriangleCount);
    }
}
