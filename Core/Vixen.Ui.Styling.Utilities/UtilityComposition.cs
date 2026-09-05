// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Styling.Utilities;

/// <summary>The <c>--tw-*</c> fragments utilities contribute to, and what each one is worth unset.</summary>
/// <remarks>
///     <para>
///         <b>A composed utility sets a custom property instead of a declaration, and a different
///         utility assembles the pieces.</b> <c>from-accent</c> emits no <c>background-image</c> at
///         all — it emits <c>--tw-gradient-from</c>, and <c>bg-linear-to-r</c> is what reads the
///         fragments and builds the gradient. Twelve of the 328 Tailwind roots in
///         <c>docs/plan/43-web-styling-parity.md</c> are this shape, and the same pattern is how v4
///         does transforms (<c>translate-x</c> + <c>scale</c> + <c>rotate</c> into one
///         <c>transform</c>), <c>box-shadow</c> and filters. So this is not a gradient feature.
///     </para>
///     <para>
///         ⚠ <b>The composition is resolved by the cascade at use time, not by the generator at emit
///         time, and the reason is variants.</b> <c>from-accent hover:from-accent-hover</c> is two
///         rules with two different selectors, and which one supplies the colour is a question only
///         the cascade can answer — it depends on whether the pointer is over the element *now*. A
///         generator composing when it emits knows neither, so it would have to either drop the
///         variant (silently: the exact failure this programme exists to eliminate) or emit the
///         cross-product of every fragment-bearing class with every assembler class, which needs a
///         selector naming both — <c>.bg-linear-to-r.hover\:from-accent-hover:hover</c> — and grows
///         as assemblers × fragments × variants. <c>CompositionTests</c> holds both halves of that
///         argument as tests rather than as prose.
///     </para>
///     <para>
///         ⚠ <b>An unset custom property poisons the whole declaration, and the initial value here is
///         the answer to it.</b> Per CSS, a <c>var()</c> that resolves to nothing and carries no
///         fallback makes the declaration <i>invalid at computed-value time</i> —
///         <see cref="Vixen.Ui.Styling.VarSubstitution" /> implements exactly that, by returning
///         null. So a naive <c>linear-gradient(var(--tw-gradient-from), var(--tw-gradient-via),
///         var(--tw-gradient-to))</c> would make <c>from-red to-blue</c> with no <c>via</c> produce
///         <i>no gradient at all</i> rather than a two-stop one. The web's two answers are
///         <c>@property</c> with an <c>initial-value</c>, or a <c>var()</c> fallback chain. Vixen has
///         no <c>@property</c>; it has had the fallback chain since <c>VarSubstitution</c> was
///         written, so every fragment is declared here <i>with</i> its initial value and is only ever
///         referenced through <see cref="Reference" />, which welds the two together. A fragment that
///         cannot say what it is worth unset does not belong in this table.
///     </para>
///     <para>
///         <b>What <c>@property</c> would still buy, so that its absence is a known quantity rather
///         than a discovery.</b> Two things, neither of which blocks this mechanism. Registration
///         carries <c>inherits: false</c>, and Vixen's custom properties all inherit — see
///         <see cref="Vixen.Ui.Styling.InheritedProperties.IsCustomProperty" /> — so a fragment set on
///         a box is visible to its descendants, and a child carrying an assembler and no fragments of
///         its own picks up its parent's. That is what unregistered custom properties do on the web
///         too, so it is correct CSS and a divergence from Tailwind, which registers them precisely to
///         stop the leak. Registration also gives a fragment a <i>type</i>, which is what lets a
///         browser interpolate one — so a transitioned gradient is out of reach until it exists.
///         Neither is a prerequisite: both are refinements to a mechanism that works without them.
///     </para>
/// </remarks>
public static class UtilityComposition {
    /// <summary>The prefix every fragment name carries.</summary>
    /// <remarks>
    ///     Tailwind's, kept rather than renamed. The names are an implementation detail of the utility
    ///     layer and never appear in markup, so the only thing distinguishing them buys is that
    ///     somebody reading a generated sheet against Tailwind's documentation sees the same words.
    ///     ⚠ It also keeps them clear of <c>--blur</c>, <c>--rotate</c> and <c>--scale</c>, which are
    ///     <i>not</i> fragments: nothing assembles them, so they are placeholders parked in a name, and
    ///     <c>InertProperties.txt</c> records them against tasks #23 and #28. Giving them assemblers is
    ///     what those tasks are.
    ///     ⚠ <b><c>--translate-x</c>/<c>-y</c> used to be in that list and are not any more.</b> They
    ///     are <see cref="TranslateX" /> and <see cref="TranslateY" /> now — real fragments, under the
    ///     prefix, assembled into <c>translate</c> — which is the distinction this paragraph exists to
    ///     draw: an unprefixed <c>--name</c> is a value parked where no engine will ever look for it,
    ///     and a prefixed one is half of a declaration something reads.
    /// </remarks>
    public const string Prefix = "--tw-";

    // ── The gradient stops ──────────────────────────────────────────────────────────────────
    //
    // Named as constants because a fragment is referred to in two places that must agree — the family
    // that sets it and the assembler that reads it — and a typo in either is a silent no-op rather
    // than a compile error. The whole point of the mechanism is that those two places are far apart.

    /// <summary>The gradient's first stop colour.</summary>
    public const string GradientFrom = Prefix + "gradient-from";

    /// <summary>The gradient's middle stop colour, when there is one.</summary>
    public const string GradientVia = Prefix + "gradient-via";

    /// <summary>The gradient's last stop colour.</summary>
    public const string GradientTo = Prefix + "gradient-to";

    /// <summary>Where the first stop sits.</summary>
    public const string GradientFromPosition = Prefix + "gradient-from-position";

    /// <summary>Where the middle stop sits.</summary>
    public const string GradientViaPosition = Prefix + "gradient-via-position";

    /// <summary>Where the last stop sits.</summary>
    public const string GradientToPosition = Prefix + "gradient-to-position";

    /// <summary>The assembled stop list every gradient assembler interpolates.</summary>
    /// <remarks>
    ///     ⚠ <b>This one's initial value <i>is</i> the two-stop list, and that is what makes a missing
    ///     <c>via-*</c> degrade instead of vanish.</b> <c>from-*</c> and <c>to-*</c> deliberately do
    ///     not set it: they set their own colours and let the fallback do the assembling, so the
    ///     two-stop form is what happens when nobody says otherwise rather than something that has to
    ///     be emitted correctly by two separate families. Only <c>via-*</c> overrides it, because
    ///     adding a middle stop is the one thing the fallback cannot express — which leaves exactly
    ///     one family in the table with an alongside declaration instead of three.
    /// </remarks>
    public const string GradientStops = Prefix + "gradient-stops";

    // ── The mask stops ──────────────────────────────────────────────────────────────────────
    //
    // ⚠ <b>A second set rather than the gradient's, and they cannot be shared however alike they
    // look.</b> An element may carry a background gradient and a mask at once — <c>bg-linear-to-r
    // from-accent to-surface-3 mask-linear-from-70%</c> is an ordinary thing to write — and one set
    // of fragments would make the mask's stops overwrite the background's silently.
    //
    // ⚠ <b>And the initials are the other way up from the gradient's, because a mask's job is the
    // opposite of a fill's.</b> A gradient with no stops set should paint nothing, so both of its
    // ends default to <c>transparent</c>. A mask with no stops set must show everything, so its near
    // end defaults to <c>black</c> — opaque, and therefore fully covering — and only its far end is
    // <c>transparent</c>. Copying the gradient's pair here would make a bare <c>mask-linear-45</c>
    // erase the element.

    /// <summary>The mask ramp's first stop colour. Only its alpha is read.</summary>
    public const string MaskFrom = Prefix + "mask-from";

    /// <summary>The mask ramp's last stop colour. Only its alpha is read.</summary>
    public const string MaskTo = Prefix + "mask-to";

    /// <summary>Where the mask ramp's first stop sits.</summary>
    public const string MaskFromPosition = Prefix + "mask-from-position";

    /// <summary>Where the mask ramp's last stop sits.</summary>
    public const string MaskToPosition = Prefix + "mask-to-position";

    /// <summary>A linear mask's direction.</summary>
    /// <remarks>
    ///     ⚠ <c>180deg</c> — CSS's own default for <c>linear-gradient()</c>, which is <c>to bottom</c>
    ///     — so <c>mask-linear-from-50%</c> on its own fades downwards rather than refusing to
    ///     resolve. Separate from <see cref="MaskConicAngle" /> because the two defaults differ and a
    ///     shared fragment would give whichever shape was written second the other's zero.
    /// </remarks>
    public const string MaskLinearAngle = Prefix + "mask-linear-angle";

    /// <summary>Where a conic mask's sweep starts.</summary>
    public const string MaskConicAngle = Prefix + "mask-conic-angle";

    /// <summary>How long a transition the <c>transition</c> class started runs for.</summary>
    /// <remarks>
    ///     ⚠ <b>A fragment rather than a plain declaration on the <c>transition</c> family, and the
    ///     reason is the generated sheet's own ordering.</b> <see cref="UtilityGenerator" /> writes
    ///     its rules in <i>ordinal class-name order</i> — deliberately, so that a project produces the
    ///     same file byte for byte — which makes class-name order the cascade order between two
    ///     utilities of equal specificity. <c>duration-1000</c> sorts before <c>transition</c>, so a
    ///     <c>transition</c> that wrote <c>transition-duration: 150ms</c> directly would land
    ///     <i>after</i> the <c>duration-*</c> beside it and beat it — turning
    ///     <c>class="transition duration-1000"</c>, which is how the class is written in practice,
    ///     into a 150 ms transition. Read through a <see cref="Reference" /> the value comes from the
    ///     fragment whichever rule is written second, and the initial value below is what
    ///     <c>transition</c> means on its own.
    /// </remarks>
    public const string TransitionDuration = Prefix + "duration";

    /// <summary>Where a radial mask's centre sits, as a CSS <c>&lt;position&gt;</c>.</summary>
    /// <remarks>
    ///     ⚠ <b><c>center</c>, which is CSS's own default, so the fragment costs nothing while nobody
    ///     sets it.</b> <c>DrawListBuilder.MaskFrame</c> resolves a centred <c>at</c> to a zero offset
    ///     and the box's own half size — the arrangement the entry already had — so
    ///     <c>radial-gradient(at center, …)</c> and <c>radial-gradient(…)</c> reach the shader as the
    ///     same record. Without that the fragment would put every radial mask in the interface on the
    ///     positioned path to arrive where it started.
    /// </remarks>
    public const string MaskRadialPosition = Prefix + "mask-radial-position";

    // ── The mask layers ─────────────────────────────────────────────────────────────────────
    //
    // ⚠ <b>A `mask-image` is a list, and these are the slots the utilities fill it from.</b> Every
    // `mask-*` class writes the same three-layer `mask-image` — see <see cref="MaskLayers" /> — and
    // sets whichever of these three its shape belongs to. That is what lets `mask-radial-from-50%`
    // and `mask-conic-to-80%` compose instead of one silently winning the cascade, which is what
    // happened while `mask-image` was written whole by each family.
    //
    // ⚠ <b>Their initial is a gradient that covers everything, and the whole scheme rests on it.</b>
    // Under `mask-composite: intersect` — which every one of these utilities also emits — an opaque
    // layer is the identity, so a slot nobody filled changes nothing. Defaulting them to `none`
    // instead would make the whole declaration invalid; defaulting them to a *transparent* gradient
    // would erase the element. `DrawListBuilder.Reduce` is what stops the untouched slots costing
    // anything, and it drops them precisely because they are opaque and intersected.

    /// <summary>The <c>mask-image</c> layer a linear mask, or a set of edge ramps, fills.</summary>
    public const string MaskLinear = Prefix + "mask-linear";

    /// <summary>The layer a radial mask fills.</summary>
    public const string MaskRadial = Prefix + "mask-radial";

    /// <summary>The layer a conic mask fills.</summary>
    public const string MaskConic = Prefix + "mask-conic";

    /// <summary>A gradient that covers everything, which is the initial value of every mask layer.</summary>
    /// <remarks>
    ///     ⚠ Two stops, because <c>GradientReader</c> refuses a one-stop gradient — and both of them
    ///     white, because only the <i>alpha</i> reaches <c>UiMask</c> and white's is one.
    /// </remarks>
    public const string MaskOpaque = "linear-gradient(#fff, #fff)";

    /// <summary>The four box edges a <c>mask-t-*</c> ramp and its siblings run from.</summary>
    /// <remarks>
    ///     ⚠ <b>The names are the CSS keywords, because they go straight into <c>to &lt;side&gt;</c>.</b>
    ///     <c>mask-t-from-50%</c> is a ramp running <i>towards</i> the top — solid at the bottom,
    ///     fading out at the top — so the gradient is <c>linear-gradient(to top, …)</c> and the class
    ///     letter has to become the keyword somewhere. Here, once, rather than at each of the twelve
    ///     registrations.
    /// </remarks>
    public static readonly string[] MaskEdges = ["top", "right", "bottom", "left"];

    /// <summary>The whole gradient one edge's ramp assembles to.</summary>
    public static string MaskEdge(string edge) => Prefix + "mask-" + edge;

    /// <summary>One edge ramp's near colour. Only its alpha is read.</summary>
    public static string MaskEdgeFrom(string edge) => MaskEdge(edge) + "-from";

    /// <summary>One edge ramp's far colour.</summary>
    public static string MaskEdgeTo(string edge) => MaskEdge(edge) + "-to";

    /// <summary>Where one edge ramp's near stop sits.</summary>
    public static string MaskEdgeFromPosition(string edge) => MaskEdge(edge) + "-from-position";

    /// <summary>Where one edge ramp's far stop sits.</summary>
    public static string MaskEdgeToPosition(string edge) => MaskEdge(edge) + "-to-position";

    // ── The translation ─────────────────────────────────────────────────────────────────────
    //
    // ⚠ Two fragments and *one* property, which is the difference between this pair and the gradient
    // stops above. `translate` takes both axes in one declaration, so `translate-x-2 translate-y-4`
    // is two classes that must end up as `translate: 8px 16px` — and a utility system emitting one
    // declaration per class cannot express that at all: whichever rule the cascade picked last would
    // win outright and the other axis would silently be zero. That is the case the mechanism exists
    // for, stated in `docs/plan/43-web-styling-parity.md` § A7 and in Tailwind v4's own output.

    /// <summary>How far along x a transform moves the box.</summary>
    public const string TranslateX = Prefix + "translate-x";

    /// <summary>How far along y.</summary>
    public const string TranslateY = Prefix + "translate-y";

    /// <summary>How much a transform scales the box along x.</summary>
    /// <remarks>
    ///     ⚠ The translations' arrangement exactly, for the identical reason one property up: CSS's
    ///     <c>scale</c> takes both axes in one declaration, so <c>scale-x-150 scale-y-50</c> is two
    ///     classes that must arrive as <c>scale: 150% 50%</c>. Written as one declaration each,
    ///     whichever rule the cascade picked last would win and the other axis would silently be the
    ///     initial value — which for a scale is one, so the class would look like it had simply been
    ///     ignored rather than overwritten.
    /// </remarks>
    public const string ScaleX = Prefix + "scale-x";

    /// <summary>And along y.</summary>
    public const string ScaleY = Prefix + "scale-y";

    // ── The transform list ──────────────────────────────────────────────────────────────────
    //
    // ⚠ <b>A fragment for a family that is currently alone, and the reason is the <i>next</i> two
    // functions rather than this one</b> — the argument <see cref="Blur" /> makes one property over.
    // CSS's `transform` is an ordered list, so `rotate-z-45 skew-x-6` has to come out as one
    // declaration holding both functions; two families each writing a whole `transform` would let
    // the cascade pick one and drop the other, silently, which is the failure `translate-x`/
    // `translate-y` had.
    //
    // ⚠ <b>And it is a list this engine can only partly spell, which is why the assembler names one
    // slot rather than v4's five.</b> `TransformReader.Functions` refuses a list outright if any one
    // function in it is unreadable — deliberately, because a card flip read as the two flat halves
    // of a `rotateX rotateY` pair is a picture, and a wrong one. So writing v4's whole
    // `rotateX(…) rotateY(…) rotateZ(…) skewX(…) skewY(…)` today would make `rotate-z-45` emit a
    // declaration the engine drops *whole*: the family would resolve, cascade and do nothing. A slot
    // joins this assembler when its function parses, and not before.

    /// <summary>How far a <c>transform</c> spins the box about the axis normal to the screen.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The angle, not the whole <c>rotateZ(…)</c>, which is where this diverges from v4's
    ///         own fragment — and the divergence buys negation.</b> Tailwind writes
    ///         <c>--tw-rotate-z: rotateZ(45deg)</c> and assembles the bare references.
    ///         <c>UtilityFamilies.TryNegate</c> flips a resolved declaration by prefixing a minus and
    ///         refuses any value that does not begin with a digit, so a fragment holding a function
    ///         call could never spell <c>-rotate-z-45</c> — the gap <see cref="Vixen.Ui.Styling.Utilities.ValueKind.Angle" />
    ///         records for <c>bg-linear-*</c>, where it is unavoidable because the value is a whole
    ///         gradient. Here it is avoidable: the function lives in the assembler and the angle lives
    ///         in the fragment, so the sign is on a number. Fragment names never appear in markup —
    ///         see <see cref="Prefix" /> — so the shape is this layer's to choose.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>0deg</c> and not <c>0</c>, for <see cref="Blur" />'s reason and with its
    ///         consequence.</b> The initial is substituted <i>inside</i> a function, and
    ///         <c>rotateZ(0)</c> is not a value CSS has: <c>TransformReader</c>'s angle parser refuses
    ///         a bare number, and a refused function refuses the whole list — so the unit here is what
    ///         keeps an element that carries no rotation at all from dropping its <c>transform</c>.
    ///     </para>
    /// </remarks>
    public const string RotateZ = Prefix + "rotate-z";

    // ── The numeric figures ─────────────────────────────────────────────────────────────────
    //
    // ⚠ <b>Five fragments for nine classes, and the grouping is CSS's rather than a compression.</b>
    // CSS Fonts 4 § 6.6 makes <c>font-variant-numeric</c> a list of *at most one keyword from each
    // of four independent sets* plus the two flags — a figure may be lining or oldstyle and cannot
    // be both, so <c>lining-nums oldstyle-nums</c> is not a thing an author can mean. A fragment per
    // class would let them both be set and would emit an invalid declaration; a fragment per set
    // makes the later class win *within* its set and leave the others alone, which is exactly what
    // the property's own grammar says. It is also what v4 emits, for the same reason.
    //
    // ⚠ <b>Their initial is the empty string, which is the first of these that resolves to nothing
    // rather than to an identity.</b> A translation unset is <c>0px</c> and a scale unset is
    // <c>1</c>, because those properties need a value; this one needs a *shorter list*, and CSS's
    // way of writing "no tokens at all" is the empty fallback — <c>var(--tw-ordinal,)</c>, which
    // `VarSubstitution` already distinguishes from a missing fallback. So an element carrying only
    // `tabular-nums` computes `font-variant-numeric` as one keyword with four empty slots around
    // it, and `UiDocument.NumericFeatures` splits on spaces and finds one keyword.

    /// <summary>The <c>ordinal</c> flag, which is on or absent.</summary>
    public const string Ordinal = Prefix + "ordinal";

    /// <summary>The <c>slashed-zero</c> flag, which is on or absent.</summary>
    public const string SlashedZero = Prefix + "slashed-zero";

    /// <summary>Whichever of <c>lining-nums</c> and <c>oldstyle-nums</c> was written last.</summary>
    public const string NumericFigure = Prefix + "numeric-figure";

    /// <summary>Whichever of <c>proportional-nums</c> and <c>tabular-nums</c> was written last.</summary>
    public const string NumericSpacing = Prefix + "numeric-spacing";

    /// <summary>Whichever of <c>diagonal-fractions</c> and <c>stacked-fractions</c> was written last.</summary>
    public const string NumericFraction = Prefix + "numeric-fraction";

    // ── The ring ────────────────────────────────────────────────────────────────────────────
    //
    // ⚠ <b>A ring is a <c>box-shadow</c>, not an outline, and Vixen emitted <c>outline-color</c> for
    // it — a property <i>no</i> version of Tailwind has ever emitted for this family.</b> Worth being
    // exact about, because `docs/plan/43-web-styling-parity.md` § D5 records it as "v3's reading" and
    // that is not right either: v3's `ring-blue-500` set `--tw-ring-color` and v3's ring was already a
    // box-shadow — the shadow is what v3 *introduced* the family for. `outline-color` was this
    // engine's own invention, so `ring-*` was the same failure as `grid-cols-3` and `--scale`: an
    // emission no engine anywhere could consume, sitting under an `InertProperties.txt` line that
    // correctly said "nothing reads this" and was therefore never going to be the thing that told
    // anybody. A reader for `outline-color` would have closed the debt and changed nothing.
    //
    // Two fragments and one property, exactly like the translation above and for the same reason:
    // v4 writes the width and the colour as separate classes — `ring-2 ring-accent` — and one
    // declaration per class would let whichever rule the cascade picked last zero the other half.

    /// <summary>How thick a ring is, as a length.</summary>
    /// <remarks>
    ///     ⚠ <b>The ring's width is a <i>spread</i>, which is why it costs the layout nothing.</b>
    ///     `DrawListBuilder.EmitShadow` folds a spread into the command's rectangle and its radius, so
    ///     `0 0 0 2px` is a rounded box two points larger than the border box in every direction,
    ///     painted behind it. That is precisely what an outline is — outside the box, and invisible to
    ///     layout — which is why this family needed no new draw path and no fourth border edge.
    /// </remarks>
    public const string RingWidth = Prefix + "ring-width";

    /// <summary>What colour it is.</summary>
    /// <remarks>
    ///     ⚠ <b>The initial is <c>currentcolor</c>, which is v4's, and it is the one part of this
    ///     family that needed something from the engine.</b> A ring with a width and no colour is the
    ///     commonest way the class is written — `ring-2` on a focused control — and any concrete
    ///     initial would have been a colour nobody chose. `transparent` would have been worse than
    ///     wrong: `ring-2` would resolve, cascade, and paint nothing, which is the "looks like it
    ///     worked" failure this whole table exists to avoid. So <c>EmitShadow</c> learned the keyword
    ///     instead, resolving it against <c>UiDocument.ForegroundOf</c> — the same answer CSS Color 4
    ///     § 6.2 gives it, and the same one an icon's <c>IconPaintKind.Foreground</c> already got.
    /// </remarks>
    public const string RingColor = Prefix + "ring-color";

    // ── The filter ──────────────────────────────────────────────────────────────────────────
    //
    // ⚠ <b>One fragment and one property, which looks like it did not need the mechanism at all —
    // and the reason it does is <i>the next</i> filter function rather than this one.</b> CSS's
    // `filter` is an ordered list, so `blur-2 brightness-50` has to come out as one declaration
    // holding both functions in the right order; two families each emitting a whole `filter` would
    // let the cascade pick one and drop the other, silently, which is exactly the failure
    // `translate-x`/`translate-y` had. Building it as a fragment now means the second function is a
    // constant and a slot in `Filter()`, not a rewrite of the first.
    //
    // ⚠ <b>And it is the fix for `--blur`.</b> That name was this engine's own invention — not CSS,
    // not a fragment, assembled by nothing — so `blur-2` resolved, cascaded, and parked a length
    // where no engine would ever look for it. `InertProperties.txt` recorded the debt against #28 and
    // could not say *that*, because a property nothing emits and a property nothing reads are
    // indistinguishable from the gate's side. The same shape as `--scale`, `--rotate` and
    // `grid-cols-3`, and closed the same way the translation was: give it a prefix and an assembler.

    /// <summary>How far a <c>filter: blur()</c> spreads, as a Gaussian standard deviation.</summary>
    /// <remarks>
    ///     ⚠ <b><c>0px</c> and not <c>0</c>, for the reason <see cref="TranslateX" />'s initial gives
    ///     at length</b> — legibility rather than interpolation. It matters slightly more here,
    ///     because the initial is substituted <i>inside</i> a function: a bare zero would generate
    ///     <c>filter: blur(0)</c>, which is valid CSS and reads like a length somebody forgot the unit
    ///     on.
    /// </remarks>
    public const string Blur = Prefix + "blur";

    // ── The seven colour functions ──────────────────────────────────────────────────────────
    //
    // ⚠ <b>Seven fragments and one property, and this is the case the paragraph above `Blur` said
    // was coming.</b> `filter` is an ordered list, so `grayscale blur-2 brightness-125` has to come
    // out as one declaration holding three functions in a fixed order — and eight families each
    // emitting a whole `filter` would let the cascade pick one and drop the other seven, silently.
    // The fragments are what make them compose; `Filter()` is where the order is decided.
    //
    // ⚠ <b>The order in `Filter()` is Tailwind v4's own and is *not* the order the classes are
    // written in, which is a real limit rather than an oversight.</b> CSS applies the list left to
    // right, so `invert brightness-200` and `brightness-200 invert` are different pictures; a
    // utility system whose classes are unordered — `class="invert brightness-200"` and
    // `class="brightness-200 invert"` are the same element — cannot express both. v4 fixes the order
    // in its assembler and so does this. Someone who needs the other order writes the `filter`
    // declaration by hand, which is what the arbitrary-property syntax is for.

    /// <summary>How much a <c>filter: brightness()</c> scales the colour. One is unchanged.</summary>
    public const string Brightness = Prefix + "brightness";

    /// <summary>How much a <c>filter: contrast()</c> pushes away from mid grey. One is unchanged.</summary>
    public const string Contrast = Prefix + "contrast";

    /// <summary>How far a <c>filter: grayscale()</c> drains the colour. Zero is unchanged.</summary>
    public const string Grayscale = Prefix + "grayscale";

    /// <summary>How far a <c>filter: invert()</c> flips the colour. Zero is unchanged.</summary>
    public const string Invert = Prefix + "invert";

    /// <summary>How much a <c>filter: saturate()</c> scales the distance from grey. One is unchanged.</summary>
    public const string Saturate = Prefix + "saturate";

    /// <summary>How far a <c>filter: sepia()</c> ages the colour. Zero is unchanged.</summary>
    public const string Sepia = Prefix + "sepia";

    /// <summary>How far a <c>filter: hue-rotate()</c> turns the hue. Zero is unchanged.</summary>
    public const string HueRotate = Prefix + "hue-rotate";

    /// <summary>A <c>filter: drop-shadow()</c>'s arguments: two or three lengths and a colour.</summary>
    /// <remarks>
    ///     ⚠ <b>The ninth function, and the only one whose fragment holds more than a number.</b>
    ///     <c>drop-shadow</c> takes an offset, a blur and a colour that are chosen together — see
    ///     <see cref="ThemeTokens.DropShadow" /> — so splitting them into four fragments would let a
    ///     stylesheet compose a height nobody designed. It is still the <i>arguments</i> and not the
    ///     function, for the reason <see cref="Blur" /> gives: <see cref="Filter" /> writes the
    ///     function name, so the initial value can be a shadow rather than an empty string.
    /// </remarks>
    public const string DropShadow = Prefix + "drop-shadow";

    // ── The backdrop's nine ─────────────────────────────────────────────────────────────────
    //
    // ⚠ <b>Nine more fragments and a second assembler, and <i>not</i> nine more slots in the first
    // one.</b> `backdrop-filter` is a different property from `filter` and an element may carry both:
    // `filter: grayscale(1)` greys the panel and `backdrop-filter: blur(8px)` blurs the scene under
    // it, which is a real and common pair. Sharing the fragments would make `blur-2 backdrop-blur-8`
    // impossible to express — the second would overwrite the first's length — and sharing the
    // assembler would emit each function into both declarations.
    //
    // ⚠ <b>Nine and not eight, because this list has `opacity()` where the other has
    // `drop-shadow()`.</b> `backdrop-opacity-*` is one of Tailwind's ten backdrop roots and
    // `backdrop-drop-shadow-*` is not one of them at all — a shadow of the backdrop would be a
    // silhouette composited under a picture that is already behind everything. `DrawListBuilder.One`
    // refuses each of them in the other's property for exactly that asymmetry.

    /// <summary>How far a <c>backdrop-filter: blur()</c> spreads. As <see cref="Blur" />.</summary>
    public const string BackdropBlur = Prefix + "backdrop-blur";

    /// <summary>How much a <c>backdrop-filter: brightness()</c> scales the colour. One is unchanged.</summary>
    public const string BackdropBrightness = Prefix + "backdrop-brightness";

    /// <summary>How much a <c>backdrop-filter: contrast()</c> pushes away from mid grey. One is unchanged.</summary>
    public const string BackdropContrast = Prefix + "backdrop-contrast";

    /// <summary>How far a <c>backdrop-filter: grayscale()</c> drains the colour. Zero is unchanged.</summary>
    public const string BackdropGrayscale = Prefix + "backdrop-grayscale";

    /// <summary>How far a <c>backdrop-filter: hue-rotate()</c> turns the hue. Zero is unchanged.</summary>
    public const string BackdropHueRotate = Prefix + "backdrop-hue-rotate";

    /// <summary>How far a <c>backdrop-filter: invert()</c> flips the colour. Zero is unchanged.</summary>
    public const string BackdropInvert = Prefix + "backdrop-invert";

    /// <summary>How far a <c>backdrop-filter: opacity()</c> fades the backdrop. One is unchanged.</summary>
    /// <remarks>
    ///     ⚠ <b>The one function in either list that is not a colour matrix and not a Gaussian.</b>
    ///     <c>UiColorMatrix</c> has three rows and cannot scale alpha, so this lands on
    ///     <c>UiBackdrop.Alpha</c> and rides the backdrop quad's own vertex alpha — the same place a
    ///     <c>drop-shadow</c>'s colour alpha rides, and for the same reason.
    /// </remarks>
    public const string BackdropOpacity = Prefix + "backdrop-opacity";

    /// <summary>How much a <c>backdrop-filter: saturate()</c> scales the distance from grey.</summary>
    public const string BackdropSaturate = Prefix + "backdrop-saturate";

    /// <summary>How far a <c>backdrop-filter: sepia()</c> ages the colour. Zero is unchanged.</summary>
    public const string BackdropSepia = Prefix + "backdrop-sepia";

    static readonly Dictionary<string, string> Initials = new(StringComparer.Ordinal) {
        [GradientFrom] = "transparent",
        [GradientVia] = "transparent",
        [GradientTo] = "transparent",
        [GradientFromPosition] = "0%",
        [GradientViaPosition] = "50%",
        [GradientToPosition] = "100%",

        // See the mask fragments' own remark for why the near end is opaque where the gradient's is
        // not: a mask that defaulted to `transparent` at both ends would erase whatever set it.
        [MaskFrom] = "black",
        [MaskTo] = "transparent",
        [MaskFromPosition] = "0%",
        [MaskToPosition] = "100%",
        [MaskLinearAngle] = "180deg",
        [MaskConicAngle] = "0deg",
        [MaskRadialPosition] = "center",

        // ⚠ v4's own number, and that is the whole of why it is this one. A different default would
        // make `class="transition"` mean a different animation in the two systems, which is a
        // divergence nobody would find by reading either sheet. There is deliberately no companion
        // for the timing function: CSS's initial value is already `ease`, so `transition` gets v4's
        // curve by saying nothing, and saying it would overwrite the `ease-*` beside it.
        [TransitionDuration] = "150ms",

        // See the mask layers' own remark: an opaque layer is the identity under `intersect`, which
        // is the operator every mask utility emits, so a slot nobody filled costs nothing and says
        // nothing. The four edges are added below, in the static constructor, because there are
        // twenty of them and a loop is one place to get it wrong instead of twenty.
        [MaskLinear] = MaskOpaque,
        [MaskRadial] = MaskOpaque,
        [MaskConic] = MaskOpaque,

        // ⚠ <b><c>0px</c> rather than <c>0</c>, and the unit is <i>not</i> doing the work it looks
        // like it is doing — measured, because the plausible reason is wrong.</b> The obvious story is
        // that <see cref="Vixen.Ui.Styling.StyleValue.CanInterpolate" /> compares units, so a
        // translation that read `0 0` at rest and `8px 0px` under the pointer would be two lists the
        // animator declines and every composed translation in the engine would jump. It does compare
        // units, and it declines nothing here: that method opens with an explicit "zero belongs to
        // every unit" rule, because `from { width: 0 } to { width: 100px }` is the commonest animation
        // there is. Both spellings interpolate, identically, and it was checked rather than reasoned
        // about. So the unit is only legibility — a generated sheet that reads `translate: 8px 0px`
        // says what it is; `8px 0` reads like a mistake — and the next person to wonder whether it is
        // load-bearing has the answer here instead of the argument.
        [TranslateX] = "0px",
        [TranslateY] = "0px",

        // ⚠ <b>The empty token stream, and it is a value rather than the absence of one.</b>
        // `var(--tw-ordinal,)` with an empty fallback is legal CSS and is what
        // `VarSubstitution.Substitute` distinguishes from `var(--tw-ordinal)` — the second is
        // invalid at computed-value time and would throw the whole assembled declaration away the
        // moment an element set four of the five slots, which is every element that uses this
        // family. Nothing else in this table needs it, because nothing else assembles a *list whose
        // length varies*.
        [Ordinal] = string.Empty,
        [SlashedZero] = string.Empty,
        [NumericFigure] = string.Empty,
        [NumericSpacing] = string.Empty,
        [NumericFraction] = string.Empty,

        // See `RotateZ`: the unit is load-bearing here rather than merely legible, because the
        // initial is substituted inside `rotateZ(…)` and a bare zero makes the whole list invalid.
        [RotateZ] = "0deg",

        // ⚠ <b>One, and this is the pair where the identity is not zero — which is the whole reason
        // these are separate fragments rather than a second use of the translations'.</b> A missing
        // translation is no movement, which is zero; a missing scale is no growth, which is one. A
        // fragment table that defaulted these to <c>0</c> would make <c>scale-x-150</c> alone collapse
        // the element vertically to nothing, and <c>scale-0</c> is a real class so the result would
        // look like a feature rather than a bug.
        //
        // ⚠ <b>Unitless rather than <c>100%</c>, unlike the family that fills them.</b> `scale-x-150`
        // writes `150%` into its own fragment, because that is what v4 emits and what
        // `TransformReader` reads as a ratio; the *default* is the bare number because a percentage
        // and a number are both legal here and the bare one cannot be misread as a length.
        [ScaleX] = "1",
        [ScaleY] = "1",

        // ⚠ <b>Zero, so that a colour on its own paints nothing — which is what v4 does too.</b>
        // `ring-accent` with no width emits only `--tw-ring-color` in Tailwind and therefore no
        // shadow at all; here it emits the assembly with a zero spread, and `EmitShadow` produces a
        // shadow the exact size of the border box that the background then covers. Same outcome,
        // reached differently, and the alternative — a non-zero default width — would make a bare
        // `ring-accent` draw a ring nobody asked for.
        [RingWidth] = "0px",
        [RingColor] = "currentcolor",
        [Blur] = "0px",

        // ⚠ <b>Each initial is the identity of <i>its own</i> function, which is one for four of
        // them and zero for three, and getting one of the seven the wrong way round is a filter
        // nobody wrote being applied to every element that wrote any of the others.</b> That is the
        // failure mode this table exists to make impossible and the reason the values are here
        // rather than inside `Filter()`: `brightness(0)` is black and `grayscale(1)` is grey, so a
        // `grayscale` on its own would turn the box black, and a `brightness-125` on its own would
        // turn it grey, and both would look like the other family being broken.
        [Brightness] = "1",
        [Contrast] = "1",
        [Grayscale] = "0",
        [Invert] = "0",
        [Saturate] = "1",
        [Sepia] = "0",

        // ⚠ <b>A <i>transparent</i> shadow, because <c>drop-shadow</c> is the one function with no
        // length that means "unchanged".</b> Every other initial above is a number the function maps
        // to itself; the nearest thing here would be a zero offset and a zero blur, which is the
        // element painted a second time exactly under itself and is very much not the identity. A
        // shadow nobody can see is, and `DrawListBuilder.Settle` drops it before it costs a surface —
        // see `UiDropShadow.IsInvisible`, which is the reader.
        //
        // ⚠ Two lengths and not three. `drop-shadow(0 0 transparent)` and `drop-shadow(0 0 0
        // transparent)` are the same shadow, and the grammar takes two lengths as readily as three —
        // so the shorter one is written, because a third zero reads like a blur somebody meant to
        // fill in.
        [DropShadow] = "0 0 transparent",

        // ⚠ <c>0deg</c> and not <c>0</c>, and here the unit is load-bearing rather than legibility.
        // <c>hue-rotate()</c> takes an <c>&lt;angle&gt;</c>, and `StyleValueParser` refuses a bare
        // number for it — see `ParseFunction`, which will not guess degrees. A plain zero would make
        // the whole assembled declaration invalid for every element that set none of the seven,
        // which is every element that writes a `blur-*`.
        [HueRotate] = "0deg",

        // ⚠ <b>The backdrop's nine, and the values are the same identities for the same reason</b> —
        // a second table rather than a second use of the first, because they are a second set of
        // fragments. See the constants: `filter` and `backdrop-filter` are different properties and
        // one element may set both, so `blur-2 backdrop-blur-8` has to be two lengths and not one.
        // ⚠ <c>opacity</c>'s identity is <b>one</b> and not zero, which is the one place a reader
        // coming from the seven above will guess wrong: `opacity(0)` erases the backdrop entirely,
        // which every element carrying any `backdrop-*` class would then do.
        [BackdropBlur] = "0px",
        [BackdropBrightness] = "1",
        [BackdropContrast] = "1",
        [BackdropGrayscale] = "0",
        [BackdropHueRotate] = "0deg",
        [BackdropInvert] = "0",
        [BackdropOpacity] = "1",
        [BackdropSaturate] = "1",
        [BackdropSepia] = "0"
    };

    static readonly List<string> Names;

    static UtilityComposition() {
        // ⚠ Before the snapshot below, which is the whole of why this loop is here rather than beside
        // the table: `Names` is what `IsFragment` and the parity gate read, and twenty fragments
        // registered after it would be twenty properties the gate calls unexplained.
        foreach (var edge in MaskEdges) {
            Initials[MaskEdge(edge)] = MaskOpaque;
            Initials[MaskEdgeFrom(edge)] = "black";
            Initials[MaskEdgeTo(edge)] = "transparent";
            Initials[MaskEdgeFromPosition(edge)] = "0%";
            Initials[MaskEdgeToPosition(edge)] = "100%";
        }

        // Two passes, because one fragment's initial value is written in terms of the others. The
        // stop list is the only one, and the alternative — a lazily resolved table — would buy
        // generality nothing has asked for and lose the invariant that `Reference` is a pure lookup.
        Initials[GradientStops] = StopList(via: false);
        Names = [.. Initials.Keys.Order(StringComparer.Ordinal)];
    }

    /// <summary>Every fragment a utility family can set, ordered.</summary>
    public static IReadOnlyList<string> Fragments => Names;

    /// <summary>Whether a property is a fragment rather than something a consumer reads.</summary>
    /// <param name="property">The CSS property name.</param>
    /// <returns>Whether it is one of <see cref="Fragments" />.</returns>
    /// <remarks>
    ///     Registered membership rather than a prefix test, so that a <c>--tw-</c> name nothing
    ///     declared is not quietly treated as composed. The parity gate leans on this: a fragment's
    ///     verdict is its assembler's, and a property that only <i>looks</i> like a fragment would
    ///     inherit an explanation it has not earned.
    /// </remarks>
    public static bool IsFragment(string property) {
        ArgumentNullException.ThrowIfNull(property);
        return Initials.ContainsKey(property);
    }

    /// <summary>What a fragment is worth when nothing has set it.</summary>
    /// <param name="fragment">The fragment name.</param>
    /// <returns>Its initial value.</returns>
    /// <exception cref="ArgumentException">The name is not a fragment.</exception>
    public static string InitialValueOf(string fragment) {
        ArgumentNullException.ThrowIfNull(fragment);

        return Initials.TryGetValue(fragment, out var initial)
            ? initial
            : throw new ArgumentException($"'{fragment}' is not a composition fragment", nameof(fragment));
    }

    /// <summary>How an assembler refers to a fragment: a <c>var()</c> carrying its initial value.</summary>
    /// <param name="fragment">The fragment name.</param>
    /// <returns><c>var(--tw-…, initial)</c>.</returns>
    /// <exception cref="ArgumentException">The name is not a fragment.</exception>
    /// <remarks>
    ///     ⚠ <b>The only supported way to mention a fragment, and the reason is that the bare form is
    ///     both shorter and wrong.</b> <c>var(--tw-gradient-to)</c> reads fine and drops the entire
    ///     declaration the moment nobody wrote a <c>to-*</c>. Routing every reference through here
    ///     means the fallback cannot be forgotten in one assembler out of five, which is how this
    ///     class of bug actually arrives.
    /// </remarks>
    public static string Reference(string fragment) => $"var({fragment}, {InitialValueOf(fragment)})";

    /// <summary>The three-layer <c>mask-image</c> every <c>mask-*</c> utility emits.</summary>
    /// <returns>The <c>mask-image</c> value.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The same string from every family, which is what makes the shapes compose.</b>
    ///         `mask-radial-from-50% mask-conic-to-80%` is two classes that have to end up as one
    ///         `mask-image` naming both — the situation the fragment mechanism exists for, and the
    ///         one `translate-x-2 translate-y-4` is in. Written whole by each family instead,
    ///         whichever rule the cascade picked last would win outright and the other shape would
    ///         vanish.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Three layers always, even for a class that fills one of them.</b> The other two
    ///         resolve to <see cref="MaskOpaque" />, which is the identity under the <c>intersect</c>
    ///         the same families emit, and <c>DrawListBuilder.Reduce</c> drops them before a group is
    ///         opened. Emitting only the layer that was set would need each family to know which
    ///         others were present, which is exactly what a stylesheet cannot know.
    ///     </para>
    /// </remarks>
    public static string MaskLayers() =>
        $"{Reference(MaskLinear)}, {Reference(MaskRadial)}, {Reference(MaskConic)}";

    /// <summary>The four-layer value the edge ramps give <see cref="MaskLinear" />.</summary>
    /// <remarks>
    ///     ⚠ <b>The edges take over the linear slot rather than getting a fourth of their own, which
    ///     is Tailwind v4's arrangement and is also the only one that fits.</b> A `mask-t-*` beside a
    ///     `mask-linear-*` is two linear masks and CSS has one linear slot; giving the edges their
    ///     own would make `mask-image` seven layers deep before anybody wrote a second class. What it
    ///     costs is that `mask-t-from-50% mask-linear-45` is a conflict — the two write the same
    ///     fragment and the cascade picks one — which is the behaviour Tailwind has.
    /// </remarks>
    public static string MaskEdgeLayers() =>
        string.Join(", ", MaskEdges.Select(edge => Reference(MaskEdge(edge))));

    /// <summary>One edge's ramp, as a gradient running towards that edge.</summary>
    /// <param name="edge">One of <see cref="MaskEdges" />.</param>
    /// <returns>The gradient text.</returns>
    /// <remarks>
    ///     ⚠ <b><c>to top</c> and not <c>to bottom</c> for <c>mask-t-*</c>, and the direction is the
    ///     part that is easy to get backwards.</b> `mask-t-from-50%` fades the element out *at the
    ///     top*: it is opaque from the bottom up to the halfway mark and transparent by the top edge.
    ///     A gradient written `to bottom` with the same stops fades the bottom instead, which is a
    ///     perfectly plausible picture and the wrong one.
    /// </remarks>
    public static string MaskEdgeImage(string edge) {
        var from = $"{Reference(MaskEdgeFrom(edge))} {Reference(MaskEdgeFromPosition(edge))}";
        var to = $"{Reference(MaskEdgeTo(edge))} {Reference(MaskEdgeToPosition(edge))}";

        return $"linear-gradient(to {edge}, {from}, {to})";
    }

    /// <summary>One mask assembler: the shape, its geometry, and the two-stop ramp.</summary>
    /// <param name="shape"><c>linear</c>, <c>radial</c> or <c>conic</c>.</param>
    /// <param name="geometry">What goes before the stops, or nothing.</param>
    /// <returns>The <c>mask-image</c> value.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>No <c>in oklab</c>, and its absence is deliberate where the gradient assembler's
    ///         presence is.</b> An interpolation space says what is halfway between two <i>colours</i>,
    ///         and a mask reads only alpha — every space the engine has lerps the alpha channel
    ///         plainly, so a hint here would be a token that changes nothing and invites the reader to
    ///         think it might. See <c>UiMask</c>, which carries no space for the same reason.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two stops and no <c>via</c>.</b> The engine's mask carries a middle stop and
    ///         nothing generates one: Tailwind has no <c>mask-*-via-*</c>, so adding a family here
    ///         would be inventing API. A hand-written <c>mask-image</c> with three stops is read
    ///         correctly all the same.
    ///     </para>
    /// </remarks>
    public static string MaskImage(string shape, string geometry) {
        var stops = $"{Reference(MaskFrom)} {Reference(MaskFromPosition)}, {Reference(MaskTo)} {Reference(MaskToPosition)}";

        return geometry.Length == 0
            ? $"{shape}-gradient({stops})"
            : $"{shape}-gradient({geometry}, {stops})";
    }

    /// <summary>The comma-separated stop list a gradient function takes.</summary>
    /// <param name="via">Whether the middle stop is included.</param>
    /// <returns>The stop list text.</returns>
    /// <remarks>
    ///     Each stop is a colour and a position, both of them fragment references, so a stop list is
    ///     well-formed however few of the six colours and positions anybody actually wrote.
    /// </remarks>
    public static string StopList(bool via) {
        var stops = new List<string> {
            $"{Reference(GradientFrom)} {Reference(GradientFromPosition)}"
        };

        if (via) {
            stops.Add($"{Reference(GradientVia)} {Reference(GradientViaPosition)}");
        }

        stops.Add($"{Reference(GradientTo)} {Reference(GradientToPosition)}");
        return string.Join(", ", stops);
    }

    /// <summary>The two-axis value a <c>translate</c> declaration takes.</summary>
    /// <returns>The assembled value.</returns>
    /// <remarks>
    ///     ⚠ <b>Both <c>translate-x</c> and <c>translate-y</c> emit this same constant, so both are
    ///     assemblers as well as contributors — which the gradient families are not.</b> The
    ///     alternative is Tailwind v3's shape, a separate <c>transform</c> class that has to be
    ///     present for either axis to do anything; v4 dropped it because the class was forgotten
    ///     constantly and its absence looked exactly like the utility being broken. Emitting the
    ///     assembly from both means <c>translate-x-2</c> alone works, and <c>translate-x-2
    ///     translate-y-4</c> composes, because the two rules write the same declaration and differ
    ///     only in which fragment they set beside it.
    /// </remarks>
    public static string Translation() => $"{Reference(TranslateX)} {Reference(TranslateY)}";

    /// <summary>The two-axis value a <c>scale</c> declaration takes.</summary>
    /// <returns>The assembled value.</returns>
    /// <remarks>
    ///     <see cref="Translation" />'s arrangement and its argument word for word: both
    ///     <c>scale-x-*</c> and <c>scale-y-*</c> emit this same constant, so either works alone and
    ///     the two compose. ⚠ Two components always, never one — a one-component <c>scale</c> is
    ///     defined as <i>uniform</i>, so emitting <c>scale: var(--tw-scale-x, 1)</c> for a lone
    ///     <c>scale-x-150</c> would stretch both axes and be exactly the bug the fragment is for.
    /// </remarks>
    public static string Scaling() => $"{Reference(ScaleX)} {Reference(ScaleY)}";

    /// <summary>The keyword list a <c>font-variant-numeric</c> declaration takes.</summary>
    /// <returns>The assembled value.</returns>
    /// <remarks>
    ///     <para>
    ///         <see cref="Translation" />'s arrangement, with one difference that is the whole reason
    ///         the family needed the mechanism: <b>the assembled value is a list whose <i>length</i>
    ///         varies</b>, so the slots nobody filled have to contribute no tokens at all rather than
    ///         an identity. Four empty <c>var()</c> fallbacks and one keyword is what
    ///         <c>tabular-nums</c> alone computes to, and `UiDocument.NumericFeatures` splits on
    ///         spaces, so the empties cost nothing downstream.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The order is CSS Fonts 4 § 6.6's order and not the classes' or the alphabet's</b>,
    ///         which matters only because a generated sheet is read by people: the property is
    ///         order-independent to a parser — every keyword names a different set — so this is the
    ///         one place in this file where the order is legibility rather than correctness. Contrast
    ///         <see cref="Transform" />, where the order is the composition.
    ///     </para>
    /// </remarks>
    public static string NumericFigures() =>
        $"{Reference(Ordinal)} {Reference(SlashedZero)} {Reference(NumericFigure)} "
        + $"{Reference(NumericSpacing)} {Reference(NumericFraction)}";

    /// <summary>The function list a <c>transform</c> declaration takes.</summary>
    /// <returns>The assembled value.</returns>
    /// <remarks>
    ///     <para>
    ///         <see cref="Translation" />'s arrangement: the family that sets the fragment emits this
    ///         beside it, so one class works alone and several compose.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One function, where v4 writes five — and the missing four are missing on purpose,
    ///         not pending.</b> See the block above <see cref="RotateZ" />: a <c>transform</c> naming
    ///         a function <c>TransformReader</c> cannot read is refused whole, so a slot added before
    ///         its parser would take the working rotation down with it. <c>rotateX</c>,
    ///         <c>rotateY</c>, <c>skewX</c> and <c>skewY</c> each join this string on the day the
    ///         reader accepts them; <c>skew</c> already parses, so its two are a family away rather
    ///         than a parser away.
    ///     </para>
    /// </remarks>
    public static string Transform() => $"rotateZ({Reference(RotateZ)})";

    /// <summary>The <c>box-shadow</c> a ring is.</summary>
    /// <returns>The assembled value.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>No offset and no blur — a ring is a spread and nothing else.</b>
    ///         <c>0 0 0 &lt;width&gt; &lt;colour&gt;</c> is v4's own shape for it, and it is what makes
    ///         the family free: <c>DrawListBuilder.EmitShadow</c> already folds a spread into the
    ///         rectangle and grows every corner radius by it, so the result is a rounded box sitting
    ///         outside the border box, painted before the background. An outline, without an
    ///         <c>outline</c> property and without a fourth border edge.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both <c>ring-&lt;width&gt;</c> and <c>ring-&lt;colour&gt;</c> emit this, so both
    ///         are assemblers</b> — the same arrangement as the two translations, and for the same
    ///         reason: v4 dropped v3's separate <c>transform</c>-style enabling class because its
    ///         absence looked exactly like the utility being broken.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A ring and a <c>shadow-*</c> on one element is still the known limit, and it is
    ///         this mechanism's now rather than the draw list's.</b> It used to be both: CSS layers
    ///         them by comma and <c>EmitShadow</c> refused a list outright. ⚠ <b>It does not any
    ///         more</b> — a list is a command each, painted last to first, and a hand-written
    ///         <c>box-shadow: a, b</c> in a <c>.vcss</c> draws both (`Rikarin/Vixen#279`). What is
    ///         left is here: the two families write the same property, so the cascade picks one and
    ///         the other is not applied. Composing them is v4's five-fragment shape —
    ///         <c>--tw-shadow</c>, <c>--tw-inset-shadow</c>, <c>--tw-ring-shadow</c>,
    ///         <c>--tw-inset-ring-shadow</c> and <c>--tw-ring-offset-shadow</c> assembled into one
    ///         comma list — which is a fragment table and no longer a draw path.
    ///     </para>
    /// </remarks>
    public static string Ring() => $"0 0 0 {Reference(RingWidth)} {Reference(RingColor)}";

    /// <summary>The <c>filter</c> declaration the eight filter families assemble into.</summary>
    /// <returns>The assembled value.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The function is written here and only the length is a fragment</b>, which is the
    ///         opposite way round from the ring, where the whole <c>0 0 0 w c</c> shape lives in the
    ///         assembler. It has to be: <c>filter</c>'s items are function calls, and a fragment
    ///         holding <c>blur(4px)</c> whole could not have an initial value that composes — the
    ///         empty string is not a filter, and <c>none</c> in the middle of a list is invalid. A
    ///         zero-length blur is the identity, so the initial can be a plain <c>0px</c> and an
    ///         unset fragment costs the list one function that does nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Eight functions, always all eight, and seven of them are doing nothing on almost
    ///         every element that carries this declaration.</b> That is what the initials in
    ///         <see cref="Initials" /> buy and it is deliberate: the alternative is emitting only the
    ///         functions somebody wrote, which a per-class generator cannot do — a <c>blur-2</c> and a
    ///         <c>grayscale</c> are two rules with two selectors, and neither knows the other exists.
    ///         See this class's opening remarks, which is the same argument the gradient stops make.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The order is Tailwind v4's and is fixed here, so <c>invert brightness-200</c> and
    ///         <c>brightness-200 invert</c> are the same picture where CSS would make them
    ///         different.</b> Classes on an element are a set, not a sequence, so no utility system
    ///         can offer both — v4 picks an order and documents it, and this picks the same one so
    ///         that a sheet ported from Tailwind renders the same. Someone who needs the other order
    ///         writes a <c>filter</c> declaration by hand.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>drop-shadow()</c> is the ninth and it is <i>last</i>, which is v4's order and
    ///         is also the only order this engine could execute.</b> Seven of the nine are a
    ///         per-pixel colour transform and the eighth is a Gaussian; a drop shadow is neither — it
    ///         is a blur of the alpha channel, offset, tinted and composited <i>under</i> the layer —
    ///         and it does not commute with the eighth. <c>blur(σ) drop-shadow(τ)</c> casts the shadow
    ///         of the blurred element and the reverse blurs a picture that already has a shadow under
    ///         it, so where a colour matrix and a Gaussian may be run in whichever order is cheap,
    ///         these two may not. Being written last here fixes the choice for every class in the
    ///         engine; see <c>UiLayer.Shadow</c>, where both executors pin the seam.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It was deliberately absent from this string until there was a reader, and that
    ///         was not caution for its own sake.</b> <c>DrawListBuilder.Filter</c> refuses a list
    ///         carrying any function it cannot execute, so adding <c>drop-shadow</c> here a day early
    ///         would have turned off every other filter in the engine — silently, on every element
    ///         carrying a <c>blur-*</c> or a <c>grayscale</c>. That is why the reader landed first.
    ///     </para>
    /// </remarks>
    public static string Filter() =>
        $"blur({Reference(Blur)}) brightness({Reference(Brightness)}) contrast({Reference(Contrast)}) "
        + $"grayscale({Reference(Grayscale)}) hue-rotate({Reference(HueRotate)}) invert({Reference(Invert)}) "
        + $"saturate({Reference(Saturate)}) sepia({Reference(Sepia)}) drop-shadow({Reference(DropShadow)})";

    /// <summary>The <c>backdrop-filter</c> declaration the ten backdrop families assemble into.</summary>
    /// <returns>The assembled value.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A second assembler and not nine more slots in <see cref="Filter" />, because
    ///         <c>filter</c> and <c>backdrop-filter</c> are different properties an element may
    ///         legitimately carry both of.</b> A single declaration cannot be two, and the picture the
    ///         pair describes — a grey panel over a blurred scene — is the ordinary use of the
    ///         feature rather than an exotic one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Nine functions in Tailwind v4's own order, which puts <c>opacity()</c> between
    ///         <c>invert()</c> and <c>saturate()</c> and has no <c>drop-shadow()</c> at the end.</b>
    ///         Everything <see cref="Filter" />'s remarks say about why the order is fixed here rather
    ///         than taken from the classes applies word for word: classes on an element are a set and
    ///         CSS's list is a sequence, so no utility system can offer both orders and v4 documents
    ///         the one it picks.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>opacity()</c>'s presence here is the reason <c>StyleValueParser</c> learned the
    ///         function at all</b>, and it is refused inside a plain <c>filter</c> —
    ///         <c>DrawListBuilder.One</c> is where that asymmetry is stated. It cannot ride the colour
    ///         matrix the other eight compose into, because a three-row matrix has no alpha row; see
    ///         <c>UiBackdrop.Alpha</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The unprefixed property only, where Tailwind emits <c>-webkit-backdrop-filter</c>
    ///         beside it.</b> That copy is for Safari and there is no Safari here, so emitting it would
    ///         put a declaration nothing can read into every generated sheet — see
    ///         <c>UtilityFamilies.BackdropAlongside</c>, which is where the choice is argued.
    ///     </para>
    /// </remarks>
    public static string BackdropFilter() =>
        $"blur({Reference(BackdropBlur)}) brightness({Reference(BackdropBrightness)}) "
        + $"contrast({Reference(BackdropContrast)}) grayscale({Reference(BackdropGrayscale)}) "
        + $"hue-rotate({Reference(BackdropHueRotate)}) invert({Reference(BackdropInvert)}) "
        + $"opacity({Reference(BackdropOpacity)}) saturate({Reference(BackdropSaturate)}) "
        + $"sepia({Reference(BackdropSepia)})";
}
