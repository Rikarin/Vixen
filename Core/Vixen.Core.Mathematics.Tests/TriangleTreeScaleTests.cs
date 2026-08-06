// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Core.Mathematics.Tests;

/// <summary>A ray hits the same triangles whatever units the model was authored in.</summary>
/// <remarks>
///     <para>
///         <b>The fourth appearance in this repository of one mistake, and the first one caught by a
///         test rather than by a symptom.</b> <see cref="MathUtil.ZeroTolerance" /> is an absolute
///         <c>1e-6</c>, and every quantity it was guarding here carries the model's units — the
///         Möller–Trumbore determinant is <c>edge1 · (direction × edge2)</c> and so carries them
///         <i>squared</i>, a barycentric denominator likewise, a segment's squared length likewise.
///         So a fixed threshold is a statement about how small a model may be before its geometry
///         stops existing.
///     </para>
///     <para>
///         ⚠ <b>The symptom is never a crash and rarely looks like scale.</b> It surfaced as a UV
///         charter cutting five of nine shapes differently at 1/1024, because occlusion rays were
///         passing straight through the surface they were meant to be blocked by — three steps
///         downstream of the arithmetic, in a component that had no reason to suspect the ray cast.
///         Its siblings surfaced as a triangulation that fanned, and as a cross field that came out
///         entirely zero.
///     </para>
/// </remarks>
public class TriangleTreeScaleTests {
    /// <summary>
    ///     ⚠ Powers of two, so the geometry is *exactly* proportional and any difference is the
    ///     predicate rather than the mesh. A tenth is not a binary fraction and a test using one
    ///     cannot tell the two apart.
    /// </summary>
    [Theory]
    [InlineData(1f / 1024f)]
    [InlineData(1f / 64f)]
    [InlineData(1f)]
    [InlineData(64f)]
    [InlineData(1024f)]
    public void ARayHitsTheSameTrianglesAtEveryScale(float scale) {
        var (vertices, indices) = Fan();
        var scaled = vertices.Select(vertex => vertex * scale).ToArray();

        var reference = new TriangleTree(vertices, indices);
        var tree = new TriangleTree(scaled, indices);

        var hits = 0;

        for (var x = -4; x <= 4; x++) {
            for (var y = -4; y <= 4; y++) {
                var origin = new Vector3(x * 0.1f, y * 0.1f, -2f);
                var expected = reference.Raycast(origin, new(0f, 0f, 1f), out var expectedBack);
                var actual = tree.Raycast(origin * scale, new(0f, 0f, 1f), out var actualBack);

                Assert.True(
                    expected == actual,
                    $"At scale {scale} the ray from {origin} {(expected ? "missed what it should have hit" : "hit what it should have missed")}."
                );

                Assert.Equal(expectedBack, actualBack);

                if (expected) {
                    hits++;
                }
            }
        }

        // ⚠ A sweep that hit nothing would pass every assertion above and prove nothing at all.
        Assert.True(hits > 20, $"Only {hits} of 81 rays hit the fan; the fixture is not exercising the predicate.");
    }

    /// <summary>The closest-point query, which reaches the same epsilon by a different road.</summary>
    [Theory]
    [InlineData(1f / 1024f)]
    [InlineData(1f)]
    [InlineData(1024f)]
    public void TheClosestTriangleIsTheSameAtEveryScale(float scale) {
        var (vertices, indices) = Fan();
        var scaled = vertices.Select(vertex => vertex * scale).ToArray();

        var reference = new TriangleTree(vertices, indices);
        var tree = new TriangleTree(scaled, indices);

        for (var x = -3; x <= 3; x++) {
            for (var y = -3; y <= 3; y++) {
                var point = new Vector3(x * 0.2f, y * 0.2f, 0.35f);

                var expected = reference.Closest(point);
                var actual = tree.Closest(point * scale);

                // ⚠ The *point*, not the triangle index, and the difference is not pedantry. A query
                // equidistant from two triangles that share an edge has two equally correct answers,
                // and this fan's spokes put a whole row of the sweep exactly there — so an index
                // comparison fails on a result that is right. What must agree is where on the
                // surface the query landed.
                var recovered = actual.Point / scale;

                Assert.Equal(expected.Point.X, recovered.X, 3);
                Assert.Equal(expected.Point.Y, recovered.Y, 3);
                Assert.Equal(expected.Point.Z, recovered.Z, 3);

                // ⚠ And where the answer is unambiguous — strictly inside one triangle, no
                // barycentric component at zero — the index must agree too, because there is no tie
                // to break. This is the half that would catch a query falling onto an edge because a
                // denominator was compared against an absolute epsilon.
                var interior = expected.Barycentric.X > 1e-3f
                    && expected.Barycentric.Y > 1e-3f
                    && expected.Barycentric.Z > 1e-3f;

                if (interior) {
                    Assert.Equal(expected.Triangle, actual.Triangle);

                    Assert.Equal(expected.Barycentric.X, actual.Barycentric.X, 3);
                    Assert.Equal(expected.Barycentric.Y, actual.Barycentric.Y, 3);
                    Assert.Equal(expected.Barycentric.Z, actual.Barycentric.Z, 3);
                }
            }
        }
    }

    /// <summary>A fan of small triangles around a point, which is what a sphere's pole actually is.</summary>
    static (Vector3[] Vertices, int[] Indices) Fan() {
        List<Vector3> vertices = [new(0f, 0f, 0f)];
        List<int> indices = [];

        const int Segments = 12;

        for (var segment = 0; segment < Segments; segment++) {
            var angle = segment * MathF.Tau / Segments;

            vertices.Add(new(MathF.Cos(angle) * 0.5f, MathF.Sin(angle) * 0.5f, 0f));
        }

        for (var segment = 0; segment < Segments; segment++) {
            indices.Add(0);
            indices.Add(1 + segment);
            indices.Add(1 + ((segment + 1) % Segments));
        }

        return ([.. vertices], [.. indices]);
    }
}
