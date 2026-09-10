// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
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

    /// <summary>
    ///     ⚠ The five findings of the run this check first fired on, which were a busy machine and
    ///     not drift, are not findings once the run's own load is divided out.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The numbers are #938's, verbatim: <c>Test</c> exited 255 with 36 288 passing and 0
    ///         failures while five worktree agents compiled on the same box, and every assembly it
    ///         named measured its committed cost to within half a percent when run alone afterwards.
    ///         The five are also the five <em>longest</em> — the ones that overlap everything else
    ///         and therefore the ones contention acts on.
    ///     </para>
    ///     <para>
    ///         ⚠ Their median ratio is 2.24×, so the expectation for each is its committed cost
    ///         times that, and every one of them lands inside one or both thresholds of it. Against
    ///         the raw committed numbers all five are reported, which is the assertion below and the
    ///         defect this normalisation is for.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AUniformlySlowRunIsAMachineAndNotADrift() {
        var committed = new Dictionary<string, double>(StringComparer.Ordinal) {
            ["Vixen.Editor.App.Tests"] = 329.5,
            ["Vixen.Graphics.Golden.Tests"] = 239.0,
            ["Vixen.Geometry.Remeshing.Tests"] = 131.3,
            ["Vixen.Raven.Tests"] = 231.7,
            ["Vixen.Geometry.Uv.Tests"] = 103.9
        };

        (string, double)[] measured = [
            ("Vixen.Editor.App.Tests", 739.1),
            ("Vixen.Graphics.Golden.Tests", 584.9),
            ("Vixen.Geometry.Remeshing.Tests", 345.6),
            ("Vixen.Raven.Tests", 404.3),
            ("Vixen.Geometry.Uv.Tests", 219.8)
        ];

        var load = TestCostDrift.Load.Of(committed, measured);

        Assert.True(load.IsMeasured);
        Assert.Equal(5, load.Sample);
        Assert.Equal(2.243, load.Scale, 2);

        Assert.Empty(TestCostDrift.Find(committed, measured, load));

        // The same comparison with no load divided out is the run that failed, and it named every
        // one of them.
        Assert.Equal(5, TestCostDrift.Find(committed, measured).Count);
    }

    /// <summary>
    ///     ⚠ And the one assembly that really did grow is still a finding on a run where the rest of
    ///     the list moved with the machine.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         #1154's numbers: <c>Vixen.Graphics.Golden.Tests</c> at 2.86× while the rest of the
    ///         top agreed, and re-measured alone it still took 609 s. That is the shape a drift has
    ///         — one row moves — and it has to survive the arithmetic that dissolves the shape a
    ///         loaded machine has.
    ///     </para>
    ///     <para>
    ///         ⚠ The median here is 1.19× rather than 1.00×, because the second-longest assembly had
    ///         grown too, and the check is still correct on both: the golden suite is reported
    ///         against a scaled expectation of 284 s, and <c>Vixen.Editor.App.Tests</c> — 1.42× raw,
    ///         1.19× scaled — is not, which is exactly what that run's operator concluded by hand.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AnAssemblyThatGrewAloneIsStillAFinding() {
        var committed = new Dictionary<string, double>(StringComparer.Ordinal) {
            ["Vixen.Graphics.Golden.Tests"] = 239.0,
            ["Vixen.Editor.App.Tests"] = 329.5,
            ["Vixen.Raven.Tests"] = 231.7,
            ["Vixen.Geometry.Remeshing.Tests"] = 131.3,
            ["Vixen.Geometry.Uv.Tests"] = 103.9
        };

        (string, double)[] measured = [
            ("Vixen.Graphics.Golden.Tests", 684.4),
            ("Vixen.Editor.App.Tests", 467.4),
            ("Vixen.Raven.Tests", 274.7),
            ("Vixen.Geometry.Remeshing.Tests", 142.6),
            ("Vixen.Geometry.Uv.Tests", 109.9)
        ];

        var load = TestCostDrift.Load.Of(committed, measured);
        var entry = Assert.Single(TestCostDrift.Find(committed, measured, load));

        Assert.Equal("Vixen.Graphics.Golden.Tests", entry.Project);
        Assert.InRange(entry.Expected, 280.0, 290.0);
        Assert.InRange(entry.Ratio, 2.3, 2.5);
        Assert.Contains("684.4", entry.Describe(), StringComparison.Ordinal);
        Assert.Contains("expected at this run's", entry.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ The bottom of the list does not vote, which is the difference between an estimate of
    ///     the machine and an estimate of process start-up.
    /// </summary>
    /// <remarks>
    ///     Over the 181 rows of the 2026-09-10 run the ratios of the rows under a second span 0.15×
    ///     to 3.26×, against 0.87×–1.11× for the twelve above thirty seconds. Letting the first
    ///     population set the scale would divide a number out of the second that has nothing to do
    ///     with it — and there are ten times as many of them, so they would decide the median.
    /// </remarks>
    [Fact]
    public void TheNoisyBottomOfTheListDoesNotSetTheScale() {
        var committed = new Dictionary<string, double>(StringComparer.Ordinal);
        var measured = new List<(string, double)>();

        for (var index = 0; index < 20; index++) {
            committed[$"Vixen.Tiny{index}.Tests"] = 1.0;
            measured.Add(($"Vixen.Tiny{index}.Tests", 3.0));
        }

        for (var index = 0; index < 5; index++) {
            committed[$"Vixen.Big{index}.Tests"] = 100.0;
            measured.Add(($"Vixen.Big{index}.Tests", 100.0));
        }

        var load = TestCostDrift.Load.Of(committed, measured);

        Assert.Equal(5, load.Sample);
        Assert.Equal(1.0, load.Scale, 6);
    }

    /// <summary>
    ///     The scale is a median, so the drift being looked for cannot normalise itself away.
    /// </summary>
    /// <remarks>
    ///     ⚠ A mean of the same five rows is 1.80×, at which the 500 s row is 1.11× of its
    ///     expectation and every honest row is 0.56× of its own — the check would lose the finding
    ///     <em>and</em> report the four assemblies that had not moved.
    /// </remarks>
    [Fact]
    public void OneDriftedRowCannotMoveTheScale() {
        var committed = new Dictionary<string, double>(StringComparer.Ordinal) {
            ["Vixen.A.Tests"] = 100.0,
            ["Vixen.B.Tests"] = 100.0,
            ["Vixen.C.Tests"] = 100.0,
            ["Vixen.D.Tests"] = 100.0,
            ["Vixen.Drifted.Tests"] = 100.0
        };

        (string, double)[] measured = [
            ("Vixen.A.Tests", 100.0),
            ("Vixen.B.Tests", 100.0),
            ("Vixen.C.Tests", 100.0),
            ("Vixen.D.Tests", 100.0),
            ("Vixen.Drifted.Tests", 500.0)
        ];

        var load = TestCostDrift.Load.Of(committed, measured);

        Assert.Equal(1.0, load.Scale, 6);

        var entry = Assert.Single(TestCostDrift.Find(committed, measured, load));

        Assert.Equal("Vixen.Drifted.Tests", entry.Project);
    }

    /// <summary>
    ///     ⚠ What this prints on the day it cannot run: that it did not, and in which direction that
    ///     leaves the verdict wrong.
    /// </summary>
    /// <remarks>
    ///     A partial results directory — <c>AffectedTests</c> writes into the same one — has too few
    ///     long assemblies to estimate anything from. The load then applies nothing and says so,
    ///     rather than answering 1.00× and letting the comparison read as normalised.
    /// </remarks>
    [Fact]
    public void ALoadTooSmallToBelieveIsSaidRatherThanAssumed() {
        var committed = new Dictionary<string, double>(StringComparer.Ordinal) {
            ["Vixen.A.Tests"] = 100.0,
            ["Vixen.B.Tests"] = 100.0
        };

        (string, double)[] measured = [("Vixen.A.Tests", 220.0), ("Vixen.B.Tests", 220.0)];

        var load = TestCostDrift.Load.Of(committed, measured);

        Assert.False(load.IsMeasured);
        Assert.Equal(2, load.Sample);
        Assert.Equal(1.0, load.Scale, 6);
        Assert.Equal(100.0, load.Expected(100.0), 6);
        Assert.Contains("compared raw", load.Describe(), StringComparison.Ordinal);

        // And with nothing divided out, a doubled machine reads as drift — which is the honest
        // answer here rather than a hidden one.
        Assert.Equal(2, TestCostDrift.Find(committed, measured, load).Count);
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

    /// <summary>
    ///     ⚠ The half of the check nobody had asked about: a row under a minute cannot be
    ///     contradicted downward by <em>any</em> measurement.
    /// </summary>
    /// <remarks>
    ///     <see cref="TestCostDrift.MinimumSeconds" /> reads as "small assemblies are too noisy to
    ///     judge", which is what it does upward. Downward it does something else entirely: a
    ///     committed 2.7 s can be wrong by at most 2.7 s in that direction, and 2.7 is not 60, so the
    ///     row is unfalsifiable — and an assembly, as <c>TestCostDrift</c>'s own remarks say, only
    ///     ever gets faster between regenerations. The single test project below is the live case
    ///     (#1128).
    /// </remarks>
    [Fact]
    public void ARowUnderAMinuteCanNeverBeReportedAsHavingBecomeFaster() {
        Assert.Null(new TestCostDrift.Reach(2.7).Shrink);
        Assert.Null(new TestCostDrift.Reach(59.9).Shrink);
        Assert.Null(new TestCostDrift.Reach(TestCostDrift.MinimumSeconds).Shrink);

        // The first row that can be, and even it has to fall to 3.6 s — far below where the ratio
        // alone would already have been satisfied.
        var shrink = new TestCostDrift.Reach(63.6).Shrink;

        Assert.NotNull(shrink);
        Assert.Equal(3.6, shrink.Value, 3);

        // Upward every row is reachable, which is why the check is a guard on the schedule's top.
        Assert.Equal(62.7, new TestCostDrift.Reach(2.7).Growth, 3);
        Assert.Equal(494.25, new TestCostDrift.Reach(329.5).Growth, 3);
    }

    /// <summary>
    ///     The stale row this was found through, asserted as the thing the check cannot say.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>Vixen.ApiCheck.Tests</c> — this assembly — is committed at 2.7 s and measured 6.8 s
    ///     after <c>UiGeneratorWiringTests</c> landed, which is 2.52× and clears
    ///     <see cref="TestCostDrift.MinimumRatio" /> outright. It is nonetheless not a finding and
    ///     could not become one at any measurement, because the seconds floor is above the whole row.
    ///     ⚠ This is deliberately not a fix: 2.7 is a number from a contended full run and 6.8 was
    ///     measured alone, so writing 6.8 into a list of contended numbers would make the file
    ///     internally inconsistent and argue the other way on the next real run.
    /// </remarks>
    [Fact]
    public void TheRowThisWasFoundThroughIsBeyondTheChecksReach() {
        var drifted = TestCostDrift.Find(
            new Dictionary<string, double>(StringComparer.Ordinal) { ["Vixen.ApiCheck.Tests"] = 2.7 },
            [("Vixen.ApiCheck.Tests", 6.8)]
        );

        Assert.Empty(drifted);
        Assert.InRange(new TestCostDrift.Entry("Vixen.ApiCheck.Tests", 2.7, 6.8).Ratio, 2.5, 2.53);
    }

    /// <summary>Coverage counts the rows the check could contradict, and it is the minority.</summary>
    [Fact]
    public void CoverageCountsOnlyTheRowsAboveTheSecondsFloor() {
        var coverage = TestCostDrift.Coverage.Of([329.5, 63.6, 60.0, 2.7, 0.4]);

        Assert.Equal(5, coverage.Rows);
        Assert.Equal(2, coverage.Shrinkable);
        Assert.Contains("2 of 5", coverage.Describe(), StringComparison.Ordinal);
        Assert.Contains("guard on the schedule's top", coverage.Describe(), StringComparison.Ordinal);
    }

    /// <summary>The run date is read out of the header, and its absence is a null rather than today.</summary>
    [Fact]
    public void TheMeasuredStampIsReadAndItsAbsenceIsNotGuessed() {
        Assert.Equal(new DateOnly(2026, 9, 5), TestCostDrift.MeasuredOn(["# measured:  2026-09-05 ", "1.0 X"]));
        Assert.Null(TestCostDrift.MeasuredOn(["# configuration: Debug", "1.0 X"]));
        Assert.Null(TestCostDrift.MeasuredOn(["# measured: whenever", "1.0 X"]));
    }

    /// <summary>
    ///     ⚠ The committed file's header is the one the generator writes — including the sentence
    ///     that says how much of the file the check can see.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The header this replaced said a stale number "fails Test", full stop. That is true of
    ///         seven rows and false of a hundred and seventy-one, and nothing could have caught it:
    ///         the sentence was a string literal in <c>build/_build.csproj</c>, which is outside
    ///         <c>Vixen.slnx</c> and which no suite compiles. Generated from
    ///         <see cref="TestCostDrift.Coverage" /> and asserted here, the claim now goes stale
    ///         loudly — add long assemblies and this is red until the file is rewritten.
    ///     </para>
    ///     <para>
    ///         ⚠ The configuration and the date are read back out of the file and handed to the
    ///         generator, so those two are not what this asserts; they have their own tests above.
    ///         What it asserts is the prose and the arithmetic.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheCommittedHeaderIsTheOneTheGeneratorWrites() {
        var lines = File.ReadAllLines(Path.Combine(RepositoryRoot(), "build", "test-cost.txt"));
        var blank = Array.IndexOf(lines, string.Empty);

        Assert.True(blank > 0, "build/test-cost.txt has no blank line between its header and its rows.");

        var costs = lines
            .Skip(blank)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => double.Parse(line.Split(' ')[0], CultureInfo.InvariantCulture))
            .ToList();

        var written = TestCostDrift.Header(
            TestCostDrift.ConfigurationOf(lines)!,
            TestCostDrift.MeasuredOn(lines)!.Value,
            costs
        );

        Assert.Equal(written, lines.Take(blank + 1));
    }

    /// <summary>The committed list says which run measured it, so its age is answerable at all.</summary>
    /// <remarks>
    ///     ⚠ Every other test here would pass unchanged on the day the stamp was dropped, and the age
    ///     is the one axis <see cref="TestCostDrift.MinimumSeconds" /> cannot swallow: on 171 of the
    ///     178 rows it is the only thing a run can honestly say about staleness.
    /// </remarks>
    [Fact]
    public void TheCommittedCostListSaysWhichRunMeasuredIt() {
        var lines = File.ReadAllLines(Path.Combine(RepositoryRoot(), "build", "test-cost.txt"));

        Assert.NotNull(TestCostDrift.MeasuredOn(lines));
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
