// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Rendering.VirtualGeometry;

/// <summary>What a built DAG has to be true of, checked against meshes with known answers.</summary>
public class MeshletBuilderTests {
    [Fact]
    public void EmptyMeshBuildsAnEmptyDag() {
        var built = MeshletBuilder.Build(new());

        Assert.Empty(built.Meshlets);
        Assert.Empty(built.Groups);
        Assert.Empty(built.Fallback);
    }

    [Fact]
    public void LevelZeroIsExactlyTheMeshItWasGiven() {
        var mesh = Shapes.Sphere(4);
        var built = MeshletBuilder.Build(mesh);

        var covered = built.Meshlets.Where(meshlet => meshlet.Level == 0).Sum(meshlet => meshlet.TriangleCount);

        Assert.Equal(mesh.TriangleCount, covered);
        Assert.Empty(MeshletValidator.Validate(built, mesh));
    }

    [Fact]
    public void ClustersStayInsideTheirBudgets() {
        var settings = new MeshletBuildSettings { MaxTriangles = 64, MaxVertices = 48 };
        var built = MeshletBuilder.Build(Shapes.Sphere(4), settings);

        Assert.All(
            built.Meshlets,
            meshlet => {
                Assert.InRange(meshlet.TriangleCount, 1, settings.MaxTriangles);
                Assert.InRange(meshlet.VertexCount, 1, settings.MaxVertices);
            }
        );
    }

    [Fact]
    public void ClustersAreAboutTheSizeTheyWereAskedFor() {
        var built = MeshletBuilder.Build(Shapes.Sphere(4));
        var leaves = built.Meshlets.Where(meshlet => meshlet.Level == 0).ToList();

        // A partition that answered with one triangle per cluster would satisfy every other test
        // here. What makes a cluster worth having is that it is nearly full.
        var average = leaves.Average(meshlet => meshlet.TriangleCount);

        Assert.True(average > 96, $"Level-zero clusters average {average:F1} of 128 triangles, over {leaves.Count} clusters.");
    }

    [Fact]
    public void TheHierarchyConvergesToAHandfulOfRoots() {
        var mesh = Shapes.Sphere(4);
        var built = MeshletBuilder.Build(mesh);

        Assert.True(built.LevelCount >= 4, $"A {mesh.TriangleCount}-triangle sphere produced {built.LevelCount} levels.");
        Assert.True(built.Roots.Length <= 4, $"It converged to {built.Roots.Length} roots.");
    }

    [Fact]
    public void EveryLevelIsCoarserThanTheOneBelow() {
        var built = MeshletBuilder.Build(Shapes.Sphere(4));

        var triangles = Enumerable.Range(0, built.LevelCount)
            .Select(level => built.Meshlets.Where(meshlet => meshlet.Level == level).Sum(meshlet => meshlet.TriangleCount))
            .ToList();

        for (var level = 1; level < triangles.Count; level++) {
            Assert.True(
                triangles[level] < triangles[level - 1],
                $"Level {level} has {triangles[level]} triangles against level {level - 1}'s {triangles[level - 1]}."
            );
        }
    }

    [Fact]
    public void AnOpenMeshKeepsItsOutsideEdge() {
        var mesh = Shapes.Grid(24);
        var built = MeshletBuilder.Build(mesh);

        // The grid's outer edge is a boundary of the mesh itself, which is every group's boundary
        // wherever it reaches one. A simplification that ate it would round off the corners of every
        // terrain tile in a level, and the seam between two tiles is exactly where nobody may.
        var welded = Topology.Weld(mesh.Positions);
        var source = Geometry.Boundary(mesh.Indices, welded);

        foreach (var root in built.Roots) {
            var corners = MeshletCut.Flatten(built, [root]);

            foreach (var edge in Geometry.Boundary(corners, welded)) {
                Assert.Contains(edge, source);
            }
        }
    }

    [Fact]
    public void SkinnedClustersCarryTheBonesTheyTouch() {
        var grid = Shapes.Grid(16);
        var bones = new int[grid.VertexCount * 4];
        var weights = new float[grid.VertexCount * 4];

        for (var vertex = 0; vertex < grid.VertexCount; vertex++) {
            // A bone per row, so a cluster's range says something about where the cluster is.
            bones[vertex * 4] = (int)(grid.Positions[vertex].Z * 16);
            weights[vertex * 4] = 1f;
        }

        var built = MeshletBuilder.Build(grid with { BoneIndices = bones, BoneWeights = weights });

        Assert.All(
            built.Meshlets,
            meshlet => {
                Assert.InRange(meshlet.FirstBone, 0, 16);
                Assert.True(meshlet.BoneCount > 0);

                for (var entry = 0; entry < meshlet.VertexCount; entry++) {
                    var bone = bones[built.Vertices[meshlet.VertexOffset + entry] * 4];
                    Assert.InRange(bone, meshlet.FirstBone, meshlet.FirstBone + meshlet.BoneCount - 1);
                }
            }
        );
    }

    [Fact]
    public void AnUnskinnedClusterSaysSoRatherThanClaimingBoneZero() {
        var built = MeshletBuilder.Build(Shapes.Grid(8));

        Assert.All(
            built.Meshlets,
            meshlet => {
                Assert.Equal(-1, meshlet.FirstBone);
                Assert.Equal(0, meshlet.BoneCount);
            }
        );
    }

    [Fact]
    public void NormalConesFaceWhereTheClusterFaces() {
        var mesh = Shapes.Sphere(3);
        var built = MeshletBuilder.Build(mesh);

        foreach (var meshlet in built.Meshlets) {
            if (meshlet.ConeCosine < 0) {
                continue;
            }

            var corners = new int[meshlet.TriangleCount * 3];
            built.GetTriangles(meshlet, corners);

            for (var triangle = 0; triangle < meshlet.TriangleCount; triangle++) {
                var a = mesh.Positions[corners[triangle * 3]];
                var b = mesh.Positions[corners[(triangle * 3) + 1]];
                var c = mesh.Positions[corners[(triangle * 3) + 2]];
                var normal = Vector3.Cross(b - a, c - a);

                if (normal.LengthSquared() <= 0) {
                    continue;
                }

                // Every triangle inside the cone the cluster claims, to a rounding tolerance. A cone
                // that did not contain one would cull a triangle that faces the camera.
                Assert.True(Vector3.Dot(meshlet.ConeAxis, Vector3.Normalize(normal)) >= meshlet.ConeCosine - 1e-4f);
            }
        }
    }

    [Fact]
    public void TwoBuildsOfOneMeshAreTheSameDag() {
        var mesh = Shapes.Sphere(4);

        var first = MeshletBuilder.Build(mesh);
        var second = MeshletBuilder.Build(mesh);

        AssertSameDag(first, second);
    }

    [Fact]
    public void SimplifyingInParallelChangesNothingButTheOrderOfTheWork() {
        var mesh = Shapes.Sphere(4);

        var sequential = MeshletBuilder.Build(mesh, new() { Parallel = false });
        var parallel = MeshletBuilder.Build(mesh, new() { Parallel = true });

        AssertSameDag(sequential, parallel);
    }

    /// <summary>Asserts that two DAGs are identical in every part a consumer reads.</summary>
    /// <param name="expected">One DAG.</param>
    /// <param name="actual">The other.</param>
    static void AssertSameDag(MeshletMesh expected, MeshletMesh actual) {
        Assert.Equal(expected.Meshlets, actual.Meshlets);
        Assert.Equal(expected.Vertices, actual.Vertices);
        Assert.Equal(expected.Triangles, actual.Triangles);
        Assert.Equal(expected.Roots, actual.Roots);
        Assert.Equal(expected.Fallback, actual.Fallback);
        Assert.Equal(expected.Groups.Length, actual.Groups.Length);

        for (var index = 0; index < expected.Groups.Length; index++) {
            Assert.Equal(expected.Groups[index].Children, actual.Groups[index].Children);
            Assert.Equal(expected.Groups[index].Parents, actual.Groups[index].Parents);
            Assert.Equal(expected.Groups[index].Error, actual.Groups[index].Error);
        }
    }
}
