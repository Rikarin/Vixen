// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Styling.Utilities.Tests;

/// <summary>
///     The utility-family gate [doc 14](../../docs/plan/14-roadmap.md) names for 4b: one test per
///     family, saying what each one emits.
/// </summary>
public class UtilityFamilyTests {
    [Theory]
    // Layout.
    [InlineData("flex", "display: flex")]
    [InlineData("hidden", "display: none")]
    [InlineData("grid", "display: grid")]
    [InlineData("flex-col", "flex-direction: column")]
    [InlineData("flex-wrap", "flex-wrap: wrap")]
    [InlineData("items-center", "align-items: center")]
    [InlineData("justify-between", "justify-content: space-between")]
    [InlineData("self-start", "align-self: flex-start")]
    // Flex and grid.
    [InlineData("grow-0", "flex-grow: 0")]
    [InlineData("shrink", "flex-shrink: 1")]
    [InlineData("order-2", "order: 2")]
    // Spacing, against the theme's base of 4.
    [InlineData("p-4", "padding: 16px")]
    [InlineData("p-0", "padding: 0px")]
    [InlineData("p-px", "padding: 1px")]
    [InlineData("m-2", "margin: 8px")]
    [InlineData("gap-2", "gap: 8px")]
    [InlineData("mt-1", "margin-top: 4px")]
    // Sizing.
    [InlineData("w-full", "width: 100%")]
    [InlineData("w-4", "width: 16px")]
    [InlineData("h-auto", "height: auto")]
    [InlineData("min-w-0", "min-width: 0px")]
    [InlineData("max-h-full", "max-height: 100%")]
    // Position.
    [InlineData("absolute", "position: absolute")]
    [InlineData("z-10", "z-index: 10")]
    [InlineData("top-0", "top: 0px")]
    // Typography.
    [InlineData("text-lg", "font-size: 17px|line-height: 24px")]
    [InlineData("text-center", "text-align: center")]
    [InlineData("font-semibold", "font-weight: 600")]
    [InlineData("leading-5", "line-height: 20px")]
    [InlineData("whitespace-nowrap", "white-space: nowrap")]
    // Colours.
    [InlineData("bg-surface-2", "background-color: #17171d")]
    [InlineData("bg-accent", "background-color: #4f7cff")]
    [InlineData("bg-accent-hover", "background-color: #6a91ff")]
    [InlineData("text-muted", "color: #8a8a99")]
    // Borders.
    [InlineData("rounded-md", "border-radius: 4px")]
    [InlineData("border-accent", "border-color: #4f7cff")]
    // Effects.
    [InlineData("opacity-50", "opacity: 0.5")]
    // Transitions.
    [InlineData("duration-200", "transition-duration: 200ms")]
    [InlineData("ease-in-out", "transition-timing-function: ease-in-out")]
    // Interactivity.
    [InlineData("cursor-pointer", "cursor: pointer")]
    [InlineData("select-none", "user-select: none")]
    [InlineData("overflow-hidden", "overflow: hidden")]
    [InlineData("pointer-events-none", "pointer-events: none")]
    // Aspect.
    [InlineData("aspect-1.5", "aspect-ratio: 1.5")]
    public void Each_family_emits_what_it_says(string candidate, string expected) {
        var fixture = new UtilityFixture();
        Assert.Equal(expected.Split('|'), fixture.Emits(candidate));
    }

    [Fact]
    public void The_spacing_scale_is_arithmetic_so_twice_the_number_is_twice_the_space() {
        // The reason spacing is one base number rather than a table of named steps. `p-md` reads
        // better in a design tool and worse in a stylesheet, because nothing about it says whether
        // it is bigger than `p-sm` by a little or a lot.
        var fixture = new UtilityFixture();

        Assert.Equal(["padding: 8px"], fixture.Emits("p-2"));
        Assert.Equal(["padding: 16px"], fixture.Emits("p-4"));
        Assert.Equal(["padding: 32px"], fixture.Emits("p-8"));
    }

    [Fact]
    public void The_longest_family_name_wins_and_not_the_first_hyphen() {
        // `p` and `pointer-events` both exist. A first-hyphen split reads the second as a family
        // called `pointer` with the value `events-none`, and quietly produces nothing.
        var fixture = new UtilityFixture();

        Assert.Equal(["pointer-events: none"], fixture.Emits("pointer-events-none"));
        Assert.Equal(["min-width: 0px"], fixture.Emits("min-w-0"));
        Assert.Equal(["column-gap: 8px"], fixture.Emits("gap-x-2"));
    }

    [Fact]
    public void One_prefix_can_mean_three_properties_and_the_order_is_documented() {
        // `text-` is alignment, then font size, then colour. Stated as a test because the
        // consequence is real: a colour named `center` would be unreachable through `text-`, and
        // that is a price worth paying for both `text-lg` and `text-accent` reading right.
        var fixture = new UtilityFixture();

        Assert.Equal(["text-align: center"], fixture.Emits("text-center"));
        Assert.Equal(["font-size: 17px", "line-height: 24px"], fixture.Emits("text-lg"));
        Assert.Equal(["color: #8a8a99"], fixture.Emits("text-muted"));
    }

    [Fact]
    public void A_bare_border_is_a_width_and_a_named_one_is_a_colour() {
        var fixture = new UtilityFixture();

        Assert.Equal(["border-width: 1px"], fixture.Emits("border"));
        Assert.Equal(["border-color: #4f7cff"], fixture.Emits("border-accent"));
    }

    [Fact]
    public void A_fraction_is_a_percentage_and_not_an_opacity() {
        // `w-2/3` and `bg-accent/50` use the same character for completely different things, which
        // is why the parser keeps the suffix as written as well as reading it as an opacity.
        var fixture = new UtilityFixture();

        Assert.Equal(["width: 50%"], fixture.Emits("w-1/2"));
        Assert.Equal(["width: 66.6667%"], fixture.Emits("w-2/3"));
        Assert.Equal(["background-color: rgba(79, 124, 255, 0.5)"], fixture.Emits("bg-accent/50"));
    }

    [Fact]
    public void An_arbitrary_value_goes_straight_through() {
        // The escape hatch exists precisely for what the token scale does not cover, so
        // second-guessing it would make it useless.
        var fixture = new UtilityFixture();

        Assert.Equal(["width: 37px"], fixture.Emits("w-[37px]"));
        Assert.Equal(["background-color: #ff0000"], fixture.Emits("bg-[#ff0000]"));
        Assert.Equal(["padding: 3.5rem"], fixture.Emits("p-[3.5rem]"));
    }

    [Fact]
    public void An_underscore_in_an_arbitrary_value_is_a_space() {
        // A class attribute cannot contain a space, and `grid-cols-[1fr_auto]` has to say two
        // things. Tailwind's convention, and the only workable one.
        var fixture = new UtilityFixture();

        Assert.Equal(["grid-template-columns: 1fr auto"], fixture.Emits("grid-cols-[1fr_auto]"));
    }

    [Fact]
    public void A_candidate_that_is_not_a_utility_is_simply_not_one() {
        // Scanning is over-inclusive by design, so most of what reaches here is ordinary prose.
        // Failing has to be quiet, or the build drowns in warnings about the word "container".
        var fixture = new UtilityFixture();

        Assert.Null(fixture.Declarations("definitely-not-a-utility"));
        Assert.Null(fixture.Declarations("p-notanumber"));
        Assert.Null(fixture.Declarations("bg-nosuchcolour"));
    }
}
