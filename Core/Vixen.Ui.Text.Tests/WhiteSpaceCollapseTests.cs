// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Text.Tests;

/// <summary>CSS Text 4 § 4.1.1's phase I, which is the whole of what <c>white-space: pre-line</c> is.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The first transformation in this engine that makes the shaped text <i>shorter</i>
///         than what the author wrote</b>, which is why the assertions below are about the index map
///         at least as much as about the string. <c>text-transform</c> could only ever expand —
///         <c>ß</c> to <c>SS</c> — so every drawn index had a source index at or before it, and a
///         reader that forgot the map was wrong by a character on one German word. A collapse can
///         drop an arbitrarily long run, so a reader that forgets it is wrong by that run on every
///         index after it.
///     </para>
///     <para>
///         ⚠ <b>The direction that is easy to get backwards is source → drawn over a removed run.</b>
///         Every index inside the run has to land on the same drawn index — the one the run's first
///         surviving neighbour occupies — because the run is not at any position a caret can be at
///         any more. <see cref="Every_index_of_a_removed_run_lands_on_the_character_after_it" />
///         is that, and it is what a selection anchored in the middle of an author's indentation
///         reads back as.
///     </para>
///     <para>
///         ⚠ <b>§ 4.1.3's phase II is here too, for the half of it that is a question about the
///         string.</b> It removes a collapsible space at the start or end of a line, which was
///         refused for years as "a question about a line" — but after phase I the only runs a line
///         can still begin or end on are the ones at the very start and end of the text, so for a
///         paragraph that owns its lines they are string positions.
///         <see cref="A_paragraph_that_owns_its_lines_loses_a_run_at_either_end" /> holds that, and
///         the same source without <c>ownsLines</c> is its control. <c>Rikarin/Vixen#249</c>.
///     </para>
/// </remarks>
public class WhiteSpaceCollapseTests {
    /// <summary>Preserving is what every value but one asks for, and it costs nothing.</summary>
    /// <remarks>
    ///     The same instance, for <c>TransformedTextTests</c>' reason: <c>UiElement.Block</c> keys its
    ///     cache on the identity of the string it shaped.
    /// </remarks>
    [Fact]
    public void Preserve_returns_the_same_string_instance() {
        var source = "a  b\n  c";
        var collapsed = TransformedText.Of(source, TextTransform.None, language: null, WhiteSpaceCollapse.Preserve);

        Assert.Same(source, collapsed.Text);
        Assert.True(collapsed.IsIdentity);
    }

    /// <summary>A run of spaces becomes one space.</summary>
    [Fact]
    public void A_run_of_spaces_collapses_to_one() {
        var collapsed = Collapsed("a   b");

        Assert.Equal("a b", collapsed.Text);
        Assert.False(collapsed.IsIdentity);
    }

    /// <summary>A tab is collapsible white space and comes out as a space.</summary>
    /// <remarks>
    ///     § 4.1.1's third step, and it is why <c>tab-size</c> stops applying under this value: there
    ///     is no tab left in the shaped text for <c>TextRun.IsTab</c> to find a stop for.
    /// </remarks>
    [Fact]
    public void A_tab_becomes_a_space_and_joins_the_run_beside_it() {
        Assert.Equal("a b", Collapsed("a\tb").Text);
        Assert.Equal("a b", Collapsed("a \t b").Text);
    }

    /// <summary>A run touching a segment break on either side is removed rather than collapsed.</summary>
    /// <remarks>
    ///     § 4.1.1's first step. The two directions are separate clauses in the implementation and
    ///     both are asserted, because an implementation that only looked forward would leave the
    ///     indentation of every line after the first.
    /// </remarks>
    [Fact]
    public void A_run_beside_a_segment_break_is_removed_on_both_sides() {
        Assert.Equal("a\nb", Collapsed("a  \nb").Text);
        Assert.Equal("a\nb", Collapsed("a\n  b").Text);
        Assert.Equal("a\nb", Collapsed("a \n b").Text);
        Assert.Equal("a\n\nb", Collapsed("a \n \n b").Text);
    }

    /// <summary>The seven segment breaks, and not U+000A alone.</summary>
    /// <remarks>
    ///     ⚠ <b>The same seven <c>LineWrapper.IsSegmentBreak</c> reads, because the two have to agree
    ///     about what ends a line.</b> A collapse that only knew about the newline would leave a
    ///     space before U+2028 — and the wrapper ends a line there, so the space would be a line's
    ///     trailing character under a value whose whole content is that such spaces are gone.
    /// </remarks>
    [Theory]
    [InlineData('\n')]
    [InlineData('\u000b')]
    [InlineData('\u000c')]
    [InlineData('\r')]
    [InlineData('\u0085')]
    [InlineData('\u2028')]
    [InlineData('\u2029')]
    public void Every_segment_break_the_wrapper_knows_removes_the_run_beside_it(char breaker) {
        Assert.Equal($"a{breaker}b", Collapsed($"a  {breaker}  b").Text);
    }

    /// <summary>A no-break space is not collapsible and survives whole.</summary>
    /// <remarks>
    ///     ⚠ The trap <c>char.IsWhiteSpace</c> is: it answers true for U+00A0 and for all seven
    ///     segment breaks, so a collapse written on it would eat the newlines this value exists to
    ///     keep and would fold the one space CSS says never folds.
    /// </remarks>
    [Fact]
    public void A_no_break_space_is_not_collapsible() {
        Assert.Equal("a\u00a0\u00a0b", Collapsed("a\u00a0\u00a0b").Text);
    }

    /// <summary>Every index of a removed run lands on the character that follows it.</summary>
    [Fact]
    public void Every_index_of_a_removed_run_lands_on_the_character_after_it() {
        //           0123 4 5678
        var collapsed = Collapsed("ab  \n  cd");

        Assert.Equal("ab\ncd", collapsed.Text);

        // `ab` is untouched.
        Assert.Equal(0, collapsed.ToDrawn(0));
        Assert.Equal(1, collapsed.ToDrawn(1));

        // Both spaces before the break, and the break itself, are at the newline's drawn index.
        Assert.Equal(2, collapsed.ToDrawn(2));
        Assert.Equal(2, collapsed.ToDrawn(3));
        Assert.Equal(2, collapsed.ToDrawn(4));

        // Both spaces after it are at `c`.
        Assert.Equal(3, collapsed.ToDrawn(5));
        Assert.Equal(3, collapsed.ToDrawn(6));
        Assert.Equal(3, collapsed.ToDrawn(7));
        Assert.Equal(4, collapsed.ToDrawn(8));
        Assert.Equal(5, collapsed.ToDrawn(9));
    }

    /// <summary>A drawn index comes back as a source index the author's own string has.</summary>
    /// <remarks>
    ///     The half a caret reads. The surviving space of a collapsed run reports the run's
    ///     <i>first</i> character, which is the same collapse <c>Record</c> makes for the two halves
    ///     of a surrogate pair and for the two <c>S</c>s of an expanded <c>ß</c>.
    /// </remarks>
    [Fact]
    public void A_drawn_index_maps_back_to_the_first_character_of_its_run() {
        //           01234
        var collapsed = Collapsed("a   b");

        Assert.Equal("a b", collapsed.Text);
        Assert.Equal(0, collapsed.ToSource(0));
        Assert.Equal(1, collapsed.ToSource(1));
        Assert.Equal(4, collapsed.ToSource(2));
        Assert.Equal(5, collapsed.ToSource(3));
    }

    /// <summary>Source to drawn to source is the identity, as it is for a case expansion.</summary>
    /// <remarks>
    ///     ⚠ <b>It is NOT the identity for a collapsed run and must not be</b> — a character that is
    ///     no longer drawn has no drawn index of its own — so the property is asserted over the
    ///     characters that survive, which is what a caret can be at. The round trip the other way is
    ///     the one the type's remarks say is not an inverse.
    /// </remarks>
    [Fact]
    public void A_surviving_index_survives_the_round_trip() {
        var source = "ab \t c\n  de";
        var collapsed = Collapsed(source);

        Assert.Equal("ab c\nde", collapsed.Text);

        foreach (var index in new[] { 0, 1, 5, 6, 9, 10, source.Length }) {
            Assert.Equal(index, collapsed.ToSource(collapsed.ToDrawn(index)));
        }
    }

    /// <summary>Casing and collapsing compose, and neither sees the other's answer.</summary>
    /// <remarks>
    ///     ⚠ <b>The pair that proves the two stages are in one walk rather than one applied to the
    ///     other's output by a caller.</b> <c>straße</c> expands by one character and the run before
    ///     it drops two, so the map has to carry a deletion and an expansion at once — which is the
    ///     arrangement in which an implementation that kept two maps and forgot to compose them is
    ///     off by exactly the difference.
    /// </remarks>
    [Fact]
    public void A_collapse_and_an_expanding_case_mapping_share_one_map() {
        //           0123456789
        var collapsed = TransformedText.Of(
            "a   straße",
            TextTransform.Uppercase,
            language: null,
            WhiteSpaceCollapse.PreserveBreaks
        );

        Assert.Equal("A STRASSE", collapsed.Text);

        // `s` is at source 4 and drawn 2; the `ß` at source 8 is the `SS` at drawn 6, and both of
        // its units come back to it.
        Assert.Equal(2, collapsed.ToDrawn(4));
        Assert.Equal(6, collapsed.ToDrawn(8));
        Assert.Equal(8, collapsed.ToSource(6));
        Assert.Equal(8, collapsed.ToSource(7));
        Assert.Equal(9, collapsed.ToSource(8));
    }

    /// <summary>
    ///     ⚠ A paragraph that owns its lines loses a collapsible run at either end — § 4.1.3's phase
    ///     II — and one that does not keeps each as one space.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Both ends and both answers, over one string.</b> The pair is what makes this a
    ///         measurement of the parameter rather than of the collapse: without <c>ownsLines</c> the
    ///         same source is phase I alone, a run at each end folded to one space, and the difference
    ///         between the two answers is the two spaces phase II removes.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The map is the half a caret reads.</b> Every index of the leading run lands on
    ///         the first letter, since the run is at no position a caret can occupy any more; the
    ///         trailing run lands on the end of the drawn text, as a removed run before a newline lands
    ///         on the newline.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_paragraph_that_owns_its_lines_loses_a_run_at_either_end() {
        var source = " \t a  b  ";
        var owned = TransformedText.Of(source, TextTransform.None, language: null, WhiteSpaceCollapse.PreserveBreaks, ownsLines: true);
        var shared = Collapsed(source);

        Assert.Equal(" a b ", shared.Text);
        Assert.Equal("a b", owned.Text);

        Assert.Equal(0, owned.ToDrawn(0));
        Assert.Equal(0, owned.ToDrawn(2));
        Assert.Equal(0, owned.ToDrawn(3));
        Assert.Equal(3, owned.ToDrawn(7));
        Assert.Equal(3, owned.ToDrawn(source.Length));
        Assert.Equal(3, owned.ToSource(0));
        Assert.Equal(6, owned.ToSource(2));
    }

    /// <summary>A run in the middle is phase I's and is not touched by the paragraph's edges.</summary>
    /// <remarks>
    ///     ⚠ The shape an implementation keyed on "any run" rather than on "a run at an end" would
    ///     get wrong, and it would get it wrong silently everywhere: <c>a b</c> drawn as <c>ab</c>.
    /// </remarks>
    [Fact]
    public void A_run_between_two_words_still_becomes_one_space() {
        var owned = TransformedText.Of("a   b", TextTransform.None, language: null, WhiteSpaceCollapse.PreserveBreaks, ownsLines: true);

        Assert.Equal("a b", owned.Text);
    }

    /// <summary>A text that is nothing but collapsible white space draws nothing at all.</summary>
    /// <remarks>
    ///     The run is at both ends at once. Chrome gives such a paragraph no line box; what this
    ///     asserts is the string, and <c>WhiteSpacePreLineTests</c> asserts the element survives it.
    /// </remarks>
    [Fact]
    public void A_text_of_only_spaces_draws_nothing() {
        var owned = TransformedText.Of("  \t ", TextTransform.None, language: null, WhiteSpaceCollapse.PreserveBreaks, ownsLines: true);

        Assert.Equal("", owned.Text);
        Assert.Equal(0, owned.ToDrawn(2));
    }

    /// <summary>The paragraph's edges mean nothing to a value that preserves.</summary>
    /// <remarks>
    ///     ⚠ <b>Phase II removes a <i>collapsible</i> space, and under <c>preserve</c> there is
    ///     none.</b> Chrome draws a <c>pre-wrap</c> paragraph's leading spaces, and this engine's
    ///     undeclared paragraph is <c>pre-wrap</c> — so a parameter that reached this value would move
    ///     every label with a leading space in every interface. Same instance, for
    ///     <see cref="Preserve_returns_the_same_string_instance" />'s reason.
    /// </remarks>
    [Fact]
    public void Owning_its_lines_changes_nothing_under_preserve() {
        var source = "  a  ";
        var preserved = TransformedText.Of(source, TextTransform.None, language: null, WhiteSpaceCollapse.Preserve, ownsLines: true);

        Assert.Same(source, preserved.Text);
    }

    /// <summary>Collapsing under <c>pre-line</c>.</summary>
    /// <param name="source">What the author wrote.</param>
    /// <returns>The transformed text.</returns>
    static TransformedText Collapsed(string source) =>
        TransformedText.Of(source, TextTransform.None, language: null, WhiteSpaceCollapse.PreserveBreaks);
}
