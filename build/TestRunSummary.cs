// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Vixen.Build;

/// <summary>
///     What a test run said about each assembly it ran, read from its console, and what is wrong
///     with that: the post-condition <c>Test</c> and <c>AffectedTests</c> hold every run to.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The defect this exists for is a line that is not there</b> (#1360).
///         <c>Vixen.Editor.Texturing.Tests</c> once finished with no summary line at all while its
///         TRX recorded 657 of 657 passed: the host died after the last test and before the runner
///         printed <c>Passed! - … Total: 657</c>. A reader that greps for <c>Failed:</c> finds
///         nothing and calls that clean, and the exit code of a host that never reached its own
///         exit is not one anybody can tell from a pass. So a missing summary is a failure here, as
///         is a non-zero exit, as is a summary whose total disagrees with the TRX.
///     </para>
///     <para>
///         Dependency-free and linked into <c>Vixen.ApiCheck.Tests</c>, for the reason every rule
///         beside it is: <c>build/_build.csproj</c> is outside the solution, and a rule that answers
///         only inside a Nuke run is a rule nobody has watched answer.
///     </para>
/// </remarks>
public static class TestRunSummary {
    /// <summary>One assembly's summary line.</summary>
    /// <param name="Assembly">The assembly name, without <c>.dll</c> — in this tree, the project name.</param>
    /// <param name="Green">Whether the line opened with <c>Passed!</c> rather than <c>Failed!</c>.</param>
    /// <param name="Failed">The failed count.</param>
    /// <param name="Total">The total, skipped cases included.</param>
    public sealed record Summary(string Assembly, bool Green, int Failed, int Total);

    /// <summary>
    ///     Both runners' summary line: Microsoft.Testing.Platform's
    ///     <c>Passed! - Failed: 0, Passed: 272, Skipped: 2, Total: 274, Duration: … - X.dll (net10.0|x64)</c>
    ///     and VSTest's column-aligned <c>Passed!  - Failed:     0, …</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ Both, because one invocation can be either: <c>Directory.Build.props</c> drops a run back
    ///     to VSTest the moment it carries a filter, a settings file or a blame switch.
    /// </remarks>
    static readonly Regex Line = new(
        @"(?<verdict>Passed|Failed)!\s+-\s+Failed:\s+(?<failed>\d+),\s+Passed:\s+\d+,\s+Skipped:\s+\d+,\s+Total:\s+(?<total>\d+),.*?-\s+(?<assembly>[^\s\\/]+?)\.dll\b",
        RegexOptions.CultureInvariant
    );

    static readonly Regex Escape = new(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.CultureInvariant);

    /// <summary>Every summary line in a run's console output, colour codes ignored.</summary>
    /// <param name="lines">The output, one line per element.</param>
    /// <returns>The summaries, in the order printed.</returns>
    public static IReadOnlyList<Summary> Parse(IEnumerable<string> lines) => [
        .. lines
            .Select(line => Line.Match(Escape.Replace(line, string.Empty)))
            .Where(match => match.Success)
            .Select(match => new Summary(
                    match.Groups["assembly"].Value,
                    match.Groups["verdict"].Value == "Passed",
                    int.Parse(match.Groups["failed"].Value, System.Globalization.CultureInfo.InvariantCulture),
                    int.Parse(match.Groups["total"].Value, System.Globalization.CultureInfo.InvariantCulture)
                )
            )
    ];

    /// <summary>Everything wrong with a run, one sentence each; empty for a run that is green.</summary>
    /// <param name="expected">The assemblies the run was asked to run.</param>
    /// <param name="lines">The run's console output.</param>
    /// <param name="exitCode">The runner's exit code.</param>
    /// <param name="trxTotals">
    ///     The <c>total</c> each assembly's TRX records, for those that wrote one, so that a summary
    ///     and a file that disagree are caught.
    /// </param>
    /// <returns>The problems, in a stable order.</returns>
    public static IReadOnlyList<string> Problems(
        IReadOnlyCollection<string> expected,
        IEnumerable<string> lines,
        int exitCode,
        IReadOnlyDictionary<string, int>? trxTotals = null
    ) {
        var summaries = Parse(lines)
            .GroupBy(summary => summary.Assembly, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

        var problems = new List<string>();

        foreach (var assembly in expected.OrderBy(name => name, StringComparer.Ordinal)) {
            if (!summaries.TryGetValue(assembly, out var summary)) {
                var recorded = trxTotals is not null && trxTotals.TryGetValue(assembly, out var total)
                    ? $" Its TRX records {total} case(s), so its host most likely died after the last of them"
                    : " It wrote no TRX either, so it did not build, did not start, or died early";

                problems.Add(
                    $"{assembly} printed no summary line.{recorded} — and a run with no summary is not a "
                    + "pass, whatever its exit code said (#1360)."
                );

                continue;
            }

            if (!summary.Green || summary.Failed > 0) {
                problems.Add($"{assembly}: {summary.Failed} of {summary.Total} failed.");
            }

            if (trxTotals is not null && trxTotals.TryGetValue(assembly, out var trx) && trx != summary.Total) {
                problems.Add(
                    $"{assembly}: the summary says Total {summary.Total} and its TRX records {trx}, so one "
                    + "of them describes a run that did not finish."
                );
            }
        }

        // ⚠ Last, and only when nothing above already explains it: a non-zero exit with every
        // summary green is the host failing outside any test, which is its own finding.
        if (exitCode != 0 && problems.Count == 0) {
            problems.Add(
                $"The runner exited {exitCode} although every expected assembly printed a green summary — "
                + "something failed outside any test."
            );
        }

        return problems;
    }
}
