// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Text.Tests;

/// <summary>CSS Text 3 § 5.2's <c>word-break</c>, at both of the stages it touches.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Measured with hand-written advances rather than with a shaped face, and that is the
///         only way half of this file can exist at all.</b> <c>keep-all</c> is a rule about CJK — it
///         suppresses the opportunities UAX#14 finds between two ideographs and between two Hangul
///         syllables, and there is no such opportunity anywhere in Latin, because LB28 already forbids
///         a break between two letters. None of the fourteen embedded test fonts covers a CJK code
///         point, so a shaped fixture would be measuring <c>.notdef</c> boxes. The wrapper's
///         second overload takes the advances from its caller, which makes the widths an input to the
///         test rather than a property of a font that does not exist here.
///     </para>
///     <para>
///         ⚠ <b>Every wrapping assertion below distinguishes <c>word-break</c> from
///         <c>overflow-wrap</c>, which is the mistake this feature exists not to make.</b>
///         <see cref="TextWrapMode.Anywhere" /> is consulted in one branch — "nothing fits: one
///         unbreakable run is wider than the whole line" — so it can only ever break a word that had
///         nowhere else to go, and it breaks it at the <i>start</i> of a fresh line. <c>break-all</c>
///         changes the opportunity list, so the line before it is filled to the edge as well. The two
///         produce different lines from the same text and the same width, and
///         <see cref="Break_all_fills_the_line_where_anywhere_only_rescues_the_word" /> is that
///         difference written down.
///     </para>
/// </remarks>
public class WordBreakTests {
    static List<int> Opportunities(string text, WordBreakMode mode) {
        var found = new List<int>();
        LineBreaker.Collect(text, found, mode);

        return found;
    }

    /// <summary>Wraps a paragraph whose every character is one unit wide.</summary>
    /// <remarks>
    ///     One advance per UTF-16 index, so a line's width is the number of characters on it and every
    ///     expectation below can be read off the string. The array is one longer than the text, which
    ///     is the contract <c>LineWrapper.Advances</c> builds to.
    /// </remarks>
    static List<string> Wrap(string text, float width, TextWrapMode mode, WordBreakMode wordBreak) {
        var advances = new float[text.Length + 1];
        Array.Fill(advances, 1f, 0, text.Length);

        var lines = new List<WrappedLine>();
        LineWrapper.Wrap(text, advances, width, lines, mode, wordBreak);

        return lines.Select(line => text[line.Start..line.End]).ToList();
    }

    [Fact]
    public void Normal_offers_nothing_between_two_letters() =>
        Assert.Equal([5], Opportunities("hello", WordBreakMode.Normal));

    [Fact]
    public void Break_all_offers_a_break_between_every_pair_of_letters() =>
        Assert.Equal([1, 2, 3, 4, 5], Opportunities("hello", WordBreakMode.BreakAll));

    /// <summary>
    ///     ⚠ <b><c>break-all</c> keeps every UAX#14 rule that is not about letters, and this is the
    ///     assertion that says the implementation is a class substitution rather than a scattering of
    ///     boundaries.</b>
    /// </summary>
    /// <remarks>
    ///     A line may not begin with a comma — LB15d — and the naive implementation of this property,
    ///     "add an opportunity at every grapheme boundary", breaks that rule along with LB13's closing
    ///     brackets, LB14's opening ones and LB21's non-starters. Resolving the letters to the
    ///     ideographic class instead leaves all four rules written against the punctuation classes,
    ///     where they go on holding.
    /// </remarks>
    [Fact]
    public void Break_all_still_refuses_to_begin_a_line_with_a_comma() {
        var found = Opportunities("ab,cd", WordBreakMode.BreakAll);

        Assert.Contains(1, found);
        Assert.DoesNotContain(2, found);
    }

    /// <summary>And the same for a bracket, on both sides of it.</summary>
    [Fact]
    public void Break_all_keeps_a_bracket_with_what_it_encloses() {
        var found = Opportunities("a(bc)d", WordBreakMode.BreakAll);

        // No break after the opening bracket (LB14) and none before the closing one (LB13).
        Assert.DoesNotContain(2, found);
        Assert.DoesNotContain(4, found);

        // But the letters inside it are still broken between, or the mode did nothing at all.
        Assert.Contains(3, found);
    }

    /// <summary>
    ///     ⚠ A break offered inside a character is a broken character, not a narrow line.
    /// </summary>
    [Fact]
    public void Break_all_does_not_offer_a_break_inside_a_grapheme_cluster() {
        // A base letter, its combining acute, then another letter.
        var found = Opportunities("áb", WordBreakMode.BreakAll);

        Assert.DoesNotContain(1, found);
        Assert.Contains(2, found);
    }

    /// <summary>
    ///     ⚠ <b><c>keep-all</c> is measured on the script it is for.</b> Latin has no implicit
    ///     opportunity between two letters to suppress, so a fixture written in it would pass against
    ///     an engine that ignored the property — which is what
    ///     <see cref="Keep_all_leaves_latin_exactly_as_it_was" /> asserts on purpose, one test down.
    /// </summary>
    [Theory]
    [InlineData("中文")] // Two Han ideographs.
    [InlineData("한글")] // Two precomposed Hangul syllables.
    public void Keep_all_suppresses_the_break_between_two_letter_units(string text) {
        Assert.Equal([1, 2], Opportunities(text, WordBreakMode.Normal));
        Assert.Equal([2], Opportunities(text, WordBreakMode.KeepAll));
    }

    /// <summary>A space is not a letter, so <c>keep-all</c> leaves the break after one alone.</summary>
    /// <remarks>
    ///     What the property means is "do not break <i>inside</i> a word". A <c>keep-all</c> that also
    ///     suppressed the space would make a CJK paragraph one unbreakable line, which is the failure
    ///     that looks like the feature working.
    /// </remarks>
    [Fact]
    public void Keep_all_still_breaks_at_a_space() =>
        Assert.Contains(3, Opportunities("中文 中文", WordBreakMode.KeepAll));

    /// <summary>And a hard newline is not something a <c>word-break</c> gets to decline.</summary>
    [Fact]
    public void Keep_all_still_breaks_at_a_newline() =>
        Assert.Contains(2, Opportunities("中\n文", WordBreakMode.KeepAll));

    /// <summary>
    ///     ⚠ <b>Latin is byte-identical under <c>keep-all</c>, and that is the correct answer rather
    ///     than a gap.</b> LB28 already forbids a break between two letters, so there is nothing for
    ///     the property to suppress — which is exactly why every other <c>keep-all</c> assertion in
    ///     this file is written in CJK.
    /// </summary>
    [Fact]
    public void Keep_all_leaves_latin_exactly_as_it_was() {
        const string Prose = "the quick brown fox, jumps over-the lazy dog";

        Assert.Equal(
            Opportunities(Prose, WordBreakMode.Normal),
            Opportunities(Prose, WordBreakMode.KeepAll)
        );
    }

    /// <summary>
    ///     ⚠ <b>The difference between <c>word-break</c> and <c>overflow-wrap</c>, as two different
    ///     sets of lines from one string and one width.</b>
    /// </summary>
    /// <remarks>
    ///     <c>anywhere</c> reaches its branch only once the long word is alone on a line, so the line
    ///     <i>before</i> it keeps the ragged edge it always had: <c>"ab "</c> and then five letters.
    ///     <c>break-all</c> put an opportunity after every letter, so greedy first-fit fills the first
    ///     line to the edge and the paragraph comes out shifted by two characters. A test that only
    ///     counted lines, or only asserted that nothing overflowed, would pass for either.
    /// </remarks>
    [Fact]
    public void Break_all_fills_the_line_where_anywhere_only_rescues_the_word() {
        Assert.Equal(
            ["ab ", "cdefg", "hij"],
            Wrap("ab cdefghij", 5f, TextWrapMode.Anywhere, WordBreakMode.Normal)
        );

        Assert.Equal(
            ["ab cd", "efghi", "j"],
            Wrap("ab cdefghij", 5f, TextWrapMode.Word, WordBreakMode.BreakAll)
        );
    }

    /// <summary><c>break-all</c> needs no help from <c>overflow-wrap</c> to keep a word in its box.</summary>
    [Fact]
    public void Break_all_keeps_every_line_inside_the_width() =>
        Assert.Equal(
            ["antid", "isest", "ablis", "hment"],
            Wrap("antidisestablishment", 5f, TextWrapMode.Word, WordBreakMode.BreakAll)
        );

    /// <summary><c>keep-all</c> makes a CJK run overflow rather than break inside itself.</summary>
    [Fact]
    public void Keep_all_lets_an_ideographic_run_overflow() {
        Assert.Equal(["中文", "中文"], Wrap("中文中文", 2f, TextWrapMode.Word, WordBreakMode.Normal));
        Assert.Equal(["中文中文"], Wrap("中文中文", 2f, TextWrapMode.Word, WordBreakMode.KeepAll));
    }

    /// <summary>
    ///     ⚠ <b>The two properties compose, and this is the combination one merged enum could not have
    ///     expressed.</b>
    /// </summary>
    /// <remarks>
    ///     <c>word-break: keep-all</c> with <c>overflow-wrap: anywhere</c> is what a narrow CJK column
    ///     wants and what CSS defines: no break between two ideographs while there is any other choice,
    ///     and a squeeze rather than an overflow when there is not. Had <c>keep-all</c> been a third
    ///     value of <see cref="TextWrapMode" />, one of the two declarations would have had to lose.
    /// </remarks>
    [Fact]
    public void Keep_all_and_anywhere_are_both_honoured() {
        // A space to break at, so `keep-all` has the choice it is supposed to prefer …
        Assert.Equal(
            ["中文 ", "中文"],
            Wrap("中文 中文", 2f, TextWrapMode.Anywhere, WordBreakMode.KeepAll)
        );

        // … and none, so the last resort still fires and nothing overflows.
        Assert.Equal(
            ["中文", "中文"],
            Wrap("中文中文", 2f, TextWrapMode.Anywhere, WordBreakMode.KeepAll)
        );
    }

    /// <summary>The lines still partition the text under either mode.</summary>
    /// <remarks>
    ///     <c>LineWrapTests</c> samples this over <see cref="TextWrapMode" />; the two new modes change
    ///     the opportunity list itself, which is the input that invariant is most easily broken by —
    ///     a <c>keep-all</c> that dropped an opportunity without reconsidering it would lose a word.
    /// </remarks>
    [Theory]
    [InlineData(WordBreakMode.Normal)]
    [InlineData(WordBreakMode.BreakAll)]
    [InlineData(WordBreakMode.KeepAll)]
    public void The_lines_partition_the_text(WordBreakMode mode) {
        const string Text = "ab 中文cd-ef\n한글 ghij";

        for (var width = 0f; width <= 12f; width += 1f) {
            var pieces = Wrap(Text, width, TextWrapMode.Word, mode);
            Assert.Equal(Text, string.Concat(pieces));
            Assert.All(pieces, piece => Assert.NotEqual(string.Empty, piece));
        }
    }
}
