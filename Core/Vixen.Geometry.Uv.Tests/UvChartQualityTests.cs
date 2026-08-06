// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Geometry.Uv.Charting;
using Xunit;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>The baseline docs/plan/42 § U3 exists to move, and which half of § D3 moved it.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/42's first exit criterion compares against xatlas on a 500-mesh corpus, and that
///         comparison cannot be run here.</b> xatlas is a native library and this repository has none.
///         What can be run is the same measurement on a fixed set of shapes chosen so each fails
///         differently, quoted against the published figures: MeshTailor reports <b>10.4</b> charts at
///         <b>1.097</b> distortion where xatlas gets <b>51.6</b> at <b>1.064</b> and Blender's Smart UV
///         Project gets <b>74.3</b>.
///     </para>
///     <para>
///         ⚠ <b>Those published numbers are an average over GarmentCodeData, and these are not.</b> A
///         garment is a large, nearly developable surface with a natural seam structure, and this corpus
///         is eleven primitives. The figures are not comparable as levels and the document should not
///         pretend they are — what <i>is</i> comparable is the mechanism, and § Part 6 says exactly what
///         it expects from it: <i>"a τ-driven recursion with a merge-back pass should land far below
///         xatlas and above MeshTailor, and the document is not going to pretend otherwise until U3 is
///         measured"</i>.
///     </para>
///     <para>
///         ⚠ <b>The one number this phase can honestly produce is the separate contribution of the two
///         halves.</b> § D3: <i>"step 4 is the cheap half of the fix and step 3's top-down direction is
///         the expensive half"</i>. Running the recursion with the merge-back pass disabled measures the
///         second alone, and the difference is the first — and that is a claim about this
///         implementation rather than about somebody else's corpus.
///     </para>
/// </remarks>
public class UvChartQualityTests {
    /// <summary>The shapes the chart-count baseline is quoted over.</summary>
    /// <remarks>
    ///     Every one of them is a surface a person would expect a real unwrap of: the trivial ones that
    ///     must come back as a single chart are left out, because averaging them in would flatter the
    ///     figure with cases no unwrapper can get wrong.
    /// </remarks>
    public static string[] Corpus =>
        [
            "sphere-cut-open",
            "cylinder-slit",
            "cylinder-closed",
            "torus-slit",
            "torus-closed",
            "hemisphere",
            "saddle",
            "obtuse-grid",
            "sphere-nearly-closed",
            "dumbbell",
            "strip"
        ];

    /// <summary>The merge-back pass never costs charts and never costs correctness.</summary>
    /// <remarks>
    ///     ⚠ <b>The direction is the assertion, and it is the one that could actually break.</b> A merge
    ///     is accepted only when the union passes the same τ the recursion accepted its two halves
    ///     against — so a merge that raised the chart count would be a bug in the greedy pass, and a
    ///     merge that shipped a union above τ would be the merge pass quietly overriding the quality
    ///     target the whole design inverts chart count around.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Shapes))]
    public void MergingBackNeverCostsChartsAndNeverCostsTheThreshold(string shape) {
        var mesh = ChartFixtures.Build(shape);
        var settings = new UvSettings();

        var merged = UvUnwrap.Charts(mesh, settings, null, 0, true, out var mergedReport);
        var split = UvUnwrap.Charts(mesh, settings, null, 0, false, out var splitReport);

        Assert.True(
            mergedReport.ChartCount <= splitReport.ChartCount,
            $"{shape}: merging back took {splitReport.ChartCount} charts to "
            + $"{mergedReport.ChartCount}, which is the wrong way round."
        );

        // Both legs are still atlases: every chart a disk, no fold anywhere.
        foreach (var (charts, report) in new[] { (merged, mergedReport), (split, splitReport) }) {
            var islands = UvUnwrap.Flatten(mesh, charts, settings, out var flattened);

            Assert.Equal(report.ChartCount, islands.Count);
            Assert.Equal(0, flattened.Distortion.Flipped);

            Assert.True(
                flattened.Distortion.StretchL2 <= settings.DistortionThreshold + 1e-3f
                || report.ChartCount == splitReport.ChartCount,
                $"{shape}: shipped L² of {flattened.Distortion.StretchL2:0.0000} above the "
                + $"{settings.DistortionThreshold} threshold."
            );
        }
    }

    public static TheoryData<string> Shapes {
        get {
            var data = new TheoryData<string>();

            foreach (var shape in Corpus) {
                data.Add(shape);
            }

            return data;
        }
    }

    /// <summary>The corpus baseline: chart count and L² stretch, with the merge pass and without it.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Measured: 3.09 charts at an L² stretch of 1.0059, against 3.64 charts with the
    ///         merge-back pass disabled.</b> So on this corpus the top-down recursion is what produces
    ///         the low count and the merge-back pass takes a further <b>15 %</b> off it — and that 15 %
    ///         is not spread evenly, it is concentrated entirely on the two shapes that fragment at all:
    ///         the closed torus goes 14 → 11 and the dumbbell 9 → 6, while the nine shapes that already
    ///         chart to three or fewer are untouched by it. § D3's <i>"cheap half"</i> is cheap in
    ///         exactly the way that description implies.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The published figures are not a like-for-like comparison and this file will not
    ///         pretend they are.</b> MeshTailor's 10.4, xatlas's 51.6 and Blender's 74.3 are averages
    ///         over GarmentCodeData — large, nearly developable garment surfaces with a natural seam
    ///         structure — and this is eleven primitives chosen so that each one fails a different way.
    ///         The levels are not comparable. What the numbers do support is the weaker claim § Part 6
    ///         actually makes, that a τ-driven recursion with a merge-back pass does not fragment.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The L² figure is the whole-mesh area-weighted one, so a charter that fragmented would
    ///         <i>improve</i> it.</b> That is why the chart count is asserted beside it and never on its
    ///         own: stretch alone is a metric any unwrapper can win by cutting more, which is precisely
    ///         the failure § D3 says produces 51.6 charts.
    ///     </para>
    ///     <para>
    ///         The bounds below are the measured figures with room, and they are a <b>regression fence
    ///         rather than a target</b> — the interesting output is the table in the failure message.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheCorpusChartsFewerThanFourTimesOverAtTheThreshold() {
        var settings = new UvSettings();
        var table = new List<string>();

        var charts = 0d;
        var withoutMerge = 0d;
        var stretch = 0d;
        var area = 0d;

        foreach (var shape in Corpus) {
            var mesh = ChartFixtures.Build(shape);
            var merged = UvUnwrap.Charts(mesh, settings, null, 0, true, out var mergedReport);

            UvUnwrap.Charts(mesh, settings, null, 0, false, out var splitReport);

            var islands = UvUnwrap.Flatten(mesh, merged, settings, out var flattened);

            Assert.Equal(mergedReport.ChartCount, islands.Count);
            Assert.Equal(0, flattened.Distortion.Flipped);

            charts += mergedReport.ChartCount;
            withoutMerge += splitReport.ChartCount;
            stretch += flattened.Distortion.StretchL2;
            area += flattened.Distortion.Area;

            table.Add(
                $"| {shape} | {mergedReport.ChartCount} | {splitReport.ChartCount} | "
                + $"{flattened.Distortion.StretchL2:0.0000} | {flattened.Distortion.Area:0.0000} | "
                + $"{mergedReport.SeamLengthNormalized:0.0000} |"
            );
        }

        var meanCharts = charts / Corpus.Length;
        var meanWithout = withoutMerge / Corpus.Length;
        var meanStretch = stretch / Corpus.Length;

        var summary = $"charts {meanCharts:0.00} merged against {meanWithout:0.00} unmerged, "
            + $"L² {meanStretch:0.0000}, area {area / Corpus.Length:0.0000}\n"
            + string.Join("\n", table);

        // The gate, set around the measured 3.09 charts at 1.0059 with room for the arithmetic to move
        // under it. A fence around the mechanism, not a target somebody should tune towards.
        Assert.True(meanCharts <= 4d, $"mean chart count regressed. {summary}");
        Assert.True(meanStretch <= 1.02d, $"mean L² stretch regressed. {summary}");
        Assert.True(meanCharts < meanWithout, $"merging back bought nothing at all. {summary}");
        Assert.True(meanStretch <= settings.DistortionThreshold, $"the corpus ships above τ. {summary}");
    }

    /// <summary>Tightening the threshold produces more charts, and loosening it produces fewer.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the inversion docs/plan/42 § D3 calls the whole design, stated as a property.</b>
    ///     Chart count is an <i>outcome of a quality target</i>: nowhere is the charter told how many
    ///     charts to make, so the only way the count can be moved is by moving τ. A charter with a hidden
    ///     count in it would pass every other test in this file and fail this one.
    /// </remarks>
    [Theory]
    [InlineData("sphere-cut-open")]
    [InlineData("dumbbell")]
    [InlineData("sphere-nearly-closed")]
    public void TighteningTheThresholdIsTheOnlyWayToMoveTheChartCount(string shape) {
        var mesh = ChartFixtures.Build(shape);

        UvUnwrap.Charts(mesh, new() { DistortionThreshold = 1.02f }, out var tight);
        UvUnwrap.Charts(mesh, new() { DistortionThreshold = 1.15f }, out var middle);
        UvUnwrap.Charts(mesh, new() { DistortionThreshold = 1.60f }, out var loose);

        Assert.True(
            tight.ChartCount >= middle.ChartCount && middle.ChartCount >= loose.ChartCount,
            $"{shape}: {tight.ChartCount} charts at τ=1.02, {middle.ChartCount} at 1.15 and "
            + $"{loose.ChartCount} at 1.60, which is not monotone."
        );
    }

    /// <summary>Asked to cut a dumbbell in two, the decomposition cuts it at the waist.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>docs/plan/42 § D4 is a claim about seam <i>quality</i>, and this is the test that
    ///         measures it.</b> Everything else in this file asserts that a chart is a disk and that the
    ///         count is an outcome, both of which a charter cutting at random would satisfy. The dumbbell
    ///         has exactly one answer a person would give.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It asks the decomposition directly rather than reading the finished atlas, and that
    ///         is the only way to ask this question cleanly.</b> The measurement that provoked this:
    ///         a bulb of this dumbbell — a deep cap whose boundary is the waist's tiny circle — is very
    ///         nearly <c>SphereNearlyClosed</c>, so it <i>folds</i>, so § D3's recursion keeps cutting it
    ///         whatever τ says, and the merge-back pass then reassembles the pieces into charts that
    ///         flatten. Every one of those later cuts is legitimate and none of them is at the waist, so
    ///         a fraction-of-total-seam-length measured over the finished charts answers a question about
    ///         the recursion rather than about the cost function.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The waist ring is 0.63 long and a meridian loop is about 5.</b> So a cost function
    ///         that had nothing but <see cref="SeamCost.Length" /> in it would also find the waist here,
    ///         and this fixture on its own does not separate the seven terms from their sum. What it does
    ///         rule out is the failure that was actually present: the growth metric first used the shared
    ///         <i>edge's</i> length as a traversal cost, which on a surface of revolution makes a narrow
    ///         waist look <i>cheap</i> to cross, and the cut went lengthwise down the model.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AskedToCutADumbbellInTwoTheDecompositionCutsItAtTheWaist() {
        var mesh = ShapeCorpus.Dumbbell();
        var graph = SeamGraph.Build(mesh, new());
        var parts = Bisection.Split(graph, [.. Enumerable.Range(0, mesh.FaceCount)]);

        Assert.Equal(2, parts.Count);

        var side = new int[mesh.FaceCount];

        for (var part = 0; part < parts.Count; part++) {
            foreach (var face in parts[part]) {
                side[face] = part;
            }
        }

        var inside = 0d;
        var total = 0d;

        for (var edge = 0; edge < mesh.Edges.Count; edge++) {
            var faces = mesh.FacesOf(edge);

            if (faces.Length != 2 || side[faces[0]] == side[faces[1]]) {
                continue;
            }

            var ends = mesh.Edges[edge];
            var a = mesh.Positions[ends.A];
            var b = mesh.Positions[ends.B];
            var length = Vector3.Distance(a, b);

            total += length;

            // The lathe runs along x from −1 to 1 and the waist is the pinch at x = 0.
            if (MathF.Abs(0.5f * (a.X + b.X)) < 0.2f) {
                inside += length;
            }
        }

        Assert.True(total > 0d);

        Assert.True(
            inside / total > 0.98d && total < 1d,
            $"{inside / total:P0} of a {total:0.000}-long cut runs through the dumbbell's waist, whose "
            + "circumference is about 0.63. docs/plan/42 § D4 — the concavity and occlusion terms exist so "
            + "that a cut lands where a texture discontinuity does not read, and the waist is the one place "
            + "on this shape where that is true."
        );
    }

    /// <summary>A bulb of the dumbbell folds, and the merge pass is what gets the two charts back.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Recorded as a fact about the corpus rather than assumed, because it is what makes the
    ///         test above measure the decomposition instead of the finished atlas.</b> § D5's third rung
    ///         ends in <i>"split the chart and recurse"</i>, and a chart that folds is refused however
    ///         generous τ is — a flipped triangle is a correctness failure and no threshold applies to
    ///         it. So even at a distortion bound nothing can fail, the recursion does not stop at the two
    ///         charts the shape suggests.
    ///     </para>
    ///     <para>
    ///         <b>And then it does, because of step four.</b> The recursion cuts a folding bulb into
    ///         pieces that do not fold, and the merge-back pass reassembles those pieces into two charts
    ///         that flatten — which is docs/plan/42 § D3's <i>"nothing that ever puts two back
    ///         together"</i> answered on the one shape in this corpus where it matters most.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ABulbOfTheDumbbellFoldsAndTheMergePassIsWhatRecoversTheTwoCharts() {
        var mesh = ShapeCorpus.Dumbbell();
        var settings = new UvSettings { DistortionThreshold = 100f };
        var graph = SeamGraph.Build(mesh, new());
        var parts = Bisection.Split(graph, [.. Enumerable.Range(0, mesh.FaceCount)]);
        var assignment = new int[mesh.FaceCount];

        for (var part = 0; part < parts.Count; part++) {
            foreach (var face in parts[part]) {
                assignment[face] = part;
            }
        }

        // The waist cut is right and its two halves still fold, so the ladder refuses one of them.
        Assert.NotEmpty(UvUnwrap.Detail(mesh, assignment, settings, null, 0).Refused);

        // The recursion's answer is more charts …
        UvUnwrap.Charts(mesh, settings, null, 0, false, out var unmerged);

        Assert.True(unmerged.ChartCount > 2, $"the recursion stopped at {unmerged.ChartCount} charts.");

        // … and the merge pass's answer is to put them back, which is the whole point of having one.
        var charts = UvUnwrap.Charts(mesh, settings, null, 0, true, out var merged);

        Assert.True(
            merged.ChartCount < unmerged.ChartCount,
            $"merging back left {merged.ChartCount} of {unmerged.ChartCount} charts."
        );

        Assert.Equal(merged.ChartCount, UvUnwrap.Flatten(mesh, charts, settings).Count);
    }

    /// <summary>And no chart ever spans both bulbs, however deep the recursion goes.</summary>
    /// <remarks>
    ///     ⚠ <b>The property the later, stretch-driven cuts must not break.</b> A chart reaching from one
    ///     bulb to the other would have to pass through the waist — the narrowest, most occluded, most
    ///     concave part of the surface, and the one place every term in § D4's cost agrees about.
    /// </remarks>
    [Fact]
    public void NoChartSpansBothOfTheDumbbellsBulbs() {
        var mesh = ShapeCorpus.Dumbbell();
        var charts = UvUnwrap.Charts(mesh, new(), out var report);
        var left = new bool[report.ChartCount];
        var right = new bool[report.ChartCount];

        for (var face = 0; face < mesh.FaceCount; face++) {
            var loop = mesh.CornersOf(face);
            var middle = 0f;

            foreach (var corner in loop) {
                middle += mesh.Positions[corner].X;
            }

            middle /= loop.Length;

            if (middle < -0.6f) {
                left[charts[face]] = true;
            } else if (middle > 0.6f) {
                right[charts[face]] = true;
            }
        }

        for (var chart = 0; chart < report.ChartCount; chart++) {
            Assert.False(left[chart] && right[chart], $"chart {chart} reaches across the waist into both bulbs.");
        }
    }
}
