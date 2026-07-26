// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Xunit;

namespace Vixen.Core.Mathematics.Tests;

/// <summary>
///     Colour, and the sRGB boundary. Almost every test here exists because the wrong answer is
///     plausible: the numbers all look reasonable, the render is merely washed out or crushed, and
///     nobody can say which conversion did it.
/// </summary>
public class ColorTests {
    const float Tolerance = 1e-4f;

    [Fact]
    public void The_transfer_functions_invert_each_other() =>
        Gen.Float[0f, 1f].Sample(
            value => Assert.Equal(value, ColorSpace.LinearToSrgb(ColorSpace.SrgbToLinear(value)), 4)
        );

    [Fact]
    public void The_transfer_functions_pin_their_endpoints_and_their_knee() {
        Assert.Equal(0f, ColorSpace.SrgbToLinear(0f), 6);
        Assert.Equal(1f, ColorSpace.SrgbToLinear(1f), 5);
        Assert.Equal(0f, ColorSpace.LinearToSrgb(0f), 6);
        Assert.Equal(1f, ColorSpace.LinearToSrgb(1f), 5);

        // The piecewise knee: below it the curve is a straight line, and the two halves meet.
        Assert.Equal(0.04045f / 12.92f, ColorSpace.SrgbToLinear(0.04045f), 6);
        Assert.Equal(0.0031308f * 12.92f, ColorSpace.LinearToSrgb(0.0031308f), 6);
    }

    [Fact]
    public void Mid_grey_is_not_half_way_which_is_the_whole_point() {
        // #808080 is the mid grey a designer picks. Its linear value is about 0.216, not 0.5 —
        // treating the byte as linear is what washes a render out, and this is the number that
        // makes the difference concrete.
        var midGrey = new Color(128, 128, 128);

        Assert.Equal(0.502f, midGrey.ToColor4().R, 3);
        Assert.Equal(0.216f, midGrey.ToLinear().R, 3);

        // And back the other way: linear 0.5 encodes to 188, not 128.
        Assert.Equal(188, Color.FromLinear(new(0.5f, 0.5f, 0.5f, 1f)).R);
    }

    [Fact]
    public void Alpha_is_never_passed_through_the_transfer_function() {
        // Alpha is a coverage fraction, not a light level. Encoding it is a classic and very
        // visible mistake — semi-transparent things come out the wrong opacity.
        var colour = new Color4(0.5f, 0.5f, 0.5f, 0.5f);

        Assert.Equal(0.5f, colour.ToSrgb().A, 5);
        Assert.Equal(0.5f, Color4.FromSrgb(colour).A, 5);
        Assert.Equal(128, Color.FromLinear(colour).A);
    }

    [Fact]
    public void A_colour_round_trips_through_bytes_and_back() =>
        Gen.Select(Gen.Byte, Gen.Byte, Gen.Byte, Gen.Byte, (r, g, b, a) => new Color(r, g, b, a))
            .Sample(colour => Assert.Equal(colour, Color.FromColor4(colour.ToColor4())));

    [Fact]
    public void A_colour_round_trips_through_linear_space() =>
        Gen.Select(Gen.Byte, Gen.Byte, Gen.Byte, Gen.Byte, (r, g, b, a) => new Color(r, g, b, a))
            .Sample(colour => Assert.Equal(colour, Color.FromLinear(colour.ToLinear())));

    [Fact]
    public void Packing_round_trips_in_both_byte_orders() {
        var colour = new Color(0x12, 0x34, 0x56, 0x78);

        Assert.Equal(0x78563412u, colour.ToRgba());
        Assert.Equal(0x78123456u, colour.ToArgb());
        Assert.Equal(colour, Color.FromRgba(colour.ToRgba()));
        Assert.Equal(colour, Color.FromArgb(colour.ToArgb()));
    }

    [Fact]
    public void Hex_parsing_accepts_every_common_spelling() {
        Assert.True(Color.TryParseHex("#FF8000", out var long6));
        Assert.Equal(new(255, 128, 0, 255), long6);

        Assert.True(Color.TryParseHex("FF8000", out var noHash));
        Assert.Equal(long6, noHash);

        Assert.True(Color.TryParseHex("#FF800080", out var withAlpha));
        Assert.Equal(new(255, 128, 0, 128), withAlpha);

        // Compact form doubles each digit, so f is 255 rather than 15.
        Assert.True(Color.TryParseHex("#F80", out var compact));
        Assert.Equal(new(255, 136, 0, 255), compact);

        Assert.True(Color.TryParseHex("#FFFF", out var compactAlpha));
        Assert.Equal(new(255, 255, 255, 255), compactAlpha);
    }

    [Fact]
    public void Hex_parsing_rejects_what_is_not_a_colour() {
        Assert.False(Color.TryParseHex("#FF801", out _));
        Assert.False(Color.TryParseHex("#FF", out _));
        Assert.False(Color.TryParseHex("", out _));
        Assert.False(Color.TryParseHex("#GGGGGG", out _));
        Assert.False(Color.TryParseHex("#FF80000", out _));
    }

    [Fact]
    public void Hex_rendering_round_trips_through_parsing() {
        var colour = new Color(1, 2, 3, 4);

        Assert.Equal("#01020304", colour.ToHex());
        Assert.Equal("#010203", colour.ToHex(includeAlpha: false));
        Assert.True(Color.TryParseHex(colour.ToHex(), out var parsed));
        Assert.Equal(colour, parsed);
    }

    [Fact]
    public void Luminance_uses_the_rec_709_weights() {
        Assert.Equal(1f, Color4.White.Luminance(), 4);
        Assert.Equal(0f, Color4.Black.Luminance(), 4);
        Assert.Equal(0.7152f, Color4.Green.Luminance(), 4);
        Assert.Equal(0.2126f, Color4.Red.Luminance(), 4);
        Assert.Equal(0.0722f, Color4.Blue.Luminance(), 4);
    }

    [Fact]
    public void Colours_are_unbounded_above_because_lights_are() {
        // Clamping here would throw away the range tonemapping exists to compress.
        var bright = new Color4(4f, 2f, 1f, 1f);

        Assert.Equal(4f, bright.R);
        Assert.Equal(8f, (bright * 2f).R);
        Assert.Equal(1f, Color4.Saturate(bright).R);
    }

    [Fact]
    public void Premultiplying_scales_the_colour_and_leaves_alpha_alone() {
        var half = new Color4(1f, 0.5f, 0.25f, 0.5f).Premultiplied();

        Assert.Equal(0.5f, half.R, 5);
        Assert.Equal(0.25f, half.G, 5);
        Assert.Equal(0.5f, half.A, 5);
    }

    [Fact]
    public void Transparent_is_black_so_premultiplied_blending_behaves() {
        // A transparent *white* leaks white into whatever filters or blends against it.
        Assert.Equal(0f, Color4.Transparent.R);
        Assert.Equal(0f, Color4.Transparent.A);
        Assert.Equal(default, Color.Transparent);
    }

    [Fact]
    public void Colours_convert_to_and_from_vectors_only_when_asked() {
        var colour = new Color4(0.1f, 0.2f, 0.3f, 0.4f);
        var vector = (Vector4)colour;

        Assert.Equal(0.1f, vector.X);
        Assert.Equal(colour, (Color4)vector);

        var rgb = (Vector3)new Color3(0.1f, 0.2f, 0.3f);
        Assert.Equal(0.2f, rgb.Y);
    }

    [Fact]
    public void Colour_arithmetic_is_component_wise() {
        var a = new Color4(0.5f, 0.25f, 0.125f, 1f);
        var b = new Color4(0.5f, 0.5f, 0.5f, 0f);

        Assert.True(Color4.NearEqual(new(1f, 0.75f, 0.625f, 1f), a + b, Tolerance));
        Assert.True(Color4.NearEqual(new(0.25f, 0.125f, 0.0625f, 0f), a * b, Tolerance));
        Assert.True(Color4.NearEqual(new(0.5f, 0.375f, 0.3125f, 0.5f), Color4.Lerp(a, b, 0.5f), Tolerance));
    }
}
