// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Text.Tests;

/// <summary>Lowercasing Greek, which is a lookaround and not a table lookup.</summary>
/// <remarks>
///     <para>
///         <b>Sigma has two lowercase forms and which one to write depends on the word.</b> σ
///         everywhere but the end, ς at the end — so <c>ΟΔΟΣ</c> lowercases to <c>οδος</c> and
///         <c>ΣΟΦΟΣ</c> to <c>σοφός</c>'s spelling of both. <c>Rune.ToLowerInvariant</c> answers σ
///         always, because a simple case mapping is one code point to one by definition and the
///         choice is not a property of U+03A3.
///     </para>
///     <para>
///         ⚠ <b>This is not a Greek-locale feature.</b> SpecialCasing.txt's <c>Final_Sigma</c> row
///         carries no language tag, so it applies in every document in every locale — unlike the
///         Turkic rows next door, which is why this file names no language at all.
///     </para>
///     <para>
///         ⚠ <b>The instrument note that applies to <c>TurkicCasingTests</c> applies here.</b> These
///         assemblies run in globalization-invariant mode, so anything routed through
///         <c>CultureInfo</c> would answer the invariant mapping and could not be told from a broken
///         implementation. <see cref="The_invariant_mode_trap_is_why_this_cannot_lean_on_dotnet" />
///         pins that this is not what happens.
///     </para>
/// </remarks>
public class FinalSigmaTests {
    [Theory]
    // The word this bug is always demonstrated with: Greek for "street", on every street sign.
    [InlineData("ΟΔΟΣ", "οδος")]

    // ⚠ Both halves of the condition in one word. The leading sigma is followed by a cased letter
    // so it stays σ; the trailing one is not, so it turns ς. An implementation that only looked
    // backwards would write ςοφος and pass a fixture built out of ΟΔΟΣ alone.
    [InlineData("ΣΟΦΟΣ", "σοφος")]

    // Interior, with the word continuing after it.
    [InlineData("ΚΟΣΜΟΣ", "κοσμος")]

    // Two words: the first sigma ends a word even though the string continues.
    [InlineData("ΟΔΟΣ ΜΟΥ", "οδος μου")]

    // ⚠ A sigma with nothing cased before it is not final, which is the half that reads as
    // pedantry until a stray capital appears on its own. Unicode says ς needs a letter to end.
    [InlineData("Σ", "σ")]
    [InlineData("A Σ B", "a σ b")]

    // Case-ignorable characters do not break the lookaround in either direction: a combining
    // acute after the sigma still leaves it word-final, and one before it still leaves the alpha
    // visible to the backward walk.
    [InlineData("ΟΔΟΣ́", "οδος́")]
    [InlineData("ΑΣ", "ας")]
    public void A_sigma_is_final_only_at_the_end_of_a_word(string source, string expected) =>
        Assert.Equal(expected, TransformedText.Of(source, TextTransform.Lowercase).Text);

    /// <summary>⚠ And a full stop between two letters does not end the word.</summary>
    /// <remarks>
    ///     <c>Case_Ignorable</c> is five general categories <i>and</i> three word-break classes, and
    ///     the second half is the one an implementation drops. A full stop between letters is
    ///     <c>MidNumLet</c> and an apostrophe is <c>Single_Quote</c>, so the sigma before either is
    ///     still followed by a cased letter and is not final.
    ///     <para>
    ///         ⚠ <b>Every row here has a cased letter on both sides, and that is what makes them
    ///         discriminating.</b> Without the word-break half a full stop is neither cased nor
    ///         ignorable, the forward walk stops on it and answers "not followed by a letter", and
    ///         the sigma turns ς. A row like <c>Σ.Σ</c> would come out the same either way — the
    ///         first sigma has nothing cased before it whichever rule is used — and would pin
    ///         nothing.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("ΑΣ.Α", "ασ.α")]
    [InlineData("ΑΣ'Α", "ασ'α")]
    [InlineData("Σ.Σ.Σ", "σ.σ.ς")]
    public void A_case_ignorable_between_two_letters_does_not_end_the_word(string source, string expected) =>
        Assert.Equal(expected, TransformedText.Of(source, TextTransform.Lowercase).Text);

    /// <summary>Uppercasing and capitalising are untouched, because the condition is lowercase-only.</summary>
    /// <remarks>
    ///     SpecialCasing.txt gives the row an uppercase and a titlecase column of U+03A3 — the
    ///     mapping only differs in the lowercase one. ⚠ And the round trip is not the identity:
    ///     <c>ς</c> uppercases to <c>Σ</c>, so <c>οδος</c> uppercased and lowercased again is the
    ///     same string, which is the property that makes a transform safe to apply twice.
    /// </remarks>
    [Fact]
    public void Only_lowercasing_asks_the_question() {
        Assert.Equal("ΟΔΟΣ", TransformedText.Of("οδος", TextTransform.Uppercase).Text);
        Assert.Equal("Οδος", TransformedText.Of("οδος", TextTransform.Capitalize).Text);

        var round = TransformedText.Of("ΟΔΟΣ", TextTransform.Lowercase).Text;
        Assert.Equal("ΟΔΟΣ", TransformedText.Of(round, TextTransform.Uppercase).Text);
    }

    /// <summary>The mapping is one code point for one, so no index moves.</summary>
    /// <remarks>
    ///     Unlike <c>ß</c> or the Turkic <c>I</c> + dot row, this one changes no length — so
    ///     <c>TransformedText</c> must still take the cheap path and hand back the identity map. A
    ///     transform that allocated the two arrays for it would be correct and would put an
    ///     allocation on every Greek label in an interface.
    /// </remarks>
    [Fact]
    public void A_final_sigma_moves_no_index() {
        var transformed = TransformedText.Of("ΟΔΟΣ ΜΟΥ", TextTransform.Lowercase);

        Assert.Equal("οδος μου", transformed.Text);

        for (var i = 0; i <= transformed.Source.Length; i++) {
            Assert.Equal(i, transformed.ToDrawn(i));
            Assert.Equal(i, transformed.ToSource(i));
        }
    }

    /// <summary>⚠ .NET's own invariant lowercasing gets this wrong, which is what makes the test real.</summary>
    /// <remarks>
    ///     The same trap <c>TurkicCasingTests</c> records one row over: these assemblies run in
    ///     globalization-invariant mode, so a <c>CultureInfo("el-GR")</c> would throw and every
    ///     <c>ToLower</c> is the invariant one. Asserting what the framework answers is how this file
    ///     shows its own assertions can be false rather than merely restating <c>string.ToLower</c>.
    /// </remarks>
    [Fact]
    public void The_invariant_mode_trap_is_why_this_cannot_lean_on_dotnet() {
        Assert.Equal("οδοσ", "ΟΔΟΣ".ToLowerInvariant());
        Assert.Equal("οδοσ", "ΟΔΟΣ".ToLower(CultureInfo.InvariantCulture));
        Assert.Equal("οδος", TransformedText.Of("ΟΔΟΣ", TextTransform.Lowercase).Text);
    }
}
