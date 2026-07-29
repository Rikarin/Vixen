// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Rendering.VirtualGeometry;

/// <summary>That the validation is a check and not a formality.</summary>
/// <remarks>
///     Every test here breaks something specific and asserts that the validator says so. A validation
///     pass that never fails is a validation pass nobody has tested, and this one exists to make the
///     failure mode it guards against — a crack at one distance on one mesh — into a build error.
/// </remarks>
public class MeshletValidatorTests {
    [Fact]
    public void AWellBuiltDagHasNothingWrongWithIt() {
        var mesh = Shapes.Sphere(3);

        Assert.Empty(MeshletValidator.Validate(MeshletBuilder.Build(mesh), mesh));
    }

    [Fact]
    public void AnOpenMeshBuildsAValidDagToo() {
        var mesh = Shapes.Grid(24);

        Assert.Empty(MeshletValidator.Validate(MeshletBuilder.Build(mesh), mesh));
    }

    [Fact]
    public void SimplifyingWithoutLockingTheGroupBoundaryIsRefused() {
        // Sphere(4) and not a smaller one: a mesh with few enough clusters to fit in one group has
        // no group boundary to move, so the sabotage would have nothing to break and the test would
        // pass for the wrong reason.
        var mesh = Shapes.Sphere(4);
        var sabotaged = MeshletBuilder.Build(mesh, new() { UnlockGroupBoundaries = true });
        var problems = MeshletValidator.Validate(sabotaged, mesh);

        // The whole scheme in one assertion. With the lock removed the simplification is free to
        // move the edges a group shares with its neighbours, which is invisible in every picture of
        // a single level of detail and is a slit in the surface the moment a cut passes through it.
        Assert.NotEmpty(problems);
        Assert.Contains(problems, problem => problem.Contains("boundary moved", StringComparison.Ordinal));
    }

    [Fact]
    public void AnErrorThatDoesNotIncreaseIsRefused() {
        var mesh = Shapes.Sphere(3);
        var built = MeshletBuilder.Build(mesh);
        var group = built.Groups[0];

        var sabotaged = built with {
            Groups = [.. built.Groups.Select(
                (candidate, index) => index == 0
                    ? candidate with { Error = built.Meshlets[candidate.Children[0]].Error }
                    : candidate
            )]
        };

        Assert.NotEmpty(MeshletValidator.Validate(sabotaged, mesh));
        Assert.NotEqual(group.Error, sabotaged.Groups[0].Error);
    }

    [Fact]
    public void AClusterWhoseParentErrorDisagreesWithItsGroupIsRefused() {
        var mesh = Shapes.Sphere(3);
        var built = MeshletBuilder.Build(mesh);
        var child = built.Groups[0].Children[0];

        var sabotaged = built with {
            Meshlets = [.. built.Meshlets.Select(
                (meshlet, index) => index == child ? meshlet with { ParentError = meshlet.ParentError * 2f } : meshlet
            )]
        };

        Assert.Contains(
            MeshletValidator.Validate(sabotaged, mesh),
            problem => problem.Contains("parent error", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void ALostTriangleIsRefused() {
        var mesh = Shapes.Sphere(3);
        var built = MeshletBuilder.Build(mesh);
        var leaf = Array.FindIndex(built.Meshlets, meshlet => meshlet.Level == 0);

        var sabotaged = built with {
            Meshlets = [.. built.Meshlets.Select(
                (meshlet, index) => index == leaf ? meshlet with { TriangleCount = meshlet.TriangleCount - 1 } : meshlet
            )]
        };

        // A dropped triangle at level zero is a hole in the mesh seen from close up, and it is the
        // failure a partition is most likely to have: every other test here would pass a DAG that
        // simply forgot one.
        Assert.Contains(
            MeshletValidator.Validate(sabotaged, mesh),
            problem => problem.Contains("missing", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void AClusterPointingPastTheSharedArraysIsRefused() {
        var mesh = Shapes.Sphere(2);
        var built = MeshletBuilder.Build(mesh);

        var sabotaged = built with {
            Meshlets = [.. built.Meshlets.Select(
                (meshlet, index) => index == 0 ? meshlet with { VertexOffset = built.Vertices.Length } : meshlet
            )]
        };

        Assert.NotEmpty(MeshletValidator.Validate(sabotaged, mesh));
    }

    [Fact]
    public void ADagOverAMeshWithNoClustersIsRefused() {
        var mesh = Shapes.Sphere(2);

        Assert.NotEmpty(MeshletValidator.Validate(new(), mesh));
    }

    [Fact]
    public void LockingEachClusterInsteadOfTheGroupNeverReachesTheReductionItAsksFor() {
        var mesh = Shapes.Sphere(4);

        var proper = MeshletBuilder.Build(mesh);
        var sabotaged = MeshletBuilder.Build(mesh, new() { LockClusterBoundaries = true });

        // The other reading of "do not move the shared edges", and the one that looks right. Locking
        // more than necessary is never a crack, so this is a quality failure rather than a validity
        // one — and it is stated here as what it is rather than as what it would be convenient for
        // it to be. What it costs is the halving: with the group's boundary locked every level meets
        // the ratio it was given exactly, and with each cluster's locked no level ever does, because
        // every edge interior to a group is some cluster's boundary and only the interiors of
        // clusters are left to collapse.
        Assert.Equal(mesh.TriangleCount / 2, TrianglesAt(proper, 1));

        Assert.True(
            TrianglesAt(sabotaged, 1) > TrianglesAt(proper, 1) * 1.1,
            $"Per-cluster locking left {TrianglesAt(sabotaged, 1)} triangles at level one "
            + $"against the group lock's {TrianglesAt(proper, 1)}."
        );

        Assert.Empty(MeshletValidator.Validate(sabotaged, mesh));
    }

    /// <summary>How many triangles one level of a DAG holds.</summary>
    /// <param name="mesh">The DAG.</param>
    /// <param name="level">The level.</param>
    /// <returns>The count.</returns>
    static int TrianglesAt(MeshletMesh mesh, int level) =>
        mesh.Meshlets.Where(meshlet => meshlet.Level == level).Sum(meshlet => meshlet.TriangleCount);
}
