// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Ui.Styling.Utilities;

/// <summary>What a variant does to the rule it prefixes.</summary>
/// <param name="SelectorSuffix">Appended to the class selector — <c>:hover</c>, <c>[data-x]</c>.</param>
/// <param name="SelectorPrefix">Prepended, for the variants that need an ancestor — <c>.dark </c>.</param>
/// <param name="AtRule">An at-rule to wrap the whole thing in, such as a media query.</param>
public readonly record struct VariantEffect(string SelectorSuffix, string SelectorPrefix, string? AtRule);

/// <summary>Turns a variant prefix into what it does to the selector.</summary>
/// <remarks>
///     <para>
///         Variants are the reason a utility system scales past the trivial. Without them a hover
///         state means a hand-written rule, and the whole argument — that styling a new panel is
///         zero new CSS — falls over at the first button.
///     </para>
///     <para>
///         Three shapes, and they compose. Most are a <b>suffix</b> on the selector
///         (<c>hover:</c> → <c>:hover</c>). Breakpoints and <c>dark:</c> under the media strategy are
///         an <b>at-rule</b> around it. <c>group-*</c>, <c>peer-*</c> and <c>dark:</c> under the
///         class strategy need something <i>before</i> the element, which is the only reason
///         <see cref="VariantEffect.SelectorPrefix" /> exists.
///     </para>
///     <para>
///         ⚠ <b>A fourth shape is an at-rule that is not a media query</b>: <c>@sm:</c>,
///         <c>@max-lg:</c>, <c>@min-[30rem]:</c> and their <c>/name</c> forms wrap the rule in a
///         <c>@container</c> instead. Nothing about the mechanism had to change for it —
///         <see cref="VariantEffect.AtRule" /> is already a string and
///         <c>UtilityGenerator</c> already nests a chain of them — which is why the blocker was
///         never the wiring: it was that the <c>--container-*</c> scale did not exist, so the only
///         numbers <c>@sm</c> could have been resolved against were the breakpoints, and those are
///         a window's rather than a box's.
///     </para>
///     <para>
///         The arbitrary form <c>[&amp;>*]:</c> substitutes the selector for the <c>&amp;</c>, which
///         is the escape hatch that stops the variant list from having to be complete. It is also
///         why the class-name parser has to be bracket-aware: that variant contains a <c>&gt;</c>
///         and could contain a <c>:</c>.
///     </para>
/// </remarks>
public static class Variants {
    static readonly Dictionary<string, string> States = new(StringComparer.Ordinal) {
        ["hover"] = ":hover",
        ["focus"] = ":focus",
        ["focus-visible"] = ":focus-visible",
        ["focus-within"] = ":focus-within",
        ["active"] = ":active",
        ["disabled"] = ":disabled",
        ["enabled"] = ":enabled",
        ["checked"] = ":checked",
        ["first"] = ":first-child",
        ["last"] = ":last-child",
        ["only"] = ":only-child",
        ["odd"] = ":nth-child(2n+1)",
        ["even"] = ":nth-child(2n)"
    };

    /// <summary>The variants that are a pseudo-class on the element itself.</summary>
    /// <remarks>
    ///     ⚠ <b>Exposed so that a test can fail on a variant nobody tested, which is the shape of
    ///     failure this table has actually had.</b> Every entry below <c>hover</c> and <c>focus</c>
    ///     went untested end to end, and a whole variant family being inert while the suite stayed
    ///     green is not hypothetical here — every breakpoint was dead until the document started
    ///     handing the cascade a <see cref="MediaContext" />. A list the coverage test can enumerate
    ///     turns "someone remembered" into "the build checks", and it is also what
    ///     <c>group-*</c> and <c>peer-*</c> compose over, so one table drives all three.
    /// </remarks>
    public static IReadOnlyCollection<string> StateVariants => States.Keys;

    /// <summary>Works out what a variant does.</summary>
    /// <param name="variant">The variant, without its colon.</param>
    /// <param name="tokens">The theme, for breakpoints and the dark-mode strategy.</param>
    /// <param name="effect">Receives what it does.</param>
    /// <returns>Whether it is a variant this system knows.</returns>
    public static bool TryResolve(string variant, ThemeTokens tokens, out VariantEffect effect) {
        ArgumentNullException.ThrowIfNull(variant);
        ArgumentNullException.ThrowIfNull(tokens);

        effect = default;

        if (States.TryGetValue(variant, out var state)) {
            effect = new VariantEffect(state, string.Empty, null);
            return true;
        }

        if (tokens.Screens.TryGetValue(variant, out var width)) {
            // Min-width, so the breakpoints stack the way everybody expects: a `md:` rule applies at
            // `lg:` too unless something overrides it.
            effect = new VariantEffect(
                string.Empty,
                string.Empty,
                string.Create(CultureInfo.InvariantCulture, $"@media (min-width: {width.ToString("0.####", CultureInfo.InvariantCulture)}px)")
            );

            return true;
        }

        if (variant.Length > 1 && variant[0] == '@' && TryContainer(variant.AsSpan(1), tokens, out effect)) {
            return true;
        }

        if (variant.Equals("dark", StringComparison.Ordinal)) {
            effect = tokens.DarkMode == DarkModeStrategy.Class
                ? new VariantEffect(string.Empty, ".dark ", null)
                : new VariantEffect(string.Empty, string.Empty, "@media (prefers-color-scheme: dark)");

            return true;
        }

        if (variant is "ltr" or "rtl") {
            // The same shape as `dark:` under the class strategy — an ancestor declares it and the
            // utility applies below. An *attribute* rather than a class because `direction` is a CSS
            // property here, so there is nothing else in the tree for a selector to match on; the
            // consequence is that an element cannot select on its own direction, only an ancestor's.
            effect = new VariantEffect(string.Empty, $"[dir={variant}] ", null);
            return true;
        }

        if (variant.StartsWith("group-", StringComparison.Ordinal)
            && States.TryGetValue(variant["group-".Length..], out var groupState)) {
            effect = new VariantEffect(string.Empty, $".group{groupState} ", null);
            return true;
        }

        if (variant.StartsWith("peer-", StringComparison.Ordinal)
            && States.TryGetValue(variant["peer-".Length..], out var peerState)) {
            // A sibling rather than an ancestor, which is what makes `peer-checked:` able to style a
            // label from the state of the input before it.
            effect = new VariantEffect(string.Empty, $".peer{peerState} ~ ", null);
            return true;
        }

        if (variant.StartsWith("data-", StringComparison.Ordinal)) {
            effect = new VariantEffect(AttributeSelector("data-", variant["data-".Length..]), string.Empty, null);
            return true;
        }

        if (variant.StartsWith("aria-", StringComparison.Ordinal)) {
            // ⚠ `="true"` and not presence, which is the one place `aria-` must not follow `data-`.
            // An ARIA state is a tri-state whose *false* is spelled out: a collapsed disclosure
            // carries `aria-expanded="false"`, it does not drop the attribute. So `[aria-expanded]`
            // — what the shorthand used to emit — is true of the collapsed element as well as the
            // expanded one, and `aria-expanded:` styled both. WAI-ARIA 1.2 § 6.3 and Tailwind's own
            // eight built-ins agree on `="true"`; a non-boolean state such as `aria-sort` has no
            // shorthand in either and wants the arbitrary form.
            effect = new VariantEffect(
                AttributeSelector("aria-", variant["aria-".Length..], shorthand: "true"),
                string.Empty,
                null
            );

            return true;
        }

        if (variant.Length > 2 && variant[0] == '[' && variant[^1] == ']') {
            // The escape hatch. `&` stands for the class selector, so `[&>*]:p-4` becomes
            // `.\[\&\>\*\]\:p-4 > *`.
            var inner = variant[1..^1].Replace('_', ' ');
            effect = new VariantEffect(inner, string.Empty, null);
            return true;
        }

        return false;
    }

    /// <summary>Whether a variant's effect goes where <c>&amp;</c> is rather than after the selector.</summary>
    /// <param name="effect">The effect.</param>
    /// <returns>Whether it contains a placeholder.</returns>
    public static bool IsArbitrary(VariantEffect effect) =>
        effect.SelectorSuffix.Contains('&', StringComparison.Ordinal);

    /// <summary>Reads <c>@sm</c>, <c>@max-lg</c>, <c>@min-[30rem]</c> and their <c>/name</c> forms.</summary>
    /// <param name="rest">The variant with its <c>@</c> already taken off.</param>
    /// <param name="tokens">The theme, for the <c>--container-*</c> scale.</param>
    /// <param name="effect">Receives the <c>@container</c> wrapper.</param>
    /// <returns>Whether it is a container variant.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Driven off <see cref="ThemeTokens.Containers" /> and never off
    ///         <see cref="ThemeTokens.Screens" />, which is the decision this whole variant family
    ///         waited on.</b> The two namespaces spell the same names — <c>sm</c>, <c>lg</c>,
    ///         <c>2xl</c> — and mean numbers two-thirds apart: a 40 rem window against a 24 rem box.
    ///         A <c>@sm:</c> resolved against the breakpoints is a threshold no dockable panel in an
    ///         editor ever reaches, so every rule it wrote would be valid CSS that never matched,
    ///         and nothing in CSS warns about a query that is merely always false.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>@max-*</c> emits <c>max-width</c>, which is <c>&lt;=</c> where v4's
    ///         <c>(width &lt; 24rem)</c> is <c>&lt;</c>.</b> The two differ on exactly one width —
    ///         the threshold itself — because <see cref="ContainerQuery" /> reads the
    ///         <c>min-</c>/<c>max-</c> prefix forms and has no range syntax to read. It is the same
    ///         inclusive reading <c>Screens</c> above already gives every breakpoint, so the
    ///         divergence is the engine's throughout rather than this family's.
    ///     </para>
    ///     <para>
    ///         The name goes after the last <c>/</c>, which is where v4 puts it and is unambiguous
    ///         here because a length never contains one: <c>@sm/main</c> asks the nearest ancestor
    ///         <i>called</i> <c>main</c>, and <c>@sm</c> asks the nearest container of any name.
    ///     </para>
    /// </remarks>
    static bool TryContainer(ReadOnlySpan<char> rest, ThemeTokens tokens, out VariantEffect effect) {
        effect = default;

        var name = string.Empty;
        var slash = rest.LastIndexOf('/');

        if (slash >= 0) {
            // `@sm/` names nothing, and a name is an identifier rather than anything at all.
            if (slash == rest.Length - 1 || !IsIdentifier(rest[(slash + 1)..])) {
                return false;
            }

            name = rest[(slash + 1)..].ToString();
            rest = rest[..slash];
        }

        var feature = "min-width";

        if (rest.StartsWith("min-", StringComparison.Ordinal)) {
            rest = rest[4..];
        } else if (rest.StartsWith("max-", StringComparison.Ordinal)) {
            feature = "max-width";
            rest = rest[4..];
        }

        string width;

        if (rest.Length > 2 && rest[0] == '[' && rest[^1] == ']') {
            // The arbitrary form. Verbatim, because the author wrote a length and the units this
            // engine can compare are `MediaQuery.TryLength`'s rather than this table's.
            width = rest[1..^1].ToString().Replace('_', ' ');

            if (width.Length == 0) {
                return false;
            }
        } else if (tokens.Containers.TryGetValue(rest.ToString(), out var scale)) {
            width = scale.ToString("0.####", CultureInfo.InvariantCulture) + "px";
        } else {
            return false;
        }

        var subject = name.Length == 0 ? string.Empty : name + " ";
        effect = new VariantEffect(string.Empty, string.Empty, $"@container {subject}({feature}: {width})");

        return true;
    }

    static bool IsIdentifier(ReadOnlySpan<char> text) {
        foreach (var c in text) {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')) {
                return false;
            }
        }

        return text.Length > 0;
    }

    static string AttributeSelector(string prefix, string rest, string? shorthand = null) {
        // `data-[state=open]:` — the arbitrary form is verbatim whatever the family, because the
        // author has written the comparison out.
        if (rest.Length > 1 && rest[0] == '[' && rest[^1] == ']') {
            return $"[{prefix}{rest[1..^1].Replace('_', ' ')}]";
        }

        // The shorthand `data-open:`, which means the attribute is present — and `aria-expanded:`,
        // which means it is present *and* `"true"`. See the `aria-` branch above for why the two
        // differ.
        return shorthand is null ? $"[{prefix}{rest}]" : $"[{prefix}{rest}=\"{shorthand}\"]";
    }
}
