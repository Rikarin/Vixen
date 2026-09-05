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
    // ⚠ <b>The shadow is a fragment and the ring is written inline, and both classes emit the same
    // assembled `box-shadow`.</b> That is what lets `shadow-lg ring-2` be both — the two families
    // wrote the one longhand until `Rikarin/Vixen#279` item 4, so the cascade picked a rule and the
    // other class silently did not apply. ⚠ `shadow-none` sets a *transparent* shadow rather than
    // the `none` keyword: `none` in the middle of a comma list is not an empty item, it is a keyword
    // `EmitShadow` refuses the whole declaration over, so `shadow-none ring-2` would have lost the
    // ring too. What drops the invisible half of the pair is `EmitOneShadow`, not the emission.
    [InlineData(
        "shadow",
        "--tw-shadow: 0px 1px 2px rgba(0, 0, 0, 0.3)"
        + "|box-shadow: 0 0 0 var(--tw-ring-width, 0px) var(--tw-ring-color, currentcolor), var(--tw-shadow, 0 0 transparent)"
    )]
    [InlineData(
        "shadow-lg",
        "--tw-shadow: 0px 8px 24px rgba(0, 0, 0, 0.45)"
        + "|box-shadow: 0 0 0 var(--tw-ring-width, 0px) var(--tw-ring-color, currentcolor), var(--tw-shadow, 0 0 transparent)"
    )]
    [InlineData(
        "shadow-none",
        "--tw-shadow: 0 0 transparent"
        + "|box-shadow: 0 0 0 var(--tw-ring-width, 0px) var(--tw-ring-color, currentcolor), var(--tw-shadow, 0 0 transparent)"
    )]
    [InlineData(
        "ring-2",
        "--tw-ring-width: 2px"
        + "|box-shadow: 0 0 0 var(--tw-ring-width, 0px) var(--tw-ring-color, currentcolor), var(--tw-shadow, 0 0 transparent)"
    )]
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
    // ⚠ <b>Four Tailwind roots under one prefix, setting three properties.</b> `snap-y` is a
    // container's axis, `snap-start` an item's alignment, `snap-always` an item's stop — and
    // `snap-mandatory` is none of the three: it is the strictness half of `scroll-snap-type`, which
    // the axis class cannot know because the two are written as separate classes. Hence the
    // fragment, whose fallback is CSS's own `proximity` so that `snap-y` alone means what it means
    // in a browser.
    [InlineData("snap-y", "scroll-snap-type: y var(--tw-scroll-snap-strictness, proximity)")]
    [InlineData("snap-both", "scroll-snap-type: both var(--tw-scroll-snap-strictness, proximity)")]
    [InlineData("snap-none", "scroll-snap-type: none")]
    [InlineData("snap-mandatory", "--tw-scroll-snap-strictness: mandatory")]
    [InlineData("snap-start", "scroll-snap-align: start")]
    [InlineData("snap-center", "scroll-snap-align: center")]
    // ⚠ `snap-align-none` and not `snap-none`, which is already the container's off switch — one
    // prefix, two properties, and v4 spells the second one longer for exactly that reason.
    [InlineData("snap-align-none", "scroll-snap-align: none")]
    [InlineData("snap-always", "scroll-snap-stop: always")]
    // ⚠ <b>The two halves of the `mask` prefix, and they are the point of `Family.ValueAlongside`.</b>
    // Tailwind spells the radial ending's *shape* `mask-circle`, on the same bare prefix as the four
    // `mask-repeat` classes — one family, because `Register` keeps the first under a name and drops a
    // second silently. A shape has to carry the three mask-layer declarations every other
    // `mask-radial-*` carries; a repeat value must not, or `mask-no-repeat` alone would install a
    // radial mask on an element nobody asked to mask. The pair below is the discriminating case: a
    // family-wide `Alongside` passes the first row and fails the second.
    [InlineData(
        "mask-circle",
        "--tw-mask-radial-shape: circle"
        + "|--tw-mask-radial: radial-gradient(var(--tw-mask-radial-shape, ellipse) var(--tw-mask-radial-size, farthest-corner) at var(--tw-mask-radial-position, center), var(--tw-mask-from, black) var(--tw-mask-from-position, 0%), var(--tw-mask-to, transparent) var(--tw-mask-to-position, 100%))"
        + "|mask-image: var(--tw-mask-linear, linear-gradient(#fff, #fff)), var(--tw-mask-radial, linear-gradient(#fff, #fff)), var(--tw-mask-conic, linear-gradient(#fff, #fff))"
        + "|mask-composite: intersect"
    )]
    // ⚠ `ellipse` is CSS's own default and the class is still not a no-op: it is how an author
    // overrides a `mask-circle` an ancestor or a component set, which is `filter-none`'s argument.
    [InlineData(
        "mask-ellipse",
        "--tw-mask-radial-shape: ellipse"
        + "|--tw-mask-radial: radial-gradient(var(--tw-mask-radial-shape, ellipse) var(--tw-mask-radial-size, farthest-corner) at var(--tw-mask-radial-position, center), var(--tw-mask-from, black) var(--tw-mask-from-position, 0%), var(--tw-mask-to, transparent) var(--tw-mask-to-position, 100%))"
        + "|mask-image: var(--tw-mask-linear, linear-gradient(#fff, #fff)), var(--tw-mask-radial, linear-gradient(#fff, #fff)), var(--tw-mask-conic, linear-gradient(#fff, #fff))"
        + "|mask-composite: intersect"
    )]
    [InlineData("mask-no-repeat", "mask-repeat: no-repeat")]
    [InlineData("mask-repeat-x", "mask-repeat: repeat-x")]
    // Transitions.
    // ⚠ The longhand AND the fragment, and the fragment is not decoration: `transition` reads its own
    // default through `var(--tw-duration, 150ms)` because the generated sheet is ordered by class name
    // and `duration-*` sorts before it. Emitting only the longhand would leave `transition duration-200`
    // running for 150 ms — see `TransitionUtilityTests`.
    [InlineData("duration-200", "transition-duration: 200ms|--tw-duration: 200ms")]
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
    // ⚠ The fifth keyword, and the one that kept all three roots at `partial`. It was unregistered
    // because `LayoutStyleBuilder` did not know the word — and that is the shape the comment above
    // is about, one keyword deeper: `overflow: clip` clipped the draw list and stayed `Visible` to
    // the layout. It reads as `hidden` there, because nothing in this engine can tell the two apart.
    [InlineData("overflow-clip", "overflow: clip")]
    [InlineData("overflow-x-clip", "overflow-x: clip")]
    [InlineData("overflow-y-clip", "overflow-y: clip")]
    [InlineData("pointer-events-none", "pointer-events: none")]
    // Aspect.
    [InlineData("aspect-1.5", "aspect-ratio: 1.5")]
    [InlineData("aspect-square", "aspect-ratio: 1 / 1")]
    [InlineData("aspect-video", "aspect-ratio: 16 / 9")]
    public void Each_family_emits_what_it_says(string candidate, string expected) {
        var fixture = new UtilityFixture();
        Assert.Equal(expected.Split('|'), fixture.Emits(candidate));
    }

    /// <summary>Two <c>snap-</c> classes write one <c>scroll-snap-type</c> between them.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Asserted as a computed value and not as emitted text, because the join happens in
    ///         the cascade.</b> <c>snap-y</c> writes an axis and a <c>var()</c>; <c>snap-mandatory</c>
    ///         writes the fragment that <c>var()</c> names. Neither rule can see the other, and what
    ///         reaches <c>ScrollView.SnapType</c> is a string the cascade assembled — which is exactly
    ///         why that method reads it as text rather than through a keyword accessor.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The strictness on its own must say nothing.</b> A fragment is half a declaration;
    ///         a <c>snap-mandatory</c> that made a container snap without an axis beside it would be
    ///         the family emitting a property the author did not ask for.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_axis_and_the_strictness_are_two_classes_writing_one_declaration() {
        var fixture = new UtilityFixture();

        // Alone, the axis resolves through the fallback — CSS's own initial, and v4's.
        Assert.Equal("y proximity", fixture.Computed(["snap-y"], "scroll-snap-type"));

        // Together, one declaration whose two halves came from two rules.
        Assert.Equal("y mandatory", fixture.Computed(["snap-y", "snap-mandatory"], "scroll-snap-type"));

        // And the strictness alone is a fragment nothing assembled.
        Assert.Null(fixture.Computed(["snap-mandatory"], "scroll-snap-type"));
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

    /// <summary><c>lh</c> is one line box, on every sizing root that answers a keyword.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The only sizing value whose <i>unit</i> did not exist, which is why it was the
    ///         ledger's one Sizing <c>partial</c> and why the row's own <c>value_gap</c> said nothing
    ///         about it.</b> Every keyword beside it resolves to a unit the parser already read;
    ///         <c>max-block-lh</c> emitted text <c>StyleValueParser</c> refused, so the class
    ///         cascaded to nothing and the demotion came from the measurement rather than from the
    ///         prose. Resolving it took a <c>StyleUnit</c>, a line height on <c>LengthContext</c> and
    ///         a wire from <c>UiDocument</c>; <c>Vixen.Ui.Tests</c>' <c>LineHeightUnitTests</c> is the
    ///         half of that which measures a box.
    ///     </para>
    ///     <para>
    ///         ⚠ And the negation, which is the third time this trap has been laid: <c>1lh</c> begins
    ///         with a digit exactly as <c>100%</c> and <c>100vh</c> do, so <c>-max-block-lh</c> would
    ///         have emitted "minus one line box" on the strength of that first character had
    ///         <c>lh</c> not been named in <c>NotNegatable</c>.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_lh_keyword_is_one_line_box_and_cannot_be_negated() {
        var fixture = new UtilityFixture();

        Assert.Equal(["max-height: 1lh"], fixture.Emits("max-block-lh"));
        Assert.Equal(["height: 1lh"], fixture.Emits("h-lh"));
        Assert.Equal(["min-height: 1lh"], fixture.Emits("min-h-lh"));
        Assert.Equal(["max-height: 1lh"], fixture.Emits("max-h-lh"));

        Assert.Null(fixture.Declarations("-h-lh"));
        Assert.Null(fixture.Declarations("-max-block-lh"));
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
    public void The_block_flow_spacing_roots_are_the_physical_longhands() {
        // ⚠ The `scroll-mbs-*` argument, arriving for the fourth time and for the pair that had been
        // left `absent`: nothing interns `margin-block-start` — `LayoutStyleBuilder.EdgeNames.For`
        // interns the four physical edges and the two *inline* logical ones, because those are the
        // pair `direction` mirrors — and with no writing mode the block axis is top-to-bottom in
        // every configuration this engine can be in. So the block longhand *is* the physical one on
        // every element that could resolve it, and emitting v4's spelling would have registered four
        // families that resolve, compute a value and move nothing.
        var fixture = new UtilityFixture();

        Assert.Equal(["margin-top: 8px"], fixture.Emits("mbs-2"));
        Assert.Equal(["margin-bottom: 8px"], fixture.Emits("mbe-2"));
        Assert.Equal(["padding-top: 4px"], fixture.Emits("pbs-1"));
        Assert.Equal(["padding-bottom: 4px"], fixture.Emits("pbe-1"));

        // `auto` and `px` are the two spellings the ledger lists for these roots, and a margin is the
        // only one of the two that takes `auto`.
        Assert.Equal(["margin-top: auto"], fixture.Emits("mbs-auto"));
        Assert.Equal(["padding-bottom: 1px"], fixture.Emits("pbe-px"));

        // ⚠ And the shadow that does not happen. `mbs` is `mb` plus a letter, so registering it could
        // have eaten `mb-2`; `SplitName` takes the longest registered prefix, which is the rule
        // `scroll-mbs` already relies on.
        Assert.Equal(["margin-bottom: 8px"], fixture.Emits("mb-2"));
        Assert.Equal(["padding-bottom: 8px"], fixture.Emits("pb-2"));
    }

    [Fact]
    public void An_axis_utility_sets_both_ends_so_no_direction_can_tell_it_from_the_logical_spelling() {
        // ⚠ <b>The refutation this test exists for.</b> The ledger recorded `mx-*`, `my-*`, `px-*`,
        // `py-*`, `inset-x/y-*` and `border-x/y-*` as "identical in LTR, wrong in RTL" — physical
        // edges where v4 emits `margin-inline` and its siblings. That is false, and the reason is
        // one line: an axis utility sets **both** ends of the axis to one value, and `direction`
        // only decides which end is called the start. `margin-inline: 8px` and
        // `margin-left: 8px; margin-right: 8px` are the same computed margins under `ltr` and under
        // `rtl` alike.
        //
        // What could distinguish them is a *vertical writing mode*, where the inline axis is the
        // vertical one — and `Vixen.Ui.Layout` has none, which is `mbs-*`'s argument one axis over.
        var fixture = new UtilityFixture();

        foreach (var candidate in (string[])["mx-2", "my-2", "px-2", "py-2", "inset-x-2", "inset-y-2"]) {
            var emitted = fixture.Emits(candidate);

            Assert.Equal(2, emitted.Length);

            // The property differs and the value does not, which is the whole of the argument: a
            // mirror swaps the two properties and leaves the pair of declarations identical.
            Assert.NotEqual(Property(emitted[0]), Property(emitted[1]));
            Assert.Equal(Value(emitted[0]), Value(emitted[1]));
        }

        // ⚠ The contrast, and the half that would break if this reasoning were applied one step too
        // far: a utility naming a single *end* keeps v4's logical spelling, because that is the one
        // shape `direction: rtl` really does mirror. `ms-*` is not `ml-*` and must never become it.
        Assert.Equal(["margin-inline-start: 8px"], fixture.Emits("ms-2"));
        Assert.Equal(["inset-inline-start: 0px"], fixture.Emits("inset-s-0"));

        static string Property(string declaration) => declaration[..declaration.IndexOf(':', StringComparison.Ordinal)];

        static string Value(string declaration) => declaration[(declaration.IndexOf(':', StringComparison.Ordinal) + 1)..];
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

    /// <summary>⚠ v4's blur scale is names, and this engine answered only numbers.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 43 § C2 went looking for a scale shifted by one step and this family had no
    ///         scale at all.</b> <c>blur-*</c> resolved through <c>--spacing</c>, so <c>blur-8</c>
    ///         worked and <c>blur-md</c> — which is the only kind of spelling v4 has for this
    ///         family, and the one <c>UiGeometry</c>'s own remarks call canonical beside
    ///         <c>rounded-2xl</c> — produced no rule at all. A class that produces no rule is
    ///         reported, so this was findable; nobody had looked, because the row read
    ///         <c>works</c> off a numeric probe.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both spellings, and the numeric one is asserted too.</b> Keeping it is what
    ///         makes this additive rather than a re-pegging: no committed picture moves, because
    ///         nothing in the tree writes either spelling yet and <c>blur-8</c> means today what it
    ///         meant yesterday.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("blur-xs", "4px")]
    [InlineData("blur-sm", "8px")]
    [InlineData("blur-md", "12px")]
    [InlineData("blur-lg", "16px")]
    [InlineData("blur-xl", "24px")]
    [InlineData("blur-2xl", "40px")]
    [InlineData("blur-3xl", "64px")]
    [InlineData("blur-2", "8px")]
    public void A_blur_resolves_a_named_step_and_a_spacing_count_alike(string utility, string expected) {
        var fixture = new UtilityFixture("");

        Assert.Contains(
            $"{UtilityComposition.Blur}: {expected}",
            string.Join("; ", fixture.Emits(utility)),
            StringComparison.Ordinal
        );
    }

    /// <summary>⚠ And the backdrop half reads the same scale, which it did not before either.</summary>
    [Theory]
    [InlineData("backdrop-blur-md", "12px")]
    [InlineData("backdrop-blur-8", "32px")]
    public void A_backdrop_blur_reads_the_same_scale(string utility, string expected) {
        var fixture = new UtilityFixture("");

        Assert.Contains(
            $"{UtilityComposition.BackdropBlur}: {expected}",
            string.Join("; ", fixture.Emits(utility)),
            StringComparison.Ordinal
        );
    }

    /// <summary>The shipped radius, shadow and drop-shadow scales are v4's own names.</summary>
    /// <remarks>
    ///     ⚠ <b>Doc 43 § C2's premise, measured — and it is refuted.</b> The row said Vixen's
    ///     <c>rounded</c> scale and the editor's <c>--radius-*</c> names "must be re-pegged, or every
    ///     <c>rounded-sm</c> in the tree means something one step off what a Tailwind user expects".
    ///     They were pegged already: C3 transcribed v4.3.3's <c>@theme</c> whole, so
    ///     <c>--radius-xs</c> is 2px and <c>--shadow-xs</c> is the one-pixel shadow v3 called
    ///     <c>shadow-sm</c>. What the editor adds — <c>--radius-panel</c>, <c>--radius-control</c>,
    ///     <c>--radius-row</c> — is a <i>semantic</i> namespace that collides with none of them and
    ///     clears none of them. So no picture moves, which is the one thing § C2 warned this item
    ///     would do.
    ///     <para>
    ///         Written as an assertion rather than as a note, because a note cannot notice the day
    ///         somebody re-transcribes the theme from v3.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_shipped_radius_and_shadow_scales_are_v4s_and_not_v3s() {
        var tokens = new UtilityFixture("").Tokens;

        // v4's radius scale starts at `xs` and runs to `4xl`; v3's started at `sm` and stopped at
        // `3xl`, so the presence of both ends is what tells the two apart.
        Assert.Equal("2px", tokens.Radius["xs"]);
        Assert.Equal("4px", tokens.Radius["sm"]);
        Assert.Equal("32px", tokens.Radius["4xl"]);

        // ⚠ The step that moved. v3's `shadow-sm` was one pixel; v4's `shadow-sm` is the two-layer
        // shadow v3 called `shadow`, and the one-pixel one is `shadow-xs`.
        Assert.Contains("0 1px 2px 0", tokens.Shadow["xs"], StringComparison.Ordinal);
        Assert.Contains("0 1px 3px 0", tokens.Shadow["sm"], StringComparison.Ordinal);
        Assert.True(tokens.Shadow.ContainsKey("2xs"), "v4's shadow scale has a 2xs and v3's did not");

        Assert.True(tokens.DropShadow.ContainsKey("xs"), "v4's drop-shadow scale has an xs and v3's did not");
    }
}
