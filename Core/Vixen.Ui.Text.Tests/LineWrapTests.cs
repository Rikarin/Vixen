// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Xunit;

namespace Vixen.Ui.Text.Tests;

/// <summary>
///     Filling break opportunities into lines of a given width.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="LineBreaker" /> answers "where <i>may</i> a line end" and is judged by the
///         Consortium's suite. This answers "where does it end", which needs measured widths and
///         cannot be judged that way at all — so the assertions here are about the two things that
///         are true whatever the font: the lines <b>partition</b> the text, and none of them is wider
///         than it was allowed to be unless nothing narrower was available.
///     </para>
///     <para>
///         Measured with a real font, because the widths are the whole input. A stub with one advance
///         per character would agree with whatever this code did to it and would hide the one thing
///         most likely to be wrong — that a bidi run's glyphs come back in visual order.
///     </para>
/// </remarks>
public class LineWrapTests {
    const string Prose = "the quick brown fox jumps over the lazy dog";

    [Fact]
    public void Text_that_fits_is_one_line() {
        var shaped = Shape(Prose);
        var line = Assert.Single(LineWrapper.Lines(shaped, shaped.Advance * 2));

        Assert.Equal(0, line.Start);
        Assert.Equal(Prose.Length, line.Length);
        Assert.False(line.Mandatory);
    }

    [Fact]
    public void An_empty_paragraph_has_no_lines() => Assert.Empty(LineWrapper.Lines(Shape(string.Empty), 100f));

    [Fact]
    public void A_paragraph_is_broken_where_a_word_would_not_fit() {
        var shaped = Shape(Prose);
        var lines = LineWrapper.Lines(shaped, shaped.Advance / 4);

        Assert.True(lines.Count >= 4, $"only {lines.Count} lines");

        // Every break landed after a space, because that is where the opportunities are.
        foreach (var line in lines.Take(lines.Count - 1)) {
            Assert.Equal(' ', Prose[line.End - 1]);
        }
    }

    /// <summary>
    ///     ⚠ Trailing whitespace is measured out of a line, and it is not a nicety. A break falls
    ///     <i>after</i> a space, so the space belongs to the line before it — counted, a line ending
    ///     in one would wrap a word earlier than a line that does not, and a right-aligned paragraph
    ///     would have a ragged edge made of invisible characters.
    /// </summary>
    [Fact]
    public void A_lines_width_does_not_count_the_space_it_ends_with() {
        var withSpace = Shape("ab ");
        var without = Shape("ab");

        var one = Assert.Single(LineWrapper.Lines(withSpace, 10_000f));

        Assert.Equal(3, one.Length);
        Assert.Equal(without.Advance, one.Advance, 0.01f);
        Assert.True(withSpace.Advance > one.Advance, "the space had no width to leave out");
    }

    /// <summary>
    ///     ⚠ A hard newline is not something line filling gets to decline, however much room is left.
    /// </summary>
    [Fact]
    public void A_mandatory_break_ends_a_line_whatever_the_width() {
        var shaped = Shape("a\nb");
        var lines = LineWrapper.Lines(shaped, 10_000f);

        Assert.Equal(2, lines.Count);
        Assert.True(lines[0].Mandatory);
        Assert.Equal(0, lines[0].Start);
        Assert.Equal(2, lines[0].End);
        Assert.Equal(2, lines[1].Start);
        Assert.Equal(3, lines[1].End);
    }

    [Fact]
    public void A_word_wider_than_the_line_overflows_rather_than_splitting() {
        const string Text = "hi antidisestablishmentarianism";

        var shaped = Shape(Text);
        var narrow = Shape("hi ").Advance * 1.5f;
        var lines = LineWrapper.Lines(shaped, narrow);

        // Two lines: the short word, and the long one whole and over-wide.
        Assert.Equal(2, lines.Count);
        Assert.Equal("hi ", Text[lines[0].Start..lines[0].End]);
        Assert.Equal("antidisestablishmentarianism", Text[lines[1].Start..lines[1].End]);
        Assert.True(lines[1].Advance > narrow, "the long word was not allowed to overflow");
    }

    [Fact]
    public void The_same_word_is_split_when_it_is_allowed_to_be() {
        const string Text = "hi antidisestablishmentarianism";

        var shaped = Shape(Text);
        var narrow = Shape("hi ").Advance * 1.5f;
        var lines = LineWrapper.Lines(shaped, narrow, TextWrapMode.Anywhere);

        Assert.True(lines.Count > 2, $"only {lines.Count} lines");
        Assert.All(lines, line => Assert.True(line.Advance <= narrow + 0.01f, $"{line} is over"));
    }

    /// <summary>
    ///     ⚠ Splitting inside a word still splits at a <i>grapheme</i> boundary. Between a base letter
    ///     and its combining mark, or inside a surrogate pair, is not a narrow line — it is a line
    ///     with a broken character on it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This asserts the property and cannot tell which code enforces it.</b> Replacing the
    ///     wrapper's grapheme boundaries with every UTF-16 index changes nothing here, because a
    ///     cluster's whole advance is recorded at its first character and the shaper's clusters are
    ///     already reconciled with grapheme clusters — so every cut inside a cluster measures the same
    ///     as the cut at its end, and taking the largest that fits lands on the end anyway. The
    ///     property is worth pinning; the guard is labelled as insurance where it is written.
    /// </remarks>
    [Fact]
    public void Splitting_inside_a_word_does_not_split_a_character() {
        // Latin letters each followed by a combining acute, with no space anywhere.
        var text = string.Concat(Enumerable.Repeat("é", 20));
        var shaped = Shape(text);
        var lines = LineWrapper.Lines(shaped, shaped.Advance / 5, TextWrapMode.Anywhere);

        Assert.True(lines.Count > 1, "nothing was split");

        foreach (var line in lines) {
            Assert.True(line.Start % 2 == 0, $"{line} starts inside a cluster");
            Assert.NotEqual('́', text[line.Start]);
        }
    }

    /// <summary>
    ///     ⚠ The invariant that holds whatever the font, the width or the text: every character is on
    ///     exactly one line, in order, and nothing is invented or dropped. A wrapper that skipped the
    ///     opportunity it had just rejected would lose a word here, and that is a mistake nobody sees
    ///     until the one paragraph where it happens.
    /// </summary>
    [Fact]
    public void The_lines_partition_the_text() {
        var generator =
            from text in Gen.String[Gen.Char["abcde \n-"], 0, 60]
            from width in Gen.Float[0f, 4000f]
                // ⚠ All three, and `BreakWord` is the one that has to be here rather than the one that
                // was added for symmetry: it and `Anywhere` differ only in a room of nothing, and
                // `width` here starts at exactly nothing. A generator over two of the three would sample
                // that width for the mode whose behaviour at it is unchanged.
            from mode in Gen.OneOfConst(TextWrapMode.Word, TextWrapMode.Anywhere, TextWrapMode.BreakWord)
            select (text, width, mode);

        generator.Sample(
            input => {
                var lines = LineWrapper.Lines(Shape(input.text), input.width, input.mode);
                var next = 0;

                foreach (var line in lines) {
                    if (line.Start != next || line.Length <= 0) {
                        return false;
                    }

                    next = line.End;
                }

                return next == input.text.Length;
            },
            iter: 2_000
        );
    }

    /// <summary>
    ///     ⚠ A right-to-left run hands its glyphs back in <b>visual</b> order, so their clusters
    ///     descend. Measuring a line by running a sum along the glyph list therefore measures a bidi
    ///     paragraph as though it were Latin — which is why the width is accumulated per cluster
    ///     instead, and why this asserts against a shaped Arabic paragraph rather than a Latin one.
    /// </summary>
    [Fact]
    public void A_right_to_left_paragraph_is_measured_by_what_is_in_the_line_not_by_glyph_order() {
        var font = TestFonts.Load(TestFonts.Arabic);
        const string Text = "لسان لسان";

        var shaped = TextShaper.Shape(font, Text);
        var lines = LineWrapper.Lines(shaped, shaped.Advance * 0.6f);

        Assert.Equal(2, lines.Count);

        // Two identical words, so the two lines cover the same characters and measure the same. A
        // running sum in visual order would give the second line the first one's width.
        Assert.Equal(lines[0].Advance, lines[1].Advance, 0.01f);
        Assert.True(lines[0].Advance > 0);
    }

    [Fact]
    public void A_width_of_zero_puts_every_opportunity_on_its_own_line() {
        var lines = LineWrapper.Lines(Shape("a b c"), 0f);

        Assert.Equal(3, lines.Count);
        Assert.Equal(["a ", "b ", "c"], lines.Select(line => "a b c"[line.Start..line.End]));
    }

    /// <summary>Four two-letter words, one advance per character.</summary>
    /// <remarks>
    ///     ⚠ <b>A stub advance array here, where the rest of this file insists on a real font, and
    ///     the difference is what the question is.</b> Every other test asks how wide something is,
    ///     which a stub would answer by agreeing with whatever the code did. These ask <i>which of
    ///     the legal breaks is taken</i> — a choice among opportunities the breaker found — so a
    ///     uniform advance is not a weaker instrument, it is the one that makes the right answer
    ///     computable by hand: "aa bb cc dd" in a room of 8 holds three words and orphans the fourth.
    /// </remarks>
    const string Monospaced = "aa bb cc dd";

    /// <summary>⚠ <c>balance</c> keeps the line count and narrows the widest line.</summary>
    /// <remarks>
    ///     Greedy first-fit fills line one to 8 and leaves 2 on line two. Balanced, the same two lines
    ///     are 5 and 5 — the narrowest width that still wraps to two lines, which is what CSS Text 4's
    ///     <c>balance</c> asks for. ⚠ <b>Both halves are asserted because either alone is passed by
    ///     something wrong</b>: a wrapper that just narrowed the box would balance a two-line heading
    ///     into three even lines, and one that only kept the count is what the default already does.
    /// </remarks>
    [Fact]
    public void Balance_keeps_the_line_count_and_narrows_the_widest_line() {
        var greedy = Fill(Monospaced, 8f, TextWrapStyle.Auto);
        var balanced = Fill(Monospaced, 8f, TextWrapStyle.Balance);

        Assert.Equal(2, greedy.Count);
        Assert.Equal(8f, Widest(greedy));

        Assert.Equal(greedy.Count, balanced.Count);
        Assert.Equal(5f, Widest(balanced));
        Assert.Equal("aa bb ", Monospaced[balanced[0].Start..balanced[0].End]);
        Assert.Equal("cc dd", Monospaced[balanced[1].Start..balanced[1].End]);
    }

    /// <summary>Balancing a paragraph that already fits on one line does nothing at all.</summary>
    /// <remarks>
    ///     ⚠ The case a bisection gets wrong by trying: one line is as balanced as a paragraph gets,
    ///     and a search for "the narrowest width with at most one line" would find the width of the
    ///     longest word and break the heading into four.
    /// </remarks>
    [Fact]
    public void Balance_leaves_a_paragraph_that_fits_alone() {
        var balanced = Fill(Monospaced, 100f, TextWrapStyle.Balance);

        Assert.Equal(Fill(Monospaced, 100f, TextWrapStyle.Auto), balanced);
        Assert.Single(balanced);
    }

    /// <summary>⚠ <c>pretty</c> pulls a word down rather than leaving one alone on the last line.</summary>
    /// <remarks>
    ///     CSS Text 4 leaves <c>pretty</c> to the user agent and names one clause outright: no last
    ///     line with a single word on it. That clause needs the previous break and nothing else, so
    ///     the lines above the last two are untouched — which is what separates this from
    ///     <see cref="TextWrapStyle.Balance" /> and why it costs two measurements rather than ten
    ///     wraps.
    /// </remarks>
    [Fact]
    public void Pretty_refuses_a_last_line_with_one_word_on_it() {
        var greedy = Fill(Monospaced, 8f, TextWrapStyle.Auto);
        var pretty = Fill(Monospaced, 8f, TextWrapStyle.Pretty);

        Assert.Equal("dd", Monospaced[greedy[^1].Start..greedy[^1].End]);
        Assert.Equal("cc dd", Monospaced[pretty[^1].Start..pretty[^1].End]);
        Assert.Equal(greedy.Count, pretty.Count);
    }

    /// <summary>⚠ …and refuses the cure where the cure would overflow the box.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The half a naïve orphan fix ships without.</b> Moving a word down lengthens the last
    ///         line: here the greedy wrap is "aa bb" then "cccc", the cure would be "aa" then
    ///         "bb cccc" — 7 wide in a room of 5 — so taking it would trade an orphan for a line
    ///         hanging out of its box. The greedy answer stands instead, orphan and all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This fixture was written first against <see cref="Monospaced" /> in a room of 4,
    ///         where it passed and proved nothing.</b> That paragraph wraps to four lines of one word
    ///         each, so the line above the orphan has no earlier break to end at and the refusal
    ///         under test was never reached — widening the overflow test to a hundred times the room
    ///         left it green. The case below has both: a cut is available <i>and</i> taking it
    ///         overflows. The no-cut refusal is worth its own test and has one.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Pretty_leaves_an_orphan_alone_rather_than_overflow_the_line() {
        const string text = "aa bb cccc";

        var greedy = Fill(text, 5f, TextWrapStyle.Auto);
        var pretty = Fill(text, 5f, TextWrapStyle.Pretty);

        Assert.Equal(2, greedy.Count);
        Assert.Equal("cccc", text[greedy[^1].Start..greedy[^1].End]);
        Assert.Equal(greedy, pretty);
    }

    /// <summary>…and where the line above the orphan has no earlier break of its own.</summary>
    /// <remarks>
    ///     A line cannot be emptied to feed the one below it. In a room of 4 the stub paragraph is
    ///     four lines of one word each, so the penultimate line is "cc" and there is nothing inside it
    ///     to end at — the orphan stands. ⚠ The two refusals are separate tests because they are
    ///     separate branches, and the overflow one was reached by neither until this one took its
    ///     fixture off it.
    /// </remarks>
    [Fact]
    public void Pretty_leaves_an_orphan_alone_when_the_line_above_it_is_one_word_too() {
        var greedy = Fill(Monospaced, 4f, TextWrapStyle.Auto);
        var pretty = Fill(Monospaced, 4f, TextWrapStyle.Pretty);

        Assert.Equal(4, greedy.Count);
        Assert.Equal("dd", Monospaced[greedy[^1].Start..greedy[^1].End]);
        Assert.Equal(greedy, pretty);
    }

    /// <summary>Balancing real prose in a real font keeps every invariant the default keeps.</summary>
    /// <remarks>
    ///     The stub above makes the choice computable; this makes sure the choice survives contact
    ///     with measured widths. The two properties are the ones this whole file is built on — the
    ///     lines partition the text, and none is wider than it was allowed to be — plus the one
    ///     balancing adds, which is that it never costs a line.
    /// </remarks>
    [Fact]
    public void Balancing_real_prose_partitions_it_and_costs_no_line() {
        var shaped = Shape(Prose);
        var width = shaped.Advance / 3;

        var greedy = new List<WrappedLine>();
        var balanced = new List<WrappedLine>();

        LineWrapper.Wrap(shaped, width, greedy);
        LineWrapper.Wrap(shaped, width, balanced, style: TextWrapStyle.Balance);

        Assert.Equal(greedy.Count, balanced.Count);
        Assert.True(Widest(balanced) <= Widest(greedy), $"{Widest(balanced)} is wider than {Widest(greedy)}");

        var at = 0;

        foreach (var line in balanced) {
            Assert.Equal(at, line.Start);
            at = line.End;
        }

        Assert.Equal(Prose.Length, at);
    }

    /// <summary>Wraps the stub paragraph, one advance per character.</summary>
    static List<WrappedLine> Fill(string text, float room, TextWrapStyle style) {
        var advances = new float[text.Length];
        Array.Fill(advances, 1f);

        var lines = new List<WrappedLine>();
        LineWrapper.Wrap(text, advances, room, lines, style: style);

        return lines;
    }

    static float Widest(List<WrappedLine> lines) => lines.Max(line => line.Advance);

    static ShapedText Shape(string text) => TextShaper.Shape(TestFonts.Load(TestFonts.ContextualLatin), text);
}
