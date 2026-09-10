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
///         ⚠ <b>And the comparison is against the committed cost <em>scaled by what this run did to
///         the whole list</em>, which is <see cref="Load" />.</b> The first real firing of this check
///         was a run in which every one of 36 288 tests passed on a box compiling five worktrees at
///         once, and the five assemblies it named were the five longest, uniformly ~2.2× — none of
///         them had drifted, and following the failure's own repair instruction would have committed
///         the contended numbers (#938). A machine multiplies every row at once and a drift moves one
///         row, so a median over the rows big enough to show contention separates the two.
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

    /// <summary>The header line <c>--update-test-cost</c> stamps the run's own date onto.</summary>
    /// <remarks>
    ///     ⚠ <b>The date of the run the numbers came from, not of the regeneration.</b> It is read
    ///     off the earliest <c>Times start</c> in the TRX being summarised, so
    ///     <c>--update-test-cost</c> pointed at a fortnight-old <c>artifacts/test-results</c> stamps
    ///     the fortnight-old date rather than today's. A stamp taken from the clock would answer
    ///     "fresh" for precisely the operation that does not refresh anything.
    /// </remarks>
    public const string MeasuredMarker = "# measured:";

    /// <summary>
    ///     What the machine did to <em>every</em> assembly of this run at once, so that it can be
    ///     divided out before any one of them is called stale.
    /// </summary>
    /// <param name="Scale">
    ///     The median of measured over committed across the rows that voted, or <c>1.0</c> where too
    ///     few did.
    /// </param>
    /// <param name="Sample">How many rows voted.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The check fired on its first real run and what it had measured was contention,
    ///         not drift</b> (#938). <c>Test</c> exited 255 with 36 288 passing and 0 failures, and
    ///         the five assemblies it named were the five <em>longest</em> — 2.24×, 2.45×, 2.63×,
    ///         1.75×, 2.12× — while every one of them measured its committed cost to within half a
    ///         percent when run alone. Five assemblies do not all get 2.2× slower at once; a machine
    ///         does. And the documented repair, <c>--update-test-cost</c>, would have committed the
    ///         contended numbers and then fired in the <em>other</em> direction on the next idle run.
    ///     </para>
    ///     <para>
    ///         <b>So the comparison is against the committed cost scaled by what this run did to the
    ///         population, and not against the committed cost.</b> A busy machine multiplies the
    ///         whole list, so the median moves with it and every row stays at ~1.0× of its
    ///         expectation; one assembly that really grew moves alone, and the median — being a
    ///         median — does not follow it. Reworked over the #938 numbers the five findings all
    ///         fall inside both thresholds, and over #1154's (one assembly at 2.86× while the rest
    ///         agreed) the one finding survives.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What this cannot do, and it is not a fixable shortcoming:</b> a list that is
    ///         uniformly stale and a machine that is uniformly busy are the same measurement. Wall
    ///         clock alone cannot separate them, so the scale is <em>reported</em> and never failed
    ///         on, and the axis that can separate them is the run date this file stamps (#1128) —
    ///         not a threshold.
    ///     </para>
    /// </remarks>
    public readonly record struct Load(double Scale, int Sample) {
        /// <summary>The committed seconds a row needs before its ratio is allowed to vote.</summary>
        /// <remarks>
        ///     Half <see cref="MinimumSeconds" />. The bottom of the list is host start-up rather
        ///     than tests — over the 181 rows of the 2026-09-10 run the ratios of rows under a
        ///     second span 0.15× to 3.26×, against 0.87×–1.11× for the twelve above thirty seconds —
        ///     and those are the rows that overlap everything else and therefore the rows contention
        ///     acts on.
        /// </remarks>
        public const double MinimumRow = MinimumSeconds / 2;

        /// <summary>How many rows have to vote before the scale is believed.</summary>
        /// <remarks>
        ///     ⚠ A median of two is a mean of two, and a mean of two is one drift away from
        ///     normalising itself out of existence. Five is the smallest number at which a single
        ///     genuinely-drifted row cannot move the middle of the sample.
        /// </remarks>
        public const int MinimumSample = 5;

        /// <summary>The load of a run that did not have enough rows to say.</summary>
        public static readonly Load Unmeasured = new(1.0, 0);

        /// <summary>Whether enough rows voted for <see cref="Scale" /> to be worth dividing out.</summary>
        public bool IsMeasured => Sample >= MinimumSample;

        /// <summary>The wall this run should be expected to measure for a committed cost.</summary>
        /// <param name="committed">The seconds the list holds.</param>
        /// <returns>That cost under this run's load, or unchanged where there is no load to apply.</returns>
        public double Expected(double committed) => IsMeasured ? committed * Scale : committed;

        /// <summary>What this run did to the assemblies big enough to show it.</summary>
        /// <param name="committed">The seconds per assembly the list holds.</param>
        /// <param name="measured">What the run measured, by assembly.</param>
        /// <returns>The load, which is <see cref="Unmeasured" /> when too few rows voted.</returns>
        /// <remarks>
        ///     The median rather than the mean, and rows rather than seconds, because the one thing
        ///     that must not move the estimate is the drift being looked for.
        /// </remarks>
        public static Load Of(
            IReadOnlyDictionary<string, double> committed,
            IEnumerable<(string Project, double Seconds)> measured
        ) {
            var ratios = measured
                .Where(measurement =>
                    committed.TryGetValue(measurement.Project, out var cost)
                    && cost >= MinimumRow
                    && measurement.Seconds > 0
                )
                .Select(measurement => measurement.Seconds / committed[measurement.Project])
                .OrderBy(ratio => ratio)
                .ToList();

            if (ratios.Count < MinimumSample) {
                return Unmeasured with { Sample = ratios.Count };
            }

            var middle = ratios.Count % 2 == 1
                ? ratios[ratios.Count / 2]
                : (ratios[(ratios.Count / 2) - 1] + ratios[ratios.Count / 2]) / 2;

            return new(middle, ratios.Count);
        }

        /// <summary>The sentence printed beside every verdict this comparator reaches.</summary>
        /// <remarks>
        ///     The two doubles are formatted invariantly and the two counts are not, for the reason
        ///     <see cref="Coverage.Describe" /> gives: they are non-negative <c>int</c>s and there is
        ///     no culture in which that reads differently.
        /// </remarks>
        public string Describe() {
            var row = MinimumRow.ToString("0", CultureInfo.InvariantCulture);
            var scale = Scale.ToString("0.00", CultureInfo.InvariantCulture);

            return IsMeasured
                ? $"This run measured {scale}× the committed cost across the {Sample} row(s) over "
                + $"{row} s, so that is divided out before any one row is called stale — a busy machine "
                + "multiplies the whole list at once and is not drift (#938). ⚠ A list that is "
                + "uniformly stale looks identical from here; only the run date can speak to that."
                : $"Only {Sample} row(s) over {row} s were measured, which is under the {MinimumSample} this "
                + "needs, so no load was estimated and the costs below are compared raw. ⚠ On a loaded "
                + "machine that reports contention as drift (#938).";
        }
    }

    /// <summary>One assembly whose committed cost no longer describes what it measured.</summary>
    /// <param name="Project">The test assembly.</param>
    /// <param name="Committed">The seconds <c>build/test-cost.txt</c> holds for it.</param>
    /// <param name="Measured">The wall this run measured.</param>
    /// <param name="Scale">
    ///     What this run did to the population, which <see cref="Expected" /> applies to
    ///     <paramref name="Committed" />. One where no load could be estimated.
    /// </param>
    public readonly record struct Entry(string Project, double Committed, double Measured, double Scale = 1.0) {
        /// <summary>The wall this row was expected to measure on the machine this run had.</summary>
        public double Expected => Committed * Scale;

        /// <summary>The absolute gap between that expectation and the measurement, in seconds.</summary>
        public double Seconds => Math.Abs(Measured - Expected);

        /// <summary>
        ///     The proportional gap, always at least one, whichever way round the two numbers are.
        /// </summary>
        /// <remarks>
        ///     A zero or negative on either side answers <see cref="double.PositiveInfinity" />
        ///     rather than dividing by it: a TRX whose <c>Times</c> span nothing, or a hand-edited
        ///     <c>0.0</c>, is a drift and not an exception.
        /// </remarks>
        public double Ratio =>
            Expected > 0 && Measured > 0
                ? Math.Max(Expected, Measured) / Math.Min(Expected, Measured)
                : double.PositiveInfinity;

        /// <summary>The line the build prints for this assembly.</summary>
        /// <remarks>
        ///     ⚠ It names the raw pair as well as the scaled one. The seconds a reader wants to see
        ///     are the two numbers on the two artefacts — the list and the TRX — and the scaled
        ///     expectation is the arithmetic that turned them into a finding.
        /// </remarks>
        public string Describe() =>
            Scale is 1.0
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Project}: the list says {Committed:0.0} s and the run measured {Measured:0.0} s "
                    + $"({Seconds:0.0} s, {Ratio:0.00}×)"
                )
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Project}: the list says {Committed:0.0} s and the run measured {Measured:0.0} s — "
                    + $"{Expected:0.0} s expected at this run's {Scale:0.00}× ({Seconds:0.0} s, {Ratio:0.00}×)"
                );
    }

    /// <summary>How far a measurement has to move before the comparison can see one row.</summary>
    /// <param name="Committed">The seconds the list holds for that assembly.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The two thresholds are not symmetric, and the asymmetry is the whole reason this
    ///         type exists.</b> Upward every row is reachable: any assembly can grow past
    ///         <see cref="Growth" />. Downward a row has to be <em>above</em>
    ///         <see cref="MinimumSeconds" /> to begin with, because a committed 2.7 s can be wrong by
    ///         at most 2.7 s in that direction and 2.7 is not 60. So the seconds threshold does not
    ///         merely make the check quiet on small assemblies — for a row under a minute it makes
    ///         one half of the check <em>unreachable</em>, by any measurement whatsoever.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And an assembly only ever gets faster between regenerations</b>, which
    ///         <c>Build.TestOrder.cs</c>'s remarks say and #863 is the proof of. That is the
    ///         direction 171 of the 178 committed rows cannot be checked in.
    ///     </para>
    /// </remarks>
    public readonly record struct Reach(double Committed) {
        /// <summary>The smallest wall this row could measure and be reported as having grown.</summary>
        public double Growth => Math.Max(Committed + MinimumSeconds, Committed * MinimumRatio);

        /// <summary>
        ///     The largest wall this row could measure and be reported as having shrunk, or
        ///     <c>null</c> where no wall is small enough.
        /// </summary>
        /// <remarks>
        ///     Zero answers <c>null</c> rather than itself: a test assembly that took no time at all
        ///     is not a measurement, so a row whose only reportable shrink is to 0.0 s is one the
        ///     check cannot reach.
        /// </remarks>
        public double? Shrink =>
            Math.Min(Committed - MinimumSeconds, Committed / MinimumRatio) is var wall && wall > 0 ? wall : null;
    }

    /// <summary>How much of a committed list the two thresholds can actually contradict.</summary>
    /// <param name="Rows">Every row carrying a number.</param>
    /// <param name="Shrinkable">Those a measurement could report as having become faster.</param>
    /// <remarks>
    ///     ⚠ <b>This exists because the answer is 7 of 178 and nothing in the tree said so.</b> The
    ///     failure message, the file's header and <c>Build.TestOrder.cs</c>'s remarks all read as
    ///     though a stale row fails; what fails is a row that has grown past a minute, and a list
    ///     that is stale everywhere else passes silently for ever (#1128). A check whose green run
    ///     is read as a freshness claim it never made is this repository's named failure — a gate
    ///     that reports success on the day it does not run — so the coverage is printed wherever the
    ///     comparison speaks, rather than being left to whoever next reads the thresholds.
    /// </remarks>
    public readonly record struct Coverage(int Rows, int Shrinkable) {
        /// <summary>The coverage of a committed cost list.</summary>
        /// <param name="committed">The seconds per assembly the list holds.</param>
        /// <returns>The counts.</returns>
        public static Coverage Of(IEnumerable<double> committed) {
            var costs = committed as IReadOnlyCollection<double> ?? [.. committed];

            return new(costs.Count, costs.Count(cost => new Reach(cost).Shrink is not null));
        }

        /// <summary>The sentence printed beside every verdict this comparator reaches.</summary>
        /// <remarks>
        ///     The floor is formatted invariantly and the two counts are not, because they are
        ///     non-negative <c>int</c>s and there is no culture in which that reads differently.
        /// </remarks>
        public string Describe() {
            var floor = MinimumSeconds.ToString("0", CultureInfo.InvariantCulture);

            return $"{Shrinkable} of {Rows} row(s) are above {floor} s, which is the only place a row that "
                + $"has become faster can be reported at all — the other {Rows - Shrinkable} can be wrong "
                + "downward by any amount and pass. This is a guard on the schedule's top, not a freshness "
                + "check on the list; only `--update-test-cost` after a full run makes it fresh.";
        }
    }

    /// <summary>
    ///     The assemblies whose committed cost and measured wall differ by more than both
    ///     thresholds, with no allowance made for what the machine was doing.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This overload is the #938 defect and is kept only for the case where a load cannot
    ///     be estimated.</b> Every caller that has a whole run's worth of measurements should pass
    ///     <see cref="Load.Of" />, because on a loaded machine the un-normalised comparison reports
    ///     the five longest assemblies and none of them has drifted.
    /// </remarks>
    public static IReadOnlyList<Entry> Find(
        IReadOnlyDictionary<string, double> committed,
        IEnumerable<(string Project, double Seconds)> measured
    ) =>
        Find(committed, measured, Load.Unmeasured);

    /// <summary>
    ///     The assemblies whose committed cost, scaled by what this run did to the whole list,
    ///     differs from the measured wall by more than both thresholds.
    /// </summary>
    /// <param name="committed">The seconds per assembly the list holds.</param>
    /// <param name="measured">What the run measured, by assembly.</param>
    /// <param name="load">What that run did to the population, from <see cref="Load.Of" />.</param>
    /// <returns>The findings, worst first.</returns>
    /// <remarks>
    ///     ⚠ A measured assembly with no committed line is <em>not</em> a drift. That case already
    ///     has an answer one layer up — it sorts first and is warned about — and folding it in here
    ///     would make every newly added test project fail a green run.
    /// </remarks>
    public static IReadOnlyList<Entry> Find(
        IReadOnlyDictionary<string, double> committed,
        IEnumerable<(string Project, double Seconds)> measured,
        Load load
    ) =>
        [
            .. measured
                .Where(measurement => committed.ContainsKey(measurement.Project))
                .Select(measurement =>
                    new Entry(
                        measurement.Project,
                        committed[measurement.Project],
                        measurement.Seconds,
                        load.IsMeasured ? load.Scale : 1.0
                    )
                )
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

    /// <summary>
    ///     The date of the run the committed list was measured from, or <c>null</c> when it does not
    ///     say.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The axis the two thresholds cannot swallow, and it is a report rather than a
    ///     check.</b> <see cref="Coverage" /> is why one is wanted: no measurement can contradict a
    ///     row under a minute, so on 171 of 178 rows the only honest thing a run can say is how old
    ///     the numbers are. Nothing here fails on an age, and no day count is invented — a threshold
    ///     nobody has watched trip would be a second instrument of the kind this is answering.
    /// </remarks>
    public static DateOnly? MeasuredOn(IEnumerable<string> lines) =>
        lines
            .Select(line => line.Trim())
            .Where(line => line.StartsWith(MeasuredMarker, StringComparison.Ordinal))
            .Select(line =>
                DateOnly.TryParse(line[MeasuredMarker.Length..].Trim(), CultureInfo.InvariantCulture, out var date)
                    ? date
                    : (DateOnly?)null
            )
            .FirstOrDefault(date => date is not null);

    /// <summary>The comment block <c>--update-test-cost</c> writes above the measurements.</summary>
    /// <param name="configuration">The configuration the walls below it were measured in.</param>
    /// <param name="measured">The date of the run they came from.</param>
    /// <param name="costs">Those walls, in seconds.</param>
    /// <returns>The lines, ending with the blank one that separates header from rows.</returns>
    /// <remarks>
    ///     ⚠ <b>Here rather than inline in the target, because the header makes a claim about the
    ///     check and a claim can be wrong.</b> The version this replaced said a stale number "fails
    ///     Test" full stop, which is true of seven rows and false of a hundred and seventy-one — and
    ///     nothing could have caught that, because the sentence lived in a string literal in
    ///     <c>build/_build.csproj</c>, which no suite in the tree compiles. Generated from
    ///     <see cref="Coverage" /> and asserted against the committed file, it now goes stale
    ///     loudly: add enough long assemblies and the header is red until it is rewritten.
    /// </remarks>
    public static IReadOnlyList<string> Header(string configuration, DateOnly measured, IEnumerable<double> costs) {
        var coverage = Coverage.Of(costs);
        var floor = MinimumSeconds.ToString("0", CultureInfo.InvariantCulture);
        var ratio = MinimumRatio.ToString("0.0", CultureInfo.InvariantCulture);
        var row = Load.MinimumRow.ToString("0", CultureInfo.InvariantCulture);
        var date = measured.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return [
            "# The wall of each test assembly in seconds, longest first, read out of the TRX",
            "# `Times` of a full Test run. Regenerate with",
            "# `./build.sh TestOrder --update-test-cost`, which reads the TRX that run already",
            "# wrote and reruns nothing.",
            "#",
            "# ⚠ A name here that is no longer a test project in Vixen.slnx fails TestOrder and",
            "# Test. A test project with no line here is scheduled first, not last. And ⚠ a",
            $"# number here that the run disagrees with by more than both {floor} s and {ratio}×",
            "# fails Test as well, in the configuration below — these numbers are read as",
            "# evidence and not only scheduled on, and a stale one argued that the run could not",
            "# be shortened at all (#863).",
            "#",
            "# ⚠ That comparison is made after this run's own load is divided out — the median of",
            $"# measured over committed across the rows above {row} s — because a busy machine multiplies",
            "# every row at once, and reading that as drift is how a run in which every test passed",
            "# failed on the five longest assemblies, none of which had moved (#938).",
            "#",
            "# ⚠ That last guard is a guard on the schedule's TOP and not a freshness check on this",
            "# file, and the difference is most of the file. A row can only be reported as having",
            $"# become faster if it is above {floor} s to begin with, so {coverage.Shrinkable} of these "
            + $"{coverage.Rows} rows",
            $"# can be caught shrinking and {coverage.Rows - coverage.Shrinkable} cannot be, by any "
            + "measurement. A green Test",
            "# says nothing about those; only regenerating after a full run does (#1128).",
            "#",
            $"{MeasuredMarker} {date}",
            $"{ConfigurationMarker} {configuration}",
            ""
        ];
    }
}
