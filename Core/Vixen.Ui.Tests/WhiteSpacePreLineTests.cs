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
///         ⚠ <b>§ 4.1.3's phase II is here too</b> — a collapsible run at the start or end of a line
///         is not drawn — for every label that is its own paragraph, and
///         <see cref="An_inline_leaf_keeps_its_leading_space_only_where_it_shares_a_line" /> is the
///         one element it is withheld from. It was refused as "a question about a line"; after phase
///         I the only runs a line can still begin or end on are at the two ends of the text, so it
///         was a question about the string after all. <c>Rikarin/Vixen#249</c>.
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

    /// <summary>Toggling the declaration on a settled element rebuilds the block rather than reusing it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The one line of this work nothing else in the tree could see, and it is a cache
    ///         key entry rather than a measurement.</b> <c>UiElement.Block</c> keeps the paragraph it
    ///         built and compares a key before reusing it; <c>white-space</c>'s collapsing half is an
    ///         entry in that key. Every other test in this file builds one element under one
    ///         declaration, so all of them pass with the entry deleted — a review deleted it and the
    ///         whole of this assembly stayed green.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Nothing else in the key can stand in for it.</b> <c>normal</c> and
    ///         <c>pre-line</c> both wrap and both preserve their segment breaks, so the width, the
    ///         wrapping flag and the <c>break-spaces</c> entry are identical across the toggle; the
    ///         transform is <c>none</c> on both sides; and the element's own string never changed, so
    ///         the reference test on <c>Text</c> says reuse. What a stale block draws is a paragraph
    ///         missing the characters the author wrote — or, in this direction, keeping the ones the
    ///         declaration just removed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>flex-direction: column</c> is load-bearing and the first two attempts at this
    ///         test were green under sabotage without it.</b> The width is an entry in the same key,
    ///         and layout asks a paragraph for its block more than once per pass — a row measures at
    ///         0 and then at the used width, an <c>align-items: flex-start</c> item at infinity and
    ///         then at 0. Any pass whose widths differ from the last pass's rebuilds the block on the
    ///         width alone, whatever every other entry says, so the collapse entry never gets to
    ///         decide and deleting it changes nothing. A column item is measured at one width, the
    ///         same one both passes, which is the only arrangement where this key entry is reachable
    ///         at all. Widening this to the <c>break-spaces</c> neighbour is
    ///         <c>WhiteSpaceBreakSpacesTests</c>' row of the same shape.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Toggling_the_declaration_on_a_settled_element_rebuilds_the_block() {
        var document = new UiDocument(900f, 300f);
        document.Fonts.Register("Test", Font);

        document.Load(
            """
            root         { width: 800px; height: 300px; flex-direction: column; }
            label        { font-family: Test; font-size: 16px; white-space: normal; }
            label.folded { white-space: pre-line; }
            """
        );

        var element = document.Root.Add("label");
        element.Text = "a    b";
        document.Update();

        var preserved = element.Block()!.Lines[0].Width;

        // The control: the block really was settled under the other value, so the toggle below is
        // the only thing that can change the answer.
        Assert.Equal(Width("a    b", Normal), preserved, Tolerance);

        element.AddClass("folded");
        document.Update();

        var collapsed = element.Block()!.Lines[0].Width;

        Assert.True(collapsed < preserved, "the block was reused under the declaration that replaced it");
        Assert.Equal(Width("a b", PreLine), collapsed, Tolerance);
    }

    /// <summary>A collapsible run at the start of a paragraph is not drawn, which is phase II.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This test used to be called
    ///         <c>A_leading_space_is_still_drawn_which_is_phase_two_and_is_owed</c></b>, named so a
    ///         change towards Chrome would come through it rather than past it — and it did. Chrome
    ///         removes a collapsible space at the start of a line; the refusal said that was a question
    ///         about a line and no line exists when the string is transformed. True, and it did not
    ///         matter: after phase I the start of the text is the only place a line can begin on a
    ///         collapsible run, and for a label that is its own paragraph the start of the text is
    ///         the start of its first line.
    ///     </para>
    ///     <para>
    ///         The control is <c>normal</c>, which in this engine is CSS's <c>pre-wrap</c> and keeps
    ///         the spaces — the premise the whole file rests on — so the difference is phase II and
    ///         nothing else.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_leading_run_is_not_drawn_because_it_starts_the_line() {
        var bare = Width("ab", PreLine);

        Assert.True(Width("   ab", Normal) > bare + Tolerance, "the control has to draw the spaces or this measures nothing");
        Assert.Equal(bare, Width("   ab", PreLine), Tolerance);
        Assert.Equal(bare, Width("\t ab", PreLine), Tolerance);
    }

    /// <summary>A collapsible run at the end of a paragraph is not drawn either.</summary>
    /// <remarks>
    ///     ⚠ <b>The half the wrapper could not reach.</b> <c>LineWrapper</c> trims a line's trailing
    ///     spaces only where it chose the break, so the last line of a paragraph kept them — correct
    ///     for a preserved space, which hangs, and wrong for a collapsible one, which phase II
    ///     removes. So a right-aligned <c>pre-line</c> label ending in a space drew short of its edge,
    ///     where Chrome draws it flush.
    /// </remarks>
    [Fact]
    public void A_trailing_run_is_not_drawn_because_it_ends_the_line() {
        var bare = Width("ab", PreLine);

        Assert.True(Width("ab   ", Normal) > Width("ab", Normal) + Tolerance, "the control has to count the spaces");
        Assert.Equal(bare, Width("ab   ", PreLine), Tolerance);
    }

    /// <summary>A label of nothing but spaces has no line at all, as it has no text.</summary>
    /// <remarks>
    ///     ⚠ <b>The first thing in this engine that can turn a non-empty <c>Text</c> into an empty
    ///     drawn string, and the first version of this change threw on it</b> — <c>TextLine</c>
    ///     refuses a line of no runs, and the layout pass that asked for the block took the exception
    ///     with it. Chrome gives such a paragraph no line box, so the element is as tall as one with
    ///     no text; the control under <c>normal</c>, whose spaces are preserved, is one line tall.
    /// </remarks>
    [Fact]
    public void A_label_of_only_spaces_has_no_line() {
        var (block, height) = Only("   ", PreLine);
        var (_, control) = Only("   ", Normal);

        Assert.Null(block);
        Assert.Equal(0f, height);
        Assert.True(control > 0f, "the control has to be a line tall or this measures nothing");
    }

    /// <summary>One label, its block and its laid-out height.</summary>
    /// <param name="text">What the author wrote.</param>
    /// <param name="label">The declaration.</param>
    /// <returns>The block, which may be null, and the element's height.</returns>
    static (TextLayout? Block, float Height) Only(string text, string label) {
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

        return (element.Block(), element.Height);
    }

    /// <summary>
    ///     ⚠ An inline leaf on a line its parent lays out keeps its leading space, and the same leaf in
    ///     a flex container, where it is blockified, loses it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The one element phase II is withheld from, and why.</b> A <c>display: inline</c>
    ///         element in an inline formatting context can begin in the middle of a line a sibling
    ///         started, and then whether its leading space survives depends on whether the text before
    ///         it ended in one — collapsing across an element boundary, which this engine does not
    ///         do. Removing it unconditionally would draw <c>foo</c> and <c> bar</c> as one word.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The flex half is the control and it is not decoration</b>: an implementation that
    ///         keyed the decision on the element's own <c>display</c> alone would pass the first half
    ///         and fail this one, because a flex item is blockified whatever its <c>display</c> says.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_inline_leaf_keeps_its_leading_space_only_where_it_shares_a_line() {
        Assert.Equal(Width(" ab", Normal), Inline("   ab", "display: block;"), Tolerance);
        Assert.Equal(Width("ab", PreLine), Inline("   ab", "display: flex;"), Tolerance);
    }

    /// <summary>A <c>display: inline</c> label under <c>pre-line</c>, inside a root of a given display.</summary>
    /// <param name="text">What the author wrote.</param>
    /// <param name="root">The root's <c>display</c> declaration.</param>
    /// <returns>The first line's width.</returns>
    static float Inline(string text, string root) {
        var document = new UiDocument(900f, 300f);
        document.Fonts.Register("Test", Font);

        document.Load(
            $$"""
              root { width: 800px; height: 300px; {{root}} }
              label { font-family: Test; font-size: 16px; display: inline; {{PreLine}} }
              """
        );

        var element = document.Root.Add("label");
        element.Text = text;
        document.Update();

        return element.Block()!.Lines[0].Width;
    }

    /// <summary>
    ///     Turning the parent from flex to block rebuilds an inline leaf's block, though nothing of the
    ///     leaf's own changed.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The key entry decided by somebody else's style</b>, and the one a test built from one
    ///     document cannot see: every assertion above makes a fresh element under a fixed parent. The
    ///     leaf's own declarations, its text and — in a column — its measured width are the same
    ///     either side of the toggle, so the entry is the only thing in <c>UiElement.Block</c>'s key
    ///     that can tell the two apart.
    /// </remarks>
    [Fact]
    public void Turning_the_parent_to_block_rebuilds_an_inline_leafs_block() {
        var document = new UiDocument(900f, 300f);
        document.Fonts.Register("Test", Font);

        document.Load(
            $$"""
              root         { width: 800px; height: 300px; display: flex; flex-direction: column; }
              root.flowing { display: block; }
              label        { font-family: Test; font-size: 16px; display: inline; {{PreLine}} }
              """
        );

        var element = document.Root.Add("label");
        element.Text = "   ab";
        document.Update();

        var blockified = element.Block()!.Lines[0].Width;

        Assert.Equal(Width("ab", PreLine), blockified, Tolerance);

        document.Root.AddClass("flowing");
        document.Update();

        Assert.Equal(Width(" ab", Normal), element.Block()!.Lines[0].Width, Tolerance);
    }
}
