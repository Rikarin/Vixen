// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Geometry.Uv.Flattening;
using Xunit;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>What is refused before a solve runs, and what it is told.</summary>
/// <remarks>
///     <para>
///         docs/plan/42 § D5. ⚠ <b>A chart that is not a disk has no injective map to the plane at
///         all</b>, so a flattener that produced coordinates for one produced a fold with extra steps.
///         The literature's usual answer is to run the solve anyway and let the flip count catch it,
///         which wastes the solve and reports the symptom rather than the cause: an annulus needs a
///         cut joining its two boundary loops, and "seventy triangles were flipped" does not say so.
///     </para>
///     <para>
///         The test is the Euler characteristic — <c>χ = 2 − 2g − b</c>, which is one exactly for a
///         disk — plus the pinch check that χ is blind to.
///     </para>
/// </remarks>
public class UvFlattenTopologyTests {
    [Fact]
    public void AnAnnulusIsRefusedAndNamedAsOne() {
        var mesh = ShapeCorpus.CylinderClosed();
        var detail = UvUnwrap.Detail(mesh, ShapeCorpus.OneChart(mesh), new(), null, 0);

        Assert.Empty(detail.Islands);

        var refusal = Assert.Single(detail.Refused);

        Assert.Equal(ChartRefusal.NotADisk, refusal.Reason);
        Assert.Contains("2 boundary loops", detail.Report.Warnings[0], StringComparison.Ordinal);
        Assert.Contains("Euler characteristic 0", detail.Report.Warnings[0], StringComparison.Ordinal);
    }

    /// <summary>One hole in a torus is one boundary loop and still not a disk, and χ is what says so.</summary>
    /// <remarks>
    ///     ⚠ <b>The shape that rules out counting boundary loops.</b> A closed torus with a single face
    ///     taken out has exactly one boundary loop — the hole's rim — so a check that counted loops and
    ///     stopped would pass it. It is genus one: <c>χ = 2 − 2g − b = −1</c>, and it needs two more
    ///     cuts before any injective map to the plane exists.
    /// </remarks>
    [Fact]
    public void AGenusOneChartWithOneBoundaryLoopIsStillRefused() {
        var mesh = ShapeCorpus.TorusClosed();
        var charts = ShapeCorpus.OneChart(mesh);

        charts[0] = -1;

        var detail = UvUnwrap.Detail(mesh, charts, new(), null, 0);

        Assert.Empty(detail.Islands);
        Assert.Equal(ChartRefusal.NotADisk, Assert.Single(detail.Refused).Reason);
        Assert.Contains("1 boundary loops", detail.Report.Warnings[0], StringComparison.Ordinal);
        Assert.Contains("Euler characteristic -1", detail.Report.Warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AClosedSurfaceIsRefusedForHavingNoBoundaryAtAll() {
        var mesh = ShapeCorpus.TorusClosed();
        var detail = UvUnwrap.Detail(mesh, ShapeCorpus.OneChart(mesh), new(), null, 0);

        Assert.Empty(detail.Islands);
        Assert.Equal(ChartRefusal.Closed, Assert.Single(detail.Refused).Reason);
        Assert.Contains("no boundary", detail.Report.Warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AChartInTwoPiecesIsRefused() {
        var mesh = ShapeCorpus.TwoIslands();
        var detail = UvUnwrap.Detail(mesh, ShapeCorpus.OneChart(mesh), new(), null, 0);

        Assert.Empty(detail.Islands);
        Assert.Equal(ChartRefusal.Disconnected, Assert.Single(detail.Refused).Reason);
    }

    /// <summary>Two triangles meeting at one vertex: Euler characteristic one, and not a disk.</summary>
    [Fact]
    public void ABowtieIsRefusedByTheCheckEulerCharacteristicCannotMake() {
        var mesh = ShapeCorpus.Bowtie();
        var detail = UvUnwrap.Detail(mesh, ShapeCorpus.OneChart(mesh), new(), null, 0);

        Assert.Empty(detail.Islands);
        Assert.Equal(ChartRefusal.NonManifoldVertex, Assert.Single(detail.Refused).Reason);
        Assert.Contains("pinches to a point", detail.Report.Warnings[0], StringComparison.Ordinal);
    }

    /// <summary>The reason is carried out, because the split that fixes each of these is a different one.</summary>
    [Fact]
    public void EveryRefusalCarriesItsOwnReasonRatherThanOneFlag() {
        var reasons = new List<ChartRefusal>();

        foreach (var mesh in new[] {
                     ShapeCorpus.CylinderClosed(),
                     ShapeCorpus.TorusClosed(),
                     ShapeCorpus.TwoIslands(),
                     ShapeCorpus.Bowtie()
                 }) {
            reasons.Add(
                UvUnwrap.Detail(mesh, ShapeCorpus.OneChart(mesh), new(), null, 0).Refused[0].Reason
            );
        }

        Assert.Equal(reasons.Count, reasons.Distinct().Count());
    }

    /// <summary>A refused chart takes nothing else down with it.</summary>
    [Fact]
    public void ARefusedChartLeavesTheRestOfTheMeshAlone() {
        var mesh = ShapeCorpus.CylinderClosed();
        var charts = ShapeCorpus.OneChart(mesh);

        // ⚠ An *interior* patch, not a whole column. A column would cut the tube open and leave a
        // rectangle, so both charts would flatten and the test would assert nothing. Two quads in the
        // middle leave an annulus with a hole in it, which is refused for the same reason plus one.
        charts[(5 * 12) + 5] = 1;
        charts[(5 * 12) + 6] = 1;

        var detail = UvUnwrap.Detail(mesh, charts, new(), null, 0);

        Assert.Single(detail.Islands);
        Assert.Equal([1], detail.ChartOfIsland);
        Assert.Equal(ChartRefusal.NotADisk, Assert.Single(detail.Refused).Reason);
        Assert.Equal(0, detail.Report.Distortion.Flipped);
    }
}
