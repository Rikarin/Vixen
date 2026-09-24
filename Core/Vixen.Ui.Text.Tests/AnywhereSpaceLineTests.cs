// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Text.Tests;

/// <summary>A break that falls before a space does not start a line with it (#249).</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two breaks can fall before a space, and both are breaks taken when nothing fits</b>:
///         one grapheme under <c>overflow-wrap: anywhere</c>, and the first opportunity under
///         <c>line-break: anywhere</c>. In a room narrower than a letter each fell between <c>b</c> and
///         the space of <c>ab cd</c>, and the wrapper answered five lines — <c>a</c>, <c>b</c>, a line
///         of nothing but the space, <c>c</c>, <c>d</c>. Chrome 153 draws four
///         (<c>Vixen.Ui.Tests/Oracle/narrow-anywhere.html</c>, all six combinations with
///         <c>normal</c>, <c>pre-line</c> and <c>pre-wrap</c>), and a min-content probe — a room of
///         zero — is exactly this case.
///     </para>
///     <para>
///         The control is <c>overflow-wrap: break-word</c> at zero, which CSS Sizing § 5.3 keeps
///         from breaking inside a word for min-content at all, so it never reached the defect; and a
///         room a letter wide, where the break after the space is the one that fits.
///     </para>
/// </remarks>
public class AnywhereSpaceLineTests {
    const string Text = "ab cd";

    static readonly float[] Advances = [10f, 10f, 4f, 10f, 10f];

    static List<WrappedLine> Wrap(TextWrapMode mode, LineBreakStrictness strictness, float width, bool breakSpaces = false) {
        var lines = new List<WrappedLine>();
        LineWrapper.Wrap(Text, Advances, width, lines, mode, strictness: strictness, breakSpaces: breakSpaces);
        return lines;
    }

    static string Describe(List<WrappedLine> lines) =>
        string.Join(" | ", lines.Select(line => $"'{Text.Substring(line.Start, line.Length)}'"));

    [Theory]
    [InlineData(TextWrapMode.Anywhere, LineBreakStrictness.Auto, 0f)]
    [InlineData(TextWrapMode.Anywhere, LineBreakStrictness.Auto, 5f)]
    [InlineData(TextWrapMode.Word, LineBreakStrictness.Anywhere, 0f)]
    [InlineData(TextWrapMode.Word, LineBreakStrictness.Anywhere, 5f)]
    [InlineData(TextWrapMode.Anywhere, LineBreakStrictness.Auto, 12f)]
    public void A_room_narrower_than_a_letter_puts_one_letter_on_a_line_and_the_space_on_none_of_its_own(
        TextWrapMode mode,
        LineBreakStrictness strictness,
        float width
    ) {
        var lines = Wrap(mode, strictness, width);

        Assert.True(lines.Count == 4, $"{lines.Count} lines: {Describe(lines)}");
        Assert.All(lines, line => Assert.NotEqual(' ', Text[line.Start]));

        // Every character on exactly one line, in order — the space on the end of `b`'s.
        Assert.Equal(0, lines[0].Start);
        Assert.All(lines.Zip(lines.Skip(1)), pair => Assert.Equal(pair.First.End, pair.Second.Start));
        Assert.Equal(Text.Length, lines[^1].End);
        Assert.Equal("b ", Text.Substring(lines[1].Start, lines[1].Length));

        // And the space hangs: it is on the line and not in its measure.
        Assert.Equal(10f, lines[1].Advance);
    }

    [Fact]
    public void Break_word_does_not_break_inside_a_word_for_min_content_and_so_never_met_the_space() {
        var lines = Wrap(TextWrapMode.BreakWord, LineBreakStrictness.Auto, 0f);

        Assert.Equal("'ab ' | 'cd'", Describe(lines));
    }
}
