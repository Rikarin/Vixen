// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Geometry.Uv.Flattening;
using Xunit;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>What happens on an obtuse triangle, which is the decision U2 had to make.</summary>
/// <remarks>
///     <para>
///         A cotangent goes negative on an obtuse angle, a Laplacian with a negative weight is not
///         guaranteed positive definite, and docs/plan/42 § B1's conjugate gradient is only valid on
///         one that is. ⚠ <b>The failure is silent and that is what makes it worth a test file.</b> CG
///         on an indefinite matrix does not throw and does not diverge — it converges to a saddle
///         point, and the chart comes back folded with nothing anywhere naming the obtuse triangle.
///     </para>
///     <para>
///         The decision — clamp to a small positive floor relative to the chart's largest weight, and
///         <i>count</i> — is argued in full on <see cref="CotangentWeights" />. These tests hold both
///         halves to account: that the input really does produce negative weights, and that the chart
///         comes out of the second rung without a fold.
///     </para>
/// </remarks>
public class UvFlattenCotangentTests {
    [Fact]
    public void TheObtuseGridReallyDoesProduceNegativeCotangents() {
        var mesh = ShapeCorpus.ObtuseGrid();
        var chart = ChartMesh.Extract(mesh, ShapeCorpus.OneChart(mesh))[0];
        var frames = new TriangleFrame[chart.TriangleCount];

        for (var triangle = 0; triangle < chart.TriangleCount; triangle++) {
            frames[triangle] = TriangleFrame.Build(
                chart.Positions[chart.Triangles[triangle * 3]],
                chart.Positions[chart.Triangles[(triangle * 3) + 1]],
                chart.Positions[chart.Triangles[(triangle * 3) + 2]]
            );
        }

        var weights = CotangentWeights.Build(chart, frames);

        // Two thirds of the corners of a sliver parallelogram's triangles are past a right angle, so
        // "some were clamped" is not a fixture that happens to trip it once.
        Assert.True(
            weights.Clamped > chart.TriangleCount / 2,
            $"only {weights.Clamped} of {chart.TriangleCount * 3} weights were negative, so this fixture "
            + "is not the obtuse input the clamp was written for."
        );

        // And after the clamp there is nothing left below the floor, which is what makes the matrix
        // positive definite once one vertex is anchored.
        Assert.All(weights.Edge, weight => Assert.True(weight > 0d, $"a weight of {weight} survived the floor"));
    }

    /// <summary>The second rung runs on that matrix and comes back injective.</summary>
    /// <remarks>
    ///     ⚠ <b>The threshold is forced to one so the ladder cannot skip the rung being tested.</b> The
    ///     obtuse grid is gentle enough that the conformal solve already passes the default bound, so a
    ///     test at the default settings would assert that a Laplacian nobody built produced no folds.
    /// </remarks>
    [Fact]
    public void TheSecondRungRunsOnAnObtuseChartWithoutFolding() {
        var mesh = ShapeCorpus.ObtuseGrid();

        var islands = UvUnwrap.Flatten(
            mesh,
            ShapeCorpus.OneChart(mesh),
            new() { DistortionThreshold = 1f },
            out var report
        );

        Assert.Single(islands);

        Assert.True(
            report.IsInjective,
            $"{report.Distortion.Flipped} of {islands[0].TriangleCount} triangles folded — which is what "
            + "an indefinite Laplacian looks like from the outside."
        );

        Assert.True(report.Distortion.StretchL2 < 1.3f, $"L² came out at {report.Distortion.StretchL2}.");
    }

    /// <summary>The clamp is reported rather than swallowed, because it is the upgrade's justification.</summary>
    [Fact]
    public void TheClampIsNamedInTheReport() {
        var mesh = ShapeCorpus.ObtuseGrid();

        UvUnwrap.Flatten(mesh, ShapeCorpus.OneChart(mesh), new(), out var report);

        Assert.Contains(
            report.Warnings,
            warning => warning.Contains("obtuse triangle corners", StringComparison.Ordinal)
                && warning.Contains("intrinsic Delaunay", StringComparison.Ordinal)
        );
    }

    /// <summary>A chart with no obtuse angle in it says nothing at all.</summary>
    [Fact]
    public void AWellShapedChartReportsNoClamp() {
        var mesh = ShapeCorpus.Strip();

        UvUnwrap.Flatten(mesh, ShapeCorpus.OneChart(mesh), new(), out var report);

        Assert.DoesNotContain(
            report.Warnings,
            warning => warning.Contains("obtuse triangle corners", StringComparison.Ordinal)
        );
    }

    /// <summary>The count is not the number to read, and an ordinary curved grid is why.</summary>
    /// <remarks>
    ///     ⚠ <b>An ordinary quad grid over a hemisphere produces one negative cotangent per quad, and so does
    ///     the sheared grid of 170° slivers.</b> Four points on a curved surface are not coplanar, so
    ///     the triangle the ear clipper cuts out of them has a corner a little past a right angle; that
    ///     is ordinary and harmless. A warning carrying only the count would fire on almost every mesh in the engine
    ///     and be ignored within a week, which is why <see cref="CotangentWeights.Worst" /> is the
    ///     number in the message.
    /// </remarks>
    [Fact]
    public void TheCountDoesNotSeparateAnOrdinaryMeshFromABadOneAndTheAngleDoes() {
        var ordinary = Weights(ShapeCorpus.Hemisphere());
        var obtuse = Weights(ShapeCorpus.ObtuseGrid());

        Assert.True(ordinary.Clamped > 0, "the hemisphere produced no negative weight at all, so this proves nothing");
        Assert.True(obtuse.Clamped > 0, "nor did the obtuse grid");

        // A cotangent of −1 is 135°, which is where a corner stops being a stretched square and starts
        // being a sliver. The two fixtures have to land on opposite sides of it.
        Assert.True(
            ordinary.Worst > -1d,
            $"the hemisphere's worst cotangent is {ordinary.Worst:0.####}, past 135°, so it is not the ordinary "
            + "mesh this half of the comparison needs."
        );

        Assert.True(
            obtuse.Worst < -3d,
            $"the obtuse grid's worst cotangent is only {obtuse.Worst:0.####}, so the two cases are not "
            + "being separated and the number in the warning says nothing."
        );
    }

    /// <summary>The weights carry no units, so a model in millimetres gives the same matrix as one in metres.</summary>
    /// <remarks>
    ///     ⚠ <b>A cotangent is a ratio of two lengths, so the whole second rung's matrix is
    ///     <i>identical</i> at any model scale — and at a power of two it is identical bit for bit.</b>
    ///     That is worth asserting rather than assuming: it is the property that makes
    ///     <c>DistortionThreshold</c> a statement about shape and not about size, and one expression
    ///     that mixed a length with a constant would quietly take it away.
    /// </remarks>
    [Theory]
    [InlineData(1f / 1024f, true)]
    [InlineData(1024f, true)]
    [InlineData(1e-3f, false)]
    [InlineData(1e+3f, false)]
    public void TheWeightsAreTheSameAtAnyModelScale(float scale, bool exact) {
        var reference = Weights(ShapeCorpus.ObtuseGrid());
        var scaled = Weights(Scaled(ShapeCorpus.ObtuseGrid(), scale));

        Assert.Equal(reference.Clamped, scaled.Clamped);
        Assert.Equal(reference.Edge.Length, scaled.Edge.Length);

        for (var index = 0; index < reference.Edge.Length; index++) {
            // A power of two moves an exponent and nothing else, so the two are the same double. A
            // scale that is not one rounds the positions themselves, so what survives is a *relative*
            // agreement — an absolute one here would be the mistake this whole test is about.
            if (exact) {
                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(reference.Edge[index]),
                    BitConverter.DoubleToInt64Bits(scaled.Edge[index])
                );

                continue;
            }

            var slip = Math.Abs(reference.Edge[index] - scaled.Edge[index]) / Math.Abs(reference.Edge[index]);

            Assert.True(
                slip < 1e-5d,
                $"weight {index} went from {reference.Edge[index]:R} to {scaled.Edge[index]:R} at {scale}×, "
                + $"which is {slip:0.###e+0} of itself."
            );
        }
    }

    static CotangentWeights Weights(EditMesh mesh) {
        var chart = ChartMesh.Extract(mesh, ShapeCorpus.OneChart(mesh))[0];
        var frames = new TriangleFrame[chart.TriangleCount];

        for (var triangle = 0; triangle < chart.TriangleCount; triangle++) {
            frames[triangle] = TriangleFrame.Build(
                chart.Positions[chart.Triangles[triangle * 3]],
                chart.Positions[chart.Triangles[(triangle * 3) + 1]],
                chart.Positions[chart.Triangles[(triangle * 3) + 2]]
            );
        }

        return CotangentWeights.Build(chart, frames);
    }

    static EditMesh Scaled(EditMesh mesh, float scale) {
        for (var position = 0; position < mesh.PositionCount; position++) {
            mesh.MovePosition(position, scale * mesh.Positions[position]);
        }

        return mesh;
    }
}
