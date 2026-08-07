// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Ui.Styling.Tests;

/// <summary><c>color()</c> parsing and <c>@media (color-gamut: …)</c>.</summary>
/// <remarks>
///     ⚠ <b>The point of both is that a wide colour reaches the working space intact.</b> A test
///     that asserted the parsed value equalled some clamped byte triple would pass on a parser that
///     had thrown the gamut away, which is the mistake doc 43 § D4 records having already been made
///     once in this area.
/// </remarks>
public class WideGamutTests {
    static StyleValueParser Parser() => new(new NameTable(), new NameTable());

    static Color4 Color(string text) {
        var value = Parser().Parse(text);
        Assert.Equal(StyleValueKind.Color, value.Kind);

        return value.Color;
    }

    /// <summary>
    ///     CSS Color 4 § 10's own worked example of a colour inside P3 and outside sRGB. It must
    ///     arrive as a linear triple with a channel past one, because that is the only shape in
    ///     which the information still exists.
    /// </summary>
    [Fact]
    public void A_display_p3_colour_outside_sRGB_survives_parsing() {
        var colour = Color("color(display-p3 1.0844 0.43 0.1)");
        var linear = new Vector3(colour.R, colour.G, colour.B);

        Assert.False(GamutMap.InGamut(linear, ColorGamut.Srgb));
        Assert.True(linear.X > 1f, $"linear red should be past one, got {linear.X}");
    }

    /// <summary>An in-gamut P3 colour is an ordinary colour, and P3 red is not sRGB red.</summary>
    [Fact]
    public void Display_p3_primaries_are_not_sRGB_primaries() {
        var p3Red = Color("color(display-p3 1 0 0)");
        var srgbRed = Color("color(srgb 1 0 0)");

        Assert.Equal(1f, srgbRed.R, 3);
        Assert.Equal(0f, srgbRed.G, 3);

        // P3's red is outside sRGB — that is what "wider" means — and it overflows on red.
        Assert.True(p3Red.R > 1f, $"P3 red should exceed sRGB red, got {p3Red.R}");
        Assert.True(GamutMap.InGamut(new Vector3(p3Red.R, p3Red.G, p3Red.B), ColorGamut.DisplayP3));
    }

    /// <summary><c>srgb-linear</c> skips the transfer function, which is the whole difference.</summary>
    [Fact]
    public void The_linear_variant_skips_the_transfer_function() {
        Assert.Equal(0.5f, Color("color(srgb-linear 0.5 0.5 0.5)").R, 3);
        Assert.Equal(ColorSpace.SrgbToLinear(0.5f), Color("color(srgb 0.5 0.5 0.5)").R, 3);
    }

    [Fact]
    public void Alpha_and_percentages_are_read() {
        var colour = Color("color(display-p3 100% 0% 0% / 50%)");

        Assert.Equal(0.5f, colour.A, 3);
        Assert.Equal(Color("color(display-p3 1 0 0)").R, colour.R, 3);
    }

    /// <summary>
    ///     ⚠ A space whose transfer curve is not implemented is refused rather than decoded with
    ///     sRGB's. The result would be wrong by a few percent in the midtones — plausible enough to
    ///     ship, and invisible until someone compares against a browser.
    /// </summary>
    [Theory]
    [InlineData("color(prophoto-rgb 0.5 0.5 0.5)")]
    [InlineData("color(a98-rgb 0.5 0.5 0.5)")]
    [InlineData("color(rec2020 0.5 0.5 0.5)")]
    [InlineData("color(xyz 0.5 0.5 0.5)")]
    [InlineData("color(display-p3 0.5 0.5)")]
    public void An_unimplemented_space_is_refused(string text) =>
        Assert.Equal(StyleValueKind.Unknown, Parser().Parse(text).Kind);

    /// <summary>
    ///     ⚠ <b>Ascending, not equal.</b> Media Queries 5 § 5.4 says a device may return true for
    ///     several values of this feature, and recommends authors set a base at <c>srgb</c> then
    ///     override at <c>p3</c>. Equality matching would make the base rule stop applying on
    ///     precisely the displays it was written for.
    /// </summary>
    [Theory]
    [InlineData(ColorGamut.Srgb, "srgb", true)]
    [InlineData(ColorGamut.Srgb, "p3", false)]
    [InlineData(ColorGamut.Srgb, "rec2020", false)]
    [InlineData(ColorGamut.DisplayP3, "srgb", true)]
    [InlineData(ColorGamut.DisplayP3, "p3", true)]
    [InlineData(ColorGamut.DisplayP3, "rec2020", false)]
    [InlineData(ColorGamut.Rec2020, "srgb", true)]
    [InlineData(ColorGamut.Rec2020, "p3", true)]
    [InlineData(ColorGamut.Rec2020, "rec2020", true)]
    public void A_wider_display_matches_every_narrower_gamut(ColorGamut gamut, string asked, bool expected) {
        var context = new MediaContext(800, 600, Gamut: gamut);

        Assert.True(
            MediaQuery.TryEvaluate($"(color-gamut: {asked})", context, out var matches, out var reason),
            reason
        );

        Assert.Equal(expected, matches);
    }

    /// <summary>The feature is discrete, so the range prefixes are a diagnostic and not a synonym.</summary>
    [Theory]
    [InlineData("(min-color-gamut: p3)")]
    [InlineData("(color-gamut: srgba)")]
    public void A_malformed_gamut_query_is_refused(string condition) {
        Assert.False(MediaQuery.TryEvaluate(condition, new MediaContext(800, 600), out _, out var reason));
        Assert.NotNull(reason);
    }
}
