// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>The fused verb, and the report it is the only call able to fill in.</summary>
/// <remarks>
///     <para>
///         docs/plan/42 § D1's fourth entry point. The other three exist because <b>conflating the
///         stages is the first mistake</b>; this one exists because the importer does not want to care.
///     </para>
///     <para>
///         ⚠ <b>Three of <see cref="UvReport" />'s fields can only be filled in here, and that is a fact
///         about the fields rather than about the plumbing.</b> Compactness, convexity and boundary
///         jaggedness are properties of an island's <i>outline</i>, which needs the flattened
///         coordinates and the mesh's corner layer at the same time — the charter has one and the packer
///         has the other.
///     </para>
/// </remarks>
public class UvAllTests {
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
            "dumbbell",
            "grouped-plate",
            "two-islands",
            "one-triangle"
        ];

    /// <summary>Every coordinate lands in the atlas, and every one of them is a number.</summary>
    [Theory]
    [MemberData(nameof(Corpus))]
    public void EveryCoordinateLandsInTheAtlas(string shape) {
        var mesh = ChartFixtures.Build(shape);
        var report = UvUnwrap.All(mesh, new(), new() { Resolution = 1024, Margin = 4 }, out var uvs);

        Assert.Equal(mesh.CornerCount, uvs.Count);
        Assert.True(report.IsInjective, $"{shape}: {string.Join(" ", report.Warnings)}");
        Assert.Equal(0, report.Distortion.Flipped);

        foreach (var uv in uvs) {
            Assert.False(float.IsNaN(uv.X) || float.IsNaN(uv.Y), $"{shape} produced a NaN coordinate.");
            Assert.InRange(uv.X, 0f, 1f);
            Assert.InRange(uv.Y, 0f, 1f);
        }
    }

    /// <summary>Every field docs/plan/42 § Part 4 names comes back with something in it.</summary>
    /// <remarks>
    ///     ⚠ <b>A report with a field silently at its default is worse than one without the field.</b>
    ///     Zero compactness is a legal reading — it is what a tendril measures — so a caller cannot tell
    ///     "this island is a spike" from "nobody computed this", and the second is the one that is a bug.
    /// </remarks>
    [Fact]
    public void EveryReportFieldIsFilledIn() {
        var mesh = ShapeCorpus.Dumbbell();
        var report = UvUnwrap.All(mesh, new(), new() { Resolution = 1024, Margin = 4 }, out _);

        Assert.True(report.ChartCount > 0);
        Assert.InRange(report.Compactness, 0f, 1.001f);
        Assert.InRange(report.Convexity, 0f, 1.001f);
        Assert.True(report.SeamLength > 0f);
        Assert.True(report.SeamLengthNormalized > 0f);
        Assert.True(report.BoundaryJaggedness >= 0f);
        Assert.True(report.Distortion.StretchL2 >= 1f);
        Assert.True(report.Distortion.Angular >= 1f);
        Assert.True(report.Distortion.Area >= 1f);
        Assert.True(report.PackingEfficiency > 0f);
        Assert.True(report.EffectiveEfficiency > 0f);
        Assert.True(report.TexelDensity.Mean > 0f);

        // All three stages timed, in the order they ran.
        Assert.Equal([UvStage.Chart, UvStage.Flatten, UvStage.Pack], report.Stages.Select(stage => stage.Stage));
    }

    /// <summary>A round island is compact and convex, and a smooth one is not jagged.</summary>
    /// <remarks>
    ///     ⚠ <b>The three shape figures need a shape whose answer is known, or they measure nothing.</b>
    ///     A flat rectangle maps to a rectangle: convexity is exactly one, jaggedness is exactly zero,
    ///     and compactness is <c>4πA/P²</c> of a 40:1 box, which is a number that can be written down
    ///     rather than accepted.
    /// </remarks>
    [Fact]
    public void AFlatRectangleMeasuresLikeARectangle() {
        var mesh = ShapeCorpus.Strip();
        var report = UvUnwrap.All(mesh, new(), new() { Resolution = 1024, Margin = 4 }, out _);

        Assert.Equal(1, report.ChartCount);
        Assert.Equal(1f, report.Convexity, 0.02f);
        Assert.Equal(0f, report.BoundaryJaggedness, 0.02f);

        // 4πA/P² for a w × h box is 4πwh / (2w + 2h)². The strip is 1 by 0.025.
        const float Expected = 4f * MathF.PI * 1f * 0.025f / (2.05f * 2.05f);

        Assert.Equal(Expected, report.Compactness, 0.02f);
    }

    /// <summary>The normalized seam length is scale-free and the raw one is not.</summary>
    /// <remarks>
    ///     <para>
    ///         docs/plan/42 § Part 1's correction to MeshTailor, which defines the figure over the
    ///         surface <i>area</i>: that is not dimensionless, so halving a model's scale doubles the
    ///         published number. ⚠ Both are reported, so the number compares across models and the
    ///         published one is still recoverable.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Measured at a power of two, because that is where the claim is exact.</b> Any other
    ///         factor rounds the mesh's own positions, so the two models are not quite proportional and
    ///         a chart sitting within a few units in the last place of τ can fall either side — see
    ///         <c>UvChartInvarianceTests</c>. A scale-freedom test run at 1e-3 would be measuring that
    ///         instead of measuring the normalization.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheNormalizedSeamLengthIsScaleFreeAndTheRawOneIsNot() {
        const float Down = 1f / 1024f;

        var packing = new PackSettings { Resolution = 512, Margin = 2 };
        var unit = UvUnwrap.All(ShapeCorpus.Dumbbell(), new(), packing, out _);
        var small = UvUnwrap.All(ShapeCorpus.Dumbbell(Down), new(), packing, out _);

        Assert.Equal(unit.SeamLength * Down, small.SeamLength, 6);
        Assert.Equal(unit.SeamLengthNormalized, small.SeamLengthNormalized, 4);

        // And the raw figure genuinely moved, so the equality above is a property rather than a
        // coincidence of two numbers that both happened to be small.
        Assert.NotEqual(unit.SeamLength, small.SeamLength);
    }

    /// <summary>A null argument is named, and the resolution is still required.</summary>
    [Fact]
    public void ANullArgumentIsNamed() {
        Assert.Throws<ArgumentNullException>(() => UvUnwrap.All(null!, new(), new() { Resolution = 64 }, out _));
        Assert.Throws<ArgumentNullException>(() => UvUnwrap.All(new(), null!, new() { Resolution = 64 }, out _));
        Assert.Throws<ArgumentNullException>(() => UvUnwrap.All(new(), new(), null!, out _));
    }

    /// <summary>An empty mesh unwraps to nothing rather than to an exception.</summary>
    [Fact]
    public void AnEmptyMeshUnwrapsToNothing() {
        var report = UvUnwrap.All(new(), new(), new() { Resolution = 64 }, out var uvs);

        Assert.Empty(uvs);
        Assert.Equal(0, report.ChartCount);
        Assert.True(report.IsInjective);
    }

    /// <summary>Charting, flattening and packing separately is the same atlas as asking for all three.</summary>
    /// <remarks>
    ///     ⚠ <b>docs/plan/42 § D1's actual claim, and it is easy to have a fused verb that quietly does
    ///     something else.</b> Every arrow in the pipeline is a public entry point over a value a caller
    ///     can hold and hand back — so the composition has to be the composition, or "just repack these
    ///     islands" is a different code path with different behaviour and the separability is a fiction.
    /// </remarks>
    [Fact]
    public void TheThreeStagesComposeIntoTheFusedVerb() {
        var mesh = ShapeCorpus.Dumbbell();
        var settings = new UvSettings();
        var packing = new PackSettings { Resolution = 1024, Margin = 4 };

        var charts = UvUnwrap.Charts(mesh, settings);
        var islands = UvUnwrap.Flatten(mesh, charts, settings);
        var placements = UvUnwrap.Pack(islands, packing);

        var stepwise = new Vector2[mesh.CornerCount];

        foreach (var placement in placements) {
            var island = islands[placement.Island];

            for (var slot = 0; slot < island.Corners.Count; slot++) {
                stepwise[island.Corners[slot]] = placement.Apply(island, island.Coordinates[slot]);
            }
        }

        UvUnwrap.All(mesh, settings, packing, out var fused);

        Assert.Equal(stepwise.Length, fused.Count);

        for (var corner = 0; corner < stepwise.Length; corner++) {
            Assert.True(
                BitConverter.SingleToInt32Bits(stepwise[corner].X) == BitConverter.SingleToInt32Bits(fused[corner].X)
                && BitConverter.SingleToInt32Bits(stepwise[corner].Y)
                == BitConverter.SingleToInt32Bits(fused[corner].Y),
                $"Corner {corner} is {fused[corner]} through All and {stepwise[corner]} through the three "
                + "stages called separately."
            );
        }
    }
}
