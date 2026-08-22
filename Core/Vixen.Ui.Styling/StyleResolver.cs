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
    readonly MediaScopes scopes;
    readonly ContainerScopes containers;
    readonly Dictionary<StyleSharingKey, ComputedStyle> shared = [];
    readonly Dictionary<int, Winner> winners = [];
    readonly List<int> candidates = [];
    readonly List<KeyValuePair<int, int>> pairs = [];

    /// <summary>Creates a resolver.</summary>
    /// <param name="rules">The loaded rules.</param>
    /// <param name="inlineStyles">The declarations written on elements themselves.</param>
    /// <param name="matcher">The selector matcher.</param>
    /// <param name="interning">Where computed styles are interned.</param>
    /// <param name="scopes">
    ///     Which <c>@media</c> groups hold on each surface the rule set is shown on.
    ///     <para>
    ///         ⚠ <b>Read off the element's own scope rather than taken as an argument, and that is
    ///         the point.</b> A verdict parameter would be a parameter every one of the cascade's
    ///         callers could forget, and forgetting it is silent — a query that stops matching, which
    ///         is this subsystem's recurring defect. Which surface an element is in is a fact about
    ///         the element, held next to its classes, and the cascade looks it up the same way.
    ///     </para>
    /// </param>
    /// <param name="containers">
    ///     Which <c>@container</c> groups hold for each container chain the document has.
    ///     <para>
    ///         Read off the element's own container scope, for the same reason the media scope is:
    ///         which box an element is inside is a fact about the element, and a parameter is a
    ///         parameter a caller can forget.
    ///     </para>
    /// </param>
    public StyleResolver(
        StyleRuleSet rules,
        InlineStyleStore inlineStyles,
        SelectorMatcher matcher,
        ComputedStyleCache interning,
        MediaScopes scopes,
        ContainerScopes containers
    ) {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(inlineStyles);
        ArgumentNullException.ThrowIfNull(matcher);
        ArgumentNullException.ThrowIfNull(interning);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(containers);

        this.rules = rules;
        this.inlineStyles = inlineStyles;
        this.matcher = matcher;
        this.interning = interning;
        this.scopes = scopes;
        this.containers = containers;
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

    /// <summary>Which properties a child gets from its parent unasked.</summary>
    /// <remarks>
    ///     Exposed because a restyle pass has to ask whether a change can reach a child, and the
    ///     answer is exactly this list. Owned here because the cascade is what applies it.
    /// </remarks>
    public InheritedProperties Inherited => inherited;

    /// <summary>Starts a restyle pass, forgetting what the last one shared.</summary>
    /// <remarks>
    ///     <para>
    ///         The sharing cache lives for one pass and no longer. Its key describes what an element
    ///         <i>is</i> — parent, tag, id, classes, state, inline style — and an entry cached before
    ///         a change knows nothing about the parent having gained a class since. Within a pass the
    ///         tree is fixed and the key is sound; across passes it is stale.
    ///     </para>
    ///     <para>
    ///         Nothing is lost by that. Sharing's job is to make one pass over a grid cheap, which is
    ///         entirely within a pass, and the reference identity that survives between passes comes
    ///         from <see cref="ComputedStyleCache" />, which is never cleared. Working out which
    ///         entries to evict instead would cost more than rebuilding them and would be one more
    ///         thing that can be subtly wrong.
    ///     </para>
    /// </remarks>
    public void BeginPass() => shared.Clear();

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

        var slot = tree.Validate(element);

        if (!rules.SharingIsSound(
                scopes.VerdictsOf(tree.ScopeAt(slot)),
                containers.VerdictsOf(tree.ContainerAt(slot))
            )) {
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

        // The surface this element is shown in, which is what its `@media` blocks are about. One
        // lookup per element rather than per candidate: every rule in the loop below is asked about
        // the same surface.
        var slot = tree.Validate(element);
        var verdicts = scopes.VerdictsOf(tree.ScopeAt(slot));

        // ⚠ The container chain this element is inside, which is a different subject from the
        // surface and answers a different table. One lookup per element for the same reason.
        var contained = containers.VerdictsOf(tree.ContainerAt(slot));

        foreach (var rule in candidates) {
            var candidate = rules[rule];

            // ⚠ Before the matcher, because it is the cheaper test and the one that can reject a
            // whole breakpoint's worth of rules on an integer compare. `Unconditional` is checked
            // first inside `Holds`, and is what every rule in every stylesheet this repository ships
            // carries — so the common case is one comparison against zero.
            if (!verdicts.Holds(candidate.Conditions)) {
                continue;
            }

            // ⚠ Also before the matcher, and it is the same integer compare — `Unconditional` is
            // checked first inside `Holds` and is what every rule in every stylesheet this repository
            // ships carries, so a document with no `@container` in it pays one comparison against
            // zero per candidate and never walks a chain.
            if (!contained.Holds(candidate.Containers)) {
                continue;
            }

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
            inline,
            tree.ScopeAt(index),
            tree.ContainerAt(index)
        );
    }

    readonly record struct Winner(int Value, CascadePrecedence Precedence);
}
