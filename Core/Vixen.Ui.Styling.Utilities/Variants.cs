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

        if (variant.Equals("dark", StringComparison.Ordinal)) {
            effect = tokens.DarkMode == DarkModeStrategy.Class
                ? new VariantEffect(string.Empty, ".dark ", null)
                : new VariantEffect(string.Empty, string.Empty, "@media (prefers-color-scheme: dark)");

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
            effect = new VariantEffect(AttributeSelector("aria-", variant["aria-".Length..]), string.Empty, null);
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

    static string AttributeSelector(string prefix, string rest) {
        // `data-[state=open]:` and the shorthand `data-open:`, which means the attribute is present.
        if (rest.Length > 1 && rest[0] == '[' && rest[^1] == ']') {
            return $"[{prefix}{rest[1..^1].Replace('_', ' ')}]";
        }

        return $"[{prefix}{rest}]";
    }
}
