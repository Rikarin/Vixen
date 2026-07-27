// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Vixen.Ui.Styling.Utilities;

/// <summary>Expands <c>@apply</c> inside a stylesheet.</summary>
/// <remarks>
///     <para>
///         <c>@apply flex items-center gap-2</c> writes out what those utilities mean, in place. It
///         exists because the two ways of styling something should be able to meet: a component with
///         a real name and a real selector, built out of the same tokens as everything else, without
///         repeating the numbers.
///     </para>
///     <para>
///         Expanded <b>before</b> ExCSS sees the stylesheet, which is why this is a text transform
///         rather than a pass over the parsed tree. ExCSS would hand <c>@apply</c> back as an unknown
///         rule with no structure worth walking, and the declarations it produces have to be part of
///         the block they were written in — not a rule of their own, which is what any
///         post-parse approach would be forced into.
///     </para>
///     <para>
///         Variants are refused rather than approximated. <c>@apply hover:bg-accent</c> would have to
///         invent a rule with a different selector from the block it sits in, which is not what
///         "apply this here" means, and quietly emitting one is worse than saying no.
///     </para>
/// </remarks>
public sealed class ApplyExpander {
    readonly ThemeTokens tokens;
    readonly List<UtilityDeclaration> declarations = [];
    readonly List<string> diagnostics = [];

    /// <summary>Creates an expander.</summary>
    /// <param name="tokens">The theme.</param>
    public ApplyExpander(ThemeTokens tokens) {
        ArgumentNullException.ThrowIfNull(tokens);
        this.tokens = tokens;
    }

    /// <summary>Every <c>@apply</c> that could not be expanded, and why.</summary>
    public IReadOnlyList<string> Diagnostics => diagnostics;

    /// <summary>Expands every <c>@apply</c> in a stylesheet.</summary>
    /// <param name="css">The stylesheet.</param>
    /// <returns>The stylesheet with each <c>@apply</c> replaced by what it meant.</returns>
    public string Expand(string css) {
        ArgumentNullException.ThrowIfNull(css);

        diagnostics.Clear();

        if (!css.Contains("@apply", StringComparison.Ordinal)) {
            return css;
        }

        var result = new StringBuilder(css.Length);
        var index = 0;

        while (index < css.Length) {
            var start = css.IndexOf("@apply", index, StringComparison.Ordinal);
            if (start < 0) {
                result.Append(css, index, css.Length - index);
                break;
            }

            result.Append(css, index, start - index);

            // The rule runs to a semicolon or to the end of its block, whichever comes first — the
            // last declaration in a block is allowed to omit its semicolon and often does.
            var end = css.IndexOfAny([';', '}'], start);
            if (end < 0) {
                end = css.Length;
            }

            var utilities = css[(start + "@apply".Length)..end];
            result.Append(ExpandList(utilities));

            index = end < css.Length && css[end] == ';' ? end + 1 : end;
        }

        return result.ToString();
    }

    string ExpandList(string utilities) {
        var expanded = new StringBuilder();

        foreach (var name in utilities.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            if (!UtilityParser.TryParse(name, out var parsed)) {
                diagnostics.Add($"'{name}' is not shaped like a utility");
                continue;
            }

            if (parsed.Variants.Count > 0) {
                diagnostics.Add($"'{name}' has a variant, and @apply can only add declarations to the block it is in");
                continue;
            }

            if (!UtilityFamilies.TryResolve(parsed, tokens, declarations)) {
                diagnostics.Add($"'{name}' is not a utility Vixen knows");
                continue;
            }

            foreach (var declaration in declarations) {
                expanded.Append(declaration.Property).Append(": ").Append(declaration.Value);

                if (parsed.Important) {
                    expanded.Append(" !important");
                }

                expanded.Append("; ");
            }
        }

        return expanded.ToString();
    }
}
