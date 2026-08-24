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

    // ⚠ `screen` is the one sizing value whose answer depends on the property. `w-screen` is the
    // viewport's WIDTH and `h-screen` is its HEIGHT, from the same word — so this pair has to be
    // asserted together or a family that answered `100vw` to both would pass on the first row. Both
    // said `100%` until the registrations grew an axis: a percentage resolves against the containing
    // block, which inside any ancestor that is not full size is a different number.
    [InlineData("w-screen", "width: 100vw")]
    [InlineData("h-screen", "height: 100vh")]
    [InlineData("min-h-screen", "min-height: 100vh")]
    [InlineData("max-w-screen", "max-width: 100vw")]
    [InlineData("max-inline-screen", "max-width: 100vw")]
    [InlineData("min-block-screen", "min-height: 100vh")]

    // The three content keywords are values and not properties, so one rule in `TrySize` answers
    // every family — the far end of them is `LayoutStyleBridgeTests`, which lays a box out.
    [InlineData("w-min", "width: min-content")]
    [InlineData("h-max", "height: max-content")]
    [InlineData("max-w-fit", "max-width: fit-content")]
    [InlineData("min-inline-min", "min-width: min-content")]
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
    // Text decoration. `decoration-*` carries three properties and `underline-offset` has to beat
    // `underline` in the name split, so both are asserted here rather than left to the family table.
    [InlineData("underline", "text-decoration-line: underline")]
    [InlineData("overline", "text-decoration-line: overline")]
    [InlineData("line-through", "text-decoration-line: line-through")]
    [InlineData("no-underline", "text-decoration-line: none")]
    [InlineData("underline-offset-4", "text-underline-offset: 4px")]
    [InlineData("underline-offset-auto", "text-underline-offset: auto")]
    [InlineData("decoration-2", "text-decoration-thickness: 2px")]
    [InlineData("decoration-auto", "text-decoration-thickness: auto")]
    [InlineData("decoration-from-font", "text-decoration-thickness: from-font")]
    [InlineData("decoration-double", "text-decoration-style: double")]
    [InlineData("decoration-accent", "text-decoration-color: #4f7cff")]
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
    [InlineData("mask-none", "mask-image: none")]
    // ⚠ The assembled `mask-image` is what these rows are really about. Each stop family sets one
    // fragment and emits the whole declaration beside it, so `mask-linear-from-50%` masks on its own
    // and `mask-linear-45 mask-linear-from-50% mask-linear-to-90%` composes: three rules writing the
    // identical `mask-image` and differing only in which fragment they set. The `var()` fallbacks are
    // the reason one class works alone — see `UtilityComposition.MaskImage`.
    //
    // ⚠ <b>And the `mask-image` names three layers, of which this class fills one.</b> The other two
    // resolve to an opaque gradient, which is the identity under the `intersect` on the next line —
    // that pair is what lets `mask-linear-from-50% mask-radial-to-80%` compose instead of one of them
    // silently winning the cascade. `DrawListBuilder.Reduce` drops the untouched layers before a group
    // is opened, so the arrangement costs nothing at the pixels.
    [InlineData(
        "mask-linear-from-50%",
        "--tw-mask-from-position: 50%|--tw-mask-linear: linear-gradient(var(--tw-mask-linear-angle, 180deg), var(--tw-mask-from, black) var(--tw-mask-from-position, 0%), var(--tw-mask-to, transparent) var(--tw-mask-to-position, 100%))|mask-image: var(--tw-mask-linear, linear-gradient(#fff, #fff)), var(--tw-mask-radial, linear-gradient(#fff, #fff)), var(--tw-mask-conic, linear-gradient(#fff, #fff))|mask-composite: intersect"
    )]
    // ⚠ <b>An edge ramp, which is the shape twelve of the roots have and the one that needed a mask
    // list.</b> It writes all four edge gradients and not only the one it drives, so
    // `mask-t-from-50% mask-b-from-50%` composes — the two rules write the same `--tw-mask-linear`
    // and each edge inside it resolves to whatever its own class set. `to top` and not `to bottom`:
    // `mask-t-*` fades the element out *at the top*.
    [InlineData(
        "mask-t-from-50%",
        "--tw-mask-top-from-position: 50%"
        + "|--tw-mask-top: linear-gradient(to top, var(--tw-mask-top-from, black) var(--tw-mask-top-from-position, 0%), var(--tw-mask-top-to, transparent) var(--tw-mask-top-to-position, 100%))"
        + "|--tw-mask-right: linear-gradient(to right, var(--tw-mask-right-from, black) var(--tw-mask-right-from-position, 0%), var(--tw-mask-right-to, transparent) var(--tw-mask-right-to-position, 100%))"
        + "|--tw-mask-bottom: linear-gradient(to bottom, var(--tw-mask-bottom-from, black) var(--tw-mask-bottom-from-position, 0%), var(--tw-mask-bottom-to, transparent) var(--tw-mask-bottom-to-position, 100%))"
        + "|--tw-mask-left: linear-gradient(to left, var(--tw-mask-left-from, black) var(--tw-mask-left-from-position, 0%), var(--tw-mask-left-to, transparent) var(--tw-mask-left-to-position, 100%))"
        + "|--tw-mask-linear: var(--tw-mask-top, linear-gradient(#fff, #fff)), var(--tw-mask-right, linear-gradient(#fff, #fff)), var(--tw-mask-bottom, linear-gradient(#fff, #fff)), var(--tw-mask-left, linear-gradient(#fff, #fff))"
        + "|mask-image: var(--tw-mask-linear, linear-gradient(#fff, #fff)), var(--tw-mask-radial, linear-gradient(#fff, #fff)), var(--tw-mask-conic, linear-gradient(#fff, #fff))"
        + "|mask-composite: intersect"
    )]
    // ⚠ Two position fragments from one class, which is why `Family.Positions` is several: `mask-x-*`
    // is the left ramp *and* the right one, and a single fragment could only have set one of them.
    [InlineData(
        "mask-x-from-50%",
        "--tw-mask-left-from-position: 50%"
        + "|--tw-mask-right-from-position: 50%"
        + "|--tw-mask-top: linear-gradient(to top, var(--tw-mask-top-from, black) var(--tw-mask-top-from-position, 0%), var(--tw-mask-top-to, transparent) var(--tw-mask-top-to-position, 100%))"
        + "|--tw-mask-right: linear-gradient(to right, var(--tw-mask-right-from, black) var(--tw-mask-right-from-position, 0%), var(--tw-mask-right-to, transparent) var(--tw-mask-right-to-position, 100%))"
        + "|--tw-mask-bottom: linear-gradient(to bottom, var(--tw-mask-bottom-from, black) var(--tw-mask-bottom-from-position, 0%), var(--tw-mask-bottom-to, transparent) var(--tw-mask-bottom-to-position, 100%))"
        + "|--tw-mask-left: linear-gradient(to left, var(--tw-mask-left-from, black) var(--tw-mask-left-from-position, 0%), var(--tw-mask-left-to, transparent) var(--tw-mask-left-to-position, 100%))"
        + "|--tw-mask-linear: var(--tw-mask-top, linear-gradient(#fff, #fff)), var(--tw-mask-right, linear-gradient(#fff, #fff)), var(--tw-mask-bottom, linear-gradient(#fff, #fff)), var(--tw-mask-left, linear-gradient(#fff, #fff))"
        + "|mask-image: var(--tw-mask-linear, linear-gradient(#fff, #fff)), var(--tw-mask-radial, linear-gradient(#fff, #fff)), var(--tw-mask-conic, linear-gradient(#fff, #fff))"
        + "|mask-composite: intersect"
    )]
    [InlineData("mask-intersect", "mask-composite: intersect")]
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

        // ⚠ The viewport keywords are the case the shape test alone would have passed: `100vh`
        // begins with a digit exactly as `100%` does, so `-h-dvh` would have emitted
        // `height: -100vh` on the strength of that first character. They are refused by being
        // named in `NotNegatable`, which is the only thing that saves `-w-full` either.
        Assert.Null(fixture.Declarations("-h-dvh"));
        Assert.Null(fixture.Declarations("-w-svw"));
        Assert.Null(fixture.Declarations("-max-w-lvw"));
    }

    [Fact]
    public void The_six_viewport_keywords_are_two_answers_and_every_sizing_root_gives_them() {
        // ⚠ One rule, seven roots. Tailwind names `svw`/`lvw`/`dvw` and `svh`/`lvh`/`dvh` after the
        // viewport axis being *measured*, not after the property being set — so `h-dvw` is a height
        // of one viewport width, and the mapping belongs on the value rather than in the seven
        // `Size` registrations. That is what makes this one change rather than seven.
        //
        // The three spellings on each axis collapse to one because a Vixen surface has no
        // retractable UA chrome for the small and large viewports to differ over: `LengthContext`
        // is built from `UiSurface`'s width and height and there is no second rectangle. Emitting
        // `100dvw` instead would put a unit in the sheet that `StyleValueParser` cannot read.
        var fixture = new UtilityFixture();

        foreach (var keyword in new[] { "svw", "lvw", "dvw" }) {
            Assert.Equal(["width: 100vw"], fixture.Emits($"w-{keyword}"));
            Assert.Equal(["height: 100vw"], fixture.Emits($"h-{keyword}"));
            Assert.Equal(["max-width: 100vw"], fixture.Emits($"max-w-{keyword}"));
        }

        foreach (var keyword in new[] { "svh", "lvh", "dvh" }) {
            Assert.Equal(["height: 100vh"], fixture.Emits($"h-{keyword}"));
            Assert.Equal(["width: 100vh"], fixture.Emits($"w-{keyword}"));
            Assert.Equal(["min-height: 100vh"], fixture.Emits($"min-h-{keyword}"));
        }

        // `size-*` sets both, so it is the one root where a single class carries the pair.
        Assert.Equal(["width: 100vw", "height: 100vw"], fixture.Emits("size-dvw"));
        Assert.Equal(["width: 100vh", "height: 100vh"], fixture.Emits("size-svh"));
    }

    [Fact]
    public void The_writing_mode_relative_sizing_roots_are_physical_on_both_axes() {
        // ⚠ The block three are the `inset-bs-*` argument: no writing mode, so the block axis is
        // top-to-bottom in every configuration and `block-size` would mean `height` on every
        // element that resolved it.
        //
        // ⚠ The inline three are physical for a *stronger* reason, which is the half that looks
        // wrong beside `inset-s-*` and `rounded-ss-*`. Those keep the logical spelling because
        // `direction: rtl` mirrors them — an edge and a corner are named by which end of the inline
        // axis they sit at. A size is not: `inline-size` is the extent *along* the axis, and
        // mirroring an axis does not change how long it is. Only a writing mode could make the
        // inline axis vertical, and there is none — so this mapping is direction-independent where
        // the block one is merely configuration-independent.
        var fixture = new UtilityFixture();

        Assert.Equal(["width: 16px"], fixture.Emits("inline-4"));
        Assert.Equal(["min-width: 100%"], fixture.Emits("min-inline-full"));
        Assert.Equal(["max-width: 100vw"], fixture.Emits("max-inline-dvw"));

        Assert.Equal(["height: 16px"], fixture.Emits("block-4"));
        Assert.Equal(["min-height: 100%"], fixture.Emits("min-block-full"));
        Assert.Equal(["max-height: 100vh"], fixture.Emits("max-block-dvh"));
    }

    [Fact]
    public void Block_and_inline_are_a_display_utility_bare_and_a_sizing_one_with_a_value() {
        // ⚠ Tailwind spells two roots with one prefix, and `UtilityFamilies.Register` keeps the
        // first family registered under a name — so these cannot be a `Static` and a `Size` in the
        // two sections they belong to. A second registration would be dropped without a word and
        // every `block-*` class would go on being reported as an unrecognised typo, which is the
        // failure this test is really pinning.
        var fixture = new UtilityFixture();

        Assert.Equal(["display: block"], fixture.Emits("block"));
        Assert.Equal(["display: inline"], fixture.Emits("inline"));

        Assert.Equal(["height: 100%"], fixture.Emits("block-full"));
        Assert.Equal(["width: 100%"], fixture.Emits("inline-full"));

        // The longer display names still win the prefix split, which is `SplitName` taking the
        // longest registered name rather than anything these two arrange.
        Assert.Equal(["display: inline-block"], fixture.Emits("inline-block"));
        Assert.Equal(["display: inline-flex"], fixture.Emits("inline-flex"));
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

        Assert.Equal(
            ["background-color: color-mix(in oklab, #4f7cff 50%, transparent)"],
            fixture.Emits("bg-accent/50")
        );
    }

    [Fact]
    public void An_opacity_modifier_survives_a_token_that_is_not_a_hex_triple() {
        // ⚠ **This is the bug the `color-mix()` rewrite fixes, and it was silent.** The old emission
        // rewrote the colour as `rgba(r, g, b, a)`, which meant taking it apart — so it worked only
        // when the token was a literal hex triple, and *dropped the opacity entirely* otherwise. Not
        // an edge case: the moment a theme token holds a reference or is authored in `oklch()`, as
        // `docs/plan/43` § D2 and § D4 both call for, every `/opacity` on it silently painted at
        // full strength. The utility resolved, the CSS was valid, and nothing failed.
        //
        // A mix does not take the colour apart, so all three of these keep their modifier.
        var fixture = new UtilityFixture(
            """
            @theme {
                --*: initial;
                --color-referenced: var(--brand);
                --color-wide: oklch(0.623 0.214 259.815);
                --color-plain: #4f7cff;
                --spacing: 4px;
            }
            """
        );

        Assert.Equal(
            ["background-color: color-mix(in oklab, var(--brand) 40%, transparent)"],
            fixture.Emits("bg-referenced/40")
        );

        Assert.Equal(
            ["color: color-mix(in oklab, oklch(0.623 0.214 259.815) 60%, transparent)"],
            fixture.Emits("text-wide/60")
        );

        // And the arbitrary opacity form is a fraction rather than a percentage, so `/[0.35]` is 35%.
        Assert.Equal(
            ["background-color: color-mix(in oklab, #4f7cff 35%, transparent)"],
            fixture.Emits("bg-plain/[0.35]")
        );
    }

    [Fact]
    public void The_mix_an_opacity_modifier_emits_resolves_to_what_the_rgba_rewrite_used_to_give() {
        // The other half of the claim above: nothing that already worked has moved. For a hex token
        // the two emissions are the same colour, because mixing against `transparent` with
        // premultiplied alpha leaves the components alone and scales only the alpha. Asserted on the
        // *parsed* value rather than on the text, which is the only place the two can be compared.
        var parser = new StyleValueParser(new NameTable(), new NameTable());
        var emitted = new UtilityFixture().Emits("bg-accent/50")[0]["background-color: ".Length..];

        var mixed = parser.Parse(emitted);
        var rewritten = parser.Parse("rgba(79, 124, 255, 0.5)");

        Assert.Equal(StyleValueKind.Color, mixed.Kind);
        Assert.Equal(rewritten.Color.R, mixed.Color.R, 1e-3f);
        Assert.Equal(rewritten.Color.G, mixed.Color.G, 1e-3f);
        Assert.Equal(rewritten.Color.B, mixed.Color.B, 1e-3f);
        Assert.Equal(0.5f, mixed.Color.A, 1e-3f);
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
