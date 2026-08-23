// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Text.Tests;

/// <summary>The wrapper's half of <c>text-indent</c>: the first line is narrower than the rest.</summary>
/// <remarks>
///     ⚠ <b>An indent is two facts and only one of them is a shift.</b> The shift is what the draw
///     list does with <c>TextLine.Offset</c>; this file is the other one, and it is the half a caller
///     could not add afterwards. A finished paragraph moved right by the indent is still wrapped to
///     the full width, so its first line runs past the box's edge by exactly the indent — which looks
///     like the feature working until the line is nearly full.
/// </remarks>
public class TextIndentTests {
    /// <summary>Wraps a paragraph whose every character is one unit wide.</summary>
    static List<string> Wrap(string text, float width, float indent) {
        var advances = new float[text.Length + 1];
        Array.Fill(advances, 1f, 0, text.Length);

        var lines = new List<WrappedLine>();
        LineWrapper.Wrap(text, advances, width, lines, TextWrapMode.Word, WordBreakMode.Normal, indent);

        return lines.Select(line => text[line.Start..line.End]).ToList();
    }

    [Fact]
    public void Without_an_indent_the_first_line_takes_the_whole_width() =>
        Assert.Equal(["aaa ", "bbb ", "ccc"], Wrap("aaa bbb ccc", 4f, 0f));

    /// <summary>
    ///     ⚠ <b>The first line is narrower and the second one is not</b>, which is the assertion that
    ///     fails for an implementation that subtracted the indent from every line.
    /// </summary>
    [Fact]
    public void The_indent_narrows_the_first_line_and_no_other() =>
        Assert.Equal(["aaa ", "bbbb ", "cccc"], Wrap("aaa bbbb cccc", 5f, 2f));

    /// <summary>A hanging indent widens it instead, and needs nothing but the sign.</summary>
    [Fact]
    public void A_negative_indent_makes_the_first_line_the_wide_one() =>
        Assert.Equal(["aa bb ", "cc dd"], Wrap("aa bb cc dd", 5f, -1f));

    /// <summary>
    ///     ⚠ <b>The indent belongs to the first line of the <i>block</i> and not to the first line
    ///     after every break.</b> CSS Text 3 § 8.1 puts it on "the first formatted line", and a
    ///     newline does not start a new block — so the paragraph after a hard break wraps to the full
    ///     width, exactly as the second visual line does.
    /// </summary>
    [Fact]
    public void A_hard_break_does_not_start_a_second_indent() =>
        Assert.Equal(["aaa\n", "bbbbb"], Wrap("aaa\nbbbbb", 5f, 3f));

    /// <summary>An indent as wide as the box leaves the first line one word, not none.</summary>
    /// <remarks>
    ///     The degenerate case, which is worth pinning because the arithmetic invites a zero-length
    ///     line: with no room at all the wrapper still has to put something on the line rather than
    ///     emitting an empty one and looping.
    /// </remarks>
    [Fact]
    public void An_indent_wider_than_the_box_still_places_the_first_word() {
        var lines = Wrap("aa bb cc", 4f, 6f);

        Assert.Equal("aa bb cc", string.Concat(lines));
        Assert.All(lines, line => Assert.NotEqual(string.Empty, line));
    }

    /// <summary>The lines still partition the text at every width and either sign of indent.</summary>
    [Fact]
    public void The_lines_partition_the_text() {
        const string Text = "aa bbbb c dd eeeee f";

        for (var indent = -6f; indent <= 6f; indent += 1f) {
            for (var width = 0f; width <= 14f; width += 1f) {
                var pieces = Wrap(Text, width, indent);

                Assert.Equal(Text, string.Concat(pieces));
                Assert.All(pieces, piece => Assert.NotEqual(string.Empty, piece));
            }
        }
    }
}
