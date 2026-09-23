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
///         ⚠ <b>What this does not do, stated because a test file is where the next reader looks.</b>
///         § 4.1.3's phase II — a collapsible space at the <i>start</i> of a line is removed — is a
///         question about a line, and no line exists at the moment a string is transformed. So
///         <c>  ab</c> under <c>pre-line</c> keeps its leading space here and a browser eats it. That
///         gap belongs to every value in this engine rather than to this one, since an undeclared
///         paragraph preserves everything. <c>Rikarin/Vixen#249</c>.
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

    /// <summary>Collapsing under <c>pre-line</c>.</summary>
    /// <param name="source">What the author wrote.</param>
    /// <returns>The transformed text.</returns>
    static TransformedText Collapsed(string source) =>
        TransformedText.Of(source, TextTransform.None, language: null, WhiteSpaceCollapse.PreserveBreaks);
}
