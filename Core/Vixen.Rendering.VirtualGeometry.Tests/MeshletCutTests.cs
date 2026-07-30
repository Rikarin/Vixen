// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Rendering.VirtualGeometry;

/// <summary>That a cut through the DAG is a surface, and one as close to the mesh as it claims.</summary>
/// <remarks>
///     These are the phase-1 exit criteria of <c>docs/plan/22-virtualized-geometry.md</c>, and the reason the
///     CPU reference exists at all. A cut is what the device will choose per cluster per frame; if the
///     rule that chooses it can produce a crack, it produces one at one distance on one mesh, and
///     nothing about the asset or the build says so.
/// </remarks>
public class MeshletCutTests {
    /// <summary>How many distances the sphere is measured at.</summary>
    const int Distances = 20;

    /// <summary>The camera the projections are made through.</summary>
    const float FieldOfView = MathF.PI / 3f;

    /// <summary>How tall the view is, in pixels.</summary>
    const float ScreenHeight = 1080f;

    [Fact]
    public void EveryCutOfAClosedMeshIsItselfClosed() {
        var mesh = Shapes.Sphere(3);
        var built = MeshletBuilder.Build(mesh);
        var welded = Topology.Weld(mesh.Positions);

        // The strongest statement of crack-freedom available: a sphere has no boundary, so any cut
        // that leaves a gap between two levels of detail leaves an edge with one triangle on it. A
        // cut that took a parent on one side of a group and a child on the other would fail here and
        // would look, in a picture, like nothing at all until the light moved.
        foreach (var threshold in Thresholds(built)) {
            var cut = MeshletCut.SelectByError(built, threshold);
            var open = Geometry.Boundary(MeshletCut.Flatten(built, cut), welded);

            Assert.True(open.Count == 0, $"The cut at an error of {threshold} left {open.Count} open edges.");
        }
    }

    [Fact]
    public void EveryCutOfAnOpenMeshHasTheSameOutline() {
        var mesh = Shapes.Grid(24);
        var built = MeshletBuilder.Build(mesh);
        var welded = Topology.Weld(mesh.Positions);
        var outline = Geometry.Boundary(mesh.Indices, welded);

        foreach (var threshold in Thresholds(built)) {
            var cut = MeshletCut.SelectByError(built, threshold);

            Assert.Equal(outline, Geometry.Boundary(MeshletCut.Flatten(built, cut), welded));
        }
    }

    [Fact]
    public void ACutAtNoErrorIsTheMeshItself() {
        var mesh = Shapes.Sphere(3);
        var built = MeshletBuilder.Build(mesh);
        var cut = MeshletCut.SelectByError(built, 0f);

        Assert.Equal(mesh.TriangleCount, MeshletCut.Flatten(built, cut).Length / 3);
        Assert.All(cut, index => Assert.Equal(0, built.Meshlets[index].Level));
    }

    [Fact]
    public void ACutAboveEveryErrorIsTheRoots() {
        var built = MeshletBuilder.Build(Shapes.Sphere(3));

        Assert.Equal(built.Roots, MeshletCut.SelectByError(built, float.MaxValue));
    }

    [Fact]
    public void EveryClusterIsDrawnAtSomeThresholdAndNeverTwice() {
        var built = MeshletBuilder.Build(Shapes.Sphere(3));

        // The intervals [Error, ParentError) along a path through the DAG have to tile the number
        // line: a cluster whose interval is empty is a hole at one distance, and two clusters of one
        // path whose intervals overlap is the same surface drawn twice.
        Assert.All(
            built.Meshlets,
            meshlet => Assert.True(
                meshlet.Error < meshlet.ParentError,
                $"A cluster is drawn between an error of {meshlet.Error} and {meshlet.ParentError}."
            )
        );
    }

    [Fact]
    public void TheSurfaceStaysUnderThePixelThresholdAtTwentyDistances() {
        var mesh = Shapes.Sphere(4);
        var built = MeshletBuilder.Build(mesh);
        const float budget = 1f;

        var coarsest = 0;

        for (var step = 0; step < Distances; step++) {
            var distance = 2f + (step * 3f);
            var threshold = MeshletCut.ErrorForPixels(budget, distance, FieldOfView, ScreenHeight);
            var cut = MeshletCut.SelectByError(built, threshold);
            var corners = MeshletCut.Flatten(built, cut);
            var deviation = Geometry.Deviation(mesh.Positions, corners, mesh.Positions);
            var pixels = MeshletCut.PixelError(deviation, distance, FieldOfView, ScreenHeight);

            Assert.True(
                pixels <= budget,
                $"At {distance} units the cut of {corners.Length / 3} triangles deviates by {deviation}, "
                + $"which is {pixels} pixels against a budget of {budget}."
            );

            coarsest = Math.Max(coarsest, built.LevelCount - CountLevels(built, cut));
        }

        // And the measurement is worth making: a build that answered with the original mesh at every
        // distance would pass everything above.
        Assert.True(coarsest > 0, "The cut never coarsened, so the threshold was never what chose it.");
    }

    [Fact]
    public void TheFallbackFitsItsBudgetAndIsAWholeSurface() {
        var mesh = Shapes.Sphere(4);
        var built = MeshletBuilder.Build(mesh, new() { FallbackTriangles = 800 });

        Assert.InRange(built.Fallback.Length / 3, 1, 800);
        Assert.Empty(Geometry.Boundary(built.Fallback, Topology.Weld(mesh.Positions)));
    }

    [Fact]
    public void AFallbackBudgetBelowTheRootsIsTheRoots() {
        var mesh = Shapes.Sphere(3);
        var built = MeshletBuilder.Build(mesh, new() { FallbackTriangles = 1 });

        // There is nothing coarser to answer with, and a fallback with a hole in it would be worse
        // than one that costs more than it was asked to.
        Assert.Equal(built.Roots.Sum(root => built.Meshlets[root].TriangleCount), built.Fallback.Length / 3);
        Assert.Empty(Geometry.Boundary(built.Fallback, Topology.Weld(mesh.Positions)));
    }

    [Fact]
    public void PixelErrorAndItsInverseAgree() {
        const float distance = 12.5f;
        var error = MeshletCut.ErrorForPixels(3f, distance, FieldOfView, ScreenHeight);

        Assert.Equal(3f, MeshletCut.PixelError(error, distance, FieldOfView, ScreenHeight), 3);
    }

    [Fact]
    public void NothingBehindTheEyeIsEverSmallEnough() =>
        Assert.Equal(float.PositiveInfinity, MeshletCut.PixelError(0.001f, 0f, FieldOfView, ScreenHeight));

    /// <summary>A spread of thresholds from the finest level to past the coarsest.</summary>
    /// <param name="mesh">The DAG.</param>
    /// <returns>The thresholds.</returns>
    static IEnumerable<float> Thresholds(MeshletMesh mesh) {
        var largest = mesh.Groups.Length == 0 ? 1f : mesh.Groups.Max(group => group.Error);

        for (var step = 0; step <= Distances; step++) {
            yield return largest * step / Distances * 1.5f;
        }
    }

    /// <summary>How many distinct levels a cut draws from.</summary>
    /// <param name="mesh">The DAG.</param>
    /// <param name="cut">The clusters.</param>
    /// <returns>The count.</returns>
    static int CountLevels(MeshletMesh mesh, int[] cut) =>
        cut.Select(index => mesh.Meshlets[index].Level).Distinct().Count();
}
