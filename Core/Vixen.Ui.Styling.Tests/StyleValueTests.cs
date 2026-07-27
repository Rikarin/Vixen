// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Ui.Styling.Tests;

/// <summary>Parsing a declaration into something interpolatable, and interpolating it.</summary>
public class StyleValueTests {
    const float Tolerance = 1e-3f;

    static StyleValueParser Parser() => new(new NameTable(), new NameTable());

    [Fact]
    public void The_same_colour_parses_the_same_whichever_form_ExCSS_left_it_in() {
        // The consequence of ADR-009 that the spike did not reach, as a test. ExCSS normalises
        // `color: red` to `rgb(255, 0, 0)` and leaves `color: var(--c)` verbatim, so the same colour
        // reaches Vixen written two different ways depending on whether a custom property was
        // involved. A parser that handled only the first would work until someone used one.
        var parser = Parser();

        var normalised = parser.Parse("rgb(255, 0, 0)");
        var substituted = parser.Parse("red");
        var hex = parser.Parse("#ff0000");

        Assert.Equal(StyleValueKind.Color, normalised.Kind);
        Assert.Equal(normalised, substituted);
        Assert.Equal(normalised, hex);
    }

    [Fact]
    public void Colours_are_decoded_to_linear_on_the_way_in() {
        // Everything past the cascade works in linear, and mid-grey is where getting it wrong is
        // most visible: sRGB 128 is linear 0.216, not 0.502. A fade computed on the encoded numbers
        // darkens in the middle.
        var parser = Parser();
        var grey = parser.Parse("rgb(128, 128, 128)");

        Assert.Equal(ColorSpace.SrgbToLinear(128f / 255f), grey.Color.R, Tolerance);
        Assert.True(grey.Color.R < 0.25f, $"expected a linear value, got {grey.Color.R}");
    }

    [Theory]
    [InlineData("12px", 12f, StyleUnit.Pixels)]
    [InlineData("-4.5px", -4.5f, StyleUnit.Pixels)]
    [InlineData("50%", 50f, StyleUnit.Percent)]
    [InlineData("2s", 2f, StyleUnit.Seconds)]
    [InlineData("250ms", 0.25f, StyleUnit.Seconds)]
    [InlineData("45deg", 45f, StyleUnit.Degrees)]
    public void Lengths_carry_their_unit(string text, float expected, StyleUnit unit) {
        var value = Parser().Parse(text);

        Assert.Equal(StyleValueKind.Length, value.Kind);
        Assert.Equal(expected, value.Number, Tolerance);
        Assert.Equal(unit, value.Unit);
    }

    [Fact]
    public void A_bare_number_is_a_number_and_not_a_length() {
        var value = Parser().Parse("0.5");

        Assert.Equal(StyleValueKind.Number, value.Kind);
        Assert.Equal(0.5f, value.Number, Tolerance);
    }

    [Fact]
    public void Several_values_in_a_row_become_a_list() {
        var value = Parser().Parse("2px 4px");

        Assert.Equal(StyleValueKind.List, value.Kind);
        Assert.Equal(2, value.Items.Length);
        Assert.Equal(2f, value.Items[0].Number, Tolerance);
        Assert.Equal(4f, value.Items[1].Number, Tolerance);
    }

    [Fact]
    public void A_function_call_stays_whole_inside_a_list() {
        var value = Parser().Parse("rgb(1, 2, 3) 4px");

        Assert.Equal(StyleValueKind.List, value.Kind);
        Assert.Equal(2, value.Items.Length);
        Assert.Equal(StyleValueKind.Color, value.Items[0].Kind);
    }

    [Fact]
    public void Numbers_and_lengths_interpolate_and_mismatched_units_do_not() {
        var parser = Parser();

        var half = StyleValue.Lerp(parser.Parse("0px"), parser.Parse("10px"), 0.5f);
        Assert.Equal(5f, half.Number, Tolerance);

        // `100px` to `50%` has a perfectly good midpoint in a browser, which resolves both against
        // the containing block first. Vixen cannot — resolution happens after the cascade — so it
        // says so and flips at the halfway mark rather than inventing a number.
        var pixels = parser.Parse("100px");
        var percent = parser.Parse("50%");

        Assert.False(StyleValue.CanInterpolate(pixels, percent));
        Assert.Equal(pixels, StyleValue.Lerp(pixels, percent, 0.49f));
        Assert.Equal(percent, StyleValue.Lerp(pixels, percent, 0.51f));
    }

    [Fact]
    public void Keywords_are_discrete_and_swap_at_the_halfway_mark() {
        var parser = Parser();
        var from = parser.Parse("auto");
        var to = parser.Parse("none");

        Assert.Equal(StyleValueKind.Keyword, from.Kind);
        Assert.Equal(from, StyleValue.Lerp(from, to, 0.4f));
        Assert.Equal(to, StyleValue.Lerp(from, to, 0.6f));
    }

    [Fact]
    public void Colours_interpolate_perceptually() {
        var parser = Parser();
        var blue = parser.Parse("rgb(0, 0, 255)");
        var white = parser.Parse("rgb(255, 255, 255)");

        var mixed = StyleValue.Lerp(blue, white, 0.5f).Color;
        var naive = Color4.Lerp(blue.Color, white.Color, 0.5f);

        // Same test as the Oklab suite's, restated where it is actually reached from: the point of
        // routing colour interpolation through Oklab is that a fade to white does not turn purple.
        Assert.True(mixed.G > mixed.R, $"expected green ahead of red, got r={mixed.R:F4} g={mixed.G:F4}");
        Assert.Equal(naive.R, naive.G, Tolerance);
    }

    [Fact]
    public void Fading_to_transparent_keeps_the_hue_rather_than_travelling_through_black() {
        // CSS's rule, and the reason it is applied here rather than in `Oklab.Lerp`: a fully
        // transparent colour has no meaningful hue of its own, so `transparent` has to borrow the
        // other endpoint's. Without it, fading a red panel out passes visibly through black.
        var parser = Parser();
        var red = parser.Parse("rgb(255, 0, 0)");
        var clear = parser.Parse("transparent");

        var half = StyleValue.Lerp(red, clear, 0.5f).Color;

        Assert.Equal(0.5f, half.A, Tolerance);
        Assert.True(half.R > 0.4f, $"the red went out of the fade: r={half.R:F4}");
    }

    [Fact]
    public void A_list_interpolates_part_by_part_and_only_when_every_part_can() {
        var parser = Parser();

        var mixed = StyleValue.Lerp(parser.Parse("0px 10px"), parser.Parse("10px 20px"), 0.5f);

        Assert.Equal(5f, mixed.Items[0].Number, Tolerance);
        Assert.Equal(15f, mixed.Items[1].Number, Tolerance);

        Assert.False(StyleValue.CanInterpolate(parser.Parse("0px 10px"), parser.Parse("10px")));
        Assert.False(StyleValue.CanInterpolate(parser.Parse("0px auto"), parser.Parse("10px auto")));
    }

    [Fact]
    public void Interpolation_is_not_clamped_because_springs_overshoot() {
        // A spring goes past its target and comes back, and a `cubic-bezier` with a control point
        // above 1 is legal CSS that overshoots on purpose. Clamping here would quietly flatten
        // exactly the motion someone wrote that curve to get.
        var parser = Parser();
        var overshot = StyleValue.Lerp(parser.Parse("0px"), parser.Parse("10px"), 1.2f);

        Assert.Equal(12f, overshot.Number, Tolerance);
    }

    [Fact]
    public void A_value_written_back_as_CSS_parses_to_what_it_was() {
        // The animator hands values back to the cascade as text, so the round trip has to hold or a
        // transition would drift a little further from itself every frame.
        var values = new NameTable();
        var keywords = new NameTable();
        var parser = new StyleValueParser(values, keywords);

        foreach (var text in new[] { "12px", "0.5", "50%", "rgb(20, 130, 200)", "2px 4px" }) {
            var original = parser.Parse(text);
            var round = parser.Parse(original.ToCss(keywords));

            if (original.Kind == StyleValueKind.Color) {
                Assert.Equal(original.Color.R, round.Color.R, 5e-3f);
                Assert.Equal(original.Color.G, round.Color.G, 5e-3f);
                Assert.Equal(original.Color.B, round.Color.B, 5e-3f);
                continue;
            }

            Assert.Equal(original, round);
        }
    }
}
