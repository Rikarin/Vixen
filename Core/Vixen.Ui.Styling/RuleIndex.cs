// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Styling;

/// <summary>Which rules an element could possibly match, without testing them all.</summary>
/// <remarks>
///     <para>
///         Naive matching is O(elements × rules), and a real stylesheet has thousands of rules while
///         a real panel has thousands of elements. The standard answer, and the one every browser
///         uses: bucket each rule by the most selective thing in its <i>rightmost</i> compound — its
///         id if it has one, else a class, else a tag — and give an element only the buckets its own
///         id, classes and tag name reach. Thousands of candidates become single digits.
///     </para>
///     <para>
///         The rightmost compound is the right key precisely because matching runs right to left: it
///         is the only part guaranteed to be tested against the element itself.
///     </para>
///     <para>
///         Rules whose rightmost compound names nothing — <c>* &gt; .x</c>'s tail, <c>:hover</c> on
///         its own — go in a universal bucket every element gets. That bucket is the one to keep an
///         eye on: a stylesheet whose rules mostly end in a bare pseudo-class has defeated the index,
///         and <see cref="UniversalCount" /> is here so that is visible rather than merely slow.
///     </para>
/// </remarks>
public sealed class RuleIndex(SelectorTable table) {
    readonly SelectorTable table = table ?? throw new ArgumentNullException(nameof(table));
    readonly Dictionary<int, List<int>> byId = [];
    readonly Dictionary<int, List<int>> byClass = [];
    readonly Dictionary<int, List<int>> byTag = [];
    readonly List<int> universal = [];
    readonly List<Selector> selectors = [];

    /// <summary>How many rules have been indexed.</summary>
    public int Count => selectors.Count;

    /// <summary>How many rules no bucket could narrow, and which every element must therefore test.</summary>
    public int UniversalCount => universal.Count;

    /// <summary>The selector behind a rule index.</summary>
    /// <param name="rule">The rule.</param>
    /// <returns>Its selector.</returns>
    public Selector SelectorOf(int rule) => selectors[rule];

    /// <summary>Adds a rule.</summary>
    /// <param name="selector">Its compiled selector.</param>
    /// <returns>The rule's index.</returns>
    public int Add(Selector selector) {
        var rule = selectors.Count;
        selectors.Add(selector);

        var rightmost = table.Compound(selector.Start + selector.Count - 1);
        var key = MostSelectiveKey(rightmost);

        switch (key.Kind) {
            case SimpleSelectorKind.Id:
                Bucket(byId, key.NameId).Add(rule);
                break;
            case SimpleSelectorKind.Class:
                Bucket(byClass, key.NameId).Add(rule);
                break;
            case SimpleSelectorKind.Type:
                Bucket(byTag, key.NameId).Add(rule);
                break;
            default:
                universal.Add(rule);
                break;
        }

        return rule;
    }

    /// <summary>Collects the rules <paramref name="element" /> could match.</summary>
    /// <param name="tree">The element store.</param>
    /// <param name="element">The element.</param>
    /// <param name="candidates">Receives the rule indices, in ascending order.</param>
    /// <remarks>
    ///     Ascending order matters: it is document order, which is the last tie-break the cascade
    ///     uses when specificity and layer agree. Merging the buckets rather than concatenating them
    ///     is what keeps it, and it costs a sort of a single-digit list.
    /// </remarks>
    public void Collect(StyleTree tree, StyleNodeId element, List<int> candidates) {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(candidates);

        var index = tree.Validate(element);
        candidates.Clear();
        candidates.AddRange(universal);

        if (byTag.TryGetValue(tree.TagOf(index), out var tagged)) {
            candidates.AddRange(tagged);
        }

        var id = tree.IdOf(index);
        if (id != NameTable.None && byId.TryGetValue(id, out var identified)) {
            candidates.AddRange(identified);
        }

        tree.ForEachIdentifier(index, name => {
            if (byClass.TryGetValue(name, out var classed)) {
                candidates.AddRange(classed);
            }
        });

        candidates.Sort();
    }

    (SimpleSelectorKind Kind, int NameId) MostSelectiveKey(CompoundSelector compound) {
        var best = (Kind: SimpleSelectorKind.Universal, NameId: NameTable.None);

        for (var i = 0; i < compound.Count; i++) {
            var simple = table.Simple(compound.Start + i);
            switch (simple.Kind) {
                case SimpleSelectorKind.Id:
                    return (SimpleSelectorKind.Id, simple.NameId);
                case SimpleSelectorKind.Class when best.Kind != SimpleSelectorKind.Class:
                    best = (SimpleSelectorKind.Class, simple.NameId);
                    break;
                case SimpleSelectorKind.Type when best.Kind == SimpleSelectorKind.Universal:
                    best = (SimpleSelectorKind.Type, simple.NameId);
                    break;
                default:
                    break;
            }
        }

        return best;
    }

    static List<int> Bucket(Dictionary<int, List<int>> buckets, int key) {
        if (buckets.TryGetValue(key, out var bucket)) {
            return bucket;
        }

        bucket = [];
        buckets[key] = bucket;
        return bucket;
    }
}
