// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Frozen;
using Xunit;

namespace Vixen.Ui.Layout.Tests.Taffy;

/// <summary>
///     Vixen's float layout against Taffy's Chrome-derived corpus.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This file exists because the corpus stopped being pending, and that is the third time
///         the same thing has happened.</b> <c>TaffyPendingCorporaTests</c> ran all 84 of these
///         fixtures and pinned the tally at <c>(0, 0, 84)</c> for as long as there was no
///         <c>float</c> field to refuse them on anything else; block and grid each left it the same
///         way, for a conformance suite that judges every fixture individually. Nothing about the
///         harness changed on any of the three occasions, which was the claim
///         <c>TaffyPendingCorporaTests</c> was written to make and has now finished making.
///     </para>
///     <para>
///         <b>All 84 pass, none is refused, and <c>FloatKnownGaps.txt</c> is empty — which is a
///         weaker result than it sounds and that file says why.</b> Every one of the 84 was an ENGINE
///         gap: the store had no field for <c>float</c>, so none of them had ever run, and the
///         algorithm was written against these exact numbers. A corpus that had never run and then
///         went green is evidence that somebody read the expectations. The 8
///         <c>block_flow_root_*_float</c> fixtures in <see cref="TaffyBlockConformanceTests" /> are
///         the only part of this that was checked against a corpus written for something else.
///     </para>
///     <para>
///         ⚠ <b>And this corpus does not test the thing floats are famous for.</b> There is not a
///         single <c>&lt;text&gt;</c> element in <c>Corpus/float.xml</c>, so no fixture here puts
///         inline content beside a float and none of them exercises §9.5's line-box shortening.
///         What the 84 do cover is §9.5.1 placement, §9.5's non-overlap rule for formatting context
///         roots, §9.5.2 clearance, and §10.6.3's contains-its-floats height. The inline half is
///         owed and is filed in <c>InlineKnownGaps.txt</c>.
///     </para>
/// </remarks>
public class TaffyFloatConformanceTests {
    const int ExpectedPassing = 84;
    const int ExpectedFailing = 0;

    // ⚠ Zero, and it has to be asserted rather than left to the pass count. `float` and `clear` are
    // still refused BY NAME for a value the map does not know — `float: inline-start` would land
    // there — so a refreshed corpus could put fixtures back in this bucket without the total moving
    // if a matching number of passes appeared elsewhere. See UnsupportedFixtures.txt.
    const int ExpectedUnsupported = 0;

    static readonly FrozenSet<string> KnownGaps = LoadKnownGaps();

    public static TheoryData<string> Fixtures {
        get {
            var data = new TheoryData<string>();
            foreach (var fixture in TaffyCorpus.Load("float")) {
                data.Add(fixture.Name);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture(string name) {
        var fixture = TaffyCorpus.Load("float").Single(fixture => fixture.Name == name);
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
        var tally = TaffyCensus.Run("float", 0).Tally;

        Assert.Equal(
            (ExpectedPassing, ExpectedFailing, ExpectedUnsupported),
            (tally.Passed, tally.Failed, tally.Unsupported)
        );
    }

    /// <summary>
    ///     Clearance specifically, counted apart from placement.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A total of 84 says nothing about which half of §9.5 is implemented, and the two
    ///     halves have almost nothing in common.</b> Placing a float is a search over an exclusion
    ///     list; clearance is margin collapsing with a floor under it, and it is where every one of
    ///     the four bugs this branch hit actually lived — a probe that left its origin behind, a
    ///     margin that both escaped and positioned, a set that was discarded when it should have been
    ///     spent, and a self-collapsing box whose margin clearance cuts in two rather than shortens.
    ///     So the <c>float_clear_*</c> and <c>*_clearance_*</c> families are counted on their own,
    ///     because "floats are placed" and "clear works" are separate claims and only the second one
    ///     was hard.
    /// </remarks>
    [Fact]
    public void Every_clearance_fixture_passes() {
        var clearance = TaffyCorpus
            .Load("float")
            .Where(fixture => fixture.Name.Contains("clear", StringComparison.Ordinal))
            .ToList();

        var outcomes = clearance.Select(fixture => (fixture.Name, Result: TaffyFixtureRunner.Run(fixture))).ToList();

        Assert.Equal(24, clearance.Count);
        Assert.Empty(
            outcomes
                .Where(entry => entry.Result.Outcome != TaffyOutcome.Pass)
                .Select(entry => $"{entry.Name}\n{entry.Result.Detail}")
        );
    }

    /// <summary>Strips the border-box/content-box and ltr/rtl suffix Taffy appends to every fixture.</summary>
    static string BaseName(string name) {
        var separator = name.IndexOf("__", StringComparison.Ordinal);

        return separator < 0 ? name : name[..separator];
    }

    static FrozenSet<string> LoadKnownGaps() {
        var path = Path.Combine(AppContext.BaseDirectory, "Taffy", "FloatKnownGaps.txt");

        return File
            .ReadLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToFrozenSet(StringComparer.Ordinal);
    }
}
