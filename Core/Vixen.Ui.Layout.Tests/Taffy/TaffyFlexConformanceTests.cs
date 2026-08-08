// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Frozen;
using Xunit;

namespace Vixen.Ui.Layout.Tests.Taffy;

/// <summary>
///     Vixen's flexbox against Taffy's Chrome-derived corpus.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This suite exists to be a second opinion, and the reason it was built first is that
///         flexbox already has a first one.</b> <c>Generated/</c> holds 534 fixtures translated from
///         Yoga, and they are green. If a second browser-derived corpus ten times the size mostly
///         agreed, the harness could be trusted for block and grid, which have no oracle at all; if
///         it mostly disagreed, the harness would be wrong and there would be no way to tell that
///         apart from a real grid bug later. It mostly agrees — <b>2 002 of the 2 208 runnable flex
///         fixtures pass on a store built entirely against a different corpus</b> — which is the
///         result B1 and B2 needed before either could start.
///     </para>
///     <para>
///         The 206 that do not are in <c>KnownGaps.txt</c> with a diagnosis each, and they are real:
///         thirteen fixtures' worth of translation bugs were found and fixed on the way here rather
///         than written down. The largest bucket is the CSS Flexbox §4.5 automatic minimum size for
///         items that are containers, which is precisely the hole the layout README predicted the
///         Yoga corpus was leaving.
///     </para>
/// </remarks>
public class TaffyFlexConformanceTests {
    /// <summary>The categories whose fixtures the flex algorithm is expected to answer.</summary>
    /// <remarks>
    ///     <c>leaf</c> is included because a leaf's intrinsic size is the flex algorithm's problem —
    ///     it is separated in Taffy's tree only because those fixtures have no container at all.
    /// </remarks>
    static readonly string[] Categories = ["flex", "leaf"];

    // ⚠ Committed counts, not lower bounds. A gap that gets fixed has to be taken off the list in
    // the same commit, and a fixture that starts failing cannot hide inside a listed family.
    // ⚠ Eight of these moved from "unsupported" to "passing" when `display: block` landed, and
    // nothing moved into or out of "failing". Those eight are flex fixtures with a *block* box
    // somewhere in the tree — the corpus was refusing whole trees for one descendant's keyword — so
    // the flex algorithm is unchanged and eight more of its fixtures now judge it. 2 074 → 2 082.
    //
    // ⚠ Sixteen more moved the same way when `display: grid` landed, for the same reason and with
    // the same non-effect on "failing": 2 082 → 2 098, unsupported 168 → 152. They are the four
    // `aspect_ratio_flex_{row,column}_fill_width_flex`, `bevy_issue_10343_grid` and
    // `bevy_issue_21240` families, each of which is a flex tree with one grid box in it.
    //
    // ⚠ THE CONSEQUENCE IS THAT THIS NUMBER IS NO LONGER PURELY FLEX'S, and it is the reason to
    // re-run this census alongside the grid one rather than only when flex changes. Those sixteen
    // trees now execute LayoutTree.Grid, so a grid regression can turn this suite red — which is
    // correct, because a fixture that stops agreeing with Chrome should be loud wherever it lives,
    // but it will read as a flex failure to anyone who does not look at the names.
    const int ExpectedPassing = 2098;
    const int ExpectedFailing = 158;
    const int ExpectedUnsupported = 152;

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
                // ⚠ Deliberately not asserting that a pass is absent from KnownGaps.txt. A line there
                // names a fixture without its border-box/content-box and ltr/rtl suffix, and three of
                // the listed gaps really do fail in only two of their four variants — box-sizing
                // changes whether the bug is reachable. Catching a *fixed* gap is
                // The_corpus_stands_where_it_is_recorded_as_standing's job, and it does it exactly:
                // the failure count is pinned, so a gap that closes turns the suite red regardless of
                // which variant closed it.
                break;

            case TaffyOutcome.Unsupported:
                // Not a failure and not a pass: the store has no field for what the fixture sets, so
                // there is nothing to be right or wrong about. Recorded so the reason stays visible.
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

    /// <summary>
    ///     The three totals, asserted together.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Per-fixture assertions alone would let the suite rot in two directions.</b> A family
    ///     listed in <c>KnownGaps.txt</c> covers up to four fixtures by name prefix, so a fifth
    ///     variant starting to fail would be absorbed silently; and a gap that gets fixed would just
    ///     go quietly green. Pinning the counts closes both, and makes the number in the README
    ///     something the build maintains rather than something someone remembered to update.
    /// </remarks>
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
        var path = Path.Combine(AppContext.BaseDirectory, "Taffy", "KnownGaps.txt");

        return File
            .ReadLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToFrozenSet(StringComparer.Ordinal);
    }
}
