// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>
///     The membership half of the <c>Benchmark</c> gate — which name on which side of the comparison
///     is a finding, and which is what the run was asked for.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Nothing has ever run this target.</b> There is no committed
///         <c>Benchmarks/baseline.json</c> (<a href="https://github.com/Rikarin/Vixen/issues/339">#339</a>),
///         so <c>Benchmark</c> fails outright, and taking one needs a full BenchmarkDotNet run
///         through <c>build.sh</c>. That is not a footnote about this file, it is the reason for it:
///         the one call site in <c>build/</c> that broke Nuke's argument-quoting rule was in this
///         target, and it survived because a judgement nobody has watched fail is indistinguishable
///         from one that cannot.
///     </para>
///     <para>
///         Linked in the way <c>CoverageReport</c>, <c>AotProbeProjectFile</c>,
///         <c>PublicApiTypeNames</c>, <c>TestCostDrift</c> and <c>WorktreeSafety</c> already are:
///         <c>build/_build.csproj</c> is outside <c>Vixen.slnx</c> and has no test project, so a
///         dependency-free file in it is testable only from here.
///     </para>
/// </remarks>
public sealed class BenchmarkInventoryTests {
    /// <summary>Both directions of drift are found and both are sorted.</summary>
    /// <remarks>
    ///     Sorted because a failure message that reorders between machines is one nobody can diff —
    ///     and the inputs here are deliberately in the wrong order, so a reader that preserved them
    ///     would fail.
    /// </remarks>
    [Fact]
    public void BothDirectionsOfDriftAreFoundAndSorted() {
        var (absent, added, _) = BenchmarkInventory.Drift(
            ["Z.Gone", "A.Kept", "M.Renamed"],
            ["A.Kept", "Y.New", "B.New"],
            BenchmarkInventory.EverySelector
        );

        Assert.Equal(["M.Renamed", "Z.Gone"], absent);
        Assert.Equal(["B.New", "Y.New"], added);
    }

    /// <summary>
    ///     ⚠ An unfiltered run makes absence fatal, which is the check's whole point: a benchmark in
    ///     the baseline that did not run was judged by nobody.
    /// </summary>
    [Theory]
    [InlineData("*")]
    [InlineData("")]
    [InlineData(null)]
    public void AnUnfilteredRunMakesAbsenceFatal(string? filter) =>
        Assert.True(BenchmarkInventory.Drift(["A"], [], filter).AbsenceIsFatal);

    /// <summary>
    ///     ⚠ And a filtered one does not, because under a filter every unselected benchmark is absent
    ///     by construction.
    /// </summary>
    /// <remarks>
    ///     The check that exists to catch a renamed benchmark otherwise fires on all of them, and the
    ///     documented way round that was <c>--report-only</c> — which switches off the allocation
    ///     comparison as well. So <c>nuke Benchmark --benchmark-filter '*Layout*' --report-only</c>,
    ///     the everyday loop doc 12 recommends, could not report the one regression that is the same
    ///     number on every machine. A gate whose only defence against a false positive is switching
    ///     the gate off is not a gate.
    /// </remarks>
    [Theory]
    [InlineData("*Layout*")]
    [InlineData("Vixen.Benchmarks.Ecs*")]
    public void AFilteredRunDoesNotMakeAbsenceFatal(string filter) {
        var (absent, _, absenceIsFatal) = BenchmarkInventory.Drift(["A", "B"], ["A"], filter);

        Assert.Equal(["B"], absent);
        Assert.False(absenceIsFatal);
    }

    /// <summary>
    ///     ⚠ The other direction stays a finding under a filter, and that is not an oversight.
    /// </summary>
    /// <remarks>
    ///     A benchmark that <em>ran</em> and is in no baseline was judged by nothing whether or not
    ///     it was selected deliberately — and selecting it deliberately makes the omission more
    ///     pointed rather than less. Downgrading this half along with the other is the obvious
    ///     symmetry and would put every newly added benchmark outside the gate for as long as anybody
    ///     iterated on it with a filter, which is #602 arriving again by a different door.
    /// </remarks>
    [Fact]
    public void ABenchmarkThatRanAndIsInNoBaselineIsStillFoundUnderAFilter() {
        var (_, added, _) = BenchmarkInventory.Drift(["A"], ["A", "NewlyWritten"], "*Newly*");

        Assert.Equal(["NewlyWritten"], added);
    }

    /// <summary>
    ///     Two sets that agree produce nothing, so the checks above are findings rather than noise.
    /// </summary>
    [Fact]
    public void TwoSetsThatAgreeProduceNoDrift() {
        var (absent, added, _) = BenchmarkInventory.Drift(["A", "B"], ["B", "A"], "*");

        Assert.Empty(absent);
        Assert.Empty(added);
    }
}
