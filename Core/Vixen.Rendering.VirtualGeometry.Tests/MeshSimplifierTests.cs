// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Rendering.VirtualGeometry;

/// <summary>That a simplification removes what it may and nothing it may not.</summary>
public class MeshSimplifierTests {
    [Fact]
    public void ItReachesTheTriangleCountItIsGiven() {
        var mesh = Shapes.Sphere(3);
        var result = Simplify(mesh, [], mesh.TriangleCount / 4);

        Assert.InRange(result.Corners.Length / 3, 1, mesh.TriangleCount / 4);
    }

    [Fact]
    public void ALockedEdgeSurvivesEveryCollapse() {
        var mesh = Shapes.Grid(12);
        var welded = Topology.Weld(mesh.Positions);
        var all = Enumerable.Range(0, mesh.TriangleCount).ToArray();
        var locked = Topology.BoundaryEdges(mesh.Indices, welded, all);

        var result = Simplify(mesh, locked, mesh.TriangleCount / 8);
        var after = Topology.BoundaryEdges(result.Corners, welded, Enumerable.Range(0, result.Corners.Length / 3).ToArray());

        Assert.Equal(locked, after);
    }

    [Fact]
    public void WithNothingLockedTheOutlineIsEatenAway() {
        var mesh = Shapes.Grid(12);
        var welded = Topology.Weld(mesh.Positions);
        var before = Topology.BoundaryEdges(mesh.Indices, welded, Enumerable.Range(0, mesh.TriangleCount).ToArray());

        var result = Simplify(mesh, [], mesh.TriangleCount / 8);
        var after = Topology.BoundaryEdges(result.Corners, welded, Enumerable.Range(0, result.Corners.Length / 3).ToArray());

        // The other half of the test above: it passes because the lock does something, not because
        // a grid's outline happens to survive a simplification.
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void NoTriangleTurnsInsideOut() {
        var mesh = Shapes.Sphere(3);
        var result = Simplify(mesh, [], mesh.TriangleCount / 8);

        // Every triangle of a sphere faces away from its centre. A collapse that pulled a vertex
        // through the surface would leave one facing inwards, which the quadric alone is happy with
        // — it measures distance to planes and does not know which side of one it is on.
        for (var triangle = 0; triangle < result.Corners.Length / 3; triangle++) {
            var a = mesh.Positions[result.Corners[triangle * 3]];
            var b = mesh.Positions[result.Corners[(triangle * 3) + 1]];
            var c = mesh.Positions[result.Corners[(triangle * 3) + 2]];

            Assert.True(Vector3.Dot(Vector3.Cross(b - a, c - a), (a + b + c) / 3f) > 0);
        }
    }

    [Fact]
    public void ItInventsNoVertices() {
        var mesh = Shapes.Sphere(3);
        var result = Simplify(mesh, [], mesh.TriangleCount / 4);

        Assert.All(result.Corners, corner => Assert.InRange(corner, 0, mesh.VertexCount - 1));
    }

    [Fact]
    public void TheErrorItReportsBoundsWhatItDid() {
        var mesh = Shapes.Sphere(3);
        var result = Simplify(mesh, [], mesh.TriangleCount / 4);

        Assert.True(result.Error > 0, "A simplification that changed the surface reported no error.");

        // Every vertex it removed is within the reported error of the surface that replaced it,
        // which is the promise the level-of-detail threshold is chosen against.
        var deviation = Geometry.Deviation(mesh.Positions, result.Corners, mesh.Positions);

        Assert.True(
            deviation <= result.Error + 1e-5f,
            $"The surface moved by {deviation} and the simplification reported {result.Error}."
        );
    }

    [Fact]
    public void ASeamIsNeverCollapsedInto() {
        var positions = new List<Vector3>();
        var texcoords = new List<Vector2>();
        var indices = new List<int>();

        // A strip whose middle row is split into two copies with different texture coordinates —
        // which is what an exporter writes wherever a UV chart ends.
        for (var x = 0; x <= 8; x++) {
            positions.Add(new(x / 8f, 0, 0));
            texcoords.Add(new(x / 8f, 0));
            positions.Add(new(x / 8f, 0, 0.5f));
            texcoords.Add(new(x / 8f, 1));
            positions.Add(new(x / 8f, 0, 0.5f));
            texcoords.Add(new(x / 8f, 0));
            positions.Add(new(x / 8f, 0, 1f));
            texcoords.Add(new(x / 8f, 1));
        }

        for (var x = 0; x < 8; x++) {
            indices.AddRange([(x * 4) + 0, (x * 4) + 1, (x * 4) + 4, (x * 4) + 4, (x * 4) + 1, (x * 4) + 5]);
            indices.AddRange([(x * 4) + 2, (x * 4) + 3, (x * 4) + 6, (x * 4) + 6, (x * 4) + 3, (x * 4) + 7]);
        }

        var mesh = new MeshletBuildInput {
            Positions = [.. positions],
            TexCoords = [.. texcoords],
            Indices = [.. indices]
        };

        var result = Simplify(mesh, [], 4);

        Assert.True(result.Corners.Length < mesh.Indices.Length, "Nothing was simplified, so nothing was tested.");

        // The two charts stay two charts. A collapse onto a seam vertex would have to pick one of
        // its two copies, and every triangle arriving from the other side would take that copy's
        // texture coordinate — which is one chart's texture smeared across the other. Here that shows
        // up as a triangle with a corner in each.
        for (var triangle = 0; triangle < result.Corners.Length / 3; triangle++) {
            var charts = Enumerable.Range(0, 3)
                .Select(corner => result.Corners[(triangle * 3) + corner] % 4 < 2)
                .Distinct()
                .Count();

            Assert.Equal(1, charts);
        }
    }

    /// <summary>Runs a simplification over a whole mesh.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="locked">Which edges may not move.</param>
    /// <param name="target">How many triangles to aim for.</param>
    /// <returns>The result.</returns>
    static SimplifyResult Simplify(MeshletBuildInput mesh, HashSet<long> locked, int target) {
        var welded = Topology.Weld(mesh.Positions);

        return MeshSimplifier.Simplify(
            mesh.Positions,
            welded,
            Topology.FindSeams(mesh, welded),
            new float[mesh.VertexCount],
            mesh.Indices,
            locked,
            target
        );
    }
}
