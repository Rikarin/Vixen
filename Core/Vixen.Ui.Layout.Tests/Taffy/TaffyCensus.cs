// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace Vixen.Ui.Layout.Tests.Taffy;

/// <param name="Passed">Fixtures whose every box matched Chrome.</param>
/// <param name="Failed">Fixtures that laid out and disagreed.</param>
/// <param name="Unsupported">Fixtures asking for a mode or property the store has no field for.</param>
sealed record TaffyTally(int Passed, int Failed, int Unsupported) {
    public int Total => Passed + Failed + Unsupported;
}

/// <summary>Runs a whole category and counts the three outcomes.</summary>
/// <remarks>
///     ⚠ <b>This is how the corpora for unimplemented modes are represented, and the alternative was
///     worse in both directions.</b> Marking 2 040 grid fixtures <c>Skip</c> buries the suite in noise
///     and tells nobody anything; deleting them throws the oracle away. Yoga's suite skips its nine
///     <c>display: contents</c> fixtures by name at translation time, listing each in the generated
///     file's header — which is legible at nine and absurd at two thousand.
///
///     So an unimplemented mode is one test that runs every one of its fixtures and asserts the
///     <i>tally</i> against a committed baseline. That has three properties a skip does not: the
///     fixtures really execute, so a crash or a hang shows up today; the number can only be changed
///     deliberately, so grid silently regressing is impossible; and as B1 and B2 land the baseline
///     goes up, which makes the same test a progress meter rather than dead weight.
/// </remarks>
static class TaffyCensus {
    public static (TaffyTally Tally, string Report) Run(string category, int reportLimit = 25) {
        var fixtures = TaffyCorpus.Load(category);
        var passed = 0;
        var failed = 0;
        var unsupported = 0;

        var byFeature = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var report = new StringBuilder();
        var reported = 0;

        foreach (var fixture in fixtures) {
            var result = TaffyFixtureRunner.Run(fixture);

            switch (result.Outcome) {
                case TaffyOutcome.Pass:
                    passed++;
                    break;

                case TaffyOutcome.Unsupported:
                    unsupported++;
                    byFeature[result.Detail] = byFeature.GetValueOrDefault(result.Detail) + 1;
                    break;

                default:
                    failed++;
                    if (reported++ < reportLimit) {
                        report.AppendLine(CultureInfo.InvariantCulture, $"{fixture.Name}\n{result.Detail}");
                    }

                    break;
            }
        }

        var tally = new TaffyTally(passed, failed, unsupported);
        var header = new StringBuilder();
        header.AppendLine(
            CultureInfo.InvariantCulture,
            $"{category}: {tally.Passed} passed, {tally.Failed} failed, {tally.Unsupported} unsupported, of {tally.Total}"
        );

        foreach (var (feature, count) in byFeature.OrderByDescending(entry => entry.Value)) {
            header.AppendLine(CultureInfo.InvariantCulture, $"  unsupported {count,5}  {feature}");
        }

        return (tally, header + report.ToString());
    }
}
