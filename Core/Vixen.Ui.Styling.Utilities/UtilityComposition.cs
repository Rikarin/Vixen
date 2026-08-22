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

    static readonly Dictionary<string, string> Initials = new(StringComparer.Ordinal) {
        [GradientFrom] = "transparent",
        [GradientVia] = "transparent",
        [GradientTo] = "transparent",
        [GradientFromPosition] = "0%",
        [GradientViaPosition] = "50%",
        [GradientToPosition] = "100%",

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

        // ⚠ <b>Zero, so that a colour on its own paints nothing — which is what v4 does too.</b>
        // `ring-accent` with no width emits only `--tw-ring-color` in Tailwind and therefore no
        // shadow at all; here it emits the assembly with a zero spread, and `EmitShadow` produces a
        // shadow the exact size of the border box that the background then covers. Same outcome,
        // reached differently, and the alternative — a non-zero default width — would make a bare
        // `ring-accent` draw a ring nobody asked for.
        [RingWidth] = "0px",
        [RingColor] = "currentcolor",
        [Blur] = "0px"
    };

    static readonly List<string> Names;

    static UtilityComposition() {
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
    ///         ⚠ <b>A ring and a <c>shadow-*</c> on one element is the known limit, and it is the draw
    ///         list's rather than this mechanism's.</b> CSS layers them by comma and
    ///         <c>EmitShadow</c> refuses a list outright — deliberately, because drawing the first of
    ///         several and dropping the rest looks like it worked. Here the two families write the
    ///         same property, so the cascade picks one and the other is simply not applied. Composing
    ///         them needs the multi-shadow draw path that refusal is holding open, not another
    ///         fragment.
    ///     </para>
    /// </remarks>
    public static string Ring() => $"0 0 0 {Reference(RingWidth)} {Reference(RingColor)}";

    /// <summary>The <c>filter</c> declaration a <c>blur-*</c> assembles into.</summary>
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
    ///         ⚠ <b>What this does <i>not</i> yet compose with is the rest of <c>filter</c>.</b> There
    ///         is one function in the list because there is one function the engine reads — see
    ///         <c>DrawListBuilder.Blur</c>, which refuses a <c>filter</c> carrying anything else
    ///         rather than honouring the part it understands. A second family adds a constant, an
    ///         initial and a slot in this string; it does not change the shape.
    ///     </para>
    /// </remarks>
    public static string Filter() => $"blur({Reference(Blur)})";
}
