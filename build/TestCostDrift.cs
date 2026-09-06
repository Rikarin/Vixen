// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

/// <summary>
///     Whether <c>build/test-cost.txt</c> still describes the run that just happened.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The cost list drifted to 2× wrong on its largest entry and nothing noticed, because
///         all three of its existing guards are about a project <em>name</em>.</b> A name the
///         solution no longer has fails; a project with no line sorts first; the run asserts one TRX
///         per project. A line whose name is real and whose <em>number</em> is stale was by design
///         not an error — "nothing fails because a number here is wrong, the run is merely packed
///         worse" (#863).
///     </para>
///     <para>
///         ⚠ <b>That was wrong twice over, and the second cost is the one that bit.</b> The numbers
///         are also read as evidence: <c>Build.TestOrder.cs</c>, <c>Workers</c>'s remarks and doc 12
///         all concluded from a stale <c>655.0 Vixen.Editor.App.Tests</c> that "the run cannot be
///         shortened by scheduling at all". On the real 329.5 s that assembly measures, greedy LPT
///         gives 489.8 s at four workers and 329.5 s at six, so raising the cap is worth ~160 s of a
///         498 s run. <b>A stale cost list argues against the work that would fix the run</b>, and it
///         always argues in that direction, because an assembly only ever gets <em>faster</em>
///         between regenerations.
///     </para>
///     <para>
///         <b>The rule is seconds <em>and</em> ratio, because the data has two populations.</b> The
///         small assemblies are dominated by host start-up and are noisy in relative terms while
///         irrelevant in absolute ones — a 1.2 s assembly measuring 3 s is 2.5× and worth nothing to
///         a schedule. The large ones move slowly in ratio and enormously in seconds. Requiring both
///         thresholds keeps a finding to something that would actually pack the run differently:
///         the historical defect above is 325.5 s and 1.99×, and clears both.
///     </para>
///     <para>
///         ⚠ <b>Compiled into a test assembly as source, not copied.</b> <c>build/_build.csproj</c>
///         is outside <c>Vixen.slnx</c> and no suite in the tree tests it, so this file is
///         dependency-free for the same reason <see cref="AotProbeProjectFile" /> is: it is linked
///         into <c>Vixen.ApiCheck.Tests</c>, and the subject of those tests is therefore the source
///         the build runs rather than a second copy of it.
///     </para>
/// </remarks>
static class TestCostDrift {
    /// <summary>
    ///     How far apart in seconds a committed cost and a measured wall must be before either is
    ///     worth reporting.
    /// </summary>
    /// <remarks>
    ///     A minute, because that is roughly what one worker-quarter of it is worth in elapsed on
    ///     this tree, and because every assembly below the top ten finishes inside it. ⚠ It is a
    ///     decision and not a discovery: it is deliberately larger than the noise a loaded machine
    ///     puts on a mid-sized assembly, so that the check keeps its meaning on the day fifteen
    ///     agent worktrees are testing at once.
    /// </remarks>
    public const double MinimumSeconds = 60.0;

    /// <summary>
    ///     How far apart proportionally a committed cost and a measured wall must be before either
    ///     is worth reporting.
    /// </summary>
    /// <remarks>
    ///     ⚠ 1.5 and not 2, and the historical case is why: the defect that motivated all of this
    ///     was 655.0 against 329.5, which is 1.988× — a threshold of 2 would have missed by hand's
    ///     breadth the one measurement it was written for.
    /// </remarks>
    public const double MinimumRatio = 1.5;

    /// <summary>The header line <c>--update-test-cost</c> stamps the configuration onto.</summary>
    public const string ConfigurationMarker = "# configuration:";

    /// <summary>One assembly whose committed cost no longer describes what it measured.</summary>
    public readonly record struct Entry(string Project, double Committed, double Measured) {
        /// <summary>The absolute gap, in seconds.</summary>
        public double Seconds => Math.Abs(Measured - Committed);

        /// <summary>
        ///     The proportional gap, always at least one, whichever way round the two numbers are.
        /// </summary>
        /// <remarks>
        ///     A zero or negative on either side answers <see cref="double.PositiveInfinity" />
        ///     rather than dividing by it: a TRX whose <c>Times</c> span nothing, or a hand-edited
        ///     <c>0.0</c>, is a drift and not an exception.
        /// </remarks>
        public double Ratio =>
            Committed > 0 && Measured > 0
                ? Math.Max(Committed, Measured) / Math.Min(Committed, Measured)
                : double.PositiveInfinity;

        /// <summary>The line the build prints for this assembly.</summary>
        public string Describe() =>
            string.Create(
                CultureInfo.InvariantCulture,
                $"{Project}: the list says {Committed:0.0} s and the run measured {Measured:0.0} s "
                + $"({Seconds:0.0} s, {Ratio:0.00}×)"
            );
    }

    /// <summary>
    ///     The assemblies whose committed cost and measured wall differ by more than both
    ///     thresholds.
    /// </summary>
    /// <remarks>
    ///     ⚠ A measured assembly with no committed line is <em>not</em> a drift. That case already
    ///     has an answer one layer up — it sorts first and is warned about — and folding it in here
    ///     would make every newly added test project fail a green run.
    /// </remarks>
    public static IReadOnlyList<Entry> Find(
        IReadOnlyDictionary<string, double> committed,
        IEnumerable<(string Project, double Seconds)> measured
    ) =>
        [
            .. measured
                .Where(measurement => committed.ContainsKey(measurement.Project))
                .Select(measurement => new Entry(measurement.Project, committed[measurement.Project], measurement.Seconds))
                .Where(entry => entry.Seconds >= MinimumSeconds && entry.Ratio >= MinimumRatio)
                .OrderByDescending(entry => entry.Seconds)
                .ThenBy(entry => entry.Project, StringComparer.Ordinal)
        ];

    /// <summary>
    ///     The configuration the committed list was measured in, or <c>null</c> when it does not say.
    /// </summary>
    /// <remarks>
    ///     ⚠ Without this the check would fail every CI run and every local <c>-c Release</c> one:
    ///     Release walls on other hardware are a different measurement, not a stale list. An absent
    ///     stamp answers <c>null</c> so the caller can say plainly that it did not check, rather
    ///     than guessing a configuration and enforcing against it.
    /// </remarks>
    public static string? ConfigurationOf(IEnumerable<string> lines) =>
        lines
            .Select(line => line.Trim())
            .Where(line => line.StartsWith(ConfigurationMarker, StringComparison.Ordinal))
            .Select(line => line[ConfigurationMarker.Length..].Trim())
            .FirstOrDefault(value => value.Length > 0);
}
