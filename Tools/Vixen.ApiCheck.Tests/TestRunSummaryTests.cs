// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Build;
using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>
///     ⚠ That a test run with no summary line for an assembly is red, and so is a non-zero exit
///     (#1360).
/// </summary>
/// <remarks>
///     The lines below are copied from real runs on this tree, 2026-10-22: the platform runner's
///     from an unfiltered <c>dotnet test Tools/Vixen.ApiCheck.Tests</c>, VSTest's from the same
///     project with a <c>--filter</c>, which <c>Directory.Build.props</c> sends back to VSTest.
/// </remarks>
public sealed class TestRunSummaryTests {
    const string Platform =
        "  Passed! - Failed: 0, Passed: 272, Skipped: 2, Total: 274, Duration: 33s 767ms - Vixen.ApiCheck.Tests.dll (net10.0|x64)";

    const string VsTest =
        "Passed!  - Failed:     0, Passed:    28, Skipped:     0, Total:    28, Duration: 1 s - Vixen.ApiCheck.Tests.dll (net10.0)";

    const string Red =
        "Failed!  - Failed:     1, Passed:    43, Skipped:     0, Total:    44, Duration: 2 s - Vixen.Fuzz.Tests.dll (net10.0)";

    [Fact]
    public void Both_runners_lines_are_read() {
        var summaries = TestRunSummary.Parse([Platform, VsTest, Red, "Build succeeded.", "  Tests succeeded: 'x.dll' [net10.0|x64]"]);

        Assert.Equal(
            [
                new TestRunSummary.Summary("Vixen.ApiCheck.Tests", true, 0, 274),
                new TestRunSummary.Summary("Vixen.ApiCheck.Tests", true, 0, 28),
                new TestRunSummary.Summary("Vixen.Fuzz.Tests", false, 1, 44)
            ],
            summaries
        );
    }

    [Fact]
    public void A_coloured_line_is_still_a_line() {
        var summary = Assert.Single(TestRunSummary.Parse(["\u001b[32;1m" + Platform + "\u001b[0m"]));

        Assert.Equal(274, summary.Total);
    }

    /// <summary>The control: a green run is green, so the cases below are not red for everything.</summary>
    [Fact]
    public void A_green_summary_with_exit_zero_and_a_matching_trx_has_no_problem() {
        Assert.Empty(
            TestRunSummary.Problems(
                ["Vixen.ApiCheck.Tests"],
                [Platform],
                exitCode: 0,
                new Dictionary<string, int> { ["Vixen.ApiCheck.Tests"] = 274 }
            )
        );
    }

    /// <summary>
    ///     ⚠ The issue's exact shape: a complete TRX, exit zero, and no summary line. Every grep for
    ///     <c>Failed:</c> calls this clean.
    /// </summary>
    [Fact]
    public void A_missing_summary_is_red_even_with_a_complete_trx_and_exit_zero() {
        var problem = Assert.Single(
            TestRunSummary.Problems(
                ["Vixen.Editor.Texturing.Tests"],
                ["  Run tests: 'Vixen.Editor.Texturing.Tests.dll' [net10.0|x64]"],
                exitCode: 0,
                new Dictionary<string, int> { ["Vixen.Editor.Texturing.Tests"] = 657 }
            )
        );

        Assert.Contains("printed no summary line", problem, StringComparison.Ordinal);
        Assert.Contains("657", problem, StringComparison.Ordinal);
    }

    /// <summary>A summary for one assembly does not stand in for another's.</summary>
    [Fact]
    public void Each_expected_assembly_needs_its_own_summary() {
        var problem = Assert.Single(TestRunSummary.Problems(["Vixen.ApiCheck.Tests", "Vixen.Ecs.Tests"], [Platform], exitCode: 0));

        Assert.StartsWith("Vixen.Ecs.Tests printed no summary line", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_zero_exit_behind_green_summaries_is_red() {
        var problem = Assert.Single(TestRunSummary.Problems(["Vixen.ApiCheck.Tests"], [Platform], exitCode: 3));

        Assert.Contains("exited 3", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_summary_is_red() {
        var problem = Assert.Single(TestRunSummary.Problems(["Vixen.Fuzz.Tests"], [Red], exitCode: 1));

        Assert.Equal("Vixen.Fuzz.Tests: 1 of 44 failed.", problem);
    }

    /// <summary>A summary and a TRX that disagree describe a run that did not finish.</summary>
    [Fact]
    public void A_summary_and_a_trx_that_disagree_are_red() {
        var problem = Assert.Single(
            TestRunSummary.Problems(
                ["Vixen.ApiCheck.Tests"],
                [Platform],
                exitCode: 0,
                new Dictionary<string, int> { ["Vixen.ApiCheck.Tests"] = 270 }
            )
        );

        Assert.Contains("Total 274", problem, StringComparison.Ordinal);
        Assert.Contains("records 270", problem, StringComparison.Ordinal);
    }
}
