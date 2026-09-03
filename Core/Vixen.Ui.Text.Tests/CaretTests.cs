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

        // ⚠ The guard that used to stand here was a silent `return` for any text with runs of two
        // directions — so the mixed case was in the theory data, reported as passing, and asserted
        // nothing at all. That is the shape this repository keeps finding: an instrument that does
        // not run prints success. The mixed case is now asserted by the test below, which is what
        // an affinity buys; this one keeps the narrower index round trip, which needs the text to
        // run one way because two indices there can share a point.
        if (shaped.Runs.Select(run => run.Item.IsRightToLeft).Distinct().Count() > 1) {
            Assert.True(text is "abcلسان", $"'{text}' is mixed-direction and is not the case that covers it");
            return;
        }

        // The one property worth more than any expectation about where a caret lands: it holds for
        // scripts nobody thought to write a case for. Both affinities, because inside a run they
        // must agree — an index strictly inside a ligature has one place, not two.
        foreach (var boundary in Boundaries(text)) {
            Assert.Equal(boundary, shaped.CaretIndexAt(shaped.CaretOffset(boundary)));
            Assert.Equal(boundary, shaped.CaretPositionAt(shaped.CaretOffset(boundary, CaretAffinity.Upstream)).Index);
        }
    }

    [Theory]
    [MemberData(nameof(Strings))]
    public void A_caret_is_drawn_where_the_click_that_found_it_was(string fontName, string text) {
        var shaped = TextShaper.Shape(TestFonts.Load(fontName), text);

        // ⚠ **This is the property affinity exists for, and it holds at a direction boundary where
        // the index round trip above cannot.** Hit-testing a caret's own offset must give back a
        // caret drawn at the same x — not necessarily the same index, because a point can genuinely
        // name two positions, but never a caret somewhere else on the line. Without an affinity it
        // is false: `CaretOffset(7)` on `abcلسان` is the left edge of the Arabic run, `CaretIndexAt`
        // of that answers 3, and `CaretOffset(3)` is the *right* edge — most of a line away. On
        // screen that is a caret that jumps when the user clicks exactly where it already is.
        foreach (var boundary in Boundaries(text).Append(text.Length)) {
            foreach (var affinity in new[] { CaretAffinity.Downstream, CaretAffinity.Upstream }) {
                var drawn = shaped.CaretOffset(boundary, affinity);
                var landed = shaped.CaretPositionAt(drawn);

                Assert.Equal(drawn, shaped.CaretOffset(landed.Index, landed.Affinity), 3);
            }
        }
    }

    [Fact]
    public void An_affinity_tells_the_two_places_one_index_can_be_apart() {
        const string mixed = "abcلسان";
        var shaped = TextShaper.Shape(TestFonts.Load(TestFonts.Arabic), mixed);
        var junction = shaped.Runs.First(run => run.Item.Script == Script.Latin).Advance;

        // Index 3 is "after the c" and "before the first Arabic letter" at once, and those are at
        // opposite ends of the Arabic run. The affinity is the whole difference between them, and
        // it is the only argument that changes: same index, same text, same shaping.
        Assert.Equal(junction, shaped.CaretOffset(3, CaretAffinity.Upstream), 3);
        Assert.Equal(shaped.Advance, shaped.CaretOffset(3, CaretAffinity.Downstream), 3);

        // And each place hit-tests back to itself — index *and* affinity. That pair is what the
        // one-argument form could not return, because it has one answer for a point with two.
        Assert.Equal((3, CaretAffinity.Upstream), shaped.CaretPositionAt(junction));
        Assert.Equal((3, CaretAffinity.Downstream), shaped.CaretPositionAt(shaped.Advance));
    }

    [Fact]
    public void Two_indices_at_one_point_stay_ambiguous_because_no_bit_could_separate_them() {
        const string mixed = "abcلسان";
        var shaped = TextShaper.Shape(TestFonts.Load(TestFonts.Arabic), mixed);
        var junction = shaped.Runs.First(run => run.Item.Script == Script.Latin).Advance;

        // ⚠ **The honest limit, asserted rather than left as prose.** Affinity separates one index's
        // two places; it does not separate two indices sharing one place. The caret after the `c`
        // and the caret at the end of the text are the same x, and a hit test there must answer with
        // one of them — drawing order breaks the tie. An editor arriving at the end of the text by
        // typing has to *carry* that position, and re-deriving it from a click will lose it.
        Assert.Equal(junction, shaped.CaretOffset(mixed.Length, CaretAffinity.Upstream), 3);
        Assert.Equal(junction, shaped.CaretOffset(3, CaretAffinity.Upstream), 3);
        Assert.Equal(3, shaped.CaretPositionAt(junction).Index);
    }

    [Fact]
    public void One_caret_index_at_a_direction_boundary_has_two_places_it_could_be() {
        const string mixed = "abcلسان";
        var shaped = TextShaper.Shape(TestFonts.Load(TestFonts.Arabic), mixed);
        var junction = shaped.Runs.First(run => run.Item.Script == Script.Latin).Advance;

        // Index 3 is both "after the c" and "before the first Arabic letter", and those are at
        // opposite ends of the Arabic run. The one-argument overloads answer with the logical one —
        // the leading edge of the character the index names — so a caret index alone still cannot
        // say which the user meant. That is now a stated default rather than the only answer
        // available: the affinity overloads beside them return both, and this test pins what the
        // narrow form keeps doing for every caller that has not been moved over.
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
