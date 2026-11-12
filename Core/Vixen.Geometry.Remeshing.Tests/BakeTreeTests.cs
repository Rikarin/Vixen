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

    /// <summary>The nearest hit, on any soup, is the plain tree's nearest hit.</summary>
    [Fact]
    [Trait("Category", "Property")]
    public void Raycast_finds_what_the_plain_tree_finds() =>
        Soup.Sample(
            static sample => {
                var indices = Indices(sample.Points.Length);
                var plain = new TriangleTree(sample.Points, indices);
                var wide = new BakeTree(sample.Points, indices);

                for (var at = 0; at + 1 < sample.Rays.Length; at += 2) {
                    var expected = plain.Raycast(sample.Rays[at], sample.Rays[at + 1]);
                    var actual = wide.Raycast(sample.Rays[at], sample.Rays[at + 1]);

                    Assert.Equal(expected.Triangle >= 0, actual.Triangle >= 0);

                    if (expected.Triangle >= 0) {
                        Assert.Equal(expected.Distance, actual.Distance, 5);
                    }
                }
            },
            iter: 300
        );

    /// <summary>A ray is blocked exactly when the plain tree finds something on it.</summary>
    [Fact]
    [Trait("Category", "Property")]
    public void Occluded_agrees_with_the_plain_tree() =>
        Soup.Sample(
            static sample => {
                var indices = Indices(sample.Points.Length);
                var plain = new TriangleTree(sample.Points, indices);
                var wide = new BakeTree(sample.Points, indices);

                for (var at = 0; at + 1 < sample.Rays.Length; at += 2) {
                    Assert.Equal(
                        plain.Raycast(sample.Rays[at], sample.Rays[at + 1]).Triangle >= 0,
                        wide.Occluded(sample.Rays[at], sample.Rays[at + 1])
                    );
                }
            },
            iter: 300
        );

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
