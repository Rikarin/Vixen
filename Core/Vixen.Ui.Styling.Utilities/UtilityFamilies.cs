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
    /// <param name="Position">
    ///     Where a <see cref="ValueKind.GradientStop" /> family puts a percentage, which is a
    ///     different fragment from where it puts a colour — <c>from-accent</c> is
    ///     <c>--tw-gradient-from</c> and <c>from-40%</c> is <c>--tw-gradient-from-position</c>. Null
    ///     on every other kind.
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
    sealed record Family(
        string Name,
        ValueKind Kind,
        string[] Properties,
        Dictionary<string, string>? Keywords = null,
        string[]? ColorProperties = null,
        string? Position = null,
        UtilityDeclaration[]? Alongside = null,
        string? Template = null
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
        Static("truncate", "overflow", "hidden");

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

        // ⚠ <b>`grid-rows-*`, `row-span-*` and the four `col-`/`row-start`/`-end` families are still
        // absent, and the bridge is no longer the reason.</b> `LayoutStyleBuilder` reads all six
        // properties now — registering the families is one line each. What stops it is
        // `UtilityConsumptionProbe`: every one of its seven scenes makes `#probe` a *flex* container
        // whose parent is also flex, so a row template or a placement longhand injected onto it
        // moves nothing, and the gate would classify six live properties as inert. The gate's own
        // rule for that is explicit — the fix is a scene that observes them, not an allow-list line
        // — and a grid scene is a bigger change than the families are. Doc 43 § B2 owes it.

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

        Keywords("align", "vertical-align", new() {
            ["top"] = "top", ["middle"] = "middle", ["bottom"] = "bottom", ["baseline"] = "baseline"
        });

        // ── Colours ─────────────────────────────────────────────────────────────────────────
        Color("bg", "background-color");
        Color("ring", "outline-color");
        Color("fill", "fill");
        Color("stroke", "stroke");

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
        Spacing("blur", "--blur");

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

    static bool Resolve(Family family, UtilityCandidate candidate, ThemeTokens tokens, List<UtilityDeclaration> declarations) {
        // An arbitrary value goes straight through. That is the point of it: `w-[37px]` exists
        // precisely for the case the token scale does not cover, and second-guessing it would make
        // the escape hatch useless.
        if (candidate.Arbitrary is { } arbitrary) {
            return family.Kind == ValueKind.BorderEdge
                ? EmitInto(LooksLikeColor(arbitrary) ? family.ColorProperties! : family.Properties, arbitrary, declarations)
                : Emit(family, arbitrary, declarations);
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
            declarations.Add(new UtilityDeclaration(family.Position!, value));
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
    ///     The same ambiguity <c>text-</c> has, and unlike <c>text-</c> this one costs nothing: no
    ///     colour is plausibly named <c>2</c>, so the number-first order shadows nothing reachable.
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

        return TryColor(candidate, tokens, out var colour)
            && EmitInto(family.ColorProperties!, colour, declarations);
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
    static void GradientStop(string name, string colour, string position, params UtilityDeclaration[] alongside) =>
        Register(new Family(
            name,
            ValueKind.GradientStop,
            [colour],
            Position: position,
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
