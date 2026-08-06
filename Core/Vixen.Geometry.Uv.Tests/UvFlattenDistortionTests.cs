// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Xunit;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>The measured baseline the next phase has to move.</summary>
/// <remarks>
///     <para>
///         docs/plan/42's first exit criterion is a comparison against xatlas on a 500-mesh corpus and
///         it cannot be run here. ⚠ <b>What can be run is a number that a regression moves</b>, on
///         shapes chosen so that each one fails differently — and the numbers are in the failure
///         messages as well as in the bounds, so a run that trips one says what it measured rather
///         than that a bound was exceeded.
///     </para>
///     <para>
///         ⚠ <b>The bounds are loose on purpose and the numbers are not.</b> A bound tight enough to
///         pin the current output would fail on the first legitimate improvement; these are set where
///         a <i>different algorithm</i> would land, which is what the next phase is going to be.
///     </para>
/// </remarks>
public class UvFlattenDistortionTests {
    /// <summary>Shape, and the ceilings for angular, area, L² and L^∞ in that order.</summary>
    public static TheoryData<string, float, float, float, float> Bounds =>
        new() {
            { "cylinder-slit", 1.001f, 1.001f, 1.001f, 1.01f },
            { "strip", 1.001f, 1.001f, 1.001f, 1.001f },
            { "obtuse-grid", 1.60f, 1.60f, 1.30f, 3.00f },
            { "saddle", 1.25f, 1.30f, 1.10f, 1.60f },
            { "hemisphere", 1.30f, 1.40f, 1.15f, 1.60f },
            { "torus-slit", 1.30f, 2.20f, 1.30f, 2.60f },
            { "sphere-cut-open", 2.60f, 6.00f, 2.20f, 6.00f }
        };

    [Theory]
    [MemberData(nameof(Bounds))]
    public void EachShapeStaysUnderItsMeasuredBaseline(string shape, float angular, float area, float l2, float lInf) {
        var mesh = FlattenFixtures.Build(shape);

        UvUnwrap.Flatten(mesh, ShapeCorpus.OneChart(mesh), new(), out var report);

        var measured = Line(shape, report.Distortion);

        Assert.True(report.Distortion.Flipped == 0, measured);
        Assert.True(report.Distortion.Angular <= angular, $"{measured} — angular over {angular}");
        Assert.True(report.Distortion.Area <= area, $"{measured} — area over {area}");
        Assert.True(report.Distortion.StretchL2 <= l2, $"{measured} — L² over {l2}");
        Assert.True(report.Distortion.StretchLInf <= lInf, $"{measured} — L^∞ over {lInf}");

        // ⚠ Every measure is normalized so that one is a perfectly isometric map. A figure *below* one
        // is the measure being wrong rather than the map being unusually good, and that is a class of
        // bug a ceiling alone never catches.
        Assert.True(report.Distortion.Angular >= 0.999f, $"{measured} — angular under one");
        Assert.True(report.Distortion.Area >= 0.999f, $"{measured} — area under one");
        Assert.True(report.Distortion.StretchL2 >= 0.999f, $"{measured} — L² under one");
        Assert.True(report.Distortion.StretchLInf >= 0.999f, $"{measured} — L^∞ under one");
    }

    /// <summary>The ladder does not pay for the second rung when the first one already passed.</summary>
    /// <remarks>
    ///     docs/plan/42 § D5: <i>"each one only paid for when the one below fails its bound"</i>. A
    ///     developable chart is what LSCM is already exact on, so raising the bound past what it
    ///     achieves has to change nothing at all about the answer — which is only true if the second
    ///     rung really was skipped.
    /// </remarks>
    [Fact]
    public void ADevelopableChartStopsAtTheFirstRung() {
        var mesh = ShapeCorpus.CylinderSlit();
        var tight = UvUnwrap.Flatten(mesh, ShapeCorpus.OneChart(mesh), new() { DistortionThreshold = 1.0001f });
        var loose = UvUnwrap.Flatten(mesh, ShapeCorpus.OneChart(mesh), new() { DistortionThreshold = 4f });

        Assert.Equal(loose[0].Coordinates, tight[0].Coordinates);
    }

    /// <summary>And it does pay for it when the first one did not.</summary>
    [Fact]
    public void ACurvedChartTakesTheSecondRungAndIsBetterForIt() {
        var mesh = ShapeCorpus.SphereCutOpen();

        UvUnwrap.Flatten(mesh, ShapeCorpus.OneChart(mesh), new() { FlattenIterations = 0 }, out var conformal);
        UvUnwrap.Flatten(mesh, ShapeCorpus.OneChart(mesh), new(), out var rigid);

        // ⚠ Area, not angles. A conformal map is *better* at angles by construction — that is what it
        // optimizes — and the whole of docs/plan/42 § D6 is the observation that saying so hides a
        // fortyfold area ratio between two ends of the same chart.
        Assert.True(
            rigid.Distortion.Area < conformal.Distortion.Area,
            $"LSCM alone measured area {conformal.Distortion.Area:0.####} and the local–global loop "
            + $"measured {rigid.Distortion.Area:0.####}, so the second rung bought nothing."
        );

        Assert.True(rigid.Distortion.StretchL2 < conformal.Distortion.StretchL2, Line("sphere ARAP", rigid.Distortion));
    }

    /// <summary>Every figure in one place, both rungs, which is what the phase hands the next one.</summary>
    /// <remarks>
    ///     ⚠ <b>An assertion rather than console output.</b> A test that printed would be a test whose
    ///     numbers nobody reads until they have already moved; this one fails and prints the whole
    ///     table the moment any shape folds, which is the only time anyone wants it. Both rungs are in
    ///     the table because the pair is the argument for having two: LSCM wins on angles by
    ///     construction and loses on area, and one column alone would say the wrong thing about both.
    /// </remarks>
    [Fact]
    public void TheWholeCorpusIsInjectiveAtEveryRung() {
        var lines = new List<string>();
        var folded = 0;

        foreach (var shape in FlattenFixtures.Corpus) {
            var mesh = FlattenFixtures.Build(shape);
            var charts = ShapeCorpus.OneChart(mesh);

            UvUnwrap.Flatten(mesh, charts, new() { FlattenIterations = 0 }, out var conformal);
            UvUnwrap.Flatten(mesh, charts, new(), out var rigid);

            lines.Add(Line($"{shape} LSCM", conformal.Distortion));
            lines.Add(Line($"{shape} ARAP", rigid.Distortion));
            folded += conformal.Distortion.Flipped + rigid.Distortion.Flipped;
        }

        Assert.True(folded == 0, string.Join(Environment.NewLine, lines));
    }

    static string Line(string shape, UvDistortion distortion) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{shape,-22} angular {distortion.Angular,8:0.0000}  area {distortion.Area,8:0.0000}  "
            + $"L² {distortion.StretchL2,8:0.0000}  L^∞ {distortion.StretchLInf,8:0.0000}  "
            + $"flipped {distortion.Flipped}"
        );
}
