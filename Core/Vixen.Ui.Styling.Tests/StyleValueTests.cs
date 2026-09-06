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
    [InlineData("2em", 2f, StyleUnit.Em)]
    [InlineData("1.5rem", 1.5f, StyleUnit.Rem)]
    [InlineData("100vw", 100f, StyleUnit.ViewportWidth)]
    [InlineData("50vh", 50f, StyleUnit.ViewportHeight)]
    [InlineData("10vmin", 10f, StyleUnit.ViewportMin)]
    [InlineData("10vmax", 10f, StyleUnit.ViewportMax)]
    public void Lengths_carry_their_unit(string text, float expected, StyleUnit unit) {
        var value = Parser().Parse(text);

        Assert.Equal(StyleValueKind.Length, value.Kind);
        Assert.Equal(expected, value.Number, Tolerance);
        Assert.Equal(unit, value.Unit);
    }

    [Fact]
    public void A_relative_length_survives_a_round_trip_through_its_own_text() {
        var parser = Parser();

        // ⚠ `em` is a suffix of `rem`, so a suffix test in the wrong order silently reads the first
        // as the second and every root-relative length in the document becomes font-relative. The
        // round trip is what makes that visible rather than a plausible-looking number.
        foreach (var text in new[] { "2em", "1.5rem", "100vw", "50vh", "10vmin", "10vmax" }) {
            Assert.Equal(text, parser.Parse(text).ToString());
        }
    }

    [Fact]
    public void A_relative_length_can_be_transitioned() {
        var parser = Parser();

        // The reason these units are representable at all. The animator interpolates StyleValue, so
        // a unit this type cannot express is a unit that cannot animate — and `width: 2em` under a
        // transition would snap while its neighbours ease, with nothing said about it.
        var half = StyleValue.Lerp(parser.Parse("2em"), parser.Parse("4em"), 0.5f);

        Assert.Equal(StyleUnit.Em, half.Unit);
        Assert.Equal(3f, half.Number, Tolerance);

        // And the older limit still holds: two units cannot be mixed, because Vixen animates
        // specified values where CSS animates computed ones.
        Assert.False(StyleValue.CanInterpolate(parser.Parse("2em"), parser.Parse("40px")));
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
    public void A_bare_zero_interpolates_with_a_length_and_takes_its_unit() {
        var parser = Parser();

        // ⚠ **CSS Values 4: a bare `0` is a valid length**, so `width: 0` and `width: 0px` are the
        // same value. This is not pedantry — "grow from nothing" is the commonest animation there
        // is, ExCSS serialises `0px` back out as `0`, and without this rule
        // `from { width: 0 } to { width: 100px }` has no midpoint and swaps at the halfway mark.
        // Which looks like an animation that does not run, not like a units rule.
        var zero = parser.Parse("0");
        var hundred = parser.Parse("100px");

        Assert.True(StyleValue.CanInterpolate(zero, hundred));

        var quarter = StyleValue.Lerp(zero, hundred, 0.25f);
        Assert.Equal(25f, quarter.Number, Tolerance);

        // And it comes back as a *length*, not as a number: the other end decides what the zero
        // meant, which is the whole content of "zero belongs to every unit".
        Assert.Equal(StyleValueKind.Length, quarter.Kind);
        Assert.Equal(hundred.Unit, quarter.Unit);

        // Both directions, and against a unit that is not pixels — a zero in `px` shrinking to a
        // percentage is the same rule seen from the other side.
        var half = StyleValue.Lerp(parser.Parse("0px"), parser.Parse("50%"), 0.5f);
        Assert.Equal(25f, half.Number, Tolerance);
        Assert.Equal(parser.Parse("50%").Unit, half.Unit);

        // ⚠ And a zero is not a licence to interpolate with anything. `0` to a colour still swaps,
        // because a zero that could become a colour would make every mismatched pair interpolable
        // as soon as one end happened to be nothing.
        Assert.False(StyleValue.CanInterpolate(zero, parser.Parse("rgb(0, 0, 0)")));
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

    [Theory]
    [InlineData("oklch(62.3% 0.214 259.815)")] // Tailwind v4 blue-500: linear blue past 1.
    [InlineData("oklch(69.6% 0.17 162.48)")] // Tailwind v4 emerald-500: linear red below 0.
    public void An_out_of_gamut_colour_survives_the_animator_round_trip(string text) {
        // ⚠ **This is the one place an out-of-gamut colour used to die.** Parse, resolve and draw all
        // carry the linear channels through untouched; the animator is the only step that writes the
        // value back out as text, and `rgba()` has eight bits of encoded sRGB to say it in. Two of
        // every three colours in v4's palette are outside sRGB — see
        // docs/plan/43-web-styling-parity.md § D4 — so a transition on a Tailwind token was flattening
        // the token to the byte grid the first frame it ran, and nothing downstream could tell that
        // from a colour that had always been in gamut.
        var keywords = new NameTable();
        var parser = new StyleValueParser(new NameTable(), keywords);

        var original = parser.Parse(text);
        Assert.Equal(StyleValueKind.Color, original.Kind);

        var outside = original.Color.R is < 0f or > 1f
            || original.Color.G is < 0f or > 1f
            || original.Color.B is < 0f or > 1f;

        Assert.True(outside, $"the sample is meant to be outside sRGB: {original.Color}");

        var css = original.ToCss(keywords);
        Assert.StartsWith("color(srgb-linear ", css, StringComparison.Ordinal);

        var round = parser.Parse(css);
        Assert.Equal(StyleValueKind.Color, round.Kind);

        // Not "close enough": the point of spelling it `color(srgb-linear …)` rather than widening
        // `rgba()`'s precision is that the text is the same triple, so the round trip is exact.
        Assert.Equal(original.Color.R, round.Color.R);
        Assert.Equal(original.Color.G, round.Color.G);
        Assert.Equal(original.Color.B, round.Color.B);
        Assert.Equal(original.Color.A, round.Color.A);

        // And the property that actually matters downstream: it is still outside sRGB afterwards.
        Assert.True(
            round.Color.R is < 0f or > 1f || round.Color.G is < 0f or > 1f || round.Color.B is < 0f or > 1f,
            $"the round trip flattened it into gamut: {css}"
        );
    }

    [Fact]
    public void An_in_gamut_colour_still_goes_back_out_as_rgba() {
        // The wide-gamut spelling is the exception, not the new default. Every colour a stylesheet
        // holds passes through `Animator`'s intern table, which compares the text; `rgba(20, 130,
        // 200, 1)` is a short string drawn from a small set, where the full-precision spelling is
        // both longer and far more varied. Keeping the common case on the short one is what makes
        // this change free for the colours that never needed it.
        var keywords = new NameTable();
        var parser = new StyleValueParser(new NameTable(), keywords);

        Assert.Equal("rgba(20, 130, 200, 1)", parser.Parse("rgb(20, 130, 200)").ToCss(keywords));

        // A spring overshoots past 1 on the alpha and `rgba()` is the branch that carries that —
        // `color()` clamps alpha on the way back in, so an in-gamut colour mid-overshoot must not be
        // routed there.
        var overshot = StyleValue.Lerp(parser.Parse("rgba(20, 130, 200, 0)"), parser.Parse("rgb(20, 130, 200)"), 1.2f);

        Assert.Equal("rgba(20, 130, 200, 1.2)", overshot.ToCss(keywords));
    }

    /// <summary>A leading minus starts a number only when a number follows it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every vendor-prefixed keyword in CSS was unparseable, and the failure was silent
    ///         in the one way that is hardest to notice.</b> The first character decided the branch,
    ///         so <c>-webkit-center</c> went down the numeric path, failed it, and came back
    ///         <see cref="StyleValueKind.Unknown" /> — a declaration that resolves to nothing with no
    ///         diagnostic, because an unparseable <i>value</i> is not a refused selector and nothing
    ///         reports one. CSS Syntax §4.3.11 lets an identifier begin with a hyphen and a great many
    ///         do.
    ///     </para>
    ///     <para>
    ///         ⚠ Both directions in one theory on purpose. The obvious repair — accept a hyphen as an
    ///         identifier start — turns <c>-4.5px</c> into the keyword <c>-4.5px</c>, which is a
    ///         length silently becoming a word, and that is worse than the bug being fixed because a
    ///         zero-valued length still lays out.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("-webkit-center", StyleValueKind.Keyword)]
    [InlineData("-webkit-left", StyleValueKind.Keyword)]
    [InlineData("-moz-fit-content", StyleValueKind.Keyword)]
    [InlineData("-4.5px", StyleValueKind.Length)]
    [InlineData("-.5", StyleValueKind.Number)]
    [InlineData("-3", StyleValueKind.Number)]
    [InlineData("+2px", StyleValueKind.Length)]
    public void A_hyphen_starts_an_identifier_unless_a_number_follows_it(string text, StyleValueKind expected) =>
        Assert.Equal(expected, Parser().Parse(text).Kind);

    /// <summary>A keyword is one interned id however its case is written.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The general form of a defect that made a hand-written declaration paint
    ///         nothing.</b> CSS Values 4 § 3.1 makes a keyword ASCII case-insensitive; the intern
    ///         table is ordinal, so before the fold two spellings were two ids and every reader in
    ///         the engine held exactly one of them. An identifier in the other case reached its
    ///         consumer <i>unrecognised</i> — not wrong, not diagnosed, just absent — so the frame
    ///         looked like the declaration had never been written.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Written at the parser and not per property, because there is one intern.</b>
    ///         <c>currentcolor</c> is the row the bug was found on, but <c>solid</c>,
    ///         <c>underline</c>, <c>flex</c> and every other keyword in the language came through the
    ///         same line — which is why pinning the one keyword in <c>DrawListBuilder</c> fixed
    ///         <c>box-shadow</c> and nothing else.
    ///     </para>
    ///     <para>
    ///         The vendor-prefixed row is here on purpose: the fold must not eat the leading hyphen
    ///         the theory above exists to protect.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("currentcolor", "CurrentColor", "CURRENTCOLOR")]
    [InlineData("solid", "Solid", "SOLID")]
    [InlineData("underline", "Underline", "UNDERLINE")]
    [InlineData("flex", "Flex", "FLEX")]
    [InlineData("-webkit-center", "-WebKit-Center", "-WEBKIT-CENTER")]
    public void A_keyword_is_one_id_however_its_case_is_written(string lower, string mixed, string upper) {
        var keywords = new NameTable();
        var parser = new StyleValueParser(new NameTable(), keywords);

        var first = parser.Parse(lower);
        Assert.Equal(StyleValueKind.Keyword, first.Kind);

        foreach (var spelling in new[] { mixed, upper }) {
            var other = parser.Parse(spelling);

            Assert.Equal(StyleValueKind.Keyword, other.Kind);
            Assert.Equal(first.Keyword, other.Keyword);
        }

        // And the id a consumer interns to compare against is the lowercase spelling, which is the
        // half that makes the fold usable: a reader writes the keyword the way CSS spells it.
        Assert.Equal(lower, keywords.NameOf(first.Keyword));
        Assert.Equal(first.Keyword, keywords.Intern(lower));
    }

    /// <summary>Only ASCII letters fold, and a non-keyword value is not touched at all.</summary>
    /// <remarks>
    ///     ⚠ <b><c>ToLowerInvariant</c> would have been wrong and would have looked right.</b> CSS
    ///     folds exactly the twenty-six ASCII letters; a Turkish <c>İ</c> or a Greek <c>Σ</c> in an
    ///     identifier is a different character from its lowercase form as far as the language is
    ///     concerned, and a table keyed on the folded text would answer for a name nobody wrote.
    /// </remarks>
    [Fact]
    public void The_fold_is_ASCII_and_reaches_only_identifiers() {
        var keywords = new NameTable();
        var parser = new StyleValueParser(new NameTable(), keywords);

        Assert.Equal("stra\u00dfe-\u00c4", keywords.NameOf(parser.Parse("Stra\u00dfe-\u00c4").Keyword));

        // A colour, a length and a number never reach the intern, so nothing about them changes.
        Assert.Equal(StyleValueKind.Color, parser.Parse("#AABBCC").Kind);
        Assert.Equal(StyleValueKind.Length, parser.Parse("12PX").Kind);
    }
}
