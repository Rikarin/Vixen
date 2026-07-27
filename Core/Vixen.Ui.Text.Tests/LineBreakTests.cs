// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Text.Tests;

/// <summary>What the line break conformance suite cannot say.</summary>
/// <remarks>
///     The 19 338 generated cases settle the rules. They are pairs and short sequences chosen to
///     exercise one rule each; none of them is a sentence. These are the things a paragraph of real
///     text will ask for, and the API around them.
/// </remarks>
public class LineBreakTests {
    [Fact]
    public void A_sentence_offers_a_break_after_each_space_and_nowhere_else() {
        Assert.Equal([4, 10, 13], LineBreaker.Opportunities("the quick fox"));
    }

    [Fact]
    public void A_number_does_not_split_at_its_separators() {
        // The rule everybody has seen broken: a table of figures wrapping mid-number.
        Assert.Equal([8], LineBreaker.Opportunities("1,000.50"));
        Assert.Equal([9], LineBreaker.Opportunities("$1,000.50"));
    }

    [Fact]
    public void A_hyphen_offers_a_break_and_a_non_breaking_hyphen_does_not() {
        // U+2010 HYPHEN offers one; U+2011 NON-BREAKING HYPHEN is the whole point of its existing.
        Assert.Equal([5, 9, 12], LineBreaker.Opportunities("well‐fed cat"));
        Assert.Equal([8], LineBreaker.Opportunities("well‑fed"));
    }

    [Fact]
    public void An_opening_bracket_keeps_what_follows_it() {
        Assert.Equal([4, 8], LineBreaker.Opportunities("see (the"));
    }

    [Fact]
    public void A_no_break_space_offers_nothing() {
        // Written as an escape, not as the character: a literal non-breaking space is invisible
        // in a diff and does not survive a round trip through a terminal — which is how both sides
        // of this test briefly became the same string and proved nothing. U+00A0 exists so that
        // `10 kg` cannot wrap between the number and its unit.
        var glued = LineBreaker.Opportunities("10\u00A0kg/m");
        var ordinary = LineBreaker.Opportunities("10 kg/m");

        Assert.DoesNotContain(3, glued);
        Assert.Contains(3, ordinary);
    }

    [Fact]
    public void CJK_breaks_between_almost_any_two_characters() {
        // Chinese and Japanese have no spaces, and wrapping them on spaces alone produces one line
        // as wide as the paragraph.
        Assert.Equal([1, 2, 3, 4], LineBreaker.Opportunities("日本語で"));
    }

    [Fact]
    public void A_small_kana_does_not_start_a_line() {
        // `CJ` — conditional Japanese starter. っ is a small tsu and belongs with what precedes it.
        Assert.Equal([2, 3], LineBreaker.Opportunities("あっち"));
    }

    [Fact]
    public void A_hard_newline_is_mandatory_and_a_space_is_not() {
        const string Text = "a\nb c";

        Assert.True(LineBreaker.IsMandatory(Text, 2));
        Assert.False(LineBreaker.IsMandatory(Text, 4));
        Assert.True(LineBreaker.IsMandatory(Text, Text.Length));
    }

    [Fact]
    public void A_CRLF_is_one_mandatory_break_and_not_two() {
        const string Text = "a\r\nb";

        Assert.False(LineBreaker.IsMandatory(Text, 2));
        Assert.True(LineBreaker.IsMandatory(Text, 3));
    }

    [Fact]
    public void The_end_of_the_text_is_always_an_opportunity_and_the_start_never_is() {
        // LB2 and LB3. Position zero is deliberately absent, unlike the segmentation breakers, and
        // it is why this returns "opportunities" rather than "boundaries".
        var opportunities = LineBreaker.Opportunities("ab");

        Assert.DoesNotContain(0, opportunities);
        Assert.Contains(2, opportunities);
    }

    [Fact]
    public void The_empty_string_offers_nothing() {
        Assert.Empty(LineBreaker.Opportunities(string.Empty));
    }

    [Fact]
    public void A_combining_mark_never_separates_from_what_it_combines_with() {
        // LB9. Decomposed text must not wrap between a letter and its accent. The two spellings
        // cannot be compared position for position — they are different lengths, which is what
        // makes this worth asserting rather than assuming.
        Assert.Equal([5, 7], LineBreaker.Opportunities("café au"));
        Assert.Equal([6, 8], LineBreaker.Opportunities("café au"));
    }
}
