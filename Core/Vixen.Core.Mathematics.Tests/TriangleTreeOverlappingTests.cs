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
}
