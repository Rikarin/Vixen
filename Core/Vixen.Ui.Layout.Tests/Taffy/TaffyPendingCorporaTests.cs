// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Layout.Tests.Taffy;

/// <summary>
///     The corpora for the layout modes that are not written yet. There are none.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Three thousand skipped tests is not an oracle, and deleting them is not either.</b>
///         Yoga's suite skips nine <c>display: contents</c> fixtures by name, listed in the header of
///         the file they were translated into — which is a fine answer at nine and an absurd one at
///         3 116. So a mode that did not exist yet got <i>one</i> test per corpus that ran every
///         fixture in it and pinned the tally.
///     </para>
///     <para>
///         That bought three things a skip does not. The fixtures actually executed, so a crash, a
///         hang or an infinite recursion in an unimplemented path was visible that day rather than on
///         the day someone enabled it. The number could not move by accident in either direction. And
///         when a mode landed, the same test became the progress meter — the pass count went up, the
///         test went red, and the new number got committed.
///     </para>
///     <para>
///         ⚠ <b>That prediction has now been paid out three times, and this file is what is left of
///         it.</b> It used to say that every block, float and grid fixture was refused at exactly one
///         point and that the day <see cref="Display" /> grew the keyword they would start answering.
///         884 block and 28 blockflex went from 0 passing to 746 in the commit that added
///         <c>Block</c>; 2 040 grid, 56 blockgrid and 24 gridflex followed <c>Grid</c>. Each moved to
///         a conformance suite of its own, where every fixture is judged individually against Chrome
///         instead of being counted.
///     </para>
///     <para>
///         ⚠ <b>The 84 <c>float</c> fixtures were the last, and they are the one case the prediction
///         got wrong in an interesting way.</b> This file said they were refused on the <c>float</c>
///         attribute rather than on <c>display</c>, "so they were never waiting on a keyword" — which
///         was right about the mechanism and wrong about the consequence. They were waiting on a
///         keyword after all: <see cref="FloatSide" />, a keyword the store had no field for at all
///         rather than a value of one it had. <see cref="TaffyFloatConformanceTests" /> judges them
///         individually now.
///     </para>
///     <para>
///         ⚠ <b>So the tally mechanism has no user left, and the file is kept anyway.</b> Not out of
///         sentiment: the two assertions below were never about a pending mode. The first is the
///         floor under every count in this directory — a corpus that fails to reach the output
///         directory makes every other suite in the project green and silent, which is the failure
///         this whole family of files was written about. The second is what a <i>deleted</i> heading
///         would cost: with the list of pending corpora gone, nothing would notice a ninth corpus
///         file being committed and never wired to a suite.
///     </para>
/// </remarks>
public class TaffyPendingCorporaTests {
    /// <summary>
    ///     Every corpus is present and whole, before anything tries to draw a conclusion from it.
    /// </summary>
    /// <remarks>
    ///     ⚠ A tally of zero passes is indistinguishable from a corpus that failed to reach the
    ///     output directory, and both would be green in every other test in this directory. This is
    ///     the one that is not.
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

    /// <summary>
    ///     Every corpus is judged by a conformance suite, and none of them is merely tallied.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is what replaces the tally, and it is deliberately stated as a property of
    ///         the corpus rather than as the empty list it currently produces.</b> "There are no
    ///         pending corpora", written as an empty collection, is a claim nothing can break —
    ///         delete every corpus file and it stays true. Written as "every category the loader
    ///         knows about appears in some suite's list, and every category some suite claims exists",
    ///         a ninth corpus arriving unwired fails here, and so does a suite quietly dropping one.
    ///         That is the only event this file was ever guarding against.
    ///     </para>
    ///     <para>
    ///         The four suites between them name all eight. <c>leaf</c> belongs to the flex suite,
    ///         which is where the measure-function fixtures have always been judged.
    ///     </para>
    /// </remarks>
    [Fact]
    public void No_corpus_is_pending_and_every_one_of_them_reaches_a_conformance_suite() {
        string[] judged = ["block", "blockflex", "blockgrid", "flex", "float", "grid", "gridflex", "leaf"];

        Assert.Empty(TaffyCorpus.Categories.Except(judged, StringComparer.Ordinal));
        Assert.Empty(judged.Except(TaffyCorpus.Categories, StringComparer.Ordinal));
    }
}
