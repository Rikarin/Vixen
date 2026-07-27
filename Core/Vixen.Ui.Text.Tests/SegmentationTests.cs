// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Text.Tests;

/// <summary>What the conformance suite cannot say.</summary>
/// <remarks>
///     <para>
///         The 2 710 generated cases settle whether the rules are right. They say nothing about the
///         <i>API</i> built on top of them — the suite only ever calls one method, with one shape of
///         input, and every case is a handful of code points chosen to exercise a rule rather than to
///         look like anything a person would type.
///     </para>
///     <para>
///         So these cover what a text editor will actually ask: where the caret goes, what backspace
///         deletes, what a double-click selects, and what happens to a string that is malformed.
///     </para>
/// </remarks>
public class SegmentationTests {
    [Fact]
    public void A_family_emoji_is_one_character_to_a_person_and_eleven_code_points_to_a_computer() {
        // 👨‍👩‍👧‍👦 — man, ZWJ, woman, ZWJ, girl, ZWJ, boy. Backspace has to delete the family,
        // not the boy, and this is the case everyone's text editor gets wrong at least once.
        const string Family = "\U0001F468‍\U0001F469‍\U0001F467‍\U0001F466";

        Assert.Equal(11, Family.Length);
        Assert.Equal(1, GraphemeBreaker.Count(Family));
        Assert.Equal((0, 11), GraphemeBreaker.ClusterAt(Family, 6));
    }

    [Fact]
    public void An_accented_letter_written_two_ways_counts_the_same_either_way() {
        // Composed and decomposed forms are different strings and the same character. A field
        // limited to ten characters must not accept fewer of one than the other.
        Assert.Equal(1, GraphemeBreaker.Count("é"));
        Assert.Equal(1, GraphemeBreaker.Count("é"));
        Assert.Equal(4, GraphemeBreaker.Count("cafe\u0301"));
    }

    [Fact]
    public void Four_regional_indicators_are_two_flags() {
        // 🇬🇧🇯🇵 — the rule that makes a run of flags selectable one at a time rather than
        // collapsing into one enormous cluster.
        const string Flags = "\U0001F1EC\U0001F1E7\U0001F1EF\U0001F1F5";

        Assert.Equal(2, GraphemeBreaker.Count(Flags));
        Assert.Equal([0, 4, 8], GraphemeBreaker.Boundaries(Flags));
    }

    [Fact]
    public void A_devanagari_conjunct_holds_together_across_its_virama() {
        // क्षि — ka, virama, ssa, vowel sign i. GB9c, and the rule most implementations skip
        // because it needs a property from a UCD file the others do not use.
        const string Conjunct = "क्षि";

        Assert.Equal(1, GraphemeBreaker.Count(Conjunct));
    }

    [Fact]
    public void A_CRLF_is_one_cluster_and_a_LFCR_is_two() {
        Assert.Equal(1, GraphemeBreaker.Count("\r\n"));
        Assert.Equal(2, GraphemeBreaker.Count("\n\r"));
    }

    [Fact]
    public void The_caret_can_land_on_every_boundary_and_nowhere_else() {
        const string Text = "áb";

        Assert.True(GraphemeBreaker.IsBoundary(Text, 0));
        Assert.False(GraphemeBreaker.IsBoundary(Text, 1));
        Assert.True(GraphemeBreaker.IsBoundary(Text, 2));
        Assert.True(GraphemeBreaker.IsBoundary(Text, 3));
    }

    [Fact]
    public void An_unpaired_surrogate_is_still_something_the_caret_can_move_over() {
        // A malformed string has to stay editable — that is how it gets fixed. Substituting U+FFFD
        // would move a boundary the caret is about to be put at.
        var lone = "a" + (char) 0xD800 + "b";

        Assert.Equal(3, GraphemeBreaker.Count(lone));
        Assert.Equal([0, 1, 2, 3], GraphemeBreaker.Boundaries(lone));
    }

    [Fact]
    public void The_empty_string_has_one_boundary_and_no_characters() {
        Assert.Equal([0], GraphemeBreaker.Boundaries(string.Empty));
        Assert.Equal(0, GraphemeBreaker.Count(string.Empty));
    }

    [Fact]
    public void A_contraction_is_one_word() {
        // The apostrophe in `can't` is only medial because there is a letter after it, which is why
        // the word rules need lookahead and the cluster rules do not.
        Assert.Equal([0, 5], WordBreaker.Boundaries("can't"));
        Assert.Equal([0, 3, 4], WordBreaker.Boundaries("can'"));
    }

    [Fact]
    public void A_formatted_number_is_one_word() {
        Assert.Equal([0, 8], WordBreaker.Boundaries("1,000.50"));
    }

    [Fact]
    public void A_double_click_selects_the_word_under_it_and_not_the_spaces_around_it() {
        const string Sentence = "the quick brown fox";

        Assert.Equal((4, 9), WordBreaker.WordAt(Sentence, 6));
        Assert.Equal((3, 4), WordBreaker.WordAt(Sentence, 3));
        Assert.Equal((16, 19), WordBreaker.WordAt(Sentence, 18));
    }

    [Fact]
    public void Katakana_holds_together_although_nothing_around_it_is_spaced() {
        // Japanese does not use spaces, and WB13 is the only word rule that gives a double-click
        // anything to select.
        Assert.Equal([0, 4], WordBreaker.Boundaries("カタカナ"));

        // And the limit of it, which is worth knowing before someone reports it as a bug: WB13 names
        // Katakana and nothing else, so hiragana falls to WB999 and every syllable is its own word.
        // Segmenting Japanese properly needs a dictionary, which UAX#29 does not claim to be.
        Assert.Equal([0, 1, 2], WordBreaker.Boundaries("です"));
    }

    [Fact]
    public void A_zero_width_joiner_does_not_join_two_letters() {
        // WB3c is `ZWJ × ExtendedPictographic`, and the pictographic half is what stops a joiner
        // between two letters gluing them into one word.
        Assert.Equal([0, 3], WordBreaker.Boundaries("a‍b"));

        // Two clusters, not three: GB9 attaches the joiner to whatever precedes it, so `a` keeps it
        // and `b` stands alone. The joiner never *starts* a cluster.
        Assert.Equal(2, GraphemeBreaker.Count("a‍b"));
    }

    [Fact]
    public void A_letter_that_is_also_a_pictograph_is_still_a_letter() {
        // ⓜ U+24C2 is Word_Break=ALetter and Extended_Pictographic=Yes at once, from two different
        // UCD files. Folding those into one class table made one shadow the other and broke
        // forty-four conformance cases; this is the shortest statement of what went wrong.
        Assert.Equal([0, 2], WordBreaker.Boundaries("Ⓜa"));
    }
}
