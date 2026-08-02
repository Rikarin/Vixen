// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Vixen.Core.Mathematics;
using Vixen.Terrain;
using Xunit;

namespace Vixen.Terrain.Tests;

/// <summary>
///     The brush — [docs/plan/31 § B7] and [§ D12].
/// </summary>
/// <remarks>
///     Almost all of this is a property rather than an example, because the claims a brush makes are
///     about every radius and every strength: full in the middle, nothing outside, never rising in
///     between. Three hand-picked radii would satisfy a brush that is hardest at its edge.
/// </remarks>
public sealed class TerrainBrushTests {
    sealed class ConstantMask(float value) : IBrushMask {
        public float Sample(Vector2 uv) => value;
    }

    sealed class RecordingMask : IBrushMask {
        public List<Vector2> Samples { get; } = [];

        public float Sample(Vector2 uv) {
            Samples.Add(uv);
            return 1f;
        }
    }

    static TerrainBrush Brush(float radius = 4f, float strength = 1f, float falloff = 0.5f) =>
        TerrainBrush.Default with { Radius = radius, Strength = strength, Falloff = falloff };

    // --- The falloff curves -------------------------------------------------

    [Theory]
    [InlineData(BrushFalloffKind.Smooth)]
    [InlineData(BrushFalloffKind.Linear)]
    [InlineData(BrushFalloffKind.Spherical)]
    [InlineData(BrushFalloffKind.Tip)]
    public void EveryFalloffStartsAtOneEndsAtZeroAndNeverRises(BrushFalloffKind kind) {
        Assert.Equal(1f, BrushFalloff.Evaluate(kind, 0f), 5);
        Assert.Equal(0f, BrushFalloff.Evaluate(kind, 1f), 5);

        var previous = 1f;

        for (var step = 0; step <= 200; step++) {
            var value = BrushFalloff.Evaluate(kind, step / 200f);

            Assert.InRange(value, 0f, 1f);
            Assert.True(value <= previous + 1e-5f, $"{kind} rose at t = {step / 200f}.");
            previous = value;
        }
    }

    [Theory]
    [InlineData(BrushFalloffKind.Smooth)]
    [InlineData(BrushFalloffKind.Linear)]
    [InlineData(BrushFalloffKind.Spherical)]
    [InlineData(BrushFalloffKind.Tip)]
    public void AFalloffIsClampedRatherThanExtrapolated(BrushFalloffKind kind) {
        Assert.Equal(1f, BrushFalloff.Evaluate(kind, -5f), 5);
        Assert.Equal(0f, BrushFalloff.Evaluate(kind, 5f), 5);

        // NaN reads as "off the end" rather than propagating into a heightfield, where one NaN
        // sample spreads to every neighbour the next time anything smooths.
        Assert.Equal(0f, BrushFalloff.Evaluate(kind, float.NaN), 5);
    }

    /// <summary>Spherical is a dome and tip is a point, so they bracket the straight line.</summary>
    /// <remarks>
    ///     What stops the four being the same curve with different names. Asserted at the midpoint,
    ///     where a linear curve is exactly a half and the other two are as far from it as they get.
    /// </remarks>
    [Fact]
    public void TheFourCurvesAreActuallyDifferentShapes() {
        var linear = BrushFalloff.Evaluate(BrushFalloffKind.Linear, 0.5f);
        var spherical = BrushFalloff.Evaluate(BrushFalloffKind.Spherical, 0.5f);
        var tip = BrushFalloff.Evaluate(BrushFalloffKind.Tip, 0.5f);

        Assert.Equal(0.5f, linear, 5);
        Assert.True(spherical > linear, "Spherical bulges outwards.");
        Assert.True(tip < linear, "Tip pinches inwards.");
        Assert.Equal(1f, spherical + tip, 5);
    }

    // --- The brush ----------------------------------------------------------

    [Fact]
    public void TheCentreIsFullStrengthAndOutsideTheRadiusIsNothing() {
        var brush = Brush(radius: 4f, strength: 0.75f);
        var stamp = new BrushStamp(new(10f, 20f));

        Assert.Equal(0.75f, brush.WeightAt(new(10f, 20f), stamp), 5);
        Assert.Equal(0f, brush.WeightAt(new(14f, 20f), stamp), 5);
        Assert.Equal(0f, brush.WeightAt(new(100f, 20f), stamp), 5);
    }

    [Fact]
    public void ThePlateauIsFlatAndFalloffMeasuresTheBandNotThePlateau() {
        // Falloff 0.25 means the outer quarter of the radius falls off, so everything inside three
        // quarters of it is full strength. Reading the setting the other way round would make this
        // the softest part of the brush.
        var brush = Brush(radius: 4f, falloff: 0.25f);
        var stamp = new BrushStamp(Vector2.Zero);

        Assert.Equal(1f, brush.WeightAt(new(0f, 0f), stamp), 5);
        Assert.Equal(1f, brush.WeightAt(new(2.9f, 0f), stamp), 5);
        Assert.True(brush.WeightAt(new(3.5f, 0f), stamp) < 1f);
    }

    [Fact]
    public void AZeroFalloffIsAHardDiscRatherThanADivisionByZero() {
        var brush = Brush(falloff: 0f);
        var stamp = new BrushStamp(Vector2.Zero);

        Assert.Equal(1f, brush.WeightAt(new(3.99f, 0f), stamp), 5);
        Assert.Equal(0f, brush.WeightAt(new(4f, 0f), stamp), 5);
    }

    [Fact]
    public void AFullFalloffFallsOffFromTheCentre() {
        var brush = Brush(falloff: 1f, strength: 1f);
        var stamp = new BrushStamp(Vector2.Zero);

        Assert.Equal(1f, brush.WeightAt(Vector2.Zero, stamp), 5);
        Assert.True(brush.WeightAt(new(0.5f, 0f), stamp) < 1f);
    }

    [Fact]
    public void ACircleDoesNotCareWhichWayItIsTurned() {
        var brush = Brush();
        var sample = new Vector2(1.5f, 0.7f);

        var straight = brush.WeightAt(sample, new(Vector2.Zero));
        var turned = brush.WeightAt(sample, new(Vector2.Zero, 1.1f));

        Assert.Equal(straight, turned, 5);
    }

    [Fact]
    public void ACircleIsRotationallySymmetric() {
        var brush = Brush();
        var expected = brush.WeightAt(new(2f, 0f), new(Vector2.Zero));

        for (var step = 0; step < 16; step++) {
            var angle = step / 16f * MathF.Tau;
            var sample = new Vector2(2f * MathF.Cos(angle), 2f * MathF.Sin(angle));

            Assert.Equal(expected, brush.WeightAt(sample, new(Vector2.Zero)), 5);
        }
    }

    [Fact]
    public void FlowScalesAStampWithoutChangingItsShape() {
        var brush = Brush(strength: 1f);
        var full = brush.WeightAt(new(2f, 0f), new(Vector2.Zero));
        var half = brush.WeightAt(new(2f, 0f), new(Vector2.Zero, 0f, 0.5f));

        Assert.Equal(full * 0.5f, half, 5);
    }

    [Fact]
    public void ANonPositiveRadiusPaintsNothingRatherThanEverything() {
        var brush = Brush(radius: 0f);
        Assert.Equal(0f, brush.WeightAt(Vector2.Zero, new(Vector2.Zero)), 5);
    }

    // --- Masks --------------------------------------------------------------

    [Fact]
    public void AMaskedBrushWithNoMaskPaintsAsACircleRatherThanThrowing() {
        var alpha = TerrainBrush.Default with { Radius = 4f, Strength = 1f, Shape = BrushShape.Alpha };
        var circle = alpha with { Shape = BrushShape.Circle };

        Assert.Equal(
            circle.WeightAt(new(2f, 0f), new(Vector2.Zero)),
            alpha.WeightAt(new(2f, 0f), new(Vector2.Zero)),
            5
        );
    }

    [Fact]
    public void AnAlphaStampReadsItsMaskOverTheStampsOwnSquare() {
        var brush = TerrainBrush.Default with {
            Radius = 4f, Strength = 1f, Falloff = 0f, Shape = BrushShape.Alpha
        };

        var mask = new RecordingMask();

        brush.WeightAt(new(10f, 20f), new(new(10f, 20f)), mask);
        Assert.Equal(new(0.5f, 0.5f), mask.Samples[0]);

        mask.Samples.Clear();
        brush.WeightAt(new(14f, 20f - 4f), new(new(10f, 20f)), mask);

        // The corner of the stamp's square, which a disc would never have reached — an alpha's
        // footprint is the square and not the inscribed circle.
        Assert.Empty(mask.Samples);

        mask.Samples.Clear();
        brush.WeightAt(new(12f, 22f), new(new(10f, 20f)), mask);
        Assert.Equal(new(0.75f, 0.75f), mask.Samples[0]);
    }

    [Fact]
    public void APatternStampReadsItsMaskInWorldSpaceSoAStrokeRevealsOneTexture() {
        var brush = TerrainBrush.Default with {
            Radius = 4f, Strength = 1f, Falloff = 0f, Shape = BrushShape.Pattern, PatternScale = 4f
        };

        var mask = new RecordingMask();

        // The same world point read by two stamps in different places must land on the same texel.
        brush.WeightAt(new(6f, 2f), new(new(5f, 2f)), mask);
        brush.WeightAt(new(6f, 2f), new(new(7f, 2f)), mask);

        Assert.Equal(2, mask.Samples.Count);
        Assert.Equal(mask.Samples[0], mask.Samples[1]);
        Assert.Equal(new(0.5f, 0.5f), mask.Samples[0]);
    }

    [Fact]
    public void APatternWrapsRatherThanRunningOffTheUnitSquare() {
        var brush = TerrainBrush.Default with {
            Radius = 100f, Strength = 1f, Falloff = 0f, Shape = BrushShape.Pattern, PatternScale = 4f
        };

        var mask = new RecordingMask();

        foreach (var x in new[] { -9f, -1f, 0f, 1f, 9f, 17f }) {
            brush.WeightAt(new(x, 0f), new(Vector2.Zero), mask);
        }

        foreach (var uv in mask.Samples) {
            Assert.InRange(uv.X, 0f, 1f);
            Assert.InRange(uv.Y, 0f, 1f);
        }
    }

    [Fact]
    public void AMaskMultipliesTheFalloffRatherThanReplacingIt() {
        var brush = TerrainBrush.Default with {
            Radius = 4f, Strength = 1f, Falloff = 0.5f, Shape = BrushShape.Alpha
        };

        var circle = brush with { Shape = BrushShape.Circle };
        var half = new ConstantMask(0.5f);

        Assert.Equal(
            circle.WeightAt(new(3f, 0f), new(Vector2.Zero)) * 0.5f,
            brush.WeightAt(new(3f, 0f), new(Vector2.Zero), half),
            5
        );
    }

    // --- Footprints ---------------------------------------------------------

    [Fact]
    public void ACirclesFootprintIsItsRadiusAndAMasksIsItsDiagonal() {
        var stamp = new BrushStamp(new(10f, 20f));

        var circle = Brush(radius: 4f).FootprintOf(stamp);
        Assert.Equal(new(6f, 16f), circle.Minimum);
        Assert.Equal(new(14f, 24f), circle.Maximum);

        // A square stamp turned 45° reaches √2 radii into its corners, and the bound is the same
        // whichever way it is turned rather than tight for one angle and wrong for the next.
        var alpha = (Brush(radius: 4f) with { Shape = BrushShape.Alpha }).FootprintOf(stamp);
        Assert.Equal(4f * MathF.Sqrt(2f), alpha.Maximum.X - 10f, 4);
    }

    [Fact]
    public void AFootprintContainsEverythingTheStampCanTouch() {
        var brush = Brush(radius: 3f);
        var stamp = new BrushStamp(new(2f, -5f));
        var footprint = brush.FootprintOf(stamp);

        for (var step = 0; step < 64; step++) {
            var angle = step / 64f * MathF.Tau;
            var sample = stamp.Centre + (new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 2.99f);

            Assert.True(brush.WeightAt(sample, stamp) > 0f);
            Assert.True(footprint.Contains(sample));
        }
    }

    // --- Properties ---------------------------------------------------------

    /// <summary>
    ///     For every radius, strength and curve: full in the middle, nothing outside, never rising.
    /// </summary>
    [Fact]
    public void AnyBrushIsMonotonicFromItsCentreToItsEdge() {
        Gen.Select(
                Gen.Float[0.01f, 200f],
                Gen.Float[0f, 1f],
                Gen.Float[0f, 1f],
                Gen.Int[0, 3]
            )
            .Sample(
                input => {
                    var (radius, strength, falloff, curve) = input;

                    var brush = TerrainBrush.Default with {
                        Radius = radius,
                        Strength = strength,
                        Falloff = falloff,
                        Curve = (BrushFalloffKind)curve
                    };

                    var stamp = new BrushStamp(Vector2.Zero);

                    Assert.Equal(strength, brush.WeightAt(Vector2.Zero, stamp), 4);
                    Assert.Equal(0f, brush.WeightAt(new(radius, 0f), stamp), 5);

                    var previous = float.PositiveInfinity;

                    for (var step = 0; step <= 64; step++) {
                        var value = brush.WeightAt(new(radius * step / 64f, 0f), stamp);

                        Assert.InRange(value, 0f, strength + 1e-4f);
                        Assert.True(value <= previous + 1e-4f, $"rose at step {step} of r = {radius}.");
                        previous = value;
                    }
                },
                iter: 500
            );
    }
}
