// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Frozen;
using Xunit;

namespace Vixen.Ui.Layout.Tests.Taffy;

/// <summary>
///     Vixen's block layout against Taffy's Chrome-derived corpus.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Nothing here is new test infrastructure, and that was the point of B0.</b> These 912
///         fixtures were committed, loaded and executed by <c>TaffyPendingCorporaTests</c> before a
///         single line of block layout existed — refused, all of them, at exactly one point: the
///         <c>display</c> keyword. The day <see cref="Display" /> grew a <c>Block</c> member they
///         started answering. This file is the same harness pointed at them with a per-fixture
///         assertion instead of a tally, which is the shape <c>TaffyFlexConformanceTests</c> already
///         had.
///     </para>
///     <para>
///         <b>764 of the 884 <c>block</c> fixtures pass and 24 of the 28 <c>blockflex</c>.</b> 120
///         and 4 are refused for a property this store has no field for, and <b>none disagree</b>.
///         The 20 that used to were all inside <c>LayoutTree.Absolute.cs</c>, in one
///         <c>aspect-ratio</c> bucket that predated block layout and that a flex parent hit
///         identically; they closed with the flex and grid copies of the same rule. Not one was ever
///         in block formatting itself, and the committed failure count is now zero.
///     </para>
///     <para>
///         ⚠ <b>The thing this corpus is here to judge is margin collapsing</b>, because that is the
///         difference between block layout and a flex column with <c>align-items: stretch</c>, and
///         approximating it would have passed a great many of these fixtures while being wrong about
///         the ones anybody would notice. 264 of the 884 are <c>margin_y_*</c>; 216 pass and the
///         other 48 are refused for <c>scrollbar-width</c>. None fails.
///     </para>
/// </remarks>
public class TaffyBlockConformanceTests {
    /// <summary>The categories the block algorithm is expected to answer.</summary>
    /// <remarks>
    ///     <c>blockflex</c> is a block container with flex descendants or the reverse, so it judges
    ///     the seam between the store's two algorithms rather than either one — which is the part a
    ///     second algorithm is most likely to get wrong. <c>blockgrid</c> waits for B2.
    /// </remarks>
    static readonly string[] Categories = ["block", "blockflex"];

    // ⚠ Committed counts, not lower bounds — see the identical comment in the flex suite. A gap that
    // closes has to be taken off BlockKnownGaps.txt in the same commit as it closes.
    //
    // ⚠ 124 → 96 unsupported without a line of block layout changing, and all 28 of them became
    // passes. They were `scrollbar-width` on `overflow: hidden` boxes, which reserves no gutter and
    // so could never have moved a box — `TaffyStyleMap` was refusing an inert declaration and taking
    // every other property the fixture set down with it. See UnsupportedFixtures.txt.
    const int ExpectedPassing = 816;
    const int ExpectedFailing = 0;
    const int ExpectedUnsupported = 96;

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

    /// <summary>
    ///     Margin collapsing specifically, counted apart from everything else.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A total of 768 says nothing about which rules are implemented, and the one rule this
    ///     algorithm could most plausibly have skipped is the one it is named for.</b> A block layout
    ///     that stacked boxes and stretched them and did no collapsing at all would still pass most
    ///     of this corpus — the majority of these fixtures set no vertical margin. So the
    ///     <c>margin_y_*</c> families are counted on their own, and the count is pinned at all of
    ///     them, because "block is done" and "collapsing is done" are separate claims and only the
    ///     second one is hard.
    /// </remarks>
    [Fact]
    public void Every_margin_collapsing_fixture_passes() {
        var collapsing = TaffyCorpus
            .Load("block")
            .Where(fixture => fixture.Name.StartsWith("block_margin_y_", StringComparison.Ordinal))
            .ToList();

        var outcomes = collapsing.Select(fixture => (fixture.Name, Result: TaffyFixtureRunner.Run(fixture))).ToList();

        var failures = outcomes
            .Where(entry => entry.Result.Outcome == TaffyOutcome.Fail)
            .Select(entry => $"{entry.Name}\n{entry.Result.Detail}")
            .ToList();

        Assert.Equal(264, collapsing.Count);
        Assert.Empty(failures);

        // ⚠ <b>THIS COUNT WAS 48 AND IT WAS HALF A BRIDGE PROBLEM, WHICH IS THE BEST DEMONSTRATION
        // IN THE CORPUS OF WHY A REFUSAL NEEDS A REASON AND NOT A NUMBER.</b> All 48 of the
        // `*_blocked_by_overflow_*` fixtures were refused for `scrollbar-width`, and the comment
        // here said what everyone would say: this store reserves no gutter, so it cannot reproduce
        // the geometry Chrome recorded. That was true of exactly half of them. The 24
        // `_overflow_{x,y}_hidden` variants clip WITHOUT a scrollbar — there is no gutter to
        // reserve, the declaration is inert, and Chrome's geometry was reproducible all along. They
        // pass. The 24 that say `scroll` are the real engine gap and are still refused. See
        // UnsupportedFixtures.txt.
        //
        // The *rule* remains covered from a second direction regardless: `Overflow` other than
        // `visible` blocks a collapse, which `MarginCollapsingTests` asserts directly.
        Assert.Equal(24, outcomes.Count(entry => entry.Result.Outcome == TaffyOutcome.Unsupported));
        Assert.Equal(240, outcomes.Count(entry => entry.Result.Outcome == TaffyOutcome.Pass));
    }

    /// <summary>Strips the border-box/content-box and ltr/rtl suffix Taffy appends to every fixture.</summary>
    static string BaseName(string name) {
        var separator = name.IndexOf("__", StringComparison.Ordinal);
        return separator < 0 ? name : name[..separator];
    }

    static FrozenSet<string> LoadKnownGaps() {
        var path = Path.Combine(AppContext.BaseDirectory, "Taffy", "BlockKnownGaps.txt");

        return File
            .ReadLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToFrozenSet(StringComparer.Ordinal);
    }
}
