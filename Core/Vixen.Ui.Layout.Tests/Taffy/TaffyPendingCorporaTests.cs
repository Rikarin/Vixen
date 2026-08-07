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
///         ⚠ <b>What the tallies say right now is worth reading as a result in itself: every single
///         block, float and grid fixture is refused at exactly one point</b> — the <c>display</c>
///         keyword. Not one of them is failing on arithmetic, because not one of them reaches any.
///         B1 is, from this suite's point of view, the day <see cref="Display" /> grows a
///         <c>Block</c> member, and B2 the day it grows <c>Grid</c>.
///     </para>
/// </remarks>
public class TaffyPendingCorporaTests {
    /// <summary>
    ///     Each pending corpus and the tally it stands at.
    /// </summary>
    /// <remarks>
    ///     The eight passing grid fixtures are not grid working. They are fixtures whose root happens
    ///     to be a default flex container with grid only further down, laid out to the same numbers
    ///     by luck of the geometry. They are counted honestly rather than excluded, because the point
    ///     of a baseline is that it moves for a reason.
    /// </remarks>
    public static TheoryData<string, int, int, int> Corpora =>
        new() {
            // category      passed failed unsupported
            { "block", 0, 0, 884 },
            { "blockflex", 0, 0, 28 },
            { "blockgrid", 0, 0, 56 },
            { "float", 0, 0, 84 },
            { "grid", 8, 0, 2032 },
            { "gridflex", 0, 0, 24 }
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
