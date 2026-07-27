// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Core.Mathematics.Tests;

/// <summary>Oklab against the values its author published, and against itself.</summary>
/// <remarks>
///     An external oracle, which is worth more than any number of hand-computed expectations: the
///     numbers below are Björn Ottosson's own, from the article that defines the space. Transcribing
///     a matrix wrongly is the obvious failure here and it produces colours that look almost right,
///     so "almost right" is exactly what must not be allowed to pass.
/// </remarks>
public class OklabTests {
    // A tolerance rather than a digit count. `Assert.Equal(x, y, 4)` rounds both to four decimals
    // and compares, so a true value sitting exactly on a rounding boundary — 0.15625 — fails on a
    // discrepancy of 3e-7, which is float noise and not a defect. Tolerance says what is meant.
    const float Tolerance = 1e-4f;

    // Ottosson's reference table, linear sRGB in and Oklab out. Reproduced to six decimal places,
    // which is more precision than a float carries and therefore checks the conversion rather than
    // the transcription.
    [Theory]
    [InlineData(1f, 1f, 1f, 1.000000f, 0.000000f, 0.000000f)]
    [InlineData(1f, 0f, 0f, 0.627955f, 0.224863f, 0.125846f)]
    [InlineData(0f, 1f, 0f, 0.866440f, -0.233888f, 0.179498f)]
    [InlineData(0f, 0f, 1f, 0.452014f, -0.032457f, -0.311528f)]
    [InlineData(0f, 0f, 0f, 0.000000f, 0.000000f, 0.000000f)]
    public void The_conversion_matches_the_values_its_author_published(
        float r,
        float g,
        float b,
        float l,
        float a,
        float bb
    ) {
        var lab = Oklab.FromLinear(new Vector3(r, g, b));

        Assert.Equal(l, lab.L, Tolerance);
        Assert.Equal(a, lab.A, Tolerance);
        Assert.Equal(bb, lab.B, Tolerance);
    }

    [Fact]
    public void Converting_back_gives_the_colour_that_went_in() {
        // The inverse matrices are a separate transcription and need a separate check. A round trip
        // over randomised colours catches a wrong coefficient in either direction, including the
        // ones that happen to be right for the five colours above.
        Gen.Select(Gen.Float[0f, 1f], Gen.Float[0f, 1f], Gen.Float[0f, 1f]).Sample(colour => {
                var (r, g, b) = colour;
                var original = new Vector3(r, g, b);
                var round = Oklab.FromLinear(original).ToLinear();

                Assert.Equal(original.X, round.X, Tolerance);
                Assert.Equal(original.Y, round.Y, Tolerance);
                Assert.Equal(original.Z, round.Z, Tolerance);
            }, iter: 1000
        );
    }

    [Fact]
    public void A_colour_outside_the_sRGB_gamut_survives_the_round_trip() {
        // HDR values and out-of-gamut colours reach this code, and the signed cube root is what
        // makes them work: `MathF.Pow(x, 1f / 3f)` returns NaN for a negative channel, and a
        // negative channel is what an out-of-gamut colour *is*.
        var wide = new Vector3(4.2f, -0.3f, 0.8f);
        var round = Oklab.FromLinear(wide).ToLinear();

        Assert.Equal(wide.X, round.X, 1e-3f);
        Assert.Equal(wide.Y, round.Y, 1e-3f);
        Assert.Equal(wide.Z, round.Z, 1e-3f);
    }

    [Fact]
    public void Blue_to_white_does_not_detour_through_purple() {
        // The failure the whole space is here to prevent, and the one everybody has seen. In sRGB
        // the midpoint of blue→white picks up red it has no business having; perceptually it should
        // stay on the blue side of neutral all the way.
        var blue = new Color4(0f, 0f, 1f, 1f);
        var white = new Color4(1f, 1f, 1f, 1f);

        var perceptual = Oklab.Lerp(blue, white, 0.5f);
        var naive = Color4.Lerp(blue, white, 0.5f);

        // Purple is red running ahead of green. In the naive mix they arrive together, and in the
        // perceptual one green leads — which is what "does not turn purple" means numerically.
        Assert.Equal(naive.R, naive.G, Tolerance);
        Assert.True(
            perceptual.G > perceptual.R,
            $"expected green ahead of red, got r={perceptual.R:F4} g={perceptual.G:F4}"
        );
    }

    [Fact]
    public void Interpolating_a_colour_with_itself_returns_it() {
        Gen.Select(Gen.Float[0f, 1f], Gen.Float[0f, 1f], Gen.Float[0f, 1f], Gen.Float[0f, 1f]).Sample(sample => {
                var (r, g, b, t) = sample;
                var colour = new Color4(r, g, b, 1f);
                var mixed = Oklab.Lerp(colour, colour, t);

                Assert.Equal(colour.R, mixed.R, Tolerance);
                Assert.Equal(colour.G, mixed.G, Tolerance);
                Assert.Equal(colour.B, mixed.B, Tolerance);
            }, iter: 500
        );
    }

    [Fact]
    public void Lightness_moves_monotonically_along_an_interpolation() {
        // What "perceptually uniform" has to mean at minimum: a fade never brightens on its way to
        // something darker. A wrong sign in the inverse matrix passes every round-trip test and
        // fails this.
        var from = Oklab.FromLinear(new Vector3(0.05f, 0.05f, 0.4f));
        var to = Oklab.FromLinear(new Vector3(0.9f, 0.85f, 0.2f));
        var previous = float.NegativeInfinity;

        for (var i = 0; i <= 20; i++) {
            var lightness = Oklab.Lerp(from, to, i / 20f).L;

            Assert.True(lightness > previous, $"lightness went backwards at {i / 20f}");
            previous = lightness;
        }
    }
}
