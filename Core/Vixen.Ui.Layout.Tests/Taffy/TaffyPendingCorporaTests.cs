// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Layout.Tests.Taffy;

/// <summary>
///     The corpora for the layout modes that are not written yet.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Three thousand skipped tests is not an oracle, and deleting them is not either.</b>
///         Yoga's suite skips nine <c>display: contents</c> fixtures by name, listed in the header of
///         the file they were translated into — which is a fine answer at nine and an absurd one at
///         3 116. So a mode that does not exist yet gets <i>one</i> test per corpus that runs every
///         fixture in it and pins the tally.
///     </para>
///     <para>
///         That buys three things a skip does not. The fixtures actually execute, so a crash, a hang
///         or an infinite recursion in an unimplemented path is visible today rather than on the day
///         someone enables it. The number cannot move by accident in either direction. And when B1
///         and B2 land, these same tests become the progress meter — the pass count goes up, the
///         test goes red, and the new number gets committed.
///     </para>
///     <para>
///         ⚠ <b>That prediction has now been paid out twice, and this file is what is left of it.</b>
///         It used to say that every block, float and grid fixture was refused at exactly one point —
///         the <c>display</c> keyword — and that B1 and B2 would be the days <see cref="Display" />
///         grew a <c>Block</c> and then a <c>Grid</c> member. Both were. 884 block and 28 blockflex
///         fixtures went from 0 passing to 746 in the commit that added the first keyword and the
///         algorithm behind it; 2 040 grid, 56 blockgrid and 24 gridflex followed the second. Each
///         corpus moved to a conformance suite of its own — <c>TaffyBlockConformanceTests</c> and
///         <c>TaffyGridConformanceTests</c> — where every fixture is judged individually against
///         Chrome instead of being counted. Nothing about the harness changed either time, which was
///         the entire claim.
///     </para>
///     <para>
///         ⚠ <b><c>float</c>'s 84 did <i>not</i> come along either time, and the reason is worth
///         stating.</b> Floats are refused by <c>TaffyStyleMap</c> on the <c>float</c> attribute
///         rather than on <c>display</c>, so they were never waiting on a keyword — every one of them
///         names a block container that this store now lays out correctly right up to the point where
///         a float would narrow a line box. Which is also why one corpus is still here rather than
///         this file being deleted: the shape it defends is a mode whose fixtures nothing has
///         accepted yet, and there is exactly one left.
///     </para>
/// </remarks>
public class TaffyPendingCorporaTests {
    /// <summary>
    ///     Each pending corpus and the tally it stands at.
    /// </summary>
    /// <remarks>
    ///     ⚠ Grid used to sit here at 8 passing, and those eight were never grid working — they were
    ///     fixtures whose root happened to be a default flex container with grid only further down,
    ///     laid out to the same numbers by luck of the geometry. They were counted honestly rather
    ///     than excluded, because the point of a baseline is that it moves for a reason, and when the
    ///     algorithm landed the number it moved from was a real one.
    /// </remarks>
    public static TheoryData<string, int, int, int> Corpora =>
        new() {
            // category      passed failed unsupported
            { "float", 0, 0, 84 }
        };

    [Theory]
    [MemberData(nameof(Corpora))]
    public void Corpus_stands_at_its_baseline(string category, int passed, int failed, int unsupported) {
        var (tally, report) = TaffyCensus.Run(category, 10);

        Assert.True(
            (passed, failed, unsupported) == (tally.Passed, tally.Failed, tally.Unsupported),
            $"The '{category}' baseline moved. If a layout mode landed, commit the new numbers.\n{report}"
        );
    }

    /// <summary>
    ///     The corpus is present and whole, before anything tries to draw a conclusion from it.
    /// </summary>
    /// <remarks>
    ///     ⚠ A tally of zero passes is indistinguishable from a corpus that failed to reach the output
    ///     directory, and both would be green in every test above. This is the one that is not.
    /// </remarks>
    [Fact]
    public void Every_corpus_is_present_and_the_whole_census_is_5524_fixtures() {
        var counts = TaffyCorpus.Categories.ToDictionary(category => category, category => TaffyCorpus.Load(category).Count);

        Assert.Equal(
            new Dictionary<string, int> {
                ["block"] = 884,
                ["blockflex"] = 28,
                ["blockgrid"] = 56,
                ["flex"] = 2352,
                ["float"] = 84,
                ["grid"] = 2040,
                ["gridflex"] = 24,
                ["leaf"] = 56
            },
            counts
        );

        Assert.Equal(5524, counts.Values.Sum());
    }
}
