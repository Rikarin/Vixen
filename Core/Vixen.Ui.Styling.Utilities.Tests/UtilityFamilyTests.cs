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
    [InlineData("flex-1", "flex: 1 1 0%")]
    [InlineData("flex-auto", "flex: 1 1 auto")]
    [InlineData("flex-initial", "flex: 0 1 auto")]
    [InlineData("flex-none", "flex: none")]
    // Spacing, against the theme's base of 4.
    [InlineData("p-4", "padding: 16px")]
    [InlineData("p-0", "padding: 0px")]
    [InlineData("p-px", "padding: 1px")]
    [InlineData("m-2", "margin: 8px")]
    [InlineData("gap-2", "gap: 8px")]
    [InlineData("mt-1", "margin-top: 4px")]
    [InlineData("ps-2", "padding-inline-start: 8px")]
    [InlineData("pe-2", "padding-inline-end: 8px")]
    [InlineData("ms-1", "margin-inline-start: 4px")]
    [InlineData("me-1", "margin-inline-end: 4px")]
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
    [InlineData("start-0", "inset-inline-start: 0px")]
    [InlineData("end-2", "inset-inline-end: 8px")]
    [InlineData("box-border", "box-sizing: border-box")]
    [InlineData("box-content", "box-sizing: content-box")]
    // Typography.
    [InlineData("text-lg", "font-size: 17px|line-height: 24px")]
    [InlineData("text-center", "text-align: center")]
    [InlineData("font-semibold", "font-weight: 600")]
    [InlineData("leading-5", "line-height: 20px")]
    [InlineData("leading-none", "line-height: 1")]
    [InlineData("leading-relaxed", "line-height: 1.625")]
    [InlineData("whitespace-nowrap", "white-space: nowrap")]
    // Colours.
    [InlineData("bg-surface-2", "background-color: #17171d")]
    [InlineData("bg-accent", "background-color: #4f7cff")]
    [InlineData("bg-accent-hover", "background-color: #6a91ff")]
    [InlineData("text-muted", "color: #8a8a99")]
    // Borders.
    [InlineData("rounded-md", "border-radius: 4px")]
    [InlineData("border-accent", "border-color: #4f7cff")]
    [InlineData("border-2", "border-width: 2px")]
    [InlineData("border-t", "border-top-width: 1px")]
    [InlineData("border-b-2", "border-bottom-width: 2px")]
    [InlineData("border-x", "border-left-width: 1px|border-right-width: 1px")]
    [InlineData("border-y-4", "border-top-width: 4px|border-bottom-width: 4px")]
    [InlineData("border-s-2", "border-inline-start-width: 2px")]
    [InlineData("border-t-accent", "border-top-color: #4f7cff")]
    // Effects.
    [InlineData("opacity-50", "opacity: 0.5")]
    [InlineData("shadow", "box-shadow: 0px 1px 2px rgba(0, 0, 0, 0.3)")]
    [InlineData("shadow-lg", "box-shadow: 0px 8px 24px rgba(0, 0, 0, 0.45)")]
    [InlineData("shadow-none", "box-shadow: none")]
    // Transitions.
    [InlineData("duration-200", "transition-duration: 200ms")]
    [InlineData("ease-in-out", "transition-timing-function: ease-in-out")]
    // Interactivity.
    [InlineData("cursor-pointer", "cursor: pointer")]
    [InlineData("cursor-grabbing", "cursor: grabbing")]
    [InlineData("cursor-col-resize", "cursor: col-resize")]
    [InlineData("text-start", "text-align: start")]
    [InlineData("text-end", "text-align: end")]
    [InlineData("select-none", "user-select: none")]
    [InlineData("overflow-hidden", "overflow: hidden")]
    // ⚠ The per-axis pair, which the engine reads as of the clip stack learning two axes. It emitted
    // exactly this before and nothing interned either name, so the class resolved and the picture did
    // not change — the worst shape a utility can have. See `Vixen.Ui.OverflowReader`.
    [InlineData("overflow-y-auto", "overflow-y: auto")]
    [InlineData("overflow-x-scroll", "overflow-x: scroll")]
    [InlineData("pointer-events-none", "pointer-events: none")]
    // Aspect.
    [InlineData("aspect-1.5", "aspect-ratio: 1.5")]
    [InlineData("aspect-square", "aspect-ratio: 1 / 1")]
    [InlineData("aspect-video", "aspect-ratio: 16 / 9")]
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
    public void The_name_has_to_end_at_a_hyphen_or_the_short_families_would_eat_the_long_ones() {
        // `p` and `ps` both exist, and so do `m`/`me` and `border`/`border-t`/`border-s`. Requiring
        // the hyphen is what keeps `p-4` from being read as the family `ps`, and — the case that
        // actually bites — leaves a colour called `surface-2` reachable through `border-`, whose
        // front the family `border-s` would otherwise claim.
        var fixture = new UtilityFixture();

        Assert.Equal(["padding: 16px"], fixture.Emits("p-4"));
        Assert.Equal(["padding-inline-start: 16px"], fixture.Emits("ps-4"));
        Assert.Equal(["margin: 8px"], fixture.Emits("m-2"));
        Assert.Equal(["margin-inline-end: 8px"], fixture.Emits("me-2"));
        Assert.Equal(["border-color: #17171d"], fixture.Emits("border-surface-2"));
        Assert.Equal(["border-inline-start-width: 1px"], fixture.Emits("border-s"));
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
    public void A_numbered_border_is_a_width_and_a_named_one_is_a_colour_on_every_edge() {
        // The same one-prefix-three-properties problem `text-` has, and it applies nine times over
        // because there is an edge family for each side. Unlike `text-` the order costs nothing: no
        // colour is plausibly named `2`, so reading a number as a width shadows nothing reachable.
        var fixture = new UtilityFixture();

        Assert.Equal(["border-top-width: 1px"], fixture.Emits("border-t"));
        Assert.Equal(["border-top-width: 2px"], fixture.Emits("border-t-2"));
        Assert.Equal(["border-top-color: #4f7cff"], fixture.Emits("border-t-accent"));
    }

    [Fact]
    public void A_border_width_is_pixels_where_padding_is_spacing_steps() {
        // Worth stating because the two read identically and mean different scales. A border is a
        // hairline or it is not; scaling it with the spacing base would mean a theme with a larger
        // base silently thickened every rule in the editor.
        var fixture = new UtilityFixture();

        Assert.Equal(["padding: 8px"], fixture.Emits("p-2"));
        Assert.Equal(["border-width: 2px"], fixture.Emits("border-2"));
    }

    [Fact]
    public void An_arbitrary_border_value_is_read_by_its_shape() {
        // `border-[3px]` and `border-[#f00]` are one utility with two meanings and nothing in the
        // class name says which. A hex triple or a colour function is a colour; everything else,
        // `var()` included, is a width — which is the reading that is right far more often.
        var fixture = new UtilityFixture();

        Assert.Equal(["border-width: 3px"], fixture.Emits("border-[3px]"));
        Assert.Equal(["border-color: #ff0000"], fixture.Emits("border-[#ff0000]"));
        Assert.Equal(["border-top-width: var(--hairline)"], fixture.Emits("border-t-[var(--hairline)]"));
    }

    [Fact]
    public void A_leading_minus_negates_the_value_and_changes_nothing_else() {
        // `-mt-4` sets exactly what `mt-4` sets, which is why the sign is stripped by the parser and
        // applied to the result rather than registered as a family of its own.
        var fixture = new UtilityFixture();

        Assert.Equal(["margin-top: -16px"], fixture.Emits("-mt-4"));
        Assert.Equal(["left: -8px", "right: -8px"], fixture.Emits("-inset-x-2"));
        Assert.Equal(["order: -1"], fixture.Emits("-order-1"));
        Assert.Equal(["margin-top: -3px"], fixture.Emits("-mt-[3px]"));
    }

    [Fact]
    public void Only_a_number_can_be_negated() {
        // A rule that silently means nothing is worse than no rule. `-w-full` is not a hundred per
        // cent to the left, and `-bg-accent` is not a colour at all.
        var fixture = new UtilityFixture();

        Assert.Null(fixture.Declarations("-w-full"));
        Assert.Null(fixture.Declarations("-bg-accent"));
        Assert.Null(fixture.Declarations("-flex-col"));
        Assert.Null(fixture.Declarations("-"));
    }

    [Fact]
    public void The_logical_edges_are_their_own_longhands_and_not_left_and_right() {
        // The layout reads `padding-inline-start` itself rather than resolving it to a side here,
        // which is what makes a panel written with `ps-` mirror under `direction: rtl` without a
        // second stylesheet. Emitting `padding-left` would have looked identical and not done that.
        var fixture = new UtilityFixture();

        Assert.Equal(["padding-inline-start: 8px"], fixture.Emits("ps-2"));
        Assert.Equal(["margin-inline-end: 4px"], fixture.Emits("me-1"));
        Assert.Equal(["inset-inline-start: 0px"], fixture.Emits("start-0"));
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
