// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>The internal manifold triangle view — docs/plan/41 § B5.</summary>
/// <remarks>
///     ⚠ <b>The ordered ring is the reason this type exists, so it is what most of these assert.</b>
///     <see cref="EditMesh.EdgesAt" /> already answers "which edges meet here", in build order.
///     Nothing in <c>Vixen.Geometry</c> answers "which neighbours, going round" — and a cross field,
///     an angle-weighted smoothing step and a curvature estimate all read the second question.
/// </remarks>
public class ManifoldMeshTests {
    [Fact]
    public void TheRingOfAnInteriorVertexIsInFanOrderAndCloses() {
        var view = View(MeshShapes.Create(ShapeKind.Sphere));

        var checkedAny = false;

        for (var vertex = 0; vertex < view.VertexCount; vertex++) {
            if (view.IsBoundary(vertex)) {
                continue;
            }

            var ring = view.Ring(vertex);

            Assert.True(ring.Length >= 3, $"Vertex {vertex} has a ring of {ring.Length}.");

            for (var step = 0; step < ring.Length; step++) {
                var a = ring[step];
                var b = ring[(step + 1) % ring.Length];

                Assert.True(
                    HasTriangle(view, vertex, a, b),
                    $"Ring of {vertex} steps from {a} to {b}, which share no triangle with it — "
                    + "that is build order, not fan order."
                );
            }

            checkedAny = true;
        }

        Assert.True(checkedAny);
    }

    /// <summary>⚠ And the same claim held against the structure it is deliberately not.</summary>
    /// <remarks>
    ///     A sphere's pole is where this shows up first: the pole's edges are discovered by
    ///     <c>EditMesh</c> in whatever order its faces were emitted, which for a generated primitive
    ///     is one ring of the fan at a time and not round the fan. If this test ever passes trivially
    ///     — if the two orders agree everywhere — then the fixture stopped being a fixture.
    /// </remarks>
    [Fact]
    public void TheBuildOrderRingAndTheFanOrderRingAreNotTheSame() {
        var mesh = MeshShapes.Create(ShapeKind.Sphere);
        var view = View(mesh);

        var differs = false;

        for (var vertex = 0; vertex < view.VertexCount && !differs; vertex++) {
            var ring = view.Ring(vertex);
            var edges = mesh.EdgesAt(vertex);

            if (ring.Length != edges.Length) {
                continue;
            }

            for (var step = 0; step < ring.Length; step++) {
                if (mesh.Edges[edges[step]].Other(vertex) != ring[step]) {
                    differs = true;
                    break;
                }
            }
        }

        Assert.True(
            differs,
            "EditMesh.EdgesAt and ManifoldMesh.Ring agree everywhere on a sphere, which would mean "
            + "the fan-ordering code is untested by this fixture."
        );
    }

    [Fact]
    public void TheRingOfABoundaryVertexIsOpenAndStartsAtTheRim() {
        var view = View(BrokenMeshes.OpenSurface());

        var checkedAny = false;

        for (var vertex = 0; vertex < view.VertexCount; vertex++) {
            if (!view.IsBoundary(vertex)) {
                continue;
            }

            var ring = view.Ring(vertex);

            Assert.True(ring.Length >= 2, $"Boundary vertex {vertex} has a ring of {ring.Length}.");

            // Consecutive pairs share a triangle — but the ring does not wrap, so the pair that
            // closes it does not have to.
            for (var step = 0; step + 1 < ring.Length; step++) {
                Assert.True(
                    HasTriangle(view, vertex, ring[step], ring[step + 1]),
                    $"Boundary ring of {vertex} is out of order at step {step}."
                );
            }

            // An open fan of k triangles has k + 1 neighbours; a closed one has k. That is the
            // difference between the ring having been walked from its rim and from somewhere in the
            // middle, and it is one number rather than a wrap-around test that a single-triangle fan
            // gets wrong in both directions.
            Assert.True(
                ring.Length == view.Outgoing(vertex).Length + 1,
                $"Boundary vertex {vertex} has {view.Outgoing(vertex).Length} triangles and a ring of "
                + $"{ring.Length}, so the walk did not start at the rim."
            );

            Assert.Equal(1, Sharing(view, vertex, ring[0]));
            Assert.Equal(1, Sharing(view, vertex, ring[^1]));

            checkedAny = true;
        }

        Assert.True(checkedAny);
    }

    [Fact]
    public void EveryFrameIsOrthonormal() {
        var view = View(MeshShapes.Create(ShapeKind.Torus));

        for (var vertex = 0; vertex < view.VertexCount; vertex++) {
            var frame = view.Frame(vertex);

            Assert.Equal(1f, frame.Normal.Length(), 3);
            Assert.Equal(1f, frame.Tangent.Length(), 3);
            Assert.Equal(1f, frame.Bitangent.Length(), 3);

            Assert.Equal(0f, Vector3.Dot(frame.Normal, frame.Tangent), 3);
            Assert.Equal(0f, Vector3.Dot(frame.Normal, frame.Bitangent), 3);
            Assert.Equal(0f, Vector3.Dot(frame.Tangent, frame.Bitangent), 3);
        }
    }

    /// <summary>⚠ docs/plan/41 § D14: the same input gives the same frame, every time.</summary>
    [Fact]
    public void TheFramesAreTheSameOnEveryBuild() {
        var mesh = MeshShapes.Create(ShapeKind.Capsule);

        var first = View(mesh);
        var second = View(mesh);

        for (var vertex = 0; vertex < first.VertexCount; vertex++) {
            Assert.Equal(first.Frame(vertex), second.Frame(vertex));
        }
    }

    [Fact]
    public void AdjacencyAgreesInBothDirections() {
        var view = View(MeshShapes.Create(ShapeKind.Box));

        for (var triangle = 0; triangle < view.TriangleCount; triangle++) {
            for (var side = 0; side < 3; side++) {
                var neighbour = view.Adjacent(triangle, side);

                if (neighbour < 0) {
                    continue;
                }

                var back = false;

                for (var other = 0; other < 3; other++) {
                    back |= view.Adjacent(neighbour, other) == triangle;
                }

                Assert.True(back, $"Triangle {triangle} names {neighbour} but is not named back.");
            }
        }
    }

    [Fact]
    public void ABoxIsClosedAndHasNoDefects() {
        var view = View(MeshShapes.Create(ShapeKind.Box));

        Assert.Equal(0, view.Defects);

        for (var vertex = 0; vertex < view.VertexCount; vertex++) {
            Assert.False(view.IsBoundary(vertex));
        }
    }

    /// <summary>⚠ The way back out must not weld, or it undoes the repair that made the view.</summary>
    [Fact]
    public void ToEditMeshKeepsCoincidentPositionsApart() {
        MeshConditioner.Condition(BrokenMeshes.TJunction(), new() { PreRemeshIterations = 0 }, out var report);

        Assert.True(report.Mesh.IsManifold, report.Mesh.Describe());
        Assert.True(report.Cut > 0);
    }

    [Fact]
    public void AnEmptyMeshBuildsIntoAnEmptyView() {
        var view = View(new EditMesh());

        Assert.Equal(0, view.VertexCount);
        Assert.Equal(0, view.TriangleCount);
        Assert.Equal(0, view.Defects);
        Assert.Equal(0f, view.Area());
        Assert.Equal(0f, view.Diagonal);
    }

    [Fact]
    public void AVertexWithNoTrianglesHasAnEmptyRingAndAUsableFrame() {
        var view = View(BrokenMeshes.SingleVertex());

        Assert.Equal(1, view.VertexCount);
        Assert.Empty(view.Ring(0).ToArray());
        Assert.Equal(0, view.Valence(0));

        var frame = view.Frame(0);

        // No triangles means no normal, so the frame is not orthonormal — but it is finite, which is
        // the whole of the contract for a degenerate vertex. A NaN here propagates into the field
        // solve and shows up as a hole three stages later.
        Assert.False(float.IsNaN(frame.Tangent.X + frame.Tangent.Y + frame.Tangent.Z));
        Assert.False(float.IsNaN(frame.Normal.X + frame.Normal.Y + frame.Normal.Z));
    }

    [Fact]
    public void ASingleTriangleIsThreeBoundaryVerticesAndNoAdjacency() {
        var view = View(BrokenMeshes.SingleTriangle());

        Assert.Equal(1, view.TriangleCount);

        for (var vertex = 0; vertex < 3; vertex++) {
            Assert.True(view.IsBoundary(vertex));
            Assert.Equal(2, view.Valence(vertex));
        }

        for (var side = 0; side < 3; side++) {
            Assert.Equal(-1, view.Adjacent(0, side));
        }
    }

    static ManifoldMesh View(EditMesh mesh) => ManifoldMesh.Build(TriangleSoup.From(mesh));

    /// <summary>How many triangles run along the edge between two vertices.</summary>
    static int Sharing(ManifoldMesh view, int a, int b) {
        var count = 0;

        foreach (var half in view.Outgoing(a)) {
            var corners = view.Corners(half / 3);

            if (corners[0] == b || corners[1] == b || corners[2] == b) {
                count++;
            }
        }

        return count;
    }

    static bool HasTriangle(ManifoldMesh view, int a, int b, int c) {
        foreach (var half in view.Outgoing(a)) {
            var corners = view.Corners(half / 3);
            var hasB = corners[0] == b || corners[1] == b || corners[2] == b;
            var hasC = corners[0] == c || corners[1] == c || corners[2] == c;

            if (hasB && hasC) {
                return true;
            }
        }

        return false;
    }
}
