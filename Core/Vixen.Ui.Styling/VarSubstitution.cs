// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Vixen.Ui.Styling;

/// <summary>Substitutes <c>var(--name, fallback)</c> against an element's custom properties.</summary>
/// <remarks>
///     <para>
///         Textual, and correct to be. CSS defines <c>var()</c> as a substitution over the token
///         stream at computed-value time, before the value is parsed as anything in particular —
///         which is exactly why <c>--corner: 4px</c> can be used by <c>border-radius</c> and by
///         <c>padding</c> without either of them knowing about the other. Doing it here, on the text,
///         keeps the property system downstream of it and unaware of it.
///     </para>
///     <para>
///         ExCSS makes this workable by leaving anything containing a <c>var()</c> verbatim — the
///         [spike](../../docs/plan/spikes/vcss-excss/RESULT.md) checked exactly that. It has a
///         consequence worth stating: <c>color: red</c> reaches Vixen already normalised to
///         <c>rgb(255, 0, 0)</c>, but <c>color: var(--accent)</c> with <c>--accent: red</c> reaches
///         it as <c>red</c>. The value parser has to accept both, because the front end normalises
///         only what it could see.
///     </para>
///     <para>
///         A <c>var()</c> that resolves to nothing and has no fallback makes the declaration
///         <i>invalid at computed-value time</i>, which is not the same as dropping it: the property
///         falls back to its inherited or initial value rather than to the next declaration in the
///         cascade. Returning <see langword="null" /> is how that is said here.
///     </para>
/// </remarks>
public static class VarSubstitution {
    /// <summary>How deep a chain of custom properties referring to each other may go.</summary>
    /// <remarks>
    ///     A cycle is invalid at computed-value time, and the honest way to find one would be a
    ///     dependency walk. A depth limit finds every cycle too, costs nothing, and cannot itself be
    ///     wrong about a value — only about how quickly it gave up on one. Nothing sane nests
    ///     custom properties past single digits.
    /// </remarks>
    public const int MaximumDepth = 32;

    /// <summary>Whether a value contains anything to substitute.</summary>
    /// <param name="value">The value text.</param>
    /// <returns>Whether it mentions <c>var(</c>.</returns>
    public static bool NeedsSubstitution(string value) {
        ArgumentNullException.ThrowIfNull(value);
        return value.Contains("var(", StringComparison.Ordinal);
    }

    /// <summary>Substitutes every <c>var()</c> in a value.</summary>
    /// <param name="value">The value text.</param>
    /// <param name="lookup">Resolves a custom property name to its value, or returns null.</param>
    /// <returns>
    ///     The substituted text, or <see langword="null" /> if the declaration is invalid at
    ///     computed-value time.
    /// </returns>
    public static string? Substitute(string value, Func<string, string?> lookup) {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(lookup);

        return Substitute(value, lookup, 0);
    }

    static string? Substitute(string value, Func<string, string?> lookup, int depth) {
        if (depth > MaximumDepth) {
            return null;
        }

        if (!NeedsSubstitution(value)) {
            return value;
        }

        var result = new StringBuilder(value.Length);
        var index = 0;

        while (index < value.Length) {
            var start = value.IndexOf("var(", index, StringComparison.Ordinal);
            if (start < 0) {
                result.Append(value, index, value.Length - index);
                break;
            }

            // `myvar(x)` mentions "var(" and is not one.
            if (start > 0 && (char.IsLetterOrDigit(value[start - 1]) || value[start - 1] is '-' or '_')) {
                result.Append(value, index, start + 4 - index);
                index = start + 4;
                continue;
            }

            var close = FindClose(value, start + 3);
            if (close < 0) {
                return null;
            }

            result.Append(value, index, start - index);

            var substituted = Resolve(value[(start + 4)..close], lookup, depth);
            if (substituted is null) {
                return null;
            }

            result.Append(substituted);
            index = close + 1;
        }

        return result.ToString();
    }

    static string? Resolve(string arguments, Func<string, string?> lookup, int depth) {
        var comma = TopLevelComma(arguments);
        var name = (comma < 0 ? arguments : arguments[..comma]).Trim();
        var fallback = comma < 0 ? null : arguments[(comma + 1)..].Trim();

        var resolved = name.Length == 0 ? null : lookup(name);
        if (resolved is not null) {
            // The substituted text can itself contain a var(), and this is where a cycle shows up as
            // a depth that never comes back down.
            return Substitute(resolved, lookup, depth + 1);
        }

        // No fallback and no value is invalid at computed-value time, which is a different outcome
        // from an empty fallback: `var(--missing,)` is legally the empty token stream.
        return fallback is null ? null : Substitute(fallback, lookup, depth + 1);
    }

    static int TopLevelComma(string text) {
        var depth = 0;

        for (var i = 0; i < text.Length; i++) {
            switch (text[i]) {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    return i;
                default:
                    break;
            }
        }

        return -1;
    }

    static int FindClose(string text, int open) {
        var depth = 0;

        for (var i = open; i < text.Length; i++) {
            switch (text[i]) {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    if (depth == 0) {
                        return i;
                    }

                    break;
                default:
                    break;
            }
        }

        return -1;
    }
}
