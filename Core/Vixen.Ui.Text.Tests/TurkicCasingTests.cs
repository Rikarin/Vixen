// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Text.Tests;

/// <summary>Casing that depends on the language, and the trap that made it look untestable.</summary>
/// <remarks>
///     <para>
///         <b>Turkish and Azerbaijani have two <c>i</c>s.</b> Dotted <c>i</c>/<c>İ</c> and dotless
///         <c>ı</c>/<c>I</c> are two letters, not two cases of one, so <c>i</c> uppercases to
///         <c>İ</c> and <c>I</c> lowercases to <c>ı</c>. Every other language pairs them the other
///         way round. A <c>text-transform: uppercase</c> on a Turkish word gets it wrong in a way
///         that is not a typographic nicety — it is a different word.
///     </para>
///     <para>
///         ⚠ <b>The trap <c>LanguageTests</c> records, and the reason this file names no culture.</b>
///         This repository's test assemblies run in globalization-invariant mode, so
///         <c>CultureInfo.GetCultureInfo("tr-TR")</c> throws and every <c>ToUpper(culture)</c> is
///         the invariant one. An implementation routed through .NET's culture casing would be
///         indistinguishable from a broken one here — so the mapping is a table keyed on the BCP-47
///         tag, no <c>CultureInfo</c> is consulted, and these assertions can be false.
///         <see cref="The_invariant_mode_trap_is_real_and_is_what_forced_the_table" /> pins that.
///     </para>
/// </remarks>
public class TurkicCasingTests {
    [Fact]
    public void Turkish_uppercases_a_dotted_i_to_a_dotted_capital() {
        Assert.Equal("İSTANBUL", TransformedText.Of("istanbul", TextTransform.Uppercase, "tr").Text);

        // The language-independent answer, which is what every other language wants and what this
        // engine produced for Turkish too until the tag reached here.
        Assert.Equal("ISTANBUL", TransformedText.Of("istanbul", TextTransform.Uppercase).Text);
        Assert.Equal("ISTANBUL", TransformedText.Of("istanbul", TextTransform.Uppercase, "en").Text);
    }

    [Fact]
    public void Turkish_lowercases_a_capital_I_to_a_dotless_i() {
        Assert.Equal("ısparta", TransformedText.Of("ISPARTA", TextTransform.Lowercase, "tr").Text);
        Assert.Equal("isparta", TransformedText.Of("ISPARTA", TextTransform.Lowercase).Text);
    }

    /// <summary>The subtag, not the whole tag, and both languages the UCD lists.</summary>
    [Theory]
    [InlineData("tr", true)]
    [InlineData("tr-TR", true)]
    [InlineData("TR", true)]
    [InlineData("az", true)]
    [InlineData("az-Latn-AZ", true)]
    [InlineData("en", false)]
    [InlineData("", false)]
    [InlineData("tri", false)]
    [InlineData("trk", false)]
    public void The_mapping_is_selected_by_the_primary_subtag(string language, bool turkic) =>
        Assert.Equal(
            turkic ? "İ" : "I",
            TransformedText.Of("i", TextTransform.Uppercase, language).Text
        );

    /// <summary>Capitalize titlecases with the same table, because titlecase is a case too.</summary>
    [Fact]
    public void Capitalize_uses_the_Turkish_letter_as_well() =>
        Assert.Equal("İzmir", TransformedText.Of("izmir", TextTransform.Capitalize, "tr").Text);

    /// <summary>U+0130 lowercases to a plain <c>i</c> in Turkish and to two characters elsewhere.</summary>
    /// <remarks>
    ///     ⚠ <b>The language-independent row is the one that expands</b>, which makes this a map test
    ///     as well as a casing one: <c>İ</c> lowercases to <c>i</c> + COMBINING DOT ABOVE outside
    ///     Turkish, because a language that does not have the dotless letter has to keep the dot
    ///     visible. So the same character is index-preserving in one language and not in another.
    /// </remarks>
    [Fact]
    public void A_dotted_capital_lowercases_two_ways() {
        var turkish = TransformedText.Of("İ", TextTransform.Lowercase, "tr");
        Assert.Equal("i", turkish.Text);
        Assert.True(turkish.IsIdentity);

        var invariant = TransformedText.Of("İ", TextTransform.Lowercase);
        Assert.Equal("i̇", invariant.Text);
        Assert.False(invariant.IsIdentity);
    }

    /// <summary>SpecialCasing's one Turkic row whose input is two characters.</summary>
    /// <remarks>
    ///     A capital <c>I</c> written with a separate COMBINING DOT ABOVE is a dotted capital spelt
    ///     the long way, so it lowercases to a plain <c>i</c> and the mark goes with it. Leaving the
    ///     mark behind would draw a second dot over a letter that already has one — and it moves an
    ///     index, so the map has to come back non-identity.
    /// </remarks>
    [Fact]
    public void A_capital_I_with_a_combining_dot_lowercases_to_one_character_in_Turkish() {
        var transformed = TransformedText.Of("AİB", TextTransform.Lowercase, "tr");

        Assert.Equal("aib", transformed.Text);
        Assert.False(transformed.IsIdentity);

        // The `b` is index 3 in what was written and index 2 in what is drawn.
        Assert.Equal(2, transformed.ToDrawn(3));
        Assert.Equal(3, transformed.ToSource(2));

        // Without the tag the dot stays, dotless, and nothing moves.
        Assert.Equal("ai̇b", TransformedText.Of("AİB", TextTransform.Lowercase).Text);
    }

    /// <summary>A capital <c>I</c> separated from its dot by another mark comes out dotless, and should not.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This asserts the wrong answer on purpose, because the row is producing wrong
    ///         output today rather than merely missing.</b> SpecialCasing's two <c>tr</c> rows are
    ///         <c>0049; 0131; …; tr Not_Before_Dot</c> and <c>0307; ; …; tr After_I</c>, and
    ///         "before dot" skips an intervening character that is neither combining class 0 nor
    ///         230. U+0316 is class 220, so the dot above is still the dot of this <c>I</c>: the
    ///         dotless row does not apply, the letter is a plain <c>i</c>, and <c>After_I</c> takes
    ///         the dot away — <c>i̖</c>. What comes out instead is a dotless <c>ı</c> with
    ///         both marks left on it, which is a different letter in Turkish and a second dot in any
    ///         language.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is not a bug with a fix in this file.</b> The condition is a question about
    ///         combining classes and no generated table in this assembly carries them, which is
    ///         #913; this row and the two Lithuanian ones are the same predicate and land together.
    ///         Pinned rather than left to be rediscovered, exactly as
    ///         <c>LithuanianCasingTests.A_capital_i_with_a_separate_mark_is_left_alone</c> is — when
    ///         the class data lands, this is a test that has to change, and that is the point of it.
    ///     </para>
    ///     <para>
    ///         The second assertion is the boundary and is <i>right</i> today: with nothing between
    ///         them the two-character row in <c>TransformedText.Of</c> does fire. So what separates
    ///         the working case from the broken one is exactly one intervening mark, which is the
    ///         difference a reader of either of them should be able to see.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_capital_I_whose_dot_is_one_mark_away_is_still_dotless_and_should_not_be() {
        // `I`, COMBINING GRAVE ACCENT BELOW (canonical combining class 220), COMBINING DOT ABOVE —
        // in escapes, because the three of them draw as one glyph and the whole test is which three.
        var transformed = TransformedText.Of("İ̖", TextTransform.Lowercase, "tr");

        // Owed: "i̖".
        Assert.Equal("ı̖̇", transformed.Text);

        // The immediate-neighbour case, which is right: the letter loses its separate dot.
        Assert.Equal("i", TransformedText.Of("İ", TextTransform.Lowercase, "tr").Text);
    }

    /// <summary>The instrument's own check: the obvious implementation could not have been shown wrong.</summary>
    /// <remarks>
    ///     ⚠ <b>This asserts a fact about the test host, not about Vixen</b>, and it is here because
    ///     the whole design of the mapping above rests on it. If globalization-invariant mode is ever
    ///     turned off for this assembly, this goes red — and at that point a reviewer should know
    ///     that "just call <c>ToUpper(culture)</c>" has become writable and is still refused, for the
    ///     reason <c>TransformedText.Of</c> gives: a document must case identically on every machine.
    /// </remarks>
    [Fact]
    public void The_invariant_mode_trap_is_real_and_is_what_forced_the_table() {
        Assert.Throws<CultureNotFoundException>(() => CultureInfo.GetCultureInfo("tr-TR"));
        Assert.Equal(string.Empty, CultureInfo.CurrentCulture.Name);
    }
}
