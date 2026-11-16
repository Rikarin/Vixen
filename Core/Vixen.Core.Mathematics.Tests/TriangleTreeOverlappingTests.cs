// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Core.Mathematics.Tests;

/// <summary>A sphere's triangles, less those wholly behind some planes, as a brush's square on a picture asks.</summary>
public class TriangleTreeOverlappingTests {
    /// <summary>
    ///     The planes take away exactly the triangles whose three corners are behind one of them, and keep the rest in
    ///     the order the sphere alone gives: the boxes the walk skips hold nothing a triangle test would have kept.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void The_planes_take_away_what_is_wholly_behind_one_and_keep_the_order(int seed) {
        var random = new Random(seed);
        var vertices = new Vector3[6000 * 3];

        for (var at = 0; at < vertices.Length; at += 3) {
            var corner = new Vector3(random.NextSingle(), random.NextSingle(), random.NextSingle()) * 4f
                - new Vector3(2f);

            for (var side = 0; side < 3; side++) {
                vertices[at + side] = corner
                    + new Vector3(random.NextSingle(), random.NextSingle(), random.NextSingle()) * 0.2f;
            }
        }

        var tree = new TriangleTree(vertices, [..Enumerable.Range(0, vertices.Length)]);
        var apex = new Vector3(0.1f, -0.2f, 5f);
        Vector3[] inward = [
            Vector3.Normalize(new(1f, 0f, -0.1f)), Vector3.Normalize(new(-1f, 0f, -0.1f)),
            Vector3.Normalize(new(0f, 1f, -0.1f)), Vector3.Normalize(new(0f, -1f, -0.1f))
        ];
        List<int> whole = [];
        List<int> seen = [];

        tree.Overlapping(new(0f, 0f, 0f), 1.5f, whole);
        tree.Overlapping(new(0f, 0f, 0f), 1.5f, apex, inward, seen);

        bool Behind(int triangle) =>
            inward.Any(normal => Enumerable.Range(0, 3)
                .All(side => Vector3.Dot(vertices[triangle * 3 + side] - apex, normal) < 0f)
            );

        var expected = whole.Where(triangle => !Behind(triangle)).ToList();

        Assert.Equal(expected, seen);
        Assert.InRange(seen.Count, 1, whole.Count / 2);
    }

    /// <summary>
    ///     A wide sphere takes whole boxes without testing their triangles (Patina #25), and still finds exactly the
    ///     triangles that come within it, in the order the walk reaches them: a narrower sphere's answer is the wider
    ///     one's with the further triangles left out, whichever boxes either took whole.
    /// </summary>
    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void Boxes_taken_whole_keep_every_triangle_and_the_order(int seed) {
        var vertices = Soup(new(seed), 4000);
        var tree = new TriangleTree(vertices, [..Enumerable.Range(0, vertices.Length)]);
        var centre = new Vector3(0.2f, -0.1f, 0.3f);
        List<int> wide = [];
        List<int> narrow = [];

        tree.Overlapping(centre, 2.5f, wide);
        tree.Overlapping(centre, 1.2f, narrow);

        float Distance(int triangle) =>
            new TriangleTree(vertices.AsSpan(triangle * 3, 3), [0, 1, 2]).DistanceSquared(centre);

        var within = Enumerable.Range(0, vertices.Length / 3).Where(triangle => Distance(triangle) <= 2.5f * 2.5f);

        Assert.Equal(within.Order(), wide.Order());
        Assert.Equal(wide.Where(triangle => Distance(triangle) <= 1.2f * 1.2f), narrow);
        Assert.InRange(narrow.Count, 1, wide.Count / 2);
    }

    /// <summary>Small triangles scattered through a cube four across, round the origin.</summary>
    static Vector3[] Soup(Random random, int count) {
        var vertices = new Vector3[count * 3];

        for (var at = 0; at < vertices.Length; at += 3) {
            var corner = new Vector3(random.NextSingle(), random.NextSingle(), random.NextSingle()) * 4f
                - new Vector3(2f);

            for (var side = 0; side < 3; side++) {
                vertices[at + side] = corner
                    + new Vector3(random.NextSingle(), random.NextSingle(), random.NextSingle()) * 0.2f;
            }
        }

        return vertices;
    }
}
