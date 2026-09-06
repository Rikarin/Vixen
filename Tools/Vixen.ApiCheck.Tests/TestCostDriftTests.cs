// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>
///     ⚠ The fourth guard over <c>build/test-cost.txt</c>, which is the first one about a
///     <em>number</em>.
/// </summary>
/// <remarks>
///     <para>
///         The three that existed are all about a project <em>name</em>: a name the solution no
///         longer has fails, a project with no line sorts first, and the run asserts one TRX per
///         project. A line whose name was real and whose number was 2× wrong therefore failed
///         nothing — and that is not a cosmetic loss, because the numbers are read as evidence:
///         <c>655.0 Vixen.Editor.App.Tests</c>, stale by 325.5 s after #557 halved that assembly,
///         is what three separate places concluded "the run cannot be shortened by scheduling at
///         all" from (#863).
///     </para>
///     <para>
///         Here rather than beside <c>build/_build.csproj</c> for the reason
///         <see cref="AotProbeProjectFileTests" /> already gives: the build project is outside
///         <c>Vixen.slnx</c> and no suite in the tree tests it. The comparator is linked into this
///         assembly as source, so the subject below is the code the gate runs.
///     </para>
/// </remarks>
public sealed class TestCostDriftTests {
    /// <summary>
    ///     The measurement the whole guard was written for, asserted as the pair of numbers it
    ///     actually was.
    /// </summary>
    /// <remarks>
    ///     ⚠ 655.0 against 329.5 is 1.988×, which is why <see cref="TestCostDrift.MinimumRatio" />
    ///     is 1.5 and not the rounder 2: a threshold of 2 would have missed by a hand's breadth the
    ///     one drift it was written for.
    /// </remarks>
    [Fact]
    public void TheDriftThatWentUnnoticedForABatchIsCaught() {
        var drifted = TestCostDrift.Find(
            new Dictionary<string, double>(StringComparer.Ordinal) { ["Vixen.Editor.App.Tests"] = 655.0 },
            [("Vixen.Editor.App.Tests", 329.5)]
        );

        var entry = Assert.Single(drifted);

        Assert.Equal("Vixen.Editor.App.Tests", entry.Project);
        Assert.Equal(325.5, entry.Seconds, 3);
        Assert.InRange(entry.Ratio, 1.98, 1.99);
        Assert.Contains("655.0", entry.Describe(), StringComparison.Ordinal);
        Assert.Contains("329.5", entry.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     A cost list that is right is not a finding, which is the half a check can lose without
    ///     anyone noticing.
    /// </summary>
    [Fact]
    public void AListThatStillDescribesTheRunReportsNothing() =>
        Assert.Empty(
            TestCostDrift.Find(
                new Dictionary<string, double>(StringComparer.Ordinal) {
                    ["Vixen.Editor.App.Tests"] = 329.5,
                    ["Vixen.Ecs.Tests"] = 2.0
                },
                [("Vixen.Editor.App.Tests", 331.9), ("Vixen.Ecs.Tests", 2.4)]
            )
        );

    /// <summary>
    ///     A small assembly that trebles is not a finding, because both thresholds have to clear.
    /// </summary>
    /// <remarks>
    ///     This is the population the seconds threshold exists for: host start-up dominates the
    ///     bottom of the list, so a 1.2 s assembly reading 4.0 s on a loaded machine is 3.3× and
    ///     worth nothing at all to a 498-second schedule.
    /// </remarks>
    [Fact]
    public void ASmallAssemblyThatTreblesIsBelowTheSecondsThreshold() =>
        Assert.Empty(
            TestCostDrift.Find(
                new Dictionary<string, double>(StringComparer.Ordinal) { ["Vixen.Gameplay.Ai.Tests"] = 1.2 },
                [("Vixen.Gameplay.Ai.Tests", 4.0)]
            )
        );

    /// <summary>
    ///     A large assembly that runs eighty seconds long is not a finding either, for the opposite
    ///     reason.
    /// </summary>
    /// <remarks>
    ///     The ratio threshold is what keeps a loaded machine from failing a green run: fifteen
    ///     agent worktrees testing at once move a 239 s assembly by more than a minute without the
    ///     list having gone stale at all.
    /// </remarks>
    [Fact]
    public void ALargeAssemblyRunningLongUnderLoadIsBelowTheRatioThreshold() =>
        Assert.Empty(
            TestCostDrift.Find(
                new Dictionary<string, double>(StringComparer.Ordinal) { ["Vixen.Graphics.Golden.Tests"] = 239.0 },
                [("Vixen.Graphics.Golden.Tests", 320.0)]
            )
        );

    /// <summary>
    ///     A measured assembly with no committed line is not a drift.
    /// </summary>
    /// <remarks>
    ///     ⚠ Folding that case in here would make every newly added test project fail an otherwise
    ///     green run, and it already has an answer one layer up: an unmeasured project is scheduled
    ///     first and warned about, because it may be the next 330-second one.
    /// </remarks>
    [Fact]
    public void AnAssemblyWithNoCommittedLineIsNotADrift() =>
        Assert.Empty(
            TestCostDrift.Find(
                new Dictionary<string, double>(StringComparer.Ordinal),
                [("Vixen.BrandNew.Tests", 400.0)]
            )
        );

    /// <summary>
    ///     A committed zero is a drift rather than a division by one, and the findings are ordered
    ///     by what they cost the schedule.
    /// </summary>
    [Fact]
    public void AZeroCostDriftsAndTheWorstIsReportedFirst() {
        var drifted = TestCostDrift.Find(
            new Dictionary<string, double>(StringComparer.Ordinal) {
                ["Vixen.Zero.Tests"] = 0.0,
                ["Vixen.Bigger.Tests"] = 400.0
            },
            [("Vixen.Zero.Tests", 90.0), ("Vixen.Bigger.Tests", 100.0)]
        );

        Assert.Equal(["Vixen.Bigger.Tests", "Vixen.Zero.Tests"], drifted.Select(entry => entry.Project));
        Assert.Equal(double.PositiveInfinity, drifted[1].Ratio);
    }

    /// <summary>The configuration stamp is read out of the header, and its absence is a null.</summary>
    /// <remarks>
    ///     ⚠ Null is what stops the check failing every CI run: Release walls on a Linux runner and
    ///     Debug walls on this laptop are different measurements, not a stale list, so the build
    ///     reports the gap and declines to fail on it.
    /// </remarks>
    [Fact]
    public void TheConfigurationStampIsReadAndItsAbsenceIsNotGuessed() {
        Assert.Equal("Release", TestCostDrift.ConfigurationOf(["# a comment", "# configuration:  Release ", "1.0 X"]));
        Assert.Null(TestCostDrift.ConfigurationOf(["# a comment", "1.0 X"]));
        Assert.Null(TestCostDrift.ConfigurationOf(["# configuration:", "1.0 X"]));
    }

    /// <summary>
    ///     ⚠ The instrument itself: the committed list carries a stamp, so the comparison in
    ///     <c>Test</c> is one that can fail rather than one that reports and returns forever.
    /// </summary>
    /// <remarks>
    ///     Every other test here would pass unchanged on the day <c>build/test-cost.txt</c> lost its
    ///     <c>#&#160;configuration:</c> line, and the guard would have quietly become a warning — the
    ///     exact shape this repository keeps rediscovering. This asserts the file the build reads,
    ///     not a fixture.
    /// </remarks>
    [Fact]
    public void TheCommittedCostListSaysWhichConfigurationMeasuredIt() {
        var lines = File.ReadAllLines(Path.Combine(RepositoryRoot(), "build", "test-cost.txt"));

        Assert.Equal("Debug", TestCostDrift.ConfigurationOf(lines));
    }

    static string RepositoryRoot() {
        var directory = AppContext.BaseDirectory;

        while (directory is not null) {
            if (File.Exists(Path.Combine(directory, "Vixen.slnx"))) {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("No Vixen.slnx above the test assembly, so no repository root.");
    }
}
