// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
///     The two directions a benchmark run and a committed baseline can disagree about membership.
/// </summary>
/// <remarks>
///     <para>
///         <b>The gap this closes: only one of the two could fail a run.</b> A benchmark in the
///         baseline that did not run was fatal — a renamed, deleted or unlaunched benchmark is judged
///         by nobody — while one that ran and was not in the baseline printed a single
///         <c>Log.Information</c> line and left the run green. So every benchmark added after the
///         baseline was taken was ungated for ever, and the only trace of it was a line in a log
///         nobody reads on a green run.
///     </para>
///     <para>
///         ⚠ <b>Both directions are the same defect, and neither of them is a number.</b> The two
///         halves this target judges — an allocation that may not grow, a mean that may not grow by
///         more than a tolerance — are only ever computed over the intersection, so a name on either
///         side of it is a benchmark nothing compared. That is the registry drift
///         <c>FuzzGateTests.TheNightlyMatrixIsTheRegistry</c> exists to stop one document over,
///         arriving in the target that is supposed to be the performance instrument.
///     </para>
///     <para>
///         ⚠ <b>Adding a benchmark is meant to be a two-step change, not a blocked one.</b> The
///         failure names <c>--update-baseline</c>, which is the same shape
///         <c>docs/WhitespaceExempt.txt</c> takes: a list that may only change deliberately, by a
///         command somebody runs, rather than one the gate quietly keeps up to date for itself.
///     </para>
/// </remarks>
static class BenchmarkInventory {
    /// <summary>The <c>--benchmark-filter</c> that selects the whole suite, which is the default.</summary>
    public const string EverySelector = "*";

    /// <summary>Whether a filter asks for less than the whole suite.</summary>
    /// <param name="filter">What <c>--benchmark-filter</c> was given, if anything.</param>
    /// <remarks>
    ///     ⚠ Deliberately crude: a filter that narrows nothing in practice — <c>Vixen*</c>, say —
    ///     reads as narrowing here. That errs towards not crying wolf, and it costs nothing where it
    ///     matters, because no CI job passes a filter at all.
    /// </remarks>
    public static bool Narrows(string? filter) =>
        !string.IsNullOrEmpty(filter) && !string.Equals(filter, EverySelector, StringComparison.Ordinal);

    /// <summary>
    ///     Compares the two name sets, and says whether the absences among them mean anything.
    /// </summary>
    /// <param name="expected">The benchmark names the baseline holds.</param>
    /// <param name="recorded">The benchmark names this run produced reports for.</param>
    /// <param name="filter">The <c>--benchmark-filter</c> the run was given.</param>
    /// <returns>
    ///     <c>Absent</c> is in the baseline and did not run; <c>Added</c> ran and is in no baseline;
    ///     <c>AbsenceIsFatal</c> is whether the first of those is a finding rather than an
    ///     explanation. Both lists sorted, so a failure message reads the same on every machine.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>Absence means "judged by nobody" only when the run was asked for everything.</b>
    ///     Under a filter every unselected benchmark is absent by construction, so the check that
    ///     exists to catch a renamed or unlaunched benchmark instead fires on all of them — and the
    ///     documented way out was <c>--report-only</c>, which silences the allocation comparison too.
    ///     So the everyday local loop doc 12 recommends could not report the one regression that is
    ///     the same number on every machine. A gate whose only defence against a false positive is
    ///     switching the gate off is not a gate.
    ///     <para>
    ///         ⚠ <b>The other direction stays fatal under a filter</b>, and that is not an oversight:
    ///         a benchmark that <em>ran</em> and is in no baseline was judged by nothing whether or
    ///         not it was selected deliberately, and selecting it deliberately makes the omission
    ///         more pointed rather than less.
    ///     </para>
    /// </remarks>
    public static (List<string> Absent, List<string> Added, bool AbsenceIsFatal) Drift(
        IEnumerable<string> expected,
        IEnumerable<string> recorded,
        string? filter
    ) {
        var baseline = expected.ToHashSet(StringComparer.Ordinal);
        var results = recorded.ToHashSet(StringComparer.Ordinal);

        return (
            baseline.Except(results, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            results.Except(baseline, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            !Narrows(filter)
        );
    }
}
