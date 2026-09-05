// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Styling;

/// <summary>What changing one name on one element can reach.</summary>
/// <remarks>
///     Built once per rule set and read on every change. Three questions, and a name can answer yes
///     to any combination: does a rule test this name against the element itself, can it reach
///     descendants, can it reach later siblings.
/// </remarks>
sealed class InvalidationEntry {
    /// <summary>Whether some rule tests this name against the element carrying it.</summary>
    public bool MatchesSelf { get; set; }

    /// <summary>Whether a rule with this name as an ancestor can match anything below it.</summary>
    public bool ReachesDescendants { get; set; }

    /// <summary>Whether a rule with this name can match a later sibling.</summary>
    public bool ReachesSiblings { get; set; }

    /// <summary>Whether this name appears inside a <c>:has()</c>, so a rule above can notice it.</summary>
    /// <remarks>
    ///     ⚠ <b>The fourth direction, and the only one that goes up.</b> Every other entry here
    ///     describes a rule reaching from a name downward or rightward; this one says the opposite —
    ///     that adding <c>.error</c> to a field can restyle the <c>.card</c> containing it. It is
    ///     what <c>:has()</c> costs, and it is why doc 09 deferred the selector rather than the
    ///     matcher: matching one is a subtree walk, but invalidating one is a change of shape.
    /// </remarks>
    public bool ReachesAncestors { get; set; }

    /// <summary>
    ///     Whether anything it reaches has to be restyled regardless of what it is called.
    /// </summary>
    /// <remarks>
    ///     What <c>.selected *</c> and <c>.selected :hover</c> produce: a rule whose far end names
    ///     nothing, so no set of features can narrow it and every element under the change is a
    ///     candidate.
    /// </remarks>
    public bool ReachesEverything { get; set; }

    /// <summary>The names the far end of such a rule tests, when it tests any.</summary>
    /// <remarks>
    ///     This is what makes invalidation minimal rather than merely correct. Given
    ///     <c>.selected .cell { … }</c>, toggling <c>.selected</c> on a row has to restyle the row's
    ///     <c>.cell</c> descendants and nothing else — not the row's whole subtree, which is what a
    ///     scheme that only knew "this reaches downward" would do.
    /// </remarks>
    public HashSet<int> Features { get; } = [];
}

/// <summary>Works out which elements a change can possibly have restyled.</summary>
/// <remarks>
///     <para>
///         The premise is that recomputing is not an option. A <c>DataGrid</c> restyles when a row
///         is selected, and if selecting a row cost a pass over ten thousand cells the grid would be
///         unusable — so the question is not "what changed" but "what could a rule have noticed".
///     </para>
///     <para>
///         Answering it is a static property of the stylesheet. For every name a rule mentions, where
///         does it appear: against the element itself, as an ancestor, before a sibling combinator?
///         And when it appears as an ancestor, what does the far end of that rule test? That last
///         question is the one worth asking — it turns "restyle the subtree" into "restyle the
///         <c>.cell</c>s in the subtree".
///     </para>
///     <para>
///         ⚠ <b>This used to say that nothing needs to look upward, and <c>:has()</c> is what
///         changed it.</b> The old reasoning was sound and its premise expired: a selector could only
///         reach downward and rightward because Vixen refused <c>:has()</c> — doc 09's P2 decision,
///         and invalidation was the second thing that refusal bought after match cost. It is
///         supported now, so a name that appears inside a <c>:has()</c> argument carries
///         <see cref="InvalidationEntry.ReachesAncestors" /> and a change to it walks up.
///         <c>:focus-within</c> remains the apparent exception that is not one: it is stored as
///         element state and set explicitly, so it arrives as a change like any other.
///     </para>
///     <para>
///         ⚠ <b>The upward direction is narrowed by name but not by depth, and that asymmetry is
///         deliberate.</b> A downward reach can be cut at the changed element's subtree; an upward
///         one cannot, because the <c>:has()</c> may sit on the document root and every rule hanging
///         off it reaches back down. So a change to a <c>:has()</c>-mentioned name walks to the top
///         and then collects by feature from there — which is a pass over the document with a
///         set-membership test per element, on the changes that touch such a name and no others.
///         That is the cost doc 09 named, made explicit rather than hidden: see
///         <c>HasInvalidationTests</c>, which measures it as elements resolved.
///     </para>
/// </remarks>
public sealed class StyleInvalidator {
    readonly Dictionary<int, InvalidationEntry> entries = [];
    readonly SelectorTable table;
    int rulesRead;

    /// <summary>Creates an invalidator over a rule set.</summary>
    /// <param name="table">The table compiled selectors point into.</param>
    public StyleInvalidator(SelectorTable table) {
        ArgumentNullException.ThrowIfNull(table);
        this.table = table;
    }

    /// <summary>
    ///     Whether an element's own state change can be narrowed at all, or whether every rule has to
    ///     be reconsidered.
    /// </summary>
    /// <remarks>
    ///     A state change (<c>:hover</c>) is not a name, so it cannot be looked up in the map. It
    ///     always invalidates the element itself; whether it reaches further is
    ///     <see cref="StateReachesDescendants" />.
    /// </remarks>
    public bool StateReachesDescendants { get; private set; }

    /// <summary>Whether a state change can reach later siblings.</summary>
    public bool StateReachesSiblings { get; private set; }

    /// <summary>Whether what a state change reaches cannot be narrowed by name.</summary>
    public bool StateReachesEverything { get; private set; }

    /// <summary>Whether a state change can be noticed by a <c>:has()</c> above it.</summary>
    /// <remarks>
    ///     What <c>.card:has(:checked)</c> produces. A state is not a name, so it cannot be looked up
    ///     in the map — the same reason <see cref="StateReachesDescendants" /> exists one direction
    ///     over.
    /// </remarks>
    public bool StateReachesAncestors { get; private set; }

    /// <summary>Whether what a <c>:has()</c> reaches back down cannot be narrowed by name.</summary>
    bool AncestorReachesEverything { get; set; }

    /// <summary>The names the far end of a <c>:has()</c>-carrying rule tests.</summary>
    HashSet<int> AncestorFeatures { get; } = [];

    /// <summary>The names the far end of a state-carrying rule tests.</summary>
    /// <remarks>
    ///     A state is not a name and cannot be looked up in the map, but what a state-carrying rule
    ///     <i>reaches</i> narrows exactly the same way a class does. Leaving this out was a bug that
    ///     made every <c>:hover</c> above a combinator restyle the whole subtree — correct, and the
    ///     one thing invalidation exists not to do.
    /// </remarks>
    HashSet<int> StateFeatures { get; } = [];

    /// <summary>Reads every rule that has been added since the last time.</summary>
    /// <param name="rules">The rule set.</param>
    /// <remarks>
    ///     Incremental, so that loading a second stylesheet does not re-read the first. The map is
    ///     purely additive — a name that could reach descendants under one stylesheet still can when
    ///     another is loaded alongside it.
    /// </remarks>
    public void Read(StyleRuleSet rules) {
        ArgumentNullException.ThrowIfNull(rules);

        for (; rulesRead < rules.Count; rulesRead++) {
            Read(rules[rulesRead].Selector);
        }
    }

    /// <summary>Collects the elements a change to an element could have restyled.</summary>
    /// <param name="tree">The element store.</param>
    /// <param name="element">The element that changed.</param>
    /// <param name="changed">The names added or removed, if the change was to names.</param>
    /// <param name="stateChanged">Whether the element's pseudo state changed.</param>
    /// <param name="roots">Receives the elements to restyle, ascending and without duplicates.</param>
    /// <remarks>
    ///     The element itself is always included. Deciding it was untouched would mean proving no
    ///     rule mentions any of the changed names, and the proof costs about what re-resolving one
    ///     element costs — which the reference comparison then makes free anyway when nothing moved.
    /// </remarks>
    public void Collect(
        StyleTree tree,
        StyleNodeId element,
        ReadOnlySpan<int> changed,
        bool stateChanged,
        List<StyleNodeId> roots
    ) {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(roots);

        var index = tree.Validate(element);
        roots.Clear();
        roots.Add(element);

        var descendants = stateChanged && StateReachesDescendants;
        var siblings = stateChanged && StateReachesSiblings;
        var everything = stateChanged && StateReachesEverything;
        var ancestors = stateChanged && StateReachesAncestors;
        var features = new HashSet<int>();

        if (stateChanged) {
            features.UnionWith(StateFeatures);
        }

        foreach (var name in changed) {
            if (!entries.TryGetValue(name, out var entry)) {
                continue;
            }

            descendants |= entry.ReachesDescendants;
            siblings |= entry.ReachesSiblings;
            everything |= entry.ReachesEverything;
            ancestors |= entry.ReachesAncestors;
            features.UnionWith(entry.Features);
        }

        if (descendants) {
            CollectSubtree(tree, index, features, everything, roots);
        }

        if (siblings) {
            for (var sibling = NextSibling(tree, index); sibling >= 0; sibling = NextSibling(tree, sibling)) {
                // A sibling rule can carry on into that sibling's subtree — `.selected + .row .cell`
                // is one selector, and stopping at the sibling would miss its cells.
                CollectSubtree(tree, sibling, features, everything, roots, includeSelf: true);
            }
        }

        if (ancestors) {
            // ⚠ Every ancestor, and then the whole document narrowed by the far end's names. The
            // first half is the `:has()` itself — only an ancestor can carry one that this element
            // satisfies. The second half is everything hanging off such an ancestor: a rule like
            // `.card:has(.error) .title` restyles a title that is a *sibling* of the changed field,
            // and no walk rooted at the change can reach it. Sabotage-tested by deleting this second
            // half: the restyle oracle and `HasInvalidationTests` both go red, which is the
            // arrangement worth having — the count says what the walk costs and the oracle says it
            // is right, and neither alone would notice a change that made the other one wrong.
            var top = index;

            for (var ancestor = tree.ParentOf(index); ancestor >= 0; ancestor = tree.ParentOf(ancestor)) {
                roots.Add(new StyleNodeId(ancestor));
                top = ancestor;
            }

            if (top != index) {
                CollectSubtree(tree, top, AncestorFeatures, AncestorReachesEverything, roots, includeSelf: true);
            }
        }

        roots.Sort((left, right) => left.Index.CompareTo(right.Index));

        // ⚠ The ancestor walk and the collection below it can name the same element twice, and the
        // contract this method has always published is "without duplicates". `StyleUpdater` dedupes
        // anyway, so the alternative was a promise that was quietly false for one caller and true
        // for the tests that read the list directly.
        var unique = 0;

        for (var i = 0; i < roots.Count; i++) {
            if (i == 0 || roots[i] != roots[i - 1]) {
                roots[unique++] = roots[i];
            }
        }

        roots.RemoveRange(unique, roots.Count - unique);
    }

    static void CollectSubtree(
        StyleTree tree,
        int root,
        HashSet<int> features,
        bool everything,
        List<StyleNodeId> roots,
        bool includeSelf = false
    ) {
        var children = tree.GetChildCount(new StyleNodeId(root));

        if (includeSelf && Wanted(tree, root, features, everything)) {
            roots.Add(new StyleNodeId(root));
        }

        for (var i = 0; i < children; i++) {
            var child = tree.GetChild(new StyleNodeId(root), i);

            if (Wanted(tree, child.Index, features, everything)) {
                roots.Add(child);
            }

            CollectSubtree(tree, child.Index, features, everything, roots);
        }
    }

    static bool Wanted(StyleTree tree, int index, HashSet<int> features, bool everything) {
        if (everything) {
            return true;
        }

        if (features.Count == 0) {
            return false;
        }

        var wanted = false;
        tree.ForEachIdentifier(index, name => wanted |= features.Contains(name));
        return wanted;
    }

    static int NextSibling(StyleTree tree, int index) {
        var parent = tree.ParentOf(index);
        if (parent < 0) {
            return -1;
        }

        var position = tree.IndexInParentOf(index) + 1;
        var owner = new StyleNodeId(parent);
        return position < tree.GetChildCount(owner) ? tree.GetChild(owner, position).Index : -1;
    }

    void Read(Selector selector) {
        // Walk left to right, because what matters about a compound is what comes *after* it: a name
        // in the last compound is tested against the element, and a name anywhere else reaches
        // whatever the last compound describes.
        var rightmost = selector.Start + selector.Count - 1;
        var far = Describe(table.Compound(rightmost));

        for (var c = selector.Start; c <= rightmost; c++) {
            var compound = table.Compound(c);
            var isRightmost = c == rightmost;

            // The combinator is recorded on the compound it *follows*, so the reach of compound `c`
            // is decided by the combinator on `c + 1`.
            var reach = isRightmost ? Combinator.None : table.Compound(c + 1).Combinator;

            ForEachName(compound, name => {
                var entry = Entry(name);

                if (isRightmost) {
                    entry.MatchesSelf = true;
                    return;
                }

                switch (reach) {
                    case Combinator.Descendant or Combinator.Child:
                        entry.ReachesDescendants = true;
                        break;
                    case Combinator.NextSibling or Combinator.SubsequentSibling:
                        entry.ReachesSiblings = true;
                        break;
                    default:
                        break;
                }

                // A rule reaching sideways can carry on downward afterwards — in `.a + .b .c` the
                // `.a` reaches a sibling's descendants — so anything that is not the rightmost
                // compound contributes the far end's features either way.
                if (far.Count == 0) {
                    entry.ReachesEverything = true;
                } else {
                    entry.Features.UnionWith(far);
                }

                if (reach is Combinator.NextSibling or Combinator.SubsequentSibling && selector.Count > c + 2) {
                    entry.ReachesDescendants = true;
                }
            });
        }

        // ⚠ The upward direction, and it is read off the *argument* of every `:has()` rather than off
        // a combinator. A name inside `:has(.error)` is not a name the rule tests against the
        // element or against anything below it: it is a name that, when it appears anywhere in a
        // subtree, changes what the element above computes. `ForEachName` above has already recorded
        // it as `MatchesSelf`, which is true and is not enough.
        for (var c = selector.Start; c <= rightmost; c++) {
            var compound = table.Compound(c);

            for (var i = 0; i < compound.Count; i++) {
                var simple = table.Simple(compound.Start + i);

                if (simple.Kind != SimpleSelectorKind.Has) {
                    continue;
                }

                for (var n = 0; n < simple.NestedCount; n++) {
                    var nested = table.Nested(simple.NestedStart + n);

                    for (var inner = 0; inner < nested.Count; inner++) {
                        var argument = table.Compound(nested.Start + inner);
                        ForEachName(argument, name => Entry(name).ReachesAncestors = true);
                        StateReachesAncestors |= HasStateTest(argument);
                    }
                }

                if (far.Count == 0) {
                    AncestorReachesEverything = true;
                } else {
                    AncestorFeatures.UnionWith(far);
                }
            }
        }

        // A state test anywhere but the rightmost compound means a `:hover` on an ancestor can
        // restyle things below it — `.row:hover .cell` is the archetype, and it is why hovering a
        // grid row is not free.
        for (var c = selector.Start; c < rightmost; c++) {
            var compound = table.Compound(c);
            if (!HasStateTest(compound)) {
                continue;
            }

            var reach = table.Compound(c + 1).Combinator;
            StateReachesDescendants |= reach is Combinator.Descendant or Combinator.Child;
            StateReachesSiblings |= reach is Combinator.NextSibling or Combinator.SubsequentSibling;

            // And what it reaches narrows by the far end's names, exactly as a class does.
            if (far.Count == 0) {
                StateReachesEverything = true;
            } else {
                StateFeatures.UnionWith(far);
            }
        }
    }

    /// <summary>The names a compound tests, or an empty set if it tests none.</summary>
    HashSet<int> Describe(CompoundSelector compound) {
        var names = new HashSet<int>();
        var narrowed = false;

        for (var i = 0; i < compound.Count; i++) {
            var simple = table.Simple(compound.Start + i);

            switch (simple.Kind) {
                case SimpleSelectorKind.Type or SimpleSelectorKind.Id or SimpleSelectorKind.Class:
                    names.Add(simple.NameId);
                    narrowed = true;
                    break;

                case SimpleSelectorKind.Is: {
                    // `:is(.a, .b)` narrows to either, so both are features. `:not()` narrows to
                    // nothing nameable — an element matches it by *lacking* something — so it is
                    // deliberately absent from this switch and leaves the compound unnarrowed.
                    for (var n = 0; n < simple.NestedCount; n++) {
                        var nested = table.Nested(simple.NestedStart + n);
                        var inner = Describe(table.Compound(nested.Start + nested.Count - 1));

                        if (inner.Count == 0) {
                            return [];
                        }

                        names.UnionWith(inner);
                        narrowed = true;
                    }

                    break;
                }

                default:
                    break;
            }
        }

        return narrowed ? names : [];
    }

    bool HasStateTest(CompoundSelector compound) {
        for (var i = 0; i < compound.Count; i++) {
            if (table.Simple(compound.Start + i).Kind == SimpleSelectorKind.State) {
                return true;
            }
        }

        return false;
    }

    void ForEachName(CompoundSelector compound, Action<int> visit) {
        for (var i = 0; i < compound.Count; i++) {
            var simple = table.Simple(compound.Start + i);

            if (simple.Kind is SimpleSelectorKind.Type or SimpleSelectorKind.Id or SimpleSelectorKind.Class) {
                visit(simple.NameId);
            }

            for (var n = 0; n < simple.NestedCount; n++) {
                var nested = table.Nested(simple.NestedStart + n);
                for (var c = 0; c < nested.Count; c++) {
                    ForEachName(table.Compound(nested.Start + c), visit);
                }
            }
        }
    }

    InvalidationEntry Entry(int name) {
        if (entries.TryGetValue(name, out var entry)) {
            return entry;
        }

        entry = new InvalidationEntry();
        entries[name] = entry;
        return entry;
    }
}
