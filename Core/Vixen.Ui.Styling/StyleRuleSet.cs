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
/// <param name="Conditions">
///     The <c>@media</c> group it was written inside, or <see cref="MediaConditions.Unconditional" />.
///     <para>
///         ⚠ <b>On the rule rather than resolved away at load, which is what lets two windows of one
///         document answer <c>max-width</c> differently.</b> The loader used to evaluate the
///         condition and either emit the rules or drop them, which put the answer in the rule set —
///         and the rule set is shared by every surface, so the question could be asked once and was
///         asked of the primary window. See <see cref="MediaConditions" />.
///     </para>
/// </param>
/// <param name="Containers">
///     The <c>@container</c> group it was written inside, or
///     <see cref="ContainerConditions.Unconditional" />.
///     <para>
///         ⚠ <b>A second, independent id rather than another value in <paramref name="Conditions" />,
///         because the two conditions are about different subjects and both have to hold.</b>
///         <c>@media (min-width: 900px) { @container (min-width: 400px) { … } }</c> asks one question
///         of the window and one of a box; a single tagged chain would have to interleave two
///         verdict tables to answer it, and each table is already a conjunction that evaluates in one
///         ascending pass on its own.
///     </para>
/// </param>
public readonly record struct StyleRule(
    Selector Selector,
    DeclarationRange Declarations,
    StyleOrigin Origin,
    int Layer,
    int Order,
    bool BlocksSharing,
    int Conditions,
    int Containers
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
    ///     Whether the style-sharing cache may be used at all with this rule set, on one surface.
    /// </summary>
    /// <returns>Whether sharing is sound.</returns>
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
    ///     <para>
    ///         ⚠ <b>Per rule set <i>and</i> per surface, which the verdicts are for.</b> A rule inside
    ///         a <c>@media</c> block is loaded whether or not the block applies, so the answer would
    ///         otherwise be dragged down by a positional rule sealed behind a breakpoint no window is
    ///         at — a permanent, invisible cost paid by a document for a rule that never matches
    ///         anything.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And per container chain, which is why a blocker is a <i>pair</i>.</b> A rule
    ///         sealed inside both a <c>@media</c> and a <c>@container</c> only reaches an element when
    ///         both hold, so testing either alone would turn sharing off for a surface, or for a box,
    ///         that the rule cannot actually reach.
    ///     </para>
    /// </remarks>
    /// <param name="verdicts">Which <c>@media</c> groups hold on the element's surface.</param>
    /// <param name="containers">Which <c>@container</c> groups hold for the element's container chain.</param>
    public bool SharingIsSound(MediaVerdicts verdicts, ContainerVerdicts containers) {
        if (!unconditionalSharingIsSound) {
            return false;
        }

        // Empty for every stylesheet this repository ships, and empty for any sheet whose conditional
        // blocks hold nothing positional. A `foreach` over nothing is what this costs in the case that
        // matters.
        foreach (var (group, container) in conditionalBlockers) {
            if (verdicts.Holds(group) && containers.Holds(container)) {
                return false;
            }
        }

        return true;
    }

    bool unconditionalSharingIsSound = true;

    readonly HashSet<(int Conditions, int Containers)> conditionalBlockers = [];

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
    /// <param name="conditions">
    ///     The <c>@media</c> group it is inside, or <see cref="MediaConditions.Unconditional" />.
    /// </param>
    /// <param name="containers">
    ///     The <c>@container</c> group it is inside, or <see cref="ContainerConditions.Unconditional" />.
    /// </param>
    /// <returns>The rule's index.</returns>
    public int Add(
        Selector selector,
        ReadOnlySpan<Declaration> block,
        StyleOrigin origin,
        int layer,
        int conditions = MediaConditions.Unconditional,
        int containers = ContainerConditions.Unconditional
    ) {
        var start = declarations.Count;
        foreach (var declaration in block) {
            declarations.Add(declaration);
        }

        var blocksSharing = BlocksSharing(selector);

        if (blocksSharing) {
            // ⚠ Per group and not one flag, because a rule inside a `@media` is now loaded whether or
            // not the block applies anywhere. One `li:nth-child(2n)` sealed inside a breakpoint no
            // window is at would otherwise turn the sharing cache off for the whole document, for
            // ever — a silent halving of the restyle rate that no test could see, since sharing is an
            // optimisation and every style it skips is still correct.
            if (conditions == MediaConditions.Unconditional && containers == ContainerConditions.Unconditional) {
                unconditionalSharingIsSound = false;
            } else {
                conditionalBlockers.Add((conditions, containers));
            }
        }

        var order = Index.Add(selector);
        rules.Add(
            new StyleRule(
                selector,
                new DeclarationRange(start, block.Length),
                origin,
                layer,
                order,
                blocksSharing,
                conditions,
                containers
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
