// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Xunit;

namespace Vixen.Core.Mathematics.Tests;

/// <summary>The acceleration structure, against brute force and against closed forms.</summary>
/// <remarks>
///     <para>
///         Moved here with the type from <c>Vixen.Rendering.DistanceFields.Tests</c>, assertion for
///         assertion, when <c>docs/plan/41-automatic-retopology.md</c> § D12 became its second
///         caller.
///     </para>
///     <para>
///         The two meshes are built here rather than borrowed from
///         <c>Vixen.Rendering.MeshPrimitives</c>, for the same reason the distance-field tests build
///         their own: these tests check a query against arithmetic, and borrowing a primitive would
///         make the renderer's winding conventions a silent input to the answer.
///     </para>
/// </remarks>
public class TriangleTreeTests {
    static readonly Vector3 A = new(0, 0, 0);
    static readonly Vector3 B = new(1, 0, 0);
    static readonly Vector3 C = new(0, 1, 0);

    // --- The closest point on one triangle ----------------------------------

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

    // --- Raycasting ---------------------------------------------------------

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

    // --- The ray hit, which is the query a bake asks ------------------------

    /// <summary>The hit names the triangle, the point, the distance and where on it.</summary>
    /// <remarks>
    ///     ⚠ <b>The boolean overload answers a sign vote and this one answers a bake.</b>
    ///     <c>docs/plan/41-automatic-retopology.md</c> § D12 casts along the output's interpolated
    ///     normal and writes the source's normal at the hit into an atlas texel, which needs the
    ///     triangle and the weights on it — neither of which a <c>bool</c> and a backface flag carry.
    /// </remarks>
    [Fact]
    public void ARayHitNamesTheTriangleAndWhereOnIt() {
        var tree = new TriangleTree([A, B, C], [0, 1, 2]);
        var hit = tree.Raycast(new(0.25f, 0.25f, 2f), new(0, 0, -4f));

        Assert.Equal(0, hit.Triangle);
        Assert.False(hit.Backface);
        Assert.Equal(0.5f, hit.Distance, 4);
        Assert.Equal(0.25f, hit.Point.X, 4);
        Assert.Equal(0.25f, hit.Point.Y, 4);
        Assert.Equal(0f, hit.Point.Z, 4);
        Assert.Equal(1f, Total(hit.Barycentric), 4);

        var rebuilt = (A * hit.Barycentric.X) + (B * hit.Barycentric.Y) + (C * hit.Barycentric.Z);

        Assert.Equal(hit.Point.X, rebuilt.X, 4);
        Assert.Equal(hit.Point.Y, rebuilt.Y, 4);
        Assert.Equal(hit.Point.Z, rebuilt.Z, 4);
    }

    /// <summary>The direction's length is the search radius, so a short ray stops short.</summary>
    /// <remarks>
    ///     ⚠ <b>Bounding by the direction rather than by a separate limit is what keeps the two from
    ///     disagreeing</b>, and it is what lets a bake express its cage as a fraction of the model's
    ///     diagonal — a limit in metres is a claim about how big the model is.
    /// </remarks>
    [Fact]
    public void ARayShorterThanTheSurfaceStopsShortOfIt() {
        var tree = new TriangleTree([A, B, C], [0, 1, 2]);

        Assert.Equal(-1, tree.Raycast(new(0.25f, 0.25f, 2f), new(0, 0, -1.5f)).Triangle);
        Assert.Equal(0, tree.Raycast(new(0.25f, 0.25f, 2f), new(0, 0, -2.5f)).Triangle);
    }

    /// <summary>A miss says so rather than naming a triangle at an infinite distance.</summary>
    [Fact]
    public void ARayHitThatMissesNamesNothing() {
        var tree = new TriangleTree([A, B, C], [0, 1, 2]);
        var hit = tree.Raycast(new(5f, 5f, 1f), new(0, 0, -4f));

        Assert.Equal(-1, hit.Triangle);
        Assert.Equal(float.PositiveInfinity, hit.Distance);
        Assert.Equal(Vector3.Zero, hit.Barycentric);
    }

    /// <summary>An empty tree is a miss and not an exception.</summary>
    [Fact]
    public void AnEmptyTreeIsHitByNothing() {
        Assert.Equal(-1, new TriangleTree([], []).Raycast(Vector3.Zero, Vector3.UnitZ).Triangle);
    }

    /// <summary>The two overloads agree about which triangle is nearest and which way it faced.</summary>
    /// <remarks>
    ///     They share a traversal and a tie-break, so a disagreement would mean one of them had
    ///     drifted — and the sign vote that the boolean one exists for is built entirely out of the
    ///     backface flag.
    /// </remarks>
    [Fact]
    public void TheTwoRaycastsAgreeAboutTheNearestHit() {
        Vector3[] vertices = [
            new(0, 0, 1), new(1, 0, 1), new(0, 1, 1),
            new(0, 0, 5), new(1, 0, 5), new(0, 1, 5)
        ];

        int[] indices = [0, 1, 2, 3, 5, 4];

        var tree = new TriangleTree(vertices, indices);
        var origin = new Vector3(0.25f, 0.25f, 0f);

        Assert.True(tree.Raycast(origin, new(0, 0, 1), out var backface));

        var hit = tree.Raycast(origin, new(0, 0, 10f));

        Assert.Equal(0, hit.Triangle);
        Assert.Equal(backface, hit.Backface);
        Assert.Equal(1f, hit.Distance * 10f, 4);
    }

    // --- Distance -----------------------------------------------------------

    [Fact]
    public void DistanceAgreesWithEveryTriangleTested() {
        var (vertices, indices) = Sphere(1f, 16, 24);
        var tree = new TriangleTree(vertices, indices);

        // A deterministic lattice of probes rather than random ones, so a failure is reproducible.
        for (var x = -3; x <= 3; x++) {
            for (var y = -3; y <= 3; y++) {
                for (var z = -3; z <= 3; z++) {
                    var point = new Vector3(x, y, z) * 0.7f;

                    Assert.Equal(Soup(vertices, indices, point).DistanceSquared, tree.DistanceSquared(point), 4);
                }
            }
        }
    }

    [Fact]
    public void ABuildIsIndependentOfTheOrderTrianglesArriveIn() {
        // Not that the tree is identical — it is not — but that the answers are. Ties in the split
        // break on triangle id, so a mesh whose triangles are all coplanar cannot depend on the
        // sort's stability.
        var (vertices, indices) = Box(new(0.5f));
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

    // --- The closest triangle, which is what attribute transfer asks for -----

    /// <summary>Over the face: the triangle is named, and the weights reproduce the point.</summary>
    /// <remarks>
    ///     Doc 41 § D12's whole requirement in one assertion. A normal, a texture coordinate or a
    ///     set of skin weights is interpolated by exactly these three weights against exactly this
    ///     triangle's three vertices, so a wrong index or a wrong weight is a wrong attribute rather
    ///     than a slightly wrong one.
    /// </remarks>
    [Fact]
    public void ClosestOverTheFaceNamesTheTriangleAndWhereOnIt() {
        var tree = new TriangleTree([A, B, C], [0, 1, 2]);
        var closest = tree.Closest(new(0.25f, 0.25f, 3f));

        Assert.Equal(0, closest.Triangle);
        Assert.Equal(9f, closest.DistanceSquared, 4);
        Assert.Equal(0.5f, closest.Barycentric.X, 4);
        Assert.Equal(0.25f, closest.Barycentric.Y, 4);
        Assert.Equal(0.25f, closest.Barycentric.Z, 4);

        AssertReconstructs(A, B, C, closest);
    }

    /// <summary>A point projecting onto an edge gets the edge's two weights and a hard zero.</summary>
    [Fact]
    public void ClosestOnAnEdgeWeighsTwoVerticesAndSumsToOne() {
        var tree = new TriangleTree([A, B, C], [0, 1, 2]);
        var closest = tree.Closest(new(2f, 2f, 1f));

        Assert.Equal(0, closest.Triangle);
        Assert.Equal(0f, closest.Barycentric.X);
        Assert.Equal(0.5f, closest.Barycentric.Y, 4);
        Assert.Equal(0.5f, closest.Barycentric.Z, 4);
        Assert.Equal(1f, Total(closest.Barycentric));

        AssertReconstructs(A, B, C, closest);
    }

    /// <summary>A point projecting onto a vertex gets that vertex and nothing else.</summary>
    [Fact]
    public void ClosestAtAVertexWeighsOneVertexAndSumsToOne() {
        var tree = new TriangleTree([A, B, C], [0, 1, 2]);
        var closest = tree.Closest(new(-2f, -2f, 0f));

        Assert.Equal(0, closest.Triangle);
        Assert.Equal(A, closest.Point);
        Assert.Equal(new(1f, 0f, 0f), closest.Barycentric);
        Assert.Equal(1f, Total(closest.Barycentric));
    }

    /// <summary>⚠ A zero-area triangle still produces weights, and they still sum to exactly one.</summary>
    /// <remarks>
    ///     The case that would poison a transfer silently. Solving for barycentrics divides by the
    ///     triangle's area, so on a pole fan — which every UV sphere has two of — the weights come
    ///     back <c>NaN</c>, and a <c>NaN</c> weight writes a <c>NaN</c> normal into the output mesh
    ///     rather than throwing anywhere near where the mistake was.
    /// </remarks>
    [Fact]
    public void ADegenerateTriangleStillWeighsToOne() {
        foreach (var (a, b, c) in ((Vector3 A, Vector3 B, Vector3 C)[]) [
            (A, A, B), (A, B, B), (A, B, A), (A, A, A), (A, B, new(2, 0, 0))
        ]) {
            var tree = new TriangleTree([a, b, c], [0, 1, 2]);

            foreach (var probe in (Vector3[]) [new(0, 0, 1), new(2, 2, 0), new(-1, -1, -1), new(0.5f, 1f, 0f)]) {
                var closest = tree.Closest(probe);
                var weights = closest.Barycentric;

                Assert.False(
                    float.IsNaN(weights.X) || float.IsNaN(weights.Y) || float.IsNaN(weights.Z),
                    $"({a}, {b}, {c}) from {probe} produced {weights}"
                );

                Assert.Equal(1f, Total(weights));
                AssertReconstructs(a, b, c, closest);
            }
        }
    }

    /// <summary>Against brute force over a soup, on where the point is as well as how far.</summary>
    [Fact]
    public void ClosestAgreesWithEveryTriangleTested() {
        var (vertices, indices) = Sphere(1f, 16, 24);
        var tree = new TriangleTree(vertices, indices);

        for (var x = -3; x <= 3; x++) {
            for (var y = -3; y <= 3; y++) {
                for (var z = -3; z <= 3; z++) {
                    var point = new Vector3(x, y, z) * 0.7f;
                    var brute = Soup(vertices, indices, point);
                    var closest = tree.Closest(point);

                    Assert.Equal(brute.DistanceSquared, closest.DistanceSquared, 4);

                    // Not the same triangle *index*: a sphere has ties along every shared edge, and
                    // brute force and the traversal reach them in different orders. What has to
                    // agree is the point, and the weights have to reproduce it on whichever
                    // triangle was named.
                    Assert.Equal(brute.Point.X, closest.Point.X, 4);
                    Assert.Equal(brute.Point.Y, closest.Point.Y, 4);
                    Assert.Equal(brute.Point.Z, closest.Point.Z, 4);

                    Assert.Equal(1f, Total(closest.Barycentric), 5);

                    AssertReconstructs(
                        vertices[indices[closest.Triangle * 3]],
                        vertices[indices[(closest.Triangle * 3) + 1]],
                        vertices[indices[(closest.Triangle * 3) + 2]],
                        closest
                    );
                }
            }
        }
    }

    /// <summary>The two entry points are one traversal, and stay one.</summary>
    /// <remarks>
    ///     <see cref="TriangleTree.DistanceSquared" /> is currently
    ///     <see cref="TriangleTree.Closest" /> with the rest of the answer discarded, which makes
    ///     this trivially true today and is exactly why it is worth writing down: the moment
    ///     somebody re-splits them into two walks for speed, this is the test that notices when the
    ///     two walks stop agreeing.
    /// </remarks>
    [Fact]
    public void ClosestAndDistanceSquaredAnswerTheSameQuestion() {
        var (vertices, indices) = Sphere(1f, 12, 16);
        var tree = new TriangleTree(vertices, indices);

        Gen.Select(Gen.Float[-3f, 3f], Gen.Float[-3f, 3f], Gen.Float[-3f, 3f])
            .Sample(
                values => {
                    var (x, y, z) = values;
                    var point = new Vector3(x, y, z);

                    Assert.Equal(tree.DistanceSquared(point), tree.Closest(point).DistanceSquared);
                }
            );
    }

    /// <summary>An empty tree has no nearest triangle, and says so rather than naming one.</summary>
    [Fact]
    public void AnEmptyTreeNamesNoTriangle() {
        var tree = new TriangleTree([], []);
        var closest = tree.Closest(new(1f, 2f, 3f));

        Assert.Equal(-1, closest.Triangle);
        Assert.Equal(float.PositiveInfinity, closest.DistanceSquared);
        Assert.Equal(float.PositiveInfinity, tree.DistanceSquared(new(1f, 2f, 3f)));
    }

    // --- The soup, and the brute force it is checked against -----------------

    /// <summary>The three weights added up, in the order a caller would add them.</summary>
    static float Total(Vector3 barycentric) => barycentric.X + barycentric.Y + barycentric.Z;

    /// <summary>The weights, applied to the triangle they belong to, are the point they came from.</summary>
    static void AssertReconstructs(Vector3 a, Vector3 b, Vector3 c, ClosestTriangle closest) {
        var rebuilt = (a * closest.Barycentric.X) + (b * closest.Barycentric.Y) + (c * closest.Barycentric.Z);

        Assert.Equal(closest.Point.X, rebuilt.X, 4);
        Assert.Equal(closest.Point.Y, rebuilt.Y, 4);
        Assert.Equal(closest.Point.Z, rebuilt.Z, 4);
    }

    /// <summary>Every triangle tested, which is the answer the tree is an optimisation of.</summary>
    static ClosestTriangle Soup(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices, Vector3 point) {
        var best = new ClosestTriangle(-1, point, float.PositiveInfinity, Vector3.Zero);

        for (var triangle = 0; triangle < indices.Length / 3; triangle++) {
            var closest = TriangleTree.ClosestPointOnTriangle(
                point,
                vertices[indices[triangle * 3]],
                vertices[indices[(triangle * 3) + 1]],
                vertices[indices[(triangle * 3) + 2]],
                out var barycentric
            );

            var distance = Vector3.DistanceSquared(point, closest);

            if (distance < best.DistanceSquared) {
                best = new(triangle, closest, distance, barycentric);
            }
        }

        return best;
    }

    /// <summary>An axis-aligned box centred on the origin, wound so every face looks outward.</summary>
    static (Vector3[] Vertices, int[] Indices) Box(Vector3 half) {
        Vector3[] vertices = [
            new(-half.X, -half.Y, -half.Z),
            new(half.X, -half.Y, -half.Z),
            new(half.X, half.Y, -half.Z),
            new(-half.X, half.Y, -half.Z),
            new(-half.X, -half.Y, half.Z),
            new(half.X, -half.Y, half.Z),
            new(half.X, half.Y, half.Z),
            new(-half.X, half.Y, half.Z)
        ];

        int[] indices = [
            4, 5, 6, 4, 6, 7, // +Z
            0, 3, 2, 0, 2, 1, // −Z
            1, 2, 6, 1, 6, 5, // +X
            0, 4, 7, 0, 7, 3, // −X
            3, 7, 6, 3, 6, 2, // +Y
            0, 1, 5, 0, 5, 4 // −Y
        ];

        return (vertices, indices);
    }

    /// <summary>A UV sphere centred on the origin, with the collapsed pole fans left in.</summary>
    static (Vector3[] Vertices, int[] Indices) Sphere(float radius, int rings, int segments) {
        var vertices = new Vector3[(rings + 1) * (segments + 1)];

        for (var ring = 0; ring <= rings; ring++) {
            var theta = MathF.PI * ring / rings;
            var sinTheta = MathF.Sin(theta);
            var cosTheta = MathF.Cos(theta);

            for (var segment = 0; segment <= segments; segment++) {
                var phi = 2f * MathF.PI * segment / segments;

                vertices[(ring * (segments + 1)) + segment] = new Vector3(
                    sinTheta * MathF.Cos(phi),
                    cosTheta,
                    sinTheta * MathF.Sin(phi)
                ) * radius;
            }
        }

        var indices = new List<int>(rings * segments * 6);

        for (var ring = 0; ring < rings; ring++) {
            for (var segment = 0; segment < segments; segment++) {
                var here = (ring * (segments + 1)) + segment;
                var below = here + segments + 1;

                indices.Add(here);
                indices.Add(here + 1);
                indices.Add(below);

                indices.Add(here + 1);
                indices.Add(below + 1);
                indices.Add(below);
            }
        }

        return (vertices, [.. indices]);
    }
}
