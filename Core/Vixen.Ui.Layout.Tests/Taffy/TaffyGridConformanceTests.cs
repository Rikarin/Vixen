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
    const int ExpectedPassing = 2078;
    const int ExpectedFailing = 42;
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
            var tally = TaffyCensus.Run(category, 0).Tally;
            passing += tally.Passed;
            failing += tally.Failed;
            unsupported += tally.Unsupported;
        }

        Assert.Equal((ExpectedPassing, ExpectedFailing, ExpectedUnsupported), (passing, failing, unsupported));
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
