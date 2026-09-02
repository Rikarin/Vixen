// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Geometry.Uv.Charting;
using Xunit;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>The same surface gets the same seams, whatever units it arrived in and however it is numbered.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>An absolute tolerance is a claim about how big the model is, and this repository has
///         been bitten by one three times in a day.</b> <c>EditMesh.Normal</c> and
///         <c>ManifoldMesh.TriangleNormal</c> both delegated to <c>Vector3.Normalize</c>, which gives up
///         below an absolute <c>MathUtil.ZeroTolerance</c> of <c>1e-6</c> — and a cross product scales
///         as the <b>square</b> of the model, so the threshold is met at a completely different physical
///         size than it looks like it is. docs/plan/24 § P1's capsule poles were the third.
///     </para>
///     <para>
///         ⚠ <b>Charting is where that would hurt most, because its output is discrete.</b> A flattener
///         with a scale-dependent epsilon returns coordinates that are slightly wrong; a <i>charter</i>
///         with one returns a different number of charts, cut in different places — so the same asset
///         imported in millimetres and in metres is two different atlases, and nothing about the
///         difference is small.
///     </para>
///     <para>
///         <b>Which is why this asserts equality rather than closeness.</b> A chart index is an integer:
///         there is no tolerance to hide in, and the assertion is simply that the two runs agree.
///     </para>
/// </remarks>
public class UvChartInvarianceTests {
    public static TheoryData<string> Shapes =>
        [
            "sphere-cut-open",
            "hemisphere",
            "saddle",
            "torus-slit",
            "torus-closed",
            "cylinder-closed",
            "dumbbell",
            "sphere-nearly-closed",
            "strip"
        ];

    /// <summary>Occlusion itself survives a unit conversion, before chart thresholds can hide a change.</summary>
    /// <remarks>
    ///     Both an absolute determinant threshold in TriangleTree and an absolute ray-origin offset
    ///     in SeamGraph make the 1/1024 case fail. The other seam terms are disabled so neither can
    ///     hide the lost visibility contribution.
    /// </remarks>
    [Theory]
    [InlineData(1f / 1024f)]
    [InlineData(1024f)]
    public void VisibilityCostsScaleWithEveryEdge(float scale) {
        var settings = new UvSettings {
            SeamCost = new() {
                Concavity = 0f,
                Visibility = 1f,
                Feature = 0f,
                Material = 0f,
                Symmetry = 0f,
                Length = 0f,
                Existing = 0f
            }
        };

        var reference = SeamGraph.Build(ShapeCorpus.Dumbbell(), settings);
        var scaled = SeamGraph.Build(ShapeCorpus.Dumbbell(scale), settings);

        // Fully exposed edges pay their whole length. Require a partially occluded edge so that
        // rays which all miss, or a visibility term which is never evaluated, cannot pass vacuously.
        Assert.Contains(
            Enumerable.Range(0, reference.Cut.Length),
            edge => reference.Cut[edge] > 0d && reference.Cut[edge] < reference.EdgeLengths[edge]
        );

        Assert.Equal(reference.Cut.Length, scaled.Cut.Length);

        for (var edge = 0; edge < reference.Cut.Length; edge++) {
            Assert.Equal(reference.Cut[edge] * scale, scaled.Cut[edge]);
        }
    }

    /// <summary>A power of two scales the model exactly, so it must not move a single chart.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the test that catches an absolute epsilon, and it is exact because it can
    ///         be.</b> Scaling a <see langword="float" /> by a power of two changes only its exponent, and
    ///         every expression in the seam cost is homogeneous in the mesh's units — the six quality
    ///         terms are dimensionless and the seventh is a length — so every comparison the charter
    ///         makes is between two quantities that picked up the same exact factor. Nothing but a
    ///         constant with units in it can make this fail.
    ///     </para>
    ///     <para>
    ///         <b>And one did.</b> Five of these nine shapes charted differently at 1/1024 when
    ///         <c>TriangleTree.Raycast</c> tested its Möller–Trumbore determinant against an absolute
    ///         <c>MathUtil.ZeroTolerance</c>. The determinant scales as the <b>square</b> of the model,
    ///         so occlusion rays missed and the visibility term read zero. The tree now uses a relative
    ///         test, and <c>SeamGraph</c> casts in the mesh's original space; this test guards that fix
    ///         without the normalized copy that used to hide the tree's scale dependence.
    ///     </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Shapes))]
    public void APowerOfTwoDoesNotMoveASingleChart(string shape) {
        var reference = UvUnwrap.Charts(ChartFixtures.Build(shape, 1f), new(), out var unit);

        foreach (var scale in new[] { 1f / 1024f, 1024f }) {
            var scaled = UvUnwrap.Charts(ChartFixtures.Build(shape, scale), new(), out var moved);

            Assert.Equal(reference, scaled);

            Assert.True(
                BitConverter.SingleToInt32Bits(unit.SeamLength * scale)
                == BitConverter.SingleToInt32Bits(moved.SeamLength),
                $"{shape} at {scale}×: a seam of {unit.SeamLength} scaled to {moved.SeamLength} where "
                + $"{unit.SeamLength * scale} was expected."
            );

            Assert.Equal(unit.SeamLengthNormalized, moved.SeamLengthNormalized, 4);
        }
    }

    /// <summary>A thousandth and a thousand times charts to within one chart of the same answer.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Within one chart rather than identically, and the reason is a threshold rather than
    ///         an epsilon.</b> A thousand is not a power of two, so <c>position × 1e-3f</c> is the unit
    ///         mesh's position <i>rounded</i> — the two meshes are not exactly proportional, and every
    ///         measurement taken of them differs in the last bits. § D3's recursion then asks
    ///         <c>StretchL2 ≤ τ</c>, which is a single floating-point comparison: a chart whose measured
    ///         stretch sits within a few units in the last place of τ genuinely falls either side, and
    ///         the partition below it is different from there down.
    ///     </para>
    ///     <para>
    ///         <b>Measured, so the claim is a fact rather than an allowance.</b> Four of these nine
    ///         shapes are bit-identical at 1e±3 anyway; the other five agree on chart count to within one
    ///         while assigning up to three quarters of their faces differently — which is exactly what a
    ///         single flipped decision high in a recursion looks like, and is not what a scale-dependent
    ///         <i>cost</i> looks like. The power-of-two case above is the one that would catch that, and
    ///         it is exact.
    ///     </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Shapes))]
    public void AThousandthAndAThousandTimesChartsToWithinOneChart(string shape) {
        var reference = ChartFixtures.Build(shape, 1f);
        var smaller = ChartFixtures.Build(shape, 1e-3f);
        var larger = ChartFixtures.Build(shape, 1e+3f);

        UvUnwrap.Charts(reference, new(), out var unit);

        var down = UvUnwrap.Charts(smaller, new(), out var small);
        var up = UvUnwrap.Charts(larger, new(), out var large);

        Assert.InRange(small.ChartCount, unit.ChartCount - 1, unit.ChartCount + 1);
        Assert.InRange(large.ChartCount, unit.ChartCount - 1, unit.ChartCount + 1);

        // ⚠ And whichever side of the threshold it landed, the answer is still an atlas: every chart a
        // disk the flattener takes, no fold anywhere. A charter that came apart at an unusual model
        // scale would show it here rather than in a count.
        Assert.Equal(small.ChartCount, UvUnwrap.Flatten(smaller, down, new(), out var flatDown).Count);
        Assert.Equal(large.ChartCount, UvUnwrap.Flatten(larger, up, new(), out var flatUp).Count);
        Assert.Equal(0, flatDown.Distortion.Flipped);
        Assert.Equal(0, flatUp.Distortion.Flipped);
    }

    /// <summary>Renumbering the mesh's positions does not move a single seam.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A vertex numbering is not a property of the surface, and a thread sweep does not
    ///         catch this.</b> An importer, a weld and a boolean all renumber a mesh routinely, so a
    ///         charter whose seeds or tie-breaks read an index produces a different atlas for the same
    ///         asset on the next import — and every determinism test that varies only the worker count
    ///         passes throughout.
    ///     </para>
    ///     <para>
    ///         This is why <c>Bisection</c> seeds on the lexicographically smallest <i>centroid</i> and
    ///         breaks every tie on a centroid before it falls back to an index, which is the
    ///         strengthening <c>Pins.Choose</c> made for the same reason one layer down.
    ///     </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Shapes))]
    public void RenumberingTheMeshDoesNotMoveASeam(string shape) {
        var mesh = ChartFixtures.Build(shape);
        var moved = ShapeCorpus.Renumber(mesh, 0x51F3u);

        Assert.NotEqual(
            Enumerable.Range(0, mesh.PositionCount).Select(index => mesh.Positions[index]).ToArray(),
            Enumerable.Range(0, moved.PositionCount).Select(index => moved.Positions[index]).ToArray()
        );

        var reference = UvUnwrap.Charts(mesh, new(), out var before);
        var renumbered = UvUnwrap.Charts(moved, new(), out var after);

        Assert.Equal(reference, renumbered);
        Assert.Equal(before.ChartCount, after.ChartCount);

        Assert.True(
            BitConverter.SingleToInt32Bits(before.SeamLength) == BitConverter.SingleToInt32Bits(after.SeamLength),
            $"{shape}: the seam went from {before.SeamLength} to {after.SeamLength} on a renumbering."
        );
    }
}
