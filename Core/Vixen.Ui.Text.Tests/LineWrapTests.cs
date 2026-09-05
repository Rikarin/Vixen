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

    static ShapedText Shape(string text) => TextShaper.Shape(TestFonts.Load(TestFonts.ContextualLatin), text);
}
