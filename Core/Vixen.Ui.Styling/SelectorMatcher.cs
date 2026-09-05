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

            case SimpleSelectorKind.Lang:
                return MatchesLanguage(tree, element, simple);

            case SimpleSelectorKind.Not: {
                for (var i = 0; i < simple.NestedCount; i++) {
                    if (MatchFrom(tree, element, table.Nested(simple.NestedStart + i), table.Nested(simple.NestedStart + i).Count - 1, useBloom)) {
                        return false;
                    }
                }

                return true;
            }

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

    /// <summary>Whether the language in force at an element is in the selector's range.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The climb is the whole difference between this and <c>[lang|="de"]</c>.</b> A
    ///         <c>lang</c> attribute states the language of an element <i>and everything under it</i>
    ///         — that is what the attribute means in every document language that has one — so the
    ///         subject of the comparison is the nearest declaration at or above this element, and
    ///         <see cref="StyleTree.Language" /> under all of them. An attribute selector compares
    ///         against what this element itself declares, which for a span inside a German paragraph
    ///         is nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>lang=""</c> is not a declaration and does not stop the climb</b>, which is
    ///         both what HTML means by it and what this tree forces. <c>UiElement.Language = null</c>
    ///         writes the empty tag rather than removing the attribute, because <c>StyleTree</c>
    ///         appends attributes and never removes one — so an empty value has to read back as "no
    ///         declaration here" or taking a declaration off would strand the subtree. This walk is
    ///         <c>UiElement.ResolvedLanguage</c>'s walk, and the two have to agree: one decides what
    ///         a rule selects and the other decides what the shaper is told, about the same words.
    ///     </para>
    /// </remarks>
    static bool MatchesLanguage(StyleTree tree, int element, SimpleSelector simple) {
        for (var node = element; node >= 0; node = tree.ParentOf(node)) {
            if (!tree.TryGetAttribute(node, simple.NameId, out var valueId) || valueId == NameTable.None) {
                continue;
            }

            var declared = tree.Names.NameOf(valueId);

            if (declared.Length != 0) {
                return Filters(declared, tree.Names.NameOf(simple.ValueId));
            }
        }

        return Filters(tree.Language, tree.Names.NameOf(simple.ValueId));
    }

    /// <summary>RFC 4647 § 3.3.2 extended filtering, which is what Selectors 4 § 9.1 asks for.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Not "starts with the range", which is the implementation everybody writes and is
    ///         wrong in both directions.</b> Too wide, because <c>den</c> starts with <c>de</c> and
    ///         is Dendi, not German — the comparison is over <i>subtags</i>, so a range only matches
    ///         at a hyphen. Too narrow, because a range's subtags need not be <i>consecutive</i> in
    ///         the tag: <c>de-AT</c> matches <c>de-Latn-AT</c>, since a script subtag somebody added
    ///         does not stop the document being Austrian German.
    ///     </para>
    ///     <para>
    ///         The rule that makes the skipping safe is the singleton one: a one-character subtag
    ///         opens an extension (<c>-u-</c>, <c>-x-</c>) and everything after it is a private or
    ///         extension namespace, so a range subtag may not be matched past one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No <c>*</c> branch, deliberately.</b> The specification allows a wildcard subtag
    ///         and <see cref="SelectorCompiler" /> can never produce one, because ExCSS refuses to
    ///         parse the form. A branch nothing can reach is this repository's commonest defect, and
    ///         the compiler's remarks carry the measurement.
    ///     </para>
    /// </remarks>
    /// <param name="tag">The language in force, as a BCP-47 tag. Empty is undetermined.</param>
    /// <param name="range">The selector's range.</param>
    /// <returns>Whether the tag is in the range.</returns>
    internal static bool Filters(ReadOnlySpan<char> tag, ReadOnlySpan<char> range) {
        if (tag.IsEmpty || range.IsEmpty) {
            return false;
        }

        if (!Next(ref tag, out var tagPart) || !Next(ref range, out var rangePart)) {
            return false;
        }

        if (!rangePart.Equals(tagPart, StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        while (Next(ref range, out rangePart)) {
            while (true) {
                if (!Next(ref tag, out tagPart)) {
                    return false;
                }

                if (rangePart.Equals(tagPart, StringComparison.OrdinalIgnoreCase)) {
                    break;
                }

                // A singleton opens an extension, and a range subtag may not be matched inside one.
                if (tagPart.Length == 1) {
                    return false;
                }
            }
        }

        return true;

        static bool Next(ref ReadOnlySpan<char> rest, out ReadOnlySpan<char> part) {
            if (rest.IsEmpty) {
                part = default;
                return false;
            }

            var hyphen = rest.IndexOf('-');

            if (hyphen < 0) {
                part = rest;
                rest = default;
            } else {
                part = rest[..hyphen];
                rest = rest[(hyphen + 1)..];
            }

            return true;
        }
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
