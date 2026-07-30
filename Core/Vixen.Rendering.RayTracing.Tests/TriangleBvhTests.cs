// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Rendering.RayTracing.Tests;

/// <summary>The build's closed forms, and the traversal against the brute force it must equal.</summary>
public class TriangleBvhTests {
    [Fact]
    public void ARayHitsWhereArithmeticSaysAndFromEitherSide() {
        // One triangle in the z = 2 plane; a ray down +Z from the origin crosses it at exactly two.
        Span<Vector3> vertices = [new(-1f, -1f, 2f), new(3f, -1f, 2f), new(-1f, 3f, 2f)];
        Span<int> indices = [0, 1, 2];

        var bvh = new TriangleBvh(vertices, indices);
        var hit = bvh.Trace(Vector3.Zero, new(0f, 0f, 1f));

        Assert.True(hit.Hit);
        Assert.Equal(2f, hit.Distance, 1e-5f);
        Assert.Equal(0f, hit.Position.X, 1e-5f);
        Assert.Equal(-1f, hit.Normal.Z, 1e-5f);

        // From behind: the same surface, two-sided, with the normal facing the ray — a tracer that
        // culled back faces is the cube capture's brightest-possible-wrong-answer all over again.
        var behind = bvh.Trace(new(0f, 0f, 4f), new(0f, 0f, -1f));

        Assert.True(behind.Hit);
        Assert.Equal(2f, behind.Distance, 1e-5f);
        Assert.Equal(1f, behind.Normal.Z, 1e-5f);

        // Parallel misses, outside-the-edges misses, and a budget shorter than the surface misses.
        Assert.False(bvh.Trace(Vector3.Zero, new(1f, 0f, 0f)).Hit);
        Assert.False(bvh.Trace(new(5f, 5f, 0f), new(0f, 0f, 1f)).Hit);
        Assert.False(bvh.Trace(Vector3.Zero, new(0f, 0f, 1f), 1.5f).Hit);
    }

    [Fact]
    public void TheNearestTriangleWinsWhateverTheOrder() {
        // Two parallel triangles; the ray must name the nearer one however the list orders them.
        Span<Vector3> vertices = [
            new(-1f, -1f, 5f), new(3f, -1f, 5f), new(-1f, 3f, 5f),
            new(-1f, -1f, 2f), new(3f, -1f, 2f), new(-1f, 3f, 2f)
        ];
        Span<int> indices = [0, 1, 2, 3, 4, 5];

        var hit = new TriangleBvh(vertices, indices).Trace(Vector3.Zero, new(0f, 0f, 1f));

        Assert.True(hit.Hit);
        Assert.Equal(1, hit.Triangle);
        Assert.Equal(2f, hit.Distance, 1e-5f);
    }

    [Fact]
    public void TheTraversalIsTheBruteForceAtAFractionOfTheVisits() {
        var bvh = Soup(400, out _);
        var seed = 99u;

        float Next() {
            seed = (seed * 1664525u) + 1013904223u;

            return (seed >> 8) * (1f / 16777216f);
        }

        var hits = 0;
        long traversal = 0;
        long brute = 0;

        for (var ray = 0; ray < 400; ray++) {
            var origin = new Vector3((Next() * 20f) - 10f, (Next() * 20f) - 10f, -12f);
            var direction = Vector3.Normalize(new((Next() - 0.5f) * 0.6f, (Next() - 0.5f) * 0.6f, 1f));

            var fast = bvh.Trace(origin, direction);
            var slow = bvh.BruteForce(origin, direction);

            Assert.Equal(slow.Hit, fast.Hit);

            if (slow.Hit) {
                Assert.Equal(slow.Triangle, fast.Triangle);
                Assert.Equal(slow.Distance, fast.Distance, 1e-5f);
                hits++;
            }

            traversal += fast.Visited;
            brute += slow.Visited;
        }

        Assert.True(hits > 100, $"only {hits} of 400 rays hit -- the soup referees too little");

        // The logarithm, measured: the hierarchy visits nodes, the brute force visits every
        // triangle, and the ratio is the entire reason acceleration structures exist.
        Assert.True(
            traversal * 4 < brute,
            $"the traversal visited {traversal} against the brute force's {brute} — no acceleration happened"
        );
    }

    [Fact]
    public void TwoBuildsAgreeToTheBit() {
        var first = Soup(200, out var vertices);
        var second = new TriangleBvh(vertices.Vertices, vertices.Indices);

        Assert.Equal(first.NodeCount, second.NodeCount);

        var origin = new Vector3(0.3f, -0.7f, -12f);
        var direction = Vector3.Normalize(new(0.1f, 0.05f, 1f));
        var one = first.Trace(origin, direction);
        var two = second.Trace(origin, direction);

        Assert.Equal(one, two);
    }

    /// <summary>A deterministic triangle soup in a slab the rays fly through.</summary>
    static TriangleBvh Soup(int triangles, out (Vector3[] Vertices, int[] Indices) mesh) {
        var seed = 7u;

        float Next() {
            seed = (seed * 1664525u) + 1013904223u;

            return (seed >> 8) * (1f / 16777216f);
        }

        var vertices = new Vector3[triangles * 3];
        var indices = new int[triangles * 3];

        for (var triangle = 0; triangle < triangles; triangle++) {
            var centre = new Vector3((Next() * 18f) - 9f, (Next() * 18f) - 9f, Next() * 16f - 8f);

            for (var corner = 0; corner < 3; corner++) {
                var at = (triangle * 3) + corner;

                vertices[at] = centre + (new Vector3(Next() - 0.5f, Next() - 0.5f, Next() - 0.5f) * 3f);
                indices[at] = at;
            }
        }

        mesh = (vertices, indices);

        return new(vertices, indices);
    }
}
