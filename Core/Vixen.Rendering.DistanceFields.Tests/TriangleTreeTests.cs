// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Rendering.DistanceFields.Tests;

public class TriangleTreeTests {
    static readonly Vector3 A = new(0, 0, 0);
    static readonly Vector3 B = new(1, 0, 0);
    static readonly Vector3 C = new(0, 1, 0);

    [Fact]
    public void ClosestPointOverTheFaceIsTheProjection() {
        var closest = TriangleTree.ClosestPointOnTriangle(new(0.25f, 0.25f, 3f), A, B, C);

        Assert.Equal(0.25f, closest.X, 5);
        Assert.Equal(0.25f, closest.Y, 5);
        Assert.Equal(0f, closest.Z, 5);
    }

    [Fact]
    public void ClosestPointBeyondTheHypotenuseIsOnThatEdge() {
        // The trap the projection-and-clamp shortcut falls into: this point projects to (2, 2, 0),
        // which is well outside the triangle, and the true answer is the midpoint of BC rather than
        // anything the clamp produces.
        var closest = TriangleTree.ClosestPointOnTriangle(new(2f, 2f, 1f), A, B, C);

        Assert.Equal(0.5f, closest.X, 4);
        Assert.Equal(0.5f, closest.Y, 4);
        Assert.Equal(0f, closest.Z, 4);
    }

    [Fact]
    public void ClosestPointBeyondAVertexIsThatVertex() {
        var closest = TriangleTree.ClosestPointOnTriangle(new(-2f, -2f, 0f), A, B, C);

        Assert.Equal(A, closest);
    }

    [Fact]
    public void ADegenerateTriangleIsMeasuredRatherThanDividedBy() {
        // Every UV sphere has a fan of these at each pole, where a whole ring of vertices is one
        // point. The barycentric solution divides by the area, so a zero-area triangle returns NaN —
        // and a NaN distance propagates through the minimum and poisons the whole field.
        // Every pairing, because each collapsed edge divides by a different one of the three
        // denominators and only one of them is the pole fan's.
        foreach (var (a, b, c) in ((Vector3 A, Vector3 B, Vector3 C)[]) [
            (A, A, B), (A, B, B), (A, B, A), (A, A, A)
        ]) {
            foreach (var probe in (Vector3[]) [new(0, 0, 1), new(2, 2, 0), new(-1, -1, -1), new(0.5f, 0, 0)]) {
                var closest = TriangleTree.ClosestPointOnTriangle(probe, a, b, c);

                Assert.False(
                    float.IsNaN(closest.X) || float.IsNaN(closest.Y) || float.IsNaN(closest.Z),
                    $"({a}, {b}, {c}) from {probe} produced {closest}"
                );
            }
        }

        // And a collinear-but-distinct triangle, which has three real edges and still no interior.
        var flat = TriangleTree.ClosestPointOnTriangle(new(0.5f, 1f, 0f), A, B, new(2, 0, 0));

        Assert.Equal(0.5f, flat.X, 4);
        Assert.Equal(0f, flat.Y, 4);
    }

    [Fact]
    public void ARayFromTheFrontDoesNotReportABackface() {
        var tree = new TriangleTree([A, B, C], [0, 1, 2]);

        // (0, 0, 1) is the counter-clockwise normal of ABC, so approaching from +Z is the front.
        Assert.True(tree.Raycast(new(0.25f, 0.25f, 1f), new(0, 0, -1), out var backface));
        Assert.False(backface);
    }

    [Fact]
    public void ARayFromBehindReportsABackface() {
        var tree = new TriangleTree([A, B, C], [0, 1, 2]);

        Assert.True(tree.Raycast(new(0.25f, 0.25f, -1f), new(0, 0, 1), out var backface));
        Assert.True(backface);
    }

    [Fact]
    public void ARayThatMissesHitsNothing() {
        var tree = new TriangleTree([A, B, C], [0, 1, 2]);

        Assert.False(tree.Raycast(new(5f, 5f, 1f), new(0, 0, -1), out _));
    }

    [Fact]
    public void TheNearestHitWins() {
        // Two parallel triangles in the path of one ray, disagreeing about which face they show it.
        // Only an answer that is the *nearest* hit rather than whichever the traversal reached first
        // is stable, and the sign vote is built entirely out of this answer.
        Vector3[] vertices = [
            new(0, 0, 1), new(1, 0, 1), new(0, 1, 1),
            new(0, 0, 5), new(1, 0, 5), new(0, 1, 5)
        ];

        // The near triangle's normal is +Z — the same way the ray travels, so it is struck from
        // behind. The far one is wound the other way and is struck from the front.
        int[] indices = [0, 1, 2, 3, 5, 4];

        var tree = new TriangleTree(vertices, indices);

        Assert.True(tree.Raycast(new(0.25f, 0.25f, 0f), new(0, 0, 1), out var backface));
        Assert.True(backface);
    }

    [Fact]
    public void DistanceAgreesWithEveryTriangleTested() {
        var (vertices, indices) = Shapes.Sphere(1f, 16, 24);
        var tree = new TriangleTree(vertices, indices);

        // A deterministic lattice of probes rather than random ones, so a failure is reproducible.
        for (var x = -3; x <= 3; x++) {
            for (var y = -3; y <= 3; y++) {
                for (var z = -3; z <= 3; z++) {
                    var point = new Vector3(x, y, z) * 0.7f;
                    var brute = float.PositiveInfinity;

                    for (var triangle = 0; triangle < indices.Length / 3; triangle++) {
                        var closest = TriangleTree.ClosestPointOnTriangle(
                            point,
                            vertices[indices[triangle * 3]],
                            vertices[indices[(triangle * 3) + 1]],
                            vertices[indices[(triangle * 3) + 2]]
                        );

                        brute = MathF.Min(brute, Vector3.DistanceSquared(point, closest));
                    }

                    Assert.Equal(brute, tree.DistanceSquared(point), 4);
                }
            }
        }
    }

    [Fact]
    public void ABuildIsIndependentOfTheOrderTrianglesArriveIn() {
        // Not that the tree is identical — it is not — but that the answers are. Ties in the split
        // break on triangle id, so a mesh whose triangles are all coplanar cannot depend on the
        // sort's stability.
        var (vertices, indices) = Shapes.Box(new(0.5f));
        var shuffled = new int[indices.Length];

        for (var triangle = 0; triangle < indices.Length / 3; triangle++) {
            var source = ((indices.Length / 3) - 1 - triangle) * 3;
            indices.AsSpan(source, 3).CopyTo(shuffled.AsSpan(triangle * 3, 3));
        }

        var forward = new TriangleTree(vertices, indices);
        var reversed = new TriangleTree(vertices, shuffled);

        for (var x = -2; x <= 2; x++) {
            for (var y = -2; y <= 2; y++) {
                var point = new Vector3(x * 0.4f, y * 0.4f, 0.3f);

                Assert.Equal(forward.DistanceSquared(point), reversed.DistanceSquared(point), 5);
            }
        }
    }
}
