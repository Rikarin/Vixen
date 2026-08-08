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
    readonly List<string> atRuleScratch = [];

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

        // Grouped by at-rule *chain*, so that twenty `md:` utilities share one media query rather
        // than opening twenty — and so that `sm:md:p-4` and `sm:m-2` share the outer one.
        var root = new AtRuleGroup();

        foreach (var candidate in seen) {
            if (!UtilityParser.TryParse(candidate, out var parsed)
                || !UtilityFamilies.TryResolve(parsed, tokens, declarations)) {
                unknown.Add(candidate);
                continue;
            }

            atRuleScratch.Clear();
            var selector = BuildSelector(parsed, atRuleScratch);

            if (selector is null) {
                unknown.Add(candidate);
                continue;
            }

            var group = root;
            foreach (var atRule in atRuleScratch) {
                group = group.Enter(atRule);
            }

            Write(group.Body, selector, parsed.Important, 2 + (2 * atRuleScratch.Count));
            RuleCount++;
        }

        var css = new StringBuilder();
        css.Append("@layer utilities {\n");
        Emit(root, css, 2);
        css.Append("}\n");

        return css.ToString();
    }

    /// <summary>One level of at-rule nesting: the rules directly inside it, and the levels below.</summary>
    /// <remarks>
    ///     ⚠ <b>A trie over the at-rule chain rather than a dictionary keyed by the joined chain,
    ///     because the point is to share prefixes.</b> <c>sm:p-4</c> and <c>sm:md:m-2</c> both open
    ///     <c>@media (min-width: 640px)</c>, and a flat key would emit it twice; CSS Conditional 5
    ///     § 3 lets a conditional group rule contain another, so one wrapper holding both a rule and a
    ///     nested wrapper is what the specification already describes.
    /// </remarks>
    sealed class AtRuleGroup {
        /// <summary>The rules written directly at this level.</summary>
        public StringBuilder Body { get; } = new();

        /// <summary>The groups nested inside this one, ordered so the file is byte-stable.</summary>
        public SortedDictionary<string, AtRuleGroup> Nested { get; } = new(StringComparer.Ordinal);

        /// <summary>The group for an at-rule inside this one, created if it is the first to ask.</summary>
        /// <param name="atRule">The at-rule.</param>
        /// <returns>Its group.</returns>
        public AtRuleGroup Enter(string atRule) {
            if (Nested.TryGetValue(atRule, out var group)) {
                return group;
            }

            group = new AtRuleGroup();
            Nested[atRule] = group;

            return group;
        }
    }

    static void Emit(AtRuleGroup group, StringBuilder css, int indent) {
        css.Append(group.Body);
        var pad = new string(' ', indent);

        foreach (var (atRule, nested) in group.Nested) {
            css.Append(pad).Append(atRule).Append(" {\n");
            Emit(nested, css, indent + 2);
            css.Append(pad).Append("}\n");
        }
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

    /// <summary>Works out a candidate's selector and the at-rules it has to sit inside.</summary>
    /// <param name="candidate">The parsed class name.</param>
    /// <param name="atRules">Receives the at-rules, outermost first.</param>
    /// <returns>The selector, or <c>null</c> if a variant is not one this system knows.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A stack of conditions rather than one, which is what <c>sm:md:p-4</c> needs and
    ///         what <c>@container</c> will need.</b> Two media variants on one utility used to be
    ///         dropped here, on the belief that Vixen's <c>@media</c> could not nest. It can — CSS
    ///         Conditional 5 § 3 nesting is what <c>StyleSheetLoader.LoadMedia</c> has always done by
    ///         recursing into the rule it just matched — so the limitation was this method carrying one
    ///         <c>string?</c> and not the cascade underneath it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Source order, deduplicated.</b> Conditional group rules conjoin, so
    ///         <c>@media A { @media B { … } }</c> and the reverse select the same elements and the
    ///         order is free; keeping what the author wrote is the one choice that never surprises,
    ///         and it is what v4 emits. Deduplication is what keeps <c>md:md:p-4</c> — which a
    ///         hand-written class list does produce — one wrapper rather than two identical ones.
    ///     </para>
    /// </remarks>
    string? BuildSelector(UtilityCandidate candidate, List<string> atRules) {
        var selector = "." + Escape(candidate.Original);
        var prefix = string.Empty;

        foreach (var variant in candidate.Variants) {
            if (!Variants.TryResolve(variant, tokens, out var effect)) {
                return null;
            }

            if (Variants.IsArbitrary(effect)) {
                selector = effect.SelectorSuffix.Replace("&", selector, StringComparison.Ordinal);
            } else {
                selector += effect.SelectorSuffix;
            }

            prefix = effect.SelectorPrefix + prefix;

            if (effect.AtRule is not null && !atRules.Contains(effect.AtRule, StringComparer.Ordinal)) {
                atRules.Add(effect.AtRule);
            }
        }

        return prefix + selector;
    }

    /// <summary>Escapes a class name so it can be a CSS selector.</summary>
    /// <param name="name">The class name as written in the markup.</param>
    /// <returns>The escaped form.</returns>
    /// <remarks>
    ///     <para>
    ///         A utility's class name contains the characters its grammar is made of — <c>:</c>,
    ///         <c>/</c>, <c>[</c>, <c>.</c> — and every one of them means something else in a selector.
    ///         <c>hover:w-1/2</c> unescaped reads as a pseudo-class on a class called <c>hover</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A leading digit is escaped as a code point and not with a backslash, and the
    ///         difference is the whole <c>2xl:</c> breakpoint.</b> CSS Syntax 3 § 4.3.8 says an
    ///         identifier starting with a digit must have that digit written as
    ///         <c>\</c> + hex + a space, because <c>\2</c> <i>begins</i> a hex escape — so
    ///         <c>.2xl\:p-4</c> is not a selector at all and <c>.\2xl\:p-4</c> is a selector for
    ///         something else entirely. The engine ships <c>--breakpoint-2xl</c> in its default theme,
    ///         so every <c>2xl:</c> utility in every project emitted a rule ExCSS then refused, with a
    ///         diagnostic nobody was reading and no test looking. <c>\32 xl\:p-4</c> is the form, space
    ///         included — the space is the escape's terminator, not padding.
    ///     </para>
    /// </remarks>
    public static string Escape(string name) {
        ArgumentNullException.ThrowIfNull(name);

        var escaped = new StringBuilder(name.Length + 8);

        for (var i = 0; i < name.Length; i++) {
            var c = name[i];

            // The two positions § 4.3.8 calls out: the first character, and the second when the first
            // is a hyphen — `-2xl` would otherwise read as a negative number rather than an identifier.
            if (char.IsAsciiDigit(c) && (i == 0 || (i == 1 && name[0] == '-'))) {
                escaped.Append("\\3").Append(c).Append(' ');
                continue;
            }

            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')) {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return escaped.ToString();
    }
}
