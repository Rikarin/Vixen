// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Styling;

/// <summary>Turns an element and a rule set into one <see cref="ComputedStyle" />.</summary>
/// <remarks>
///     <para>
///         Four steps, in this order and no other. <b>Collect</b> the candidate rules from the
///         bucketed index and keep the ones that match. <b>Cascade</b> them: for each property, the
///         declaration with the highest <see cref="CascadePrecedence" /> wins. <b>Inherit</b>: fill
///         anything the cascade did not settle, from the parent for a property that inherits.
///         <b>Substitute</b> <c>var()</c>, which happens last because a custom property can itself be
///         inherited and could not be resolved before inheritance had run.
///     </para>
///     <para>
///         Then intern, so that two elements that resolved to the same thing hold the same object —
///         and, when the sharing cache can be trusted, skip all four steps for the second one.
///     </para>
/// </remarks>
public sealed class StyleResolver {
    readonly StyleRuleSet rules;
    readonly InlineStyleStore inlineStyles;
    readonly SelectorMatcher matcher;
    readonly InheritedProperties inherited;
    readonly ComputedStyleCache interning;
    readonly Dictionary<StyleSharingKey, ComputedStyle> shared = [];
    readonly Dictionary<int, Winner> winners = [];
    readonly List<int> candidates = [];
    readonly List<KeyValuePair<int, int>> pairs = [];

    /// <summary>Creates a resolver.</summary>
    /// <param name="rules">The loaded rules.</param>
    /// <param name="inlineStyles">The declarations written on elements themselves.</param>
    /// <param name="matcher">The selector matcher.</param>
    /// <param name="interning">Where computed styles are interned.</param>
    public StyleResolver(
        StyleRuleSet rules,
        InlineStyleStore inlineStyles,
        SelectorMatcher matcher,
        ComputedStyleCache interning
    ) {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(inlineStyles);
        ArgumentNullException.ThrowIfNull(matcher);
        ArgumentNullException.ThrowIfNull(interning);

        this.rules = rules;
        this.inlineStyles = inlineStyles;
        this.matcher = matcher;
        this.interning = interning;
        inherited = new InheritedProperties(rules.Properties);
    }

    /// <summary>How many elements got a style from the sharing cache rather than the cascade.</summary>
    /// <remarks>
    ///     Doc 09's claim in one number: ten thousand identical grid cells should produce nine
    ///     thousand nine hundred and ninety-nine of these.
    /// </remarks>
    public int SharingHits { get; private set; }

    /// <summary>How many elements had to be cascaded.</summary>
    public int Cascades { get; private set; }

    /// <summary>Resolves an element's style, using the sharing cache where it is sound.</summary>
    /// <param name="tree">The element store.</param>
    /// <param name="element">The element.</param>
    /// <param name="parent">The parent's already-resolved style, or null for a root.</param>
    /// <param name="inline">A handle on declarations written on the element itself, if any.</param>
    /// <returns>The interned computed style.</returns>
    public ComputedStyle Resolve(
        StyleTree tree,
        StyleNodeId element,
        ComputedStyle? parent = null,
        InlineStyleId? inline = null
    ) {
        ArgumentNullException.ThrowIfNull(tree);

        if (!rules.SharingIsSound) {
            return Cascade(tree, element, parent, inline);
        }

        var key = SharingKey(tree, element, inline);
        if (shared.TryGetValue(key, out var existing)) {
            SharingHits++;
            return existing;
        }

        var computed = Cascade(tree, element, parent, inline);
        shared[key] = computed;
        return computed;
    }

    /// <summary>Resolves an element's style without consulting the sharing cache.</summary>
    /// <param name="tree">The element store.</param>
    /// <param name="element">The element.</param>
    /// <param name="parent">The parent's already-resolved style, or null for a root.</param>
    /// <param name="inline">A handle on declarations written on the element itself, if any.</param>
    /// <returns>The interned computed style.</returns>
    /// <remarks>
    ///     The oracle the sharing cache is checked against. Sharing is a pure optimisation or it is a
    ///     bug, and the only way to know which is to have something that does the work every time.
    /// </remarks>
    public ComputedStyle Cascade(
        StyleTree tree,
        StyleNodeId element,
        ComputedStyle? parent = null,
        InlineStyleId? inline = null
    ) {
        ArgumentNullException.ThrowIfNull(tree);

        Cascades++;
        winners.Clear();

        rules.Index.Collect(tree, element, candidates);

        foreach (var rule in candidates) {
            var candidate = rules[rule];
            if (!matcher.Matches(tree, element, candidate.Selector)) {
                continue;
            }

            foreach (var declaration in rules.DeclarationsOf(candidate.Declarations)) {
                Offer(
                    declaration,
                    new CascadePrecedence(
                        CascadeRanks.Of(candidate.Origin, declaration.Important),
                        declaration.Important
                            ? rules.Layers.ImportantRank(candidate.Layer)
                            : rules.Layers.NormalRank(candidate.Layer),
                        candidate.Selector.Specificity,
                        candidate.Order
                    )
                );
            }
        }

        if (inline is { } written) {
            foreach (var declaration in inlineStyles.DeclarationsOf(written)) {
                // An inline declaration has no selector, so specificity never enters into it — it is
                // placed by origin alone, above every rule of the same importance.
                Offer(
                    declaration,
                    new CascadePrecedence(
                        CascadeRanks.OfInline(declaration.Important),
                        int.MaxValue,
                        default,
                        int.MaxValue
                    )
                );
            }
        }

        return Build(parent);
    }

    void Offer(Declaration declaration, CascadePrecedence precedence) {
        // Strictly greater, so that a later offer at *equal* precedence wins. Two declarations tie
        // only when they came from the same place — one rule body, or one inline block — and there
        // the rule is the plainest one CSS has: the last one wins. `>=` here would keep the first,
        // and `style="color: red; color: blue"` would come out red.
        if (winners.TryGetValue(declaration.Property, out var held) && held.Precedence > precedence) {
            return;
        }

        winners[declaration.Property] = new Winner(declaration.Value, precedence);
    }

    ComputedStyle Build(ComputedStyle? parent) {
        pairs.Clear();

        foreach (var (property, winner) in winners) {
            pairs.Add(new KeyValuePair<int, int>(property, winner.Value));
        }

        // Inheritance fills what the cascade did not settle. It runs over the *parent's* resolved
        // table rather than over the rule set, which is what makes it one pass rather than a climb:
        // the parent already inherited from its own parent.
        if (parent is not null) {
            var properties = parent.Properties;
            var values = parent.Values;

            for (var i = 0; i < properties.Length; i++) {
                var property = properties[i];
                if (winners.ContainsKey(property) || !inherited.Inherits(property)) {
                    continue;
                }

                pairs.Add(new KeyValuePair<int, int>(property, values[i]));
            }
        }

        Substitute(parent);
        return interning.Intern(ComputedStyle.Create(pairs, parent));
    }

    /// <summary>Resolves every <c>var()</c> against the custom properties this element ended with.</summary>
    /// <remarks>
    ///     Last, and it has to be. A custom property can be inherited, so <c>var(--accent)</c> cannot
    ///     be answered until inheritance has run — and it is answered against the element's <i>own</i>
    ///     resolved set, not its parent's, because an element may override <c>--accent</c> and use it
    ///     in the same rule.
    /// </remarks>
    void Substitute(ComputedStyle? parent) {
        var needed = false;
        for (var i = 0; i < pairs.Count; i++) {
            if (VarSubstitution.NeedsSubstitution(rules.Values.NameOf(pairs[i].Value))) {
                needed = true;
                break;
            }
        }

        if (!needed) {
            return;
        }

        var custom = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in pairs) {
            var name = rules.Properties.NameOf(pair.Key);
            if (InheritedProperties.IsCustomProperty(name)) {
                custom[name] = rules.Values.NameOf(pair.Value);
            }
        }

        for (var i = pairs.Count - 1; i >= 0; i--) {
            var text = rules.Values.NameOf(pairs[i].Value);
            if (!VarSubstitution.NeedsSubstitution(text)) {
                continue;
            }

            var substituted = VarSubstitution.Substitute(text, name => custom.GetValueOrDefault(name));

            if (substituted is null) {
                // Invalid at computed-value time: the property behaves as though nothing had set it,
                // which for an inherited one means the parent's value and not the next declaration
                // down. Removing it here and re-inheriting is exactly that.
                var property = pairs[i].Key;
                pairs.RemoveAt(i);

                if (parent is not null
                    && inherited.Inherits(property)
                    && parent.TryGet(property, out var inheritedValue)) {
                    pairs.Add(new KeyValuePair<int, int>(property, inheritedValue));
                }

                continue;
            }

            pairs[i] = new KeyValuePair<int, int>(pairs[i].Key, rules.Values.Intern(substituted));
        }
    }

    static StyleSharingKey SharingKey(StyleTree tree, StyleNodeId element, InlineStyleId? inline) {
        var index = tree.Validate(element);
        var classHash = 0;

        tree.ForEachClass(index, name => {
            // Order-independent, because `class="a b"` and `class="b a"` are the same element as far
            // as any selector can tell. Addition rather than a mixing combine for exactly that
            // reason; the count travels alongside so a collision needs a second coincidence.
            classHash += HashCode.Combine(name);
        });

        return new StyleSharingKey(
            new StyleNodeId(tree.ParentOf(index)),
            tree.TagOf(index),
            tree.IdOf(index),
            classHash,
            tree.ClassCountOf(index),
            tree.StateOf(index),
            inline
        );
    }

    readonly record struct Winner(int Value, CascadePrecedence Precedence);
}
