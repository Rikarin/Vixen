// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Styling.Utilities.Tests;

/// <summary>The eight <c>contain-*</c> classes, which were the whole of what #246 had left.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The property was built and the family was not, and that is a shape worth a test of
///         its own.</b> <c>ContainmentReader</c>, <c>LayoutStyle.Containment</c>, the size branch in
///         <c>CalculateLayoutImpl</c>, the block-formatting-context rule and the paint clip all
///         landed under #785 and #786, and a hand-written <c>.vcss</c> got every one of them. What
///         nobody could write was <c>contain-content</c> — so the ledger row read <c>absent</c> for a
///         feature that was finished, which is this repository's commonest defect wearing the other
///         face: not a finished thing nothing calls, but a finished thing nothing can name.
///     </para>
///     <para>
///         ⚠ <b>What the parity ledger cannot see, and why these are spelled out.</b>
///         <c>ParityLedgerTests</c> measures that <em>something</em> in the engine reads <c>contain</c>
///         and moves the row to <c>works</c> on that alone — it never looks at the value. A family
///         that emitted <c>contain:content</c> for every one of the eight classes would satisfy it
///         completely. So the assertion here is the mapping, one class at a time.
///     </para>
///     <para>
///         ⚠ <b><c>contain-style</c> is registered although the engine folds <c>style</c> to no
///         flag</b>, which reads like a family emitting something nothing acts on and is the
///         opposite. <c>ContainmentReader.Parse</c> drops the <em>whole</em> declaration on a word it
///         does not know, as CSS does — so an unregistered <c>style</c> would make
///         <c>contain: layout style</c> contain nothing, and <c>contain-content</c> and
///         <c>contain-strict</c> both expand through it. Understood-and-inert is a different state
///         from unparseable, and it is the one this engine is in.
///     </para>
/// </remarks>
public class ContainFamilyTests {
    /// <summary>Every class of the root emits the keyword it names, and not its neighbour's.</summary>
    /// <remarks>
    ///     The two aggregates are in the list because they are the two Tailwind spells that are not
    ///     the CSS keyword with a prefix cut off it — <c>contain-content</c> is <c>content</c>, which
    ///     CSS forbids beside anything else, and a family that had folded it out to
    ///     <c>layout paint style</c> here would be answering a question the reader answers.
    /// </remarks>
    [Theory]
    [InlineData("contain-none", "none")]
    [InlineData("contain-content", "content")]
    [InlineData("contain-strict", "strict")]
    [InlineData("contain-size", "size")]
    [InlineData("contain-inline-size", "inline-size")]
    [InlineData("contain-layout", "layout")]
    [InlineData("contain-paint", "paint")]
    [InlineData("contain-style", "style")]
    public void Each_containment_class_computes_to_its_own_keyword(string utility, string keyword) =>
        Assert.Equal(keyword, new UtilityFixture().Computed([utility], "contain"));

    /// <summary>And the root answers nothing to a keyword CSS has no such value for.</summary>
    /// <remarks>
    ///     ⚠ <b>The half that stops the theory above passing on a family that answers everything.</b>
    ///     A registration that fell through to the arbitrary-value path, or one whose dictionary was
    ///     consulted with the wrong comparer, would compute <c>contain-nonsense</c> to something —
    ///     and every row of the theory would still be green, because each of those eight names is
    ///     also a legal keyword.
    /// </remarks>
    [Fact]
    public void A_word_the_property_has_no_value_for_computes_to_nothing() =>
        Assert.Null(new UtilityFixture().Computed(["contain-nonsense"], "contain"));
}
