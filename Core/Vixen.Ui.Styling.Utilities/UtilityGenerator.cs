// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Vixen.Ui.Styling.Utilities;

/// <summary>Turns the utilities a project uses into a stylesheet.</summary>
/// <remarks>
///     <para>
///         Emitted into <c>@layer utilities</c>, and that one line is what makes the whole system
///         behave. A generated <c>.p-4</c> is one class and a hand-written <c>.card .body</c> is two,
///         so on specificity alone the utility loses every time and the only remedy is
///         <c>!important</c> on everything the generator produces. With a layer the question is
///         settled once, declaratively, and specificity never enters into it.
///     </para>
///     <para>
///         Only the utilities that are <i>used</i> are emitted. The alternative — every combination
///         of every family and every token — is a stylesheet in the tens of megabytes, and the
///         scanner exists so that nobody has to think about that.
///     </para>
/// </remarks>
public sealed class UtilityGenerator {
    readonly ThemeTokens tokens;
    readonly List<UtilityDeclaration> declarations = [];
    readonly List<string> unknown = [];

    /// <summary>Creates a generator.</summary>
    /// <param name="tokens">The theme.</param>
    public UtilityGenerator(ThemeTokens tokens) {
        ArgumentNullException.ThrowIfNull(tokens);
        this.tokens = tokens;
    }

    /// <summary>The candidates that looked like utilities and were not ones.</summary>
    /// <remarks>
    ///     Not diagnostics. Scanning is deliberately over-inclusive — it cannot tell a class name
    ///     from any other string — so most of these are ordinary words, and warning about them would
    ///     make the build unusable. Exposed because a <i>typo</i> in a real utility lands here too,
    ///     and having somewhere to look is the difference between a five-minute puzzle and an hour.
    /// </remarks>
    public IReadOnlyList<string> Unrecognised => unknown;

    /// <summary>How many rules the last generation emitted.</summary>
    public int RuleCount { get; private set; }

    /// <summary>Generates a stylesheet for a set of candidate class names.</summary>
    /// <param name="candidates">The class names, in any order.</param>
    /// <returns>VCSS text.</returns>
    public string Generate(IEnumerable<string> candidates) {
        ArgumentNullException.ThrowIfNull(candidates);

        unknown.Clear();
        RuleCount = 0;

        // Sorted and de-duplicated, so that the same project produces the same file byte for byte.
        // A generated artefact that changes order between builds turns every diff into noise and
        // every content hash into a cache miss.
        var seen = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates) {
            seen.Add(candidate);
        }

        // Grouped by at-rule, so that twenty `md:` utilities share one media query rather than
        // opening twenty.
        var plain = new StringBuilder();
        var wrapped = new SortedDictionary<string, StringBuilder>(StringComparer.Ordinal);

        foreach (var candidate in seen) {
            if (!UtilityParser.TryParse(candidate, out var parsed)
                || !UtilityFamilies.TryResolve(parsed, tokens, declarations)) {
                unknown.Add(candidate);
                continue;
            }

            var (selector, atRule) = BuildSelector(parsed);
            if (selector is null) {
                unknown.Add(candidate);
                continue;
            }

            var into = atRule is null ? plain : Bucket(wrapped, atRule);
            Write(into, selector, parsed.Important, atRule is null ? 2 : 4);
            RuleCount++;
        }

        var css = new StringBuilder();
        css.Append("@layer utilities {\n");
        css.Append(plain);

        foreach (var (atRule, body) in wrapped) {
            css.Append("  ").Append(atRule).Append(" {\n");
            css.Append(body);
            css.Append("  }\n");
        }

        css.Append("}\n");
        return css.ToString();
    }

    void Write(StringBuilder into, string selector, bool important, int indent) {
        var pad = new string(' ', indent);
        into.Append(pad).Append(selector).Append(" {");

        foreach (var declaration in declarations) {
            into.Append(' ').Append(declaration.Property).Append(": ").Append(declaration.Value);

            if (important) {
                into.Append(" !important");
            }

            into.Append(';');
        }

        into.Append(" }\n");
    }

    (string? Selector, string? AtRule) BuildSelector(UtilityCandidate candidate) {
        var selector = "." + Escape(candidate.Original);
        var prefix = string.Empty;
        string? atRule = null;

        foreach (var variant in candidate.Variants) {
            if (!Variants.TryResolve(variant, tokens, out var effect)) {
                return (null, null);
            }

            if (Variants.IsArbitrary(effect)) {
                selector = effect.SelectorSuffix.Replace("&", selector, StringComparison.Ordinal);
            } else {
                selector += effect.SelectorSuffix;
            }

            prefix = effect.SelectorPrefix + prefix;

            if (effect.AtRule is null) {
                continue;
            }

            if (atRule is not null && atRule != effect.AtRule) {
                // Two media queries on one utility would have to nest, and Vixen's `@media` support
                // does not. Dropped rather than half-applied, which is the rule everywhere else in
                // this engine.
                return (null, null);
            }

            atRule = effect.AtRule;
        }

        return (prefix + selector, atRule);
    }

    /// <summary>Escapes a class name so it can be a CSS selector.</summary>
    /// <param name="name">The class name as written in the markup.</param>
    /// <returns>The escaped form.</returns>
    /// <remarks>
    ///     A utility's class name contains the characters its grammar is made of — <c>:</c>,
    ///     <c>/</c>, <c>[</c>, <c>.</c> — and every one of them means something else in a selector.
    ///     <c>hover:w-1/2</c> unescaped reads as a pseudo-class on a class called <c>hover</c>.
    /// </remarks>
    public static string Escape(string name) {
        ArgumentNullException.ThrowIfNull(name);

        var escaped = new StringBuilder(name.Length + 8);

        foreach (var c in name) {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')) {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return escaped.ToString();
    }

    static StringBuilder Bucket(SortedDictionary<string, StringBuilder> buckets, string key) {
        if (buckets.TryGetValue(key, out var bucket)) {
            return bucket;
        }

        bucket = new StringBuilder();
        buckets[key] = bucket;
        return bucket;
    }
}
