// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Styling;

/// <summary>Decides whether a selector matches an element.</summary>
/// <remarks>
///     <para>
///         Right to left, which is the opposite of how a selector reads and the only way to do it at
///         speed. <c>.sidebar .row .cell</c> read left to right means finding every
///         <c>.sidebar</c>, then every <c>.row</c> under it, then every <c>.cell</c> under that —
///         work proportional to the document. Read right to left it means: is this element a
///         <c>.cell</c>? Almost always no, and the answer costs one test. Every browser does this.
///     </para>
///     <para>
///         The ancestor bloom is checked before any descendant combinator is walked, so a rule whose
///         ancestor part cannot possibly be present costs two loads instead of a climb to the root.
///     </para>
///     <para>
///         Backtracking is real and is not avoided: <c>a b c</c> may find a <c>b</c> that has no
///         <c>a</c> above it and have to keep climbing. Child and next-sibling combinators do not
///         backtrack, because they have exactly one candidate.
///     </para>
/// </remarks>
public sealed class SelectorMatcher(SelectorTable table) {
    readonly SelectorTable table = table ?? throw new ArgumentNullException(nameof(table));

    /// <summary>Whether <paramref name="selector" /> matches <paramref name="element" />.</summary>
    /// <param name="tree">The element store.</param>
    /// <param name="element">The element.</param>
    /// <param name="selector">The selector.</param>
    /// <param name="useBloom">
    ///     Whether to consult the ancestor bloom filter. Off is the brute-force path the oracle test
    ///     compares against; it must always give the same answer, only more slowly.
    /// </param>
    /// <returns>Whether it matches.</returns>
    public bool Matches(StyleTree tree, StyleNodeId element, Selector selector, bool useBloom = true) {
        ArgumentNullException.ThrowIfNull(tree);
        return MatchFrom(tree, tree.Validate(element), selector, selector.Count - 1, useBloom);
    }

    bool MatchFrom(StyleTree tree, int element, Selector selector, int compoundIndex, bool useBloom) {
        var compound = table.Compound(selector.Start + compoundIndex);
        if (!MatchesCompound(tree, element, compound, useBloom)) {
            return false;
        }

        if (compoundIndex == 0) {
            return true;
        }

        // The combinator recorded on *this* compound says how it reaches the one before it.
        var previous = table.Compound(selector.Start + compoundIndex - 1);
        switch (compound.Combinator) {
            case Combinator.Child: {
                var parent = tree.ParentOf(element);
                return parent >= 0 && MatchFrom(tree, parent, selector, compoundIndex - 1, useBloom);
            }

            case Combinator.NextSibling: {
                var sibling = tree.PreviousSiblingOf(element);
                return sibling >= 0 && MatchFrom(tree, sibling, selector, compoundIndex - 1, useBloom);
            }

            case Combinator.Descendant: {
                if (useBloom && !CouldHaveAncestor(tree, element, previous)) {
                    return false;
                }

                for (var ancestor = tree.ParentOf(element); ancestor >= 0; ancestor = tree.ParentOf(ancestor)) {
                    if (MatchFrom(tree, ancestor, selector, compoundIndex - 1, useBloom)) {
                        return true;
                    }
                }

                return false;
            }

            case Combinator.SubsequentSibling: {
                for (var sibling = tree.PreviousSiblingOf(element); sibling >= 0; sibling = tree.PreviousSiblingOf(sibling)) {
                    if (MatchFrom(tree, sibling, selector, compoundIndex - 1, useBloom)) {
                        return true;
                    }
                }

                return false;
            }

            default:
                return false;
        }
    }

    /// <summary>
    ///     Whether the bloom rules out every element that could satisfy <paramref name="compound" />
    ///     being an ancestor.
    /// </summary>
    /// <remarks>
    ///     Only the parts of a compound that name something can be asked about — a bloom holds names.
    ///     A compound of nothing but <c>:hover</c> is unfilterable and answers yes, which is correct
    ///     and is why the filter is an optimisation rather than a decision.
    /// </remarks>
    bool CouldHaveAncestor(StyleTree tree, int element, CompoundSelector compound) {
        var bloom = tree.BloomOf(element);

        for (var i = 0; i < compound.Count; i++) {
            var simple = table.Simple(compound.Start + i);
            var nameId = simple.Kind switch {
                SimpleSelectorKind.Type or SimpleSelectorKind.Id or SimpleSelectorKind.Class => simple.NameId,
                _ => NameTable.None
            };

            if (nameId == NameTable.None) {
                continue;
            }

            if (!bloom.MightContain(nameId)) {
                return false;
            }
        }

        return true;
    }

    bool MatchesCompound(StyleTree tree, int element, CompoundSelector compound, bool useBloom) {
        for (var i = 0; i < compound.Count; i++) {
            if (!MatchesSimple(tree, element, table.Simple(compound.Start + i), useBloom)) {
                return false;
            }
        }

        return true;
    }

    bool MatchesSimple(StyleTree tree, int element, SimpleSelector simple, bool useBloom) {
        switch (simple.Kind) {
            case SimpleSelectorKind.Universal:
                return true;

            case SimpleSelectorKind.Type:
                return tree.TagOf(element) == simple.NameId;

            case SimpleSelectorKind.Id:
                return tree.IdOf(element) == simple.NameId;

            case SimpleSelectorKind.Class:
                return simple.NameId != NameTable.None && tree.HasClass(element, simple.NameId);

            case SimpleSelectorKind.Attribute:
                return MatchesAttribute(tree, element, simple);

            case SimpleSelectorKind.State:
                return (tree.StateOf(element) & simple.State) == simple.State;

            case SimpleSelectorKind.Position:
                return MatchesPosition(tree, element, simple);

            // ⚠ Text counts, and getting this wrong inverts the selector. CSS asks for no child
            // *nodes*, and in the DOM a run of text is one — Vixen hangs text off the element
            // instead, so a test of the child count alone would call every label in the document
            // empty. Both rules in the tree that wanted `:empty` are on leaves whose content is
            // `UiElement.Text` and nothing else, so counting children alone would have hidden them
            // always rather than never: a dead rule replaced by a wrong one.
            case SimpleSelectorKind.Empty:
                return tree.ChildCountOf(element) == 0 && !tree.HasTextAt(element);

            case SimpleSelectorKind.Not: {
                for (var i = 0; i < simple.NestedCount; i++) {
                    if (MatchFrom(tree, element, table.Nested(simple.NestedStart + i), table.Nested(simple.NestedStart + i).Count - 1, useBloom)) {
                        return false;
                    }
                }

                return true;
            }

            // ⚠ A walk of the whole subtree, and there is no bloom to shorten it. The ancestor bloom
            // answers "could an element with this name be *above* me", which is the question every
            // other combinator asks; `:has()` asks about below, and a descendant bloom would have to
            // be rebuilt on every insertion rather than inherited once at creation. So the cost is
            // real and is the reason doc 09 deferred this — see `StyleInvalidator`, where the other
            // half of that cost lives.
            case SimpleSelectorKind.Has:
                return MatchesInSubtree(tree, element, simple, useBloom);

            case SimpleSelectorKind.Is: {
                for (var i = 0; i < simple.NestedCount; i++) {
                    if (MatchFrom(tree, element, table.Nested(simple.NestedStart + i), table.Nested(simple.NestedStart + i).Count - 1, useBloom)) {
                        return true;
                    }
                }

                return false;
            }

            default:
                return false;
        }
    }

    /// <summary>Whether any descendant of <paramref name="element" /> satisfies a <c>:has()</c>.</summary>
    /// <remarks>
    ///     Depth-first and short-circuiting, so a <c>:has()</c> that is satisfied by the first child
    ///     costs one test rather than a subtree. The one that is <i>not</i> satisfied costs the whole
    ///     subtree, and that is the case a document pays for on every restyle.
    /// </remarks>
    bool MatchesInSubtree(StyleTree tree, int element, SimpleSelector simple, bool useBloom) {
        var owner = new StyleNodeId(element);
        var children = tree.GetChildCount(owner);

        for (var i = 0; i < children; i++) {
            var child = tree.GetChild(owner, i).Index;

            for (var n = 0; n < simple.NestedCount; n++) {
                var nested = table.Nested(simple.NestedStart + n);

                // Every argument is one compound — the compiler refuses the rest — so this is a test
                // of the descendant itself and never a climb back out of the subtree.
                if (MatchFrom(tree, child, nested, nested.Count - 1, useBloom)) {
                    return true;
                }
            }

            if (MatchesInSubtree(tree, child, simple, useBloom)) {
                return true;
            }
        }

        return false;
    }

    static bool MatchesAttribute(StyleTree tree, int element, SimpleSelector simple) {
        if (!tree.TryGetAttribute(element, simple.NameId, out var valueId)) {
            return false;
        }

        if (simple.Operator == AttributeOperator.Present) {
            return true;
        }

        // An operator with nothing to compare against never matches, which is also what a browser
        // does with `[a^=""]`.
        if (simple.ValueId == NameTable.None) {
            return false;
        }

        var actual = tree.Names.NameOf(valueId);
        var expected = tree.Names.NameOf(simple.ValueId);

        if (expected.Length == 0) {
            return false;
        }

        return simple.Operator switch {
            AttributeOperator.Equals => string.Equals(actual, expected, StringComparison.Ordinal),
            AttributeOperator.Prefix => actual.StartsWith(expected, StringComparison.Ordinal),
            AttributeOperator.Suffix => actual.EndsWith(expected, StringComparison.Ordinal),
            AttributeOperator.Substring => actual.Contains(expected, StringComparison.Ordinal),
            AttributeOperator.DashMatch => string.Equals(actual, expected, StringComparison.Ordinal)
                || (actual.StartsWith(expected, StringComparison.Ordinal) && actual.Length > expected.Length && actual[expected.Length] == '-'),
            AttributeOperator.Includes => Includes(actual, expected),
            _ => false
        };

        static bool Includes(string actual, string expected) {
            foreach (var range in actual.AsSpan().Split(' ')) {
                if (actual.AsSpan(range).SequenceEqual(expected)) {
                    return true;
                }
            }

            return false;
        }
    }

    static bool MatchesPosition(StyleTree tree, int element, SimpleSelector simple) {
        var index = tree.IndexInParentOf(element);
        var siblings = tree.SiblingCountOf(element);

        return simple.Position switch {
            PositionTest.First => index == 0,
            PositionTest.Last => index == siblings - 1,
            PositionTest.Only => siblings == 1,
            PositionTest.Nth => MatchesNth(index + 1, simple.Step, simple.Offset),
            PositionTest.NthLast => MatchesNth(siblings - index, simple.Step, simple.Offset),

            // ⚠ The of-type tests count the same way the four above do, over a different sequence:
            // the siblings sharing this element's tag. Nothing is stored for that, so it is walked.
            PositionTest.FirstOfType => tree.TypeIndexOf(element) == 1,
            PositionTest.LastOfType => tree.TypeIndexOf(element) == tree.TypeCountOf(element),
            PositionTest.OnlyOfType => tree.TypeCountOf(element) == 1,
            PositionTest.NthOfType => MatchesNth(tree.TypeIndexOf(element), simple.Step, simple.Offset),
            PositionTest.NthLastOfType => MatchesNth(
                tree.TypeCountOf(element) - tree.TypeIndexOf(element) + 1,
                simple.Step,
                simple.Offset
            ),
            _ => false
        };

        // CSS counts from one, and `an+b` matches position p when p = a·n + b for some n ≥ 0.
        static bool MatchesNth(int position, int step, int offset) {
            if (step == 0) {
                return position == offset;
            }

            var difference = position - offset;
            return difference % step == 0 && difference / step >= 0;
        }
    }
}
