// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Core.Mathematics.Tests;

/// <summary>
///     CSS Color 4's gamut mapping, and the gamut arithmetic under it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Nothing here is checked against a reference implementation, on purpose.</b> The
///         previous attempt at measuring this palette compared against something that clamped its own
///         output before printing, so the two agreed on precisely the numbers the clamp had already
///         decided. Every assertion below is either a structural property that must hold whatever the
///         numbers are — white stays white, a round trip is an identity, sRGB fits inside P3 — or a
///         claim about hue that is stated as a <em>comparison against per-channel clipping</em>
///         rather than as a magic constant.
///     </para>
///     <para>
///         The colours come from Tailwind v4's own <c>theme.css</c>, and reach Oklab here through
///         <see cref="Oklab" /> directly rather than through the styling parser — a second path to
///         the same finding.
///     </para>
/// </remarks>
public sealed class GamutMapTests {
    /// <summary>A linear sRGB colour from Oklch components, the way a stylesheet writes them.</summary>
    static Vector3 Oklch(float lightness, float chroma, float degrees) {
        var hue = degrees * MathF.PI / 180f;

        return new Oklab(lightness, chroma * MathF.Cos(hue), chroma * MathF.Sin(hue)).ToLinear();
    }

    static float Hue(Vector3 linear) {
        var lab = Oklab.FromLinear(linear);

        return MathF.Atan2(lab.B, lab.A) * 180f / MathF.PI;
    }

    static float HueDifference(Vector3 left, Vector3 right) {
        var difference = MathF.Abs(Hue(left) - Hue(right)) % 360f;

        return difference > 180f ? 360f - difference : difference;
    }

    /// <summary>
    ///     White is the one colour every one of these gamuts agrees on, because they share a white
    ///     point — and it is the assertion that catches the classic broken colour matrix, which has
    ///     the right hues and the wrong balance because its columns were never scaled.
    /// </summary>
    [Theory]
    [InlineData(ColorGamut.DisplayP3)]
    [InlineData(ColorGamut.Rec2020)]
    public void White_is_white_in_every_gamut(ColorGamut gamut) {
        var white = GamutMap.FromLinearSrgb(new Vector3(1f, 1f, 1f), gamut);

        Assert.Equal(1f, white.X, 4);
        Assert.Equal(1f, white.Y, 4);
        Assert.Equal(1f, white.Z, 4);
    }

    /// <summary>
    ///     ⚠ <b>The derivation checked against numbers this code did not produce.</b> The linear
    ///     sRGB to Display P3 matrix is widely published as
    ///     <c>[0.822462 0.177538 0; 0.033194 0.966806 0; 0.017083 0.072397 0.910520]</c>, and
    ///     transforming the three basis vectors recovers its columns. A round-trip test alone would
    ///     pass on a matrix and its exact inverse however wrong both were; this is the assertion
    ///     that says the primaries and the white-point scaling are right.
    /// </summary>
    [Fact]
    public void The_derived_P3_matrix_matches_the_published_one() {
        var red = GamutMap.FromLinearSrgb(new Vector3(1f, 0f, 0f), ColorGamut.DisplayP3);
        var green = GamutMap.FromLinearSrgb(new Vector3(0f, 1f, 0f), ColorGamut.DisplayP3);
        var blue = GamutMap.FromLinearSrgb(new Vector3(0f, 0f, 1f), ColorGamut.DisplayP3);

        Assert.Equal(0.822462f, red.X, 4);
        Assert.Equal(0.033194f, red.Y, 4);
        Assert.Equal(0.017083f, red.Z, 4);

        Assert.Equal(0.177538f, green.X, 4);
        Assert.Equal(0.966806f, green.Y, 4);
        Assert.Equal(0.072397f, green.Z, 4);

        // sRGB blue and P3 blue share a chromaticity — (0.150, 0.060) in both — so the top two
        // entries of this column are exactly zero, and a derivation that got the primaries from the
        // wrong P3 would not reproduce that.
        Assert.Equal(0f, blue.X, 5);
        Assert.Equal(0f, blue.Y, 5);
        Assert.Equal(0.910520f, blue.Z, 4);
    }

    [Theory]
    [InlineData(ColorGamut.Srgb)]
    [InlineData(ColorGamut.DisplayP3)]
    [InlineData(ColorGamut.Rec2020)]
    public void Rebasing_onto_a_gamut_and_back_is_an_identity(ColorGamut gamut) {
        foreach (var colour in new[] {
            new Vector3(0.2f, 0.7f, 0.4f), new Vector3(1f, 0f, 0f), new Vector3(0.05f, 0.05f, 0.9f)
        }) {
            var round = GamutMap.ToLinearSrgb(GamutMap.FromLinearSrgb(colour, gamut), gamut);

            Assert.Equal(colour.X, round.X, 4);
            Assert.Equal(colour.Y, round.Y, 4);
            Assert.Equal(colour.Z, round.Z, 4);
        }
    }

    /// <summary>
    ///     P3 and Rec. 2020 both contain sRGB entirely, so no sRGB colour should ever be reported as
    ///     needing repair on a wide display. This is the property that makes "map against the
    ///     display's gamut" safe to turn on.
    /// </summary>
    [Theory]
    [InlineData(ColorGamut.DisplayP3)]
    [InlineData(ColorGamut.Rec2020)]
    public void Every_sRGB_colour_is_inside_the_wider_gamuts(ColorGamut gamut) {
        for (var r = 0; r <= 4; r++) {
            for (var g = 0; g <= 4; g++) {
                for (var b = 0; b <= 4; b++) {
                    var colour = new Vector3(r / 4f, g / 4f, b / 4f);

                    Assert.True(
                        GamutMap.InGamut(colour, gamut),
                        $"{colour} is inside sRGB but was reported outside {gamut}"
                    );
                }
            }
        }
    }

    /// <summary>
    ///     ⚠ <b>The finding this whole task rests on, re-derived by a second route.</b> Two of the
    ///     three sampled Tailwind v4 colours cannot be shown on an sRGB display — and both of them
    ///     can be shown on a P3 one, which is the entire argument for mapping against the display's
    ///     gamut rather than always against sRGB.
    /// </summary>
    [Fact]
    public void Two_of_three_Tailwind_colours_need_sRGB_and_none_need_P3() {
        var blue = Oklch(0.623f, 0.214f, 259.815f);
        var emerald = Oklch(0.696f, 0.17f, 162.48f);
        var red = Oklch(0.637f, 0.237f, 25.331f);

        Assert.False(GamutMap.InGamut(blue, ColorGamut.Srgb));
        Assert.False(GamutMap.InGamut(emerald, ColorGamut.Srgb));
        Assert.True(GamutMap.InGamut(red, ColorGamut.Srgb));

        // Which way each one overflows, because "out of gamut" alone would pass on a sign error.
        Assert.True(blue.Z > 1f, $"blue-500's linear blue should overflow, got {blue.Z}");
        Assert.True(emerald.X < 0f, $"emerald-500's linear red should go negative, got {emerald.X}");

        // The point of the exercise: on the display this engine is developed on, all three are
        // showable and the mapper must leave them alone.
        Assert.True(GamutMap.InGamut(blue, ColorGamut.DisplayP3));
        Assert.True(GamutMap.InGamut(emerald, ColorGamut.DisplayP3));
        Assert.True(GamutMap.InGamut(red, ColorGamut.DisplayP3));
    }

    /// <summary>A colour inside the gamut is returned bit for bit — the intent is relative colorimetric.</summary>
    [Fact]
    public void An_in_gamut_colour_is_untouched() {
        var colour = new Vector3(0.2f, 0.6f, 0.35f);

        Assert.Equal(colour, GamutMap.Map(colour, ColorGamut.Srgb));

        // And the wide-gamut case that would otherwise be silently destroyed.
        var blue = Oklch(0.623f, 0.214f, 259.815f);
        Assert.Equal(blue, GamutMap.Map(blue, ColorGamut.DisplayP3));
    }

    [Theory]
    [InlineData(ColorGamut.Srgb)]
    [InlineData(ColorGamut.DisplayP3)]
    public void A_mapped_colour_is_inside_the_gamut(ColorGamut gamut) {
        for (var hue = 0f; hue < 360f; hue += 15f) {
            var mapped = GamutMap.Map(Oklch(0.7f, 0.4f, hue), gamut);

            Assert.True(
                GamutMap.InGamut(mapped, gamut),
                $"hue {hue} mapped to {mapped}, still outside {gamut}"
            );
        }
    }

    /// <summary>
    ///     ⚠ <b>The claim that justifies the algorithm over a two-line clamp.</b> Per-channel clipping
    ///     shifts hue because a vivid red runs out of red first and keeps its green and blue; chroma
    ///     reduction cannot shift hue, because hue is what it holds fixed. Stated as a comparison so
    ///     that it is a fact about the two methods rather than a tolerance someone tuned.
    /// </summary>
    [Fact]
    public void Chroma_reduction_holds_hue_where_clipping_does_not() {
        var worstClip = 0f;
        var worstMap = 0f;

        for (var hue = 0f; hue < 360f; hue += 5f) {
            var origin = Oklch(0.65f, 0.37f, hue);

            if (GamutMap.InGamut(origin, ColorGamut.Srgb)) {
                continue;
            }

            worstClip = MathF.Max(worstClip, HueDifference(origin, GamutMap.Clip(origin, ColorGamut.Srgb)));
            worstMap = MathF.Max(worstMap, HueDifference(origin, GamutMap.Map(origin, ColorGamut.Srgb)));
        }

        // Measured, not assumed: at L=0.65, C=0.37 the worst per-channel clip moves the hue by about
        // 42.5°, which is red arriving as orange. The mapper's worst is about 5.5°, and that residual
        // is the local-MINDE clip at the end — the deliberate trade that buys back chroma near a
        // concave patch of the surface. The bounds below are loose around those two numbers so that
        // this fails on a regression rather than on the last digit of a cube root.
        Assert.True(worstClip > 20f, $"clipping should shift hue badly, worst was {worstClip}");
        Assert.True(worstMap < 10f, $"mapping should hold hue, worst was {worstMap}");

        Assert.True(
            worstMap < worstClip / 4f,
            $"mapping should hold hue far better than clipping: mapped {worstMap}, clipped {worstClip}"
        );
    }

    /// <summary>
    ///     Lightness is held too, which is the other half of "constant-lightness, constant-hue chroma
    ///     reduction" and the reason a mapped palette keeps its contrast ladder.
    /// </summary>
    [Fact]
    public void Mapping_holds_lightness() {
        for (var hue = 0f; hue < 360f; hue += 30f) {
            var origin = Oklch(0.55f, 0.35f, hue);
            var mapped = GamutMap.Map(origin, ColorGamut.Srgb);

            // Within one JND: local MINDE ends on a clipped colour, so lightness moves a little.
            Assert.InRange(
                MathF.Abs(Oklab.FromLinear(mapped).L - Oklab.FromLinear(origin).L),
                0f,
                GamutMap.JustNoticeableDifference
            );
        }
    }

    /// <summary>
    ///     Out of range on the lightness axis there is no chroma reduction that helps, so the
    ///     specification names the answers instead of searching for them.
    /// </summary>
    [Fact]
    public void Lightness_outside_the_range_maps_to_white_or_black() {
        Assert.Equal(new Vector3(1f, 1f, 1f), GamutMap.Map(new Vector3(4f, 4f, 4f), ColorGamut.Srgb));
        Assert.Equal(default, GamutMap.Map(new Vector3(-1f, -1f, -1f), ColorGamut.Srgb));
    }

    [Fact]
    public void Alpha_is_carried_through_untouched() {
        var vivid = Oklch(0.7f, 0.4f, 30f);
        var mapped = GamutMap.Map(new Color4(vivid.X, vivid.Y, vivid.Z, 0.25f), ColorGamut.Srgb);

        Assert.Equal(0.25f, mapped.A);
    }

    /// <summary>ΔE<sub>OK</sub> is a Euclidean distance in Oklab, nothing more.</summary>
    [Fact]
    public void The_difference_metric_is_a_plain_distance() {
        Assert.Equal(0f, GamutMap.DeltaEOk(new Oklab(0.5f, 0.1f, 0.1f), new Oklab(0.5f, 0.1f, 0.1f)));

        Assert.Equal(
            5f,
            GamutMap.DeltaEOk(new Oklab(0f, 0f, 0f), new Oklab(3f, 4f, 0f)),
            4
        );
    }

    /// <summary>
    ///     Rec. 2020 is wider than P3, so a colour needing repair for P3 may need none for Rec. 2020 —
    ///     and never the other way round. The ordering is what makes "map against the display" mean
    ///     anything.
    /// </summary>
    [Fact]
    public void The_three_gamuts_nest() {
        for (var hue = 0f; hue < 360f; hue += 10f) {
            var colour = Oklch(0.7f, 0.25f, hue);

            if (GamutMap.InGamut(colour, ColorGamut.Srgb)) {
                Assert.True(GamutMap.InGamut(colour, ColorGamut.DisplayP3), $"sRGB but not P3 at {hue}");
            }

            if (GamutMap.InGamut(colour, ColorGamut.DisplayP3)) {
                Assert.True(GamutMap.InGamut(colour, ColorGamut.Rec2020), $"P3 but not Rec2020 at {hue}");
            }
        }
    }

    /// <summary>
    ///     CSS Color 4 writes the lightness branches before the in-gamut test; <see cref="GamutMap.Map" />
    ///     asks the in-gamut question first so that a showable colour never pays for
    ///     <see cref="Oklab.FromLinear" />'s three cube roots. This pins that the swap is free.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The oracle is the specification's ordering written out here, not a golden number.</b>
    ///     The reordering is only sound because no colour inside any of these three gamuts has a
    ///     lightness outside <c>(0, 1)</c> — they share D65 and normalise white to <c>L = 1</c> — so
    ///     the two orders can disagree only about colours that are white to within float noise. The
    ///     generator is deliberately allowed well outside <c>[0, 1]</c> per channel, because
    ///     out-of-gamut input is the entire population this function exists for.
    /// </remarks>
    [Fact]
    public void Map_agrees_with_the_specification_ordering() {
        var channel = Gen.Float[-0.5f, 1.5f];

        Gen.Select(channel, channel, channel, Gen.Int[0, 2])
            .Sample(sample => {
                var (r, g, b, which) = sample;
                var colour = new Vector3(r, g, b);
                var gamut = (ColorGamut) which;

                var actual = GamutMap.Map(colour, gamut);
                var expected = SpecificationOrder(colour, gamut);

                Assert.Equal(expected.X, actual.X, 4);
                Assert.Equal(expected.Y, actual.Y, 4);
                Assert.Equal(expected.Z, actual.Z, 4);
            });
    }

    /// <summary>The lightness branches ahead of the in-gamut test, as CSS Color 4 § 14.2 writes them.</summary>
    static Vector3 SpecificationOrder(Vector3 linear, ColorGamut gamut) {
        var origin = Oklab.FromLinear(linear);

        if (origin.L >= 1f) {
            return new Vector3(1f, 1f, 1f);
        }

        if (origin.L <= 0f) {
            return default;
        }

        return GamutMap.Map(linear, gamut);
    }
}
