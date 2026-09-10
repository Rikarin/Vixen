// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>
///     What <c>white-space: break-spaces</c> is actually missing, measured — and it is not the
///     collapsing that four places record as the reason it is owed.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The recorded reason, and why it cannot be true of this keyword.</b>
///         <c>InlineKnownGaps.txt</c>, doc 43's B3 row, the parity ledger's <c>whitespace</c> note
///         and several of #249's comments say <c>pre-line</c> and <c>break-spaces</c> are owed
///         together because both need CSS Text § 4's collapsing, which nothing in <c>Vixen.Ui</c>
///         does. That is right about <c>pre-line</c>, whose longhand is
///         <c>white-space-collapse: preserve-breaks</c> — collapse the spaces, keep the newlines. It
///         cannot be right about <c>break-spaces</c>, because that value <b>preserves</b> exactly as
///         <c>preserve</c> does. Collapsing is the third of the property <c>break-spaces</c> already
///         agrees with this engine about, not the third it is waiting for. The sibling file settles
///         the premise: <c>WhiteSpacePreTests</c> measures that nothing collapses and that every
///         mandatory break is taken, so an undeclared element is already behaving as
///         <c>pre-wrap</c> — and CSS Text § 3.1 separates <c>break-spaces</c> from <c>pre-wrap</c> by
///         two rules, neither of which is about collapsing.
///     </para>
///     <para>
///         <b>Those two rules, and the two tests below are one each.</b> (1) A sequence of preserved
///         white space that would otherwise <i>hang</i> at the end of a line instead takes up space.
///         (2) There is a soft wrap opportunity after every preserved white space character,
///         <i>including between two of them</i>. This engine answers both the <c>pre-wrap</c> way
///         today, and each is a place rather than a subsystem: the hang is
///         <c>LineWrapper.Width</c>'s trailing-whitespace trim (<c>LineWrapper.cs:779</c>), and the
///         opportunities are UAX #14's LB7 and LB18 as <c>LineBreaker</c> implements them, which
///         offer a break after a run of spaces and never inside one.
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
///         ⚠ <b>The trim reaches the line's reported width only where the wrapper chose the break,
///         and that turned out to be right rather than a defect.</b> The wrapped line <c>[0,7)</c>
///         below reports 40.29 — trimmed — while a paragraph whose single line is <c>ab</c> and two
///         spaces reports 26.99 against <c>ab</c>'s 18.68, and <c>DrawListBuilder</c> takes its
///         alignment slack from that width. <a href="https://github.com/Rikarin/Vixen/issues/1211">#1211</a>
///         called the resulting shift the ragged edge <c>LineWrapper.Width</c>'s own remark says the
///         trim exists to prevent; measured in Chrome 152 it is what the browser draws. Preserved
///         trailing white space hangs at a <i>soft wrap</i> and nowhere else — not at the end of the
///         text, not before a forced break — so the untrimmed line box is the browser's answer and
///         the trimmed one is right for the wrapped line above it. What was wrong is the other
///         question: an intrinsic measure never counts hanging white space, and
///         <c>TextLayout.Width</c> was reading the same untrimmed number it aligns by.
///         <c>TrailingSpaceAlignmentTests</c> holds the four measurements and
///         <c>TextLine.Trimmed</c> is the separation. ⚠ Both halves still have to end up behind the
///         one value <c>break-spaces</c> switches, or the two fight.
///     </para>
///     <para>
///         ⚠ <b>And a third half, which is the wrapped paragraph's own last line.</b>
///         <a href="https://github.com/Rikarin/Vixen/issues/1237">#1237</a>: the trimmed measure was
///         reaching every line the wrapper produced and not only the one it broke, so the last line
///         of a paragraph hung its spaces where a browser keeps them. <c>UiElement.Wrap</c> now
///         passes it down for a soft wrap alone — which leaves the test below untouched, because
///         <c>[0,7)</c> is a soft wrap — and that switch is the third thing <c>break-spaces</c> has
///         to reach.
///     </para>
///     <para>
///         ⚠ <b>Both tests assert the engine as it stands and are meant to go red on the day the
///         keyword lands</b>, which is the shape <c>WhiteSpacePreTests</c>' first two take and for
///         the same reason: they are what says the gap is these two rules rather than a subsystem
///         nobody has written. Whatever implements <c>break-spaces</c> makes both conditional on the
///         value, at which point this file is re-read rather than re-run and each assertion becomes
///         the <c>pre-wrap</c> half of a pair.
///     </para>
///     <para>
///         ⚠ <b>What this file does not claim.</b> Nothing about <c>pre-line</c>, which is owed for
///         exactly the recorded reason and stays owed; and knowing what a keyword needs is not
///         implementing it — <c>UiDocument</c> interns two <c>white-space</c> values, <c>nowrap</c>
///         and <c>pre</c>, and reads no others. See <c>Rikarin/Vixen#249</c>.
///     </para>
/// </remarks>
public class WhiteSpaceBreakSpacesTests {
    /// <summary>The paragraph both halves of the first test wrap, at two different widths.</summary>
    /// <remarks>
    ///     Its three break opportunities are at 3, 7 and 9, and the range <c>[0,7)</c> is what the
    ///     two answers disagree about: 40.29 points trimmed, 48.60 with its two spaces counted.
    /// </remarks>
    const string Spaced = "ab cd  ef";

    static readonly FontFace Font = LoadFont();

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Tests.Fonts.OpenSans-Regular.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "OpenSans");
    }

    /// <summary>Wraps <see cref="Spaced" /> in a box of the given width.</summary>
    /// <param name="width">How wide, in points.</param>
    /// <returns>The laid-out paragraph.</returns>
    static TextLayout Box(float width) {
        var document = new UiDocument(900f, 300f);
        document.Fonts.Register("Test", Font);

        document.Load(
            $$"""
              root { width: {{width}}px; height: 300px; align-items: flex-start; }
              label { font-family: Test; font-size: 16px; }
              """
        );

        var element = document.Root.Add("label");
        element.Text = Spaced;
        document.Update();

        var block = element.Block();
        Assert.NotNull(block);

        return block;
    }

    /// <summary>Rule one, inverted: the two spaces hang, so a range too wide with them still fits.</summary>
    /// <remarks>
    ///     ⚠ <b>44 points is chosen between the two measures of one range and nowhere else would
    ///     do.</b> <c>ab cd  </c> is 40.29 without its trailing spaces and 48.60 with them, so a
    ///     44-point box is the one width at which the trim decides the answer: the line takes all
    ///     seven characters. The 39-point half is the control — one point under the trimmed measure,
    ///     where the wrapper must break earlier — and it is what says the box is doing the deciding
    ///     rather than the paragraph being short enough for anything.
    /// </remarks>
    [Fact]
    public void A_trailing_space_hangs_and_that_is_the_first_thing_break_spaces_turns_off() {
        var hung = Box(44f).Lines;

        Assert.Equal(2, hung.Length);
        Assert.Equal(0, hung[0].Start);
        Assert.Equal(7, hung[0].Length);

        Assert.Equal(3, Box(39f).Lines.Length);
    }

    /// <summary>Rule two, inverted: a run of spaces offers one break at its end and none inside.</summary>
    /// <remarks>
    ///     ⚠ <b>Read off <see cref="LineBreaker.Collect(System.ReadOnlySpan{char}, System.Collections.Generic.List{int})" /> rather than off a wrapped paragraph,
    ///     because the question is which opportunities EXIST and not which one a width chose.</b> A
    ///     box narrow enough to break <c>a</c>, two spaces, <c>b</c> takes the same break under both
    ///     answers, so a paragraph cannot tell them apart; only the list can. UAX #14's LB7 forbids
    ///     a break before a space and LB18 puts one after a run of them, so 3 is offered and 2 is
    ///     not — and 2 is precisely what <c>break-spaces</c> adds.
    /// </remarks>
    [Fact]
    public void A_break_between_two_spaces_is_the_second_and_UAX14_does_not_offer_it() {
        var opportunities = new List<int>();

        LineBreaker.Collect("a  b", opportunities);

        // The control: the run's own opportunity is there, so an empty or truncated list cannot pass.
        Assert.Contains(3, opportunities);

        Assert.DoesNotContain(2, opportunities);
    }
}
