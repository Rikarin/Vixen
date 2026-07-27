// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Text.Tests;

/// <summary>Where a caret goes, and what a click means.</summary>
/// <remarks>
///     A shaping cluster is not a grapheme cluster. A cluster is whatever the shaper could not
///     subdivide; a grapheme is what a person would call one character. They agree often enough to
///     be dangerous and disagree in exactly the scripts that are hard to test by eye — which is why
///     most of what is checked here is an <i>invariant</i> rather than a number somebody typed.
/// </remarks>
public class CaretTests {
    public static TheoryData<string, string> Strings => new() {
        { TestFonts.ContextualLatin, "a a" },
        { TestFonts.Kannada, "ಲ್ಲಿ" },
        { TestFonts.Kannada, "ಖ್ಯೆ ಫ್ರಿ" },
        { TestFonts.Arabic, "لسان" },
        { TestFonts.Arabic, "فن خطاطی" },
        { TestFonts.Arabic, "abcلسان" }
    };

    [Theory]
    [MemberData(nameof(Strings))]
    public void Hit_testing_a_caret_gives_the_caret_back_when_the_text_runs_one_way(string fontName, string text) {
        var shaped = TextShaper.Shape(TestFonts.Load(fontName), text);

        // ⚠ Only where the text runs one way, and the exception below is why rather than an
        // exemption. Asserting this everywhere would have meant deleting the mixed case or
        // inventing a rule to make it pass, and both would have hidden a real property of bidi
        // text. Even the weaker "lands somewhere that draws at the same x" is false there.
        if (shaped.Runs.Select(run => run.Item.IsRightToLeft).Distinct().Count() > 1) {
            return;
        }

        // The one property worth more than any expectation about where a caret lands: it holds for
        // scripts nobody thought to write a case for.
        foreach (var boundary in Boundaries(text)) {
            Assert.Equal(boundary, shaped.CaretIndexAt(shaped.CaretOffset(boundary)));
        }
    }

    [Fact]
    public void One_caret_index_at_a_direction_boundary_has_two_places_it_could_be() {
        const string mixed = "abcلسان";
        var shaped = TextShaper.Shape(TestFonts.Load(TestFonts.Arabic), mixed);
        var junction = shaped.Runs.First(run => run.Item.Script == Script.Latin).Advance;

        // Index 3 is both "after the c" and "before the first Arabic letter", and those are at
        // opposite ends of the Arabic run. This API answers with the logical one — the leading edge
        // of the character the index names — so a caret index alone cannot say which the user
        // meant. Telling them apart needs an affinity carried beside the index, which is owed with
        // TextEditor; what is not acceptable is for that to be discovered by someone watching a
        // caret teleport.
        Assert.Equal(shaped.Advance, shaped.CaretOffset(3), 3);
        Assert.Equal(junction, shaped.CaretOffset(mixed.Length), 3);

        // And the same point on screen therefore has two indices. Drawing order breaks the tie.
        Assert.Equal(3, shaped.CaretIndexAt(junction));
    }

    [Theory]
    [MemberData(nameof(Strings))]
    public void The_clusters_tile_the_text_without_a_gap_or_an_overlap(string fontName, string text) {
        var shaped = TextShaper.Shape(TestFonts.Load(fontName), text);
        var covered = new bool[text.Length];

        foreach (var span in shaped.Clusters) {
            Assert.True(span.End > span.Start, $"cluster [{span.Start},{span.End}) is empty or inverted");

            for (var i = span.Start; i < span.End; i++) {
                Assert.False(covered[i], $"character {i} is in two clusters");
                covered[i] = true;
            }
        }

        Assert.All(covered, Assert.True);
    }

    [Fact]
    public void A_caret_moves_left_through_left_to_right_text() {
        var shaped = TextShaper.Shape(TestFonts.Load(TestFonts.ContextualLatin), "a a");

        var offsets = Boundaries("a a").Select(shaped.CaretOffset).ToList();

        Assert.Equal(offsets.Order(), offsets);
        Assert.Equal(0, offsets[0]);
        Assert.Equal(shaped.Advance, offsets[^1]);
    }

    [Fact]
    public void A_caret_moves_right_through_right_to_left_text() {
        const string word = "لسان";
        var shaped = TextShaper.Shape(TestFonts.Load(TestFonts.Arabic), word);

        var offsets = Boundaries(word).Select(shaped.CaretOffset).ToList();

        // Reading forwards moves the caret leftwards on the screen, which is the whole point of
        // keeping the caret in text order and the geometry in visual order.
        Assert.Equal(offsets.OrderDescending(), offsets);
        Assert.Equal(shaped.Advance, offsets[0]);
        Assert.Equal(0, offsets[^1]);
    }

    [Fact]
    public void A_caret_lands_inside_a_cluster_that_holds_more_than_one_character() {
        // Five code points, and the shaper produces two glyphs — so three of the four caret
        // positions inside this syllable have no glyph edge to sit on.
        const string syllable = "ಲ್ಲಿ";
        var shaped = TextShaper.Shape(TestFonts.Load(TestFonts.Kannada), syllable);

        var span = shaped.Clusters.First(cluster => cluster.End - cluster.Start > 1);
        var inside = Boundaries(syllable).Where(index => index > span.Start && index < span.End).ToList();

        Assert.NotEmpty(inside);

        foreach (var index in inside) {
            var offset = shaped.CaretOffset(index);

            Assert.True(
                offset > span.X && offset < span.Right,
                $"the caret at {index} sits at {offset}, on the edge of [{span.X}, {span.Right}] rather than inside it"
            );
        }
    }

    [Fact]
    public void Clicking_past_the_end_of_the_line_puts_the_caret_at_the_end() {
        const string text = "a a";
        var shaped = TextShaper.Shape(TestFonts.Load(TestFonts.ContextualLatin), text);

        Assert.Equal(text.Length, shaped.CaretIndexAt(shaped.Advance * 4));
        Assert.Equal(0, shaped.CaretIndexAt(-shaped.Advance));
    }

    [Fact]
    public void Empty_text_has_one_caret_position_and_it_is_at_zero() {
        var shaped = TextShaper.Shape(TestFonts.Load(TestFonts.ContextualLatin), string.Empty);

        Assert.Empty(shaped.Clusters);
        Assert.Equal(0, shaped.CaretOffset(0));
        Assert.Equal(0, shaped.CaretIndexAt(37));
    }

    static List<int> Boundaries(string text) {
        var found = new List<int>();
        GraphemeBreaker.Collect(text, found);
        return found;
    }
}
