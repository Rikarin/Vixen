// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Geometry.Uv.Flattening;
using Xunit;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>The ladder does what § D5 says, on the shapes § D5 was written about.</summary>
/// <remarks>
///     docs/plan/42 § D5 and § D6. ⚠ <b>The assertion that matters is that
///     <see cref="UvDistortion.Flipped" /> is zero</b>, per chart, on everything that produced an
///     island at all. It is a correctness field wearing a metric's clothes: a flipped triangle is a
///     region of the atlas where the mapping is not invertible, so a bake writes to the wrong texel
///     and sampling reads from it.
/// </remarks>
public class UvFlattenTests {
    public static TheoryData<string> DiskShapes =>
        ["sphere-cut-open", "cylinder-slit", "torus-slit", "hemisphere", "saddle", "strip", "obtuse-grid"];

    [Theory]
    [MemberData(nameof(DiskShapes))]
    public void EveryDiskFlattensWithoutAFold(string shape) {
        var mesh = FlattenFixtures.Build(shape);
        var islands = UvUnwrap.Flatten(mesh, ShapeCorpus.OneChart(mesh), new(), out var report);

        Assert.Single(islands);
        Assert.Equal(1, report.ChartCount);

        Assert.True(
            report.IsInjective,
            $"{shape} came back with {report.Distortion.Flipped} folded triangles of "
            + $"{islands[0].TriangleCount}. docs/plan/42 § D5 — that is a correctness failure, not a "
            + "quality one."
        );

        Assert.DoesNotContain(report.Warnings, warning => warning.Contains("refused", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(DiskShapes))]
    public void EveryIslandCarriesOneCoordinatePerCornerAndAUsableScale(string shape) {
        var mesh = FlattenFixtures.Build(shape);
        var island = UvUnwrap.Flatten(mesh, ShapeCorpus.OneChart(mesh), new())[0];

        Assert.Equal(island.Corners.Count, island.Coordinates.Count);
        Assert.Equal(0, island.Corners.Count % 3);
        Assert.True(island.Scale > 0f, $"{shape} reported a scale of {island.Scale}, so the packer cannot honour a density.");

        foreach (var coordinate in island.Coordinates) {
            Assert.True(float.IsFinite(coordinate.X) && float.IsFinite(coordinate.Y), $"{shape} produced {coordinate}.");
            Assert.True(coordinate.X >= island.Minimum.X && coordinate.X <= island.Maximum.X, "outside its own bounds");
            Assert.True(coordinate.Y >= island.Minimum.Y && coordinate.Y <= island.Maximum.Y, "outside its own bounds");
        }
    }

    /// <summary>The corners the flattener writes are the mesh's, so a caller can put the result back.</summary>
    [Fact]
    public void CornersIndexTheMeshsOwnCornerLayer() {
        var mesh = ShapeCorpus.Saddle();
        var island = UvUnwrap.Flatten(mesh, ShapeCorpus.OneChart(mesh), new())[0];

        foreach (var corner in island.Corners) {
            Assert.InRange(corner, 0, mesh.CornerCount - 1);
        }

        // Every corner exactly once: the chart is the whole mesh, so nothing may be dropped or
        // duplicated by the triangulation's corner mapping.
        Assert.Equal(mesh.CornerCount, island.Corners.Distinct().Count());
    }

    /// <summary>A flat chart is mapped by a similarity, so every measure is exactly one.</summary>
    [Fact]
    public void AFlatChartIsIsometricToWithinRounding() {
        var mesh = ShapeCorpus.Strip();

        UvUnwrap.Flatten(mesh, ShapeCorpus.OneChart(mesh), new(), out var report);

        Assert.Equal(0, report.Distortion.Flipped);
        Assert.Equal(1f, report.Distortion.Angular, 4);
        Assert.Equal(1f, report.Distortion.Area, 4);
        Assert.Equal(1f, report.Distortion.StretchL2, 4);
        Assert.Equal(1f, report.Distortion.StretchLInf, 4);
    }

    /// <summary>The smallest chart there is.</summary>
    [Fact]
    public void OneTriangleIsFlattenedExactly() {
        var mesh = ShapeCorpus.OneTriangle();
        var islands = UvUnwrap.Flatten(mesh, ShapeCorpus.OneChart(mesh), new(), out var report);

        Assert.Single(islands);
        Assert.Equal(1, islands[0].TriangleCount);
        Assert.Equal(0, report.Distortion.Flipped);

        // A single triangle has an isometric parameterization and the ladder has to find it: two of
        // its three vertices are pinned at their true distance and the third has nowhere else to be.
        Assert.Equal(1f, report.Distortion.StretchL2, 3);
    }

    /// <summary>Two triangles sharing an edge, which is the smallest chart with an interior edge.</summary>
    [Fact]
    public void TwoTrianglesSharingAnEdgeAreFlattenedExactly() {
        var mesh = ShapeCorpus.TwoTriangles();
        var islands = UvUnwrap.Flatten(mesh, ShapeCorpus.OneChart(mesh), new(), out var report);

        Assert.Single(islands);
        Assert.Equal(2, islands[0].TriangleCount);
        Assert.Equal(0, report.Distortion.Flipped);
        Assert.Equal(1f, report.Distortion.StretchL2, 3);
    }

    /// <summary>A triangle with no area is dropped from every energy, named, and not called a fold.</summary>
    /// <remarks>
    ///     ⚠ <b>Its cotangents are infinite and it has no orientation to reverse.</b> Counting it as
    ///     flipped would put a correctness field permanently above zero on any mesh a conditioning pass
    ///     left a sliver in — and the field that must be zero cannot be the field that is always one.
    /// </remarks>
    [Fact]
    public void ATriangleWithNoAreaIsDroppedRatherThanCountedAsAFold() {
        var mesh = ShapeCorpus.WithDegenerateTriangle();
        var islands = UvUnwrap.Flatten(mesh, ShapeCorpus.OneChart(mesh), new(), out var report);

        Assert.Single(islands);
        Assert.Equal(3, islands[0].TriangleCount);
        Assert.Equal(0, report.Distortion.Flipped);

        Assert.Contains(
            report.Warnings,
            warning => warning.Contains("1 triangles with no area in three", StringComparison.Ordinal)
        );

        // The two real triangles are a flat square, so the measures are exactly one — which is only
        // true if the degenerate one contributed nothing to any of them rather than an infinity.
        Assert.Equal(1f, report.Distortion.StretchL2, 4);
        Assert.Equal(1f, report.Distortion.Area, 4);
    }

    /// <summary>A negative face index leaves the face out entirely rather than making a chart of it.</summary>
    [Fact]
    public void ANegativeChartLeavesTheFaceOut() {
        var mesh = ShapeCorpus.Saddle();
        var charts = ShapeCorpus.Strips(mesh, 2);

        // ⚠ A contiguous half rather than every other face. A checkerboard leaves a chart whose faces
        // share no edge, and `Disconnected` is the right answer to that — which is a different test.
        for (var face = 0; face < charts.Length; face++) {
            if (charts[face] == 1) {
                charts[face] = -1;
            }
        }

        var islands = UvUnwrap.Flatten(mesh, charts, new(), out var report);
        var covered = islands.Sum(island => island.TriangleCount);

        Assert.True(covered > 0);
        Assert.True(covered < mesh.Triangulate().Length / 3, "every face was flattened, so nothing was excluded");
        Assert.Equal(0, report.Distortion.Flipped);
    }

    /// <summary>Several charts over one mesh, and each one flattens on its own.</summary>
    [Fact]
    public void ChartsAreIndependentAndComeBackInAscendingOrder() {
        var mesh = ShapeCorpus.SphereCutOpen();
        var islands = UvUnwrap.Flatten(mesh, ShapeCorpus.Strips(mesh, 12), new(), out var report);

        Assert.Equal(12, islands.Count);
        Assert.Equal(12, report.ChartCount);
        Assert.Equal(0, report.Distortion.Flipped);

        var detail = UvUnwrap.Detail(mesh, ShapeCorpus.Strips(mesh, 12), new(), null, 0);

        Assert.Equal(Enumerable.Range(0, 12), detail.ChartOfIsland);
    }

    /// <summary>Chart ids are read rather than assumed dense, because a charter that split leaves gaps.</summary>
    [Fact]
    public void SparseChartIdsAreKeptAsTheyWere() {
        var mesh = ShapeCorpus.Saddle();
        var charts = ShapeCorpus.Strips(mesh, 4);

        for (var face = 0; face < charts.Length; face++) {
            charts[face] = (charts[face] * 7) + 100;
        }

        var detail = UvUnwrap.Detail(mesh, charts, new(), null, 0);

        Assert.Equal([100, 107, 114, 121], detail.ChartOfIsland);
    }

    [Fact]
    public void TheReportNamesTheStageAndTheTriangleCount() {
        var mesh = ShapeCorpus.CylinderSlit();

        UvUnwrap.Flatten(mesh, ShapeCorpus.OneChart(mesh), new(), out var report);

        var timing = Assert.Single(report.Stages);

        Assert.Equal(UvStage.Flatten, timing.Stage);
        Assert.Equal(mesh.Triangulate().Length / 3, timing.Elements);
    }

    [Fact]
    public void AChartAssignmentOfTheWrongLengthIsRefusedByName() {
        var mesh = ShapeCorpus.TwoTriangles();
        var thrown = Assert.Throws<ArgumentException>(() => UvUnwrap.Flatten(mesh, [0], new()));

        Assert.Contains("1 chart assignments for 2 faces", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FlattenedIslandsPackWithoutTheirShapesChanging() {
        var mesh = ShapeCorpus.SphereCutOpen();
        var islands = UvUnwrap.Flatten(mesh, ShapeCorpus.Strips(mesh, 24), new());
        var placements = UvUnwrap.Pack(islands, new() { Resolution = 512, Margin = 2 }, out var packed);

        Assert.Equal(islands.Count, placements.Count);
        Assert.True(packed.PackingEfficiency > 0.1f, $"{packed.PackingEfficiency:P2} of the atlas is not a pack.");
    }
}

/// <summary>The corpus by name, so a theory can name a shape and a failure message can too.</summary>
static class FlattenFixtures {
    /// <summary>Every shape, in the order the baseline table quotes them.</summary>
    public static string[] Corpus =>
        ["sphere-cut-open", "cylinder-slit", "torus-slit", "hemisphere", "saddle", "strip", "obtuse-grid"];

    public static EditMesh Build(string shape, float scale = 1f) =>
        shape switch {
            "sphere-cut-open" => ShapeCorpus.SphereCutOpen(scale),
            "cylinder-slit" => ShapeCorpus.CylinderSlit(scale),
            "torus-slit" => ShapeCorpus.TorusSlit(scale),
            "hemisphere" => ShapeCorpus.Hemisphere(scale),
            "saddle" => ShapeCorpus.Saddle(scale),
            "strip" => ShapeCorpus.Strip(scale),
            "obtuse-grid" => ShapeCorpus.ObtuseGrid(),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Not one of the corpus's shapes.")
        };

    /// <summary>Every island's coordinates, keyed by the mesh corner they belong to.</summary>
    public static Dictionary<int, Vector2> ByCorner(IReadOnlyList<UvIsland> islands) {
        var coordinates = new Dictionary<int, Vector2>();

        foreach (var island in islands) {
            for (var corner = 0; corner < island.Corners.Count; corner++) {
                coordinates[island.Corners[corner]] = island.Coordinates[corner];
            }
        }

        return coordinates;
    }
}
