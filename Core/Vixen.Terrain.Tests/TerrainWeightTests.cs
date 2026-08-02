// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Terrain;
using Xunit;

namespace Vixen.Terrain.Tests;

/// <summary>
///     Paint channels and the sum-to-one invariant — [docs/plan/31 § D5].
/// </summary>
public sealed class TerrainWeightTests {
    static TerrainDescription Shape() =>
        TerrainDescription.Default with { TileSamples = 8, TilesX = 2, TilesZ = 2, MetresPerQuad = 1f };

    static TerrainWeights Weights(params string[] names) {
        var weights = new TerrainWeights(Shape());

        foreach (var name in names) {
            weights.AddLayer(name);
        }

        return weights;
    }

    /// <summary>Walks every sample and demands the invariant, reporting the layer that broke it.</summary>
    static void AssertSumsToOne(TerrainWeights weights) => Assert.Null(weights.Verify());

    [Fact]
    public void TheFirstWeightBlendedLayerStartsAtFullCoverage() {
        // A terrain whose layers all start at zero has no valid weights anywhere — the invariant is
        // broken the moment the second layer exists. Every quick-start guide calls painting the base
        // layer over the whole terrain a troubleshooting step; this makes it unnecessary.
        var weights = Weights("Soil");

        Assert.Equal(TerrainWeights.Total, weights.WeightAt(0, 3, 3));
        AssertSumsToOne(weights);

        weights.AddLayer("Grass");
        Assert.Equal(0, weights.WeightAt(1, 3, 3));
        AssertSumsToOne(weights);
    }

    [Fact]
    public void PaintingALayerTakesFromTheOthersProportionally() {
        var weights = Weights("Soil", "Grass", "Rock");

        // Start from an even split, then paint Rock up.
        weights.SetWeight(1, 2, 2, 100);
        weights.SetWeight(2, 2, 2, 50);
        AssertSumsToOne(weights);

        var grassBefore = weights.WeightAt(1, 2, 2);
        var soilBefore = weights.WeightAt(0, 2, 2);

        weights.Paint(2, 2, 2, 60);

        AssertSumsToOne(weights);

        // Both gave up something, and roughly in proportion to what they held.
        Assert.True(weights.WeightAt(1, 2, 2) < grassBefore);
        Assert.True(weights.WeightAt(0, 2, 2) < soilBefore);
    }

    /// <summary>
    ///     Proportional, not uniform — otherwise a layer can become impossible to reach.
    /// </summary>
    /// <remarks>
    ///     Subtracting the same amount from every other layer drives the small ones to zero first and
    ///     then has nowhere left to take from, so the layer being painted stops being able to reach
    ///     full coverage. Painting one layer repeatedly must converge on it holding everything.
    /// </remarks>
    [Fact]
    public void PaintingOneLayerRepeatedlyReachesFullCoverage() {
        var weights = Weights("Soil", "Grass", "Rock", "Snow");

        weights.SetWeight(1, 2, 2, 80);
        weights.SetWeight(2, 2, 2, 60);
        weights.SetWeight(3, 2, 2, 40);

        for (var stroke = 0; stroke < 200; stroke++) {
            weights.Paint(0, 2, 2, 20);
        }

        Assert.Equal(TerrainWeights.Total, weights.WeightAt(0, 2, 2));
        Assert.Equal(0, weights.WeightAt(1, 2, 2));
        AssertSumsToOne(weights);
    }

    [Fact]
    public void TheInvariantSurvivesTenThousandRandomisedStrokes() {
        // The gate docs/plan/31 § Part 4 asks for. A weight-sum drift is a rounding bug that
        // presents as a barely-visible tint, so the only way to find it is to hammer the arithmetic.
        var weights = Weights("Soil", "Grass", "Rock", "Snow", "Sand");
        var random = new Random(20260802);

        for (var stroke = 0; stroke < 10_000; stroke++) {
            weights.Paint(
                random.Next(5),
                random.Next(Shape().SamplesX),
                random.Next(Shape().SamplesZ),
                random.Next(-120, 121)
            );
        }

        AssertSumsToOne(weights);
    }

    /// <summary>
    ///     The invariant checker actually catches a violation, and names where it is.
    /// </summary>
    /// <remarks>
    ///     A checker with no test is a checker nobody knows works. The public surface cannot produce
    ///     a broken state, so this reaches past it with <c>PokeRaw</c> — which is what a rounding bug
    ///     would do from the inside — and demands that the message carry the sample and the layer.
    ///     "The weights at (5, 6) sum to 254" is a fact nobody can act on; "and Grass holds 138 of
    ///     it" is a place to look.
    /// </remarks>
    [Fact]
    public void VerifyCatchesAViolationAndNamesTheSampleAndLayer() {
        var weights = Weights("Soil", "Grass", "Rock");
        AssertSumsToOne(weights);

        // Grass gains weight nobody gave up, so the sample sums to 393 instead of 255.
        weights.PokeRaw(1, 5, 6, 138);

        var message = weights.Verify();

        Assert.NotNull(message);
        Assert.Contains("(5, 6)", message, StringComparison.Ordinal);
        Assert.Contains("393", message, StringComparison.Ordinal);

        // And it names the layer holding the most there — Soil, at full coverage — which is the
        // documented rule: for a rounding drift every layer is a plausible culprit and the largest
        // holder is the one worth looking at first.
        Assert.Contains("Soil", message, StringComparison.Ordinal);
        Assert.Contains("255", message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyIsSilentWhenNothingIsWrong() {
        var weights = Weights("Soil", "Grass", "Rock");

        weights.SetWeight(1, 2, 2, 90);
        weights.Paint(2, 3, 3, 40);

        Assert.Null(weights.Verify());
    }

    [Fact]
    public void ANonWeightBlendedLayerTakesFromNobody() {
        // Snow over everything: it lies on top rather than replacing what is underneath.
        var weights = new TerrainWeights(Shape());
        weights.AddLayer("Soil");
        weights.AddLayer("Grass");
        var snow = weights.AddLayer("Snow", TerrainBlend.NonWeight);

        weights.SetWeight(1, 2, 2, 100);
        var soil = weights.WeightAt(0, 2, 2);
        var grass = weights.WeightAt(1, 2, 2);

        weights.Paint(snow, 2, 2, 200);

        Assert.Equal(200, weights.WeightAt(snow, 2, 2));
        Assert.Equal(soil, weights.WeightAt(0, 2, 2));
        Assert.Equal(grass, weights.WeightAt(1, 2, 2));
        AssertSumsToOne(weights);
    }

    [Fact]
    public void ANonWeightBlendedLayerIsClampedToTheTotal() {
        var weights = new TerrainWeights(Shape());
        weights.AddLayer("Soil");
        var snow = weights.AddLayer("Snow", TerrainBlend.NonWeight);

        weights.Paint(snow, 1, 1, 9_999);
        Assert.Equal(TerrainWeights.Total, weights.WeightAt(snow, 1, 1));

        weights.Paint(snow, 1, 1, -9_999);
        Assert.Equal(0, weights.WeightAt(snow, 1, 1));
    }

    [Fact]
    public void RemovingAWeightBlendedLayerGivesItsWeightBack() {
        // Otherwise every sample it covered drops below the total and the material reads a hole.
        var weights = Weights("Soil", "Grass", "Rock");

        weights.SetWeight(1, 3, 3, 150);
        AssertSumsToOne(weights);

        weights.RemoveLayer(1);

        Assert.Equal(2, weights.LayerCount);
        AssertSumsToOne(weights);
    }

    [Fact]
    public void RemovingTheLastWeightBlendedLayerLeavesNothingToVerify() {
        var weights = new TerrainWeights(Shape());
        weights.AddLayer("Soil");
        weights.AddLayer("Snow", TerrainBlend.NonWeight);

        weights.RemoveLayer(0);

        Assert.Equal(1, weights.LayerCount);
        Assert.Null(weights.Verify());
    }

    [Fact]
    public void TheMaterialPermutationIsQuantisedSoASeventhLayerCompilesNoNewShader() {
        var weights = new TerrainWeights(Shape());

        Assert.Equal(0, weights.MaterialLayerSlots);

        for (var layer = 1; layer <= 4; layer++) {
            weights.AddLayer($"L{layer}");
            Assert.Equal(4, weights.MaterialLayerSlots);
        }

        for (var layer = 5; layer <= 8; layer++) {
            weights.AddLayer($"L{layer}");
            Assert.Equal(8, weights.MaterialLayerSlots);
        }

        Assert.Equal(2, weights.WeightmapCount);
    }

    [Fact]
    public void PaintingOutsideTheTerrainDoesNothing() {
        var weights = Weights("Soil", "Grass");

        weights.Paint(0, -5, 3, 100);
        weights.Paint(0, 9_999, 3, 100);
        weights.Paint(99, 3, 3, 100);

        AssertSumsToOne(weights);
    }

    [Fact]
    public void PaintingIsTheInverseOfPaintingBackForASingleLayerPair() {
        var weights = Weights("Soil", "Grass");

        var before = (weights.WeightAt(0, 4, 4), weights.WeightAt(1, 4, 4));

        weights.Paint(1, 4, 4, 90);
        weights.Paint(1, 4, 4, -90);

        Assert.Equal(before, (weights.WeightAt(0, 4, 4), weights.WeightAt(1, 4, 4)));
        AssertSumsToOne(weights);
    }

    [Fact]
    public void TheSculptPaintKernelWritesThroughTheBrush() {
        var terrain = new Terrain(TerrainDescription.Default with {
            TileSamples = 32, TilesX = 2, TilesZ = 2, MetresPerQuad = 1f
        });

        terrain.Weights.AddLayer("Soil");
        var grass = terrain.Weights.AddLayer("Grass");

        var brush = TerrainBrush.Default with { Radius = 6f, Strength = 1f, Falloff = 0.5f };
        TerrainSculpt.Paint(terrain, grass, brush, new(new(16f, 16f)), TerrainWeights.Total);

        Assert.Equal(TerrainWeights.Total, terrain.Weights.WeightAt(grass, 16, 16));
        // Distance 4 with a radius of 6 and half falloff: inside the brush, outside the plateau,
        // which is at radius × (1 − falloff) = 3.
        Assert.True(terrain.Weights.WeightAt(grass, 20, 16) is > 0 and < TerrainWeights.Total);
        Assert.Equal(0, terrain.Weights.WeightAt(grass, 25, 16));

        Assert.Null(terrain.Weights.Verify());
    }
}
