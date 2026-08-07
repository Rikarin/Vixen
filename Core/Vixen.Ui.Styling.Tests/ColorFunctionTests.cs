// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Ui.Styling.Tests;

/// <summary><c>oklab()</c>, <c>oklch()</c> and <c>color-mix()</c>, against oracles that are not this code.</summary>
/// <remarks>
///     <para>
///         <b>Two external oracles do nearly all the work here, and neither is a number this parser
///         produced.</b> The first is Björn Ottosson's published conversion table, which
///         <c>Vixen.Core.Mathematics.Tests.OklabTests</c> already holds the engine to — so
///         <c>oklab(0.627955 0.224863 0.125846)</c> has to come out sRGB red, and the polar form of
///         the same three numbers has to come out the same red. The second is arithmetic that can be
///         done on paper: an Oklab lightness of 0.5 with no chroma is a linear value of exactly
///         0.125, because the cube roots collapse and the inverse matrix's first row sums to one.
///         That single fact separates the three interpolation spaces without trusting any of them.
///     </para>
///     <para>
///         The percentage rules are cited rather than assumed. CSS Values 5 §&#160;"normalize mix
///         percentages", called with <c>color-mix()</c>'s force-normalization flag; the three cases
///         are tested apart because they fail apart.
///     </para>
/// </remarks>
public class ColorFunctionTests {
    const float Tolerance = 1e-3f;

    static StyleValueParser Parser() => new(new NameTable(), new NameTable());

    static Color4 Color(string text) {
        var value = Parser().Parse(text);
        Assert.Equal(StyleValueKind.Color, value.Kind);
        return value.Color;
    }

    static void AssertUnknown(string text) =>
        Assert.Equal(StyleValueKind.Unknown, Parser().Parse(text).Kind);

    static void AssertLinear(Color4 colour, float r, float g, float b, float a) {
        Assert.Equal(r, colour.R, Tolerance);
        Assert.Equal(g, colour.G, Tolerance);
        Assert.Equal(b, colour.B, Tolerance);
        Assert.Equal(a, colour.A, Tolerance);
    }

    // ---------------------------------------------------------------- oklab() and oklch()

    [Theory]
    [InlineData("oklab(1 0 0)", 1f, 1f, 1f)]
    [InlineData("oklab(0.627955 0.224863 0.125846)", 1f, 0f, 0f)]
    [InlineData("oklab(0.866440 -0.233888 0.179498)", 0f, 1f, 0f)]
    [InlineData("oklab(0.452014 -0.032457 -0.311528)", 0f, 0f, 1f)]
    [InlineData("oklab(0 0 0)", 0f, 0f, 0f)]
    public void Oklab_parses_to_the_linear_colour_its_author_published(string text, float r, float g, float b) {
        // Ottosson's own table, read backwards. `OklabTests` checks linear-in/Oklab-out against these
        // five rows; this checks that the CSS surface reaches the same maths, and it is a separate
        // claim — a parser that mixed up the components, or decoded sRGB on the way out, would pass
        // that test and fail this one.
        AssertLinear(Color(text), r, g, b, 1f);
    }

    [Theory]
    [InlineData("oklch(0.627955 0.257681 29.2338)", 1f, 0f, 0f)]
    [InlineData("oklch(0.866440 0.294827 142.4953)", 0f, 1f, 0f)]
    [InlineData("oklch(0.452014 0.313214 264.0520)", 0f, 0f, 1f)]
    public void Oklch_is_the_same_space_in_polar_form(string text, float r, float g, float b) {
        // The chroma and hue are the polar form of the row above, computed from Ottosson's numbers
        // rather than from this code: C is the hypotenuse of a and b, H is their arctangent. Red's
        // 0.2577 at 29.23° and blue's 0.3132 at 264.05° are the two most-quoted values in the space
        // and are worth recognising on sight.
        AssertLinear(Color(text), r, g, b, 1f);
    }

    [Fact]
    public void The_lightness_percentage_is_against_one_and_the_chroma_percentage_is_against_zero_point_four() {
        // ⚠ The single easiest thing to get wrong in this function, and it produces a palette that is
        // merely *bolder* than intended rather than obviously broken. CSS Color 4 § 9.3.
        AssertLinear(Color("oklch(50% 0 0)"), 0.125f, 0.125f, 0.125f, 1f);
        Assert.Equal(Color("oklch(0.6 0.2 30)"), Color("oklch(60% 50% 30)"));
        Assert.Equal(Color("oklab(0.6 0.2 -0.1)"), Color("oklab(60% 50% -25%)"));
    }

    [Theory]
    [InlineData("oklch(0.7 0.1 90)")]
    [InlineData("oklch(0.7 0.1 90deg)")]
    [InlineData("oklch(0.7 0.1 100grad)")]
    [InlineData("oklch(0.7 0.1 0.25turn)")]
    public void A_hue_is_degrees_with_or_without_a_unit(string text) {
        // ⚠ The bare-number case is not a convenience: every colour Tailwind v4 ships is written
        // `oklch(62.3% 0.214 259.815)`, with no unit at all. A parser that required one would reject
        // the entire default palette.
        var radians = Color("oklch(0.7 0.1 1.5707963rad)");
        AssertLinear(Color(text), radians.R, radians.G, radians.B, 1f);
    }

    /// <summary>A colour encoded to sRGB and rounded to bytes, clamped the way a display would.</summary>
    static string Clamped(string text) {
        var srgb = Color(text).ToSrgb();

        static int Byte(float value) => (int) MathF.Round(Math.Clamp(value, 0f, 1f) * 255f);

        return $"#{Byte(srgb.R):x2}{Byte(srgb.G):x2}{Byte(srgb.B):x2}";
    }

    [Fact]
    public void The_palette_this_exists_for_parses_and_two_thirds_of_it_is_outside_sRGB() {
        // ⚠ **`blue-500`, `red-500` and `emerald-500`, copied out of Tailwind v4's own `theme.css`
        // rather than recalled.** The third oracle, and the only one made of shipped data. It is what
        // doc 43 § D4's claim amounts to: no hex anywhere, a lightness as a percentage, a chroma as a
        // bare number against 0.4, a hue as a bare number of degrees — four places this parser could
        // have been wrong and still produced a colour.
        //
        // ⚠ **And the first version of this test asserted all three were inside sRGB, which is
        // false.** They are not, and the reason the mistake survived being "checked" is worth having
        // written down: the reference implementation it was checked against clamped its own output
        // before printing it, so the two agreed on exactly the numbers the clamp had already
        // decided. Two of the three overflow —
        //
        //   blue-500     linear blue  +1.0527, which encodes to 1.023 — past white
        //   emerald-500  linear red   -0.0385, which encodes to -0.498 — past black
        //   red-500      in gamut, and the only one of the three that is
        //
        // — so **the gamut question is not academic for the palette this engine is adopting**: it is
        // load-bearing for two colours in three, before anyone writes a vivid one by hand. That is an
        // argument for doing § D4 properly against the display's gamut, and an argument against
        // clamping here, where the information it needs would already be gone.
        var blue = Color("oklch(62.3% 0.214 259.815)").ToSrgb();
        var red = Color("oklch(63.7% 0.237 25.331)").ToSrgb();
        var emerald = Color("oklch(69.6% 0.17 162.48)").ToSrgb();

        Assert.True(blue.B > 1f, $"blue-500 is outside sRGB, got {blue.B}");
        Assert.True(emerald.R < 0f, $"emerald-500 is outside sRGB, got {emerald.R}");

        Assert.InRange(red.R, 0f, 1f);
        Assert.InRange(red.G, 0f, 1f);
        Assert.InRange(red.B, 0f, 1f);

        // Clamped — which is what a display does and what v4's own generated sRGB fallbacks are —
        // all three land on the bytes an independent transcription of Ottosson's inverse matrices
        // gives. So the overflow is in the colours and not in this code.
        Assert.Equal("#2b7fff", Clamped("oklch(62.3% 0.214 259.815)"));
        Assert.Equal("#fb2c36", Clamped("oklch(63.7% 0.237 25.331)"));
        Assert.Equal("#00bc7d", Clamped("oklch(69.6% 0.17 162.48)"));
    }

    [Fact]
    public void Alpha_is_read_off_the_slash_in_either_notation() {
        AssertLinear(Color("oklch(0.5 0 0 / 0.4)"), 0.125f, 0.125f, 0.125f, 0.4f);
        AssertLinear(Color("oklch(0.5 0 0 / 40%)"), 0.125f, 0.125f, 0.125f, 0.4f);
        AssertLinear(Color("oklab(0.5 0 0 / 40%)"), 0.125f, 0.125f, 0.125f, 0.4f);
    }

    [Fact]
    public void A_missing_component_is_zero() {
        // CSS Color 4 § 4.4. Vixen carries no missing-component model, so `none` takes the value the
        // specification says a missing component behaves as in every context that has none.
        Assert.Equal(Color("oklch(0.5 0 0)"), Color("oklch(0.5 none none)"));
        Assert.Equal(Color("oklab(0.5 0 0)"), Color("oklab(0.5 none none)"));
    }

    [Fact]
    public void A_negative_chroma_is_folded_to_zero_and_a_negative_hue_is_not() {
        // Only one of the two has a meaningless side. -30° is 330°, and clamping it would silently
        // move the colour a third of the way round the wheel.
        Assert.Equal(Color("oklch(0.5 0 0)"), Color("oklch(0.5 -0.2 40)"));
        Assert.Equal(Color("oklch(0.6 0.1 330)"), Color("oklch(0.6 0.1 -30)"));
    }

    [Theory]
    [InlineData("oklch(0.5 0.1)")]
    [InlineData("oklch(0.5 0.1 20 / 0.5 0.5)")]
    [InlineData("oklch(0.5 0.1 nonsense)")]
    [InlineData("oklab(0.5 0.1 red)")]
    [InlineData("oklch(0.5 0.1 20rpm)")]
    public void A_malformed_colour_function_is_unknown_rather_than_a_guess(string text) => AssertUnknown(text);

    // ---------------------------------------------------------------- out of gamut

    [Fact]
    public void An_out_of_gamut_colour_is_carried_through_rather_than_clamped() {
        // ⚠ The interim behaviour, and the reason it is safe to change: `oklch(0.7 0.4 30)` is well
        // outside sRGB and its linear blue channel is negative. Clipping per channel here would shift
        // the hue towards orange *and* destroy the chroma that the real repair — reduce chroma, hold
        // lightness and hue, against the gamut of the display — needs in order to run at all. Carrying
        // it means doc 43 § D4 can be implemented downstream with no change to this file.
        var wide = Color("oklch(0.7 0.4 30)");

        Assert.True(wide.B < 0f, $"expected a negative blue channel outside sRGB, got {wide.B}");
        Assert.False(float.IsNaN(wide.R) || float.IsNaN(wide.G) || float.IsNaN(wide.B), "no channel is NaN");

        // And it survives being written back out, because the piecewise sRGB transfer function's
        // linear segment handles negatives — a `pow()` approximation there would give NaN, which is
        // how "carry it unclamped" turns into a black element three layers downstream.
        var written = wide.ToSrgb();
        Assert.False(float.IsNaN(written.R) || float.IsNaN(written.G) || float.IsNaN(written.B), "no NaN out");
        Assert.True(written.B < 0f, $"still out of gamut after encoding, got {written.B}");
    }

    // ---------------------------------------------------------------- color-mix: the space

    [Fact]
    public void The_interpolation_space_is_honoured_and_the_three_give_three_answers() {
        // The whole point of the keyword, in one assertion. Halfway from white to black is:
        //   in srgb        — encoded 0.5, which decodes to about 0.214;
        //   in srgb-linear — 0.5;
        //   in oklab       — lightness 0.5, which is linear 0.125 exactly, since with no chroma the
        //                    cube roots collapse and the inverse matrix's first row sums to one.
        // Three numbers a long way apart, none of them computed by the code under test.
        AssertLinear(Color("color-mix(in srgb, white, black)"), 0.2140f, 0.2140f, 0.2140f, 1f);
        AssertLinear(Color("color-mix(in srgb-linear, white, black)"), 0.5f, 0.5f, 0.5f, 1f);
        AssertLinear(Color("color-mix(in oklab, white, black)"), 0.125f, 0.125f, 0.125f, 1f);
        AssertLinear(Color("color-mix(in oklch, white, black)"), 0.125f, 0.125f, 0.125f, 1f);
    }

    [Fact]
    public void An_unrecognised_space_is_refused_rather_than_quietly_replaced() {
        // ⚠ CSS has a dozen spaces and Vixen has the conversions for four. Accepting `in lab` and
        // interpolating in Oklab instead would be the exact failure this programme is about: a
        // keyword that is read, ignored, and produces a plausible wrong colour.
        AssertUnknown("color-mix(in lab, white, black)");
        AssertUnknown("color-mix(in hsl, white, black)");
        AssertUnknown("color-mix(in xyz, white, black)");
        AssertUnknown("color-mix(oklab, white, black)");
    }

    /// <summary>The midpoint hue each method produces, as the colour it lands on.</summary>
    static void AssertMidpointHue(string method, float from, float to, float hue) {
        var mix = $"color-mix(in oklch{method}, oklch(0.7 0.1 {from}), oklch(0.7 0.1 {to}))";
        var landed = Color($"oklch(0.7 0.1 {hue})");

        AssertLinear(Color(mix), landed.R, landed.G, landed.B, 1f);
    }

    [Fact]
    public void The_four_hue_methods_pick_four_different_arcs() {
        // ⚠ **Ten and thirty degrees, deliberately.** The first version of this test used 10° and
        // 350°, which reads as the more searching pair and is in fact the one case where `longer`
        // needs no adjustment at all — the two ends are already 340° apart, so deleting both of the
        // `longer` branches outright left the test passing. Twenty degrees apart is the case where
        // every method has to do something, and it separates all four:
        //
        //   from → to      shorter   longer   increasing   decreasing
        //   10 → 30           20       200        20          200
        //   30 → 10           20       200       200           20
        //
        // The pair of columns that swap under reversal is what distinguishes `increasing` and
        // `decreasing` from the two symmetric methods, and nothing else in this file does.
        AssertMidpointHue(" shorter hue", 10f, 30f, 20f);
        AssertMidpointHue(" longer hue", 10f, 30f, 200f);
        AssertMidpointHue(" increasing hue", 10f, 30f, 20f);
        AssertMidpointHue(" decreasing hue", 10f, 30f, 200f);

        AssertMidpointHue(" shorter hue", 30f, 10f, 20f);
        AssertMidpointHue(" longer hue", 30f, 10f, 200f);
        AssertMidpointHue(" increasing hue", 30f, 10f, 200f);
        AssertMidpointHue(" decreasing hue", 30f, 10f, 20f);

        // Shorter is the default, and it is the one that crosses zero rather than travelling 340°.
        AssertMidpointHue("", 10f, 30f, 20f);
        AssertMidpointHue("", 10f, 350f, 0f);
    }

    [Fact]
    public void A_hue_method_belongs_to_a_polar_space_and_nowhere_else() {
        // ⚠ A syntax error rather than an ignored word, so that `in oklab longer hue` cannot look
        // supported — it would parse, mean exactly what `in oklab` means, and give someone a reason
        // to believe the arc was honoured.
        AssertUnknown("color-mix(in oklab longer hue, white, black)");
        AssertUnknown("color-mix(in srgb shorter hue, white, black)");
        AssertUnknown("color-mix(in oklch longer, white, black)");
        AssertUnknown("color-mix(in oklch sideways hue, white, black)");
    }

    // ---------------------------------------------------------------- color-mix: the percentages

    [Fact]
    public void One_percentage_omitted_means_the_other_is_its_complement() {
        // Case one of three. CSS Values 5, step 2: what is left of 100% goes to the one that said
        // nothing.
        Assert.Equal(
            Color("color-mix(in oklab, white 30%, black 70%)"),
            Color("color-mix(in oklab, white 30%, black)")
        );

        Assert.Equal(
            Color("color-mix(in oklab, white 30%, black 70%)"),
            Color("color-mix(in oklab, white, black 70%)")
        );

        // Both omitted is 50/50, which needs no rule of its own — step 2 divides the whole 100%
        // between the two of them.
        Assert.Equal(Color("color-mix(in oklab, white 50%, black 50%)"), Color("color-mix(in oklab, white, black)"));
    }

    [Fact]
    public void Two_percentages_that_do_not_sum_to_a_hundred_are_scaled_and_the_shortfall_becomes_alpha() {
        // ⚠ Case two, and the one an implementation gets wrong by being reasonable. `white 20%,
        // black 60%` is *not* a 20/80 mix and *not* an opaque 25/75 one: steps 4 and 5 scale the
        // weights to 25/75 and keep the missing 20 points as `leftover`, which multiplies the
        // result's alpha. The specification's own worked example.
        var under = Color("color-mix(in oklab, white 20%, black 60%)");
        var opaque = Color("color-mix(in oklab, white 25%, black 75%)");

        AssertLinear(under, opaque.R, opaque.G, opaque.B, 0.8f);

        // Over 100% there is no shortfall, so the weights scale and the alpha does not.
        var over = Color("color-mix(in oklab, white 60%, black 60%)");
        AssertLinear(over, 0.125f, 0.125f, 0.125f, 1f);

        // And a percentage above 100 is clamped into range rather than refused, per the grammar's
        // `<percentage [0,100]>`.
        Assert.Equal(Color("color-mix(in oklab, white 100%, black 0%)"), Color("color-mix(in oklab, white 140%, black 0%)"));
    }

    [Fact]
    public void Two_percentages_summing_to_zero_are_transparent_black_and_not_invalid() {
        // ⚠ Case three, and the brief this was written from said "invalid". An older CSS Color 5
        // draft did say so and the claim is still widely repeated; the current one produces
        // transparent black — `oklch(0% 0 none / 0)` in its own example — and no code is needed to
        // make it do so. Step 4 does not fire because the total is not above zero, step 5 makes the
        // leftover 100% and so the alpha multiplier zero, and two zero weights give zero components.
        var value = Parser().Parse("color-mix(in oklab, red 0%, blue 0%)");

        Assert.Equal(StyleValueKind.Color, value.Kind);
        AssertLinear(value.Color, 0f, 0f, 0f, 0f);
    }

    [Fact]
    public void A_negative_percentage_is_refused() {
        // Explicitly disallowed by CSS Color 5 § 3, and it has to be: the normalisation divides by
        // the sum, so a negative weight makes a mix that leaves the segment between its endpoints.
        AssertUnknown("color-mix(in oklab, red -10%, blue 60%)");
    }

    [Fact]
    public void The_percentage_may_sit_on_either_side_of_the_colour() {
        // `<color> && <percentage>?`, and `&&` is order-free.
        Assert.Equal(
            Color("color-mix(in oklab, white 30%, black 70%)"),
            Color("color-mix(in oklab, 30% white, 70% black)")
        );
    }

    // ---------------------------------------------------------------- color-mix: premultiplied alpha

    [Fact]
    public void Mixing_with_transparent_keeps_the_colour_and_moves_only_the_alpha() {
        // ⚠ The whole reason `color-mix()` is the right answer to the opacity modifier, and it works
        // only because the components are premultiplied by alpha before interpolating and divided
        // back out afterwards — CSS Color 4 § 12.3. Without that, the transparent end contributes a
        // weighted *black* rather than a weighted nothing, and `blue 50%` comes out a dark blue at
        // half alpha: invisible on a dark background, obvious on a light one.
        var half = Color("color-mix(in oklab, #4f7cff 50%, transparent)");
        var opaque = Color("#4f7cff");

        AssertLinear(half, opaque.R, opaque.G, opaque.B, 0.5f);

        // Which makes it exactly what the old `rgba()` rewrite produced for a hex colour — the point
        // being that the utility layer can switch to emitting a mix without moving any colour that
        // already worked.
        AssertLinear(half, Color("rgba(79, 124, 255, 0.5)").R, Color("rgba(79, 124, 255, 0.5)").G, Color("rgba(79, 124, 255, 0.5)").B, 0.5f);
    }

    [Fact]
    public void The_transparent_end_of_an_oklch_mix_does_drag_the_hue() {
        // ⚠ Not a bug, and the reason the opacity modifier must name the rectangular space. A hue is
        // an angle with no zero to scale towards, so CSS leaves it out of the premultiplication —
        // and `transparent` is `rgb(0 0 0 / 0)`, whose hue is 0°. Blue sits at 264°, so the mix lands
        // near 312° and comes out purple. Browsers agree; this test exists so that anyone who
        // "fixes" it finds out it was deliberate.
        var rectangular = Color("color-mix(in oklab, blue 50%, transparent)");
        var polar = Color("color-mix(in oklch, blue 50%, transparent)");

        AssertLinear(rectangular, Color("blue").R, Color("blue").G, Color("blue").B, 0.5f);
        Assert.True(polar.R > rectangular.R + 0.05f, $"expected the polar mix to redden, got {polar.R} vs {rectangular.R}");
    }

    // ---------------------------------------------------------------- color-mix: what the endpoints may be

    [Fact]
    public void An_endpoint_may_be_written_any_way_the_parser_understands() {
        // ⚠ Not a completeness exercise. ExCSS normalises `red` to `rgb(255, 0, 0)` only where it
        // recognises the value, and it does not recognise `color-mix()` — so the endpoints arrive
        // spelt exactly as the author wrote them, and `var()` substitution hands over a hex triple.
        // A mix that accepted only `rgb()` would have failed on every real one.
        var expected = Color("color-mix(in oklab, rgb(255, 0, 0) 50%, rgb(0, 0, 255) 50%)");

        Assert.Equal(expected, Color("color-mix(in oklab, red 50%, blue 50%)"));
        Assert.Equal(expected, Color("color-mix(in oklab, #ff0000 50%, #0000ff 50%)"));
        Assert.Equal(expected, Color("color-mix(in oklab, #f00 50%, #00f 50%)"));

        AssertLinear(
            Color("color-mix(in oklab, oklab(0.627955 0.224863 0.125846), transparent)"),
            1f,
            0f,
            0f,
            0.5f
        );
    }

    [Fact]
    public void A_mix_may_hold_a_mix() {
        // Falls out of parsing the endpoints recursively, and worth pinning: the depth-aware splitter
        // is what makes the inner commas belong to the inner function.
        Assert.Equal(
            Color("color-mix(in srgb-linear, white 25%, black 75%)"),
            Color("color-mix(in srgb-linear, color-mix(in srgb-linear, white 50%, black 50%) 50%, black 50%)")
        );
    }

    [Fact]
    public void The_interpolation_method_may_be_left_out() {
        // Optional in the current CSS Color 5 grammar, defaulting to Oklab.
        Assert.Equal(Color("color-mix(in oklab, white, black)"), Color("color-mix(white, black)"));
    }

    // ---------------------------------------------------------------- against the real cascade

    [Fact]
    public void The_mix_is_evaluated_after_var_substitution_and_never_sees_the_variable() {
        // ⚠ **The order of operations, proved rather than reasoned about, because it is the one place
        // this was most likely to be silently wrong.** Two rules that differ only in whether the
        // first endpoint is written out or held in a custom property. If substitution ran *after*
        // the value were parsed, the second would reach `StyleValueParser` still containing `var(`
        // and would have to be understood there; if it ran before, the two are the same text by the
        // time anything looks at them.
        //
        // They are the same text. `StyleResolver.Substitute` runs inside `Build`, over the resolved
        // pairs, and re-interns the substituted result — and `StyleValueParser.Parse` only ever runs
        // on what a `ComputedStyle` ended up holding. So `color-mix()` needs no notion of `var()`,
        // and the one thing it does need is visible in the assertion below: what substitution hands
        // over is `#4f7cff`, not `rgb(79, 124, 255)`. ExCSS normalises only what it could see, and
        // it cannot see into a custom property — so a mix that accepted only `rgb()` endpoints would
        // have worked on every literal and failed on every variable.
        var fixture = new CascadeFixture();
        fixture.Load(
            """
            .written { color: color-mix(in oklab, #4f7cff 50%, transparent); }
            .held    { --accent: #4f7cff; color: color-mix(in oklab, var(--accent) 50%, transparent); }
            """
        );

        var written = fixture.Value(fixture.Tree.CreateElement("div", classNames: ["written"]));
        var held = fixture.Value(fixture.Tree.CreateElement("div", classNames: ["held"]));

        Assert.Equal("color-mix(in oklab, #4f7cff 50%, transparent)", written);
        Assert.Equal(written, held);

        // And the text both of them arrive as is a colour, at half alpha, with the hue intact.
        AssertLinear(Color(held!), Color("#4f7cff").R, Color("#4f7cff").G, Color("#4f7cff").B, 0.5f);
    }

    [Fact]
    public void Excss_leaves_a_colour_function_it_does_not_know_exactly_as_written() {
        // The other half of the same fact, and the reason none of this needed a change to the
        // stylesheet loader. ⚠ Note the third row: inside `color-mix()` the endpoints are *not*
        // normalised either, so `red` reaches the parser as `red` where `color: red` would have
        // reached it as `rgb(255, 0, 0)`.
        var fixture = new CascadeFixture();
        fixture.Load(
            """
            .a { color: oklch(62.3% 0.214 259.815); }
            .b { color: red; }
            .c { background-color: color-mix(in srgb, red 50%, blue); }
            """
        );

        Assert.Equal(
            "oklch(62.3% 0.214 259.815)",
            fixture.Value(fixture.Tree.CreateElement("div", classNames: ["a"]))
        );

        Assert.Equal("rgb(255, 0, 0)", fixture.Value(fixture.Tree.CreateElement("div", classNames: ["b"])));

        Assert.Equal(
            "color-mix(in srgb, red 50%, blue)",
            fixture.Value(fixture.Tree.CreateElement("div", classNames: ["c"]), "background-color")
        );
    }

    [Theory]
    [InlineData("color-mix(in oklab, red)")]
    [InlineData("color-mix(in oklab, red, blue, green)")]
    [InlineData("color-mix(in oklab, 50%, blue)")]
    [InlineData("color-mix(in oklab, red 50% 20%, blue)")]
    [InlineData("color-mix(in oklab, notacolour 50%, blue)")]
    [InlineData("color-mix(in oklab, red 50%, 12px)")]
    public void A_malformed_mix_is_unknown_rather_than_a_guess(string text) => AssertUnknown(text);
}
