// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Text.Tests;

/// <summary>
///     <see cref="LineWrapper.Wrap(ShapedText,float,System.Collections.Generic.List{WrappedLine},TextWrapMode,WordBreakMode,float,float,HyphenMode,TextWrapStyle,LineBreakStrictness,string,bool)" />'s
///     <c>breakSpaces</c>: CSS Text § 3.1's two rules, each measured against the <c>pre-wrap</c>
///     answer the same paragraph gives without the flag.
/// </summary>
/// <remarks>
///     <para>
///         Every test here is a pair, because a test that only asserted the <c>break-spaces</c>
///         answer would pass on a wrapper that ignored the flag whenever the two answers happened
///         to agree. The control half pins what the flag changes <i>from</i>, at the same width.
///     </para>
///     <para>
///         ⚠ <b>The widths are chosen from measurements of the same range, not written down.</b>
///         The one width at which the hang decides an answer is between the trimmed and untrimmed
///         measure of one range, so each test computes both and picks the midpoint; a number typed
///         in would be a number true of one face.
///     </para>
/// </remarks>
public class BreakSpacesWrapTests {
    static ShapedText Shape(string text) => TextShaper.Shape(TestFonts.Load(TestFonts.ContextualLatin), text);

    static List<WrappedLine> Lines(string text, float width, bool breakSpaces) {
        var lines = new List<WrappedLine>();
        LineWrapper.Wrap(Shape(text), width, lines, breakSpaces: breakSpaces);

        return lines;
    }

    /// <summary>Rule one: the spaces at a line's end take up room, so they can push the break earlier.</summary>
    /// <remarks>
    ///     <c>ab cd  ef</c> breaks at 3, 7 and 9 under UAX #14. At a width between the trimmed
    ///     measure of <c>[0,7)</c> and its untrimmed one, <c>pre-wrap</c> hangs the two spaces and
    ///     keeps all seven characters on the first line; <c>break-spaces</c> counts them and cannot.
    /// </remarks>
    [Fact]
    public void Trailing_spaces_take_up_room_instead_of_hanging() {
        const string text = "ab cd  ef";

        var trimmed = Shape("ab cd").Advance;
        var untrimmed = Shape("ab cd  ").Advance;
        Assert.True(untrimmed > trimmed, "the spaces had no width");

        var width = (trimmed + untrimmed) / 2f;

        var hung = Lines(text, width, breakSpaces: false);
        Assert.Equal(7, hung[0].Length);

        var kept = Lines(text, width, breakSpaces: true);
        Assert.True(kept[0].Length < 7, $"the first line still took {kept[0].Length} characters");
    }

    /// <summary>Rule one, the reported width: a broken line's advance includes its spaces.</summary>
    [Fact]
    public void A_broken_lines_advance_counts_its_spaces() {
        const string text = "ab cd";

        var withSpace = Shape("ab ").Advance;
        var without = Shape("ab").Advance;
        var width = (withSpace + Shape("ab cd").Advance) / 2f;

        var hung = Lines(text, width, breakSpaces: false);
        Assert.Equal(2, hung.Count);
        Assert.Equal(without, hung[0].Advance, 0.01f);

        var kept = Lines(text, width, breakSpaces: true);
        Assert.Equal(2, kept.Count);
        Assert.Equal(withSpace, kept[0].Advance, 0.01f);
    }

    /// <summary>Rule two: a line may end between two spaces.</summary>
    /// <remarks>
    ///     A width that holds <c>a</c> and one space but not two: <c>pre-wrap</c> has no
    ///     opportunity inside the run, so the whole run hangs off the first line and <c>b</c> starts
    ///     the second; <c>break-spaces</c> ends the first line after the first space and the second
    ///     line begins with the other.
    /// </remarks>
    [Fact]
    public void A_break_between_two_spaces_exists() {
        const string text = "a  b";

        var one = Shape("a ").Advance;
        var two = Shape("a  ").Advance;
        var width = (one + two) / 2f;

        var hung = Lines(text, width, breakSpaces: false);
        Assert.Equal(2, hung.Count);
        Assert.Equal(3, hung[0].Length);
        Assert.Equal("b", text.Substring(hung[1].Start, hung[1].Length));

        var kept = Lines(text, width, breakSpaces: true);
        Assert.Equal(2, kept.Count);
        Assert.Equal(2, kept[0].Length);
        Assert.Equal(" b", text.Substring(kept[1].Start, kept[1].Length));
    }

    /// <summary>A forced line still leaves its newline out of the width under either rule.</summary>
    /// <remarks>
    ///     The one trailing character <c>break-spaces</c> does not count: a segment break occupies
    ///     nothing in any browser, and what the face shaped U+000A to is not a width.
    /// </remarks>
    [Fact]
    public void A_newline_is_still_not_a_width() {
        var lines = Lines("ab \ncd", 10_000f, breakSpaces: true);

        Assert.Equal(2, lines.Count);
        Assert.True(lines[0].Mandatory);
        Assert.Equal(Shape("ab ").Advance, lines[0].Advance, 0.01f);
    }

    /// <summary>The paragraph's last line keeps its spaces too, and the flag changes nothing there.</summary>
    [Fact]
    public void The_last_lines_advance_is_unchanged_by_the_flag_when_nothing_trails() {
        var kept = Assert.Single(Lines("ab cd", 10_000f, breakSpaces: true));
        var hung = Assert.Single(Lines("ab cd", 10_000f, breakSpaces: false));

        Assert.Equal(hung.Advance, kept.Advance, 0.01f);
    }
}
