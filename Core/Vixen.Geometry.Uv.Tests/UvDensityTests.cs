// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>docs/plan/42 § D9 and exit criterion 5, measured rather than claimed.</summary>
/// <remarks>
///     <para>
///         <b>"Uniform mode holds texel density within 2 % across every chart."</b> The measurement
///         below does not read <see cref="UvReport.TexelDensity" />; it goes back through
///         <see cref="UvPlacement.Apply" />, the island's own parameter area and the mesh's world area,
///         and works out how many texels land on a square unit of surface. ⚠ <b>Computing it two ways
///         is the point.</b> The report's figure is assembled from the factors the packer applied, so
///         in uniform mode it is <i>exactly</i> uniform by construction and its variance is
///         structurally zero — a field like that is a statement about the arithmetic and not a
///         measurement, until something independent agrees with it.
///     </para>
///     <para>
///         ⚠ <b>Measured, and the criterion is met: <c>0.0000 %</c> across every chart of every
///         fixture, against the 2 % it asks for.</b> The two computations agree to five decimal places
///         on every island.
///     </para>
///     <para>
///         ⚠ <b>And the shipped default is the mode § D9 forbids.</b> <see cref="PackSettings" /> leaves
///         <see cref="PackSettings.TexelDensity" /> at zero, which means <i>keep each island at the
///         scale the flattener gave it</i> — and a flattener's scales differ between charts by exactly
///         the area distortion of their maps. Measured on the same fixtures with the same packer:
///         <b>22.9 % on a hemisphere, 12.9 % on a saddle, 1.2 % on a slit sphere</b>. That is D9's
///         failure — a character's face at half the resolution of their boots — reachable by leaving a
///         setting alone. It is recorded here rather than fixed because the fix is a value judgement
///         about a public default; <see cref="UvDensity.Reference" /> is the number a caller who has no
///         opinion should pass.
///     </para>
/// </remarks>
public class UvDensityTests {
    /// <summary>docs/plan/42's exit criterion 5, over four shapes that flatten differently.</summary>
    [Theory]
    [InlineData("sphere-cut-open")]
    [InlineData("hemisphere")]
    [InlineData("saddle")]
    [InlineData("torus-slit")]
    public void UniformModeHoldsEveryChartWithinTwoPercent(string shape) {
        var (mesh, islands) = Unwrapped(shape);
        var settings = new PackSettings { Resolution = 1024, Margin = 4, TexelDensity = 128f };
        var placements = UvUnwrap.Pack(islands, settings, out var report);

        // ⚠ Two islands at least, or "across every chart" is a statement about one number.
        Assert.True(islands.Count >= 2, $"{shape} charted into {islands.Count} island(s).");

        var measured = UvDensity.Measure(islands, placements, settings.Resolution);
        var spread = UvDensity.Spread(measured);

        Assert.True(
            spread <= 0.02f,
            $"{shape}: the achieved density spreads by {spread:P4} across {islands.Count} charts, and "
            + "docs/plan/42's exit criterion 5 asks for 2 %."
        );

        // And the same number, measured off the mesh rather than off the packer's factors.
        var independent = Independent(mesh, islands, placements, settings.Resolution);

        for (var index = 0; index < islands.Count; index++) {
            Assert.Equal(independent[index], measured[index], 3);
        }

        Assert.Equal(report.TexelDensity.Minimum, measured.Min(), 3);
        Assert.Equal(report.TexelDensity.Maximum, measured.Max(), 3);
        Assert.Equal(0f, report.TexelDensity.Variance, 3);
    }

    /// <summary>Leaving the density at its default is the non-uniform mode, and by how much.</summary>
    /// <remarks>
    ///     ⚠ <b>A test that pins a finding rather than a property.</b> The assertion is that at least
    ///     one fixture blows the 2 % criterion when the density is left at zero — so that if somebody
    ///     changes the default to a positive number, or makes the packer normalize the scales itself,
    ///     this test fails and gets deleted rather than the finding being lost.
    /// </remarks>
    [Fact]
    public void TheDefaultDensityIsTheModeCriterionFiveRulesOut() {
        var worst = 0f;
        var offender = string.Empty;

        foreach (var shape in new[] { "sphere-cut-open", "hemisphere", "saddle", "torus-slit" }) {
            var (_, islands) = Unwrapped(shape);
            var settings = new PackSettings { Resolution = 1024, Margin = 4 };

            Assert.Equal(0f, settings.TexelDensity);

            var placements = UvUnwrap.Pack(islands, settings, out _);
            var spread = UvDensity.Spread(UvDensity.Measure(islands, placements, settings.Resolution));

            if (spread > worst) {
                worst = spread;
                offender = shape;
            }
        }

        Assert.True(
            worst > 0.02f,
            $"The worst default-density spread is {worst:P4} on {offender}, which is now inside the "
            + "criterion — so either the default changed or the packer normalizes, and this finding is stale."
        );
    }

    /// <summary>The reference density makes uniform mode reachable without knowing a number.</summary>
    [Theory]
    [InlineData("sphere-cut-open")]
    [InlineData("hemisphere")]
    [InlineData("saddle")]
    public void TheReferenceDensityPacksUniformly(string shape) {
        var (_, islands) = Unwrapped(shape);
        var reference = UvDensity.Reference(islands);

        Assert.True(reference > 0f, $"{shape}: the reference came out at {reference}.");

        var settings = new PackSettings { Resolution = 1024, Margin = 4, TexelDensity = reference };
        var placements = UvUnwrap.Pack(islands, settings, out _);

        Assert.Equal(0f, UvDensity.Spread(UvDensity.Measure(islands, placements, settings.Resolution)), 4);
    }

    /// <summary>A per-chart multiplier of two is twice the texels per world unit, and nothing else moves.</summary>
    /// <remarks>
    ///     ⚠ <b>The ratio and not the absolute, because the packer rescales everything to fit.</b> An
    ///     island asked for at twice its neighbours' density is still at twice theirs after a global
    ///     shrink, which is the property that makes a multiplier the right thing to expose and an
    ///     absolute per-chart density the wrong one.
    /// </remarks>
    [Fact]
    public void APerChartMultiplierIsARatioThatSurvivesARescale() {
        var (_, islands) = Unwrapped("sphere-cut-open");
        var multipliers = new float[islands.Count];

        for (var index = 0; index < multipliers.Length; index++) {
            multipliers[index] = index == 0 ? 2f : 1f;
        }

        var weighted = UvDensity.Weight(islands, multipliers);

        // The coordinates are untouched, which is docs/plan/42's exit criterion 7.
        for (var index = 0; index < islands.Count; index++) {
            Assert.Equal(islands[index].Coordinates, weighted[index].Coordinates);
            Assert.Equal(islands[index].Corners, weighted[index].Corners);
            Assert.Equal(islands[index].Minimum, weighted[index].Minimum);
            Assert.Equal(islands[index].Maximum, weighted[index].Maximum);
        }

        var settings = new PackSettings { Resolution = 1024, Margin = 4, TexelDensity = 128f };
        var placements = UvUnwrap.Pack(weighted, settings, out var report);

        // ⚠ Measured against the *original* scales. The weighting is expressed as an island claiming to
        // be larger in the world than it is, so measuring against the weighted list would divide the
        // claim back out and report every island at the same density — the one answer that makes a
        // working multiplier look like it did nothing.
        var measured = UvDensity.Measure(islands, placements, settings.Resolution);

        for (var index = 1; index < measured.Count; index++) {
            Assert.Equal(2f, measured[0] / measured[index], 3);
        }

        // And the report, which only ever sees what it was handed, calls the atlas uniform — which it
        // is, in the units it was given. A deliberate multiplier is not a rescale the packer did.
        Assert.Equal(0f, report.TexelDensity.Variance, 4);
    }

    /// <summary>A per-material override is the same thing said in texels rather than in ratios.</summary>
    [Fact]
    public void APerMaterialOverrideIsAMultiplierWithTheDivisionDoneForYou() {
        var (_, islands) = Unwrapped("hemisphere");
        var materials = new int[islands.Count];

        for (var index = 0; index < materials.Length; index++) {
            materials[index] = index == 0 ? 1 : 0;
        }

        // Material 0 takes the reference; material 1 wants four times it.
        var overridden = UvDensity.Override(islands, materials, [0f, 512f], 128f);
        var plain = UvDensity.Weight(islands, [.. Enumerable.Range(0, islands.Count).Select(index => index == 0 ? 4f : 1f)]);

        for (var index = 0; index < islands.Count; index++) {
            Assert.Equal(plain[index].Scale, overridden[index].Scale, 5);
        }

        var settings = new PackSettings { Resolution = 1024, Margin = 4, TexelDensity = 128f };
        var placements = UvUnwrap.Pack(overridden, settings, out _);
        var measured = UvDensity.Measure(islands, placements, settings.Resolution);

        for (var index = 1; index < measured.Count; index++) {
            Assert.Equal(4f, measured[0] / measured[index], 3);
        }
    }

    /// <summary>The reference and the spread refuse to invent a number out of nothing.</summary>
    /// <remarks>
    ///     ⚠ <b>The zero that means "off" against the zero that means "measured zero".</b> An island
    ///     with no usable scale cannot be brought to a texels-per-metre figure at all, and averaging it
    ///     in as a zero would report a density no island has. Both helpers skip it; a set that is
    ///     entirely unusable comes back as zero, which is the only honest answer to a question with no
    ///     data behind it.
    /// </remarks>
    [Fact]
    public void AnIslandWithNoScaleIsSkippedRatherThanCountedAsZero() {
        var usable = IslandCorpus.Square(0.25f, 4f);
        var unusable = IslandCorpus.Square(0.25f, 0f);

        Assert.Equal(4f, UvDensity.Reference([usable, unusable]), 5);
        Assert.Equal(0f, UvDensity.Reference([unusable]));
        Assert.Equal(0f, UvDensity.Spread([]));
        Assert.Equal(0f, UvDensity.Spread([0f, 0f]));

        // The unusable entry is skipped rather than dragging the mean to 1: the range is 2 − 1 over a
        // mean of 1.5, and counting the zero would have made it 2 over 1.
        Assert.Equal(2f / 3f, UvDensity.Spread([1f, 0f, 2f]), 5);
    }

    /// <summary>Density is measured before the margin, and the margin is what the efficiency pair is for.</summary>
    /// <remarks>
    ///     ⚠ <b>The two are different questions and only one of them is what a texture artist sees.</b>
    ///     A margin is empty space <i>between</i> islands: it costs atlas area, so it moves
    ///     <see cref="UvReport.EffectiveEfficiency" /> away from
    ///     <see cref="UvReport.PackingEfficiency" />, and it does not change how many texels land on a
    ///     square unit of surface. A density charged for its margin band would be a number no sampler
    ///     ever reads — and it would make a 12-texel margin look like a resolution drop.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(12)]
    public void TheMarginMovesTheEfficiencyAndNotTheDensity(int margin) {
        var (_, islands) = Unwrapped("saddle");
        var settings = new PackSettings { Resolution = 1024, Margin = margin, TexelDensity = 96f };
        var placements = UvUnwrap.Pack(islands, settings, out var report);
        var measured = UvDensity.Measure(islands, placements, settings.Resolution);

        // Whatever the margin, the islands were not rescaled, so every one of them is at the density
        // that was asked for.
        foreach (var density in measured) {
            Assert.Equal(96f, density, 2);
        }

        Assert.Equal(0f, UvDensity.Spread(measured), 4);

        if (margin > 0) {
            Assert.True(
                report.EffectiveEfficiency > report.PackingEfficiency,
                $"A {margin}-texel margin consumed no more of the atlas than the islands themselves."
            );
        } else {
            Assert.Equal(report.PackingEfficiency, report.EffectiveEfficiency, 4);
        }
    }

    /// <summary>What <c>Pack</c> alone honestly knows, and the one field that reads as a measurement.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>docs/plan/42 § D1 makes the three stages independent, so the packer cannot know a
    ///         chart's shape, its seam or its distortion</b> — and it leaves those fields at their
    ///         defaults, which the <c>Pack</c> overload's own documentation says.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The trap is <see cref="UvReport.IsInjective" />, which comes back <c>true</c> from a
    ///         stage that never measured injectivity.</b> It is <c>Distortion.Flipped == 0</c>, and
    ///         <c>Flipped</c> defaults to zero — so a caller who packs an artist's islands and asks "is
    ///         all of this usable" is told yes by arithmetic rather than by a check. Pinned here so the
    ///         behaviour cannot change silently, and named so that whoever needs the answer knows to run
    ///         <see cref="UvUnwrap.All" /> or the flattener for it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The same shape for <see cref="UvReport.Compactness" />.</b> Zero is a
    ///         <i>legitimate</i> compactness — an infinitely thin tendril — so the default is
    ///         indistinguishable from the worst possible measurement rather than from a missing one.
    ///     </para>
    /// </remarks>
    [Fact]
    public void PackAloneLeavesTheOtherTwoStagesFieldsAlone() {
        var islands = IslandCorpus.Trellis(64);

        UvUnwrap.Pack(islands, new() { Resolution = 512, Margin = 4, TexelDensity = 64f }, out var report);

        Assert.Equal(islands.Length, report.ChartCount);
        Assert.Equal(0f, report.Compactness);
        Assert.Equal(0f, report.Convexity);
        Assert.Equal(0f, report.SeamLength);
        Assert.Equal(0f, report.SeamLengthNormalized);
        Assert.Equal(0f, report.BoundaryJaggedness);
        Assert.Equal(default, report.Distortion);

        // ⚠ True without a measurement, which is the finding rather than the contract.
        Assert.True(report.IsInjective);

        // What it does know, and every one of these is a real measurement.
        Assert.True(report.PackingEfficiency > 0f);
        Assert.True(report.EffectiveEfficiency > report.PackingEfficiency);
        Assert.Equal(64f, report.TexelDensity.Mean, 2);
        Assert.Single(report.Stages);
        Assert.Equal(UvStage.Pack, report.Stages[0].Stage);
        Assert.Equal(islands.Length, report.Stages[0].Elements);
    }

    /// <summary>And <c>All</c> fills every one of them, which is the other half of the same claim.</summary>
    [Fact]
    public void AllFillsWhatPackAloneCannot() {
        var mesh = ShapeCorpus.Dumbbell(1f, 16, 20);
        var report = UvUnwrap.All(mesh, new(), new() { Resolution = 512, Margin = 4, TexelDensity = 64f }, out _);

        Assert.True(report.ChartCount > 2);
        Assert.True(report.Compactness > 0f);
        Assert.True(report.Convexity > 0f);
        Assert.True(report.SeamLength > 0f);
        Assert.True(report.SeamLengthNormalized > 0f);
        Assert.True(report.BoundaryJaggedness >= 0f);
        Assert.True(report.Distortion.StretchL2 >= 1f);
        Assert.True(report.PackingEfficiency > 0f);
        Assert.True(report.TexelDensity.Mean > 0f);

        // ⚠ Over the square root of the area rather than over the area, because the published
        // definition is not dimensionless — halve a model's scale and the paper's figure doubles.
        // The identity is asserted here rather than trusted: the surface area is recomputed from the
        // mesh, and the two seam figures have to agree through it.
        var area = 0d;

        for (var face = 0; face < mesh.FaceCount; face++) {
            var loop = mesh.CornersOf(face);

            for (var corner = 1; corner + 1 < loop.Length; corner++) {
                var a = mesh.Positions[loop[0]];
                var b = mesh.Positions[loop[corner]];
                var c = mesh.Positions[loop[corner + 1]];

                area += 0.5d * Vector3.Cross(b - a, c - a).Length();
            }
        }

        Assert.Equal(report.SeamLength / MathF.Sqrt((float)area), report.SeamLengthNormalized, 3);
    }

    /// <summary>Texels per world unit, measured off the mesh and the placement rather than off the packer.</summary>
    static float[] Independent(
        EditMesh mesh,
        IReadOnlyList<UvIsland> islands,
        IReadOnlyList<UvPlacement> placements,
        int resolution
    ) {
        var densities = new float[islands.Count];

        foreach (var placement in placements) {
            var island = islands[placement.Island];
            var surface = 0d;
            var atlas = 0d;

            for (var triangle = 0; triangle < island.TriangleCount; triangle++) {
                var a = mesh.Positions[mesh.Corners[island.Corners[(triangle * 3) + 0]]];
                var b = mesh.Positions[mesh.Corners[island.Corners[(triangle * 3) + 1]]];
                var c = mesh.Positions[mesh.Corners[island.Corners[(triangle * 3) + 2]]];

                surface += 0.5d * Vector3.Cross(b - a, c - a).Length();

                var ua = placement.Apply(island, island.Coordinates[(triangle * 3) + 0]) * resolution;
                var ub = placement.Apply(island, island.Coordinates[(triangle * 3) + 1]) * resolution;
                var uc = placement.Apply(island, island.Coordinates[(triangle * 3) + 2]) * resolution;

                atlas += 0.5d * Math.Abs((((double)ub.X - ua.X) * ((double)uc.Y - ua.Y))
                    - (((double)ub.Y - ua.Y) * ((double)uc.X - ua.X)));
            }

            densities[placement.Island] = surface > 0d ? (float)Math.Sqrt(atlas / surface) : 0f;
        }

        return densities;
    }

    static (EditMesh Mesh, IReadOnlyList<UvIsland> Islands) Unwrapped(string shape) {
        var mesh = FlattenFixtures.Build(shape);
        var settings = new UvSettings();

        return (mesh, UvUnwrap.Flatten(mesh, UvUnwrap.Charts(mesh, settings, out _), settings, out _));
    }
}
