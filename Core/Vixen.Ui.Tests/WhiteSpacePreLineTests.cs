// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary><c>white-space: pre-line</c> collapses, which is the one thing nothing in this engine did.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every pair below is a control and a declaration at the same width, for
///         <c>WhiteSpaceBreakSpacesTests</c>' reason.</b> An assertion that only said "<c>pre-line</c>
///         gives this width" would be satisfied by a box wide enough, by a wrapper that had stopped
///         working, or by a string with nothing to collapse. The control is <c>normal</c> — which in
///         this engine preserves everything, the premise <c>WhiteSpacePreTests</c> measures rather
///         than assumes — so a run that is narrower under the declaration than under the control is
///         the collapse and nothing else.
///     </para>
///     <para>
///         ⚠ <b>The control being <c>normal</c> is itself the finding worth carrying, and it will
///         date.</b> CSS collapses under <c>normal</c> too, and turns its segment breaks into
///         spaces; this engine does neither, so an undeclared paragraph here is CSS's
///         <c>pre-wrap</c>. The day <c>normal</c> learns to collapse, every control below reports the
///         same number as its declaration and this file goes red rather than quietly measuring
///         nothing — which is the property a pair has and a single-sided assertion does not.
///     </para>
///     <para>
///         ⚠ <b>What is not here.</b> § 4.1.3's phase II — a collapsible space at the <i>start</i> of
///         a line is removed — is not implemented, so <c>  ab</c> still draws its leading space and a
///         browser eats it. That is a question about a line rather than about a string, it is owed
///         for every value in this engine rather than for this one, and
///         <see cref="A_leading_space_is_still_drawn_which_is_phase_two_and_is_owed" /> pins the
///         current answer so a change towards Chrome comes through it rather than past it.
///         <c>Rikarin/Vixen#249</c>.
///     </para>
/// </remarks>
public class WhiteSpacePreLineTests {
    const float Tolerance = 0.05f;

    const string PreLine = "white-space: pre-line;";

    const string Normal = "white-space: normal;";

    static readonly FontFace Font = LoadFont();

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Tests.Fonts.OpenSans-Regular.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "OpenSans");
    }

    /// <summary>A box wide enough that nothing wraps, so every width below is the collapse.</summary>
    /// <param name="text">What the author wrote.</param>
    /// <param name="label">The declaration under test.</param>
    /// <returns>The laid-out paragraph.</returns>
    static TextLayout Block(string text, string label) {
        var document = new UiDocument(900f, 300f);
        document.Fonts.Register("Test", Font);

        document.Load(
            $$"""
              root { width: 800px; height: 300px; align-items: flex-start; }
              label { font-family: Test; font-size: 16px; {{label}} }
              """
        );

        var element = document.Root.Add("label");
        element.Text = text;
        document.Update();

        var block = element.Block();
        Assert.NotNull(block);

        return block;
    }

    /// <summary>The first line's width under a declaration.</summary>
    /// <param name="text">What the author wrote.</param>
    /// <param name="label">The declaration.</param>
    /// <returns>The width in points.</returns>
    static float Width(string text, string label) => Block(text, label).Lines[0].Width;

    /// <summary>A run of spaces is one space wide, where the control counts all of them.</summary>
    [Fact]
    public void A_run_of_spaces_measures_as_one_space() {
        var one = Width("a b", PreLine);
        var control = Width("a    b", Normal);

        Assert.True(control > one, "the control has to count the extra spaces or this measures nothing");
        Assert.Equal(one, Width("a    b", PreLine), Tolerance);
    }

    /// <summary>A tab is collapsible white space here, so the tab stops stop applying.</summary>
    /// <remarks>
    ///     ⚠ <b>The half of § 4.1.1 that looks like a detail and is a whole subsystem switching
    ///     off.</b> A tab's advance in this engine is the distance to the next stop —
    ///     <c>TextRun.IsTab</c>, <c>tab-size</c>, <c>UiElement.TabStop</c> — so a tab under any other
    ///     value measures a column rather than a character. Under this one there is no tab left in
    ///     the shaped text to find a stop for, and the control at a deliberately wide
    ///     <c>tab-size</c> is what says so: the two answers have to differ.
    /// </remarks>
    [Fact]
    public void A_tab_collapses_to_a_space_and_stops_reaching_the_tab_stops() {
        var control = Width("a\tb", Normal + " tab-size: 12;");

        Assert.Equal(Width("a b", PreLine), Width("a\tb", PreLine + " tab-size: 12;"), Tolerance);
        Assert.True(control > Width("a\tb", PreLine + " tab-size: 12;"), "the stop has to be wider than a space");
    }

    /// <summary>A segment break still ends a line, which is the half the value preserves.</summary>
    /// <remarks>
    ///     ⚠ <b>The half that makes the collapse safe.</b> An implementation that reached the widths
    ///     above by treating every run of white space as collapsible — segment breaks included, which
    ///     is what <c>char.IsWhiteSpace</c> answers — would pass every other assertion in this file
    ///     and would put a whole poem on one line. That is CSS's <c>collapse</c>, a different value,
    ///     and it is the one mistake this value cannot survive.
    /// </remarks>
    [Fact]
    public void A_newline_still_ends_a_line() {
        Assert.Single(Block("a b", PreLine).Lines);
        Assert.Equal(2, Block("a\nb", PreLine).Lines.Length);
        Assert.Equal(3, Block("a\n\nb", PreLine).Lines.Length);
    }

    /// <summary>The spaces around a segment break are gone rather than collapsed to one.</summary>
    /// <remarks>
    ///     ⚠ <b>Both sides, because they are two clauses and an implementation usually writes one.</b>
    ///     Looking only forward leaves the indentation of every line after the first, which is the
    ///     shape a <c>pre-line</c> paragraph is written in — a string in source that is indented to
    ///     match the code around it. So the first line of the pair is measured as well as the second.
    /// </remarks>
    [Fact]
    public void A_run_touching_a_segment_break_is_removed_on_both_sides() {
        var plain = Block("a\nb", PreLine);
        var padded = Block("a  \n  b", PreLine);
        var control = Block("a  \n  b", Normal);

        Assert.Equal(2, padded.Lines.Length);
        Assert.Equal(plain.Lines[0].Width, padded.Lines[0].Width, Tolerance);
        Assert.Equal(plain.Lines[1].Width, padded.Lines[1].Width, Tolerance);

        Assert.True(
            control.Lines[1].Width > padded.Lines[1].Width,
            "the control has to draw the indentation of the second line or this measures nothing"
        );
    }

    /// <summary>A no-break space is not collapsible and is still drawn.</summary>
    /// <remarks>
    ///     The predicate's own trap, end to end: <c>char.IsWhiteSpace</c> is true of U+00A0, and a
    ///     collapse written on it would fold the one space CSS says never folds — which is the
    ///     character an author reaches for precisely because it must not be touched.
    /// </remarks>
    [Fact]
    public void A_no_break_space_is_not_collapsed() {
        var one = Width("a\u00a0b", PreLine);
        var two = Width("a\u00a0\u00a0b", PreLine);

        Assert.True(two > one, "the second no-break space has to widen the line");
    }

    /// <summary>A collapsible run is still drawn at the start of a line, which is phase II and is owed.</summary>
    /// <remarks>
    ///     ⚠ <b>Named so that a change towards Chrome comes through this test rather than past it</b>,
    ///     which is the arrangement <c>WhiteSpaceBreakSpacesTests</c>' <c>NotEqual</c> row uses for
    ///     the other half-landed rule. Chrome removes a collapsible space at the start of a line;
    ///     this engine keeps it, because § 4.1.3 is a question about a line and the transformation
    ///     happens before any line exists. The run does collapse to one space first, which is the
    ///     part that landed.
    /// </remarks>
    [Fact]
    public void A_leading_space_is_still_drawn_which_is_phase_two_and_is_owed() {
        var bare = Width("ab", PreLine);
        var padded = Width("   ab", PreLine);

        Assert.True(padded > bare, "phase II is not implemented, so the leading space is still drawn");
        Assert.Equal(Width(" ab", PreLine), padded, Tolerance);
    }
}
