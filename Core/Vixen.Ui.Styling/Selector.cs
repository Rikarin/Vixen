// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Ui.Styling;

/// <summary>What joins two compound selectors.</summary>
public enum Combinator : byte {
    /// <summary>Nothing — this is the first compound in the selector.</summary>
    None,

    /// <summary>A space: an ancestor at any depth.</summary>
    Descendant,

    /// <summary>A <c>&gt;</c>: the immediate parent.</summary>
    Child,

    /// <summary>A <c>+</c>: the element immediately before.</summary>
    NextSibling,

    /// <summary>A <c>~</c>: any element before, under the same parent.</summary>
    SubsequentSibling
}

/// <summary>What one part of a compound selector tests.</summary>
public enum SimpleSelectorKind : byte {
    /// <summary><c>*</c> — matches anything.</summary>
    Universal,

    /// <summary>A tag name.</summary>
    Type,

    /// <summary>An <c>#id</c>.</summary>
    Id,

    /// <summary>A <c>.class</c>.</summary>
    Class,

    /// <summary>An <c>[attribute]</c> test.</summary>
    Attribute,

    /// <summary>A state such as <c>:hover</c>.</summary>
    State,

    /// <summary><c>:first-child</c>, <c>:last-child</c>, <c>:only-child</c> or <c>:nth-child()</c>.</summary>
    Position,

    /// <summary><c>:not()</c> — none of the nested selectors may match.</summary>
    Not,

    /// <summary><c>:is()</c> or <c>:where()</c> — one of the nested selectors must match.</summary>
    Is,

    /// <summary><c>:empty</c> — the element has neither children nor text.</summary>
    Empty
}

/// <summary>How an attribute selector compares.</summary>
public enum AttributeOperator : byte {
    /// <summary><c>[a]</c> — present at all.</summary>
    Present,

    /// <summary><c>[a=b]</c>.</summary>
    Equals,

    /// <summary><c>[a~=b]</c> — one of a whitespace-separated list.</summary>
    Includes,

    /// <summary><c>[a|=b]</c> — equal, or followed by a hyphen.</summary>
    DashMatch,

    /// <summary><c>[a^=b]</c>.</summary>
    Prefix,

    /// <summary><c>[a$=b]</c>.</summary>
    Suffix,

    /// <summary><c>[a*=b]</c>.</summary>
    Substring
}

/// <summary>Which position-in-parent test a <see cref="SimpleSelectorKind.Position" /> is.</summary>
public enum PositionTest : byte {
    /// <summary><c>:first-child</c>.</summary>
    First,

    /// <summary><c>:last-child</c>.</summary>
    Last,

    /// <summary><c>:only-child</c>.</summary>
    Only,

    /// <summary><c>:nth-child(an+b)</c>.</summary>
    Nth,

    /// <summary><c>:nth-last-child(an+b)</c>.</summary>
    NthLast,

    /// <summary><c>:first-of-type</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>The five of-type tests count a different sequence from the five above them, and that
    ///     is the whole reason they are separate members rather than a flag.</b> A child index is a
    ///     position in the parent's child list, which <see cref="StyleTree" /> already stores; an
    ///     of-type index is a position in the subsequence of siblings sharing this element's tag,
    ///     which nothing stores and which has to be counted. Folding them together would have made
    ///     <c>:nth-of-type(2)</c> answer <c>:nth-child(2)</c>'s question — the two agree on every
    ///     document whose children all have one tag, which is most fixtures and no real panel.
    /// </remarks>
    FirstOfType,

    /// <summary><c>:last-of-type</c>.</summary>
    LastOfType,

    /// <summary><c>:only-of-type</c>.</summary>
    OnlyOfType,

    /// <summary><c>:nth-of-type(an+b)</c>.</summary>
    NthOfType,

    /// <summary><c>:nth-last-of-type(an+b)</c>.</summary>
    NthLastOfType
}

/// <summary>One test inside a compound selector.</summary>
/// <param name="Kind">What it tests.</param>
/// <param name="NameId">The interned tag, id, class or attribute name, where there is one.</param>
/// <param name="ValueId">The interned attribute value, where there is one.</param>
/// <param name="Operator">How an attribute is compared.</param>
/// <param name="State">The state bit a <see cref="SimpleSelectorKind.State" /> requires.</param>
/// <param name="Position">Which position test this is.</param>
/// <param name="Step">The <c>a</c> of <c>an+b</c>.</param>
/// <param name="Offset">The <c>b</c> of <c>an+b</c>.</param>
/// <param name="NestedStart">Where this selector's nested selectors start.</param>
/// <param name="NestedCount">How many nested selectors it has.</param>
public readonly record struct SimpleSelector(
    SimpleSelectorKind Kind,
    int NameId = NameTable.None,
    int ValueId = NameTable.None,
    AttributeOperator Operator = AttributeOperator.Present,
    ElementState State = ElementState.None,
    PositionTest Position = PositionTest.First,
    int Step = 0,
    int Offset = 0,
    int NestedStart = 0,
    int NestedCount = 0
);

/// <summary>A run of simple selectors that all have to hold of the same element.</summary>
/// <param name="Combinator">How this compound relates to the one before it.</param>
/// <param name="Start">Where its simple selectors begin.</param>
/// <param name="Count">How many it has.</param>
public readonly record struct CompoundSelector(Combinator Combinator, int Start, int Count);

/// <summary>How specific a selector is, and therefore what it beats.</summary>
/// <param name="Ids">How many id selectors it contains.</param>
/// <param name="Classes">How many class, attribute and pseudo-class selectors.</param>
/// <param name="Types">How many type and pseudo-element selectors.</param>
public readonly record struct Specificity(int Ids, int Classes, int Types) : IComparable<Specificity> {
    /// <inheritdoc />
    public int CompareTo(Specificity other) {
        var ids = Ids.CompareTo(other.Ids);
        if (ids != 0) {
            return ids;
        }

        var classes = Classes.CompareTo(other.Classes);
        return classes != 0 ? classes : Types.CompareTo(other.Types);
    }

    /// <summary>Compares two specificities.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether the left is less specific.</returns>
    public static bool operator <(Specificity left, Specificity right) => left.CompareTo(right) < 0;

    /// <summary>Compares two specificities.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether the left is more specific.</returns>
    public static bool operator >(Specificity left, Specificity right) => left.CompareTo(right) > 0;

    /// <summary>Compares two specificities.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether the left is no more specific.</returns>
    public static bool operator <=(Specificity left, Specificity right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two specificities.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether the left is no less specific.</returns>
    public static bool operator >=(Specificity left, Specificity right) => left.CompareTo(right) >= 0;

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"({Ids},{Classes},{Types})");
}

/// <summary>One selector, compiled into something the matcher can run.</summary>
/// <param name="Start">Where its compounds begin.</param>
/// <param name="Count">How many compounds it has.</param>
/// <param name="Specificity">How specific it is.</param>
/// <remarks>
///     ⚠ <b>There is no pseudo-element here, and there used to be.</b> A fourth field held the
///     interned name of a <c>::before</c> or <c>::after</c>, and nothing in the matcher, the rule set
///     or the resolver ever read it — so the rule matched the originating element and coloured
///     <i>it</i>. A field written and never read is not a partial feature; it is a rule that means
///     something else. <see cref="SelectorCompiler" /> now refuses the selector with a diagnostic,
///     and the field is gone so that nothing can be built on top of a value that is always absent.
///     Doc 43's A12 is where generated boxes are planned. ⚠ It is <i>not</i> blocked on the
///     one-node-one-box invariant, which is what this used to say: that invariant has moved twice
///     since — inline fragmentation, then anonymous block boxes — and neither move reached this.
///     What A12 needs is a second <i>style</i> slot, not a second rectangle.
/// </remarks>
public readonly record struct Selector(int Start, int Count, Specificity Specificity);
