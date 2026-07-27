// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Text.Tests;

/// <summary>What ninety thousand cases of code points cannot say.</summary>
/// <remarks>
///     The conformance suite settles the algorithm. It says nothing about the API on top, and none of
///     its cases is a sentence anybody would write. These are.
/// </remarks>
public class BidiTests {
    // "shalom" in Hebrew, and "peace" in Arabic. Real words, so that a failure is legible.
    const string Hebrew = "שלום";
    const string Arabic = "سلام";

    [Fact]
    public void An_English_paragraph_is_left_to_right_and_stays_in_order() {
        var result = BidiAlgorithm.Resolve("hello");

        Assert.Equal(0, result.ParagraphLevel);
        Assert.Equal([0, 1, 2, 3, 4], result.VisualOrder);
        Assert.All(result.Levels, level => Assert.Equal(0, level));
    }

    [Fact]
    public void A_Hebrew_paragraph_is_right_to_left_and_comes_out_reversed() {
        var result = BidiAlgorithm.Resolve(Hebrew);

        Assert.Equal(1, result.ParagraphLevel);
        Assert.Equal([3, 2, 1, 0], result.VisualOrder);
    }

    [Fact]
    public void The_base_direction_comes_from_the_first_strong_character() {
        // P2 and P3, which is why almost nothing should ever pass an explicit direction: the text
        // already says which way it runs.
        Assert.Equal(1, BidiAlgorithm.Resolve($"{Hebrew} hello").ParagraphLevel);
        Assert.Equal(0, BidiAlgorithm.Resolve($"hello {Hebrew}").ParagraphLevel);

        // Digits and punctuation are not strong, so a paragraph of them takes the default.
        Assert.Equal(0, BidiAlgorithm.Resolve("123 —").ParagraphLevel);
    }

    [Fact]
    public void An_explicit_direction_overrides_what_the_text_says() {
        Assert.Equal(1, BidiAlgorithm.Resolve("hello", ParagraphDirection.RightToLeft).ParagraphLevel);
        Assert.Equal(0, BidiAlgorithm.Resolve(Hebrew, ParagraphDirection.LeftToRight).ParagraphLevel);
    }

    [Fact]
    public void An_English_word_inside_a_Hebrew_sentence_keeps_its_own_direction() {
        // The case the whole algorithm exists for. The Hebrew reverses; the Latin inside it does not.
        var result = BidiAlgorithm.Resolve($"{Hebrew} abc {Hebrew}");

        Assert.Equal(1, result.ParagraphLevel);

        // The Latin run is at an even level inside an odd paragraph, which is what "keeps its own
        // direction" means numerically.
        Assert.Equal(2, result.Levels[5]);
        Assert.Equal(1, result.Levels[0]);
    }

    [Fact]
    public void A_number_in_an_Arabic_sentence_reads_left_to_right() {
        // I1's two-level bump. Arabic runs right to left and its digits do not, and a phone number
        // rendered backwards is the single most recognisable bidi bug there is.
        var result = BidiAlgorithm.Resolve($"{Arabic} 2024 {Arabic}");
        var digits = Enumerable.Range(5, 4).Select(i => result.Levels[i]).ToArray();

        Assert.Equal(1, result.ParagraphLevel);
        Assert.All(digits, level => Assert.Equal(2, level));

        // And they stay in reading order within the reversed paragraph.
        var order = result.VisualOrder;
        var positions = digits.Select((_, i) => Array.IndexOf(order, 5 + i)).ToArray();

        Assert.Equal(positions.OrderBy(p => p), positions);
    }

    [Fact]
    public void Brackets_around_right_to_left_text_point_the_right_way() {
        // N0. Without it, `(שלום)` renders with both brackets facing the same way, which reads as a
        // rendering fault rather than as a direction one.
        var result = BidiAlgorithm.Resolve($"({Hebrew})", ParagraphDirection.LeftToRight);

        Assert.Equal(result.Levels[0], result.Levels[^1]);
        Assert.Equal(1, result.Levels[1]);
    }

    [Fact]
    public void Trailing_whitespace_belongs_to_the_paragraph_and_not_to_the_last_word() {
        // L1. Without it a right-to-left paragraph puts its trailing spaces on the left, pushing the
        // text off the margin it should sit against.
        var result = BidiAlgorithm.Resolve($"{Hebrew}   ");

        Assert.Equal(1, result.ParagraphLevel);
        Assert.All(result.Levels[^3..], level => Assert.Equal(1, level));
    }

    [Fact]
    public void An_isolate_keeps_what_is_inside_it_from_affecting_what_is_outside() {
        // The reason isolates were added in Unicode 6.3 and the reason to prefer them to embeddings:
        // a Hebrew name in a first-strong isolate must not turn the whole paragraph around.
        var isolated = BidiAlgorithm.Resolve($"⁨{Hebrew}⁩ and then");
        var embedded = BidiAlgorithm.Resolve($"{Hebrew} and then");

        Assert.Equal(0, isolated.ParagraphLevel);
        Assert.Equal(1, embedded.ParagraphLevel);
    }

    [Fact]
    public void The_embedding_controls_are_removed_from_the_visual_order() {
        // X9 and L3. They are instructions, not characters, and drawing them would put a blank where
        // the text should be.
        var result = BidiAlgorithm.Resolve($"a‫{Hebrew}‬b");

        Assert.DoesNotContain(1, result.VisualOrder);
        Assert.DoesNotContain(6, result.VisualOrder);
        Assert.Equal(result.Levels.Length - 2, result.VisualOrder.Length);
    }

    [Fact]
    public void The_empty_paragraph_resolves_to_nothing() {
        var result = BidiAlgorithm.Resolve(string.Empty);

        Assert.Equal(0, result.ParagraphLevel);
        Assert.Empty(result.VisualOrder);
    }

    [Fact]
    public void An_astral_character_counts_as_one_position_and_not_two() {
        // The API works in code points, because levels are per character and a surrogate pair is one
        // character. A UTF-16 index here would put half a character at a different level from the
        // other half.
        var result = BidiAlgorithm.Resolve("a\U0001F600b");

        Assert.Equal(3, result.Levels.Length);
        Assert.Equal([0, 1, 2], result.VisualOrder);
    }
}
