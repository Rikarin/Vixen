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

    /// <summary>An angle in whole degrees substituted into a template: <c>bg-conic-180</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><see cref="CountTemplate" /> with one difference, and the difference is zero.</b>
    ///         <c>TryCount</c> refuses a count of zero, which is right for every family that has
    ///         one — <c>grid-cols-0</c> is <c>repeat(0, …)</c>, which is not a track list, and
    ///         <c>col-span-0</c> is not a span. An <i>angle</i> of zero is a real value that means
    ///         something specific: <c>bg-linear-0</c> is a ramp running upwards and
    ///         <c>bg-conic-0</c> is a sweep starting at twelve o'clock. Sharing the count's parser
    ///         would have left both of those reported as unrecognised typos, which is what they were
    ///         before this kind existed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Non-negative, and the reason is <c>TryNegate</c> rather than CSS.</b>
    ///         Negation is applied to the resolved declaration and refuses any value that does not
    ///         begin with a digit — and this kind's value is a whole <c>linear-gradient(…)</c>, so
    ///         <c>-bg-linear-45</c> cannot be flipped after the fact. Every negative angle has a
    ///         positive spelling (<c>bg-linear-315</c>), so the gap is a spelling and not a
    ///         capability; it is recorded on the row rather than papered over by inventing a second
    ///         negation path.
    ///     </para>
    /// </remarks>
    Angle,

    /// <summary>A CSS <c>&lt;position&gt;</c> or a pair of lengths, written as an arbitrary value.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Arbitrary-valued because Tailwind gives these two roots no named scale at all, and
    ///         inventing one is the failure <c>bg-conic-&lt;angle&gt;</c> is recorded under.</b> v4
    ///         spells <c>background-size</c> and <c>background-position</c> as
    ///         <c>bg-size-[&lt;value&gt;]</c> and <c>bg-position-[&lt;value&gt;]</c> — the keyword
    ///         forms it does ship (<c>bg-cover</c>, <c>bg-center</c>) hang off the bare <c>bg</c>
    ///         root, which is a different family.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Its own kind rather than <see cref="Static" />, because a family whose only
    ///         surface is an arbitrary value is one <c>UtilityConsumptionGateTests</c> would never
    ///         meet.</b> <c>ValuesFor</c> yields an arbitrary probe for this kind and for no
    ///         other — which is what makes these two families measurable rather than vacuously green,
    ///         and is exactly the objection that keeps <c>font-features-*</c> unregistered one section
    ///         up. The difference is the class name: a length and a percentage escape into a
    ///         selector, and the quotes <c>font-feature-settings</c> requires do not.
    ///     </para>
    /// </remarks>
    Placement,

    /// <summary>An OpenType feature list, which is arbitrary-only: <c>font-features-["onum"_1]</c>.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="Placement" />'s shape and the objection <see cref="Placement" />'s remark
    ///     raises against it, met rather than restated.</b> v4 has no named step for this root — the
    ///     value is always arbitrary — so like a placement it would contribute nothing to
    ///     <c>UtilityFamilies.Surface</c> without a probe of its own, and a family with no surface is one the
    ///     consumption gate never meets: it passes vacuously, for ever, while the ledger reads
    ///     <c>absent</c>. The thing that kept it out was the <i>class name</i>: every value of
    ///     <c>font-feature-settings</c> that does anything contains quotes, by CSS's grammar. That is
    ///     a question about <c>UtilityGenerator.Escape</c> and about the selector matcher, not
    ///     about the property — which is read end to end — and it is answered rather than avoided.
    /// </remarks>
    FontFeatures,

    /// <summary>A blur radius: a named step, or a spacing count: <c>blur-md</c>, <c>blur-8</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>Two scales under one prefix, and the named one wins — which is the opposite of the
    ///     arrangement <see cref="Radius" /> has and is deliberate.</b> A radius has only a named
    ///     scale, so <c>rounded-4</c> is not a class. A blur here has both: v4's spellings are
    ///     <c>blur-xs</c>…<c>blur-3xl</c> and nothing else, and this engine answered a spacing count
    ///     and nothing else — so keeping the count is a superset that costs a line, while dropping
    ///     it would break <c>backdrop-blur-8</c> in this repository's own remarks. Named first,
    ///     because the two cannot collide: a theme key is not a number.
    /// </remarks>
    Blur,

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

    /// <summary>A <c>drop-shadow()</c>'s arguments named by a token: <c>drop-shadow-lg</c>.</summary>
    /// <remarks>
    ///     ⚠ Its own kind rather than <see cref="Shadow" /> against a second table, because the two
    ///     are read out of different theme namespaces <i>and</i> one is a whole declaration where the
    ///     other is one item of a list. See <see cref="ThemeTokens.DropShadow" />: a
    ///     <c>box-shadow</c> token may hold a comma, and a comma inside <c>filter</c> invalidates the
    ///     declaration it lands in.
    /// </remarks>
    DropShadow,

    /// <summary>A gradient stop, which is a colour or a position: <c>from-accent</c>, <c>from-40%</c>.</summary>
    /// <remarks>
    ///     ⚠ Composed — it emits a <see cref="UtilityComposition" /> fragment and no declaration of
    ///     its own. The colour and the position are separate fragments because Tailwind lets them be
    ///     written separately: <c>from-accent from-40%</c> is two classes setting two things.
    /// </remarks>
    GradientStop
}

/// <summary>What a top-level slash means to a family.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A slash is not one thing, and reading it as one was a silent wrong answer rather
///         than a missing feature.</b> <see cref="UtilityParser" /> has always kept both readings —
///         <see cref="UtilityCandidate.Opacity" /> and <see cref="UtilityCandidate.SlashSuffix" /> —
///         and its own remark says which one a slash means is the utility's to decide. Nothing
///         decided: a family that did not look at the suffix simply resolved the head and dropped
///         it, so <c>aspect-16/9</c> emitted <c>aspect-ratio: 16</c> and <c>text-lg/7</c> emitted
///         the theme's line height. Both are valid CSS with a value nobody asked for, which is worse
///         than an unrecognised class.
///     </para>
///     <para>
///         <b>So every family says which reading it takes, and <see cref="None" /> is a refusal.</b>
///         A slash on a family that has no modifier means the class was misspelt, and the honest
///         answer is no rule at all — the same answer <c>TryArbitraryProperty</c> already gave
///         <c>[color:red]/50</c> for exactly this reason.
///     </para>
/// </remarks>
enum SlashMeaning : byte {
    /// <summary>No modifier. A slash is a misspelling and the class is refused.</summary>
    None,

    /// <summary>An alpha on the colour: <c>bg-accent/50</c>.</summary>
    Opacity,

    /// <summary>A denominator: <c>w-2/3</c> is two thirds wide.</summary>
    Fraction,

    /// <summary>The other half of a ratio: <c>aspect-16/9</c>.</summary>
    Ratio,

    /// <summary>
    ///     A line height beside a font size — <c>text-lg/7</c> — or an alpha when the value read as
    ///     a colour instead, which is the one family whose slash means two things.
    /// </summary>
    Leading
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
    /// <param name="ValueAlongside">
    ///     Declarations emitted verbatim when the family resolves <i>a particular keyword</i>, keyed
    ///     by that keyword.
    ///     <para>
    ///         ⚠ <b>This exists because a Tailwind prefix is not always one property family, and
    ///         <see cref="Alongside" /> above cannot express that.</b> <c>mask-circle</c> and
    ///         <c>mask-ellipse</c> are spelled with the bare <c>mask</c> prefix, which is already the
    ///         <c>mask-repeat</c> family — and <see cref="Register" /> keeps the first family under a
    ///         name and discards a second silently, so a second registration is not an option. The
    ///         two shape values need the three mask-layer declarations every other
    ///         <c>mask-radial-*</c> emits and the four repeat values must not have them, which is a
    ///         difference between <i>values</i> and not between families.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Keyed on the keyword as written, and applied after the value resolved</b>, for
    ///         <see cref="Alongside" />'s reason one step finer: a table keyed on a value the family
    ///         does not accept would emit a mask layer for a typo, which is a rule that exists and
    ///         silently changes the picture.
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
    ///         <c>:where(.space-x-4 &gt; :not(:last-child))</c>, which the selector engine compiles
    ///         and matches — <see cref="SimpleSelectorKind.Not" />, <see cref="PositionTest.Last" />
    ///         and <see cref="Combinator.Child" /> have all been there the whole time, and the
    ///         <c>:where()</c> that keeps the rule out of a child's way is the generator's.
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
        Dictionary<string, UtilityDeclaration[]>? ValueAlongside = null,
        string? Template = null,
        string? Scope = null
    ) {
        /// <summary>Which reading of a top-level slash this family takes.</summary>
        /// <remarks>
        ///     ⚠ <b>Defaulted from <see cref="Kind" /> rather than written on every registration,
        ///     because a family that forgot to say would then quietly take the permissive reading
        ///     — and permissive is the bug.</b> Two hundred registrations and one omission is all
        ///     it takes to put a silently-dropped modifier back. The kinds that take one are the
        ///     kinds that resolve a colour (an alpha) and the sizing kind (a fraction); everything
        ///     else refuses, and the two families whose slash the kind cannot predict —
        ///     <c>aspect</c> and <c>text</c> — say so themselves.
        /// </remarks>
        public SlashMeaning Slash { get; init; } = Kind switch {
            ValueKind.Color or ValueKind.BorderEdge or ValueKind.Shadow or ValueKind.DropShadow
                or ValueKind.GradientStop => SlashMeaning.Opacity,
            ValueKind.Size => SlashMeaning.Fraction,
            ValueKind.FontSize => SlashMeaning.Leading,
            _ => SlashMeaning.None
        };
    }

    static readonly Dictionary<string, Family> Registry = new(StringComparer.Ordinal);
    static readonly List<string> Names = [];

    static UtilityFamilies() {
        // ── Layout ──────────────────────────────────────────────────────────────────────────
        //
        // ⚠ <b>`block` and `inline` are two Tailwind roots apiece, and the bare form is the one that
        // is `display`.</b> `block` is `display: block`; `block-40` is `block-size: 40 * spacing`,
        // which here is `height`. `Register` keeps the first family under a name, so these cannot be
        // a `Static` and a `Size` registered in the two sections they belong to — they are one family
        // whose empty keyword is the display value and whose value kind is the sizing one, which is
        // the same shape `flex` has for the same reason. The sizing half is written up beside the
        // other four logical roots, under `── Sizing ──` below.
        StaticOrSize("block", "display", "block", "height", "100vh");
        StaticOrSize("inline", "display", "inline", "width", "100vw");
        Static("inline-block", "display", "inline-block");
        Static("flex", "display", "flex");
        Static("inline-flex", "display", "inline-flex");
        Static("grid", "display", "grid");
        Static("hidden", "display", "none");

        // ⚠ <b>All of Tailwind's keywords, and the two that used to be missing were missing for a
        // reason that turned out to be a conflation.</b> This comment said `float-start` and
        // `float-end` emit `float: inline-start` / `inline-end`, "which CSS Logical Properties
        // resolves against the writing mode", and then listed three shapes of which only leaving
        // them unspelt was honest. CSS Logical Properties resolves them against the writing mode
        // AND the direction, and with no vertical writing mode — the decision #282 recorded — the
        // inline axis is horizontal in every configuration this engine can be in. So the resolution
        // is `direction` alone, `FloatSide` and `Clear` gained a flow-relative pair each, and there
        // was a fourth shape all along.
        //
        // ⚠ <b>The float corpus observation was true and was about the other keywords.</b> Ten
        // `float_bfc_*` families do ship RTL variants with identical expectations, which proves
        // `float: left` does not flip — and that is the reason `inline-start` is a separate value
        // rather than a rereading of `left`, not a reason it cannot be spelt.
        Keywords("float", "float", new() {
            ["left"] = "left", ["right"] = "right", ["none"] = "none",
            ["start"] = "inline-start", ["end"] = "inline-end"
        });

        Keywords("clear", "clear", new() {
            ["left"] = "left", ["right"] = "right", ["both"] = "both", ["none"] = "none",
            ["start"] = "inline-start", ["end"] = "inline-end"
        });

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

        // ⚠ <b>`sr-only` is eight declarations where v4 writes nine, and the missing one is `clip`.</b>
        // The class hides an element from sight while leaving it in the accessibility tree, and the
        // eight that land are what does the hiding: a one-point absolutely-positioned box with no
        // edges, pulled a point out of flow, clipping whatever is inside it. `clip: rect(0,0,0,0)`
        // adds nothing to that — it is 2009's spelling of the same intent, kept in the v4 recipe for
        // browsers that shipped before `overflow: hidden` on a 1×1 box was reliable — and nothing in
        // this engine reads a clip rectangle, so emitting it would put a property on
        // `InertProperties.txt` for no behaviour. That is the documented substitute #268 asks for
        // rather than a reader.
        //
        // ⚠ <b>The class only means anything because the accessibility tree is built from
        // <c>Role</c> and not from geometry.</b> `UiElement.IsInAccessibilityTree` asks the role
        // alone; nothing subtracts an element for being one point wide, clipped or off-screen. Were
        // it otherwise this family would not hide an element from sight, it would delete it — which
        // is the opposite of what the author wrote, and would have been the reason to refuse.
        Register(new Family(
            "sr-only",
            ValueKind.Static,
            ["position"],
            new Dictionary<string, string>(StringComparer.Ordinal) { [string.Empty] = "position:absolute" },
            Alongside: [
                new UtilityDeclaration("width", "1px"),
                new UtilityDeclaration("height", "1px"),
                new UtilityDeclaration("padding", "0"),
                new UtilityDeclaration("margin", "-1px"),
                new UtilityDeclaration("overflow", "hidden"),
                new UtilityDeclaration("white-space", "nowrap"),
                new UtilityDeclaration("border-width", "0")
            ]
        ));

        // The undo, and it is not the same list backwards: v4's `not-sr-only` restores seven
        // properties and leaves `border-width` alone, because a border the element declared for
        // itself is not `sr-only`'s to give back.
        Register(new Family(
            "not-sr-only",
            ValueKind.Static,
            ["position"],
            new Dictionary<string, string>(StringComparer.Ordinal) { [string.Empty] = "position:static" },
            Alongside: [
                new UtilityDeclaration("width", "auto"),
                new UtilityDeclaration("height", "auto"),
                new UtilityDeclaration("padding", "0"),
                new UtilityDeclaration("margin", "0"),
                new UtilityDeclaration("overflow", "visible"),
                new UtilityDeclaration("white-space", "normal")
            ]
        ));

        // ⚠ <b>Two static roots and not a keyword family, because v4 spells them with two different
        // shapes</b> — `isolate` bare and `isolation-auto` prefixed — which is `normal-nums`' problem
        // further down and has the same answer.
        //
        // ⚠ <b>The property was worth nothing at all until `mix-blend-mode` existed, and is worth
        // exactly a composited group now that it does.</b> `isolation` has no picture of its own: its
        // only defined effect is on a descendant's blend, and it bounds that blend by being a
        // stacking context. `DrawListBuilder` reads it as a sixth reason to open a group, and a
        // nested group's draws land in its parent's surface — so the bound comes out of the
        // compositor's existing shape rather than out of anything new. See `UiLayer.Blend`.
        Static("isolate", "isolation", "isolate");
        Static("isolation-auto", "isolation", "auto");

        // ⚠ <b>Fourteen static roots and not two keyword families, because Tailwind spells two
        // properties under one prefix</b> — `object-contain` is a fit and `object-center` is a
        // position, and a family named `object` would have to decide which property a value belongs
        // to by looking the value up. That is `mask-alpha`'s arrangement beside `mask-repeat`, one
        // prefix and two meanings, and the answer there was separate registry roots.
        //
        // ⚠ <b>Four of the nine positions are TWO-WORD values, which is why this root needed a
        // reader and not just a table.</b> `object-left-top` computes to `left top`, and
        // `UiDocument.KeywordOf` answers null to anything that is not one bare identifier — so the
        // four corners were unreadable by every accessor `StyleAccess` had. `UiDocument.PositionOf`
        // is the fifth, and it is the same `<position>` parser `background-position` uses.
        //
        // ⚠ <b>All five fit keywords but `fill` are undefined without an intrinsic size, and
        // supplying one is an application's job.</b> `Image.IntrinsicSize` is where it goes; zero
        // means unknown, and unknown draws `fill` whatever the class says — which is CSS's own answer
        // for content with no intrinsic dimensions rather than a shortfall.
        Static("object-contain", "object-fit", "contain");
        Static("object-cover", "object-fit", "cover");
        Static("object-fill", "object-fit", "fill");
        Static("object-none", "object-fit", "none");
        Static("object-scale-down", "object-fit", "scale-down");
        Static("object-bottom", "object-position", "bottom");
        Static("object-center", "object-position", "center");
        Static("object-left", "object-position", "left");
        Static("object-left-bottom", "object-position", "left bottom");
        Static("object-left-top", "object-position", "left top");
        Static("object-right", "object-position", "right");
        Static("object-right-bottom", "object-position", "right bottom");
        Static("object-right-top", "object-position", "right top");
        Static("object-top", "object-position", "top");

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

        // ⚠ <b>The <c>-safe</c> suffix is a prefix in the CSS, and the two halves are one value.</b>
        // Tailwind spells CSS Box Alignment §4.1's <c>[ safe | unsafe ]? &lt;position&gt;</c> with the
        // overflow position last — <c>items-end-safe</c> — and CSS writes it first, so the keyword
        // tables below map one to the other rather than composing the class name.
        //
        // ⚠ <b>These arrived in two stages and the gap between them was invisible from either
        // end.</b> The layout has had `OverflowAlignment`, six `*Overflow` fields and
        // `LayoutTree.SafeFallback` at six sites since safe alignment landed, and 76 conformance
        // fixtures pass against it — but `LayoutStyleBuilder` never read the prefix, because a
        // two-word value reaches it as a token list and `TryKeyword` wants one keyword. So the
        // engine could do this and no stylesheet could ask for it. The reader is now
        // `LayoutStyleBuilder.TryAlignment` and these are what spell it.
        //
        // ⚠ <b>Only <c>center</c> and <c>end</c> take the suffix, and that is not an omission.</b>
        // `safe start` is a contradiction — start is where safe falls back to — and the prefix is
        // invalid on `stretch`, `baseline` and the three `space-*` distributions, which the bridge
        // refuses by name. `items-baseline-last`, `self-baseline-last`, `justify-baseline` and
        // `justify-stretch` are Tailwind roots deliberately still absent: `Align` has no
        // last-baseline member and `Justify` has no `Stretch`, so each would be a class that
        // resolves onto a keyword the bridge drops. Recorded in `43-web-styling-parity.tsv`'s `note`
        // column on the `items`, `justify` and `self` rows — ⚠ not `value_gap`, which is an *input*
        // to the generated state and would drag two `works` rows to `partial` over classes the
        // ledger never listed. `content-*` is the row that does carry its refusal in `value_gap`,
        // and is `partial` because of it.
        Keywords("items", "align-items", new() {
            ["start"] = "flex-start", ["end"] = "flex-end", ["center"] = "center",
            ["baseline"] = "baseline", ["stretch"] = "stretch",
            ["center-safe"] = "safe center", ["end-safe"] = "safe end"
        });

        Keywords("self", "align-self", new() {
            ["auto"] = "auto", ["start"] = "flex-start", ["end"] = "flex-end",
            ["center"] = "center", ["baseline"] = "baseline", ["stretch"] = "stretch",
            ["center-safe"] = "safe center", ["end-safe"] = "safe end"
        });

        // ⚠ <b><c>normal</c> is the initial value written out, and it is not a no-op</b> — the same
        // argument `visible` makes two sections up. `justify-content: normal` behaves as
        // `flex-start` and `align-content: normal` as `stretch`, which is what the bridge already
        // does with the property unset; what the class buys is overriding a `justify-center` that a
        // theme sheet or an earlier utility set, which nothing else in this vocabulary can say.
        Keywords("justify", "justify-content", new() {
            ["normal"] = "normal",
            ["start"] = "flex-start", ["end"] = "flex-end", ["center"] = "center",
            ["between"] = "space-between", ["around"] = "space-around", ["evenly"] = "space-evenly",
            ["center-safe"] = "safe center", ["end-safe"] = "safe end"
        });

        Keywords("content", "align-content", new() {
            ["normal"] = "normal",
            ["start"] = "flex-start", ["end"] = "flex-end", ["center"] = "center",
            ["between"] = "space-between", ["around"] = "space-around", ["stretch"] = "stretch",
            ["evenly"] = "space-evenly",
            ["center-safe"] = "safe center", ["end-safe"] = "safe end"
        });

        // ── The three `place-*` shorthands ──────────────────────────────────────────────────
        //
        // ⚠ <b>Two longhands each and not the shorthand, and the difference is ExCSS.</b> The same
        // trade `scroll-m-*` makes further down, arrived at the same way: ExCSS has never heard of
        // `place-content`, `place-items` or `place-self`, so it hands each back whole — and
        // `ShorthandExpansion` does not take them apart, which its own remark says and lists them
        // for. A family emitting `place-content: center` would therefore produce a declaration that
        // parses, cascades, resolves, and reaches a computed style under a name no consumer asks
        // for. Measured: the class would have resolved and moved nothing.
        //
        // ⚠ <b>So a <i>hand-written</i> `place-content: center` in a `.vcss` is still silent</b>, and
        // registering these does not change that. Filed as `Rikarin/Vixen#529`; the grammar is the
        // reason it is not done here — `place-content: safe center` is one value and not two, so a
        // whitespace split would emit `align-content: safe` and `justify-content: center`, which is
        // two refused declarations rather than one honoured one.
        //
        // ⚠ <b>`place-items-baseline` is half a real answer and is registered anyway.</b>
        // `align-items: baseline` is genuine — grid shims the row and flex aligns the line — while
        // `justify-items: baseline` falls through `AlignInArea`'s switch to the start edge, which is
        // exactly the fallback alignment CSS Box Alignment §9.3 specifies when baseline alignment
        // cannot be performed. A degradation the spec names is not an inert half.
        Register(new Family(
            "place-content",
            ValueKind.Keyword,
            ["align-content", "justify-content"],
            new Dictionary<string, string>(StringComparer.Ordinal) {
                ["center"] = "center", ["start"] = "start", ["end"] = "end",
                ["center-safe"] = "safe center", ["end-safe"] = "safe end",
                ["between"] = "space-between", ["around"] = "space-around", ["evenly"] = "space-evenly"
            }
        ));

        Register(new Family(
            "place-items",
            ValueKind.Keyword,
            ["align-items", "justify-items"],
            new Dictionary<string, string>(StringComparer.Ordinal) {
                ["center"] = "center", ["start"] = "start", ["end"] = "end",
                ["center-safe"] = "safe center", ["end-safe"] = "safe end",
                ["baseline"] = "baseline", ["stretch"] = "stretch"
            }
        ));

        Register(new Family(
            "place-self",
            ValueKind.Keyword,
            ["align-self", "justify-self"],
            new Dictionary<string, string>(StringComparer.Ordinal) {
                ["auto"] = "auto", ["center"] = "center", ["start"] = "start", ["end"] = "end",
                ["center-safe"] = "safe center", ["end-safe"] = "safe end",
                ["stretch"] = "stretch"
            }
        ));

        // ── Flex and grid ───────────────────────────────────────────────────────────────────
        Number("grow", "flex-grow");
        Number("shrink", "flex-shrink");
        Number("order", "order");

        // ⚠ <b>`order-none` is `order: 0` and not a keyword CSS has</b>, which is why it belongs in a
        // table rather than in `Number`'s scale: v4 emits the initial value under a name that reads
        // as an absence. The property is already read — `LayoutTree.Order` sorts a container's
        // children by it — so this is the smallest shape a registration can have, an existing reader
        // and one more spelling that reaches it.
        Keywords("order", "order", new() { ["none"] = "0" });
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

        // ⚠ <b>The bare roots, which are the shorthand and not a sixth and seventh longhand.</b>
        // `col-3` is `grid-column: 3` — v4's own spelling for "put this item in column 3" — and it
        // is a different statement from `col-start-3`, which names only the start edge and leaves
        // the end auto. They happen to compute the same thing here, because `ShorthandExpansion`
        // splits `grid-column: 3` into `grid-column-start: 3` and `grid-column-end: auto`, and that
        // is exactly what makes the shorthand worth emitting rather than the pair: the expansion
        // gives the cascade two comparable declarations, so a later `col-end-5` beats the `auto`
        // this one wrote and an earlier one loses to it. Emitting the longhands here would have made
        // the class unable to reset an end edge, which is the half of a shorthand that is not the
        // value it names.
        //
        // ⚠ <b>Two-letter names under five longer ones that start with them, which `SplitName`
        // settles and registration order does not.</b> `Names` is sorted longest-first at the end of
        // this constructor, so `col-span-2`, `col-start-3` and `col-end-1` go on splitting on their
        // own prefixes and these two only ever catch `col-<n>`, `col-auto`, `row-<n>` and
        // `row-auto`. Written here beside the four they could otherwise have swallowed, because the
        // guarantee is thirteen hundred lines away.
        //
        // `Number` rather than `CountTemplate` for `col-start`'s reason: a line number is emitted as
        // written, so `-col-3` is `grid-column: -3` and §8.3 counts it back from the end edge.
        Number("col", "grid-column");
        Number("row", "grid-row");

        Keywords("col", "grid-column", new() { ["auto"] = "auto" });
        Keywords("row", "grid-row", new() { ["auto"] = "auto" });

        // ⚠ <b>`start` and `end` rather than `flex-start` and `flex-end`, which is the opposite of
        // what `items-*` above emits.</b> Both spellings reach `Align.FlexStart` through the bridge's
        // one alignment table, so the choice is about what a generated sheet reads like next to
        // Tailwind's documentation — and `justify-items: flex-start` is not a value CSS Box Alignment
        // gives that property, so a browser would drop the very declaration this engine honours.
        //
        // ⚠ <b>`normal` and the two `-safe` values used to be missing on purpose, and the reason
        // given for it was half right.</b> The note here said `justify-items: safe center` "is two
        // tokens and the cascade hands the bridge one interned keyword, so it would fall out of the
        // alignment table" — the conclusion was correct and the mechanism was not. The cascade hands
        // the bridge a `StyleValueKind.List` of two keywords, because `StyleValueParser` splits on
        // top-level whitespace before it decides anything; there is no interned `"safe center"` for
        // the table to miss. Either way the property stayed at its initial value with nothing said.
        // `LayoutStyleBuilder.TryAlignment` is the reading these were waiting for.
        Keywords("justify-items", "justify-items", new() {
            ["normal"] = "normal",
            ["start"] = "start", ["end"] = "end", ["center"] = "center", ["stretch"] = "stretch",
            ["center-safe"] = "safe center", ["end-safe"] = "safe end"
        });

        Keywords("justify-self", "justify-self", new() {
            ["auto"] = "auto", ["start"] = "start", ["end"] = "end",
            ["center"] = "center", ["stretch"] = "stretch",
            ["center-safe"] = "safe center", ["end-safe"] = "safe end"
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

        // ⚠ <b>The block pair is physical where the inline pair above is logical, and it is the same
        // asymmetry `scroll-mbs-*`, `inset-bs-*` and `border-bs-*` already carry.</b> Nothing interns
        // `margin-block-start`: `LayoutStyleBuilder.EdgeNames.For` interns the four physical edges and
        // the two *inline* logical ones, because those two are the pair `direction` mirrors and the
        // store resolves them per element. So the block spelling would resolve, compute a value and
        // move nothing — the inert family the consumption gate exists to keep out.
        //
        // The physical spelling is not an approximation of it. `Vixen.Ui.Layout` has no writing mode,
        // so the block axis is top-to-bottom in every configuration this engine can be in, and
        // `margin-block-start` *is* `margin-top` on every element that could ever resolve it. ⚠ The
        // contrast worth keeping is `rounded-ss-*`, which is deliberately not done this way: a corner
        // is named on the inline axis too, and that axis really does mirror.
        //
        // ⚠ <b>`mbs` shadows nothing, though it reads as if it should.</b> `SplitName` takes the
        // longest registered prefix, so `mbs-4` reaches here and `mb-4` still reaches `mb` —
        // `ShadowedFamilyTests` holds that rule, and it is the same one `scroll-mbs` relies on.
        Spacing("mbs", "margin-top");
        Spacing("mbe", "margin-bottom");
        Spacing("pbs", "padding-top");
        Spacing("pbe", "padding-bottom");

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

        // ⚠ <b>The block pair is physical where the inline pair above is logical, and the asymmetry
        // is the same one `inset-bs-*` and `border-bs-*` already carry.</b> `ScrollView.EdgeIds.For`
        // interns six names per family — the four physical edges and the two *inline* logical ones —
        // so `scroll-margin-block-start` is read by nobody and would measure inert on every scene.
        // The physical spelling is not an approximation of it: `Vixen.Ui.Layout` has no writing mode,
        // so the block axis is top-to-bottom in every configuration this engine can be in, and
        // `scroll-margin-block-start` would mean `scroll-margin-top` on every element that ever
        // resolved it. Contrast `rounded-ss-*`, which is *not* done this way: a radius corner is
        // named on the inline axis too, and that axis really does mirror.
        Spacing("scroll-mbs", "scroll-margin-top");
        Spacing("scroll-mbe", "scroll-margin-bottom");

        Spacing("scroll-p", "scroll-padding-top", "scroll-padding-right", "scroll-padding-bottom", "scroll-padding-left");
        Spacing("scroll-px", "scroll-padding-left", "scroll-padding-right");
        Spacing("scroll-py", "scroll-padding-top", "scroll-padding-bottom");
        Spacing("scroll-pt", "scroll-padding-top");
        Spacing("scroll-pr", "scroll-padding-right");
        Spacing("scroll-pb", "scroll-padding-bottom");
        Spacing("scroll-pl", "scroll-padding-left");
        Spacing("scroll-ps", "scroll-padding-inline-start");
        Spacing("scroll-pe", "scroll-padding-inline-end");

        // Physical for the reason `scroll-mbs`/`scroll-mbe` are, one comment up.
        Spacing("scroll-pbs", "scroll-padding-top");
        Spacing("scroll-pbe", "scroll-padding-bottom");

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

        // ⚠ <b>Four Tailwind roots and one family, because all twelve classes are spelled `snap-`
        // and they set three different properties.</b> `snap-y` is the container's axis,
        // `snap-mandatory` its strictness, `snap-start` an item's alignment and `snap-always` an
        // item's stop — and `Register` keeps the first family under a name, so a family per property
        // is not available. The keyword table already carries a property per value, which is what
        // makes one entry per class the natural shape rather than a workaround.
        //
        // ⚠ <b>The strictness is a fragment and the axis references it, which is the one thing here
        // that could not be a plain declaration.</b> `snap-y snap-mandatory` is two classes writing
        // one `scroll-snap-type`; the axis class cannot know the strictness and the strictness class
        // cannot know the axis, so the axis names it through a `var()` whose fallback is CSS's own
        // `proximity`. `ScrollView.SnapType` reads the assembled value as *text* for this reason —
        // what arrives there was joined by the cascade rather than typed by a person, and it is
        // deliberately order-independent.
        //
        // ⚠ <b>`snap-align-none` and not `snap-none` for the alignment's off switch.</b> `snap-none`
        // is already the container's `scroll-snap-type: none`, and that is v4's spelling of both —
        // one prefix, two properties, and the longer name belongs to the one that came second.
        Register(new Family(
            "snap",
            ValueKind.Keyword,
            ["scroll-snap-type"],
            new Dictionary<string, string>(StringComparer.Ordinal) {
                ["none"] = "scroll-snap-type:none",
                ["x"] = $"scroll-snap-type:x {SnapStrictness}",
                ["y"] = $"scroll-snap-type:y {SnapStrictness}",
                ["both"] = $"scroll-snap-type:both {SnapStrictness}",
                ["mandatory"] = $"{UtilityComposition.ScrollSnapStrictness}:mandatory",
                ["proximity"] = $"{UtilityComposition.ScrollSnapStrictness}:proximity",
                ["start"] = "scroll-snap-align:start",
                ["end"] = "scroll-snap-align:end",
                ["center"] = "scroll-snap-align:center",
                ["align-none"] = "scroll-snap-align:none",
                ["normal"] = "scroll-snap-stop:normal",
                ["always"] = "scroll-snap-stop:always"
            }
        ));

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
        //
        // ⚠ <b>`screen` is the one sizing value that depends on the PROPERTY and not on the value,
        // which is why it is registered per family where the six viewport keywords are not.</b>
        // Tailwind names `svw`/`dvh` and their four siblings after the viewport axis being
        // *measured*, so `h-dvw` really is `height: 100vw` and one rule in `TrySize` answers every
        // family. `screen` is named after nothing: `w-screen` is the viewport's width and
        // `h-screen` is its height, from the same word. `TrySize` sees only the value, so it cannot
        // tell them apart — and answering both with `100%` (which is what it did) is wrong in
        // exactly the case anyone writes the class for, because a percentage resolves against the
        // containing block and a viewport unit against the surface. Inside any ancestor that is not
        // full size the two disagree.
        SizeToScreen("w", "100vw", "width");
        SizeToScreen("h", "100vh", "height");
        Size("size", "width", "height");
        SizeToScreen("min-w", "100vw", "min-width");
        SizeToScreen("min-h", "100vh", "min-height");
        SizeToScreen("max-w", "100vw", "max-width");
        SizeToScreen("max-h", "100vh", "max-height");

        // ⚠ <b>The six writing-mode-relative sizing roots, and all six are physical — including the
        // inline three, which is the half that looks wrong and is not.</b> The block three go the way
        // `inset-bs-*` and `scroll-mbs-*` went: `Vixen.Ui.Layout` has no writing mode, so the block
        // axis is top-to-bottom in every configuration this engine can be in, and `block-size` would
        // mean `height` on every element that ever resolved it.
        //
        // ⚠ <b>The inline three are physical for a different and stronger reason, and it is worth
        // separating because the neighbouring precedent points the other way.</b> `inset-s-*` and
        // `rounded-ss-*` keep their logical spelling because `direction: rtl` really does mirror
        // them — an *edge* and a *corner* are named by which end of the inline axis they sit at, and
        // which end that is depends on the direction. A *size* is not: `inline-size` is the extent
        // *along* the inline axis, and mirroring the axis does not change how long it is.
        // `direction` chooses a direction within the axis; only a writing mode chooses which axis is
        // inline, and there is none. So `inline-size` is `width` in LTR and in RTL alike, which is
        // strictly safer than the block mapping rather than a compromise with it.
        //
        // ⚠ <b>And the deciding fact is in the code rather than in the reasoning above.</b>
        // `inline-size` and `block-size` appear in this tree in exactly one place — `ContainerQuery`,
        // where they are `container-type` values and query feature names, not declarations — and
        // `ContainerQuery.Match` maps them to width and height with no direction consulted and the
        // same comment written over it. Nothing interns either name as a property: `LayoutStyleBuilder`
        // interns `width`, `height`, `min-`/`max-` of each, and no logical spelling. Emitting the
        // logical names would therefore resolve, compute, and move nothing — the inert family this
        // table is not allowed to add.
        //
        // ⚠ <b>Four of the six are here and two are not.</b> `inline-*` and `block-*` are registered
        // under `── Layout ──` by `StaticOrSize`, because Tailwind spells `display: block` and
        // `block-size` with the same prefix and `Register` keeps the first family under a name — a
        // `Size("block", "height")` written here would be discarded without a word and every
        // `block-*` class would go on being reported as a typo.
        SizeToScreen("min-inline", "100vw", "min-width");
        SizeToScreen("max-inline", "100vw", "max-width");
        SizeToScreen("min-block", "100vh", "min-height");
        SizeToScreen("max-block", "100vh", "max-height");

        // ── Position ────────────────────────────────────────────────────────────────────────
        Static("static", "position", "static");
        Static("relative", "position", "relative");
        Static("absolute", "position", "absolute");

        // ⚠ <b>`sticky` is here and `fixed` never will be, and the two look alike from Tailwind's
        // side only.</b> Doc 09 refuses `fixed` because there is no viewport in a game overlay — a
        // box positioned against one has nothing to be positioned against. That argument does not
        // reach `sticky`, whose reference is a SCROLLPORT: the nearest scrolling ancestor's box,
        // which every `ScrollView` in the editor has. A sticky table header inside a scroller is a
        // real requirement rather than a web habit.
        //
        // ⚠ <b>And it is honoured outside `Vixen.Ui.Layout`, which is not where doc 43 sized it.</b>
        // A sticky box's offset is a function of a scroll offset and that store has none —
        // `ScrollView` scrolls by writing `UiElement.OffsetY`, which never reaches the layout tree.
        // `UiDocument.Accumulate` is where a position is already assembled from more than one
        // contribution, and it is where this one lands. See `Core/Vixen.Ui/Sticky.cs`.
        Static("sticky", "position", "sticky");
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

        // ⚠ <b>`text-indent` was one of Part 0's seven interned-but-unread properties and is now
        // read, and what it needed was not a reader.</b> `LineWrapper.Wrap` took one width for the
        // whole paragraph, so an indent had to become a *second* width — the first line is narrower
        // and the rest are not, which is what an indent is — and the offset then has to travel on
        // the line, because the draw list, the caret and the hit test all measure from it.
        // `UiDocument.ResolveText` computes it beside `line-height` and `letter-spacing` rather than
        // inheriting the specified value, for the same reason those two do: it takes relative units.
        //
        // ⚠ The spacing scale rather than a keyword table, which is Tailwind's own: `indent-4` is
        // four spacing steps and `-indent-4` hangs the first line out to the left, which CSS calls a
        // hanging indent and `LineWrapper` gets for nothing from the sign.
        Spacing("indent", "text-indent");

        // ⚠ <b>The two that were missing are the two the reader is RIGHT about</b>, which inverts the
        // usual argument for leaving a keyword out. `UiDocument.WrapsOf` answers one of
        // `white-space`'s three questions — whether the text may break across lines — and its own
        // remark records that `pre` is registered while being answered wrongly, because `pre` does
        // not wrap and this reader says it does. `pre-line` and `break-spaces` both DO wrap, so the
        // one third of the property that is read gives the specified answer for them; what they
        // share with `pre` and `pre-wrap` is the two thirds nobody reads yet — collapsing runs of
        // space and keeping newlines. Registering them adds no new gap and closes a spelling gap.
        Keywords("whitespace", "white-space", new() {
            ["normal"] = "normal", ["nowrap"] = "nowrap", ["pre"] = "pre", ["pre-wrap"] = "pre-wrap",
            ["pre-line"] = "pre-line", ["break-spaces"] = "break-spaces"
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

        // ── Numeric figures ─────────────────────────────────────────────────────────────────
        // ⚠ <b>Every keyword of this property is one OpenType feature, and the blocker was never the
        // property.</b> `TextShaper.ShapeRun` ended `font.Shaper.Shape(buffer, [])` — the array was
        // empty because nothing plumbed one — so `tabular-nums` would have resolved, computed,
        // reached the shaper and been dropped on the floor. The array is threaded now, and the part
        // that would have shipped broken is `ShapingCache`'s key: it was the font and the string, so
        // a paragraph of tabular figures and one of proportional figures would have shared whichever
        // entry was shaped first. See `FontFeatureSet`, which exists to be that key.
        //
        // ⚠ <b>Nine `Static` families rather than one keyword table, because v4 spells them as bare
        // words</b> — `tabular-nums`, not `nums-tabular`. Inventing the second spelling is the
        // failure `bg-conic-<angle>` is recorded under.
        //
        // ⚠ <b>Composed, and the row this section used to carry — "two of them on one element keep
        // the last" — is closed.</b> Each class emitted the whole property, so
        // `class="tabular-nums slashed-zero"` kept whichever declaration the cascade picked second
        // and the other silently did nothing: a *wrong answer* rather than a refusal, which is worse
        // than an unregistered class because there is nothing to look up. The eight keywords write
        // `--tw-*` fragments now and every one of them emits the same assembled
        // `font-variant-numeric` beside it, so any combination of them composes.
        //
        // ⚠ <b>Five fragments and not nine, which is CSS's grammar rather than a compression.</b>
        // CSS Fonts 4 § 6.6 takes at most one keyword from each of three sets — figure, spacing,
        // fraction — plus the two independent flags, so `lining-nums oldstyle-nums` is not something
        // an author can mean. A fragment per class would let both be set and would emit an invalid
        // declaration; a fragment per *set* makes the later class win within its set and leave the
        // rest alone, which is what the property says and what v4 emits.
        //
        // ⚠ <b>`normal-nums` stays a whole declaration, and it has to.</b> `normal` is the one
        // keyword CSS forbids beside any other, so composing it would produce
        // `normal tabular-nums` — invalid — where writing the property outright makes it the
        // override it is meant to be. v4 does the same.
        Static("normal-nums", "font-variant-numeric", "normal");
        NumericFigure("ordinal", UtilityComposition.Ordinal, "ordinal");
        NumericFigure("slashed-zero", UtilityComposition.SlashedZero, "slashed-zero");
        NumericFigure("lining-nums", UtilityComposition.NumericFigure, "lining-nums");
        NumericFigure("oldstyle-nums", UtilityComposition.NumericFigure, "oldstyle-nums");
        NumericFigure("proportional-nums", UtilityComposition.NumericSpacing, "proportional-nums");
        NumericFigure("tabular-nums", UtilityComposition.NumericSpacing, "tabular-nums");
        NumericFigure("diagonal-fractions", UtilityComposition.NumericFraction, "diagonal-fractions");
        NumericFigure("stacked-fractions", UtilityComposition.NumericFraction, "stacked-fractions");

        // ⚠ <b>`font-features-*` is registered, and the blocker it was held behind was the
        // <i>class name</i> rather than anything about the property.</b> `font-feature-settings` has
        // been read end to end since the shaper learnt to take a set — `UiDocument.ResolveText`
        // parses the list, `TextShaper` hands it to HarfBuzz, `ShapingCache` is keyed on it — and it
        // has always been reachable through the arbitrary-property hatch,
        // `[font-feature-settings:"tnum"_1]`. What was missing was the family's *own* spelling.
        //
        // ⚠ <b>Arbitrary-only, which is why it needs a probe of its own and why it could not simply
        // be left out.</b> v4 has no `font-features-tnum`, so the family enumerates nothing into
        // `UtilityFamilies.Surface` — and a family with no surface is one
        // `UtilityConsumptionGateTests` never meets: it would pass vacuously, for ever, while the
        // parity ledger's emission column stayed empty and the row read `absent`. `ValueKind`'s
        // `FontFeatures` carries the probe.
        //
        // ⚠ <b>And every value of this property that does anything contains quotes, by CSS's own
        // grammar</b>, which is the one part that touched real code: `UtilityGenerator.Escape`
        // already backslashes them and `SelectorCompiler` already unescapes them, so
        // `.font-features-\[\"onum\"_1\]` matches — measured in
        // `ArbitraryPropertyTests`, because "it should" is exactly the reasoning that left this
        // family unregistered.
        Register(new Family("font-features", ValueKind.FontFeatures, ["font-feature-settings"]));

        // ── Wrapping ────────────────────────────────────────────────────────────────────────
        // ⚠ <b>`overflow-wrap` and `word-break` are two properties and two families, and the two are
        // not interchangeable however similar the class names look.</b> `UiDocument.WrapModeOf` reads
        // `overflow-wrap` and maps `anywhere` and `break-word` onto `TextWrapMode.Anywhere`, which
        // `LineWrapper` applies at a *grapheme* boundary when one unbreakable run is wider than the
        // whole line. That is what CSS Text 3 § 5.5 says both keywords mean, and it is a decision the
        // line filler takes only when nothing else fits. `word-break` is read separately by
        // `UiDocument.WordBreakOf` and changes the opportunity list itself — see `break-all` below.
        // Keeping them apart is what lets `break-keep` and `wrap-anywhere` be written together and
        // both mean something, which one merged mode could not have expressed.
        //
        // ⚠ <b>`anywhere` and `break-word` ARE distinguished, and this comment said for months that
        // they were not.</b> CSS Sizing § 5.2 separates them only by their min-content contribution:
        // `anywhere` lets the intrinsic minimum shrink to one grapheme and `break-word` does not.
        // #682 is where that landed, as `TextWrapMode.BreakWord` beside `Anywhere` — the two are the
        // same break at every width a box can be seen at and differ in a room of nothing, which is
        // exactly how `LayoutTree` asks a box for its min-content size.
        Keywords("wrap", "overflow-wrap", new() {
            ["anywhere"] = "anywhere", ["break-word"] = "break-word", ["normal"] = "normal"
        });

        // v3's spelling of `wrap-break-word`, which v4 keeps. The same declaration under the name
        // people have in their fingers, exactly as `start-*` is kept beside `inset-s-*`.
        Static("break-words", "overflow-wrap", "break-word");

        // ⚠ <b>`word-break`, and it is not `overflow-wrap` under another name — which is the mistake
        // that would have closed this cheaply and wrongly.</b> `overflow-wrap` is consulted in
        // exactly one branch of `LineWrapper`, "nothing fits: one unbreakable run is wider than the
        // whole line", so it can never move a break that had somewhere else to go. `break-all` makes
        // every letter offer one, so a word that *would* have fitted on the next line is split at the
        // end of this one; `break-keep` suppresses the opportunities UAX#14 finds between two CJK
        // characters and between two Hangul syllables. Both are read by `UiDocument.WordBreakOf` and
        // applied by `LineBreaker.Collect` — a different stage from `overflow-wrap`, which is why the
        // two compose rather than competing.
        //
        // ⚠ <b>The pair is registered together and neither would have been registered alone.</b>
        // `break-all` on its own is `wrap-anywhere`'s declaration under a second spelling that lies
        // about what it does; `break-keep` on its own is a property with one keyword, whose only
        // opt-out would be to delete the class. See `WordBreakMode` for what each does to the
        // opportunity list.
        Static("break-all", "word-break", "break-all");

        // v4 spells `word-break: keep-all` as `break-keep`, not `break-keep-all`. Tailwind's name,
        // not this project's — the failure `bg-conic-<angle>` is recorded under.
        Static("break-keep", "word-break", "keep-all");

        // ⚠ <b>Both of Tailwind's declarations, and the second one arrived with its reader.</b> v4
        // emits `overflow-wrap: normal; word-break: normal`, and this used to emit the first alone,
        // because `word-break` was a property nothing read and the second half would have been an
        // inert entry in the gate's ledger. `WordBreakOf` reads it now, `word-break` is in
        // `InheritedProperties`, and so the half that was missing is exactly the opt-out a child
        // needs from a `break-all` on its container — the same argument `text-clip` and `wrap-normal`
        // each earn their place with, and the row's value gap closes with it.
        Register(new Family(
            "break-normal",
            ValueKind.Static,
            ["overflow-wrap"],
            new Dictionary<string, string>(StringComparer.Ordinal) { [string.Empty] = "overflow-wrap:normal" },
            Alongside: [new UtilityDeclaration("word-break", "normal")]
        ));

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

        // ── Line clamp ──────────────────────────────────────────────────────────────────────
        // ⚠ <b>One declaration where Tailwind emits four, and the three that are missing are three
        // separate decisions rather than one shortfall.</b>
        //
        // `display: -webkit-box` and `-webkit-box-orient: vertical` are dropped under
        // `-webkit-backdrop-filter`'s rule: a prefixed name no engine here can read is a line in
        // every generated sheet that nothing will ever look at. ⚠ And the sizing that called them
        // "a box model Vixen does not have" was measuring the wrong thing — the 2009 box *is*
        // expressible, it is a flex column, and mapping it that way would be actively wrong: it
        // would make a text element a flex container, and a text element here is a leaf that
        // measures itself. In a browser the pair is a marker Chrome special-cases; here
        // `UiDocument.LineClampOf` reads the clamp directly, so the marker has nothing to mark.
        //
        // `overflow: hidden` is dropped for a different reason: a browser lays out every line and
        // *hides* the ones past the clamp, so it needs the clip to do the hiding. `UiElement.Block`
        // drops them, so a clamped block genuinely has N lines and there is nothing left to clip.
        //
        // ⚠ <b>Two registrations for one family, and the order is load-bearing.</b> `Register` keeps
        // the first family under a name and merges a later one's keywords into it, so the numeric
        // kind has to be registered first for `line-clamp-none` to be a keyword rather than a value
        // that fails to parse — the same arrangement `decoration` uses for its three properties.
        Number("line-clamp", "-webkit-line-clamp");

        Keywords("line-clamp", "-webkit-line-clamp", new() { ["none"] = "none" });

        // ── Text transform ──────────────────────────────────────────────────────────────────
        // ⚠ <b>Four bare words for one property, and the fourth is spelled `normal-case` rather than
        // `case-normal`.</b> That is v4's name and it is also the only one that does not collide:
        // `case` is not a prefix any other family uses, and inventing one for a single opt-out would
        // put a family in the table whose only member is a value already reachable by writing
        // nothing.
        //
        // ⚠ <b>These are read at *shaping* time, which is where the property's cost lives.</b>
        // `UiDocument.TextTransformOf` is consulted by `UiElement.Block` before a glyph is chosen,
        // because a case mapping changes how wide the text is; and `TransformedText` carries the map
        // back to what the author wrote, because a full Unicode uppercase changes the string's
        // *length* — `straße` becomes `STRASSE` — and every caret index in the tree is an index into
        // the element's own text. The four classes were held back until that map existed, for
        // exactly the reason `tab-*` was: a text feature that misplaces a caret is worse than
        // an absent one.
        Static("uppercase", "text-transform", "uppercase");
        Static("lowercase", "text-transform", "lowercase");
        Static("capitalize", "text-transform", "capitalize");
        Static("normal-case", "text-transform", "none");

        // ── Tab size ────────────────────────────────────────────────────────────────────────
        // ⚠ <b>A bare count, which is why this is a `Number` family and not a length one.</b> v4
        // spells `tab-1`, `tab-2`, `tab-4`, `tab-8` and an arbitrary count. CSS Text 3 § 6.1 also
        // allows a `<length>`, and `UiDocument.TabSizeOf` refuses that form rather than resolving
        // it — a length here takes relative units, so it would have to be computed and inherited
        // beside `line-height` instead of living in `InheritedProperties`, for a form no class in
        // this table can even spell.
        //
        // ⚠ <b>The reader is a layout seam rather than a property lookup, which is why this row sat
        // `absent` while its four siblings above shipped.</b> A tab's advance is the distance to the
        // next stop, so it is a fact about where the run *sits* — while `TextLine.Place`,
        // `CaretOffset` and `Width`, and every width in `LineWrapper`, are prefix sums over advances
        // that are facts about the character. `TextRun.IsTab` and `TextLine.WidthOf` are what
        // separate the two; before they existed a `tab-*` that resolved would have broken the
        // paragraph in one place, drawn it in another, and put the caret a stop out.
        Number("tab", "tab-size");

        // ── Hyphens ─────────────────────────────────────────────────────────────────────────
        // ⚠ <b>Two of Tailwind's three, and the third is left unregistered on purpose.</b>
        // `hyphens-auto` needs a per-language Liang pattern set. Registering it would put a class in
        // the table that resolves, computes a value and hyphenates nothing, which is the exact state
        // `UtilityConsumptionGateTests` exists to keep out. The root stays `partial` with the reason
        // named, which is the honest state rather than the flattering one.
        //
        // ⚠ <b>Half of the reason this comment used to give has expired, and it expired without
        // anything noticing — which is the finding worth more than the sentence.</b> It said the
        // refusal also rested on there being no language to pick a pattern set with, `TextShaper`
        // leaving HarfBuzz's language unset. `UiElement.ResolvedLanguage` carries a BCP-47 tag that
        // inherits by tree and reaches `TextShaper.ShapeRun`, so that half is false. It went stale
        // in prose because `RefusalExpiry` could not reach it: this root is `partial`, and until now
        // only an `expires-when-read` clause was allowed on a `partial` row. The remaining half now
        // carries `[expires-on Vixen.Ui.Text.HyphenMode.Auto]` in the ledger, so the arrival of the
        // pattern set reddens a test instead of leaving a paragraph standing.
        //
        // ⚠ <b>`hyphens-manual` is the initial value, and it is registered anyway.</b> Normally a
        // class whose only effect is "write nothing" earns no place — `normal-case` is the exception
        // above and argues its own case. This one is different: `hyphens` inherits, so `manual` is
        // the only way to opt a child back in under an ancestor's `hyphens-none`, and there is no
        // other spelling for it.
        //
        // ⚠ <b>And `manual` was already half-implemented, which made this a defect and not a
        // gap.</b> `LineBreaker` has always broken at U+00AD; the hyphen was never drawn, because
        // the character is `Default_Ignorable` and the shaper deletes it. `UiElement.Hyphenated`
        // supplies the visible half.
        Static("hyphens-none", "hyphens", "none");
        Static("hyphens-manual", "hyphens", "manual");

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
        // ⚠ <b>`decoration-dotted` and `-dashed` were absent under the same measurement
        // `divide-solid` was, and that measurement has changed: there is a dash pattern in
        // `Vixen.Ui` now.</b> `Dashes` distributes the marks and `DrawListBuilder.EmitDecoration`
        // emits a rectangle each — which a bar can do and a border's ring cannot, because a bar is
        // an axis-aligned rectangle with no corner radius, so breaking it up is breaking up a
        // length. Four of CSS's five are drawn.
        //
        // ⚠ <b>`-wavy` stays absent, and the dash pattern does not touch its reason.</b> A wave is a
        // stroked path where every other decoration is a rectangle: it needs the tessellator, a
        // thickness that is a stroke width rather than a height, and an amplitude and a period CSS
        // does not state. It would resolve cleanly, compute a value and paint a straight line, which
        // is the inert family `UtilityConsumptionGateTests` exists to keep out.
        Color("decoration", "text-decoration-color");

        Keywords("decoration", "text-decoration-thickness", new() {
            ["0"] = "0px", ["1"] = "1px", ["2"] = "2px", ["4"] = "4px", ["8"] = "8px",
            ["auto"] = "auto", ["from-font"] = "from-font"
        });

        Keywords("decoration", "text-decoration-style", new() {
            ["solid"] = "solid", ["double"] = "double", ["dashed"] = "dashed", ["dotted"] = "dotted"
        });

        // ── Colours ─────────────────────────────────────────────────────────────────────────
        Color("bg", "background-color");
        Color("fill", "fill");
        Color("stroke", "stroke");

        // ⚠ <b>`none` is a paint and not a colour, and the reader had to learn the difference
        // first.</b> This is the trap `docs/plan/43` § F8 files under refusal shape 3 and the ledger
        // recorded against `stroke-none` for weeks: `fill` and `stroke` are both read, so the
        // consumption gate would have scored a registration green — while `Icon.Resolve` asked
        // `ColorOf` for the slot, got `null` because `none` is not a colour, and fell through to the
        // foreground. The class would have resolved, cascaded, and painted the very glyph it was
        // written to hide. `Icon.IsNone` is the reading that closes it, at both draw paths.
        Keywords("fill", "fill", new() { ["none"] = "none" });
        Keywords("stroke", "stroke", new() { ["none"] = "none" });

        // ⚠ <b>The one interactivity colour with a reader, and finding the reader is what decided
        // it.</b> `TextField` and `CodeEditor` have drawn their insertion point off Vixen's own
        // `--caret-color` token since they were written; the standard spelling is now asked before
        // it, so `caret-accent` on a field is the field's answer and the palette stays the
        // document's. Its sibling `accent-*` is absent for the opposite reason — see the refusals at
        // the foot of this table.
        Color("caret", "caret-color");

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
            Alongside: [new UtilityDeclaration("box-shadow", UtilityComposition.Shadows())]
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
        //
        // ⚠ <b>The angle form is registered <i>first</i> and the keyword table second, and that order
        // is the whole reason both spellings work.</b> `Register` keeps the first family under a name
        // and merges nothing but a later keyword table into it — so a `Keywords` call first would
        // make the family's `ValueKind` `Keyword`, and `bg-linear-45` would fall out of the table and
        // be reported as an unrecognised typo. The same shape as `StaticOrSize`, one section up.
        Register(new Family("bg-linear", ValueKind.Angle, ["background-image"], Template: Gradient("linear", "{0}")));

        Keywords("bg-linear", "background-image", new() {
            ["to-t"] = Gradient("linear", "to top"), ["to-tr"] = Gradient("linear", "to top right"),
            ["to-r"] = Gradient("linear", "to right"), ["to-br"] = Gradient("linear", "to bottom right"),
            ["to-b"] = Gradient("linear", "to bottom"), ["to-bl"] = Gradient("linear", "to bottom left"),
            ["to-l"] = Gradient("linear", "to left"), ["to-tl"] = Gradient("linear", "to top left")
        });

        // ⚠ <b>The two round shapes take no geometry when they are written bare, and that is
        // Tailwind's own default rather than a simplification.</b> `bg-radial` is
        // `radial-gradient(in oklab, …)` — no `at`, no ending shape — because CSS's defaults are a
        // centred farthest-corner ellipse, and bare `bg-conic` is the same story with a sweep from
        // twelve o'clock.
        Static("bg-radial", "background-image", Gradient("radial", string.Empty));

        // ⚠ <b>`bg-conic-<angle>` was owed by the *utility* table and by nothing below it.</b>
        // `GradientReader.ReadPrelude` has read `from <angle>` since conic gradients landed, and the
        // box shader recovers it from the axis lane with `atan2(x, -y)` — so this line is the whole
        // of the feature, and writing it earlier was blocked only on a value kind that admits zero.
        // The bare `bg-conic` keeps its own registration below, which is what `Register` merges in.
        Register(new Family("bg-conic", ValueKind.Angle, ["background-image"], Template: Gradient("conic", "from {0}")));
        Static("bg-conic", "background-image", Gradient("conic", string.Empty));

        // ⚠ <b>`bg-none` is the opt-out and the eight `bg-gradient-to-*` are v3's spelling, and both
        // hang off the bare `bg` root rather than off `bg-linear`.</b> That is Tailwind's own
        // arrangement — v4 keeps `bg-gradient-to-*` in `compat/legacy-utilities.ts`, aliased to
        // exactly what `bg-linear-to-*` emits — and it is the same argument `start-*` beside
        // `inset-s-*` and `break-words` beside `wrap-break-word` are kept under: the declaration
        // people already have in their fingers, under the name they have it in.
        //
        // ⚠ <b>`bg-none` earns its place the way `text-clip` and `filter-none` do.</b> It is CSS's
        // initial value, so it says nothing on an element that has no gradient — but
        // `background-image` is a property a `@apply`-ed component or a theme rule can have set, and
        // `GradientReader` reads `none` as `GradientRefusal.NotAGradient` and paints no layer. There
        // is no other way to write "whatever gradient you gave me, not here".
        // ⚠ <b>The two placement roots, and they are only observable together — which is why they
        // land in one commit with the engine that reads them.</b> `background-position` moves the tile
        // a `background-size` made smaller than the box, and `background-repeat` decides what happens
        // outside it; with the tile equal to the border box all three are the same picture, which is
        // the measurement the ledger carried as `refused, measured` for the repeat root.
        //
        // ⚠ <b>`bg-auto`, `bg-cover` and `bg-contain` are deliberately NOT registered, and the reason
        // is CSS rather than a missing reader.</b> Backgrounds 3 § 3.9 resolves all three against the
        // image's intrinsic dimensions and ratio — a gradient has neither, so `auto` is 100%,
        // `contain` is the positioning area and `cover` is the positioning area. For the only kind of
        // `background-image` this engine paints the three are one picture *and the same picture as the
        // default*, which is three classes that differ from each other and from nothing else in name
        // only. That is the inert family `UtilityConsumptionGateTests` exists to keep out, and it
        // would be invisible to that gate: `background-size` is read. Recorded on the row instead.
        Register(new Family("bg-size", ValueKind.Placement, ["background-size"]));
        Register(new Family("bg-position", ValueKind.Placement, ["background-position"]));

        // ⚠ <b>Four of v4's six, and the two that are absent are absent for `divide-solid`'s reason
        // rather than for a lane.</b> `round` rescales the tile so a whole number of them fits and
        // `space` distributes the remainder as gaps — both are a *second* size computed from the box,
        // not a flag, and `space`'s gaps are not a period a `mod` can express. `DrawListBuilder.Repeat`
        // drops a declaration naming either, which leaves CSS's initial `repeat`; registering them
        // would be two classes that resolve, compute and tile exactly like `bg-repeat`.
        Keywords("bg", "background-repeat", new() {
            ["repeat"] = "repeat",
            ["no-repeat"] = "no-repeat",
            ["repeat-x"] = "repeat-x",
            ["repeat-y"] = "repeat-y"
        });

        Keywords("bg", "background-image", new() {
            ["none"] = "none",
            ["gradient-to-t"] = Gradient("linear", "to top"),
            ["gradient-to-tr"] = Gradient("linear", "to top right"),
            ["gradient-to-r"] = Gradient("linear", "to right"),
            ["gradient-to-br"] = Gradient("linear", "to bottom right"),
            ["gradient-to-b"] = Gradient("linear", "to bottom"),
            ["gradient-to-bl"] = Gradient("linear", "to bottom left"),
            ["gradient-to-l"] = Gradient("linear", "to left"),
            ["gradient-to-tl"] = Gradient("linear", "to top left")
        });

        // ── Borders ─────────────────────────────────────────────────────────────────────────
        // ⚠ `border-2` is two *pixels* where `p-2` is two spacing steps, which is Tailwind's choice
        // and the right one. A border is a hairline or it is not; scaling it with the spacing base
        // would mean a theme with a larger base silently thickened every rule in the editor.
        BorderEdge("border", ["border-width"], ["border-color"]);

        // ⚠ <b>All six style keywords, and they were all six absent until `DrawListBuilder` grew a
        // reader for `border-style`.</b> Doc 43 § A3: nothing interned the four style longhands, so
        // the property resolved into them and moved no channel in any scene — which is why this
        // family, `divide-<style>`, `decoration-*` and `outline-*` all read `partial` at once and
        // why they close together.
        //
        // ⚠ <b>`border-none` is the one with a consequence, because this engine paints from the
        // width.</b> A browser needs `border-style` to be anything but `none` before a width paints,
        // and Vixen has always painted from the width alone — so before this reader existed,
        // `border-none` beside a `border-2` drew the ring anyway and the class was a lie. It is now
        // the one keyword that takes a border *away*, which is what everybody writing it means.
        //
        // ⚠ <b>`groove`, `ridge`, `inset` and `outset` are not here, and Tailwind does not have them
        // either.</b> All four are two-tone — CSS derives a lighter and a darker shade of the border
        // colour and gives one to each pair of edges — which is a second colour the border record
        // does not carry. See `Vixen.Ui.StrokeStyle`.
        Keywords("border", "border-style", new() {
            ["solid"] = "solid", ["dashed"] = "dashed", ["dotted"] = "dotted",
            ["double"] = "double", ["none"] = "none", ["hidden"] = "hidden"
        });
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
        // ⚠ No `border-block-start-style`. v4 emits one alongside the width because a browser needs
        // a style before a width paints; here an absent style reads as `solid` — see
        // `Vixen.Ui.StrokeStyle` — so the extra longhand would be `solid` written on every edge
        // utility, which is the value the reader already assumes. That is the same argument
        // `outline` makes below: `Family.Alongside` is appended on *every* resolution of a family,
        // so it would ride `border-bs-accent` too and out-specify a `border-dashed` beside it.
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
        // custom property. The zero is what this does not follow: emitting the leading edge would
        // buy nothing and cost something real — it would out-specify a child's own `border-s-2` and
        // silently erase it — so the family writes the one edge it means. Same argument for
        // `space-x-*`, which is why it writes no leading margin either.
        //
        // ⚠ <b>Both reasons `divide-x-reverse` was refused under have expired, and it stays
        // unregistered on this one instead.</b> `StyleValueParser` folds a `calc()` now, and the
        // flag is not a custom property nobody reads: `ReverseFlagTests` measures the whole v4 shape
        // end to end — a flag one class writes is read by another class's declaration, it inherits
        // to the descendants a `> :not(:last-child)` rule matches, and both arms of the arithmetic
        // fold at both values. So the machinery works and the one-edge decision above is the whole
        // of what is left. ⚠ Writing that leading edge as `calc(w * var(--tw-divide-x-reverse, 0))`
        // rather than as a literal `0` does not dodge it: it is the declaration that out-specifies a
        // child's utility, not the value it computes to.
        //
        // ⚠ <b>No colour longhands, and that is what `ColorProperties: null` says.</b> `divide-x-2`
        // is a width and `divide-x-accent` is not a class Tailwind has; the colour is written
        // `divide-accent`, on the family below, and reaches all four physical `border-color`
        // longhands through ExCSS's expansion. `TryBorderEdge` reports the unregistered spelling as
        // unknown rather than inventing an edge colour for it.
        //
        // ⚠ <b>`divide-solid` and the rest of the style keywords used to be deliberately absent and
        // are registered below.</b> The measurement they were absent under — `border-style` emitted
        // by nothing here and read by nothing either — was true and is not any more: doc 43 § A3 gave
        // `DrawListBuilder` a reader for the four style longhands and `Vixen.Ui` a dash pattern.
        // ── The outline ─────────────────────────────────────────────────────────────────────
        //
        // ⚠ <b>An outline is not a thin border and it is not the ring either.</b> `ring-*` is a
        // `box-shadow` — v4's own reading, see `UtilityComposition.Ring` — and this is CSS's second
        // ring property: drawn outside the border box, taking no space in the layout, following the
        // border radius outward. `DrawListBuilder.EmitOutline` draws it as a `Border` command on a
        // rectangle grown by `outline-offset + outline-width`, which is the same shader and the same
        // geometry the border already used.
        //
        // ⚠ <b>`BorderEdge` for `border`'s reason: one prefix, told apart by the value's shape.</b>
        // `outline-2` is a width and `outline-accent` is a colour. The bare `outline` is one pixel,
        // which is v4.
        BorderEdge("outline", ["outline-width"], ["outline-color"]);

        // ⚠ <b>No `outline-style: solid` alongside the width, where v4 emits one — and this is the
        // one place the table deliberately diverges from Tailwind rather than following it.</b> v4
        // writes `outline-style: var(--tw-outline-style)` on every width class because a browser
        // defaults `outline-style` to `none` and would paint nothing. This engine's border model has
        // no style at all — `border-width` alone paints, because an absent `border-style` reads as
        // `solid` rather than as CSS's `none`, which `Vixen.Ui.StrokeStyle` argues — and
        // `EmitOutline` is built to match it: a width is what makes a ring. Emitting the extra
        // longhand would buy nothing and cost fidelity in the other direction, because
        // `Family.Alongside` is appended on *every* resolution of a family, so
        // `outline-accent` would have carried a `solid` v4 does not emit for it and painted a ring
        // nobody asked for.
        //
        // ⚠ <b>Five of five, and this used to be two.</b> `outline-dashed`, `-dotted` and `-double`
        // were absent under the measurement `divide-solid` and `decoration-dotted` were: there was no
        // dash pattern in `Vixen.Ui` and a doubled ring is two rings. Both exist now — `Dashes`
        // distributes the marks, `Rings` walks the ring's centre line, and `EmitOutline` draws a
        // doubled ring as two `Border` commands a third as thick.
        Keywords("outline", "outline-style", new() {
            ["solid"] = "solid", ["none"] = "none", ["dashed"] = "dashed",
            ["dotted"] = "dotted", ["double"] = "double"
        });

        // ⚠ <b>`outline-hidden` is v4's spelling and here it is `outline-none` exactly, which is a
        // loss worth naming rather than papering over.</b> In v4 the class removes the visible ring
        // *and* restores a transparent two-pixel one inside `@media (forced-colors: active)`, so a
        // Windows high-contrast user keeps a focus indicator the sighted default hid. ⚠ `MediaQuery`
        // evaluates `forced-colors` now and `IPlatform.Accessibility` feeds it — that half of this
        // remark is out of date — but this engine still has no forced-colors *mode* for the
        // transparent ring to be substituted against, so the second half has nowhere to go and the
        // class collapses to the first. It is
        // registered anyway because the visible half is real, is read, and is the idiom every v4
        // sheet writes for "take the focus ring off" — refusing it would leave the common case
        // spelled only by the v3 name.
        Static("outline-hidden", "outline-style", "none");

        // ⚠ <b>A keyword table and not `Spacing`, because these are pixels and not spacing steps —
        // `border-*`'s argument one property over.</b> An outline is a hairline that happens to sit
        // a hairline away; scaling its offset with the theme's spacing base would mean a larger base
        // silently pushed every focus ring off its control.
        Keywords("outline-offset", "outline-offset", new() {
            ["0"] = "0px", ["1"] = "1px", ["2"] = "2px", ["4"] = "4px", ["8"] = "8px"
        });

        Between("divide-x", ValueKind.BorderEdge, ["border-inline-end-width"]);
        Between("divide-y", ValueKind.BorderEdge, ["border-bottom-width"]);
        Between("divide", ValueKind.Color, ["border-color"]);

        // ⚠ <b>The five style keywords, scoped to the same children the widths are.</b> They were
        // absent under the measurement above and are registered for the same reason it changed: a
        // divider is a `border-bottom-width` or a `border-inline-end-width` on every child but the
        // last, so `divide-dashed` lands on `DrawListBuilder`'s per-edge *band* path rather than on
        // its ring — which is why that path had to answer the broken styles too and could not leave
        // them to the stroked centre line. `Register` merges these into the family above and keeps
        // its `> :not(:last-child)` scope, so the style reaches exactly the edges the width did.
        Keywords("divide", "border-style", new() {
            ["solid"] = "solid", ["dashed"] = "dashed", ["dotted"] = "dotted",
            ["double"] = "double", ["none"] = "none"
        });

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

        // ⚠ <b>The six logical radii, and they are a reader rather than a rename — which is the
        // opposite of what the block-axis families above are.</b> `inset-bs-*`, `space-y-*` and
        // `border-bs-*` all emit the *physical* longhand, because the block axis is top-to-bottom in
        // every configuration this engine can be in and there is no writing mode to flip it. A
        // radius corner is named on both axes at once, and the inline half is one this engine really
        // does mirror: `rounded-ss` is the top-left corner under `direction: ltr` and the top-right
        // under `rtl`. So `border-top-left-radius` would have been right exactly half the time, and
        // these four longhands are interned and resolved against `direction` in
        // `DrawListBuilder.Corners` instead — the same property `StyleResolution.LeftEdge` resolves
        // the logical insets with, so `rounded-ss-lg` rounds the same corner `ps-2` pads.
        //
        // ⚠ <b>A side here is two corners on the inline axis, where `rounded-t`'s two are on the
        // block axis.</b> `rounded-s` is the whole start side — the top-start and bottom-start
        // corners — which is `border-start-start-radius` and `border-end-start-radius`. The leading
        // half of each name is the block axis and the trailing half the inline one, so it is the
        // *second* half that `-s` and `-e` are naming.
        Radius("rounded-ss", "border-start-start-radius");
        Radius("rounded-se", "border-start-end-radius");
        Radius("rounded-ee", "border-end-end-radius");
        Radius("rounded-es", "border-end-start-radius");

        Radius("rounded-s", "border-start-start-radius", "border-end-start-radius");
        Radius("rounded-e", "border-start-end-radius", "border-end-end-radius");

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
            ValueKind.Blur,
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

        // ⚠ <b>The ninth family in the block and the only one that sets no fragment, which is what
        // makes it the odd one and also what makes it correct.</b> `filter-none` is not "every
        // function at its identity" — that is what an element carrying none of the eight already
        // gets, and `DrawListBuilder` opens no group for it. It is the keyword `none`, which
        // `DrawListBuilder.Filter` reads as "not a list" and returns `default` for, so the element
        // draws unfiltered whatever the eight fragments the cascade handed it say. Emitting the
        // assembled eight here with every fragment forced to its identity would be a *different*
        // declaration with the same picture and a `var()` chain nobody can read.
        //
        // ⚠ <b>Which of `filter-none` and `blur-2` wins on one element is the cascade's answer and
        // not this table's, and it is worth knowing that before someone reports it as a bug.</b> Both
        // rules set `filter`, both have one class's specificity, so the later rule in the generated
        // sheet wins — which is the order `UtilityGenerator` emits families in, not the order the
        // classes were written in. Tailwind v4 has the identical ambiguity for the identical reason.
        // The class earns its place on elements that inherit or `@apply` a filter and want it off,
        // which is the only case where there is nothing else to write.
        Keywords("filter", "filter", new Dictionary<string, string>(StringComparer.Ordinal) { ["none"] = "none" });

        // ⚠ <b>The ninth function, and the one that took a compositor change rather than a
        // constant.</b> The seven above are a 3×4 matrix folded into the composite draw the group was
        // making anyway — no surface, no pass. A drop shadow is a Gaussian over the group's
        // <i>alpha</i>, offset, tinted and composited under it: a second viewport-sized surface, two
        // more render passes and a second quad, on both executors. That is why it did not land beside
        // them and why `UtilityComposition.Filter` left a hole where it now goes.
        //
        // ⚠ <b>A token scale rather than a spacing multiple, which is <c>shadow-*</c>'s arrangement
        // and not <c>blur-*</c>'s.</b> `blur-2` is a length and means one thing; a drop shadow is an
        // offset, a blur and an alpha chosen together to read as one height above the surface, and a
        // scale that let them be picked apart would invite the combinations that do not. See
        // `ThemeTokens.DropShadow`, and `--drop-shadow-*` in `vixen.default.vcss`.
        //
        // ⚠ <b>`drop-shadow-none` is here rather than in the theme</b>, for the reason `shadow-none`
        // is: turning one off must not depend on somebody having remembered to define it. It sets the
        // fragment to the same transparent shadow the initial value holds, which
        // `DrawListBuilder.Settle` drops before it costs a surface — so the class is a real override
        // in the cascade and costs the frame nothing.
        Register(new Family(
            "drop-shadow",
            ValueKind.DropShadow,
            [UtilityComposition.DropShadow],
            new Dictionary<string, string>(StringComparer.Ordinal) {
                ["none"] = UtilityComposition.DropShadow + ":0 0 transparent"
            },
            Alongside: [new UtilityDeclaration("filter", UtilityComposition.Filter())]
        ));

        // ⚠ <b>Sixteen keywords rather than the eight the ledger's `classes` column transcribed, and
        // the extra eight are not padding.</b> That column is the original survey's list and is short
        // of v4 on several roots — the four non-separable modes are the ones anybody actually reaches
        // for on a tinted panel, and `hard-light`, `soft-light`, `difference` and `exclusion` are the
        // rest of CSS Compositing 1 § 5.1. Registering fewer would leave `mix-blend-difference`
        // producing no rule at all, which is the failure `blur-md` was.
        //
        // ⚠ <b>`plus-darker` and `plus-lighter` are deliberately absent.</b> They are CSS
        // Compositing 2's *porter-duff* operators rather than § 5.1 blend functions — they change how
        // much of the source lands, not what colour it is — so they do not fit `UiBlend.Apply`'s
        // shape at all, and neither `UiLayer` nor either executor has a second composite operator to
        // put them in. A class that resolved to a keyword nothing could act on would measure `inert`
        // and read as a family that half works.
        Keywords(
            "mix-blend",
            "mix-blend-mode",
            new Dictionary<string, string>(StringComparer.Ordinal) {
                ["normal"] = "normal",
                ["multiply"] = "multiply",
                ["screen"] = "screen",
                ["overlay"] = "overlay",
                ["darken"] = "darken",
                ["lighten"] = "lighten",
                ["color-dodge"] = "color-dodge",
                ["color-burn"] = "color-burn",
                ["hard-light"] = "hard-light",
                ["soft-light"] = "soft-light",
                ["difference"] = "difference",
                ["exclusion"] = "exclusion",
                ["hue"] = "hue",
                ["saturation"] = "saturation",
                ["color"] = "color",
                ["luminosity"] = "luminosity"
            }
        );

        // ── The backdrop ────────────────────────────────────────────────────────────────
        //
        // ⚠ <b>Ten roots in the shape of the nine above, and the shape is all they share.</b> These
        // set `backdrop-filter`, which transforms the picture <i>behind</i> the element rather than
        // what it drew — so an element may carry `blur-2` and `backdrop-blur-8` and mean two different
        // pictures, which is why the fragments are a second set and `UtilityComposition.BackdropFilter`
        // is a second assembler rather than nine more slots in the first.
        //
        // ⚠ <b>One declaration alongside and not Tailwind's two.</b> v4 emits
        // `-webkit-backdrop-filter` beside the unprefixed property because Safari needs it; this
        // engine is not a browser, so that copy would be a property emitted into every generated
        // sheet that nothing could ever read — which is precisely what the consumption gate exists to
        // flag, and what `InertProperties.txt` exists to record when it cannot be avoided. Here it
        // can be. The ledger's `css` column still lists both, because that column is about what
        // Tailwind emits.
        //
        // ⚠ <b><c>backdrop-opacity-*</c> is here and <c>backdrop-drop-shadow-*</c> is not.</b> That
        // is Tailwind's set, and it is also this engine's: a shadow of the backdrop is a silhouette
        // composited under a picture that is already behind everything, and `DrawListBuilder.One`
        // refuses `drop-shadow()` inside a `backdrop-filter` for that reason.
        Register(new Family(
            "backdrop-blur",
            ValueKind.Blur,
            [UtilityComposition.BackdropBlur],
            Alongside: BackdropAlongside
        ));

        Backdrop("backdrop-brightness", UtilityComposition.BackdropBrightness);
        Backdrop("backdrop-contrast", UtilityComposition.BackdropContrast);
        Backdrop("backdrop-grayscale", UtilityComposition.BackdropGrayscale, bare: "1");
        Backdrop("backdrop-invert", UtilityComposition.BackdropInvert, bare: "1");
        Backdrop("backdrop-opacity", UtilityComposition.BackdropOpacity);
        Backdrop("backdrop-saturate", UtilityComposition.BackdropSaturate);
        Backdrop("backdrop-sepia", UtilityComposition.BackdropSepia, bare: "1");

        // ⚠ The angle, for `hue-rotate`'s reason exactly: `StyleValueParser` refuses a bare number
        // where an angle belongs, and a family emitting one would produce a declaration the engine
        // drops *whole* — taking every other backdrop function on the element with it.
        Register(new Family(
            "backdrop-hue-rotate",
            ValueKind.CountTemplate,
            [UtilityComposition.BackdropHueRotate],
            Template: "{0}deg",
            Alongside: BackdropAlongside
        ));

        // ⚠ <b><c>backdrop-filter-none</c>, which sets no fragment for the reason <c>filter-none</c>
        // sets none.</b> It is the keyword `none`, which `DrawListBuilder` reads as "not a list" and
        // returns nothing for — so the element composites over an untouched backdrop whatever the nine
        // fragments the cascade handed it say. Assembling the nine at their identities instead would
        // be a different declaration with the same picture and a `var()` chain nobody can read.
        // ⚠ The prefixed copy is set too, so that turning the feature off turns off the copy a browser
        // would have read.
        Keywords(
            "backdrop-filter",
            "backdrop-filter",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["none"] = "none" }
        );

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
        // ⚠ <b>The nine positions, and they set a fragment rather than writing the whole layer</b> —
        // `mask-radial-at-top mask-radial-from-40%` is two classes that have to agree about one
        // `mask-image`, which is what the fragments exist for. Tailwind's own spelling: `at` is part
        // of the class name, not of the value.
        //
        // ⚠ <b>`mask-radial-*`'s other half — the ending sizes — is registered now, and the refusal
        // that stood here was right about the trade and wrong about the blocker.</b> It read "they
        // land when `UiMask` carries a stated pair of radii", and `UiShape.Paint.zw` <i>is</i> a
        // stated pair, honoured as an arbitrary pair by every rasteriser. Nothing was waiting on a
        // lane or on a shader: what was missing was somewhere on `BackgroundGradient` to record
        // which ending was written and the other closed forms in `RampFrame`, both of which #545
        // built. The trade the refusal named still stands and is what made it worth keeping until
        // then — a refused mask layer is *no mask at all* rather than a slightly wrong one, so a
        // family registered against an ending the reader declines deletes the masking it was
        // written to shape.
        //
        // ⚠ <b>`mask-circle` and `mask-ellipse` are registered now, under the `mask` prefix a
        // hundred lines below, and what they waited on was never gradients.</b> The reader has
        // understood `circle` since #545; the obstacle was this table's own shape — the `mask`
        // prefix is already the `mask-repeat` family and `Alongside` belonged to a family rather
        // than to a value, so the two shape values could not carry the mask layer their siblings do
        // while the four repeat values must not. `Family.ValueAlongside` is that difference, and it
        // was worth a second field rather than a second family: `Register` keeps the first family
        // under a name and discards a second silently.
        Keywords("mask-radial", UtilityComposition.MaskRadialSize, new() {
            ["closest-side"] = "closest-side",
            ["closest-corner"] = "closest-corner",
            ["farthest-side"] = "farthest-side",
            ["farthest-corner"] = "farthest-corner"
        }, [.. MaskAlongside(UtilityComposition.MaskRadial, Radial)]);

        Keywords("mask-radial-at", UtilityComposition.MaskRadialPosition, new() {
            ["top"] = "top", ["top-left"] = "top left", ["top-right"] = "top right",
            ["left"] = "left", ["center"] = "center", ["right"] = "right",
            ["bottom"] = "bottom", ["bottom-left"] = "bottom left", ["bottom-right"] = "bottom right"
        }, [.. MaskAlongside(UtilityComposition.MaskRadial, Radial)]);

        // ⚠ <b>`mask-mode`, and it is the one property of the six that costs no lane and no branch.</b>
        // CSS Masking 1 § 7.2 makes a luminance mask `luminance(rgb) × a` — a scalar per stop — so
        // `DrawListBuilder.MaskAlphas` computes it from colours it already has and writes it into the
        // same three floats the alpha reading fills. `match-source` is `alpha` for every image that is
        // not an SVG `<mask>`, which is every image here; it earns its place as the opt-out from a
        // `mask-luminance` a component set, the same argument `text-clip` and `filter-none` make.
        Static("mask-alpha", "mask-mode", "alpha");
        Static("mask-luminance", "mask-mode", "luminance");
        Static("mask-match", "mask-mode", "match-source");

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
        // ⚠ <b>The mask's placement trio, and the same three shapes the background's has — because CSS
        // gives them one grammar apiece.</b> Masking 1 § 4 defers to Backgrounds 3 for
        // `mask-position`, `mask-size` and `mask-repeat`, so these are `ValueKind.Placement` and a
        // keyword table for exactly the reasons written up beside `bg-size-*` two hundred lines up:
        // v4 gives the first two no named scale, and `round`/`space` are a second size computed from
        // the box rather than a flag.
        //
        // ⚠ <b>`mask-repeat-*` is not one of the nine roots doc 43 lists as owed, and it is here
        // anyway, because `mask-size-*` is wrong without it.</b> CSS's initial `mask-repeat` is
        // `repeat`; a mask tile smaller than the box that did not tile would be `no-repeat` under
        // another name, and every `mask-size-*` in the world would draw one tile with its last stop
        // smeared across the rest of the element.
        Register(new Family("mask-size", ValueKind.Placement, ["mask-size"]));
        Register(new Family("mask-position", ValueKind.Placement, ["mask-position"]));

        // ⚠ <b>One prefix, two unrelated properties, and that is Tailwind's spelling rather than an
        // accident of this table.</b> `mask-repeat` and its three siblings are `mask-repeat`;
        // `mask-circle` and `mask-ellipse` are the radial ending's *shape*. `Register` keeps the
        // first family under a name and discards a second silently, so these cannot be two
        // registrations — and the two halves differ in what they must emit *alongside* the value,
        // which is why `Family.ValueAlongside` had to exist before this line could be written. A
        // shape carries the three mask-layer declarations every other `mask-radial-*` carries; a
        // repeat value must not, or `mask-no-repeat` on its own would install a radial mask.
        Register(new Family(
            "mask",
            ValueKind.Keyword,
            ["mask-repeat"],
            new Dictionary<string, string>(StringComparer.Ordinal) {
                ["repeat"] = "mask-repeat:repeat",
                ["no-repeat"] = "mask-repeat:no-repeat",
                ["repeat-x"] = "mask-repeat:repeat-x",
                ["repeat-y"] = "mask-repeat:repeat-y",
                ["circle"] = $"{UtilityComposition.MaskRadialShape}:circle",
                ["ellipse"] = $"{UtilityComposition.MaskRadialShape}:ellipse"
            },
            ValueAlongside: new Dictionary<string, UtilityDeclaration[]>(StringComparer.Ordinal) {
                ["circle"] = MaskAlongside(UtilityComposition.MaskRadial, Radial),
                ["ellipse"] = MaskAlongside(UtilityComposition.MaskRadial, Radial)
            }
        ));

        Static("mask-none", "mask-image", "none");

        // A token names a whole declaration rather than a number, because a shadow is a designed
        // thing: its offset, blur and alpha are chosen together to read as one height above the
        // surface. `shadow-none` is here rather than in the theme so that turning one off never
        // depends on somebody having remembered to define it.
        //
        // ⚠ <b>Composed, and the fragment is the whole shadow.</b> This family wrote `box-shadow`
        // directly until `Rikarin/Vixen#279` item 4, and so does `ring-*` — two families, one
        // longhand, so `shadow-lg ring-2` on one element resolved to whichever rule the cascade
        // picked and the other class silently did not apply. Nothing about *this* family needed a
        // fragment; sharing the property with the ring did. See `UtilityComposition.Shadows`.
        //
        // ⚠ <b>`shadow-none` is now a transparent shadow rather than the `none` keyword</b>, and the
        // change is not cosmetic: `none` substituted into the middle of a comma list is not an empty
        // item, it is a keyword `EmitShadow` refuses the whole declaration over — so the old spelling
        // would have made `shadow-none ring-2` paint no ring either.
        Register(new Family(
            "shadow",
            ValueKind.Shadow,
            [UtilityComposition.Shadow],
            new Dictionary<string, string>(StringComparer.Ordinal) {
                ["none"] = UtilityComposition.Shadow + ":0 0 transparent"
            },
            Alongside: [new UtilityDeclaration("box-shadow", UtilityComposition.Shadows())]
        ));

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
        // — and the engine reads `translate` in `UiDocument.Accumulate`.
        //
        // ⚠ <b>And `scale` and `rotate` are read now too, which retired the refusal that used to be
        // the rest of this remark.</b> Both are composed into a `UiTransform` in the same accumulation
        // pass, `DrawListBuilder` opens a composited group for either, and the matrix is spent on the
        // composite quad's four vertices — so a `DrawCommand` is still an axis-aligned rectangle and
        // a clip is still a rectangle, which is what the refusal was protecting. See the closed block
        // in `InertProperties.txt`, which is worth reading before adding to this section: the refusal
        // was never wrong, its premise was "once the offscreen compositor exists", and it outlived
        // that by a week because nobody re-read it.
        Translate("translate-x", UtilityComposition.TranslateX);
        Translate("translate-y", UtilityComposition.TranslateY);

        // ⚠ <b>The root that moves BOTH axes, and it is not a third fragment.</b> v4's `translate-4`
        // is `translate: 1rem 1rem` — one class moving a box on the diagonal — so it writes the same
        // two `--tw-*` slots the two axis families write and assembles the same `translate`. One
        // family over both fragments rather than a new slot is what makes `translate-4
        // translate-x-8` compose the way the cascade says it should, last declaration winning per
        // slot with one assembly either way. Registering it as a single-property family beside the
        // two axes would have made `translate-4` a `translate-x-4` under a different spelling with y
        // left at its initial, which reads as the class half-working — the worse of the two
        // failures — and registering it against a `translate` of its own would have had the two
        // spellings of one movement fight.
        //
        // ⚠ Its own root and not a value on `translate-x`, and `SplitName`'s longest-prefix rule is
        // what keeps that safe: `translate-x-4` still reaches the axis family above and `translate-4`
        // reaches this one. `ShadowedFamilyTests` holds the rule; `rotate-z` beside `rotate` is the
        // same arrangement two sections down.
        //
        // ⚠ <b><see cref="ValueKind.Size" /> for `Translate`'s reason, and it is what closes the
        // three values the ledger recorded as missing on this root</b>: `translate-full` is a
        // hundred per cent of the element's own border box, `translate-px` is one pixel, and
        // `translate-4` is the spacing scale. ⚠ The ledger also listed `translate-x-full`,
        // `translate-x-px`, `translate-y-full` and `translate-y-px` as missing on this row, and all
        // four of them already resolved through the axis families above —
        // `CompositionTests.Emits("translate-x-full")` has pinned `--tw-translate-x: 100%` the whole
        // time. A value-gap column enumerated by class-name prefix attributes a sibling root's
        // classes to this one.
        Register(new Family(
            "translate",
            ValueKind.Size,
            [UtilityComposition.TranslateX, UtilityComposition.TranslateY],
            Alongside: [new UtilityDeclaration("translate", UtilityComposition.Translation())]
        ));

        // ⚠ <b>Its own registered name rather than a keyword on the family above, because
        // `Alongside` is appended on EVERY resolution of a family.</b> A `none` in that keyword
        // table would emit `translate: none` and then the assembly over the top of it, and the
        // assembly would win — a class spelling "do not move" that moves. `SplitName` takes the
        // longest registered prefix, so `translate-none` arrives here and `translate-4` arrives
        // above; `ShadowedFamilyTests` is what holds that rule.
        //
        // ⚠ And `none` is read BY NAME, which is the same shape `scale-none` and `rotate-none` have:
        // `TranslationReader.Of` compares the interned value against `none` before it parses
        // anything, so this is refusal shape 3's opposite — a reader that already distinguishes the
        // value, and no family able to emit it.
        //
        // ⚠ <b>This was written up as a REFUSAL on a parallel branch, and the refusal was wrong on
        // its second premise rather than its first.</b> That reading had `Family.Alongside` belongs
        // to the family and not to the value — which is true and is exactly the paragraph above —
        // and concluded from it that the class cannot be registered at all. It concluded that
        // because it only considered `Keywords("translate", …)` on the functional root; a separate
        // registered name is not a keyword on that family and never reaches its `Alongside`. The
        // ledger's `partial` on this row, and `mask-circle`'s neighbouring refusal, are the two
        // places that reading also reached.
        Static("translate-none", "translate", "none");

        // ⚠ <b>A percentage, because Tailwind's scale runs in hundredths.</b> `scale-150` is one and
        // a half, not a hundred and fifty — v4 emits `scale: 150%` and CSS reads a percentage on this
        // property as a ratio. Emitting the bare count, which is what `Number` did into `--scale`,
        // would make `scale-150` mean a hundred and fifty times the size.
        CountTemplate("scale", "{0}%", "scale");

        // ⚠ <b>`none` on both of these is read, and it is read by name rather than by falling out of
        // a parse.</b> `TransformReader.Of` opens with three `TryGet`s each of which compares the
        // written value against the interned `none` — so `scale: none` and `rotate: none` are the
        // documented way to turn one of the two properties off while its neighbour stands, and the
        // class that spells it is the one thing that could not reach it. That is refusal shape 3's
        // opposite: a reader that already distinguishes the value, and no family emitting it.
        //
        // ⚠ `scale-3d` is NOT here and the reason is not the reader. v4 emits
        // `scale: var(--tw-scale-x) var(--tw-scale-y) var(--tw-scale-z)` — a third axis, which
        // `UiTransform` deliberately cannot express for the reason `rotate-x-*` is refused two
        // blocks down. It stays a value gap on this root rather than becoming a declaration nothing
        // can act on.
        Keywords("scale", "scale", new() { ["none"] = "none" });

        // ⚠ <b>Per-axis, on the translations' mechanism exactly, and it needed the reader before it
        // could be honest.</b> These two were refused as "shadowed by `scale`, which is itself
        // inert" — a per-axis family over a refused property being inert by construction. That is
        // no longer the case, and the composition is the same one the translations use for the same
        // reason: CSS's `scale` takes both axes in one declaration, so `scale-x-150 scale-y-50` has
        // to arrive as one of them. The fragments' initial value is <b>one</b> and not zero — see
        // `UtilityComposition`, where getting that wrong would make a lone `scale-x-*` collapse the
        // other axis to nothing.
        Scale("scale-x", UtilityComposition.ScaleX);
        Scale("scale-y", UtilityComposition.ScaleY);

        // ⚠ And an angle, for the same class of reason: `rotate: 45` is not a value CSS has. The unit
        // is the whole difference between a declaration a browser honours and one it drops.
        CountTemplate("rotate", "{0}deg", "rotate");

        // The other half of the pair above; see its remark for why `none` is a read value here.
        Keywords("rotate", "rotate", new() { ["none"] = "none" });

        // ⚠ <b>And the third axis's spelling, which was refused for a blocker that had already
        // shipped.</b> The ledger's note said `rotate-z-*` waits on a `<transform-function>` parser —
        // "no `matrix()`, no `rotate()`, no list of functions in `StyleValue`" — and
        // `TransformReader.Functions` reads exactly that list: `matrix`, `translate`, `translateX/Y`,
        // `scale`, `scaleX/Y`, `rotate`, `rotateZ`, `skew` and `skewX/Y`, asserted against pixels in
        // `Vixen.Ui.Tests.TransformTests`. The refusal even declared its own expiry condition — an
        // `expires-on` clause naming `Vixen.Ui.Styling.StyleValueKind.Function`, written here without
        // its brackets because the sweep reads prose now and a quotation in brackets would be
        // recorded as a declaration — and that condition is *still* not met, because the parser was
        // built in `TransformReader` over the declaration's text rather than as a value kind.
        // ⚠ <b>A refusal can be satisfied without its named symbol arriving, and `RefusalExpiryTests`
        // cannot see that</b>; this is the first row it happened to.
        //
        // ⚠ <b>Registered as its own root rather than folded into `rotate`, and `SplitName` is why
        // that is safe:</b> the longest registered prefix wins, so `rotate-z-45` reaches here and
        // `rotate-45` still reaches `rotate` above. `ShadowedFamilyTests` holds that rule.
        //
        // ⚠ <b><see cref="ValueKind.Angle" /> and not <see cref="ValueKind.CountTemplate" />, because
        // zero is a value.</b> `TryCount` refuses it — rightly, for `grid-cols-0` — and `rotate-z-0`
        // is a real class that means the identity. ⚠ It also means `rotate-0` is *unresolved* today
        // while `rotate-z-0` resolves, which is a divergence in the family one line up rather than
        // in this one.
        //
        // ⚠ <b>The three-dimensional siblings are NOT registered here, and the reason is no longer
        // the parser.</b> `rotate-x-*`, `rotate-y-*`, `translate-z-*` and `scale-z-*` need a third
        // axis and a projective composite `UiTransform` deliberately cannot express — see its own
        // remark, and `Apply`'s: the composite quad's texture coordinates are interpolated linearly,
        // which is exact for an affine map and an approximation for a projective one. That is a
        // renderer decision and it is recorded on issue #228, not worked around here.
        Register(new Family(
            "rotate-z",
            ValueKind.Angle,
            [UtilityComposition.RotateZ],
            Template: "{0}",
            Alongside: [new UtilityDeclaration("transform", UtilityComposition.Transform())]
        ));

        // ⚠ <b>The two skews, and they were never a parser away either — which makes this the second
        // family to close on a refusal whose premise had already expired.</b> `rotate-z-*`'s note
        // said the shorthand waited on a `<transform-function>` grammar; `TransformReader.Functions`
        // has read `skew`, `skewX` and `skewY` since it was written, and `Vixen.Ui.Tests.TransformTests`
        // has asserted `transform: skewX(45deg)` against pixels for as long. The rows sat `absent`
        // with an empty note, so nothing recorded a reason and nothing could expire — worse than
        // `rotate-z-*`, whose refusal at least named a condition. See #227, which corrected itself.
        //
        // ⚠ <b><see cref="ValueKind.Angle" /> and the angle in the fragment, for `rotate-z-*`'s two
        // reasons</b>: zero is a real value here — `skew-x-0` means the identity — and `TryNegate`
        // refuses a value that does not begin with a digit, so `-skew-x-6` is spellable only while
        // the fragment holds `6deg` and the assembler holds `skewX(…)`.
        Skew("skew-x", [UtilityComposition.SkewX]);
        Skew("skew-y", [UtilityComposition.SkewY]);

        // ⚠ <b>Both fragments from one class, which is v4's own reading and not a shorthand for it.</b>
        // Tailwind's `skew-6` emits `skewX(6deg) skewY(6deg)` — two functions — rather than CSS's
        // two-argument `skew(6deg, 6deg)`. Writing the CSS spelling instead would resolve and paint
        // the same box today and would silently drop the axis of any `skew-y-*` written beside it,
        // because a `skew(…)` slot and a `skewY(…)` slot are different slots. The pair is the
        // translations' arrangement, arrived at from the other direction.
        Skew("skew", [UtilityComposition.SkewX, UtilityComposition.SkewY]);

        // ⚠ <b>`transform-none` is a keyword this engine already read, and the three classes v4
        // spells beside it are refused rather than absent.</b> `TransformReader` answers `none` with
        // the identity, so this row is a registration and nothing else. `transform-cpu` and
        // `transform-gpu` are compositing hints — v4's `transform-gpu` prepends `translateZ(0)` to
        // force a layer — and this engine has no layer to force: `DrawListBuilder` rebuilds the whole
        // draw list every frame and promotion is decided by what the element does, not by what its
        // classes ask for, which is `will-change-*`'s refusal one property over. Emitting the
        // `translateZ(0)` v4 emits would be worse than nothing: `TransformReader` cannot read it and
        // refuses the whole list, so `transform-gpu` beside a `rotate-z-45` would silently unrotate
        // the box. `transform-flat`/`transform-3d` are `transform-style` and `transform-content` and
        // its four siblings are `transform-box` — different properties, both refused with the 3D
        // family under #228 rather than here.
        Keywords("transform", "transform", new() { ["none"] = "none" });

        // ⚠ <b>The third refusal this section retired, and the only one that was refused as
        // <i>unobservable</i> rather than merely unread.</b> Doc 43 § C6 struck `origin-*` because
        // "`transform-origin` moves no channel, and cannot: it needs a transform whose fixed point
        // matters, and `translate` — the one transform the engine implements — is origin-independent."
        // Both halves were true and the second stopped being so with `rotate` and `scale`: a rotation
        // about a corner is a different picture from one about the centre, which is asserted against
        // pixels in `TransformPaintTests`. The refusal named its own condition and nothing was
        // watching for it — the third time on this page.
        Keywords("origin", "transform-origin", new() {
            ["center"] = "center",
            ["top"] = "top",
            ["top-right"] = "top right",
            ["right"] = "right",
            ["bottom-right"] = "bottom right",
            ["bottom"] = "bottom",
            ["bottom-left"] = "bottom left",
            ["left"] = "left",
            ["top-left"] = "top left"
        });

        // ── Transitions ─────────────────────────────────────────────────────────────────────
        //
        // ⚠ <b>The duration rides alongside, and without it the class was half of one.</b>
        // `transition-duration` defaults to <i>zero</i>, so a `transition` that named a property and
        // set no duration was a declaration that could not move a pixel — `class="transition"` did
        // nothing at all unless a `duration-*` happened to sit beside it. That is not a smaller family
        // than Tailwind's, it is a family whose single value is inert on its own, which is the shape
        // the consumption gate exists to refuse and could not see here: `transition-property` measures
        // as read off the `primed` scene, where the duration comes from the scene and not the class.
        //
        // ⚠ <b>The duration is the ONLY half that was missing, and the timing function is deliberately
        // not written — v4 emits `ease` and so does this, by saying nothing.</b> Tailwind's `transition`
        // sets `150ms` and `ease`; CSS's initial `transition-duration` is `0s`, which is why the first
        // is load-bearing, but its initial `transition-timing-function` is *already* `ease`, and
        // `Animator.ReadSpecs` falls back to `TimingFunction.Ease` for exactly that reason. Emitting it
        // anyway would buy nothing and cost `ease-*`, which sorts before `transition` and would be
        // overwritten by it — see the ordering note below.
        //
        // ⚠ <b>150 ms is v4's own number, not a choice made here.</b> A different default would make
        // the same class name mean a different animation in the two systems — the failure
        // `bg-conic-<angle>` is recorded under, arriving where it would be much harder to see.
        //
        // ⚠ <b>Through the composition fragments and NOT as two plain declarations, and the test that
        // says why is `TransitionUtilityTests.A_duration_beside_it_overrides_the_families_own_default`
        // — it was written against the plain version and it failed.</b> `UtilityGenerator` writes its
        // rules in ordinal class-name order for byte-determinism, which makes class-name order the
        // cascade order between two utilities of equal specificity; `duration-1000` sorts before
        // `transition`, so a `transition` writing `transition-duration: 150ms` directly lands after it
        // and wins. `class="transition duration-1000"` — the way the class is actually written —
        // would have become a 150 ms transition, which is a regression wearing a fix's clothes.
        // A `var(--tw-duration, 150ms)` takes the fragment whichever rule comes second.
        Register(new Family("transition", ValueKind.Static, ["transition-property"], new Dictionary<string, string>(StringComparer.Ordinal) {
            [string.Empty] = "all"
        }, Alongside: [
            new UtilityDeclaration("transition-duration", UtilityComposition.Reference(UtilityComposition.TransitionDuration))
        ]));

        // ⚠ Both the fragment and the longhand, which is v4's shape too. The longhand alone would be
        // invisible to the `transition` above; the fragment alone would make the family `composed`,
        // and `duration-*` has to keep working beside a hand-written `transition-property` that knows
        // nothing about any `--tw-*`.
        Register(new Family("duration", ValueKind.Duration, ["transition-duration", UtilityComposition.TransitionDuration]));

        // ⚠ <b>`transition-delay` had a reader before it had a class</b> — `Animator.ReadSpecs` reads
        // it into `RunningTransition.Delay`, which `Progress` and `IsFinished` both consult — so this
        // is a family gap and not an engine one, and the `Duration` kind it shares with `duration-*`
        // is the whole of what it needed. That asymmetry is why `delay-*` lands here while `animate-*`
        // does not: one was missing a spelling, the other is missing three mechanisms.
        Register(new Family("delay", ValueKind.Duration, ["transition-delay"]));

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
            ["ew-resize"] = "ew-resize", ["ns-resize"] = "ns-resize",
            ["help"] = "help"
        });

        Keywords("select", "user-select", new() {
            ["none"] = "none", ["text"] = "text", ["all"] = "all", ["auto"] = "auto"
        });

        Keywords("pointer-events", "pointer-events", new() { ["none"] = "none", ["auto"] = "auto" });

        // ⚠ `clip` is the fifth keyword and it was the one thing keeping all three of these roots off
        // `works`. It reads as `hidden` — `LayoutStyleBuilder` maps it there and says why at length:
        // CSS separates the two by a scroll container and by programmatic scrolling, and this engine
        // grants `hidden` neither, so the pair cannot be told apart by any consumer. Registering it
        // was not cosmetic: until `LayoutStyleBuilder` learned the keyword, `overflow-clip` clipped in
        // the draw list and stayed `Visible` to the layout, which is the half-property `overflow-auto`
        // used to be.
        Keywords("overflow", "overflow", new() {
            ["auto"] = "auto", ["hidden"] = "hidden", ["clip"] = "clip", ["visible"] = "visible", ["scroll"] = "scroll"
        });

        Keywords("overflow-x", "overflow-x", new() {
            ["auto"] = "auto", ["hidden"] = "hidden", ["clip"] = "clip", ["visible"] = "visible", ["scroll"] = "scroll"
        });

        Keywords("overflow-y", "overflow-y", new() {
            ["auto"] = "auto", ["hidden"] = "hidden", ["clip"] = "clip", ["visible"] = "visible", ["scroll"] = "scroll"
        });

        // ⚠ <b>Lengths where the web has keywords, because the two are answering different
        // questions.</b> A browser's `scrollbar-width: auto | thin | none` is a page's *preference*
        // about a widget the browser owns and draws; nothing here owns one, so the useful value is
        // the thickness, and `LayoutStyle.ScrollbarWidth` is a float of points. The three class
        // names stay Tailwind's so a stylesheet reads the same, and each resolves to a number.
        //
        // ⚠ `auto` is 10 because that is what `scrollbar.vertical` is in `ControlTheme.vcss`. The
        // point of this family is to reserve room for the bar `ScrollView` actually builds, and a
        // gutter that disagrees with the bar is worse than no gutter at all — it is the same wrong
        // width, plus a gap. If the theme's 10 moves, this moves with it.
        //
        // ⚠ Inert unless the box scrolls, and that is the property doing its job rather than a
        // hole: `Overflow.Hidden` clips without a bar, so there is nothing to reserve for. See
        // `LayoutStyle.ScrollbarWidth`.
        Keywords("scrollbar", "scrollbar-width", new() {
            ["auto"] = "10px", ["thin"] = "6px", ["none"] = "0px"
        });

        // The ratio keywords are pairs rather than numbers because the layout reads `16 / 9` with a
        // parser of its own — `LayoutStyleBuilder.TryRatio`, beside the bare-number form.
        //
        // ⚠ <b>`aspect-16/9` was said to be unspellable because the parser read a top-level slash as
        // an opacity, and that was never true.</b> `UtilityParser` keeps the suffix as written in
        // `SlashSuffix` as well as reading it as an alpha, and says in its own remark that which one
        // it means is the family's to decide. What was missing is the deciding: this family did not
        // look, so `aspect-16/9` resolved its head and emitted `aspect-ratio: 16`. A wrong ratio,
        // silently, rather than a class that could not be written.
        Register(new Family("aspect", ValueKind.Number, ["aspect-ratio"], new Dictionary<string, string>(StringComparer.Ordinal) {
            ["square"] = "aspect-ratio:1 / 1",
            ["video"] = "aspect-ratio:16 / 9",
            ["auto"] = "aspect-ratio:auto"
        }) { Slash = SlashMeaning.Ratio });

        // ── The eighteen roots that are deliberately NOT here ───────────────────────────────
        //
        // ⚠ <b>`docs/plan/43`'s `shadowed_by` column names nineteen rows today, eighteen of them
        // refusals with a measurement behind each, and this comment exists because the obvious
        // reading of that column — "one `Register` call per row" — is the one that produces a row
        // of inert classes.</b> ⚠ The count moves: the column held twenty-nine refusals at
        // `cf701146` and shrinks as they close, a closing root having its `shadowed_by` cell cleared
        // and its family filled in. `stroke-none` is the nineteenth and keeps both cells, because it
        // closed on a *keyword* of the very family that shadows it rather than on a family of its
        // own. Each refusal is written out in the
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
        //   debt rather than adding one.</b> ⚠ <b>This category is now EMPTY, and how it emptied is
        //   the warning worth keeping.</b> It held `scale-x/y/z-*` and `rotate-x/y/z-*` on the
        //   grounds that `scale` and `rotate` were `#23` in `InertProperties.txt` — "a per-axis
        //   family over a refused property is inert by construction", which was sound reasoning over
        //   a premise that had expired. Both properties are read now, `scale-x-*` and `scale-y-*`
        //   are registered above, and nothing in this file or in the gate could have said so: a
        //   refusal that *cites* another refusal inherits its expiry date, and no test checks either.
        //
        //   What is left of the six is not category 2. `scale-z-*`, `rotate-x/y/z-*` and
        //   `translate-z-*` are category 4: v4 emits them through `transform: rotateX(45deg)`.
        //   ⚠ <b>This used to continue "and there is no `<transform-function>` parser here", and
        //   that half was false when it was written</b> — `TransformReader.Functions` in
        //   `Vixen.Ui/Transform.cs` reads `matrix`, `translate/X/Y`, `scale/X/Y`, `rotate`,
        //   `rotateZ` and `skew/X/Y`, composed right to left, refusing the whole list if one
        //   function in it is unreadable, and pinned against pixels in `TransformTests` (#585). What
        //   is true is the clause beside it: `StyleValue` has no function kind, which is why a
        //   `transform` declaration cannot interpolate — and it is not what holds these back.
        //   `skew-*` is a family registration away (#227). The three-dimensional four are a *vertex*
        //   away: `UiVertex` has nowhere to put a `w`, so a projective quad would be rasterised with
        //   affine barycentrics (#548).
        //
        //   ⚠ <b>3. The property is READ, and the value is refused — so the gate stays green over a
        //   class that paints nothing.</b> The dangerous kind, and the one this table has to catch
        //   by hand because no per-property measurement can. `inset-shadow-*` and `inset-ring-*`
        //   emit `box-shadow`, which is read — but `DrawListBuilder.EmitShadow` refuses the `inset`
        //   keyword outright and says why, and `box-shadow: inset 0 2px 4px #000` moves no channel
        //   in any scene while `box-shadow: 0 2px 4px #000` moves paint. ⚠ <b>`ring-offset-*` used to
        //   be worse than inert and is not any more, and the half that changed is worth reading.</b>
        //   An offset ring is a two-shadow *list*, and `EmitShadow` refused lists — so a
        //   `ring-offset-2` beside a `ring-2` would have stopped the ring painting at all. Lists are
        //   painted now, a command each, last to first (`Rikarin/Vixen#279`). What still blocks the
        //   root is the last third of that issue, and ⚠ <b>the `calc()` clause that used to stand
        //   here expired on 2026-09-05</b>: v4 writes the outer ring's spread as
        //   `calc(var(--tw-ring-offset-width) + var(--tw-ring-width))`, and `StyleValueParser` folds
        //   that now — fold or refuse, so a mixed-unit expression is still `Unknown`. What is left
        //   is the five-fragment composition, which is what makes `shadow-lg ring-2` stop being
        //   "the cascade picks one", and `UtilityComposition` carries no offset fragment at all. ⚠ <b>`stroke-none` was the third example here and is now closed, which is worth
        //   keeping because of *how*: not by a registration but by a reading.</b> `Icon.Resolve`
        //   asked `ColorOf` for the slot and fell back to the foreground for anything that was not
        //   a colour, so `stroke: none` stroked. `UiDocument.KeywordOf` — the fourth reading beside
        //   `ColorOf`, `LengthOf` and `NumberOf` — and `Icon.IsNone` at both draw paths tell a paint
        //   from a colour, and `IconArtTests` pins all three states in pixels. Registering the
        //   keyword before the reader existed would have scored green and painted the glyph.
        //
        //   <b>4. The class is v4 compatibility surface, and `docs/plan/43` § D5 already says not to
        //   implement it.</b> `flex-shrink-*`, `flex-grow-*` and `max-w-screen-*` live in v4's
        //   `compat/legacy-utilities.ts`: registered, undocumented, superseded by `shrink-*`,
        //   `grow-*` and the sizing scale, all of which are here and read. Their properties are read
        //   too, so these three would have registered cleanly and passed everything — which is why
        //   the reason they are absent is a policy and not a measurement.
        //
        //   ⚠ <b>5. There is no channel to point a reader at, and the property's meaning is a
        //   negotiation with something this engine does not have.</b> Interactivity's six refusals
        //   are all this shape, and it is worth separating from shape 1 because "inert" understates
        //   it — there is not a reader missing, there is nothing for a reader to read. `appearance`
        //   is `color-scheme`'s refusal with the same subject: every control here is drawn from
        //   authored CSS, so `none` has no second look to strip. `touch` needs a UA touch behaviour
        //   to withhold, and touch events never reach `UiDocument` at all — `PlatformInput` drops
        //   `TouchDown`/`Moved`/`Up` through its `default`. `will-change` needs a retained surface
        //   keyed on an element, and `UiGeometryBuilder.LayerImage` is a per-frame ordinal.
        //   `field-sizing` needs a UA default field size for `fixed` to restore, and there is none.
        //   `resize` needs a grip element that no property can conjure, F6 having refused pseudo-
        //   elements. And `accent-*` is the near miss: `RangeBase` could read `accent-color`
        //   tomorrow, but the checkbox, radio and switch it is mostly written on are tinted by
        //   `var(--accent)` on a child part, and `var()` reads custom properties only — so half a
        //   family would score green over the half people use. Every keyword of every one of these
        //   was checked against the reader by hand; the per-family gate cannot, and `visibility`'s
        //   dead `collapse` is what happens when nobody does.
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

            // ⚠ Forty-five and not two, and not zero either. Zero is the one value of an angle family
            // that is often the *default* — `bg-conic-0` is the sweep `bg-conic` already draws — so a
            // probe written with it would measure the family inert on any engine that reads the
            // property, which is the shape of vacuity this list has been wrong about nine times.
            case ValueKind.Angle:
                yield return "45";
                break;

            // ⚠ An arbitrary probe, and the only one in this method. A `Placement` family has no named
            // scale to enumerate, so without this line it would contribute nothing to `Surface`, the
            // consumption gate would never meet it, and it would pass vacuously for ever — see the
            // kind's own remark. A quarter rather than the whole, because a layer sized to the whole
            // positioning area is the arrangement the record already has and moves nothing.
            case ValueKind.Placement:
                yield return "[25%_75%]";
                break;

            // ⚠ The second arbitrary probe, and the one whose class name has quotes in it. `onum`
            // rather than `tnum` because the probe's face already draws lining tabular figures, so
            // `tnum` is what it does anyway and the probe would measure the property inert — the
            // same trap `bg-conic-0` is written up under, arriving through a font instead of a
            // default.
            case ValueKind.FontFeatures:
                yield return "[\"onum\"_1]";
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

            // Both halves of the prefix, because they take different paths through `TryBlur` and a
            // probe of one says nothing about the other.
            case ValueKind.Blur:
                foreach (var blur in First(tokens.Blur.Keys)) {
                    yield return blur;
                }

                yield return "2";
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

            case ValueKind.DropShadow:
                foreach (var shadow in First(tokens.DropShadow.Keys)) {
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

        // ⚠ A modifier a family does not read must be a refusal and not a shrug. `p-4/2` used to
        // emit `padding: 16px` — the head resolved, the suffix went nowhere, and the author got a
        // rule that looks like the one they wrote. See `SlashMeaning`.
        if (candidate.SlashSuffix is not null && family.Slash == SlashMeaning.None) {
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

        // ⚠ The same rule one step finer, and in the same place for the same reason: only once the
        // value has resolved, so a `mask-nonsense` cannot emit a radial mask layer for a keyword the
        // family declined. See `Family.ValueAlongside`.
        if (family.ValueAlongside is not null
            && family.ValueAlongside.TryGetValue(candidate.Value, out var perValue)) {
            declarations.AddRange(perValue);
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

            // ⚠ <b>An angle family is the one kind whose declaration is not its value, so the hatch
            // has to go through the template rather than around it.</b> <c>bg-conic-[3rad]</c> means
            // a sweep of three radians; emitting <c>background-image: 3rad</c> is a declaration
            // `StyleValueParser` drops whole, which would take the element's stop list with it. This
            // is `hue-rotate`'s argument one level up: there the template appends a unit, here it
            // wraps the value in the function it belongs to.
            if (family.Kind == ValueKind.Angle) {
                return Emit(family, string.Format(CultureInfo.InvariantCulture, family.Template!, arbitrary), declarations);
            }

            return Emit(family, arbitrary, declarations);
        }

        // ⚠ A ratio before the keywords, because the keyword table is keyed on the whole value and
        // `16` is not in it — but `aspect-square/9` would otherwise take the keyword branch and
        // drop the denominator, which is the failure this whole mechanism exists to stop. A ratio
        // family reaching here with a suffix means the pair is the value.
        if (family.Slash == SlashMeaning.Ratio && candidate.SlashSuffix is { } denominator) {
            return TryRatioPart(candidate.Value, out var antecedent)
                && TryRatioPart(denominator, out var consequent)
                && Emit(family, antecedent + " / " + consequent, declarations);
        }

        // Keywords first, because `text-center` has to beat any colour or size named `center`.
        if (family.Keywords is not null && family.Keywords.TryGetValue(candidate.Value, out var keyword)) {
            // ⚠ A keyword takes no modifier even on a family that has one. `text-center/50` is not
            // a translucent alignment and `bg-cover/50` is not a translucent size; both used to
            // resolve and quietly lose the suffix.
            if (candidate.SlashSuffix is not null) {
                return false;
            }

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
            ValueKind.Angle => TryAngle(candidate.Value, out var degrees)
                && Emit(family, string.Format(CultureInfo.InvariantCulture, family.Template!, degrees + "deg"), declarations),

            // The arbitrary branch above has already answered every value this kind takes; a bare one
            // is a class v4 does not have, and inventing it here is what this kind's remark refuses.
            ValueKind.Placement => false,
            ValueKind.FontFeatures => false,
            ValueKind.Duration => TryNumber(candidate.Value, out var ms) && Emit(family, ms + "ms", declarations),
            ValueKind.Fraction => TryFraction(candidate.Value, out var fraction) && Emit(family, fraction, declarations),
            ValueKind.Radius => TryRadius(candidate.Value, tokens, out var radius) && Emit(family, radius, declarations),
            ValueKind.Blur => TryBlur(candidate.Value, tokens, out var blur) && Emit(family, blur, declarations),
            ValueKind.FontWeight => TryFontWeight(candidate.Value, tokens, out var weight) && Emit(family, weight, declarations),
            ValueKind.FontSize => TryFontSizeOrColor(candidate, tokens, declarations),
            ValueKind.Color => TryColor(candidate, tokens, out var colour) && Emit(family, colour, declarations),
            ValueKind.BorderEdge => TryBorderEdge(family, candidate, tokens, declarations),
            ValueKind.Shadow => TryShadow(family, candidate, tokens, declarations),
            ValueKind.DropShadow => TryDropShadow(family, candidate, tokens, declarations),
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
    ///     <para>
    ///         ⚠ <b>The six viewport keywords are here for exactly the reason the note above
    ///         describes, and they are the case that would have slipped through it.</b>
    ///         <c>full</c> and <c>screen</c> resolve to <c>100%</c>, which the shape test catches
    ///         only because this set names them; <c>svh</c> and its five siblings resolve to
    ///         <c>100vh</c>, which begins with a digit in the same way. <c>-h-dvh</c> is not
    ///         "minus one viewport tall" any more than <c>-w-full</c> is minus one hundred per
    ///         cent wide.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>lh</c> joined them the day it resolved, and it is the same trap a third
    ///         time.</b> It comes out as <c>1lh</c> — a digit again — so <c>-max-block-lh</c> would
    ///         have been "minus one line box tall" rather than a refusal. A value that stops being
    ///         unresolvable has to be looked at here as well as in <see cref="TrySize" />.
    ///     </para>
    /// </remarks>
    static readonly HashSet<string> NotNegatable = new(StringComparer.Ordinal) {
        "auto", "full", "screen", "min", "max", "fit",
        "svw", "lvw", "dvw", "svh", "lvh", "dvh", "lh"
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

    /// <summary>A named drop shadow, or the theme's default one for a bare <c>drop-shadow</c>.</summary>
    /// <remarks>
    ///     <see cref="TryShadow" />'s shape against the other namespace, and the resemblance is the
    ///     whole of what it shares — what lands in <paramref name="declarations" /> here is a
    ///     <c>--tw-*</c> fragment that <c>UtilityComposition.Filter</c> assembles, not a property any
    ///     engine reads. See <see cref="ThemeTokens.DropShadow" />.
    /// </remarks>
    static bool TryDropShadow(
        Family family,
        UtilityCandidate candidate,
        ThemeTokens tokens,
        List<UtilityDeclaration> declarations
    ) {
        var key = candidate.Value.Length == 0 ? ThemeTokens.DefaultKey : candidate.Value;
        return tokens.DropShadow.TryGetValue(key, out var shadow) && Emit(family, shadow, declarations);
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

    /// <summary>Resolves the <c>/7</c> of <c>text-lg/7</c> through the family that owns line heights.</summary>
    /// <remarks>
    ///     ⚠ <b>Delegated to <c>leading</c>'s own table rather than reimplemented, because the two
    ///     spellings have to mean the same thing.</b> <c>text-lg/7</c> and
    ///     <c>text-lg leading-7</c> are the same declaration written twice in Tailwind v4, and a
    ///     second copy of the scale here is a pair that agrees until one of them is edited. The
    ///     keyword half matters as much as the count: <c>leading-none</c> is the ratio <c>1</c> and
    ///     not a length, so <c>text-lg/none</c> has to be the ratio too.
    ///     <para>
    ///         The arbitrary form <c>text-lg/[1.5]</c> goes through verbatim, which is what the
    ///         brackets are for everywhere else.
    ///     </para>
    /// </remarks>
    static bool TryLeading(string suffix, ThemeTokens tokens, out string result) {
        result = string.Empty;

        if (suffix.Length == 0) {
            return false;
        }

        if (suffix[0] == '[' && suffix[^1] == ']') {
            var inside = suffix[1..^1].Replace('_', ' ');

            if (!IsPlausibleValue(inside)) {
                return false;
            }

            result = inside;
            return true;
        }

        if (Registry.TryGetValue("leading", out var leading)
            && leading.Keywords is { } keywords
            && keywords.TryGetValue(suffix, out var keyword)) {
            // The keyword table holds whole pairs — `line-height:1.25` — and only the value half is
            // wanted here, because the property is already known.
            result = keyword[(keyword.IndexOf(':', StringComparison.Ordinal) + 1)..];
            return true;
        }

        return TrySpacing(suffix, tokens, out result);
    }

    static bool TryFontSizeOrColor(UtilityCandidate candidate, ThemeTokens tokens, List<UtilityDeclaration> declarations) {
        // The documented resolution order for `text-`: keyword (already tried), then font size,
        // then colour. A colour named `lg` would be unreachable, which is the price of one prefix
        // meaning three things and is worth paying — `text-lg` and `text-accent` both read right.
        if (tokens.FontSize.TryGetValue(candidate.Value, out var size)) {
            var height = Px(size.LineHeight);

            // ⚠ <b>`text-lg/7` is v4's spelling for a size with a line height, and the slash here
            // is not the alpha it is one line below.</b> The same prefix takes both readings and
            // the value decides which: a font-size token takes a leading, a colour takes an alpha.
            // That is why the suffix is kept as written — a leading of `7` read as an opacity is
            // seven per cent.
            if (candidate.SlashSuffix is { } leading) {
                if (!TryLeading(leading, tokens, out height)) {
                    return false;
                }
            }

            declarations.Add(new UtilityDeclaration("font-size", Px(size.Size)));
            declarations.Add(new UtilityDeclaration("line-height", height));
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

    /// <summary>Resolves the value half of a sizing utility.</summary>
    /// <remarks>
    ///     ⚠ <b>The six viewport keywords are three spellings of two answers, and that is a property
    ///     of this engine rather than a shortcut.</b> CSS Values 4 separates <c>svw</c>/<c>lvw</c>/
    ///     <c>dvw</c> — and the <c>-vh</c> trio — only by what a retracting browser toolbar does to
    ///     the viewport: the <i>small</i> viewport assumes every retractable UA chrome is shown, the
    ///     <i>large</i> one assumes it is all hidden, and the <i>dynamic</i> one tracks the current
    ///     state. A Vixen surface has no retractable chrome to show or hide —
    ///     <c>LengthContext</c> is built from <c>UiSurface</c>'s width and height and
    ///     there is no second, smaller rectangle anywhere for the small viewport to be — so all three
    ///     name the same measurement and <c>vw</c>/<c>vh</c> is it. Emitting <c>100dvw</c> instead
    ///     would put a unit into the sheet that <c>StyleValueParser</c> does not read, which is the
    ///     inert-class shape this table is not allowed to add.
    ///     <para>
    ///         ⚠ <b>Both trios are answered by every family, including the ones named for the other
    ///         axis.</b> <c>h-dvw</c> is <c>height: 100vw</c> and <c>w-svh</c> is
    ///         <c>width: 100vh</c> — Tailwind names these after the viewport axis being measured and
    ///         not after the property being set, so the mapping belongs here, on the value, rather
    ///         than in the seven <see cref="Size" /> registrations. That is also what makes one rule
    ///         close all seven sizing roots at once.
    ///     </para>
    /// </remarks>
    static bool TrySize(UtilityCandidate candidate, ThemeTokens tokens, out string result) {
        result = string.Empty;

        // ⚠ A denominator is only a denominator to a numerator, so the keyword arm below is not
        // reachable with one. `w-full/2` used to be `width: 100%` — the suffix went nowhere, and
        // half of a full width is not something the class can be read as meaning.
        if (candidate.SlashSuffix is not null) {
            return TryFractionOf(candidate, out result);
        }

        switch (candidate.Value) {
            case "full":
                result = "100%";
                return true;
            // ⚠ <b>The families that measure one axis never get here</b>: each registers `screen` in
            // its keyword table as `100vw` or `100vh`, and `Resolve` reads that first. What is left
            // is `size-*` and the inset roots, which measure both axes at once and for which
            // Tailwind ships no `screen` class at all — a percentage of the containing block is what
            // they answered before this and nothing claims it is the viewport.
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
            case "svw":
            case "lvw":
            case "dvw":
                result = "100vw";
                return true;
            case "svh":
            case "lvh":
            case "dvh":
                result = "100vh";
                return true;

            // ⚠ <b>One line box, and the only sizing value whose unit the engine had to learn.</b>
            // Every other keyword above resolves to a unit the parser already read; `lh` did not
            // exist, so `max-block-lh` emitted text `StyleValueParser` refused and the class was the
            // ledger's one Sizing `partial`. It is answered by every family for the reason the
            // viewport trios are — Tailwind names it after what is measured, not after the property.
            case "lh":
                result = "1lh";
                return true;
            default:
                break;
        }

        return TrySpacing(candidate.Value, tokens, out result);
    }

    /// <summary>
    ///     <c>w-1/2</c> — the slash is a fraction here and not an opacity, which is why the suffix is
    ///     kept as written as well as read as one.
    /// </summary>
    static bool TryFractionOf(UtilityCandidate candidate, out string result) {
        if (candidate.SlashSuffix is { } denominator
            && float.TryParse(candidate.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && float.TryParse(denominator, NumberStyles.Float, CultureInfo.InvariantCulture, out var divisor)
            && divisor != 0f) {
            result = (numerator / divisor * 100f).ToString("0.####", CultureInfo.InvariantCulture) + "%";
            return true;
        }

        result = string.Empty;
        return false;
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

    /// <summary>Resolves a <c>blur-*</c>: a named step of the scale, or a count of the spacing unit.</summary>
    /// <remarks>
    ///     ⚠ <b>The named step is tried first and the fall-through is what keeps the old spelling
    ///     alive.</b> `blur-md` is v4's and was unresolvable here until the `--blur-*` namespace
    ///     shipped; `blur-8` is this engine's and is written in its own remarks. They cannot
    ///     collide — a theme key is not a number — so answering both costs one lookup and loses
    ///     nothing.
    /// </remarks>
    static bool TryBlur(string value, ThemeTokens tokens, out string result) {
        if (tokens.Blur.TryGetValue(value, out var blur)) {
            result = Px(blur);
            return true;
        }

        return TrySpacing(value, tokens, out result);
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

    /// <summary>One half of a written ratio.</summary>
    /// <remarks>
    ///     ⚠ Strictly positive, which <see cref="TryNumber" /> is not. CSS Sizing 4 § 4.1 makes a
    ///     <c>&lt;ratio&gt;</c> two positive numbers, and <c>aspect-16/0</c> is a box with no height
    ///     at any width — a declaration the layout would honour into a zero-area element rather than
    ///     one it would refuse. A class that cannot mean anything is better reported as unknown.
    /// </remarks>
    static bool TryRatioPart(string value, out string result) {
        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && number > 0f
            && float.IsFinite(number)) {
            result = number.ToString("0.####", CultureInfo.InvariantCulture);
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

    /// <summary>A whole number of degrees. Zero is a value, which is the whole of why this is not <see cref="TryCount" />.</summary>
    static bool TryAngle(string value, out int degrees) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out degrees) && degrees >= 0;

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

    /// <summary>Registers one <c>font-variant-numeric</c> keyword as a fragment plus the assembly.</summary>
    /// <param name="name">The class, which is also the keyword — v4 spells these as bare words.</param>
    /// <param name="fragment">The <c>--tw-*</c> slot it writes, one per CSS keyword *set*.</param>
    /// <param name="keyword">The keyword it writes there.</param>
    /// <remarks>
    ///     ⚠ <see cref="Translate" />'s shape with the fragment named per call rather than derived
    ///     from the class, because two classes share a slot: <c>lining-nums</c> and
    ///     <c>oldstyle-nums</c> are the two values of one set and must overwrite each other. A helper
    ///     that derived the slot from the name would give them one each and let both apply.
    /// </remarks>
    static void NumericFigure(string name, string fragment, string keyword) =>
        Register(new Family(
            name,
            ValueKind.Static,
            [fragment],
            new Dictionary<string, string>(StringComparer.Ordinal) { [string.Empty] = $"{fragment}:{keyword}" },
            Alongside: [
                new UtilityDeclaration("font-variant-numeric", UtilityComposition.NumericFigures())
            ]
        ));

    static void Static(string name, string property, string value) =>
        Register(new Family(name, ValueKind.Static, [property], new Dictionary<string, string>(StringComparer.Ordinal) {
            [string.Empty] = $"{property}:{value}"
        }));

    /// <summary>Registers a name that is a static utility bare and a sizing one with a value.</summary>
    /// <param name="name">The utility prefix, which is also the whole of the static class.</param>
    /// <param name="staticProperty">What the bare form sets.</param>
    /// <param name="staticValue">What it sets it to.</param>
    /// <param name="sizeProperty">What the form with a value sets.</param>
    /// <remarks>
    ///     ⚠ <b>One family and not two, because <see cref="Register" /> keeps the first under a
    ///     name.</b> A second <see cref="Size" /> call for <c>block</c> would be silently discarded
    ///     and every <c>block-*</c> class would go on being reported as an unrecognised typo — the
    ///     failure being quiet is why this is a named helper rather than a hand-rolled
    ///     <see cref="Register" /> at each site.
    ///     <para>
    ///         The split works because <see cref="Resolve" /> consults the keyword table before the
    ///         value kind: the empty key answers the bare class, and anything else falls through to
    ///         <see cref="TrySize" />. <c>ValueKind.Static</c> is deliberately *not* used here —
    ///         it is the kind that answers <c>false</c> to every value, which is the behaviour being
    ///         replaced.
    ///     </para>
    /// </remarks>
    /// <param name="screen">The viewport extent along the sizing property's axis. See <see cref="SizeToScreen" />.</param>
    static void StaticOrSize(string name, string staticProperty, string staticValue, string sizeProperty, string screen) =>
        Register(new Family(name, ValueKind.Size, [sizeProperty], new Dictionary<string, string>(StringComparer.Ordinal) {
            [string.Empty] = $"{staticProperty}:{staticValue}",
            ["screen"] = screen
        }));

    static void Keywords(
        string name,
        string property,
        Dictionary<string, string> keywords,
        params UtilityDeclaration[] alongside
    ) {
        var qualified = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in keywords) {
            qualified[key] = $"{property}:{value}";
        }

        Register(
            new Family(
                name,
                ValueKind.Keyword,
                [property],
                qualified,

                // ⚠ Null and not an empty array where there is nothing alongside, because `Family`
                // distinguishes the two and the ledger's join reads what a family emits. `params`
                // hands an empty array to every existing caller otherwise, which would put a family
                // with no companions in the same shape as one whose companions were dropped.
                Alongside: alongside.Length > 0 ? alongside : null
            )
        );
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

    /// <summary>What every <c>backdrop-*</c> family emits beside its own fragment.</summary>
    /// <remarks>
    ///     ⚠ <b>The unprefixed property alone, where Tailwind v4 emits <c>-webkit-backdrop-filter</c>
    ///     beside it.</b> That copy exists for Safari and there is no Safari here — it would be a
    ///     declaration in every generated sheet that nothing in the engine could ever read, which is
    ///     the exact shape of debt the consumption gate is for. Dropping it also keeps these ten roots
    ///     honest in the parity ledger: with the prefix, every one of them would read <c>partial</c>
    ///     for a reason that has nothing to do with the feature.
    /// </remarks>
    static UtilityDeclaration[] BackdropAlongside => [
        new("backdrop-filter", UtilityComposition.BackdropFilter())
    ];

    /// <summary>One of the seven proportional <c>backdrop-filter</c> functions.</summary>
    /// <param name="name">The class prefix, whose tail is also the CSS function's name.</param>
    /// <param name="fragment">The <c>--tw-*</c> the amount goes into.</param>
    /// <param name="bare">
    ///     What the class with no value means, or null where it means nothing.
    ///     <c>backdrop-grayscale</c>, <c>backdrop-invert</c> and <c>backdrop-sepia</c> have one.
    /// </param>
    /// <remarks>
    ///     ⚠ <see cref="Filter" />'s shape with a different assembler, and the duplication is the
    ///     point rather than an oversight: the two properties are independent and a helper that served
    ///     both would have to be told which, on every call, for one line saved.
    /// </remarks>
    static void Backdrop(string name, string fragment, string? bare = null) =>
        Register(new Family(
            name,
            ValueKind.Fraction,
            [fragment],
            bare is null
                ? null
                : new Dictionary<string, string>(StringComparer.Ordinal) { [string.Empty] = fragment + ":" + bare },
            Alongside: BackdropAlongside
        ));

    /// <summary>Registers a family whose rule is about the element's children rather than the element.</summary>
    /// <param name="name">The utility prefix.</param>
    /// <param name="kind">How its value turns into declarations — the same kinds as anything else.</param>
    /// <param name="properties">What it sets, on each child but the last.</param>
    /// <remarks>
    ///     ⚠ <b>The scope is the bare <c>&gt; :not(:last-child)</c>, and v4's <c>:where()</c> goes
    ///     round the whole selector rather than round this.</b> The <c>&amp;</c> in v4's
    ///     <c>:where(&amp; &gt; :not(:last-child))</c> is CSS nesting, which the loader does not do,
    ///     so the flattening is <see cref="UtilityGenerator" />'s — and it wraps there because that
    ///     is where the variants have already been applied and the whole selector exists. Wrapping
    ///     this constant instead would emit <c>.space-y-4 &gt; :where(:not(:last-child))</c>, which
    ///     lands at (0,1,0) and only ties with a child's own <c>mb-0</c>.
    ///     <para>
    ///         ⚠ <b>This remark used to say the rule was stuck at (0,2,0) because
    ///         <c>SelectorCompiler</c> counts <c>:where()</c> like <c>:is()</c> and the charge could
    ///         not be dropped. It counted nothing:</b> ExCSS 4.3.2 does not parse <c>:where()</c> at
    ///         all, so the whole selector arrived as one unknown and the rule was refused rather
    ///         than compiled at the wrong specificity. The compiler repairs that text itself now —
    ///         <c>Vixen.Ui.Styling.Tests</c>' <c>WhereSelectorTests</c>.
    ///     </para>
    /// </remarks>
    static void Between(string name, ValueKind kind, string[] properties) =>
        Register(new Family(name, kind, properties, Scope: BetweenChildren));

    /// <summary>Every child but the last, which is what <c>space-*</c> and <c>divide-*</c> are about.</summary>
    const string BetweenChildren = " > :not(:last-child)";

    static void Size(string name, params string[] properties) =>
        Register(new Family(name, ValueKind.Size, properties));

    /// <summary>A sizing family that also answers <c>screen</c>, on the axis its property measures.</summary>
    /// <param name="name">The utility prefix.</param>
    /// <param name="screen">The viewport extent along that axis: <c>100vw</c> or <c>100vh</c>.</param>
    /// <param name="properties">What it sets. All on the one axis, or the answer would be two values.</param>
    /// <remarks>
    ///     ⚠ <b>Through the keyword table rather than through <see cref="TrySize" />, because
    ///     <see cref="Resolve" /> consults keywords first and <see cref="TrySize" /> never sees the
    ///     property.</b> The value carries no colon, so the keyword branch emits it into every one of
    ///     the family's properties — which is why they must share an axis. <c>size-*</c> is the one
    ///     sizing root that does not, and Tailwind has no <c>size-screen</c> for it to answer.
    /// </remarks>
    static void SizeToScreen(string name, string screen, params string[] properties) =>
        Register(new Family(name, ValueKind.Size, properties, new Dictionary<string, string>(StringComparer.Ordinal) {
            ["screen"] = screen
        }));

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

    /// <summary>Registers one axis of a composed <c>scale</c>.</summary>
    /// <param name="name">The utility prefix.</param>
    /// <param name="fragment">The <c>--tw-*</c> name this axis writes.</param>
    /// <remarks>
    ///     ⚠ <see cref="Translate" />'s shape with one difference that matters:
    ///     <see cref="ValueKind.CountTemplate" /> rather than <see cref="ValueKind.Size" />, because a
    ///     scale's count is a percentage and not a length. <c>scale-x-150</c> is one and a half;
    ///     resolving it through the spacing scale, which is what <c>Size</c> does, would make it six
    ///     hundred pixels of nothing.
    /// </remarks>
    static void Scale(string name, string fragment) =>
        Register(new Family(
            name,
            ValueKind.CountTemplate,
            [fragment],
            Template: "{0}%",
            Alongside: [new UtilityDeclaration("scale", UtilityComposition.Scaling())]
        ));

    /// <summary>Registers a skew axis as an angle fragment plus the <c>transform</c> it assembles into.</summary>
    /// <param name="name">The utility prefix.</param>
    /// <param name="fragments">The fragments it writes — one per axis, both for the bare root.</param>
    static void Skew(string name, string[] fragments) =>
        Register(new Family(
            name,
            ValueKind.Angle,
            fragments,
            Template: "{0}",
            Alongside: [new UtilityDeclaration("transform", UtilityComposition.Transform())]
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

    /// <summary>A round mask, centred where <c>mask-radial-at-*</c> put it.</summary>
    /// <remarks>
    ///     ⚠ <b>The <c>at</c> is written unconditionally and its fragment defaults to <c>center</c>,
    ///     which is CSS's own default — so this says what "no geometry at all" said before it.</b>
    ///     <c>DrawListBuilder.MaskFrame</c> resolves a centred position to a zero offset and the box's
    ///     half size, which is the record a radial mask already had; the alternative, emitting the
    ///     <c>at</c> only from <c>mask-radial-at-*</c>, would need that class to win the cascade
    ///     against every other <c>mask-radial-*</c> on the element, and it does not.
    /// </remarks>
    /// <remarks>
    ///     ⚠ <b>The ending size is written unconditionally too, for the <c>at</c>'s reason and with
    ///     the same consequence.</b> Its fragment defaults to CSS's own <c>farthest-corner</c>, so a
    ///     mask that names no ending says exactly what "no geometry at all" said before — and
    ///     <c>BackgroundGradient.IsDefaultEnding</c> is what keeps that on the shader's fast path
    ///     rather than merely arriving at the same picture by a longer route.
    /// </remarks>
    /// <remarks>
    ///     ⚠ <b>The ending <i>shape</i> is written unconditionally as well, and it is a second
    ///     fragment rather than part of the size's.</b> CSS makes the two independent —
    ///     <c>circle closest-side</c> is one ending named in two keywords — so
    ///     <c>mask-circle mask-radial-closest-side</c> is two classes assembling one
    ///     <c>mask-image</c>, and a single fragment would let whichever the cascade picked last
    ///     erase the other's half. <c>GradientReader</c> reads the prelude word by word and has done
    ///     since #545, so <c>ellipse farthest-corner at center</c> is the same record
    ///     <c>farthest-corner at center</c> already produced.
    /// </remarks>
    static string Radial =>
        UtilityComposition.MaskImage(
            "radial",
            $"{UtilityComposition.Reference(UtilityComposition.MaskRadialShape)} "
            + $"{UtilityComposition.Reference(UtilityComposition.MaskRadialSize)} "
            + $"at {UtilityComposition.Reference(UtilityComposition.MaskRadialPosition)}"
        );

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

    /// <summary>The <c>var()</c> a <c>snap-x</c>/<c>snap-y</c>/<c>snap-both</c> names its strictness by.</summary>
    static string SnapStrictness => UtilityComposition.Reference(UtilityComposition.ScrollSnapStrictness);

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
