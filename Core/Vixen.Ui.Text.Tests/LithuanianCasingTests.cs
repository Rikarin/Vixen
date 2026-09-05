// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Text.Tests;

/// <summary>Lithuanian keeps the dot on a lowercase <c>i</c> that has an accent above it.</summary>
/// <remarks>
///     <para>
///         <b>The precomposed capitals are the whole of it.</b> U+00CC, U+00CD and U+0128 each
///         lowercase in Lithuanian to <c>i</c> + COMBINING DOT ABOVE + the accent, rather than to the
///         precomposed small letter whose dot the accent has taken the place of. Lithuanian
///         orthography treats the dot as part of the letter and the accent as something stacked over
///         it, so the precomposed form is missing a mark a reader expects to see.
///     </para>
///     <para>
///         ⚠ <b>These are the three <c>lt</c> rows in SpecialCasing.txt that carry a language and no
///         condition, and that is why they could land while the rest of the set could not.</b> The
///         other Lithuanian rows — <c>More_Above</c> on <c>I</c>, <c>J</c> and U+012E, and
///         <c>After_Soft_Dotted</c> on U+0307 — are conditional on combining class 230 and on the
///         <c>Soft_Dotted</c> property, and no generated table in this assembly carries either. So
///         "half of them is worse than none" is true of those and not of these: the two groups are
///         disjoint, and the rows left undone are exactly as unimproved after this as before rather
///         than newly wrong. <see cref="A_capital_i_with_a_separate_mark_is_left_alone" /> is that
///         boundary, asserted rather than described.
///     </para>
///     <para>
///         ⚠ <b>No <c>CultureInfo</c>, for the reason <c>FinalSigmaTests</c> gives it.</b> These
///         assemblies run in globalization-invariant mode, where
///         <c>CultureInfo.GetCultureInfo("lt-LT")</c> throws — so an implementation routed through
///         .NET's culture casing would answer the invariant mapping here and could not be told from a
///         broken one. The language arrives as a tag on the element and is compared ordinally.
///     </para>
///     <para>
///         ⚠ Every string below is written in <c>\u</c> escapes rather than as literal marks. A
///         combining sequence is invisible in a diff, two of these fixtures differ from each other by
///         one scalar, and the precomposed U+00CC and the two-scalar <c>I</c>-plus-grave look
///         identical on screen while being the whole distinction the last test rests on.
///     </para>
/// </remarks>
public class LithuanianCasingTests {
    /// <summary>U+00CC LATIN CAPITAL LETTER I WITH GRAVE, one scalar.</summary>
    const string PrecomposedCapitalIGrave = "\u00CC";

    /// <summary>U+0307 COMBINING DOT ABOVE — the mark Lithuanian keeps and nothing else writes.</summary>
    const string DotAbove = "\u0307";

    [Theory]
    // U+00CC → i + dot above + grave.
    [InlineData("\u00CC", "\u0069\u0307\u0300")]

    // U+00CD → i + dot above + acute.
    [InlineData("\u00CD", "\u0069\u0307\u0301")]

    // U+0128 → i + dot above + tilde.
    [InlineData("\u0128", "\u0069\u0307\u0303")]
    public void A_precomposed_capital_lowercases_with_its_dot_kept(string source, string expected) =>
        Assert.Equal(expected, TransformedText.Of(source, TextTransform.Lowercase, "lt").Text);

    /// <summary>And without the language it is the ordinary precomposed small letter.</summary>
    /// <remarks>
    ///     ⚠ <b>The half that says the mapping is language-tagged rather than universal.</b> A file
    ///     with only the positive rows passes against an implementation that spelt every document
    ///     the Lithuanian way, which would put a stray dot under every accent in French.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("en")]
    [InlineData("fr-FR")]
    [InlineData("tr")]
    public void Every_other_language_gets_the_precomposed_letter(string? language) =>
        Assert.Equal(
            "\u00EC",
            TransformedText.Of(PrecomposedCapitalIGrave, TextTransform.Lowercase, language).Text
        );

    /// <summary>A tag that merely starts with <c>lt</c> is a different language.</summary>
    /// <remarks>
    ///     ⚠ <c>ltg</c> is Latgalian and <c>lto</c> is Tsotso; both are well-formed primary subtags
    ///     and neither is Lithuanian. A prefix comparison takes them, which is the mistake this row
    ///     refuses — and it is the same shape the Turkic check has always had to avoid for <c>tra</c>.
    /// </remarks>
    [Theory]
    [InlineData("ltg")]
    [InlineData("lto")]
    public void A_longer_primary_subtag_is_not_lithuanian(string language) =>
        Assert.Equal(
            "\u00EC",
            TransformedText.Of(PrecomposedCapitalIGrave, TextTransform.Lowercase, language).Text
        );

    /// <summary>The expansion moves the index map, which is what makes this a <c>TransformedText</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>One character becoming three is the failure this whole type exists for.</b> A caret
    ///     after the letter has to land after all three units, and every unit of the expansion has to
    ///     map back to the single source character it came from — there is no caret position inside a
    ///     letter the author typed once.
    /// </remarks>
    [Fact]
    public void The_expansion_carries_the_index_map_with_it() {
        var transformed = TransformedText.Of(PrecomposedCapitalIGrave + "A", TextTransform.Lowercase, "lt");

        // Lowercased, so the `A` is an `a`: the fixture is a lowercase transform and the trailing
        // letter is there for the index arithmetic rather than to be left alone.
        Assert.Equal("\u0069\u0307\u0300a", transformed.Text);

        // The `A` is source index 1 and drawn index 3, which is the arithmetic a one-to-one map gets
        // wrong and which nothing else in this string would reveal.
        Assert.Equal(3, transformed.ToDrawn(1));

        Assert.Equal(0, transformed.ToSource(0));
        Assert.Equal(0, transformed.ToSource(1));
        Assert.Equal(0, transformed.ToSource(2));
        Assert.Equal(1, transformed.ToSource(3));
    }

    /// <summary>A capital <c>I</c> followed by its own mark is untouched, and that is the boundary.</summary>
    /// <remarks>
    ///     ⚠ <b>The row that is still owed, pinned as owed rather than left to be rediscovered.</b>
    ///     SpecialCasing's <c>lt More_Above</c> says <c>I</c> + a mark above should lowercase to
    ///     <c>i</c> + DOT ABOVE + that mark, the same shape as the three above — but
    ///     <c>More_Above</c> is defined on combining class 230, which no generated table here
    ///     carries, and guessing at it would be wrong for every mark that is not above. So the answer
    ///     stays the language-independent one, deliberately. When the class data lands this is the
    ///     test that has to change, which is the point of writing it down now.
    /// </remarks>
    [Fact]
    public void A_capital_i_with_a_separate_mark_is_left_alone() {
        // `I` and a combining grave — two scalars, and *not* the precomposed U+00CC above, which is
        // the pair this whole file is spelt in escapes to keep apart.
        var transformed = TransformedText.Of("\u0049\u0300", TextTransform.Lowercase, "lt");

        Assert.Equal("\u0069\u0300", transformed.Text);
        Assert.False(transformed.Text.Contains(DotAbove, StringComparison.Ordinal));
    }
}
