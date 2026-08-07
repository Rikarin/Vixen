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
    ///     ⚠ It also keeps them clear of <c>--blur</c>, <c>--rotate</c>, <c>--scale</c> and
    ///     <c>--translate-x</c>/<c>-y</c>, which are <i>not</i> fragments: nothing assembles them, so
    ///     they are placeholders parked in a name, and <c>InertProperties.txt</c> records them against
    ///     task #23 and #28. Giving them assemblers is what those tasks are.
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

    static readonly Dictionary<string, string> Initials = new(StringComparer.Ordinal) {
        [GradientFrom] = "transparent",
        [GradientVia] = "transparent",
        [GradientTo] = "transparent",
        [GradientFromPosition] = "0%",
        [GradientViaPosition] = "50%",
        [GradientToPosition] = "100%"
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
}
