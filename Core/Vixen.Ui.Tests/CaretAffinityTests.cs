// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>A caret index names a boundary, and at a direction change a boundary is two places.</summary>
/// <remarks>
///     <para>
///         <b>The layout stack asks the affinity question three times and this file covers the
///         middle one.</b> <c>TextLayout</c> asks which <i>line</i> — a wrap, covered in
///         <c>TextWrapTests</c>. <c>ShapedText</c> asks which <i>cluster</i>, covered by
///         <c>Vixen.Ui.Text.Tests.CaretTests</c> against a single shaped string. Between them
///         <c>TextLine</c> asks which <i>run</i>, and that needs something neither of the others
///         has: one line holding two runs that face opposite ways.
///     </para>
///     <para>
///         ⚠ <b>This file exists because a sabotage stayed green.</b> Disabling the run-boundary
///         branch in <see cref="TextLine.CaretOffset(int, CaretAffinity)" /> left all 966 tests in
///         this project passing, because every multi-run fixture in the suite is multi-run for
///         <i>font fallback</i> and both its runs face the same way — where the two affinities are
///         the same pixel and nothing can tell them apart. A green sabotage means the test is the
///         defect, so the fixture was built rather than the claim downgraded.
///     </para>
///     <para>
///         ⚠ <b>The two faces are disjoint on purpose and the coverage is asserted rather than
///         assumed.</b> <c>TestShapeLana</c> draws Latin and no Arabic, <c>TestShapeAran</c> draws
///         Arabic and no Latin, so a string mixing them has exactly one correct split — and because
///         the scripts also disagree about direction, that split is a level change as well as a face
///         change. A subsetted replacement would turn every test here green by removing the split.
///     </para>
/// </remarks>
public class CaretAffinityTests {
    const string Latin = "AB";

    /// <summary>ALEF then TEH. Two letters, two glyphs, and no joining between them.</summary>
    const string Arabic = "ات";

    const float Tolerance = 0.01f;

    static readonly FontFace Lana = LoadFont("TestShapeLana.ttf", "lana");
    static readonly FontFace Aran = LoadFont("TestShapeAran.ttf", "aran");

    static FontFace LoadFont(string resource, string name) {
        using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream($"Vixen.Ui.Tests.Fonts.{resource}")
            ?? throw new InvalidOperationException($"the test font '{resource}' is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: name);
    }

    static UiDocument Documented() {
        var document = new UiDocument(400f, 200f);

        document.Fonts.Register("Test", Lana);
        document.Fonts.AddFallback(Aran);
        document.Load("root { width: 400px; height: 200px; align-items: flex-start; } label { font-family: Test; }");

        return document;
    }

    static TextLine Lined(UiDocument document, string text) {
        var label = document.Root.Add("label");
        label.Text = text;
        document.Update();

        return label.Block()!.Lines[0];
    }

    [Fact]
    public void The_two_faces_have_the_disjoint_coverage_every_test_here_assumes() {
        Assert.True(Lana.Supports('A'));
        Assert.False(Lana.Supports('ا'));

        Assert.True(Aran.Supports('ا'));
        Assert.False(Aran.Supports('A'));
    }

    [Fact]
    public void The_fixture_really_is_one_line_of_two_runs_facing_opposite_ways() {
        using var document = Documented();
        var line = Lined(document, Latin + Arabic);

        // ⚠ Asserted before anything is measured, because every claim below is vacuous otherwise.
        // A fixture that produced one run, or two runs at the same level, would pass the caret
        // assertions by making both affinities the same answer — which is exactly the condition
        // under which the sabotage that prompted this file stayed green.
        Assert.Equal(2, line.Runs.Length);
        Assert.Equal([Lana, Aran], line.Runs.Select(run => run.Font));
        Assert.NotEqual(line.Runs[0].Level % 2, line.Runs[1].Level % 2);
    }

    [Fact]
    public void The_index_where_the_direction_changes_is_drawn_at_two_places_a_run_apart() {
        using var document = Documented();
        var line = Lined(document, Latin + Arabic);

        var upstream = line.CaretOffset(Latin.Length, CaretAffinity.Upstream);
        var downstream = line.CaretOffset(Latin.Length, CaretAffinity.Downstream);

        // ⚠ **The whole of the claim, and it is a distance rather than a pair of numbers.** The
        // upstream caret trails the `B`, so it sits where the Latin run ends. The downstream caret
        // leads the ALEF, which in a right-to-left run is drawn at that run's *far* end — so the two
        // are the width of the Arabic run apart, and a caller that could not say which it meant
        // would be wrong by that much every time it guessed.
        Assert.Equal(line.Runs[0].Width, upstream, Tolerance);
        Assert.Equal(line.Runs[0].Width + line.Runs[1].Width, downstream, Tolerance);
        Assert.True(downstream - upstream > 1f, "the two readings are a whole run apart, not a rounding");
    }

    [Fact]
    public void A_caret_is_drawn_where_the_click_that_found_it_was() {
        using var document = Documented();
        var line = Lined(document, Latin + Arabic);

        // The property `Vixen.Ui.Text` is judged by, re-asserted one layer up where the runs — and
        // therefore the pens — are `Vixen.Ui`'s own arithmetic rather than the shaper's. A caret
        // hit-tested from its own offset must be drawn at the same x; not necessarily at the same
        // index, because two indices can share a point, but never somewhere else on the line.
        for (var index = 0; index <= Latin.Length + Arabic.Length; index++) {
            foreach (var affinity in new[] { CaretAffinity.Downstream, CaretAffinity.Upstream }) {
                var drawn = line.CaretOffset(index, affinity);
                var landed = line.CaretPositionAt(drawn);

                Assert.Equal(drawn, line.CaretOffset(landed.Index, landed.Affinity), Tolerance);
            }
        }
    }

    /// <summary>The middle of the character between two indices, whichever way its run faces.</summary>
    /// <remarks>
    ///     ⚠ A midpoint rather than an edge, and taken from the two caret readings that bracket the
    ///     character rather than from a cluster: in a right-to-left run the leading edge is the
    ///     larger number, so anything that assumed an order would sample the neighbour instead.
    /// </remarks>
    static float Middle(TextLine line, int index) =>
        (line.CaretOffset(index, CaretAffinity.Downstream) + line.CaretOffset(index + 1, CaretAffinity.Upstream)) / 2f;

    static bool Covers(List<(float X, float Width)> ranges, float x) =>
        ranges.Any(range => x >= range.X - Tolerance && x <= range.X + range.Width + Tolerance);

    [Fact]
    public void A_span_inside_one_run_is_one_range_over_exactly_that_character() {
        using var document = Documented();
        var line = Lined(document, Latin + Arabic);

        var ranges = new List<(float X, float Width)>();
        line.VisualRanges(0, 1, ranges);

        var range = Assert.Single(ranges);

        Assert.Equal(line.CaretOffset(0, CaretAffinity.Downstream), range.X, Tolerance);
        Assert.Equal(line.CaretOffset(1, CaretAffinity.Upstream) - range.X, range.Width, Tolerance);
    }

    [Fact]
    public void A_span_crossing_the_direction_change_is_two_ranges_with_the_unselected_letter_between() {
        using var document = Documented();
        var line = Lined(document, Latin + Arabic);

        // The second Latin letter and the first Arabic one: adjacent in the text, and with the
        // second Arabic letter drawn between them.
        var ranges = new List<(float X, float Width)>();
        line.VisualRanges(1, 3, ranges);

        // ⚠ **The count is the claim, and the covered points are what make it a claim about the
        // right two ranges.** A bounding box over this span has the same extent as the correct
        // answer — it reaches from the second Latin letter to the far end of the Arabic run — so
        // anything measuring extent passes against the defect. What separates them is the letter in
        // the middle, which is inside that box and is not selected.
        Assert.Equal(2, ranges.Count);

        Assert.False(Covers(ranges, Middle(line, 0)), "the first Latin letter is outside the span");
        Assert.True(Covers(ranges, Middle(line, 1)), "the second Latin letter is inside it");
        Assert.True(Covers(ranges, Middle(line, 2)), "the first Arabic letter is inside it");
        Assert.False(Covers(ranges, Middle(line, 3)), "the second Arabic letter is not, and is drawn between the two");

        // And the two together cover only the two letters, which is the area oracle: one rectangle
        // over the same span would be wider by the whole of the letter it should not be painting.
        var covered = ranges.Sum(range => range.Width);
        var letters = MathF.Abs(line.CaretOffset(2, CaretAffinity.Upstream) - line.CaretOffset(1, CaretAffinity.Downstream))
            + MathF.Abs(line.CaretOffset(3, CaretAffinity.Upstream) - line.CaretOffset(2, CaretAffinity.Downstream));

        Assert.Equal(letters, covered, Tolerance);
        Assert.True(line.Runs[0].Width + line.Runs[1].Width - covered > 1f, "the fixture would be vacuous if it did");
    }

    [Fact]
    public void The_whole_line_is_one_range_because_touching_ranges_are_merged() {
        using var document = Documented();
        var line = Lined(document, Latin + Arabic);

        var ranges = new List<(float X, float Width)>();
        line.VisualRanges(0, Latin.Length + Arabic.Length, ranges);

        // ⚠ Two runs and one rectangle. Without the merge every font fallback would paint a seam,
        // and the count above would stop being an oracle — a caller could no longer tell "two runs"
        // from "two visually disjoint pieces", which is the only distinction that matters here.
        var range = Assert.Single(ranges);

        Assert.Equal(0f, range.X, Tolerance);
        Assert.Equal(line.Runs[0].Width + line.Runs[1].Width, range.Width, Tolerance);
    }

    [Fact]
    public void The_one_argument_overload_still_answers_the_run_that_ends_there() {
        using var document = Documented();
        var line = Lined(document, Latin + Arabic);

        // ⚠ Pinned because it is the compatibility claim the whole change rests on: every caller
        // that has not been moved over keeps the answer it had, and the affinity overloads are an
        // addition rather than a change of behaviour.
        Assert.Equal(
            line.CaretOffset(Latin.Length, CaretAffinity.Upstream),
            line.CaretOffset(Latin.Length),
            Tolerance
        );
    }
}
