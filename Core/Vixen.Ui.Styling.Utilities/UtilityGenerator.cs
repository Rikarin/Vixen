// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Vixen.Ui.Styling.Utilities;

/// <summary>Why a candidate that <i>was</i> a utility still produced no rule.</summary>
public enum UtilityRefusalKind {
    /// <summary>The family is registered and does not answer this value.</summary>
    /// <remarks>
    ///     <c>bg-clip-text</c>: <c>bg</c> exists, takes the longest registered prefix, and has nothing
    ///     for <c>clip-text</c>. Every one of these is somebody writing a real Tailwind class against a
    ///     root Vixen has only partly registered.
    /// </remarks>
    Value,

    /// <summary>The utility resolved and one of its variants did not.</summary>
    /// <remarks><c>supports-hover:p-4</c> — the <c>p-4</c> is fine and the whole class emits nothing.</remarks>
    Variant
}

/// <summary>One candidate the generator refused, and what it was about it.</summary>
/// <param name="Candidate">The class name as scanned.</param>
/// <param name="Family">The registered family that was consulted — the <c>bg</c> of <c>bg-clip-text</c>.</param>
/// <param name="Detail">The part it could not answer: the value, or the variant.</param>
/// <param name="Kind">Which of the two refusals this is.</param>
public readonly record struct UtilityRefusal(
    string Candidate,
    string Family,
    string Detail,
    UtilityRefusalKind Kind
);

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
///         ⚠ <b>And an <c>@layer base, components, utilities;</c> statement above it, which is what
///         makes the layer worth anything.</b> A layer only wins if something loses to it, and until
///         the engine's own sheets moved into <c>components</c> nothing did: an unlayered rule beats
///         every layer, so a control default beat every utility silently and always. The statement is
///         also the ladder's *order*, and order is decided by whoever declares a name first — so a
///         sheet naming only its own layer is a sheet whose position depends on load order. Every
///         theme sheet in the tree opens with the same line for the same reason.
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
    readonly List<UtilityRefusal> unresolved = [];
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
    ///     <para>
    ///         ⚠ <b>No longer everything the generator dropped: see <see cref="Unresolved" />.</b> A
    ///         candidate whose family is registered leaves here and goes there, so this list is now
    ///         what its remark always claimed it was — the prose.
    ///     </para>
    /// </remarks>
    public IReadOnlyList<string> Unrecognised => unknown;

    /// <summary>The candidates that named a registered family and still produced no rule.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The distinction <see cref="Unrecognised" /> could not make, and the reason the
    ///         list above was unreadable.</b> <c>bg-clip-text</c> and the English word <c>however</c>
    ///         both used to arrive in one list of several hundred entries, so the one that is a real
    ///         Tailwind class Vixen half-supports was indistinguishable from the several hundred that
    ///         are prose — and indistinguishable-from-a-typo is the failure mode. Every entry here is
    ///         news: the author wrote a class whose <i>root</i> this engine registers, which is the
    ///         only reason they had to expect it to work.
    ///     </para>
    ///     <para>
    ///         <b>What it is not is a to-do list of missing families.</b> A refusal names the family
    ///         that was consulted, not the family that should have been:
    ///         <see cref="UtilityFamilies.SplitName" /> takes the longest registered prefix, so
    ///         <c>rounded-ss-lg</c> is refused by <c>rounded</c> and what it actually wants is a
    ///         <c>rounded-ss</c> nobody has registered. There is no shorter prefix to retry — a
    ///         retrying <c>SplitName</c> would rescue none of these, which
    ///         <c>Vixen.Ui.Styling.Utilities.Tests.ShadowedFamilyTests</c> measures rather than
    ///         asserts from memory.
    ///     </para>
    /// </remarks>
    public IReadOnlyList<UtilityRefusal> Unresolved => unresolved;

    /// <summary>How many rules the last generation emitted.</summary>
    public int RuleCount { get; private set; }

    /// <summary>Generates a stylesheet for a set of candidate class names.</summary>
    /// <param name="candidates">The class names, in any order.</param>
    /// <returns>VCSS text.</returns>
    public string Generate(IEnumerable<string> candidates) {
        ArgumentNullException.ThrowIfNull(candidates);

        unknown.Clear();
        unresolved.Clear();
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
            if (!UtilityParser.TryParse(candidate, out var parsed)) {
                unknown.Add(candidate);
                continue;
            }

            if (!UtilityFamilies.TryResolve(parsed, tokens, declarations)) {
                // ⚠ The split, and it is the only place that can make it. `TryResolve` says no for
                // two reasons and has one `false` to say them with; the registry is what tells them
                // apart, and asking it here rather than threading a reason out of `TryResolve` keeps
                // the refusal a property of *this* run — the same candidate is prose in a project
                // whose theme lacks the family and news in one whose theme has it.
                if (UtilityFamilies.IsRegistered(parsed.Name)) {
                    unresolved.Add(
                        new UtilityRefusal(candidate, parsed.Name, parsed.Value, UtilityRefusalKind.Value)
                    );
                } else {
                    unknown.Add(candidate);
                }

                continue;
            }

            atRuleScratch.Clear();
            var selector = BuildSelector(parsed, atRuleScratch, out var refusedVariant);

            if (selector is null) {
                // The utility resolved, so this is news whatever the variant was: nothing here is
                // prose — prose does not survive `TryResolve`.
                unresolved.Add(
                    new UtilityRefusal(candidate, parsed.Name, refusedVariant, UtilityRefusalKind.Variant)
                );
                continue;
            }

            // ⚠ <b>After the variants, and it is the only order that means what the class says.</b>
            // A scoped family's rule is about the *children* of whatever the variants selected, so
            // `hover:space-x-4` is `.hover\:space-x-4:hover > :not(:last-child)` — space the children
            // while the container is hovered. Appending the scope first would give
            // `.hover\:space-x-4 > :not(:last-child):hover`, which compiles, matches, and is a
            // different rule: space whichever child the pointer happens to be over. Null for every
            // family but the `space-*` and `divide-*` set, and `+= null` is the empty string.
            selector += UtilityFamilies.ScopeOf(parsed.Name);

            var group = root;
            foreach (var atRule in atRuleScratch) {
                group = group.Enter(atRule);
            }

            Write(group.Body, selector, parsed.Important, 2 + (2 * atRuleScratch.Count));
            RuleCount++;
        }

        var css = new StringBuilder();

        // ⚠ The order statement, and it is not decoration: `CascadeLayers.Declare` fixes a layer's
        // position the *first* time anything names it, so a document that loaded this sheet before
        // any theme sheet would make `utilities` layer zero and every later `@layer components` beat
        // it — the exact defect the layer exists to fix, restored silently by load order. Naming all
        // three here means a generated sheet arriving first still puts them in the right order, and
        // a re-declaration by a theme sheet is a no-op. Tailwind's own output opens the same way.
        css.Append("@layer base, components, utilities;\n\n");
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
    /// <param name="refused">Receives the variant that was not one this system knows, or empty.</param>
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
    string? BuildSelector(UtilityCandidate candidate, List<string> atRules, out string refused) {
        var selector = "." + Escape(candidate.Original);
        var prefix = string.Empty;
        refused = string.Empty;

        foreach (var variant in candidate.Variants) {
            if (!Variants.TryResolve(variant, tokens, out var effect)) {
                refused = variant;
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
