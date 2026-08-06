// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>What the charter must produce, and the four ways it could fail to terminate.</summary>
/// <remarks>
///     <para>
///         docs/plan/42 § D3. <b>Chart count is an outcome of a quality target rather than a knob</b>, so
///         almost nothing here asserts a count — what it asserts is the property that makes a count
///         meaningful: <b>every chart the charter emits is a disk the flattener will accept</b>. § D5's
///         ladder assumes one, and an annulus, a closed component and a pinch each need a different
///         answer rather than a retry.
///     </para>
///     <para>
///         ⚠ <b>Exit criterion 2 is "no exceptions, no hangs", and that is a statement about the
///         recursion rather than about the geometry.</b> The two ways it could fail are a chart that
///         never comes under τ and never gets smaller, and a split that returns its own input — both are
///         covered below, at settings chosen to provoke them rather than at the defaults.
///     </para>
/// </remarks>
public class UvChartTests {
    public static TheoryData<string> Corpus =>
        [
            "sphere-cut-open",
            "cylinder-slit",
            "cylinder-closed",
            "torus-slit",
            "torus-closed",
            "hemisphere",
            "saddle",
            "strip",
            "obtuse-grid",
            "sphere-nearly-closed",
            "dumbbell",
            "grouped-plate",
            "bowtie",
            "two-islands",
            "one-triangle",
            "two-triangles",
            "degenerate-triangle"
        ];

    /// <summary>Every chart is a disk the flattener accepts, on every shape in the corpus.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the phase's contract and everything else is detail.</b> § D5's ladder is defined
    ///     on a disk; U2 refuses anything else by name before a solve runs. So a charter that emitted an
    ///     annulus would not produce a bad atlas, it would produce a chart with <i>no coordinates at
    ///     all</i> — and the mesh's texture would have a hole in it that no distortion figure mentions.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Corpus))]
    public void EveryChartIsADiskTheFlattenerAccepts(string shape) {
        var mesh = ChartFixtures.Build(shape);
        var charts = UvUnwrap.Charts(mesh, new(), out var report);

        Assert.Equal(mesh.FaceCount, charts.Count);

        var count = charts.Max() + 1;

        Assert.Equal(count, report.ChartCount);

        for (var face = 0; face < charts.Count; face++) {
            Assert.InRange(charts[face], 0, count - 1);
        }

        var islands = UvUnwrap.Flatten(mesh, charts, new(), out var flattened);

        Assert.True(
            islands.Count == count,
            $"{shape}: {count} charts produced {islands.Count} islands, so {count - islands.Count} of "
            + $"them could not be laid flat. The flattener said: {string.Join(" ", flattened.Warnings)}"
        );

        Assert.Equal(0, flattened.Distortion.Flipped);
        Assert.True(flattened.IsInjective);
    }

    /// <summary>A shape that needs no cut is not cut.</summary>
    /// <remarks>
    ///     ⚠ <b>A flat plate and a slit cylinder are both isometric, so a charter that split either of
    ///     them is splitting on something other than distortion.</b> That is the failure § D3 names in
    ///     the established tools — a growth bound tripping rather than a quality target being missed —
    ///     and it is invisible on a curved shape, where a split is always defensible.
    /// </remarks>
    [Theory]
    [InlineData("strip")]
    [InlineData("cylinder-slit")]
    [InlineData("one-triangle")]
    [InlineData("two-triangles")]
    public void AShapeThatNeedsNoCutIsNotCut(string shape) {
        var mesh = ChartFixtures.Build(shape);
        var charts = UvUnwrap.Charts(mesh, new(), out var report);

        Assert.Equal(1, report.ChartCount);
        Assert.All(charts, chart => Assert.Equal(0, chart));
        Assert.Equal(0f, report.SeamLength);
        Assert.Equal(0f, report.SeamLengthNormalized);
    }

    /// <summary>A chart that shares no edge with another is its own chart before anything is measured.</summary>
    /// <remarks>
    ///     ⚠ <b>Three of <c>ChartRefusal</c>'s reasons are answered here rather than by a bisection.</b>
    ///     A disconnected region, a bowtie pinch and a region reached through a non-manifold edge are
    ///     none of them shape problems, so a split weighted by concavity would be answering the wrong
    ///     question about all three. The dual graph only ever links faces across an edge carrying exactly
    ///     two of them, which makes all three fall out of the seeding.
    /// </remarks>
    [Theory]
    [InlineData("two-islands")]
    [InlineData("bowtie")]
    public void APieceThatSharesNoEdgeIsItsOwnChart(string shape) {
        var mesh = ChartFixtures.Build(shape);
        var charts = UvUnwrap.Charts(mesh, new(), out var report);

        Assert.Equal(2, report.ChartCount);
        Assert.NotEqual(charts[0], charts[1]);
    }

    /// <summary>A closed surface has no boundary, so it is cut until it has one.</summary>
    [Theory]
    [InlineData("torus-closed")]
    [InlineData("dumbbell")]
    [InlineData("cylinder-closed")]
    public void AClosedOrAnnularSurfaceIsCutOpen(string shape) {
        var mesh = ChartFixtures.Build(shape);
        var charts = UvUnwrap.Charts(mesh, new(), out var report);

        Assert.True(report.ChartCount > 1, $"{shape} has no boundary and was left as one chart.");
        Assert.True(report.SeamLength > 0f, $"{shape} was charted into {report.ChartCount} with no seam.");

        // And the cut is a real one: the flattener takes every piece.
        Assert.Equal(report.ChartCount, UvUnwrap.Flatten(mesh, charts, new()).Count);
    }

    /// <summary>Group boundaries partition first and unconditionally, and the merge pass may not undo it.</summary>
    /// <remarks>
    ///     docs/plan/42 § D3. The plate is flat, so distortion has no opinion at all and the merge-back
    ///     pass has every reason to put the two halves together — which is exactly what makes this a test
    ///     of the word <i>unconditionally</i> rather than of the seeding.
    /// </remarks>
    [Fact]
    public void GroupBoundariesPartitionFirstAndTheMergePassMayNotUndoIt() {
        var mesh = ShapeCorpus.GroupedPlate();
        var kept = UvUnwrap.Charts(mesh, new(), out var keptReport);

        Assert.Equal(2, keptReport.ChartCount);

        for (var face = 0; face < mesh.FaceCount; face++) {
            for (var other = 0; other < mesh.FaceCount; other++) {
                if (mesh.Faces[face].Group != mesh.Faces[other].Group) {
                    Assert.NotEqual(kept[face], kept[other]);
                }
            }
        }

        // And turning it off leaves a flat plate as the one chart it always was.
        UvUnwrap.Charts(mesh, new() { KeepGroups = false }, out var fused);

        Assert.Equal(1, fused.ChartCount);
    }

    /// <summary>A mesh with no groups at all charts the same either way.</summary>
    [Fact]
    public void AMeshWithNoGroupsChartsTheSameWithGroupsKeptOrNot() {
        var mesh = ShapeCorpus.SphereCutOpen();

        Assert.Equal(
            UvUnwrap.Charts(mesh, new() { KeepGroups = true }),
            UvUnwrap.Charts(mesh, new() { KeepGroups = false })
        );
    }

    /// <summary>An isometric threshold is unreachable for anything curved, and still terminates.</summary>
    /// <remarks>
    ///     ⚠ <b>A zero that means "off", in the shape docs/plan/42 § D3 leaves open.</b> One is a
    ///     perfectly isometric map, which no curved surface has — so every chart fails, every chart
    ///     recurses, and the only thing standing between the caller and a hang is
    ///     <see cref="UvSettings.MaxDepth" /> and the rule that a split which fails to reduce is detected
    ///     rather than repeated. Exit criterion 2 asks for no exceptions and no hangs before it asks for
    ///     quality.
    /// </remarks>
    [Theory]
    [InlineData("sphere-cut-open")]
    [InlineData("hemisphere")]
    [InlineData("dumbbell")]
    public void AnIsometricThresholdRecursesToTheDepthBoundAndStops(string shape) {
        var mesh = ChartFixtures.Build(shape);
        var settings = new UvSettings { DistortionThreshold = 1f, MaxDepth = 4 };
        var charts = UvUnwrap.Charts(mesh, settings, out var report);

        // Two to the depth bound is the most a halving recursion can reach, per connected seed.
        Assert.InRange(report.ChartCount, 2, (1 << 4) * Math.Max(1, CountSeeds(mesh)));
        Assert.Equal(report.ChartCount, UvUnwrap.Flatten(mesh, charts, settings).Count);
    }

    /// <summary>A depth bound of zero still cuts what has to be cut.</summary>
    /// <remarks>
    ///     ⚠ <b>The two bounds are different bounds and this is what says so.</b>
    ///     <see cref="UvSettings.MaxDepth" /> governs the <i>distortion</i> recursion; a chart that
    ///     cannot be laid flat at all is cut regardless, because accepting one would ship a chart with no
    ///     coordinates. A single depth counter for both would make a closed torus at
    ///     <c>MaxDepth = 0</c> unwrap to nothing.
    /// </remarks>
    [Theory]
    [InlineData("torus-closed")]
    [InlineData("dumbbell")]
    [InlineData("cylinder-closed")]
    public void ADepthBoundOfZeroStillCutsWhatMustBeCut(string shape) {
        var mesh = ChartFixtures.Build(shape);
        var settings = new UvSettings { MaxDepth = 0 };
        var charts = UvUnwrap.Charts(mesh, settings, out var report);

        Assert.True(report.ChartCount > 1);
        Assert.Equal(report.ChartCount, UvUnwrap.Flatten(mesh, charts, settings).Count);
    }

    /// <summary>Every seam weight at zero is a legal setting and produces a legal answer.</summary>
    /// <remarks>
    ///     ⚠ <b>A zero that means "off" in the other direction.</b> With no weights the barrier metric
    ///     would be identically zero, every face would sit at distance zero from every seed, and the
    ///     bisection would return whatever a tie-break said rather than a bisection. The floor in
    ///     <c>SeamGraph</c> is what turns the degenerate setting into a plain geodesic split, which is a
    ///     defensible answer to <i>"cut this in half and tell me nothing about where"</i>.
    /// </remarks>
    [Fact]
    public void EverySeamWeightAtZeroStillCharts() {
        var settings = new UvSettings {
            SeamCost = new() {
                Concavity = 0f,
                Visibility = 0f,
                Feature = 0f,
                Material = 0f,
                Symmetry = 0f,
                Length = 0f,
                Existing = 0f
            }
        };

        foreach (var shape in new[] { "dumbbell", "torus-closed", "sphere-cut-open" }) {
            var mesh = ChartFixtures.Build(shape);
            var charts = UvUnwrap.Charts(mesh, settings, out var report);

            Assert.True(report.ChartCount >= 1);
            Assert.Equal(report.ChartCount, UvUnwrap.Flatten(mesh, charts, settings).Count);
        }
    }

    /// <summary>An empty mesh is an empty answer rather than an exception.</summary>
    [Fact]
    public void AnEmptyMeshCharts() {
        var charts = UvUnwrap.Charts(new(), new(), out var report);

        Assert.Empty(charts);
        Assert.Equal(0, report.ChartCount);
        Assert.Equal(0f, report.SeamLength);
    }

    /// <summary>A null argument is named.</summary>
    [Fact]
    public void ANullArgumentIsNamed() {
        Assert.Throws<ArgumentNullException>(() => UvUnwrap.Charts(null!, new()));
        Assert.Throws<ArgumentNullException>(() => UvUnwrap.Charts(new(), null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => UvUnwrap.Charts(new(), new() { MaxDepth = -1 }));
    }

    /// <summary>The seam length is measured on cuts, not on the mesh's own boundary.</summary>
    /// <remarks>
    ///     ⚠ <b>Otherwise an unwrap of a flat square would report the perimeter of the square.</b> Nothing
    ///     was cut there, and a figure that counted it would make seam length a statement about a
    ///     surface's silhouette rather than about what the charter did to it.
    /// </remarks>
    [Fact]
    public void AMeshBoundaryIsNotASeam() {
        var mesh = ShapeCorpus.Strip();

        UvUnwrap.Charts(mesh, new(), out var report);

        Assert.Equal(0f, report.SeamLength);
    }

    /// <summary>A decomposition that declines falls back, and one that answers is used.</summary>
    /// <remarks>
    ///     docs/plan/42 § D14's second rule: <b>it proposes and never decides</b>. Whatever comes back is
    ///     still flattened, still measured, and still has to pass τ — so the worst a bad proposer can do
    ///     is cost chart quality, and it can never cost validity.
    /// </remarks>
    [Fact]
    public void ADecompositionProposesAndNeverDecides() {
        var mesh = ShapeCorpus.Dumbbell();
        var declining = new Declining();
        var built = UvUnwrap.Charts(mesh, new(), out var builtReport);

        Assert.Equal(built, UvUnwrap.Charts(mesh, new() { Decomposition = declining }, out var declined));
        Assert.Equal(builtReport.ChartCount, declined.ChartCount);
        Assert.True(declining.Asked > 0, "the hook was never reached, so declining proved nothing");

        // One that answers is obeyed — and its answer still has to be a set of disks.
        var settings = new UvSettings { Decomposition = new Alternating() };
        var charts = UvUnwrap.Charts(mesh, settings, out var report);

        Assert.True(report.ChartCount > 1);
        Assert.Equal(report.ChartCount, UvUnwrap.Flatten(mesh, charts, settings).Count);
    }

    static int CountSeeds(EditMesh mesh) => UvUnwrap.Charts(mesh, new() { MaxDepth = 0 }).Distinct().Count();

    /// <summary>A decomposition that never has an opinion.</summary>
    sealed class Declining : IChartDecomposition {
        public int Asked { get; private set; }

        public IReadOnlyList<int>? Decompose(EditMesh mesh, IReadOnlyList<int> faces, int parts) {
            Asked++;

            return null;
        }
    }

    /// <summary>A decomposition that splits by parity, which is deterministic and geometrically silly.</summary>
    sealed class Alternating : IChartDecomposition {
        public IReadOnlyList<int> Decompose(EditMesh mesh, IReadOnlyList<int> faces, int parts) {
            var split = new int[faces.Count];

            for (var index = 0; index < faces.Count; index++) {
                split[index] = index < faces.Count / 2 ? 0 : 1;
            }

            return split;
        }
    }
}

/// <summary>The corpus, by name, for the charting suites.</summary>
static class ChartFixtures {
    public static EditMesh Build(string shape, float scale = 1f) =>
        shape switch {
            "sphere-cut-open" => ShapeCorpus.SphereCutOpen(scale),
            "cylinder-slit" => ShapeCorpus.CylinderSlit(scale),
            "cylinder-closed" => ShapeCorpus.CylinderClosed(scale),
            "torus-slit" => ShapeCorpus.TorusSlit(scale),
            "torus-closed" => ShapeCorpus.TorusClosed(scale),
            "hemisphere" => ShapeCorpus.Hemisphere(scale),
            "saddle" => ShapeCorpus.Saddle(scale),
            "strip" => ShapeCorpus.Strip(scale),
            "obtuse-grid" => ShapeCorpus.ObtuseGrid(),
            "sphere-nearly-closed" => ShapeCorpus.SphereNearlyClosed(scale),
            "dumbbell" => ShapeCorpus.Dumbbell(scale),
            "grouped-plate" => ShapeCorpus.GroupedPlate(scale),
            "bowtie" => ShapeCorpus.Bowtie(),
            "two-islands" => ShapeCorpus.TwoIslands(),
            "one-triangle" => ShapeCorpus.OneTriangle(scale),
            "two-triangles" => ShapeCorpus.TwoTriangles(),
            "degenerate-triangle" => ShapeCorpus.WithDegenerateTriangle(),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Not one of the corpus's shapes.")
        };
}
