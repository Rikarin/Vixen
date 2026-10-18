// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Styling.Utilities.Tests;

/// <summary>An arbitrary value's math functions get the operator spacing a class name cannot hold (#1467).</summary>
/// <remarks>
///     <para>
///         ⚠ <b>CSS Values 4 requires whitespace around <c>+</c> and <c>-</c> in <c>calc()</c>, and a
///         class attribute cannot contain whitespace</b>, so Tailwind v4's arbitrary-value decoder
///         puts it back: <c>mt-[calc(1px+2px)]</c> emits <c>calc(1px + 2px)</c>. Vixen emitted the
///         value verbatim, <c>StyleValueParser</c> rightly refused <c>calc(1px+2px)</c>, and the class
///         resolved, emitted and computed to nothing.
///     </para>
///     <para>
///         The half that matters as much is what is <i>not</i> spaced: a sign (<c>1px+-2px</c>,
///         <c>calc(-1*x)</c>), an exponent (<c>1e-3</c>), a hyphen in a custom property's name
///         (<c>var(--my-gap)</c>), and anything outside a math function.
///     </para>
/// </remarks>
public class ArbitraryMathSpacingTests {
    /// <summary>The decoded arbitrary value, as Tailwind v4's decoder writes it.</summary>
    [Theory]
    [InlineData("mt-[calc(1px+2px)]", "calc(1px + 2px)")]
    [InlineData("mx-[calc(100%-2px)]", "calc(100% - 2px)")]
    [InlineData("w-[calc(100%-2rem)]", "calc(100% - 2rem)")]
    [InlineData("w-[calc(2*3px)]", "calc(2 * 3px)")]
    [InlineData("w-[calc(10px/2)]", "calc(10px / 2)")]
    [InlineData("w-[calc(1px+-2px)]", "calc(1px + -2px)")]
    [InlineData("w-[calc(var(--a)-1px)]", "calc(var(--a) - 1px)")]
    [InlineData("w-[calc(var(--my-gap)*2)]", "calc(var(--my-gap) * 2)")]
    [InlineData("w-[calc(var(--gap-2)*2)]", "calc(var(--gap-2) * 2)")]
    [InlineData("w-[calc(1e-3*2px)]", "calc(1e-3 * 2px)")]
    [InlineData("w-[calc(-1*var(--x))]", "calc(-1 * var(--x))")]
    [InlineData("w-[calc((1px+2px)*3)]", "calc((1px + 2px) * 3)")]
    [InlineData("w-[min(10px,calc(100%-4px))]", "min(10px, calc(100% - 4px))")]
    [InlineData("w-[clamp(1rem,2vw+1rem,3rem)]", "clamp(1rem, 2vw + 1rem, 3rem)")]
    [InlineData("mt-[calc(1px_+_2px)]", "calc(1px + 2px)")]
    [InlineData("[margin-top:calc(1px+2px)]", "calc(1px + 2px)")]
    public void A_math_function_is_spaced_the_way_tailwind_spaces_it(string candidate, string arbitrary) {
        Assert.True(UtilityParser.TryParse(candidate, out var parsed));

        Assert.Equal(arbitrary, parsed.Arbitrary, StringComparer.Ordinal);
    }

    /// <summary>A value with no math function in it is left exactly as it was written.</summary>
    [Theory]
    [InlineData("grid-cols-[repeat(3,1fr)]", "repeat(3,1fr)")]
    [InlineData("bg-[var(--brand-color)]", "var(--brand-color)")]
    [InlineData("w-[var(--a-b)]", "var(--a-b)")]
    [InlineData("bg-[linear-gradient(to_right,red,blue)]", "linear-gradient(to right,red,blue)")]
    [InlineData("w-[-1px]", "-1px")]
    public void A_value_outside_a_math_function_is_untouched(string candidate, string arbitrary) {
        Assert.True(UtilityParser.TryParse(candidate, out var parsed));

        Assert.Equal(arbitrary, parsed.Arbitrary, StringComparer.Ordinal);
    }

    /// <summary>The emitted declaration carries the spaced value.</summary>
    [Fact]
    public void The_declaration_carries_the_spaced_value() {
        var fixture = new UtilityFixture();

        Assert.Equal(["margin-top: calc(1px + 2px)"], fixture.Emits("mt-[calc(1px+2px)]"));
        Assert.Equal(
            ["margin-left: calc(100% - 2px)", "margin-right: calc(100% - 2px)"],
            fixture.Emits("mx-[calc(100%-2px)]")
        );
    }

    /// <summary>
    ///     ⚠ <b>The class computes to three pixels</b>, which is the claim: before #1467 it computed to
    ///     a value no reader could fold, and the margin was nothing.
    /// </summary>
    [Theory]
    [InlineData("mt-[calc(1px+2px)]")]
    [InlineData("[margin-top:calc(1px+2px)]")]
    [InlineData("mt-[calc(5px+-2px)]")]
    public void The_margin_computes_to_three_pixels(string candidate) {
        var fixture = new UtilityFixture();

        var computed = fixture.Computed([candidate], "margin-top");
        Assert.NotNull(computed);

        var value = new StyleValueParser(new NameTable(), new NameTable()).Parse(computed);

        Assert.Equal(StyleValueKind.Length, value.Kind);
        Assert.Equal(StyleUnit.Pixels, value.Unit);
        Assert.Equal(3f, value.Number, 0.001f);
    }

    /// <summary>A breakpoint variant's arbitrary width is decoded the same way.</summary>
    [Fact]
    public void A_variant_width_is_spaced_too() {
        var css = new UtilityFixture().Generate("min-[calc(40rem+1px)]:mt-1");

        Assert.Contains("calc(40rem + 1px)", css, StringComparison.Ordinal);
    }
}
