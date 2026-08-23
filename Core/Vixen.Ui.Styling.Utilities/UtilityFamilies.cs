// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Ui.Styling.Utilities;

/// <summary>One <c>property: value</c> a utility emits.</summary>
/// <param name="Property">The CSS property.</param>
/// <param name="Value">Its value.</param>
public readonly record struct UtilityDeclaration(string Property, string Value);

/// <summary>How a utility turns its value into declarations.</summary>
enum ValueKind : byte {
    /// <summary>No value at all: <c>flex</c>, <c>truncate</c>.</summary>
    Static,

    /// <summary>A multiple of the spacing unit: <c>p-4</c>.</summary>
    Spacing,

    /// <summary>A colour token: <c>bg-accent</c>.</summary>
    Color,

    /// <summary>A bare number: <c>grow-0</c>, <c>z-10</c>.</summary>
    Number,

    /// <summary>A whole count substituted into a template: <c>grid-cols-3</c>, <c>col-span-2</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>Its own kind because the emitted value is not the value that was written, and every
    ///     other numeric family's is.</b> <c>grid-cols-3</c> does not mean
    ///     <c>grid-template-columns: 3</c> — that is not a track list and no engine has ever read it
    ///     — it means <c>repeat(3, minmax(0, 1fr))</c>. Emitting the bare number was a family that
    ///     resolved, cascaded, and could never do anything, which is exactly the shape of failure the
    ///     parity gate exists to find and could not see while nothing read the property at all.
    /// </remarks>
    CountTemplate,

    /// <summary>One of a fixed set of keywords: <c>items-center</c>.</summary>
    Keyword,

    /// <summary>A length that may also be a fraction or a keyword: <c>w-1/2</c>, <c>w-full</c>.</summary>
    Size,

    /// <summary>A radius token: <c>rounded-md</c>.</summary>
    Radius,

    /// <summary>A font size token, which also sets a line height: <c>text-lg</c>.</summary>
    FontSize,

    /// <summary>A font weight token: <c>font-semibold</c>.</summary>
    FontWeight,

    /// <summary>A duration in milliseconds: <c>duration-200</c>.</summary>
    Duration,

    /// <summary>A percentage written as a whole number: <c>opacity-50</c> is <c>0.5</c>.</summary>
    Fraction,

    /// <summary>A width in pixels or a colour: <c>border-2</c>, <c>border-t-accent</c>.</summary>
    BorderEdge,

    /// <summary>A whole <c>box-shadow</c> declaration named by a token: <c>shadow-lg</c>.</summary>
    Shadow,

    /// <summary>A gradient stop, which is a colour or a position: <c>from-accent</c>, <c>from-40%</c>.</summary>
    /// <remarks>
    ///     ⚠ Composed — it emits a <see cref="UtilityComposition" /> fragment and no declaration of
    ///     its own. The colour and the position are separate fragments because Tailwind lets them be
    ///     written separately: <c>from-accent from-40%</c> is two classes setting two things.
    /// </remarks>
    GradientStop
}

/// <summary>The utilities a class name can name, and what each one emits.</summary>
/// <remarks>
///     <para>
///         Table-driven, because the interesting part of a utility system is not any individual
///         utility — it is that adding one is a line of data rather than a branch. The families are
///         the set [doc 09](../../docs/plan/09-ui-framework.md) names for 1.0: what an editor
///         actually needs, which is a good deal less than what Tailwind ships.
///     </para>
///     <para>
///         <b>Two utilities genuinely collide and the collision is resolved by the token tables.</b>
///         <c>text-</c> is font size, colour, and alignment: <c>text-lg</c>, <c>text-accent</c> and
///         <c>text-center</c> are three different properties behind one prefix. So <c>text-</c>
///         resolves in order — keyword, then font-size token, then colour — and the consequence is
///         worth knowing before someone hits it: a colour named <c>center</c> would be unreachable
///         through <c>text-</c>. The same applies to <c>border-</c>, which is width or colour.
///     </para>
/// </remarks>
public static class UtilityFamilies {
    /// <summary>One utility family.</summary>
    /// <param name="Name">The prefix a class name is split on.</param>
    /// <param name="Kind">How its value turns into declarations.</param>
    /// <param name="Properties">What it sets.</param>
    /// <param name="Keywords">The fixed values it also accepts, already written as pairs.</param>
    /// <param name="ColorProperties">
    ///     Where a <see cref="ValueKind.BorderEdge" /> family puts a colour, which is a different set
    ///     of longhands from where it puts a width — <c>border-t-2</c> is <c>border-top-width</c> and
    ///     <c>border-t-accent</c> is <c>border-top-color</c>. Null on every other kind.
    /// </param>
    /// <param name="Positions">
    ///     Where a <see cref="ValueKind.GradientStop" /> family puts a percentage, which is a
    ///     different fragment from where it puts a colour — <c>from-accent</c> is
    ///     <c>--tw-gradient-from</c> and <c>from-40%</c> is <c>--tw-gradient-from-position</c>. Null
    ///     on every other kind.
    ///     <para>
    ///         ⚠ <b>Several, for the same reason <see cref="Properties" /> is several.</b>
    ///         <c>mask-x-from-50%</c> is one class setting the near stop of <i>both</i> the left and
    ///         the right edge ramp, and a single fragment here could only have set one of them — the
    ///         element would fade on one side and not the other, which reads as the utility half
    ///         working rather than as a missing field.
    ///     </para>
    /// </param>
    /// <param name="Alongside">
    ///     Declarations emitted verbatim whenever the family resolves, whatever its value.
    ///     <para>
    ///         ⚠ <b>This is what a <i>composing</i> utility needs and no other kind does.</b>
    ///         <c>via-accent</c> has to say two things at once: the colour it was given, and — because
    ///         a middle stop is the one thing a <c>var()</c> fallback cannot conjure — that the stop
    ///         list is now the three-stop form. The second is a constant, identical for every
    ///         <c>via-*</c> in the theme, so it belongs to the family rather than to the value.
    ///     </para>
    /// </param>
    /// <param name="Template">
    ///     The value a <see cref="ValueKind.CountTemplate" /> family emits, with <c>{0}</c> where the
    ///     count goes. Null on every other kind.
    /// </param>
    /// <param name="Scope">
    ///     What the family's rule is <i>about</i>, appended to the selector the class name would
    ///     otherwise produce. Null — the overwhelming default — means the rule is about the element
    ///     carrying the class.
    ///     <para>
    ///         ⚠ <b>Two of Tailwind's families are not property families at all, and this is the
    ///         whole of what they need.</b> <c>space-x-4</c> and <c>divide-y</c> put a margin or a
    ///         border on <i>every child but the last</i>: they are a rule over a relationship, not a
    ///         declaration on a box, and no amount of value-table work reaches them. With
    ///         <c>" &gt; :not(:last-child)"</c> here the generator writes
    ///         <c>.space-x-4 &gt; :not(:last-child)</c>, which the selector engine compiles and
    ///         matches — <see cref="SimpleSelectorKind.Not" />, <see cref="PositionTest.Last" /> and
    ///         <see cref="Combinator.Child" /> have all been there the whole time.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is appended <i>after</i> the variants, which is the only order that is
    ///         right.</b> <c>hover:space-x-4</c> means "when the container is hovered, space its
    ///         children" — <c>.hover\:space-x-4:hover &gt; :not(:last-child)</c> — and a suffix
    ///         written before the variant would say "when a spaced child is hovered", which is a
    ///         different rule that happens to compile.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A scoped family cannot be <c>@apply</c>-ed</b>, for the same reason a variant
    ///         cannot: it is a rule with a selector of its own rather than a set of declarations to
    ///         drop into the block. <see cref="ApplyExpander" /> refuses it by name.
    ///     </para>
    /// </param>
    sealed record Family(
        string Name,
        ValueKind Kind,
        string[] Properties,
        Dictionary<string, string>? Keywords = null,
        string[]? ColorProperties = null,
        string[]? Positions = null,
        UtilityDeclaration[]? Alongside = null,
        string? Template = null,
        string? Scope = null
    );

    static readonly Dictionary<string, Family> Registry = new(StringComparer.Ordinal);
    static readonly List<string> Names = [];

    static UtilityFamilies() {
        // ── Layout ──────────────────────────────────────────────────────────────────────────
        Static("block", "display", "block");
        Static("inline", "display", "inline");
        Static("inline-block", "display", "inline-block");
        Static("flex", "display", "flex");
        Static("inline-flex", "display", "inline-flex");
        Static("grid", "display", "grid");
        Static("hidden", "display", "none");

        // ⚠ <b>`visibility` was never a missing reader — `DrawListBuilder` has honoured `hidden`
        // since the draw list existed. What was absent was the three classes and one keyword.</b>
        // The ledger's `absent` against this root read as "the engine cannot do this"; it actually
        // meant "nobody can spell it", which is a different debt and a much smaller one. Note the
        // pairing with the line above: `hidden` is `display: none` and `invisible` is
        // `visibility: hidden`, which is Tailwind's naming and also the distinction CSS has two
        // properties for — the first takes the box out of layout, the second leaves it there.
        //
        // ⚠ <b>`visible` emits the initial value and is not therefore a no-op.</b> The whole point
        // of the keyword is to override an *inherited* `hidden` on a descendant, which is the one
        // thing `display` cannot express — a hidden subtree with a visible island in it. The gate
        // cannot see that from a single probe element, so `VisibilityTests` asserts it directly.
        Static("visible", "visibility", "visible");
        Static("invisible", "visibility", "hidden");
        Static("collapse", "visibility", "collapse");

        // ⚠ <b>Three declarations, because `truncate` <i>is</i> three declarations.</b> It was one
        // here — `overflow: hidden` alone — and doc 43's F5 is the finding that the other two were
        // missing: the class named the ellipsis it could not draw, and the wrapping the third
        // suppresses went on happening, so a long title in `TaskCenter.vxml` grew the row downwards
        // instead of ending in a marker.
        //
        // ⚠ The order the two arrived in is the part worth keeping. Emitting these before
        // `Vixen.Ui.Text` could draw an ellipsis would have produced this repository's most-repeated
        // defect — a property that resolves and paints nothing — and the consumption gate would have
        // caught it and been answered with a line in `InertProperties.txt`, which is the cheap close.
        // The reader landed first (`UiDocument.EllipsisOf`), the `clipped` scene proved the gate can
        // see it, and this changed last. So no line was needed and none was added.
        Register(new Family(
            "truncate",
            ValueKind.Static,
            ["overflow"],
            new Dictionary<string, string>(StringComparer.Ordinal) { [string.Empty] = "overflow:hidden" },
            Alongside: [
                new UtilityDeclaration("text-overflow", "ellipsis"),
                new UtilityDeclaration("white-space", "nowrap")
            ]
        ));

        // `flex-wrap` and `flex-col` are both values of `flex`, and they set different properties.
        // Registering `flex-wrap` as a family of its own would make the class `flex-wrap` a family
        // with no value rather than the family `flex` with the value `wrap`.
        Keywords("flex", "flex-direction", new() {
            ["row"] = "row", ["row-reverse"] = "row-reverse",
            ["col"] = "column", ["col-reverse"] = "column-reverse"
        });

        Keywords("flex", "flex-wrap", new() {
            ["wrap"] = "wrap", ["wrap-reverse"] = "wrap-reverse", ["nowrap"] = "nowrap"
        });

        // `flex-1` is the shorthand and not a third property, so it joins the same keyword table:
        // one prefix, and the value decides which of `display`, `flex-direction`, `flex-wrap` and
        // `flex` it sets. ExCSS expands the shorthand into its longhands while parsing, so the
        // cascade sees `flex-grow`/`flex-shrink`/`flex-basis` and never the word itself.
        Keywords("flex", "flex", new() {
            ["1"] = "1 1 0%", ["auto"] = "1 1 auto", ["initial"] = "0 1 auto", ["none"] = "none"
        });

        Keywords("items", "align-items", new() {
            ["start"] = "flex-start", ["end"] = "flex-end", ["center"] = "center",
            ["baseline"] = "baseline", ["stretch"] = "stretch"
        });

        Keywords("self", "align-self", new() {
            ["auto"] = "auto", ["start"] = "flex-start", ["end"] = "flex-end",
            ["center"] = "center", ["baseline"] = "baseline", ["stretch"] = "stretch"
        });

        Keywords("justify", "justify-content", new() {
            ["start"] = "flex-start", ["end"] = "flex-end", ["center"] = "center",
            ["between"] = "space-between", ["around"] = "space-around", ["evenly"] = "space-evenly"
        });

        Keywords("content", "align-content", new() {
            ["start"] = "flex-start", ["end"] = "flex-end", ["center"] = "center",
            ["between"] = "space-between", ["around"] = "space-around", ["stretch"] = "stretch"
        });

        // ── Flex and grid ───────────────────────────────────────────────────────────────────
        Number("grow", "flex-grow");
        Number("shrink", "flex-shrink");
        Number("order", "order");
        Size("basis", "flex-basis");
        // ⚠ <b>Both of these used to emit the bare count, and both were wrong rather than merely
        // unread.</b> `grid-template-columns: 3` is not a track list in any engine, so the family
        // could never have worked even once the bridge existed — it was inert twice over, and only
        // the second reason was written down. Tailwind's own expansions are what they emit now.
        //
        // ⚠ `minmax(0, 1fr)` rather than `1fr`, and the difference is load-bearing: §7.2.3 makes a
        // bare `1fr` mean `minmax(auto, 1fr)`, whose automatic floor is the track's min-content
        // size — so a `grid-cols-3` holding one wide child would refuse to divide evenly. The
        // explicit zero floor is why Tailwind writes it that way and why a grammar that reads
        // `minmax()` by discarding its arguments passes every test until something overflows.
        CountTemplate("grid-cols", "repeat({0}, minmax(0, 1fr))", "grid-template-columns");

        // ⚠ `span N / span N` is Tailwind's literal output and is what §8.3 calls over-constrained:
        // two span edges name no line between them, so the end edge is dropped and the item spans N
        // from wherever auto-placement puts it. Emitting exactly what Tailwind does keeps a
        // stylesheet copied from its documentation working, and the store resolves it identically.
        CountTemplate("col-span", "span {0} / span {0}", "grid-column");
        CountTemplate("row-span", "span {0} / span {0}", "grid-row");

        // ⚠ <b>`full` is a line pair and not a span, so it cannot be the template with a count in
        // it.</b> `1 / -1` names the first line of the explicit grid and its last, which is a
        // different thing from spanning every track: an item spanning `N` from wherever
        // auto-placement dropped it would run off the end. Tailwind emits the line pair, `§8.3`
        // resolves `-1` against the explicit grid, and `GridPlacement.TryParseShorthand` reads both
        // edges — so the keyword rides on the same family rather than needing one of its own.
        Keywords("col-span", "grid-column", new() { ["full"] = "1 / -1" });
        Keywords("row-span", "grid-row", new() { ["full"] = "1 / -1" });

        CountTemplate("grid-rows", "repeat({0}, minmax(0, 1fr))", "grid-template-rows");

        // ⚠ <b>`none` is the initial value written out, and it is not the same as an empty
        // declaration.</b> `GridTrackList` refuses the token — correctly, since §7.2's
        // `<auto-track-list>` has no `none` — so `TrackListProperty` reads it for the two explicit
        // properties only and resets the node. `grid-rows-subgrid` is deliberately absent: there is
        // no subgrid in `Vixen.Ui.Layout`, and a class that resolved to a declaration the bridge
        // then refused would look like it worked.
        Keywords("grid-cols", "grid-template-columns", new() { ["none"] = "none" });
        Keywords("grid-rows", "grid-template-rows", new() { ["none"] = "none" });

        // The implicit tracks. `Spacing` because v4's numeric form is `calc(var(--spacing) * N)` and
        // this system's spacing scale is the same idea with the multiplication already done; the four
        // keywords are what the family is actually written with.
        //
        // ⚠ `fr` is `minmax(0, 1fr)` rather than `1fr`, for the reason `grid-cols` is: a bare `1fr`
        // floors at min-content, so a cycle of `auto-cols-fr` tracks holding one wide item would stop
        // being an even cycle.
        Spacing("auto-cols", "grid-auto-columns");
        Spacing("auto-rows", "grid-auto-rows");

        Keywords("auto-cols", "grid-auto-columns", new() {
            ["auto"] = "auto", ["min"] = "min-content", ["max"] = "max-content", ["fr"] = "minmax(0, 1fr)"
        });

        Keywords("auto-rows", "grid-auto-rows", new() {
            ["auto"] = "auto", ["min"] = "min-content", ["max"] = "max-content", ["fr"] = "minmax(0, 1fr)"
        });

        // ⚠ <b>`grid-flow-col` is `column`, and the family is `grid-flow` rather than `grid`.</b>
        // Tailwind abbreviates in the class name and CSS does not in the value — the same trade
        // `flex-col` already makes here. The prefix has to be the whole of `grid-flow` because
        // `SplitName` takes the longest registered name and `grid` is one: without the longer entry,
        // `grid-flow-col` would split as the display family `grid` with the value `flow-col`, which is
        // not a keyword it has, and the class would be reported as a typo.
        Keywords("grid-flow", "grid-auto-flow", new() {
            ["row"] = "row", ["col"] = "column", ["dense"] = "dense",
            ["row-dense"] = "row dense", ["col-dense"] = "column dense"
        });

        // The four placement longhands. `Number` rather than `CountTemplate` because the value is
        // emitted as written — a line number is a line number — and because that is what makes
        // `-col-start-1` work: `TryNegate` flips the sign of a resolved number, and §8.3 counts a
        // negative line back from the end edge of the explicit grid.
        Number("col-start", "grid-column-start");
        Number("col-end", "grid-column-end");
        Number("row-start", "grid-row-start");
        Number("row-end", "grid-row-end");

        Keywords("col-start", "grid-column-start", new() { ["auto"] = "auto" });
        Keywords("col-end", "grid-column-end", new() { ["auto"] = "auto" });
        Keywords("row-start", "grid-row-start", new() { ["auto"] = "auto" });
        Keywords("row-end", "grid-row-end", new() { ["auto"] = "auto" });

        // ⚠ <b>`start` and `end` rather than `flex-start` and `flex-end`, which is the opposite of
        // what `items-*` above emits.</b> Both spellings reach `Align.FlexStart` through the bridge's
        // one alignment table, so the choice is about what a generated sheet reads like next to
        // Tailwind's documentation — and `justify-items: flex-start` is not a value CSS Box Alignment
        // gives that property, so a browser would drop the very declaration this engine honours.
        //
        // ⚠ <b>`normal` and the two `-safe` values are missing on purpose.</b> `justify-items: safe
        // center` is two tokens and the cascade hands the bridge one interned keyword, so it would
        // fall out of the alignment table and leave the property at its initial value with nothing
        // said — an inert class that looks like it works. They belong here the day
        // `LayoutStyleBuilder.Keywords.Alignments` has a reading of them.
        Keywords("justify-items", "justify-items", new() {
            ["start"] = "start", ["end"] = "end", ["center"] = "center", ["stretch"] = "stretch"
        });

        Keywords("justify-self", "justify-self", new() {
            ["auto"] = "auto", ["start"] = "start", ["end"] = "end",
            ["center"] = "center", ["stretch"] = "stretch"
        });

        // ── Gap and spacing ─────────────────────────────────────────────────────────────────
        Spacing("gap", "gap");
        Spacing("gap-x", "column-gap");
        Spacing("gap-y", "row-gap");

        Spacing("p", "padding");
        Spacing("px", "padding-left", "padding-right");
        Spacing("py", "padding-top", "padding-bottom");
        Spacing("pt", "padding-top");
        Spacing("pr", "padding-right");
        Spacing("pb", "padding-bottom");
        Spacing("pl", "padding-left");

        Spacing("m", "margin");
        Spacing("mx", "margin-left", "margin-right");
        Spacing("my", "margin-top", "margin-bottom");
        Spacing("mt", "margin-top");
        Spacing("mr", "margin-right");
        Spacing("mb", "margin-bottom");
        Spacing("ml", "margin-left");

        // The logical edges, which the layout reads as its own longhands rather than resolving to
        // left and right — so `ps-2` is the leading edge under `direction: rtl` as well as `ltr`,
        // and a panel written with them mirrors without a second stylesheet.
        Spacing("ps", "padding-inline-start");
        Spacing("pe", "padding-inline-end");
        Spacing("ms", "margin-inline-start");
        Spacing("me", "margin-inline-end");

        // ── Scroll insets ───────────────────────────────────────────────────────────────────
        //
        // ⚠ <b>Four longhands where `m-*` emits one shorthand, and the difference is ExCSS.</b>
        // `Spacing("m", "margin")` works because the parser expands `margin` on the way in, so the
        // cascade never sees a shorthand at all. ExCSS has never heard of `scroll-margin`, and
        // `ShorthandExpansion` only runs for the two placement properties and for values holding a
        // `var()` — so `scroll-margin: 4px` would reach a computed style intact and `ScrollView`
        // would read four absent longhands beside one declaration nothing looks at. That is the
        // `inset` hole `ShorthandExpansion` already records, and it is invisible from the class: the
        // CSS is valid, the cascade computes it, and the scroll does not move. Emitting the longhands
        // is not a workaround for it — there is simply no shorthand worth writing when nothing reads
        // one.
        //
        // ⚠ <b>`scroll-mx-*` is the physical pair where v4 spells it `scroll-margin-inline`, for the
        // reason `space-y-*` is `margin-bottom`</b> — see the remark below. The `-inline` and
        // `-block` shorthands are read by nobody here and expanded by nobody either, so v4's spelling
        // would resolve, compute and move nothing. The per-edge logical pair *is* read, because
        // `ScrollView.InsetOf` folds `-inline-start`/`-inline-end` against `direction` itself, so
        // `scroll-ms-*` and `scroll-me-*` mirror under `rtl` exactly as `ms-*` does.
        Spacing("scroll-m", "scroll-margin-top", "scroll-margin-right", "scroll-margin-bottom", "scroll-margin-left");
        Spacing("scroll-mx", "scroll-margin-left", "scroll-margin-right");
        Spacing("scroll-my", "scroll-margin-top", "scroll-margin-bottom");
        Spacing("scroll-mt", "scroll-margin-top");
        Spacing("scroll-mr", "scroll-margin-right");
        Spacing("scroll-mb", "scroll-margin-bottom");
        Spacing("scroll-ml", "scroll-margin-left");
        Spacing("scroll-ms", "scroll-margin-inline-start");
        Spacing("scroll-me", "scroll-margin-inline-end");

        Spacing("scroll-p", "scroll-padding-top", "scroll-padding-right", "scroll-padding-bottom", "scroll-padding-left");
        Spacing("scroll-px", "scroll-padding-left", "scroll-padding-right");
        Spacing("scroll-py", "scroll-padding-top", "scroll-padding-bottom");
        Spacing("scroll-pt", "scroll-padding-top");
        Spacing("scroll-pr", "scroll-padding-right");
        Spacing("scroll-pb", "scroll-padding-bottom");
        Spacing("scroll-pl", "scroll-padding-left");
        Spacing("scroll-ps", "scroll-padding-inline-start");
        Spacing("scroll-pe", "scroll-padding-inline-end");

        // ⚠ <b>`scroll` is a shorter prefix than `scroll-m` and registering it here is safe only
        // because `SplitName` takes the longest.</b> `scroll-mt-4` matches `scroll-mt` before it can
        // match `scroll`, and `scroll-smooth` cannot match `scroll-m` because the character after the
        // prefix has to be a hyphen. Both are `SplitName`'s existing rules rather than anything this
        // family needed, and `ThemeAndScannerTests` is what would notice if that changed.
        Keywords("scroll", "scroll-behavior", new Dictionary<string, string>(StringComparer.Ordinal) {
            ["auto"] = "auto", ["smooth"] = "smooth"
        });

        // ⚠ <b>`contain` and `none` are registered although `ScrollView` treats them alike</b>, and
        // that is not the inert-class defect: the property moves a channel — `auto` chains the wheel
        // outwards and both of the others do not — so a reader acts on it. What the two values share
        // is that this engine has no rubber-band or pull-to-refresh for `none` to additionally
        // suppress, which is a documented equivalence rather than a missing half. See
        // `OverscrollBehavior`.
        var overscroll = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["auto"] = "auto", ["contain"] = "contain", ["none"] = "none"
        };

        Keywords("overscroll", "overscroll-behavior", overscroll);
        Keywords("overscroll-x", "overscroll-behavior-x", overscroll);
        Keywords("overscroll-y", "overscroll-behavior-y", overscroll);

        // ── The two families that are a rule over children ──────────────────────────────────
        //
        // ⚠ <b>`space-x-4` is not a property on the element that carries it.</b> It is
        // `& > :not(:last-child) { margin-inline-end: … }` — a margin on every child but the last —
        // and the reason it never got written here is that the family table had no way to say so.
        // `Family.Scope` is that way, and the selector engine needed nothing: a child combinator, a
        // `:not()` and `:last-child` all compile and match today.
        //
        // ⚠ <b>`space-y-*` emits the physical `margin-bottom` where v4 emits `margin-block-end`, and
        // the difference is measured rather than assumed.</b> `margin-block-start`/`-end` are interned
        // by nobody — `LayoutStyleBuilder.EdgeNames` reads `-left`, `-top`, `-right`, `-bottom`,
        // `-inline-start` and `-inline-end` and no block pair — so v4's spelling resolves, computes,
        // and moves nothing, which is exactly the inert family this table is not allowed to add. The
        // physical pair is not an approximation of it either: `Vixen.Ui.Layout` has no writing mode,
        // so the block axis *is* top-to-bottom in every configuration the engine can be in, and
        // `margin-block-end` would mean `margin-bottom` on every element that ever resolved it.
        // `space-x-*` keeps v4's logical spelling because `margin-inline-end` is read and mirrors
        // under `direction: rtl`, which is the whole point of it.
        Between("space-x", ValueKind.Spacing, ["margin-inline-end"]);
        Between("space-y", ValueKind.Spacing, ["margin-bottom"]);

        // ── Sizing ──────────────────────────────────────────────────────────────────────────
        Size("w", "width");
        Size("h", "height");
        Size("size", "width", "height");
        Size("min-w", "min-width");
        Size("min-h", "min-height");
        Size("max-w", "max-width");
        Size("max-h", "max-height");

        // ── Position ────────────────────────────────────────────────────────────────────────
        Static("static", "position", "static");
        Static("relative", "position", "relative");
        Static("absolute", "position", "absolute");
        Size("inset", "top", "right", "bottom", "left");
        Size("inset-x", "left", "right");
        Size("inset-y", "top", "bottom");
        Size("top", "top");
        Size("right", "right");
        Size("bottom", "bottom");
        Size("left", "left");
        Size("start", "inset-inline-start");
        Size("end", "inset-inline-end");

        // ⚠ <b>v4's four logical insets, and `start-*`/`end-*` above are the compatibility spelling
        // of the first two rather than the other way round.</b> `docs/plan/43` § D5 lists
        // `start-*`/`end-*` among the utilities v4 keeps only in `compat/legacy-utilities.ts` —
        // registered, undocumented — and `inset-s/e/bs/be` among what v4.0 *added*. The rule that
        // section states is "implement the documented name and not the compatibility one", so these
        // four are the names a person reading Tailwind's documentation will write. The two legacy
        // ones stay because removing a registered family is a breaking change to every sheet in the
        // tree, and because they cost one table entry each.
        //
        // ⚠ <b>The inline pair is logical and the block pair is physical, and that asymmetry is the
        // whole of what is worth reading here.</b> `inset-inline-start`/`-end` are longhands
        // `LayoutStyleBuilder.EdgeNames.ForInset` interns and the layout mirrors under
        // `direction: rtl` — measured, `[hit,layout,paint]` — so emitting them keeps `inset-s-4` the
        // leading edge in both directions. `inset-block-start`/`-end` are interned by nobody and
        // measure inert on every scene, and the physical pair is not an approximation of them:
        // `Vixen.Ui.Layout` has no writing mode, so the block axis *is* top-to-bottom in every
        // configuration the engine can be in and `inset-block-start` would mean `top` on every
        // element that ever resolved it. Same argument, same measurement, as `space-y-*` above.
        Size("inset-s", "inset-inline-start");
        Size("inset-e", "inset-inline-end");
        Size("inset-bs", "top");
        Size("inset-be", "bottom");

        Number("z", "z-index");

        Static("box-border", "box-sizing", "border-box");
        Static("box-content", "box-sizing", "content-box");

        // ── Typography ──────────────────────────────────────────────────────────────────────
        // `start` and `end` alongside the physical four, because the renderer resolves them against
        // `direction` — the same property the logical edges above resolve against, so `text-end` and
        // `pe-2` land on the same side of a mirrored panel.
        Register(new Family("text", ValueKind.FontSize, ["font-size"], new Dictionary<string, string>(StringComparer.Ordinal) {
            ["left"] = "text-align:left", ["center"] = "text-align:center",
            ["right"] = "text-align:right", ["justify"] = "text-align:justify",
            ["start"] = "text-align:start", ["end"] = "text-align:end"
        }));

        Register(new Family("font", ValueKind.FontWeight, ["font-weight"]));
        // Two genuinely different things behind one prefix, and the difference is not cosmetic:
        // `leading-6` is a length that every descendant inherits as written, and `leading-normal` is
        // a *ratio* each descendant multiplies by its own font size. The renderer keeps them apart,
        // so the utilities have to as well — a heading inside a body with `leading-relaxed` wants
        // the ratio, and the same value in pixels would give it the body's line height.
        Register(new Family("leading", ValueKind.Spacing, ["line-height"], new Dictionary<string, string>(StringComparer.Ordinal) {
            ["none"] = "line-height:1",
            ["tight"] = "line-height:1.25",
            ["snug"] = "line-height:1.375",
            ["normal"] = "line-height:1.5",
            ["relaxed"] = "line-height:1.625",
            ["loose"] = "line-height:2"
        }));
        Spacing("tracking", "letter-spacing");

        Keywords("whitespace", "white-space", new() {
            ["normal"] = "normal", ["nowrap"] = "nowrap", ["pre"] = "pre", ["pre-wrap"] = "pre-wrap"
        });

        // ⚠ <b>The slant, and it is registered here rather than being another value of `font`
        // because v4 spells it as two bare words.</b> `italic` and `not-italic` are `font-style`;
        // `font-*` is the weight scale. A `font-italic` family would be this project inventing a
        // class name, which is the failure `bg-conic-<angle>` is recorded under.
        //
        // ⚠ <b>The reader was already here and only the family was missing, which is the opposite of
        // this table's usual gap and worth saying so nobody looks for the engine work.</b>
        // `UiDocument.FontStyleOf` reads the property, `font-style` is in `InheritedProperties`, and
        // `FontRegistry.Slanted` implements CSS Fonts 4 § 5.2's slant matching in full — italic, then
        // oblique, then upright. So `italic` picks the italic face of the family when one is
        // registered and honestly falls back to the upright when none is, exactly as `font-bold`
        // does for a family with no bold. What is *not* on offer is a synthesised slant: Vixen does
        // not shear an upright face, and `FontRegistry.Slanted`'s own remark says so.
        Static("italic", "font-style", "italic");
        Static("not-italic", "font-style", "normal");

        // ── Wrapping ────────────────────────────────────────────────────────────────────────
        // ⚠ <b>`overflow-wrap` and not `word-break`, and the two are not interchangeable however
        // similar the class names look.</b> `UiDocument.WrapModeOf` reads `overflow-wrap` and maps
        // `anywhere` and `break-word` onto `TextWrapMode.Anywhere`, which `LineWrapper` applies at a
        // *grapheme* boundary when one unbreakable run is wider than the whole line. That is what
        // CSS Text 3 § 5.5 says both keywords mean. `word-break: break-all` means something else —
        // every character is a break opportunity, so a word that would have fitted on the next line
        // is still split at the end of this one — and nothing reads that property. Registering
        // `break-all` here would give the same declaration two spellings, one of which is a lie.
        //
        // ⚠ <b>Vixen does not distinguish `anywhere` from `break-word`, and both are registered
        // anyway.</b> CSS Sizing § 5.2 separates them only by their min-content contribution:
        // `anywhere` lets the intrinsic minimum shrink to one grapheme and `break-word` does not.
        // `Vixen.Ui.Layout` has no intrinsic-minimum stage that consults either, so the two are one
        // behaviour here — a stated deviation rather than a missing keyword, and the same shape as
        // `WrapsOf` answering one of `white-space`'s three questions.
        Keywords("wrap", "overflow-wrap", new() {
            ["anywhere"] = "anywhere", ["break-word"] = "break-word", ["normal"] = "normal"
        });

        // v3's spelling of `wrap-break-word`, which v4 keeps. The same declaration under the name
        // people have in their fingers, exactly as `start-*` is kept beside `inset-s-*`.
        Static("break-words", "overflow-wrap", "break-word");

        // ⚠ <b>Tailwind's `break-normal` is two declarations and this is one, and the missing half is
        // deliberate.</b> v4 emits `overflow-wrap: normal; word-break: normal`. The second is the
        // initial value of a property nothing in this engine reads, so emitting it would add an
        // inert property to the gate's ledger and buy a class exactly nothing — a no-op twice over.
        // The half that is here is not a no-op: `overflow-wrap` inherits, so this is how a child
        // escapes a `break-words` its container asked for, which is the same argument `text-clip`
        // earns its place with. When `word-break` gains a reader, the other half belongs here.
        Static("break-normal", "overflow-wrap", "normal");

        // The two halves of `text-overflow` under the prefix v4 gives them. Registered as a second
        // keyword table on `text` rather than as a family of its own, for the reason the type's
        // remarks give: `text-ellipsis` has to be the family `text` with the value `ellipsis`, or the
        // class becomes a family with no value and `text-sm` stops resolving.
        //
        // ⚠ <c>text-clip</c> earns its place rather than being symmetry. It is CSS's initial value and
        // would be a no-op on its own — but `text-overflow` inherits in Vixen (see
        // `UiDocument.EllipsisOf`), so it is the opt-out a child needs to escape an ellipsis its
        // container asked for, and there is no other way to write that.
        Keywords("text", "text-overflow", new() {
            ["ellipsis"] = "ellipsis", ["clip"] = "clip"
        });

        // ⚠ <b>Two of this root's four, and the two that are absent are absent on purpose.</b>
        // `text-wrap` is CSS Text 4's half of `white-space`, and `UiDocument.WrapsOf` reads it beside
        // `white-space` — so `text-nowrap` genuinely stops the wrapping and `text-wrap` is the
        // inherited opt-out from it, the same shape as `text-clip`.
        //
        // `text-balance` and `text-pretty` are not registered and must not be. Both ask for a better
        // *choice* of breaks rather than for breaking to stop: balance minimises the raggedness of
        // the whole paragraph and pretty forbids a one-word last line. `LineWrapper` is greedy
        // first-fit by an argued decision — see its remarks — so both would resolve, compute, reach
        // `WrapsOf`, fall through to "wraps", and produce exactly the lines `text-wrap` produces. Two
        // classes that differ from the default in name only is the inert family this table's gate
        // exists to keep out, and it would be invisible to that gate: the property is read.
        Keywords("text", "text-wrap", new() {
            ["wrap"] = "wrap", ["nowrap"] = "nowrap"
        });

        Keywords("align", "vertical-align", new() {
            ["top"] = "top", ["middle"] = "middle", ["bottom"] = "bottom", ["baseline"] = "baseline"
        });

        // ── Text decoration ─────────────────────────────────────────────────────────────────
        // ⚠ <b>Four families for one line, and it is `text-decoration-line` that gets its own class
        // names rather than a value.</b> v4 spells the lines as bare words — `underline`, not
        // `decoration-underline` — because they are the ones anybody writes, and `decoration-*` is
        // reserved for the three properties that modify them. Following that is not deference: a
        // `decoration-underline` family would collide with `decoration-2` and `decoration-red-500`
        // under one prefix that is already carrying three meanings.
        Static("underline", "text-decoration-line", "underline");
        Static("overline", "text-decoration-line", "overline");
        Static("line-through", "text-decoration-line", "line-through");
        Static("no-underline", "text-decoration-line", "none");

        // ⚠ <b>`underline-offset` is a longer name than `underline`, and that is the only reason
        // `underline-offset-2` is not read as the family `underline` with a stray value.</b>
        // `SplitName`'s longest-first sort settles it, exactly as it settles `rounded-tl` against
        // `rounded` — worth saying here because these two are the first pair where the shorter name
        // is a `Static` family, so the failure would not be an unknown-token diagnostic but a silent
        // `underline` with the offset dropped.
        //
        // ⚠ <b>A keyword table rather than `Spacing`, because v4's offsets are a fixed scale and not
        // the spacing one.</b> `underline-offset-3` is not a class in any Tailwind, and registering
        // the spacing scale here would invent five that resolve, compute and draw — real classes
        // this project made up, which is the failure `bg-conic-<angle>` is recorded under.
        Keywords("underline-offset", "text-underline-offset", new() {
            ["0"] = "0px", ["1"] = "1px", ["2"] = "2px", ["4"] = "4px", ["8"] = "8px", ["auto"] = "auto"
        });

        // ⚠ <b>One prefix, three properties, and the resolution order is the same one `text` uses:
        // keywords first, then the family's own kind.</b> `decoration-2` is a thickness because `2`
        // is in the keyword table; `decoration-accent` is a colour because it is not. The colour
        // registration comes first so that the family's `ValueKind` is the fallthrough, and
        // `Register` merges the two keyword tables into it.
        //
        // ⚠ <b>`decoration-dotted`, `-dashed` and `-wavy` are deliberately absent — the same
        // measurement `divide-solid` is absent under.</b> There is no dash pattern in `Vixen.Ui` and
        // no stroke that could carry one: `border-style` is emitted by nothing and read by nothing,
        // and a wave is a path where every other decoration is a rectangle. All three would resolve
        // cleanly, compute a value and paint a solid line, which is the inert family
        // `UtilityConsumptionGateTests` exists to keep out. `solid` and `double` are registered
        // because both are genuinely drawn — see `TextRun.Bars`, where `double` is two bars — so
        // `text-decoration-style` is a property the engine reads rather than one it stores.
        Color("decoration", "text-decoration-color");

        Keywords("decoration", "text-decoration-thickness", new() {
            ["0"] = "0px", ["1"] = "1px", ["2"] = "2px", ["4"] = "4px", ["8"] = "8px",
            ["auto"] = "auto", ["from-font"] = "from-font"
        });

        Keywords("decoration", "text-decoration-style", new() {
            ["solid"] = "solid", ["double"] = "double"
        });

        // ── Colours ─────────────────────────────────────────────────────────────────────────
        Color("bg", "background-color");
        Color("fill", "fill");
        Color("stroke", "stroke");

        // ⚠ <b>A ring is a <c>box-shadow</c> with a width, and this family used to emit
        // <c>outline-color</c> — which no version of Tailwind has ever emitted for it.</b> Not v4's
        // reading and not v3's either: v3 is where the ring was *introduced* as a box-shadow, and its
        // colour utility set `--tw-ring-color`. So the debt filed under `outline-color` could never
        // have come due, exactly like `grid-cols-3`'s `grid-template-columns: 3` and the transform
        // families' `--scale` — an emission no engine could consume, under a line that truthfully
        // said nothing read it. See `UtilityComposition.Ring`.
        //
        // <c>BorderEdge</c> because the ambiguity is precisely `border`'s: `ring-2` is a width and
        // `ring-accent` is a colour, one prefix, told apart by the value's shape. The bare `ring` is
        // one pixel, which is v4 — v3's three-pixel `ring` became `ring-3` (§ D5).
        Register(new Family(
            "ring",
            ValueKind.BorderEdge,
            [UtilityComposition.RingWidth],
            ColorProperties: [UtilityComposition.RingColor],
            Alongside: [new UtilityDeclaration("box-shadow", UtilityComposition.Ring())]
        ));

        // ── Gradients: the composed families ────────────────────────────────────────────────
        //
        // ⚠ <b>None of these three emits `background-image`.</b> They set the fragments in
        // `UtilityComposition`, and `bg-linear-*` is the only thing here that emits a declaration a
        // consumer could read. That is the whole shape doc 43 calls `composed`, and the reason it is
        // done this way rather than folded together when the sheet is generated is written out on
        // `UtilityComposition` itself: `hover:from-accent-hover` is decided at use time.
        GradientStop("from", UtilityComposition.GradientFrom, UtilityComposition.GradientFromPosition);
        GradientStop("to", UtilityComposition.GradientTo, UtilityComposition.GradientToPosition);

        // The one family with an alongside declaration. `from-*` and `to-*` need none, because the
        // two-stop list is already `--tw-gradient-stops`' initial value.
        GradientStop(
            "via",
            UtilityComposition.GradientVia,
            UtilityComposition.GradientViaPosition,
            new UtilityDeclaration(UtilityComposition.GradientStops, UtilityComposition.StopList(via: true))
        );

        // The assemblers. Eight directions, and the direction is written into each one rather than
        // parked in a fragment of its own — Tailwind keeps a `--tw-gradient-position` so that
        // `bg-radial` and `bg-conic` can share one stop list, which buys nothing while the position
        // is a compile-time constant in all ten of these.
        //
        // ⚠ `bg-linear` is registered *after* `bg`, and it still wins for `bg-linear-to-r`, because
        // `SplitName` sorts longest-first at the bottom of this method rather than trusting the order
        // things appear in here. `bg-accent` is unaffected, and so are `bg-radial` and `bg-conic`.
        Keywords("bg-linear", "background-image", new() {
            ["to-t"] = Gradient("linear", "to top"), ["to-tr"] = Gradient("linear", "to top right"),
            ["to-r"] = Gradient("linear", "to right"), ["to-br"] = Gradient("linear", "to bottom right"),
            ["to-b"] = Gradient("linear", "to bottom"), ["to-bl"] = Gradient("linear", "to bottom left"),
            ["to-l"] = Gradient("linear", "to left"), ["to-tl"] = Gradient("linear", "to top left")
        });

        // ⚠ <b>The two round shapes take no geometry at all, and that is Tailwind's own default
        // rather than a simplification.</b> `bg-radial` is `radial-gradient(in oklab, …)` — no
        // `at`, no ending shape — because CSS's defaults are a centred farthest-corner ellipse, and
        // `bg-conic` is the same story with a sweep from twelve o'clock. Tailwind reaches the
        // positioned forms only through its arbitrary-value syntax, and those are what
        // `GradientRefusal.Extent` refuses: they need a centre in `UiShape`, which is a whole further
        // `Vector4` for a form no theme in this repository writes.
        Static("bg-radial", "background-image", Gradient("radial", string.Empty));
        Static("bg-conic", "background-image", Gradient("conic", string.Empty));

        // ── Borders ─────────────────────────────────────────────────────────────────────────
        // ⚠ `border-2` is two *pixels* where `p-2` is two spacing steps, which is Tailwind's choice
        // and the right one. A border is a hairline or it is not; scaling it with the spacing base
        // would mean a theme with a larger base silently thickened every rule in the editor.
        BorderEdge("border", ["border-width"], ["border-color"]);
        BorderEdge("border-x", ["border-left-width", "border-right-width"], ["border-left-color", "border-right-color"]);
        BorderEdge("border-y", ["border-top-width", "border-bottom-width"], ["border-top-color", "border-bottom-color"]);
        BorderEdge("border-t", ["border-top-width"], ["border-top-color"]);
        BorderEdge("border-r", ["border-right-width"], ["border-right-color"]);
        BorderEdge("border-b", ["border-bottom-width"], ["border-bottom-color"]);
        BorderEdge("border-l", ["border-left-width"], ["border-left-color"]);
        BorderEdge("border-s", ["border-inline-start-width"], ["border-inline-start-color"]);
        BorderEdge("border-e", ["border-inline-end-width"], ["border-inline-end-color"]);

        // ⚠ <b>The block pair, physical for the same reason `inset-bs-*` and `space-y-*` are.</b>
        // `border-block-start-width` and `border-block-end-width` are interned by nothing —
        // `LayoutStyleBuilder.EdgeNames.For(table, "border-width", "border", "-width")` reads the
        // four physical edges and the two *inline* logical ones — and both measure inert on every
        // scene. With no writing mode in `Vixen.Ui.Layout` the block axis is always top-to-bottom,
        // so `border-block-start` is `border-top` on every element that could ever resolve it, and
        // the physical spelling is the same declaration written in the name the engine reads.
        //
        // ⚠ Note what this does *not* inherit from `border-s`/`border-e`: those two are the only
        // partial pair in the table, because their widths are read and their colours are not — the
        // two `border-inline-*-color` lines in `InertProperties.txt`. Both physical colours are
        // painted, so these two are read on every longhand they set.
        //
        // ⚠ No `border-block-start-style`. v4 emits one alongside the width and Vixen's physical
        // edges do not, for the reason `divide-solid` is absent: `border-style` is emitted by
        // nothing here and read by nothing either. Following `border-t` rather than following
        // Tailwind is what keeps this from being one inert longhand out of two.
        BorderEdge("border-bs", ["border-top-width"], ["border-top-color"]);
        BorderEdge("border-be", ["border-bottom-width"], ["border-bottom-color"]);

        // ⚠ <b>`divide-*` is `border-*` written on the gaps rather than on the boxes</b>, so it is
        // the same three kinds of value — a width, a bare form meaning one pixel, and a colour —
        // scoped to `> :not(:last-child)`. One rule per class still, and the rule is what puts a
        // single hairline between two rows instead of two touching ones.
        //
        // ⚠ <b>`divide-x` is the *end* edge and `divide-y` the *bottom* one, which is v4's choice and
        // not an arbitrary half of the pair.</b> Tailwind emits both edges of each axis — a zero on
        // one and the width on the other — so that `divide-x-reverse` can swap them by flipping a
        // custom property. The zero is what this cannot follow: `StyleValueParser` has no `calc()`,
        // so the reverse fragment has nothing to multiply by and `divide-x-reverse` is not
        // registered. Emitting the leading `0` anyway would buy nothing and cost something real — it
        // would out-specify a child's own `border-s-2` and silently erase it — so the family writes
        // the one edge it means. Same argument for `space-x-*`, which is why it writes no leading
        // margin either.
        //
        // ⚠ <b>No colour longhands, and that is what `ColorProperties: null` says.</b> `divide-x-2`
        // is a width and `divide-x-accent` is not a class Tailwind has; the colour is written
        // `divide-accent`, on the family below, and reaches all four physical `border-color`
        // longhands through ExCSS's expansion. `TryBorderEdge` reports the unregistered spelling as
        // unknown rather than inventing an edge colour for it.
        //
        // ⚠ <b>`divide-solid` and the rest of the style keywords are deliberately absent.</b>
        // `border-style` is emitted by nothing here and read by nothing either — measured, not
        // assumed: it resolves into four longhands and moves no channel in any scene. A
        // `divide-dashed` that computed a value and drew a solid line is precisely the inert family
        // `UtilityConsumptionGateTests` exists to keep out.
        Between("divide-x", ValueKind.BorderEdge, ["border-inline-end-width"]);
        Between("divide-y", ValueKind.BorderEdge, ["border-bottom-width"]);
        Between("divide", ValueKind.Color, ["border-color"]);

        // ⚠ <b>Four of these names are prefixes of others — <c>rounded</c> of <c>rounded-t</c>, and
        // <c>rounded-t</c> of <c>rounded-tl</c> — and it is `SplitName`'s longest-first sort that
        // settles them, not the order they appear in here.</b> Worth saying because the sort happens
        // once at the bottom of this method and is easy to read as a tidiness pass: without it
        // `rounded-tl-lg` would split as the family `rounded` with the value `tl-lg`, which is not a
        // radius token, and the class would be reported as an unrecognised typo rather than as a
        // table that needed sorting.
        //
        // ⚠ <b>A side is two corners and not an edge.</b> `rounded-t` writes the two *top* corner
        // radii, which is why its property list names `border-top-left-radius` and
        // `border-top-right-radius` rather than anything called "top". CSS has no per-side radius,
        // because a radius belongs to a corner and every corner is shared by two sides.
        Radius("rounded-tl", "border-top-left-radius");
        Radius("rounded-tr", "border-top-right-radius");
        Radius("rounded-br", "border-bottom-right-radius");
        Radius("rounded-bl", "border-bottom-left-radius");

        Radius("rounded-t", "border-top-left-radius", "border-top-right-radius");
        Radius("rounded-r", "border-top-right-radius", "border-bottom-right-radius");
        Radius("rounded-b", "border-bottom-right-radius", "border-bottom-left-radius");
        Radius("rounded-l", "border-top-left-radius", "border-bottom-left-radius");

        Radius("rounded", "border-radius");

        // ── Effects ─────────────────────────────────────────────────────────────────────────
        // `opacity-50` is half, not fifty. CSS's `opacity` runs 0 to 1 and the utility scale runs
        // 0 to 100, because nobody writes `opacity-0.5`.
        Register(new Family("opacity", ValueKind.Fraction, ["opacity"]));
        // ⚠ <b>Composed, not <c>Spacing("blur", "--blur")</c>, and the change is what closed #28's
        // half of A8.</b> `--blur` was a name of this engine's own invention that nothing assembled
        // and nothing could read; the fragment and the assembler put the length inside a real
        // `filter` declaration, which `DrawListBuilder` now reads. See `UtilityComposition.Filter`.
        Register(new Family(
            "blur",
            ValueKind.Spacing,
            [UtilityComposition.Blur],
            Alongside: [new UtilityDeclaration("filter", UtilityComposition.Filter())]
        ));

        // ── The colour filters ──────────────────────────────────────────────────────────
        //
        // ⚠ <b>Seven families, one shape, and every one of them is an assembler as well as a
        // contributor — which is `translate-x`'s arrangement and not the gradient stops'.</b> Each
        // sets its own fragment and emits the whole `filter` declaration beside it, so
        // `grayscale` alone works and `grayscale blur-2 brightness-125` composes: three rules write
        // the identical `filter` value and differ only in which fragment they set. The alternative
        // is Tailwind v3's separate enabling class, which v4 dropped because forgetting it looked
        // exactly like the utilities being broken.
        //
        // ⚠ <b>`Fraction` for six of them, because Tailwind's scale runs in hundredths and CSS's
        // does not.</b> `brightness-125` is `1.25`, `grayscale-50` is `0.5`. Emitting the bare count
        // would be `brightness(125)` — valid CSS, a hundred and twenty-five times the exposure, and
        // a white rectangle where the panel was.
        //
        // ⚠ <b>And the bare forms, which are half of why anyone writes these.</b> `grayscale`,
        // `invert` and `sepia` with no value mean *fully*, and the three whose identity is one have
        // no bare form at all — a bare `brightness` would have to mean something, and Tailwind does
        // not define it. The empty key is the keyword table's, so the pair is written out.
        Filter("brightness", UtilityComposition.Brightness);
        Filter("contrast", UtilityComposition.Contrast);
        Filter("grayscale", UtilityComposition.Grayscale, bare: "1");
        Filter("invert", UtilityComposition.Invert, bare: "1");
        Filter("saturate", UtilityComposition.Saturate);
        Filter("sepia", UtilityComposition.Sepia, bare: "1");

        // ⚠ <b>An angle, and the one of the eight that is not a proportion.</b> `hue-rotate-90` is
        // ninety degrees, so the unit has to be appended — which is `CountTemplate`'s whole job, and
        // the same reason `rotate` uses it. `StyleValueParser` refuses `hue-rotate(90)` outright, so
        // a family emitting the bare count here would produce a declaration the engine drops *whole*,
        // taking every other filter on the element with it.
        Register(new Family(
            "hue-rotate",
            ValueKind.CountTemplate,
            [UtilityComposition.HueRotate],
            Template: "{0}deg",
            Alongside: [new UtilityDeclaration("filter", UtilityComposition.Filter())]
        ));

        // ── Masks ───────────────────────────────────────────────────────────────────────
        //
        // ⚠ <b>Twenty-five roots now, and what is still missing is `mask-origin-*`,
        // `mask-position-*`, `mask-size-*` and `mask-repeat-*` — all four of which describe where a
        // mask image is placed relative to a box it does not already fill.</b> A gradient sized to
        // the border box needs none of them, and registering one would emit a property nothing reads,
        // which is exactly what the consumption gate is for. See `InertProperties.txt` and doc 43.
        //
        // ⚠ <b>`mask-t-from-*` and its eleven siblings are here because `UiLayer` carries a mask
        // <i>list</i>.</b> They are per-edge ramps that only mean anything combined, and combining
        // them is what `mask-composite` does — so nine of these roots waited on the list rather than
        // on anything about gradients.
        //
        // ⚠ <b>Every one of these is an assembler as well as a contributor, which is the colour
        // filters' arrangement rather than the gradient stops'.</b> `from-accent` alone paints
        // nothing until a `bg-linear-*` says what shape to paint; `mask-linear-from-50%` alone has to
        // mask, because there is no separate "turn masking on" class in v4 and forgetting one would
        // look exactly like the utility being broken.
        Mask("mask-linear-from", UtilityComposition.MaskFrom, UtilityComposition.MaskFromPosition, UtilityComposition.MaskLinear, Linear);
        Mask("mask-linear-to", UtilityComposition.MaskTo, UtilityComposition.MaskToPosition, UtilityComposition.MaskLinear, Linear);
        Mask("mask-radial-from", UtilityComposition.MaskFrom, UtilityComposition.MaskFromPosition, UtilityComposition.MaskRadial, Radial);
        Mask("mask-radial-to", UtilityComposition.MaskTo, UtilityComposition.MaskToPosition, UtilityComposition.MaskRadial, Radial);
        Mask("mask-conic-from", UtilityComposition.MaskFrom, UtilityComposition.MaskFromPosition, UtilityComposition.MaskConic, Conic);
        Mask("mask-conic-to", UtilityComposition.MaskTo, UtilityComposition.MaskToPosition, UtilityComposition.MaskConic, Conic);

        // ⚠ <b>The twelve edge ramps, and `mask-x-*` and `mask-y-*` are pairs rather than shorthands
        // for a wider box.</b> `mask-x-from-50%` sets the near stop of the left ramp *and* of the
        // right one — two entries in the list, intersected — which is why `Family.Positions` is
        // several rather than one. A shorthand that widened a single ramp would fade one side and
        // brighten the other.
        MaskEdgeRamp("mask-t-from", ["top"], near: true);
        MaskEdgeRamp("mask-t-to", ["top"], near: false);
        MaskEdgeRamp("mask-r-from", ["right"], near: true);
        MaskEdgeRamp("mask-r-to", ["right"], near: false);
        MaskEdgeRamp("mask-b-from", ["bottom"], near: true);
        MaskEdgeRamp("mask-b-to", ["bottom"], near: false);
        MaskEdgeRamp("mask-l-from", ["left"], near: true);
        MaskEdgeRamp("mask-l-to", ["left"], near: false);
        MaskEdgeRamp("mask-x-from", ["left", "right"], near: true);
        MaskEdgeRamp("mask-x-to", ["left", "right"], near: false);
        MaskEdgeRamp("mask-y-from", ["top", "bottom"], near: true);
        MaskEdgeRamp("mask-y-to", ["top", "bottom"], near: false);

        // ⚠ <b>The operator, as four keywords, and it is worth having even though every mask utility
        // already writes one.</b> `intersect` is what the families emit because it is what makes an
        // unfilled layer harmless; an author combining a radial and a conic deliberately may well
        // want `subtract` or `exclude` instead, and there is no other way to say so from a class.
        Static("mask-add", "mask-composite", "add");
        Static("mask-subtract", "mask-composite", "subtract");
        Static("mask-intersect", "mask-composite", "intersect");
        Static("mask-exclude", "mask-composite", "exclude");

        // ⚠ <b>The two angles, and they set a fragment rather than writing the whole function.</b>
        // `mask-linear-45 mask-linear-from-30%` is two classes that have to agree about one
        // `mask-image`, which is the situation the fragments exist for — the same one
        // `translate-x-2 translate-y-4` is in. `CountTemplate` appends the unit for
        // `hue-rotate`'s reason: `StyleValueParser` refuses a bare number where an angle belongs, and
        // a bare one here would invalidate the whole assembled declaration.
        Register(new Family(
            "mask-linear",
            ValueKind.CountTemplate,
            [UtilityComposition.MaskLinearAngle],
            Template: "{0}deg",
            Alongside: MaskAlongside(UtilityComposition.MaskLinear, Linear)
        ));

        Register(new Family(
            "mask-conic",
            ValueKind.CountTemplate,
            [UtilityComposition.MaskConicAngle],
            Template: "{0}deg",
            Alongside: MaskAlongside(UtilityComposition.MaskConic, Conic)
        ));

        // ⚠ Its own family rather than a keyword on one of the above, because `mask-none` has to work
        // where nothing else set a mask — a keyword hanging off `mask-linear` would need the author to
        // have written a `mask-linear-*` first.
        Static("mask-none", "mask-image", "none");

        // A token names a whole declaration rather than a number, because a shadow is a designed
        // thing: its offset, blur and alpha are chosen together to read as one height above the
        // surface. `shadow-none` is here rather than in the theme so that turning one off never
        // depends on somebody having remembered to define it.
        Register(new Family("shadow", ValueKind.Shadow, ["box-shadow"], new Dictionary<string, string>(StringComparer.Ordinal) {
            ["none"] = "box-shadow:none"
        }));

        // ── Transforms ──────────────────────────────────────────────────────────────────────
        //
        // ⚠ <b>All four of these emitted a <c>--</c> name of their own invention, and only two of
        // them have stopped.</b> `--translate-x`, `--scale` and `--rotate` are not CSS properties;
        // they are not fragments either, because nothing assembled them. They were values parked in a
        // spelling no engine anywhere — this one, or a browser — will ever look at, which is the same
        // failure `grid-cols-3` had when it emitted `grid-template-columns: 3`: a family that would
        // have gone on doing nothing even once a reader existed, and a debt recorded against the
        // wrong name. See the closed block in `InertProperties.txt`.
        //
        // The two translations are composed now — a fragment each, and one `translate` between them
        // — and the engine reads `translate` in `UiDocument.Accumulate`. `scale` and `rotate` emit
        // the properties CSS actually has, and nothing reads either yet; their debt is recorded
        // against those names, so the day a reader arrives the gate's expiry check is what says so.
        Translate("translate-x", UtilityComposition.TranslateX);
        Translate("translate-y", UtilityComposition.TranslateY);

        // ⚠ <b>A percentage, because Tailwind's scale runs in hundredths.</b> `scale-150` is one and
        // a half, not a hundred and fifty — v4 emits `scale: 150%` and CSS reads a percentage on this
        // property as a ratio. Emitting the bare count, which is what `Number` did into `--scale`,
        // would make `scale-150` mean a hundred and fifty times the size the day something read it.
        CountTemplate("scale", "{0}%", "scale");

        // ⚠ And an angle, for the same class of reason: `rotate: 45` is not a value CSS has. The unit
        // is the whole difference between a declaration a browser honours and one it drops.
        CountTemplate("rotate", "{0}deg", "rotate");

        // ── Transitions ─────────────────────────────────────────────────────────────────────
        Register(new Family("transition", ValueKind.Static, ["transition-property"], new Dictionary<string, string>(StringComparer.Ordinal) {
            [string.Empty] = "all"
        }));

        Register(new Family("duration", ValueKind.Duration, ["transition-duration"]));

        Keywords("ease", "transition-timing-function", new() {
            ["linear"] = "linear", ["in"] = "ease-in", ["out"] = "ease-out", ["in-out"] = "ease-in-out"
        });

        // ── Interactivity ───────────────────────────────────────────────────────────────────
        // The set `UiCursor` has a reading of, and no more — a keyword the document cannot map is a
        // rule that resolves to the host's default, which is indistinguishable from having written
        // nothing and is not worth a family entry.
        Keywords("cursor", "cursor", new() {
            ["auto"] = "auto", ["default"] = "default", ["none"] = "none", ["pointer"] = "pointer",
            ["text"] = "text", ["move"] = "move", ["not-allowed"] = "not-allowed",
            ["grab"] = "grab", ["grabbing"] = "grabbing", ["crosshair"] = "crosshair",
            ["wait"] = "wait", ["progress"] = "progress",
            ["col-resize"] = "col-resize", ["row-resize"] = "row-resize",
            ["ew-resize"] = "ew-resize", ["ns-resize"] = "ns-resize"
        });

        Keywords("select", "user-select", new() {
            ["none"] = "none", ["text"] = "text", ["all"] = "all", ["auto"] = "auto"
        });

        Keywords("pointer-events", "pointer-events", new() { ["none"] = "none", ["auto"] = "auto" });

        Keywords("overflow", "overflow", new() {
            ["auto"] = "auto", ["hidden"] = "hidden", ["visible"] = "visible", ["scroll"] = "scroll"
        });

        Keywords("overflow-x", "overflow-x", new() {
            ["auto"] = "auto", ["hidden"] = "hidden", ["visible"] = "visible", ["scroll"] = "scroll"
        });

        Keywords("overflow-y", "overflow-y", new() {
            ["auto"] = "auto", ["hidden"] = "hidden", ["visible"] = "visible", ["scroll"] = "scroll"
        });

        // The ratio keywords have to be pairs rather than numbers: the layout reads `16 / 9` with a
        // parser of its own, and `aspect-16/9` cannot be written as a class because the parser reads
        // a top-level slash as an opacity long before the family sees it.
        Register(new Family("aspect", ValueKind.Number, ["aspect-ratio"], new Dictionary<string, string>(StringComparer.Ordinal) {
            ["square"] = "aspect-ratio:1 / 1",
            ["video"] = "aspect-ratio:16 / 9",
            ["auto"] = "aspect-ratio:auto"
        }));

        // ── The twenty-nine roots that are deliberately NOT here ────────────────────────────
        //
        // ⚠ <b>`docs/plan/43`'s `shadowed_by` column is 35 rows and six of them are registered
        // above. The other twenty-nine are refusals with a measurement behind each, and this comment
        // exists because the obvious reading of that column — "thirty-five `Register` calls" — is
        // the one that produces thirty-five inert classes.</b> Each refusal is written out in the
        // `note` cell of its own row; the shapes are worth having in one place, because they are the
        // four ways a registration can be wrong and only the first is visible to the gate:
        //
        //   <b>1. The property is inert, and registering it turns the gate red.</b> The honest kind.
        //   `border-spacing-*`, `border-spacing-x/y-*` (no table layout exists at all),
        //   `font-stretch-*` (interned by `InheritedProperties` and read by nobody — the exact case
        //   `UtilityConsumptionGateTests.An_interned_property_no_consumer_acts_on_reads_as_inert`
        //   pins), `text-shadow-*`, `background-clip`/`-origin`/`-blend-mode`/`-repeat` for the four
        //   `bg` keyword sets, and `content` for `content-none` — which has nothing to apply to
        //   either, since F6 refused pseudo-elements rather than building them.
        //
        //   <b>2. The property is inert and already allow-listed, so the shadowed root inherits a
        //   debt rather than adding one.</b> `scale-x/y/z-*` and `rotate-x/y/z-*`. `scale` and
        //   `rotate` are `#23` in `InertProperties.txt`, refused at the draw list because a rotated
        //   box is not a rectangle and a scaled subtree needs re-shaping; a per-axis family over a
        //   refused property is inert by construction. The three `-z` and two 3D rotations are
        //   further out still: `transform` and `perspective` are not interned anywhere and measure
        //   inert too.
        //
        //   ⚠ <b>3. The property is READ, and the value is refused — so the gate stays green over a
        //   class that paints nothing.</b> The dangerous kind, and the one this table has to catch
        //   by hand because no per-property measurement can. `inset-shadow-*` and `inset-ring-*`
        //   emit `box-shadow`, which is read — but `DrawListBuilder.EmitShadow` refuses the `inset`
        //   keyword outright and says why, and `box-shadow: inset 0 2px 4px #000` moves no channel
        //   in any scene while `box-shadow: 0 2px 4px #000` moves paint. `ring-offset-*` is worse
        //   than inert: an offset ring is a two-shadow *list*, `EmitShadow` refuses lists for the
        //   same stated reason, and a `ring-offset-2` beside a `ring-2` would stop the ring painting
        //   at all. `stroke-none` is the same shape one file over — `stroke: <colour>` moves paint
        //   and `stroke: none` moves nothing, because `Icon.Resolve` reads the slot with `ColorOf`
        //   and falls back to the foreground when it is not a colour.
        //
        //   <b>4. The class is v4 compatibility surface, and `docs/plan/43` § D5 already says not to
        //   implement it.</b> `flex-shrink-*`, `flex-grow-*` and `max-w-screen-*` live in v4's
        //   `compat/legacy-utilities.ts`: registered, undocumented, superseded by `shrink-*`,
        //   `grow-*` and the sizing scale, all of which are here and read. Their properties are read
        //   too, so these three would have registered cleanly and passed everything — which is why
        //   the reason they are absent is a policy and not a measurement.
        //
        //   <b>And the six logical radii are their own case.</b> `rounded-s/e/ss/se/ee/es-*` set
        //   `border-start-start-radius` and its three siblings, none of which anything interns, so
        //   they belong to shape 1 — but the physical fallback that rescued `inset-bs-*` is not
        //   available to them, and that is the part worth writing down. A radius corner is named on
        //   the *inline* axis, which this engine really does mirror: `rounded-ss` is the top-left
        //   corner under `direction: ltr` and the top-right under `rtl`. `border-top-left-radius`
        //   would therefore be right half the time, which is worse than absent — the block-axis
        //   mapping above is safe precisely because no configuration of this engine flips it.
        //
        // Longest first, so `min-w` wins over nothing and `flex-wrap` over `flex`.
        Names.Sort(static (left, right) => right.Length.CompareTo(left.Length));
    }

    /// <summary>Splits a utility into the longest registered family name and the rest.</summary>
    /// <param name="whole">The utility text, without variants or suffixes.</param>
    /// <returns>The family name and its value.</returns>
    /// <remarks>
    ///     Longest prefix rather than first hyphen, because <c>p</c> and <c>pointer-events</c> both
    ///     exist and a first-hyphen split would read the second as the family <c>pointer</c>. The
    ///     hyphen after the name has to be there, or <c>p</c> would claim <c>pointer-events</c>.
    /// </remarks>
    public static (string Name, string Value) SplitName(string whole) {
        ArgumentNullException.ThrowIfNull(whole);

        foreach (var name in Names) {
            if (whole.Equals(name, StringComparison.Ordinal)) {
                return (name, string.Empty);
            }

            if (whole.Length > name.Length
                && whole.StartsWith(name, StringComparison.Ordinal)
                && whole[name.Length] == '-') {
                return (name, whole[(name.Length + 1)..]);
            }
        }

        return (whole, string.Empty);
    }

    /// <summary>Whether a name is one the registry holds.</summary>
    /// <param name="name">A name, as <see cref="SplitName" /> returns it.</param>
    /// <returns>Whether a family is registered under it.</returns>
    /// <remarks>
    ///     ⚠ <b>The question <see cref="TryResolve" />'s <c>false</c> cannot answer, and the whole of
    ///     why it is public.</b> <c>TryResolve</c> returns <c>false</c> for two situations that read
    ///     identically to whoever wrote the class and are opposite in what to do about them:
    ///     <c>flexx-4</c> is a typo and <c>bg-clip-text</c> is a registered family being asked for a
    ///     value it does not have. Reporting them through one channel makes the second look like the
    ///     first, and the first is what the scanner produces by the hundred — so the second drowns.
    ///     <see cref="UtilityGenerator.Unresolved" /> is the channel that needs this to exist.
    ///     <para>
    ///         <b>Not a way to ask whether a class works.</b> A registered name says a family will be
    ///         consulted, not that it will answer: <c>bg</c> is registered and <c>bg-clip-text</c>
    ///         still emits nothing. Only <see cref="TryResolve" /> knows that.
    ///     </para>
    /// </remarks>
    public static bool IsRegistered(string name) {
        ArgumentNullException.ThrowIfNull(name);
        return Registry.ContainsKey(name);
    }

    /// <summary>What a family's rule is about, when it is not the element carrying the class.</summary>
    /// <param name="name">The family name, as <see cref="SplitName" /> returns it.</param>
    /// <returns>
    ///     The selector text to append — <c>" &gt; :not(:last-child)"</c> — or <c>null</c> for the
    ///     overwhelming majority of families, whose rule is about the element itself.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>Public because two callers outside this file have to know, and both of them are
    ///     wrong without it.</b> <see cref="UtilityGenerator" /> has to append it to the selector, or
    ///     <c>space-x-4</c> emits a margin on the container and silently does the opposite of what it
    ///     says. <see cref="ApplyExpander" /> has to refuse it, or <c>@apply space-x-4</c> quietly
    ///     drops the same declarations into whichever block it was written in. A family registered
    ///     here and unknown to either of those is the "registered in one table and not another"
    ///     failure, so this is the one table both of them read.
    /// </remarks>
    public static string? ScopeOf(string name) {
        ArgumentNullException.ThrowIfNull(name);
        return Registry.TryGetValue(name, out var family) ? family.Scope : null;
    }

    /// <summary>Every class name that reaches a distinct part of what the families can emit.</summary>
    /// <param name="tokens">The theme, which decides what a token-valued family can be given.</param>
    /// <returns>The class names, ordered, each one of which resolves against <paramref name="tokens" />.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The point of this is that it is <i>computed</i>, and every hand-written inventory of
    ///         this table has rotted.</b> <c>docs/plan/43</c> § Part 0 is a survey somebody did by hand
    ///         against 328 Tailwind roots, and its own opening caveat is that the script that produced
    ///         it is not in the tree. Anything that enumerates the family surface by listing class names
    ///         is a second copy of the registry that drifts from the first the next time a family is
    ///         added — which is exactly how "43 registrations" came to be quoted for a table holding 98.
    ///     </para>
    ///     <para>
    ///         <b>A family is covered by more than one class, because a family emits more than one
    ///         thing.</b> <c>flex</c> alone is <c>display</c>, <c>flex-col</c> is <c>flex-direction</c>,
    ///         <c>flex-wrap</c> is <c>flex-wrap</c> and <c>flex-1</c> is the <c>flex</c> shorthand — one
    ///         prefix, four properties, so one example class would measure a quarter of it. Every key of
    ///         a family's keyword table is emitted, plus a value of the family's own kind, plus a colour
    ///         for the border families, whose colour longhands are a different set from their widths.
    ///     </para>
    ///     <para>
    ///         Only the names that <see cref="TryResolve" /> actually answers come back, so a theme with
    ///         no <c>radius</c> scale yields no <c>rounded-*</c> — which is a true statement about that
    ///         theme rather than a hole in this method.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<string> Surface(ThemeTokens tokens) {
        ArgumentNullException.ThrowIfNull(tokens);

        var probes = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var declarations = new List<UtilityDeclaration>();

        // Ordered by name rather than by the longest-first order `SplitName` needs, so that a failure
        // message reads alphabetically and two runs produce the same list.
        foreach (var name in Names.Order(StringComparer.Ordinal)) {
            var family = Registry[name];

            // The bare form — `border`, `rounded`, `grow`, and every `Static` family, whose value
            // lives under the keyword table's empty key.
            Consider(name);

            if (family.Keywords is not null) {
                foreach (var key in family.Keywords.Keys.Order(StringComparer.Ordinal)) {
                    Consider(key.Length == 0 ? name : $"{name}-{key}");
                }
            }

            foreach (var value in ValuesFor(family, tokens)) {
                Consider($"{name}-{value}");
            }
        }

        return probes;

        void Consider(string candidate) {
            if (!seen.Add(candidate)
                || !UtilityParser.TryParse(candidate, out var parsed)
                || !TryResolve(parsed, tokens, declarations)) {
                return;
            }

            probes.Add(candidate);
        }
    }

    /// <summary>The values worth giving a family of each kind, drawn from the theme where there is one.</summary>
    /// <remarks>
    ///     ⚠ <b>The first token of a scale rather than all of them.</b> Two radii emit the same property
    ///     with different numbers, so the second says nothing new about <i>which</i> properties a family
    ///     can set — where a second keyword often does, which is why the keyword table is enumerated
    ///     whole and the token scales are not. <see cref="ValueKind.FontSize" /> and
    ///     <see cref="ValueKind.BorderEdge" /> take two values apiece because those two kinds genuinely
    ///     change property depending on how the value reads.
    /// </remarks>
    static IEnumerable<string> ValuesFor(Family family, ThemeTokens tokens) {
        switch (family.Kind) {
            case ValueKind.Spacing:
            case ValueKind.Size:
            case ValueKind.Number:
            case ValueKind.CountTemplate:
                yield return "2";
                break;

            case ValueKind.Duration:
                yield return "300";
                break;

            case ValueKind.Fraction:
                yield return "50";
                break;

            case ValueKind.Color:
                foreach (var colour in First(tokens.Colors.Keys)) {
                    yield return colour;
                }

                break;

            case ValueKind.Radius:
                foreach (var radius in First(tokens.Radius.Keys)) {
                    yield return radius;
                }

                break;

            case ValueKind.FontWeight:
                foreach (var weight in First(tokens.FontWeight.Keys)) {
                    yield return weight;
                }

                break;

            case ValueKind.Shadow:
                foreach (var shadow in First(tokens.Shadow.Keys)) {
                    yield return shadow;
                }

                break;

            // Both readings, for the same reason `text-` and `border-` take two: a percentage and a
            // colour are two different fragments, and probing one would leave the other unmeasured.
            case ValueKind.GradientStop:
                yield return "40%";

                foreach (var colour in First(tokens.Colors.Keys)) {
                    yield return colour;
                }

                break;

            case ValueKind.FontSize:
                // Both readings of `text-`, because they are two different properties: a size token
                // sets `font-size` and `line-height`, and anything else falls through to `color`.
                foreach (var size in First(tokens.FontSize.Keys)) {
                    yield return size;
                }

                foreach (var colour in First(tokens.Colors.Keys)) {
                    yield return colour;
                }

                break;

            case ValueKind.BorderEdge:
                // A width and a colour, which land in two different sets of longhands — the case
                // `docs/plan/43` F1 is about, where one set was read and the other was not.
                yield return "2";

                foreach (var colour in First(tokens.Colors.Keys)) {
                    yield return colour;
                }

                break;

            case ValueKind.Static:
            case ValueKind.Keyword:
            default:
                break;
        }
    }

    static IEnumerable<string> First(IEnumerable<string> keys) {
        var ordered = keys.Order(StringComparer.Ordinal).FirstOrDefault();
        return ordered is null ? [] : [ordered];
    }

    /// <summary>Turns a parsed candidate into the declarations it stands for.</summary>
    /// <param name="candidate">The candidate.</param>
    /// <param name="tokens">The theme.</param>
    /// <param name="declarations">Receives the declarations.</param>
    /// <returns>Whether it is a utility this system knows.</returns>
    public static bool TryResolve(UtilityCandidate candidate, ThemeTokens tokens, List<UtilityDeclaration> declarations) {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(declarations);

        declarations.Clear();

        // ⚠ <b>An arbitrary property is resolved before the registry is consulted, because it has no
        // entry there and is not supposed to.</b> `[mask-type:luminance]` is the escape hatch for a
        // property this table has never heard of, so there is nothing to validate the declaration
        // against and nothing should be: it emits `mask-type: luminance` and the cascade refuses it
        // downstream if nothing reads it. That is the whole point of the hatch and it is also why the
        // two halves are shape-tested on the way in — see `UtilityParser.IsPropertyName` for the
        // name and `IsPlausibleValue` below for the value.
        if (candidate.Property is { } property) {
            return TryArbitraryProperty(candidate, property, declarations);
        }

        if (!Registry.TryGetValue(candidate.Name, out var family)) {
            return false;
        }

        // Negation is applied to the result rather than threaded through every branch below, because
        // `-mt-4` sets exactly what `mt-4` sets and the only difference is the sign of the number.
        if (!Resolve(family, candidate, tokens, declarations)
            || (candidate.Negative && !TryNegate(candidate, declarations))) {
            return false;
        }

        // ⚠ Last, and after negation, and only once the value has resolved. After negation because a
        // stop list is not a number and flipping its sign is meaningless; only once the value has
        // resolved because a family that appended its constants first would leave `via-nonsense` —
        // a typo — emitting a three-stop list for a colour nobody supplied, which is a rule that
        // exists and silently changes the gradient.
        if (family.Alongside is not null) {
            declarations.AddRange(family.Alongside);
        }

        return true;
    }

    /// <summary>Emits the one declaration an arbitrary property names, if both halves are sound.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is exempt from the consumption gate, it needs no code to be exempt, and the
    ///         absence of that code is the point.</b> <c>UtilityConsumptionGateTests</c> asks that no
    ///         utility <i>family</i> emit a property nothing acts on, and it asks it of
    ///         <see cref="Surface" /> — an enumeration of the registry. An arbitrary property is never
    ///         registered, so it is not on the surface, contributes nothing to the gate's `Emitted`
    ///         set, and can appear in neither `Inert` nor an allow-list. No branch anywhere says "skip
    ///         the gate for this", and a branch that did would be the hole: the gate is strong because
    ///         its domain is defined positively, by what the registry holds, rather than negatively by
    ///         a list of things that get out of it.
    ///     </para>
    ///     <para>
    ///         <b>Nor can the hatch launder a family's debt, which is the test of whether an exemption
    ///         is really a hole.</b> Registering a <see cref="UtilityComposition" /> fragment
    ///         <i>was</i> a way to move a property out of `Inert`, which is why that mechanism needed
    ///         an explicit guard holding the assembler accountable. There is no matching move here.
    ///         Writing <c>[mask-type:luminance]</c> in a <c>.vxml</c> changes
    ///         <see cref="Surface" /> by nothing at all — it never reads a source file — and the only
    ///         way to take a family off the surface is to delete its registration, which stops every
    ///         use of it generating anywhere in the tree. That is a loud change, not a silent one.
    ///     </para>
    ///     <para>
    ///         <b>What the author is owed instead is the truth, and the truth is that nothing checked.</b>
    ///         A family is a promise — the registry says <c>p-4</c> will do something, so a <c>p-4</c>
    ///         that does nothing is a lie the gate exists to catch. An arbitrary property promises
    ///         nothing: the author typed the property name themselves, no table told them it would
    ///         work, and "the cascade drops it if no consumer interns it" is the documented outcome
    ///         rather than a defect. <c>Vixen.Ui.Styling.Utilities.Tests.ArbitraryPropertyTests</c>
    ///         pins the structural claim, so a future <see cref="Surface" /> that started reading
    ///         generated sheets would fail there rather than quietly widening the gate.
    ///     </para>
    /// </remarks>
    static bool TryArbitraryProperty(
        UtilityCandidate candidate,
        string property,
        List<UtilityDeclaration> declarations
    ) {
        if (candidate.Arbitrary is not { } value || !IsPlausibleValue(value)) {
            return false;
        }

        // ⚠ Both of these would be silently dropped rather than honoured, and a dropped half of a
        // class is the failure this file refuses everywhere else. `-[color:red]` has no sign to flip
        // — negation is arithmetic on a resolved number and there is no number here — and
        // `[color:red]/50` has nowhere to put the opacity, because that is a family's reading of a
        // slash and this candidate has no family. Refusing means no rule, and the caller reports the
        // class unrecognised.
        if (candidate.Negative || candidate.SlashSuffix is not null) {
            return false;
        }

        declarations.Add(new UtilityDeclaration(property, value));

        return true;
    }

    static bool Resolve(Family family, UtilityCandidate candidate, ThemeTokens tokens, List<UtilityDeclaration> declarations) {
        // An arbitrary value goes straight through, once it is CSS at all. That is the point of it:
        // `w-[37px]` exists precisely for the case the token scale does not cover, and second-guessing
        // it would make the escape hatch useless.
        if (candidate.Arbitrary is { } arbitrary) {
            if (!IsPlausibleValue(arbitrary)) {
                return false;
            }

            // ⚠ A widths-only border family — `divide-x`, `divide-y` — has nowhere to put an
            // arbitrary colour, so `divide-x-[red]` is refused rather than emitted as a width.
            if (family.Kind == ValueKind.BorderEdge && LooksLikeColor(arbitrary)) {
                return family.ColorProperties is not null
                    && EmitInto(family.ColorProperties, arbitrary, declarations);
            }

            return Emit(family, arbitrary, declarations);
        }

        // Keywords first, because `text-center` has to beat any colour or size named `center`.
        if (family.Keywords is not null && family.Keywords.TryGetValue(candidate.Value, out var keyword)) {
            return keyword.Contains(':', StringComparison.Ordinal)
                ? EmitPair(keyword, declarations)
                : Emit(family, keyword, declarations);
        }

        // `border` and `rounded` on their own mean a default width and a default radius — CSS's own
        // ambiguity rather than one invented here, and handled apart from the table so that the
        // table stays one entry per family.
        if (candidate.Value.Length == 0 && TryBareForm(candidate, declarations)) {
            return true;
        }

        return family.Kind switch {
            // A family with no value reaches its declaration through the keyword table's empty key,
            // which the branch above has already tried. Getting here means a value was given to a
            // utility that does not take one.
            ValueKind.Static => false,
            ValueKind.Spacing => TrySpacing(candidate.Value, tokens, out var spacing) && Emit(family, spacing, declarations),
            ValueKind.Size => TrySize(candidate, tokens, out var size) && Emit(family, size, declarations),
            ValueKind.Number => TryNumber(candidate.Value, out var number) && Emit(family, number, declarations),
            ValueKind.CountTemplate => TryCount(candidate.Value, out var count)
                && Emit(family, string.Format(CultureInfo.InvariantCulture, family.Template!, count), declarations),
            ValueKind.Duration => TryNumber(candidate.Value, out var ms) && Emit(family, ms + "ms", declarations),
            ValueKind.Fraction => TryFraction(candidate.Value, out var fraction) && Emit(family, fraction, declarations),
            ValueKind.Radius => TryRadius(candidate.Value, tokens, out var radius) && Emit(family, radius, declarations),
            ValueKind.FontWeight => TryFontWeight(candidate.Value, tokens, out var weight) && Emit(family, weight, declarations),
            ValueKind.FontSize => TryFontSizeOrColor(candidate, tokens, declarations),
            ValueKind.Color => TryColor(candidate, tokens, out var colour) && Emit(family, colour, declarations),
            ValueKind.BorderEdge => TryBorderEdge(family, candidate, tokens, declarations),
            ValueKind.Shadow => TryShadow(family, candidate, tokens, declarations),
            ValueKind.GradientStop => TryGradientStop(family, candidate, tokens, declarations),
            _ => false
        };
    }

    /// <summary>A gradient stop: a percentage is where it sits, anything else is what colour it is.</summary>
    /// <remarks>
    ///     ⚠ <b>Percentage-first, and unlike <c>text-</c> this order shadows nothing.</b> A colour
    ///     token cannot be named <c>40%</c>, because <c>%</c> is not a character a theme key is
    ///     written with — so the two readings of <c>from-</c> are separated by the value's shape and
    ///     no palette can collide with either.
    /// </remarks>
    static bool TryGradientStop(Family family, UtilityCandidate candidate, ThemeTokens tokens, List<UtilityDeclaration> declarations) {
        var value = candidate.Arbitrary ?? candidate.Value;

        if (value.EndsWith('%') && float.TryParse(value[..^1], CultureInfo.InvariantCulture, out _)) {
            foreach (var position in family.Positions!) {
                declarations.Add(new UtilityDeclaration(position, value));
            }

            return true;
        }

        return candidate.Arbitrary is not null
            ? EmitInto(family.Properties, candidate.Arbitrary, declarations)
            : TryColor(candidate, tokens, out var colour) && Emit(family, colour, declarations);
    }

    /// <summary>The values that are sizes rather than lengths, and so cannot be negated.</summary>
    /// <remarks>
    ///     ⚠ Checked against the value <i>as written</i> rather than against what it resolved to,
    ///     because <c>full</c> and <c>screen</c> both come out as <c>100%</c> — which begins with a
    ///     digit and would sail through the shape test below. <c>-w-full</c> silently meaning
    ///     "minus one hundred per cent wide" is exactly the class of bug that test is there to stop,
    ///     and it took the negation being written the shape-only way once to notice.
    ///     <c>px</c> is deliberately absent: <c>-mt-px</c> is a real and useful one-pixel pull.
    /// </remarks>
    static readonly HashSet<string> NotNegatable = new(StringComparer.Ordinal) {
        "auto", "full", "screen", "min", "max", "fit"
    };

    /// <summary>Flips the sign of everything a utility resolved to.</summary>
    /// <remarks>
    ///     Only a number can be negated. <c>-w-full</c> is not a hundred per cent to the left and
    ///     <c>-bg-accent</c> is nothing at all, so both are refused rather than emitted with a stray
    ///     minus in front of them — a rule that silently means nothing is worse than no rule.
    /// </remarks>
    static bool TryNegate(UtilityCandidate candidate, List<UtilityDeclaration> declarations) {
        if (declarations.Count == 0 || NotNegatable.Contains(candidate.Value)) {
            return false;
        }

        for (var i = 0; i < declarations.Count; i++) {
            var value = declarations[i].Value;

            if (value.Length == 0 || !(char.IsAsciiDigit(value[0]) || value[0] == '.')) {
                return false;
            }

            declarations[i] = declarations[i] with { Value = "-" + value };
        }

        return true;
    }

    /// <summary>A border edge, which is a width or a colour depending on how the value reads.</summary>
    /// <remarks>
    ///     <para>
    ///         The same ambiguity <c>text-</c> has, and unlike <c>text-</c> this one costs nothing: no
    ///         colour is plausibly named <c>2</c>, so the number-first order shadows nothing reachable.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A null <see cref="Family.ColorProperties" /> means the family is widths only</b>,
    ///         which <c>divide-x</c> and <c>divide-y</c> are: Tailwind writes the divider's colour
    ///         <c>divide-accent</c>, never <c>divide-x-accent</c>. Refusing the spelling reports it as
    ///         unknown, which is what it is; the alternative reading — dereference and emit — is an
    ///         invented class and, before this line, a null reference.
    ///     </para>
    /// </remarks>
    static bool TryBorderEdge(Family family, UtilityCandidate candidate, ThemeTokens tokens, List<UtilityDeclaration> declarations) {
        // `border` and `border-t` on their own are a one-pixel edge — CSS's own default, and the
        // reason `border-width` has one at all.
        if (candidate.Value.Length == 0) {
            return EmitInto(family.Properties, "1px", declarations);
        }

        if (TryNumber(candidate.Value, out var width)) {
            return EmitInto(family.Properties, width + "px", declarations);
        }

        return family.ColorProperties is not null
            && TryColor(candidate, tokens, out var colour)
            && EmitInto(family.ColorProperties, colour, declarations);
    }

    /// <summary>A named shadow, or the theme's default one for a bare <c>shadow</c>.</summary>
    /// <remarks>
    ///     The same <c>DEFAULT</c> convention the colour tokens use, so <c>shadow</c> and
    ///     <c>bg-accent</c> answer their unqualified forms the same way rather than each having its
    ///     own rule.
    /// </remarks>
    static bool TryShadow(Family family, UtilityCandidate candidate, ThemeTokens tokens, List<UtilityDeclaration> declarations) {
        var key = candidate.Value.Length == 0 ? ThemeTokens.DefaultKey : candidate.Value;
        return tokens.Shadow.TryGetValue(key, out var shadow) && Emit(family, shadow, declarations);
    }

    /// <summary>Whether an arbitrary value is CSS at all, and so worth emitting a declaration for.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An unused rule is free by the scanner's own argument and a malformed one is not.</b>
    ///         The scanner is over-inclusive on purpose, so <c>text[1..]</c> — a C# range expression —
    ///         arrives here as the utility <c>text</c> with the arbitrary value <c>1..</c>, and
    ///         <c>font-size: 1..</c> was emitted, parsed by ExCSS, and dropped without a word. A rule
    ///         nothing matches costs nothing; a declaration the parser throws away is noise in every
    ///         diagnostic anyone ever runs over the generated sheet, and is indistinguishable from the
    ///         real parse failure the next person is looking for. So a candidate whose value is not CSS
    ///         is refused outright, and refusing means <i>no rule</i> — the caller reports it
    ///         unrecognised, the same as a misspelt utility, rather than emitting an empty block.
    ///     </para>
    ///     <para>
    ///         <b>"Plausible" is a token-shape test and must never become a value parser.</b> The
    ///         question asked is whether the text could be a CSS component-value sequence at all, not
    ///         whether the property would accept it: <c>font-size: red</c> is refused by CSS and
    ///         accepted here, because deciding otherwise means a table of every property's grammar and
    ///         a new way for the escape hatch to be wrong. Three things are checked, and they are the
    ///         three that no CSS value can violate. Parentheses balance. A string and a <c>url()</c>
    ///         are closed, and their contents are a single token that nothing inside this method reads.
    ///         And every <c>.</c> outside those belongs to a number — CSS has no other use for one —
    ///         which is what <c>1..</c> fails and what <c>[3px]</c>, <c>[50%]</c>, <c>[1fr]</c>,
    ///         <c>[#f00]</c>, <c>[var(--x)]</c>, <c>[calc(100%-2rem)]</c> and <c>[0.5]</c> all pass.
    ///     </para>
    /// </remarks>
    static bool IsPlausibleValue(string value) {
        var text = value.AsSpan();

        if (text.IsWhiteSpace()) {
            return false;
        }

        var depth = 0;

        for (var i = 0; i < text.Length; i++) {
            var c = text[i];

            // A string is one token and its insides are content rather than syntax. Unterminated, it
            // is not a token at all.
            if (c is '\'' or '"') {
                var close = text[(i + 1)..].IndexOf(c);
                if (close < 0) {
                    return false;
                }

                i += close + 1;
                continue;
            }

            if (c == '(') {
                // ⚠ `url(…)` is a token whose body CSS Syntax 3 § 4.3.6 consumes without tokenising,
                // so `url(a/b2.png)` is a url and not a malformed number followed by a word.
                if (i >= 3 && text[(i - 3)..i].Equals("url", StringComparison.OrdinalIgnoreCase)) {
                    var close = text[(i + 1)..].IndexOf(')');
                    if (close < 0) {
                        return false;
                    }

                    i += close + 1;
                    continue;
                }

                depth++;
                continue;
            }

            if (c == ')') {
                if (--depth < 0) {
                    return false;
                }

                continue;
            }

            if (StartsNumber(text, i)) {
                var after = EndOfNumber(text, i);

                // The whole of the defect: a number followed by a second decimal point. `1..` is what
                // `text[1..]` leaves behind and is not a number, a dimension or anything else.
                if (after < text.Length && text[after] == '.') {
                    return false;
                }

                i = after - 1;
                continue;
            }

            if (c == '.') {
                return false;
            }
        }

        return depth == 0;
    }

    /// <summary>Whether a number begins here, as CSS Syntax 3 § 4.3.10 defines it.</summary>
    static bool StartsNumber(ReadOnlySpan<char> text, int i) {
        var c = text[i];

        if (c is '+' or '-') {
            return i + 1 < text.Length
                && (char.IsAsciiDigit(text[i + 1])
                    || (text[i + 1] == '.' && i + 2 < text.Length && char.IsAsciiDigit(text[i + 2])));
        }

        if (c == '.') {
            return i + 1 < text.Length && char.IsAsciiDigit(text[i + 1]);
        }

        return char.IsAsciiDigit(c);
    }

    /// <summary>Where the number beginning here ends, as CSS Syntax 3 § 4.3.12 consumes it.</summary>
    /// <remarks>
    ///     The exponent is only taken when digits really follow it, which is what keeps the <c>e</c>
    ///     of <c>1em</c> out of the number and the <c>5</c> of <c>1e5</c> in it.
    /// </remarks>
    static int EndOfNumber(ReadOnlySpan<char> text, int i) {
        if (text[i] is '+' or '-') {
            i++;
        }

        while (i < text.Length && char.IsAsciiDigit(text[i])) {
            i++;
        }

        if (i < text.Length && text[i] == '.' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1])) {
            i++;

            while (i < text.Length && char.IsAsciiDigit(text[i])) {
                i++;
            }
        }

        if (i < text.Length && text[i] is 'e' or 'E') {
            var exponent = i + 1;

            if (exponent < text.Length && text[exponent] is '+' or '-') {
                exponent++;
            }

            if (exponent < text.Length && char.IsAsciiDigit(text[exponent])) {
                i = exponent;

                while (i < text.Length && char.IsAsciiDigit(text[i])) {
                    i++;
                }
            }
        }

        return i;
    }

    /// <summary>Whether an arbitrary value on a border edge is a colour rather than a width.</summary>
    /// <remarks>
    ///     <c>border-[3px]</c> and <c>border-[#f00]</c> are one utility with two meanings and nothing
    ///     in the class name says which, so it is read from the value's shape. A hex triple or a
    ///     colour function is a colour; everything else is a width, which includes
    ///     <c>border-[var(--x)]</c> — there is genuinely no way to tell, and a width is the commoner
    ///     one. The escape hatch for the other reading is <c>border-color-[…]</c> written by hand.
    /// </remarks>
    static bool LooksLikeColor(string value) =>
        value.StartsWith('#')
        || value.StartsWith("rgb", StringComparison.Ordinal)
        || value.StartsWith("hsl", StringComparison.Ordinal);

    /// <summary>The bare form of a family that also takes a value.</summary>
    /// <remarks>
    ///     <c>grow</c> on its own means one and <c>rounded</c> on its own means a default radius,
    ///     which is CSS's own ambiguity rather than one this system invented. Handled here so the
    ///     table stays one entry per family. The border families do the same thing for themselves,
    ///     because there are nine of them and each has its own longhands to write it into.
    /// </remarks>
    static bool TryBareForm(UtilityCandidate candidate, List<UtilityDeclaration> declarations) {
        if (candidate.Value.Length != 0) {
            return false;
        }

        switch (candidate.Name) {
            case "grow":
                declarations.Add(new UtilityDeclaration("flex-grow", "1"));
                return true;

            case "shrink":
                declarations.Add(new UtilityDeclaration("flex-shrink", "1"));
                return true;

            case "rounded":
                declarations.Add(new UtilityDeclaration("border-radius", "4px"));
                return true;

            default:
                return false;
        }
    }

    static bool TryFontSizeOrColor(UtilityCandidate candidate, ThemeTokens tokens, List<UtilityDeclaration> declarations) {
        // The documented resolution order for `text-`: keyword (already tried), then font size,
        // then colour. A colour named `lg` would be unreachable, which is the price of one prefix
        // meaning three things and is worth paying — `text-lg` and `text-accent` both read right.
        if (tokens.FontSize.TryGetValue(candidate.Value, out var size)) {
            declarations.Add(new UtilityDeclaration("font-size", Px(size.Size)));
            declarations.Add(new UtilityDeclaration("line-height", Px(size.LineHeight)));
            return true;
        }

        if (!TryColor(candidate, tokens, out var colour)) {
            return false;
        }

        declarations.Add(new UtilityDeclaration("color", colour));
        return true;
    }

    /// <summary>A theme colour, with the <c>/50</c> modifier folded in if there was one.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This used to rewrite the colour as <c>rgba()</c>, and could only do so when the
    ///         token was a hex triple — so every token that was not one had its opacity silently
    ///         dropped.</b> Which sounds like an edge case and is the ordinary case the moment tokens
    ///         become references: <c>--accent: var(--blue-500)</c>, or an <c>@theme</c> block written
    ///         in <c>oklch()</c> as <c>docs/plan/43</c> § D4 calls for, are both "not a hex triple".
    ///         The utility resolved, emitted valid CSS, and painted at full opacity.
    ///     </para>
    ///     <para>
    ///         <b><c>color-mix()</c> removes the condition rather than widening it.</b> The colour
    ///         goes in as text and is never taken apart here, so this works for a hex code, an
    ///         <c>oklch()</c>, a <c>var()</c> — whatever the token holds and whatever it will hold
    ///         later. It is what Tailwind v4 emits for the same modifier, and for a hex colour it is
    ///         arithmetically the same answer the <c>rgba()</c> rewrite gave: mixing against
    ///         <c>transparent</c> with premultiplied alpha leaves the colour where it was and moves
    ///         only the alpha.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>in oklab</c>, not <c>in oklch</c>.</b> A hue is not premultiplied, and
    ///         <c>transparent</c> is black at zero alpha whose hue is 0° — so the polar space would
    ///         drag every colour's hue towards red on its way to being translucent. See
    ///         <c>Vixen.Ui.Styling.ColorFunctions.Mix</c>, which has the arithmetic.
    ///     </para>
    /// </remarks>
    static bool TryColor(UtilityCandidate candidate, ThemeTokens tokens, out string value) {
        value = string.Empty;

        if (candidate.Value.Length == 0) {
            return false;
        }

        if (!tokens.TryGetColor(candidate.Value, out var colour)) {
            return false;
        }

        if (candidate.Opacity is not { } opacity) {
            value = colour;
            return true;
        }

        value = string.Create(
            CultureInfo.InvariantCulture,
            $"color-mix(in oklab, {colour} {(opacity * 100f).ToString("0.###", CultureInfo.InvariantCulture)}%, transparent)"
        );

        return true;
    }

    static bool TrySpacing(string value, ThemeTokens tokens, out string result) {
        result = string.Empty;

        if (value.Length == 0) {
            return false;
        }

        if (value.Equals("px", StringComparison.Ordinal)) {
            result = "1px";
            return true;
        }

        if (value.Equals("auto", StringComparison.Ordinal)) {
            result = "auto";
            return true;
        }

        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var steps)) {
            return false;
        }

        result = Px(steps * tokens.SpacingBase);
        return true;
    }

    static bool TrySize(UtilityCandidate candidate, ThemeTokens tokens, out string result) {
        result = string.Empty;

        switch (candidate.Value) {
            case "full":
                result = "100%";
                return true;
            case "screen":
                result = "100%";
                return true;
            case "auto":
                result = "auto";
                return true;
            case "min":
                result = "min-content";
                return true;
            case "max":
                result = "max-content";
                return true;
            case "fit":
                result = "fit-content";
                return true;
            default:
                break;
        }

        // `w-1/2` — the slash is a fraction here and not an opacity, which is why the suffix is kept
        // as written as well as read as one.
        if (candidate.SlashSuffix is { } denominator
            && float.TryParse(candidate.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && float.TryParse(denominator, NumberStyles.Float, CultureInfo.InvariantCulture, out var divisor)
            && divisor != 0f) {
            result = (numerator / divisor * 100f).ToString("0.####", CultureInfo.InvariantCulture) + "%";
            return true;
        }

        return TrySpacing(candidate.Value, tokens, out result);
    }

    /// <summary>Resolves a <c>rounded-*</c> against the theme.</summary>
    /// <remarks>
    ///     ⚠ <b>Emitted as written, because a radius token is text now.</b> It used to be a
    ///     <c>float</c> this turned back into a pixel string, which meant the only radius a theme
    ///     could hold was a number — and the editor, whose three radii are custom properties on the
    ///     root, could therefore declare none of them. <see cref="ThemeTokens.Radius" /> records what
    ///     that cost; the change here is the whole of the fix.
    /// </remarks>
    static bool TryRadius(string value, ThemeTokens tokens, out string result) {
        if (tokens.Radius.TryGetValue(value, out var radius)) {
            result = radius;
            return true;
        }

        result = string.Empty;
        return false;
    }

    static bool TryFontWeight(string value, ThemeTokens tokens, out string result) {
        if (tokens.FontWeight.TryGetValue(value, out var weight)) {
            result = weight.ToString("0.###", CultureInfo.InvariantCulture);
            return true;
        }

        result = string.Empty;
        return false;
    }

    static bool TryFraction(string value, out string result) {
        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)) {
            result = (percent / 100f).ToString("0.####", CultureInfo.InvariantCulture);
            return true;
        }

        result = string.Empty;
        return false;
    }

    static bool TryNumber(string value, out string result) {
        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) {
            result = number.ToString("0.####", CultureInfo.InvariantCulture);
            return true;
        }

        result = string.Empty;
        return false;
    }

    /// <summary>Parses the whole, positive count a track template can be repeated.</summary>
    /// <remarks>
    ///     ⚠ Stricter than <see cref="TryNumber" /> on purpose. <c>repeat(2.5, …)</c> and
    ///     <c>repeat(0, …)</c> are not track lists, and a family that emitted either would push the
    ///     failure out of the utility compiler — where it is a name nobody registered — and into the
    ///     stylesheet, where it becomes a refused declaration on every element that used the class.
    /// </remarks>
    static bool TryCount(string value, out int count) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out count) && count > 0;

    static bool Emit(Family family, string value, List<UtilityDeclaration> declarations) =>
        EmitInto(family.Properties, value, declarations);

    static bool EmitInto(string[] properties, string value, List<UtilityDeclaration> declarations) {
        foreach (var property in properties) {
            declarations.Add(new UtilityDeclaration(property, value));
        }

        return true;
    }

    static bool EmitPair(string pair, List<UtilityDeclaration> declarations) {
        var colon = pair.IndexOf(':', StringComparison.Ordinal);
        declarations.Add(new UtilityDeclaration(pair[..colon], pair[(colon + 1)..]));
        return true;
    }

    static string Px(float value) => value.ToString("0.####", CultureInfo.InvariantCulture) + "px";

    static void Register(Family family) {
        // A family registered twice keeps the first, so that `flex` as a display utility is not
        // replaced by `flex` as a direction one — they are the same prefix and different values,
        // which the keyword table is what resolves.
        if (Registry.TryAdd(family.Name, family)) {
            Names.Add(family.Name);
            return;
        }

        var existing = Registry[family.Name];
        if (family.Keywords is null) {
            return;
        }

        var merged = existing.Keywords is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(existing.Keywords, StringComparer.Ordinal);

        foreach (var (key, value) in family.Keywords) {
            merged[key] = value.Contains(':', StringComparison.Ordinal) ? value : $"{family.Properties[0]}:{value}";
        }

        Registry[family.Name] = existing with { Keywords = merged };
    }

    static void Static(string name, string property, string value) =>
        Register(new Family(name, ValueKind.Static, [property], new Dictionary<string, string>(StringComparer.Ordinal) {
            [string.Empty] = $"{property}:{value}"
        }));

    static void Keywords(string name, string property, Dictionary<string, string> keywords) {
        var qualified = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in keywords) {
            qualified[key] = $"{property}:{value}";
        }

        Register(new Family(name, ValueKind.Keyword, [property], qualified));
    }

    static void Spacing(string name, params string[] properties) =>
        Register(new Family(name, ValueKind.Spacing, properties));

    /// <summary>One of the six proportional <c>filter</c> functions.</summary>
    /// <param name="name">The class prefix, which is also the CSS function's name.</param>
    /// <param name="fragment">The <c>--tw-*</c> the amount goes into.</param>
    /// <param name="bare">
    ///     What the class with no value means, or null where it means nothing. <c>grayscale</c>,
    ///     <c>invert</c> and <c>sepia</c> have one and the other three do not.
    /// </param>
    /// <remarks>
    ///     ⚠ <b>Every one of these emits the <i>whole</i> <c>filter</c> declaration alongside its
    ///     fragment, and that is what makes any of them work on its own.</b> See
    ///     <see cref="UtilityComposition.Filter" />: the declaration names all eight functions and
    ///     the seven nobody set resolve to their identities through the <c>var()</c> fallbacks, so
    ///     one class is one working filter and eight classes are one declaration rather than eight
    ///     fighting over the cascade.
    /// </remarks>
    static void Filter(string name, string fragment, string? bare = null) =>
        Register(new Family(
            name,
            ValueKind.Fraction,
            [fragment],
            bare is null
                ? null
                : new Dictionary<string, string>(StringComparer.Ordinal) { [string.Empty] = fragment + ":" + bare },
            Alongside: [new UtilityDeclaration("filter", UtilityComposition.Filter())]
        ));

    /// <summary>Registers a family whose rule is about the element's children rather than the element.</summary>
    /// <param name="name">The utility prefix.</param>
    /// <param name="kind">How its value turns into declarations — the same kinds as anything else.</param>
    /// <param name="properties">What it sets, on each child but the last.</param>
    /// <remarks>
    ///     ⚠ <b><c>:not(:last-child)</c> and not v4's <c>:where(&amp; &gt; :not(:last-child))</c>,
    ///     and the difference is one Vixen cannot currently paper over.</b> The <c>&amp;</c> is CSS
    ///     nesting, which the loader does not do, so the emitted form is the flattened one — proved
    ///     rather than assumed, in <c>ChildScopedFamilyTests</c>. The <c>:where()</c> is v4's way of
    ///     keeping the rule at one class of specificity so that a child's own <c>me-0</c> still
    ///     wins; here <c>SelectorCompiler</c> counts <c>:where()</c> like <c>:is()</c> and adds a
    ///     class either way, so the rule lands at <c>(0,2,0)</c> and beats a child's own single-class
    ///     utility. That is exactly what Tailwind v3 did for four major versions, it is written down
    ///     in the guide rather than left to be discovered, and the fix is three lines in a file this
    ///     project does not own.
    /// </remarks>
    static void Between(string name, ValueKind kind, string[] properties) =>
        Register(new Family(name, kind, properties, Scope: BetweenChildren));

    /// <summary>Every child but the last, which is what <c>space-*</c> and <c>divide-*</c> are about.</summary>
    const string BetweenChildren = " > :not(:last-child)";

    static void Size(string name, params string[] properties) =>
        Register(new Family(name, ValueKind.Size, properties));

    /// <summary>Registers one axis of the composed translation: a fragment, and the assembly.</summary>
    /// <param name="name">The utility prefix.</param>
    /// <param name="fragment">The fragment this axis sets.</param>
    /// <remarks>
    ///     ⚠ <b><see cref="ValueKind.Size" /> rather than <c>Spacing</c>, so that <c>translate-x-full</c>
    ///     is a hundred per cent.</b> CSS resolves a percentage translation against the element's own
    ///     border box, which is what makes <c>-translate-x-full</c> the idiom for sliding a panel
    ///     exactly its own width off the edge — a spacing-only family could not express it, and the
    ///     number it would need depends on a width nobody knows when the class is written.
    ///     <para>
    ///         The assembly rides in <c>Alongside</c>, which <see cref="TryResolve" /> appends
    ///         <i>after</i> negation — load-bearing here. <see cref="TryNegate" /> refuses a
    ///         declaration whose value does not begin with a digit, so an assembly appended first
    ///         would make <c>-translate-x-2</c> resolve to nothing at all rather than to minus eight
    ///         pixels, and the class would be reported as an unrecognised typo.
    ///     </para>
    /// </remarks>
    static void Translate(string name, string fragment) =>
        Register(new Family(
            name,
            ValueKind.Size,
            [fragment],
            Alongside: [new UtilityDeclaration("translate", UtilityComposition.Translation())]
        ));

    static void Number(string name, params string[] properties) =>
        Register(new Family(name, ValueKind.Number, properties));

    /// <summary>Registers a family whose count is substituted into a CSS template.</summary>
    /// <param name="name">The utility prefix.</param>
    /// <param name="template">The value, with <c>{0}</c> where the count goes.</param>
    /// <param name="properties">The properties it sets.</param>
    static void CountTemplate(string name, string template, params string[] properties) =>
        Register(new Family(name, ValueKind.CountTemplate, properties, Template: template));

    static void Color(string name, params string[] properties) =>
        Register(new Family(name, ValueKind.Color, properties));

    static void BorderEdge(string name, string[] widths, string[] colours) =>
        Register(new Family(name, ValueKind.BorderEdge, widths, ColorProperties: colours));

    static void Radius(string name, params string[] properties) =>
        Register(new Family(name, ValueKind.Radius, properties));

    /// <summary>Registers a composed family: a colour fragment, a position fragment, and no declaration.</summary>
    /// <summary>A linear mask, swept by <c>--tw-mask-linear-angle</c>.</summary>
    static string Linear => UtilityComposition.MaskImage("linear", UtilityComposition.Reference(UtilityComposition.MaskLinearAngle));

    /// <summary>A round mask. CSS's default is a centred farthest-corner ellipse, which is what Tailwind means.</summary>
    static string Radial => UtilityComposition.MaskImage("radial", string.Empty);

    /// <summary>A swept mask, started by <c>--tw-mask-conic-angle</c>.</summary>
    static string Conic => UtilityComposition.MaskImage("conic", $"from {UtilityComposition.Reference(UtilityComposition.MaskConicAngle)}");

    /// <summary>One mask stop: a colour or a position, and the <c>mask-image</c> that reads it.</summary>
    /// <param name="name">The class prefix.</param>
    /// <param name="colour">The fragment a colour goes into.</param>
    /// <param name="position">The fragment a percentage goes into.</param>
    /// <param name="layer">The <c>mask-image</c> layer fragment this shape fills.</param>
    /// <param name="image">The assembled gradient that goes in it.</param>
    /// <remarks>
    ///     ⚠ <b><see cref="ValueKind.GradientStop" /> rather than a kind of its own, and it is the
    ///     right one for a reason beyond convenience.</b> That kind is what routes a percentage to
    ///     <paramref name="position" /> and a colour to <paramref name="colour" />, which is exactly
    ///     the split a mask stop needs: <c>mask-linear-from-50%</c> is a position and
    ///     <c>mask-linear-from-black</c> is a colour. Only the alpha of the colour survives into
    ///     <c>UiMask</c>, but that is the renderer's business and not the parser's — a mask written
    ///     with <c>#00000080</c> means half coverage and has to reach the engine intact to say so.
    /// </remarks>
    static void Mask(string name, string colour, string position, string layer, string image) =>
        MaskFamily(name, [colour], [position], layer, image);

    /// <summary>The declarations every <c>mask-*</c> family emits beside whatever it was given.</summary>
    /// <param name="layer">The <c>mask-image</c> layer fragment this family fills.</param>
    /// <param name="image">The gradient that goes in it.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Three declarations and not one, and the <c>mask-composite</c> is the one that is
    ///         easy to think optional.</b> The layer fragment says what this class draws; the
    ///         <c>mask-image</c> says the list is three layers of which this is one; and the
    ///         <c>intersect</c> is what makes the two layers nobody filled — opaque, by their initial
    ///         value — change nothing. Without it the list composites with CSS's initial <c>add</c>,
    ///         under which an opaque layer forces full coverage everywhere and the mask does exactly
    ///         nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>intersect</c> is also what Tailwind writes</b>, on every one of its mask
    ///         utilities, for this reason. It is not CSS's default — that is <c>add</c>, which
    ///         <c>DrawListBuilder</c> honours for a hand-written <c>mask-image</c> list with nothing
    ///         beside it.
    ///     </para>
    /// </remarks>
    static UtilityDeclaration[] MaskAlongside(string layer, string image) => [
        new(layer, image),
        new("mask-image", UtilityComposition.MaskLayers()),
        new("mask-composite", "intersect")
    ];

    /// <summary>One mask stop family: colour fragments, position fragments, and the layer they fill.</summary>
    static void MaskFamily(string name, string[] colours, string[] positions, string layer, string image) =>
        Register(new Family(
            name,
            ValueKind.GradientStop,
            colours,
            Positions: positions,
            Alongside: MaskAlongside(layer, image)
        ));

    /// <summary>One edge-ramp family, which is <see cref="MaskFamily" /> over one or two edges.</summary>
    /// <param name="name">The class prefix, such as <c>mask-t-from</c>.</param>
    /// <param name="edges">Which edges it drives. Two for <c>mask-x-*</c> and <c>mask-y-*</c>.</param>
    /// <param name="near">Whether it sets the ramp's near stop rather than its far one.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every edge's gradient is emitted, not only the ones this class drives, and that is
    ///         what makes two edge classes compose.</b> `mask-t-from-50% mask-b-from-50%` is two rules
    ///         writing the same <c>--tw-mask-linear</c>; whichever the cascade picks, it names all
    ///         four edge fragments, and each of those resolves to whatever its own class set or to an
    ///         opaque gradient if nothing did. Emitting only the driven edge would make the second
    ///         class delete the first.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the edges take the <i>linear</i> layer.</b> See
    ///         <c>UtilityComposition.MaskEdgeLayers</c>: a <c>mask-t-*</c> beside a
    ///         <c>mask-linear-*</c> is a conflict rather than a composition, which is Tailwind's
    ///         behaviour and is what having one linear slot means.
    ///     </para>
    /// </remarks>
    static void MaskEdgeRamp(string name, string[] edges, bool near) {
        var colours = new string[edges.Length];
        var positions = new string[edges.Length];
        var alongside = new List<UtilityDeclaration>();

        for (var index = 0; index < edges.Length; index++) {
            colours[index] = near
                ? UtilityComposition.MaskEdgeFrom(edges[index])
                : UtilityComposition.MaskEdgeTo(edges[index]);

            positions[index] = near
                ? UtilityComposition.MaskEdgeFromPosition(edges[index])
                : UtilityComposition.MaskEdgeToPosition(edges[index]);
        }

        foreach (var edge in UtilityComposition.MaskEdges) {
            alongside.Add(new UtilityDeclaration(UtilityComposition.MaskEdge(edge), UtilityComposition.MaskEdgeImage(edge)));
        }

        alongside.AddRange(MaskAlongside(UtilityComposition.MaskLinear, UtilityComposition.MaskEdgeLayers()));

        Register(new Family(
            name,
            ValueKind.GradientStop,
            colours,
            Positions: positions,
            Alongside: [.. alongside]
        ));
    }



    static void GradientStop(string name, string colour, string position, params UtilityDeclaration[] alongside) =>
        Register(new Family(
            name,
            ValueKind.GradientStop,
            [colour],
            Positions: [position],
            Alongside: alongside.Length == 0 ? null : alongside
        ));

    /// <summary>One gradient assembler: the shape, the geometry, and the stop list.</summary>
    /// <param name="shape">
    ///     <c>linear</c>, <c>radial</c> or <c>conic</c> — the CSS function, without its suffix.
    /// </param>
    /// <param name="geometry">
    ///     What goes before the interpolation hint: a <c>to …</c> for a linear gradient, and nothing
    ///     for the two round ones, whose CSS defaults are what Tailwind means by them.
    /// </param>
    /// <returns>The <c>background-image</c> value.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The stop list is reached through <see cref="UtilityComposition.Reference" />, so
    ///         the two-stop form is what an absent <c>via-*</c> falls back to</b> rather than something
    ///         this string has to remember to spell. <c>from-red to-blue</c> with no <c>via</c> is a
    ///         two-stop gradient; the version of this that wrote <c>var(--tw-gradient-stops)</c> bare
    ///         would make it no gradient at all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>in oklab</c> on every one of them, because that is what Tailwind v4 emits and
    ///         the difference is not subtle.</b> CSS's default for an unhinted gradient is sRGB, and
    ///         the engine's palette now ships as v4.3.3's — quoted in <c>oklch</c>, chosen so that
    ///         equal steps look equal. Interpolating two of those swatches anywhere but a perceptual
    ///         space throws that away at the midpoint, which is the one pixel the choice is visible
    ///         at: between complements it is the difference between a colour and a grey dead zone.
    ///         Leaving the hint off would have been a gradient that is right at both ends and wrong in
    ///         the middle on every element in the editor.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Written into the value rather than left to the renderer's default</b>, and that is
    ///         the same argument the fragments make about <c>hover:from-*</c>: a hint in the text is
    ///         one a person reading a generated sheet against Tailwind's documentation sees, and one
    ///         <c>GradientReader</c> honours through the same code path it honours a hand-written
    ///         <c>in srgb</c> with. A renderer-side default would be a second place the answer lives.
    ///     </para>
    /// </remarks>
    static string Gradient(string shape, string geometry) {
        var prelude = geometry.Length == 0 ? "in oklab" : $"{geometry} in oklab";
        return $"{shape}-gradient({prelude}, {UtilityComposition.Reference(UtilityComposition.GradientStops)})";
    }
}
