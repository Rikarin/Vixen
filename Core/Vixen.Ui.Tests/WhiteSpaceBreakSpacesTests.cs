// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>
///     <c>white-space: break-spaces</c>: the two rules that separate it from <c>pre-wrap</c>,
///     each measured against the answer the same paragraph gives without the declaration.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The recorded reason this keyword was owed, and why it could not be true of it.</b>
///         <c>InlineKnownGaps.txt</c>, doc 43's B3 row, the parity ledger's <c>whitespace</c> note
///         and several of #249's comments said <c>pre-line</c> and <c>break-spaces</c> were owed
///         together because both need CSS Text § 4's collapsing. That is right about
///         <c>pre-line</c>, whose longhand is <c>white-space-collapse: preserve-breaks</c>. It
///         cannot be right about <c>break-spaces</c>, because that value <b>preserves</b> exactly
///         as <c>preserve</c> does — and <c>WhiteSpacePreTests</c> measures that an undeclared
///         element here already behaves as <c>pre-wrap</c>. CSS Text § 3.1 separates the two by two
///         rules, neither of which is about collapsing, and this file held both as assertions about
///         the engine until the keyword landed. It holds them as pairs now.
///     </para>
///     <para>
///         <b>The two rules.</b> (1) A sequence of preserved white space at the end of a line takes
///         up space rather than hanging — <c>LineWrapper.Width</c>'s trailing trim, behind the
///         flag. (2) There is a soft wrap opportunity after every preserved white space character,
///         <i>including between two of them</i>, which UAX #14's LB7 and LB18 never offer —
///         <c>LineWrapper.AddSpaceOpportunities</c>, over the Consortium's answer rather than inside
///         <c>LineBreaker</c>, which the conformance suite keeps judging as written. And § 3.1's
///         third sentence, that the spaces "affect the box's intrinsic sizes", is
///         <c>TextLine.Trimmed</c> reporting the untrimmed width under the value.
///     </para>
///     <para>
///         ⚠ <b>The first version of the hang test was green under sabotage and it is worth saying
///         what it got wrong.</b> It put <c>ab</c> and two spaces in a box too narrow for them and
///         asserted one line — which is true whatever the trim does, because a trailing run of
///         spaces has no break opportunity <i>inside</i> it and the only one is the end of the text.
///         That test was measuring rule two and reporting it as rule one. The pair below breaks at a
///         width chosen between the trimmed and untrimmed measure of the same range, which is the
///         only place the two answers differ.
///     </para>
///     <para>
///         ⚠ <b>The control halves are the <c>pre-wrap</c> answers this engine gave before the
///         keyword, and they are what says the value is doing the deciding</b> rather than the
///         paragraph being short enough for anything. Each pair runs the same text at the same
///         width, and only the declaration differs. #1211 and #1237 settled what the control
///         halves are: preserved trailing white space hangs at a soft wrap and nowhere else, so
///         under <c>pre-wrap</c> a wrapped line's reported width is trimmed and the last line's is
///         not, while the intrinsic measure never counts it.
///     </para>
///     <para>
///         ⚠ <b>Every pair below was read against Chrome 152.0.7977.76 with the same Open Sans face
///         at 16px, served over localhost and read through <c>Range.getClientRects</c>.</b> Chrome
///         shapes <c>ab cd</c> to 40.297 and <c>ab cd  </c> to 48.609, which is this engine's
///         40.29 and 48.60. At 44px, <c>pre-wrap</c> gives <c>[0,7)</c> and <c>[7,9)</c>;
///         <c>break-spaces</c> gives <c>ab </c> <c>[0,3)</c> and <c>cd  ef</c> <c>[3,9)</c>. At the
///         midpoint of <c>a </c> and <c>a  </c> — 15.125 — <c>pre-wrap</c> gives <c>a  </c> and
///         <c>b</c>; <c>break-spaces</c> gives <c>a </c> and <c> b</c>. <c>ab</c>, a space and a newline under
///         <c>break-spaces</c> reports its first line at 22.844, the width of <c>ab </c>. Every
///         one of those is what the assertions say.
///     </para>
///     <para>
///         ⚠ <b>The intrinsic table, which took three readings to get right and refuted a claim of
///         its own on the way.</b> For <c>ab  </c> in this face, Chrome: under <c>pre-wrap</c>
///         every shrink-to-fit form — <c>inline-block</c>, <c>float</c>, <c>position: absolute</c>,
///         <c>width: fit-content</c>, <c>width: min-content</c> — is 18.688, the glyphs alone;
///         under <c>break-spaces</c> all of them are 22.844, which is <c>ab </c>, because the value
///         puts a break opportunity after the first space and the second one hangs off the line
///         that ends there; under <c>pre</c> all of them are 27.000. ⚠ <b>Only the explicit
///         <c>width: max-content</c> keyword stands apart</b>, reporting 27.000 under all three —
///         which is the reading a first pass here took for shrink-to-fit, filed as #1318, and
///         refuted by re-measuring after a reload. #1211's fix is Chrome's answer and stands.
///     </para>
///     <para>
///         ⚠ <b>What is left of it is smaller and real, and the last test below pins it.</b> Under
///         <c>break-spaces</c> this engine's <c>TextLayout.Width</c> counts both spaces — 27.000 —
///         where Chrome's shrink-to-fit counts one. The line box is right either way; what differs
///         is that Chrome's intrinsic measure re-asks where the paragraph could break, and
///         <c>TextLine.Trimmed</c> under this value does not ask at all.
///     </para>
///     <para>
///         ⚠ <b>What this file does not claim.</b> Nothing about <c>pre-line</c>, which is owed for
///         exactly the recorded reason and stays owed. See <c>Rikarin/Vixen#249</c>.
///     </para>
/// </remarks>
public class WhiteSpaceBreakSpacesTests {
    /// <summary>The paragraph the hang pair wraps.</summary>
    /// <remarks>
    ///     Its three UAX #14 break opportunities are at 3, 7 and 9, and the range <c>[0,7)</c> is
    ///     what the two answers disagree about: 40.29 points trimmed, 48.60 with its two spaces.
    /// </remarks>
    const string Spaced = "ab cd  ef";

    const string BreakSpaces = "white-space: break-spaces;";

    static readonly FontFace Font = LoadFont();

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Tests.Fonts.OpenSans-Regular.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "OpenSans");
    }

    /// <summary>Lays a paragraph out in a box of the given width.</summary>
    /// <param name="text">The paragraph.</param>
    /// <param name="width">How wide, in points.</param>
    /// <param name="label">Extra declarations on the label.</param>
    static TextLayout Box(string text, float width, string label = "") {
        var document = new UiDocument(900f, 300f);
        document.Fonts.Register("Test", Font);

        document.Load(
            $$"""
              root { width: {{width}}px; height: 300px; align-items: flex-start; }
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

    /// <summary>The width of a string that fits on one line, as the last line reports it.</summary>
    /// <remarks>
    ///     A last line's <c>Width</c> keeps its trailing spaces under either value — #1237 — so this
    ///     is the untrimmed measure whatever the declaration.
    /// </remarks>
    static float Measure(string text) => Assert.Single(Box(text, 800f).Lines).Width;

    /// <summary>Rule one, the control: the two spaces hang, so a range too wide with them still fits.</summary>
    /// <remarks>
    ///     ⚠ <b>44 points is chosen between the two measures of one range and nowhere else would
    ///     do.</b> <c>ab cd  </c> is 40.29 without its trailing spaces and 48.60 with them, so a
    ///     44-point box is the one width at which the trim decides the answer: under <c>pre-wrap</c>
    ///     the line takes all seven characters. The 39-point half is the control's control — one
    ///     point under the trimmed measure, where the wrapper must break earlier.
    /// </remarks>
    [Fact]
    public void Under_pre_wrap_a_trailing_space_hangs() {
        Assert.True(Measure("ab cd") < 44f && 44f < Measure("ab cd  "), "44 is not between the two measures");

        var hung = Box(Spaced, 44f).Lines;

        Assert.Equal(2, hung.Length);
        Assert.Equal(0, hung[0].Start);
        Assert.Equal(7, hung[0].Length);

        Assert.Equal(3, Box(Spaced, 39f).Lines.Length);
    }

    /// <summary>Rule one: under <c>break-spaces</c> the spaces take up room, so the same box breaks earlier.</summary>
    /// <remarks>
    ///     The same 44 points. <c>ab cd </c> — six characters, one space — is already wider than the
    ///     box once its space counts, so the first line is <c>ab </c>: three characters, and its
    ///     reported width is the three of them rather than the two glyphs.
    /// </remarks>
    [Fact]
    public void Under_break_spaces_a_trailing_space_takes_up_room() {
        Assert.True(Measure("ab cd ") > 44f, "the six-character range fits with its space counted");

        var kept = Box(Spaced, 44f, BreakSpaces).Lines;

        // Chrome 152: `ab ` then `cd  ef`, the second line's 40.141 fitting the 44px box.
        Assert.Equal(2, kept.Length);
        Assert.Equal(0, kept[0].Start);
        Assert.Equal(3, kept[0].Length);
        Assert.Equal(Measure("ab "), kept[0].Width, 0.05f);
        Assert.Equal(3, kept[1].Start);
        Assert.Equal(6, kept[1].Length);
    }

    /// <summary>Rule two, the control: a run of spaces offers one break at its end and none inside.</summary>
    /// <remarks>
    ///     ⚠ <b>Read off <see cref="LineBreaker.Collect(System.ReadOnlySpan{char}, System.Collections.Generic.List{int})" /> rather than off a wrapped paragraph,
    ///     because the question is which opportunities UAX #14 offers, and the keyword's extra one
    ///     is deliberately added above it rather than inside it.</b> LB7 forbids a break before a
    ///     space and LB18 puts one after a run of them, so 3 is offered and 2 is not — and the
    ///     Consortium's 19 338-case suite goes on judging that answer as written.
    /// </remarks>
    [Fact]
    public void UAX14_does_not_offer_a_break_between_two_spaces() {
        var opportunities = new List<int>();

        LineBreaker.Collect("a  b", opportunities);

        // The control: the run's own opportunity is there, so an empty or truncated list cannot pass.
        Assert.Contains(3, opportunities);

        Assert.DoesNotContain(2, opportunities);
    }

    /// <summary>Rule two: under <c>break-spaces</c> a line may end between two spaces.</summary>
    /// <remarks>
    ///     A box that holds <c>a</c> and one space but not two. Under <c>pre-wrap</c> both spaces
    ///     hang off the first line and <c>b</c> starts the second at index 3; under
    ///     <c>break-spaces</c> the first line ends after the first space and the second begins
    ///     with the other, at index 2.
    /// </remarks>
    [Fact]
    public void Under_break_spaces_a_line_may_end_between_two_spaces() {
        var width = (Measure("a ") + Measure("a  ")) / 2f;

        var hung = Box("a  b", width).Lines;
        Assert.Equal(2, hung.Length);
        Assert.Equal(3, hung[1].Start);

        var kept = Box("a  b", width, BreakSpaces).Lines;
        Assert.Equal(2, kept.Length);
        Assert.Equal(2, kept[1].Start);
        Assert.Equal(2, kept[0].Length);
    }

    /// <summary>Rule three: the spaces reach the intrinsic measure.</summary>
    /// <remarks>
    ///     <para>
    ///         <c>TextLayout.Width</c> is what a shrink-to-fit box measures. The <c>pre-wrap</c>
    ///         half is Chrome's answer to the glyph: 18.688 for <c>ab  </c>, the spaces hung out of
    ///         the box, which is what #1211 landed. Under <c>break-spaces</c> they do not hang and
    ///         the measure grows.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The <c>break-spaces</c> half is this engine's answer and not Chrome's, by one
    ///         space.</b> Chrome makes it 22.844 — <c>ab </c> — because its intrinsic measure
    ///         re-asks where the paragraph may break and the value has put an opportunity after the
    ///         first space, so the second hangs off the line that ends there. This engine reports
    ///         27.000, both spaces, since <c>TextLine.Trimmed</c> under the value is the line's
    ///         whole width and asks nothing about breaking. Asserted as it is rather than as Chrome
    ///         has it, with the difference written down, because a min-content pass over a
    ///         <c>break-spaces</c> paragraph is a change to the measure and not to this keyword.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Under_break_spaces_trailing_spaces_reach_the_intrinsic_width() {
        var glyphs = Measure("ab");
        var all = Measure("ab  ");
        var one = Measure("ab ");
        Assert.True(all > one && one > glyphs, "the spaces had no width");

        Assert.Equal(glyphs, Box("ab  ", 800f).Width, 0.05f);
        Assert.Equal(all, Box("ab  ", 800f, BreakSpaces).Width, 0.05f);

        // Chrome's shrink-to-fit is `one` here; see the class remarks. Named so that a change
        // towards it has to come through this test rather than past it.
        Assert.NotEqual(one, Box("ab  ", 800f, BreakSpaces).Width, 0.05f);
    }

    /// <summary>A forced break still occupies nothing under the value.</summary>
    /// <remarks>
    ///     The one trailing character <c>break-spaces</c> does not count. A line ending at a
    ///     newline reports the width of what precedes it, spaces included, and never the advance
    ///     the face happened to shape U+000A to.
    /// </remarks>
    [Fact]
    public void Under_break_spaces_a_newline_is_still_not_a_width() {
        var lines = Box("ab \ncd", 800f, BreakSpaces).Lines;

        Assert.Equal(2, lines.Length);
        Assert.Equal(Measure("ab "), lines[0].Width, 0.05f);
    }

    /// <summary>A space before a forced break is not an opportunity, however little room is left.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The pair the first landing of this keyword did not write, and the one shape in
    ///         which rule two departed from Chrome.</b> The two newline tests above run in an
    ///         800-point box, where nothing overflows and an extra opportunity before the newline is
    ///         never the one a line ends on. In a box one glyph wide it is the only one reachable,
    ///         and the line that ended there made the newline a line of its own — three line boxes,
    ///         the middle one blank. UAX #14's LB6 forbids breaking before a hard break and § 3.1
    ///         adds its opportunity <i>after</i> a space, so neither rule ever offered that index.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Read against Chrome 152.0.7977.76 through the same fixture rather than reasoned
    ///         from the specification</b>, because the specification is what the first landing read
    ///         and it still produced three lines. In an 8.891-point block — <c>measure("a")</c> in
    ///         that browser — both <c>pre-wrap</c> and <c>break-spaces</c> give two rows,
    ///         <c>[0,3)</c> at 13.047 and <c>[3,4)</c> at 9.797: the space overflows the block and
    ///         the newline ends the line it is on. 13.047 is Chrome's <c>a </c>, which is why the
    ///         first line's width is asserted against this engine's measure of the same range.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Under_break_spaces_a_space_before_a_forced_break_is_not_an_opportunity() {
        const string text = "a \nb";

        // ⚠ `min-width: 0` and the test is green without it. A label in a flex root takes
        // `min-width: auto`, so the box is never narrower than the paragraph's min-content
        // contribution — and under this value that contribution counts the space, which is
        // precisely the overflow the defect needed. The declaration is CSS's own way of saying the
        // box may be narrower than its content, and it is what makes this pair reach the wrapper's
        // "nothing fits" path at all.
        const string narrow = "min-width: 0;";

        var width = Measure("a");

        var hung = Box(text, width, narrow).Lines;
        var kept = Box(text, width, narrow + BreakSpaces).Lines;

        Assert.Equal(2, hung.Length);
        Assert.Equal(hung.Length, kept.Length);
        Assert.DoesNotContain(kept, line => line.Length == 0);
        Assert.Equal(3, kept[0].Length);
        Assert.Equal(Measure("a "), kept[0].Width, 0.05f);
    }
}
