// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace Vixen.Ui.Styling;

/// <summary>One rule: a selector, the declarations it applies, and where it sits in the cascade.</summary>
/// <param name="Selector">Its compiled selector.</param>
/// <param name="Declarations">The declarations it applies.</param>
/// <param name="Origin">Who it came from.</param>
/// <param name="Layer">Its cascade layer, or <see cref="CascadeLayers.Unlayered" />.</param>
/// <param name="Order">Its position in source order across every stylesheet loaded.</param>
/// <param name="BlocksSharing">
///     Whether matching it can depend on something a <see cref="StyleSharingKey" /> does not carry.
///     See <see cref="StyleRuleSet.SharingIsSound" />.
/// </param>
public readonly record struct StyleRule(
    Selector Selector,
    DeclarationRange Declarations,
    StyleOrigin Origin,
    int Layer,
    int Order,
    bool BlocksSharing
);

/// <summary>Every rule that has been loaded, indexed and ready to cascade.</summary>
/// <remarks>
///     <para>
///         The rule index buckets by the rightmost compound and hands an element single digits'
///         worth of candidates; this holds what those candidates <i>are</i>, which the matcher never
///         needed to know.
///     </para>
///     <para>
///         Rules from every origin live in one list and one index. They have to: an element's
///         candidates are found once, and separating the origins would mean three lookups and three
///         blooms to save a comparison that <see cref="CascadePrecedence" /> does anyway.
///     </para>
/// </remarks>
public sealed class StyleRuleSet {
    readonly List<StyleRule> rules = [];
    readonly List<Declaration> declarations = [];
    readonly SelectorTable table;

    /// <summary>Creates an empty set.</summary>
    /// <param name="table">The table compiled selectors point into.</param>
    /// <param name="names">The table selector names are interned in.</param>
    /// <param name="properties">The table property names are interned in.</param>
    /// <param name="values">The table declaration values are interned in.</param>
    public StyleRuleSet(SelectorTable table, NameTable names, NameTable properties, NameTable values) {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(values);

        this.table = table;
        Names = names;
        Properties = properties;
        Values = values;
        Index = new RuleIndex(table);
        Layers = new CascadeLayers();
    }

    /// <summary>The table selector names are interned in.</summary>
    public NameTable Names { get; }

    /// <summary>The table property names are interned in.</summary>
    public NameTable Properties { get; }

    /// <summary>The table declaration values are interned in.</summary>
    public NameTable Values { get; }

    /// <summary>The bucketed index over these rules.</summary>
    public RuleIndex Index { get; }

    /// <summary>The layers these rules declared.</summary>
    public CascadeLayers Layers { get; }

    /// <summary>How many rules there are.</summary>
    public int Count => rules.Count;

    /// <summary>
    ///     Whether the style-sharing cache may be used at all with this rule set.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A <see cref="StyleSharingKey" /> says what an element <i>is</i>: its parent, tag, id,
    ///         classes, state and inline style. Three things a rule can match on are not in there, and
    ///         any one of them makes sharing wrong rather than merely coarse.
    ///     </para>
    ///     <para>
    ///         <b>Position among siblings.</b> <c>li:nth-child(2n)</c> and <c>.selected + .row</c>
    ///         tell apart two elements the key cannot, and sharing would hand the second one the
    ///         first one's style.
    ///     </para>
    ///     <para>
    ///         <b>Attributes.</b> <c>[data-kind=danger]</c> likewise. Attributes could be hashed into
    ///         the key, unlike position, and are not — an element may carry any number of arbitrary
    ///         ones, and hashing them all on every element to serve the rare stylesheet that selects
    ///         on one is the wrong trade.
    ///     </para>
    ///     <para>
    ///         <b>Contents.</b> <c>:empty</c> asks what an element holds, and the key describes only
    ///         what it is. Two lanes of a vector field are the same tag with the same classes under
    ///         the same parent, and the one that was given a name is not empty — which is the whole
    ///         reason a theme writes the rule.
    ///     </para>
    ///     <para>
    ///         Browsers decide this per element, refusing to share only the ones such rules could
    ///         reach. Vixen decides it per rule set: one such rule anywhere and sharing is off
    ///         everywhere. Coarser, and deliberately so — the per-element version wants the
    ///         invalidation machinery that is not written yet, and a sharing cache that is subtly
    ///         wrong is far worse than no sharing cache. What is <i>not</i> lost when this is false
    ///         is interning: identical elements still resolve to the same
    ///         <see cref="ComputedStyle" /> reference, they just each pay a cascade to get there.
    ///     </para>
    /// </remarks>
    public bool SharingIsSound { get; private set; } = true;

    /// <summary>A rule.</summary>
    /// <param name="rule">Its index.</param>
    /// <returns>The rule.</returns>
    public StyleRule this[int rule] => rules[rule];

    /// <summary>The declarations of a rule.</summary>
    /// <param name="range">The rule's declaration range.</param>
    /// <returns>The declarations.</returns>
    public ReadOnlySpan<Declaration> DeclarationsOf(DeclarationRange range) =>
        CollectionsMarshal.AsSpan(declarations).Slice(range.Start, range.Count);

    /// <summary>Adds a rule.</summary>
    /// <param name="selector">Its compiled selector.</param>
    /// <param name="block">Its declarations.</param>
    /// <param name="origin">Who it came from.</param>
    /// <param name="layer">Its layer, or <see cref="CascadeLayers.Unlayered" />.</param>
    /// <returns>The rule's index.</returns>
    public int Add(Selector selector, ReadOnlySpan<Declaration> block, StyleOrigin origin, int layer) {
        var start = declarations.Count;
        foreach (var declaration in block) {
            declarations.Add(declaration);
        }

        var blocksSharing = BlocksSharing(selector);
        if (blocksSharing) {
            SharingIsSound = false;
        }

        var order = Index.Add(selector);
        rules.Add(
            new StyleRule(
                selector,
                new DeclarationRange(start, block.Length),
                origin,
                layer,
                order,
                blocksSharing
            )
        );

        return order;
    }

    /// <summary>Whether matching a selector can depend on something a sharing key does not carry.</summary>
    /// <param name="selector">The selector.</param>
    /// <returns>Whether it does.</returns>
    bool BlocksSharing(Selector selector) {
        for (var c = 0; c < selector.Count; c++) {
            var compound = table.Compound(selector.Start + c);

            if (compound.Combinator is Combinator.NextSibling or Combinator.SubsequentSibling) {
                return true;
            }

            for (var s = 0; s < compound.Count; s++) {
                var simple = table.Simple(compound.Start + s);

                if (simple.Kind is SimpleSelectorKind.Position
                    or SimpleSelectorKind.Attribute
                    or SimpleSelectorKind.Empty) {
                    return true;
                }

                // `:is(:first-child)` hides one a level down, and a check that stopped at the top
                // level would be exactly the kind of soundness hole this flag exists to close.
                for (var n = 0; n < simple.NestedCount; n++) {
                    if (BlocksSharing(table.Nested(simple.NestedStart + n))) {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}
