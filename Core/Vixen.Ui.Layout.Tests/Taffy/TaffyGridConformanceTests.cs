// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Frozen;
using Xunit;

namespace Vixen.Ui.Layout.Tests.Taffy;

/// <summary>
///     Vixen's grid layout against Taffy's Chrome-derived corpus.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Nothing here is new test infrastructure either, and that is B0's prediction paying
///         out a second time.</b> These 2 120 fixtures were committed, loaded and executed by
///         <c>TaffyPendingCorporaTests</c> before a line of grid layout existed — refused, all but
///         eight of them, at exactly one point: the <c>display</c> keyword. The day
///         <see cref="Display" /> grew a <c>Grid</c> member they started answering. This file is the
///         same harness pointed at them with a per-fixture assertion instead of a tally, which is
///         the shape <c>TaffyFlexConformanceTests</c> and <c>TaffyBlockConformanceTests</c> already
///         had.
///     </para>
///     <para>
///         ⚠ <b>Grid is the first mode whose <i>translation</i> is a program rather than a lookup,
///         and that is the risk this suite carries that the other two did not.</b> Every other
///         property in <see cref="TaffyStyleMap" /> is a keyword or a number;
///         <c>grid-template-columns</c> is a nested grammar with two kinds of <c>repeat()</c>, and
///         a track list that parses into something plausible but wrong produces a numeric mismatch
///         indistinguishable from an algorithm bug. <see cref="TaffyTrackListParser" /> answers that
///         by refusing everything outside the grammar it states rather than guessing, and
///         <c>TaffyTrackListParserTests</c> judges it against hand-written expectations so that the
///         parser has an oracle that is not the algorithm.
///     </para>
///     <para>
///         ⚠ <b><c>gridflex</c> and <c>blockgrid</c> are the seams and they are counted with the
///         rest on purpose.</b> A grid container with a flex child, or a block container with a grid
///         child, exercises the handover between two sizing protocols — a grid item's
///         <c>min-content</c> contribution has to come back out of a flex container that was never
///         asked for one. That is the part a third algorithm is most likely to get wrong, and it is
///         80 fixtures rather than 2 040, so it would vanish inside a single total if it were not
///         named.
///     </para>
/// </remarks>
public class TaffyGridConformanceTests {
    /// <summary>The categories the grid algorithm is expected to answer.</summary>
    static readonly string[] Categories = ["grid", "blockgrid", "gridflex"];

    // ⚠ 76 → 0: 40 `scrollbar-width` and 36 `safe` alignment, all 76 passing. Grid needed two rules
    // for the gutter that the other algorithms did not: `RecordAbsoluteGridAreas` had to put it
    // inside the padding edge an `auto` grid line resolves to, and the RTL mirror in
    // `PlaceGridItemBoxes` had to have its origin clamped into a box narrower than its own scrollbar.
    // See the flex suite's note on why an engine gap converts differently from a harness one.
    const int ExpectedPassing = 2108;
    const int ExpectedFailing = 12;
    const int ExpectedUnsupported = 0;

    static readonly FrozenSet<string> KnownGaps = LoadKnownGaps();

    public static TheoryData<string, string> Fixtures {
        get {
            var data = new TheoryData<string, string>();
            foreach (var category in Categories) {
                foreach (var fixture in TaffyCorpus.Load(category)) {
                    data.Add(category, fixture.Name);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture(string category, string name) {
        var fixture = TaffyCorpus.Load(category).Single(fixture => fixture.Name == name);
        var result = TaffyFixtureRunner.Run(fixture);

        switch (result.Outcome) {
            case TaffyOutcome.Pass:
                break;

            case TaffyOutcome.Unsupported:
                Assert.Skip($"{result.Detail}");
                break;

            default:
                Assert.True(
                    KnownGaps.Contains(BaseName(name)),
                    $"'{name}' disagrees with Chrome and is not a known gap:\n{result.Detail}"
                );

                break;
        }
    }

    /// <summary>The three totals, asserted together.</summary>
    [Fact]
    public void The_corpus_stands_where_it_is_recorded_as_standing() {
        var passing = 0;
        var failing = 0;
        var unsupported = 0;

        foreach (var category in Categories) {
            var tally = TaffyCensus.TallyOf(category);
            passing += tally.Passed;
            failing += tally.Failed;
            unsupported += tally.Unsupported;
        }

        Assert.Equal((ExpectedPassing, ExpectedFailing, ExpectedUnsupported), (passing, failing, unsupported));
    }

    /// <summary>The gaps file's own summary line states those same counts.</summary>
    /// <remarks>
    ///     ⚠ <b>The sentence a reader meets first was the only number in this arrangement nothing
    ///     checked</b>, and <c>GridKnownGaps.txt</c>'s was two batches stale before anybody noticed.
    ///     See <see cref="TaffyGapsSummary" /> for why the line count is asserted before the digits
    ///     on it are: a sweep that finds no line agrees with a file the line was deleted out of.
    /// </remarks>
    [Fact]
    public void Summary_line_states_the_counts_the_suite_pins() {
        TaffyGapsSummary.Check("GridKnownGaps.txt", ExpectedPassing, ExpectedFailing, ExpectedUnsupported);
    }

    /// <summary>
    ///     The gap this file called cyclic for four generations is closed, and the two numbers that
    ///     were wrong are asserted rather than left to the corpus's pass/fail.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>It was never cyclic and it was never grid's.</b> The entry read "Chrome reads the
    ///         item's inline max-content contribution off the container's definite block size even
    ///         though the area's is not yet known", and four audits accepted that as a refusal.
    ///         Measured, the whole of it is CSS Sizing §4.1's <i>transferred size</i> going missing
    ///         in an intrinsic inline pass: a box with a preferred aspect ratio and a definite block
    ///         size has a definite inline size, and neither the flex path nor the block path carried
    ///         a container's own stated height into the pass that asks how wide it wants to be. The
    ///         same defect reproduces with no grid anywhere in the tree — see
    ///         <see cref="TransferredSizeTests" />, whose first case is a flex row holding a
    ///         <c>height: 40px</c> block holding a <c>height: 100%; aspect-ratio: 1</c> box and
    ///         reports 0 against Chrome's 40.
    ///     </para>
    ///     <para>
    ///         What grid owed on top of that is one number: §5.2.1's "not yet known" is a claim about
    ///         the AREA, and a grid with a definite content-box height and exactly one row that
    ///         <c>align-content</c> stretches knows the area's block size before the column pass runs.
    ///         <c>GridAxis.DefiniteCrossSpace</c> is that number and <c>MeasureGridItem</c> hands it
    ///         to the probe.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This test is kept rather than deleted because <see cref="Fixture" /> would not
    ///         notice the difference between these four passing and these four being listed as gaps.</b>
    ///         The predecessor asserted <see cref="TaffyOutcome.Fail" /> and the exact pair of
    ///         mismatches; this asserts the pass and the two boxes that used to carry them, so a
    ///         regression that puts either number back is named here rather than folded into a count.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("border_box_ltr")]
    [InlineData("border_box_rtl")]
    [InlineData("content_box_ltr")]
    [InlineData("content_box_rtl")]
    public void The_gap_that_was_called_cyclic_stands_where_Chrome_puts_it(string suffix) {
        var name = $"chrome_issue_325928327__{suffix}";
        var fixture = TaffyCorpus.Load("grid").Single(fixture => fixture.Name == name);
        var result = TaffyFixtureRunner.Run(fixture);

        Assert.Equal(TaffyOutcome.Pass, result.Outcome);
        Assert.Equal(string.Empty, result.Detail.Trim());
    }

    /// <summary>Strips the border-box/content-box and ltr/rtl suffix Taffy appends to every fixture.</summary>
    static string BaseName(string name) {
        var separator = name.IndexOf("__", StringComparison.Ordinal);
        return separator < 0 ? name : name[..separator];
    }

    static FrozenSet<string> LoadKnownGaps() {
        var path = Path.Combine(AppContext.BaseDirectory, "Taffy", "GridKnownGaps.txt");

        return File
            .ReadLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToFrozenSet(StringComparer.Ordinal);
    }
}
