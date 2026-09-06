// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Serilog;

/// <summary>
///     The order <see cref="Test" /> starts the test assemblies in, which is longest first.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The run's elapsed was the longest assembly's <em>start</em> plus its wall, and the
///         start was 218 s of it.</b> Measured from the 178 TRX of the 2026-09-05 local run, laid
///         against the run's own zero: the whole run is 873.3 s, <c>Vixen.Editor.App.Tests</c>
///         starts at 218.4 s and finishes at 873.3 s — it *is* the finish — and everything else in
///         the tree has finished by 542.9 s. Three of the four workers are therefore idle for the
///         last five and a half minutes, waiting on an assembly that could have been started first.
///     </para>
///     <para>
///         What decided the order was <c>Vixen.slnx</c>. A solution build of a non-standard target
///         walks each project's dependencies first, so the assembly that references the most of the
///         tree is scheduled last — and the assembly that references the most of the tree is, for
///         exactly that reason, usually the slowest one. Cost was not an input to it at all.
///     </para>
///     <para>
///         <b>Longest-processing-time-first is the rule, and the traversal project below is how the
///         dependency edges stop deciding.</b> Nothing here needs building — <c>VSTestNoBuild</c> is
///         set and <see cref="Compile" /> has already run — so an edge between two test assemblies
///         buys the schedule nothing and costs it the ordering. Greedy LPT over the same 178
///         measured walls gives a makespan of 655.0 s against the 873.3 s that ran, and 655.0 s was
///         then the longest single assembly, so the run could not be shortened by scheduling at all
///         — only by <see cref="Test" />'s slowest assembly getting faster (#557).
///     </para>
///     <para>
///         ⚠ <b>That last sentence expired, and a stale cost list is how it stayed believed.</b>
///         #557 landed and halved the assembly the whole conclusion rested on:
///         <c>Vixen.Editor.App.Tests</c> is <b>329.5 s</b> in the 178 TRX of the 2026-09-05 23:04
///         run, against the <b>655.0</b> this file's list still claimed for it — a schedule input
///         wrong by 2×, and wrong in the direction that makes the run look unfixable. Regenerated,
///         the same greedy LPT gives <b>489.8 s</b> at four workers (the run itself measured
///         498.3 s, so the model is within 1.7% of what happens) and <b>329.5 s</b> at six or more.
///         So scheduling headroom is back: raising <see cref="Workers" /> from four now buys about
///         160 s of a 498 s run, which the old list said was worth exactly nothing.
///     </para>
///     <para>
///         ⚠ <b>A cost list is an instrument, and this repository's rule is to ask what it prints on
///         the day it is wrong.</b> Three answers are built in: a name in <c>build/test-cost.txt</c>
///         that is no longer a test project in the solution <em>fails</em> the target rather than
///         being ignored; a test project with no line sorts <em>first</em> rather than last, because
///         an unmeasured assembly may be the next 330 s one and starting a 1 s assembly early costs
///         nothing; and the run asserts afterwards that it produced one TRX per test project, so a
///         traversal that quietly ran a subset cannot look like a fast run.
///     </para>
///     <para>
///         ⚠ <b>What none of the three caught was the case above: a name that is still real and a
///         number that is no longer true.</b> Nothing failed, by design — the run is merely packed
///         worse — but the numbers are also read as evidence, here and in <see cref="Workers" />'s
///         own remarks, and a wrong one argues against the work that would fix it (#562).
///     </para>
///     <para>
///         <b>That is now a fourth guard, and it is a property of a run that happened rather than of
///         somebody's memory.</b> <see cref="Test" /> has always read every TRX back afterwards to
///         assert one per project; it now also compares each measured wall with the committed cost
///         and fails on a gap over both of <see cref="TestCostDrift.MinimumSeconds" /> and
///         <see cref="TestCostDrift.MinimumRatio" />. ⚠ It fails rather than warns because a warning
///         in the log of a 498-second run is close to nothing, and because the recovery is one
///         command over artefacts the failing run has already produced:
///         <c>./build.sh TestOrder --update-test-cost</c>. Nothing is re-run to satisfy it.
///     </para>
///     <para>
///         ⚠ <b>And it is enforced only where the two numbers are the same measurement.</b> The list
///         is written by <c>--update-test-cost</c> with the configuration it was measured in stamped
///         into its header; CI runs <c>Test</c> in Release on three operating systems, so enforcing
///         there would fail every run over a configuration difference rather than a stale list.
///         Outside that case the comparison still runs and still prints — what it does not do is
///         fail — so the answer to "what does this print on the day it does not check" is a line
///         saying which configuration the list holds and which one just ran.
///     </para>
/// </remarks>
partial class Build {
    [Parameter("Rewrite build/test-cost.txt from the TRX of the last run instead of reading it")]
    readonly bool UpdateTestCost;

    AbsolutePath TestCostFile => RootDirectory / "build" / "test-cost.txt";

    /// <summary>The generated traversal project, which is an artefact and not a committed file.</summary>
    AbsolutePath TestTraversalFile => ArtifactsDirectory / "test-order.proj";

    /// <summary>
    ///     The test projects <c>Vixen.slnx</c> contains, by the same name rule
    ///     <c>Directory.Build.props</c> uses.
    /// </summary>
    IReadOnlyList<AbsolutePath> SolutionTestProjects() {
        var projects = Solution.AllProjects
            .Select(project => project.Path)
            .Where(IsTestProject)
            .OrderBy(project => project.ToString(), StringComparer.Ordinal)
            .ToList();

        // The instrument again: an empty list here would make Test a target that runs nothing and
        // reports success in record time, which is precisely the shape being guarded against
        // further down.
        Assert.True(projects.Count > 0, $"Read no test projects at all out of {Solution.Path}.");

        return projects;
    }

    /// <summary>
    ///     Seconds per test assembly, by project name, as last measured.
    /// </summary>
    /// <remarks>
    ///     ⚠ Every name in the file must still be a test project in the solution. A rename that
    ///     leaves a stale line behind is the way a cost list stops being read without anyone
    ///     noticing — the ordering silently degrades towards the solution order it replaced — so the
    ///     stale name is an error and not a shrug.
    /// </remarks>
    IReadOnlyDictionary<string, double> TestCosts() {
        Assert.FileExists(TestCostFile);

        var costs = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var line in TestCostFile.ReadAllLines()) {
            var text = line.Trim();

            if (text.Length == 0 || text.StartsWith('#')) {
                continue;
            }

            var fields = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

            Assert.True(
                fields.Length == 2
                && double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _),
                $"{TestCostFile.Name}: '{text}' is not '<seconds> <project name>'."
            );

            costs[fields[1]] = double.Parse(fields[0], CultureInfo.InvariantCulture);
        }

        var known = SolutionTestProjects().Select(project => project.NameWithoutExtension).ToHashSet(StringComparer.Ordinal);
        var stale = costs.Keys.Where(name => !known.Contains(name)).OrderBy(name => name, StringComparer.Ordinal).ToList();

        Assert.True(
            stale.Count == 0,
            $"{TestCostFile.Name} names {stale.Count} project(s) the solution no longer has a test "
            + $"project for: {string.Join(", ", stale)}. Rerun Test and then "
            + "`./build.sh TestOrder --update-test-cost`, or delete the lines by hand — a cost list "
            + "nobody maintains schedules the run in the order it was written in, which is the "
            + "order this replaced."
        );

        return costs;
    }

    /// <summary>
    ///     Every test project in the solution, longest first, with the unmeasured ones ahead of all
    ///     of them.
    /// </summary>
    IReadOnlyList<AbsolutePath> OrderedTestProjects() {
        var costs = TestCosts();
        var projects = SolutionTestProjects();

        var unmeasured = projects
            .Where(project => !costs.ContainsKey(project.NameWithoutExtension))
            .Select(project => project.NameWithoutExtension)
            .ToList();

        if (unmeasured.Count > 0) {
            Log.Warning(
                "{Count} test project(s) have no line in {File} and are scheduled first, which is the "
                + "safe guess rather than the right one: {Projects}",
                unmeasured.Count,
                TestCostFile.Name,
                string.Join(", ", unmeasured)
            );
        }

        return [
            .. projects
                .OrderByDescending(project =>
                    costs.TryGetValue(project.NameWithoutExtension, out var seconds) ? seconds : double.PositiveInfinity
                )
                .ThenBy(project => project.ToString(), StringComparer.Ordinal)
        ];
    }

    /// <summary>
    ///     Writes the traversal project <see cref="Test" /> hands to MSBuild, and returns its path.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A flat item list and one <c>MSBuild</c> task, because that is the whole difference: a
    ///         solution hands MSBuild a graph and MSBuild honours the graph, while an item list is
    ///         dispatched to free nodes in the order it is written. <c>BuildInParallel</c> is what
    ///         lets <c>-m:</c> mean anything here, exactly as it did through the solution.
    ///     </para>
    ///     <para>
    ///         ⚠ No <c>SkipNonexistentTargets</c>, which a solution build of a custom target sets for
    ///         itself. Every one of them references <c>Microsoft.NET.Test.Sdk</c> and therefore has a
    ///         <c>VSTest</c> target; skipping silently is how one that stopped having one would
    ///         become a suite that no longer runs and no longer says so.
    ///     </para>
    ///     <para>
    ///         Plain <c>&lt;Project&gt;</c> with no <c>Sdk</c> attribute, so the repository's
    ///         <c>Directory.Build.props</c> is not imported into it: this file is a schedule and not
    ///         a project, and it should not acquire a target framework or a packing obligation by
    ///         living under the root.
    ///     </para>
    /// </remarks>
    AbsolutePath WriteTestTraversalProject(IReadOnlyList<AbsolutePath> projects) {
        var items = projects.Select(project => new XElement("TestProject", new XAttribute("Include", project.ToString())));

        var document = new XDocument(
            new XComment(
                " Generated by the Test target from build/test-cost.txt. Longest assembly first; see "
                + "build/Build.TestOrder.cs for why the solution's own order is not used. "
            ),
            new XElement("Project",
                new XAttribute("DefaultTargets", "VSTest"),
                new XElement("ItemGroup", items),
                new XElement("Target",
                    new XAttribute("Name", "VSTest"),
                    new XElement("MSBuild",
                        new XAttribute("Projects", "@(TestProject)"),
                        new XAttribute("Targets", "VSTest"),
                        new XAttribute("BuildInParallel", "true")
                    )
                )
            )
        );

        TestTraversalFile.WriteAllText(document.ToString());

        return TestTraversalFile;
    }

    /// <summary>
    ///     The seconds each assembly took, read back out of the TRX the last run wrote.
    /// </summary>
    /// <remarks>
    ///     ⚠ The TRX's own <c>Times</c>, and not the sum of its test durations. What the scheduler
    ///     is packing is the wall of a test host including the time it spends starting, and on the
    ///     small assemblies that start-up is most of the number.
    /// </remarks>
    IReadOnlyList<(string Project, double Seconds)> MeasuredTestCosts() {
        var results = TestResultsDirectory.GlobFiles("*.trx");

        Assert.True(
            results.Count > 0,
            $"No TRX in {TestResultsDirectory}. --update-test-cost reads the last run's results, so "
            + "run Test first."
        );

        return [
            .. results
                .Select(trx => {
                        var times = XDocument.Load(trx).Root!
                            .Elements()
                            .Single(element => element.Name.LocalName == "Times");

                        var start = DateTimeOffset.Parse(times.Attribute("start")!.Value, CultureInfo.InvariantCulture);
                        var finish = DateTimeOffset.Parse(times.Attribute("finish")!.Value, CultureInfo.InvariantCulture);

                        return (Project: trx.NameWithoutExtension, Seconds: (finish - start).TotalSeconds);
                    }
                )
                .OrderByDescending(measurement => measurement.Seconds)
                .ThenBy(measurement => measurement.Project, StringComparer.Ordinal)
        ];
    }

    /// <summary>
    ///     Compares the committed cost list with the TRX of the run that has just finished, and
    ///     fails when the list no longer describes it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ Called from inside <see cref="Test" />, immediately after the one-TRX-per-project
    ///         assertion, and that placement is the whole answer to "what does it print on the day
    ///         it does not run". A freshness check can only mean anything after a full run, and
    ///         <c>artifacts/test-results</c> is empty in a fresh clone and in every agent worktree —
    ///         so hanging it off the presence of TRX would make it a check that no-ops silently for
    ///         everyone who has not just run the suite. Here the TRX are guaranteed: the target
    ///         wrote them, counted them, and asserted their number, three lines above.
    ///     </para>
    ///     <para>
    ///         The failure is deliberately last. Every test has already run and every TRX is already
    ///         on disk when this speaks, so a red run here means "the suite passed and the schedule
    ///         input is stale", and <c>./build.sh TestOrder --update-test-cost</c> reads those same
    ///         artefacts without running anything again.
    ///     </para>
    /// </remarks>
    void AssertTestCostsStillDescribeTheRun() {
        var stamped = TestCostDrift.ConfigurationOf(TestCostFile.ReadAllLines());
        var drifted = TestCostDrift.Find(TestCosts(), MeasuredTestCosts());
        var comparable = IsLocalBuild && string.Equals(stamped, Configuration.ToString(), StringComparison.Ordinal);

        if (drifted.Count == 0) {
            Log.Information(
                "{File} still describes the run: nothing differs by both {Seconds} s and {Ratio}×.",
                TestCostFile.Name,
                TestCostDrift.MinimumSeconds,
                TestCostDrift.MinimumRatio
            );

            return;
        }

        foreach (var entry in drifted) {
            Log.Warning("{Drift}", entry.Describe());
        }

        if (!comparable) {
            // Not a shrug and not silence: the numbers above are real, they are simply not evidence
            // that the list is stale. A Release wall on a CI runner and a Debug wall on this laptop
            // are different measurements of different things.
            Log.Warning(
                "Not failing on the {Count} drift(s) above: the list was measured in {Stamped} and this "
                + "run is {Configuration}{Ci}, so the gap is a difference of measurement rather than a "
                + "stale list.",
                drifted.Count,
                stamped ?? "an unrecorded configuration",
                Configuration,
                IsLocalBuild ? string.Empty : " on a CI runner"
            );

            return;
        }

        Assert.Fail(
            $"Every test passed. What failed is the schedule: {drifted.Count} assembly(ies) in "
            + $"{TestCostFile.Name} differ from what this run measured by more than "
            + $"{TestCostDrift.MinimumSeconds} s and {TestCostDrift.MinimumRatio}× — "
            + $"{string.Join("; ", drifted.Select(entry => entry.Describe()))}. Run "
            + "`./build.sh TestOrder --update-test-cost`, which reads the TRX this run has already "
            + "written and reruns nothing, then commit the list. ⚠ These numbers are read as "
            + "evidence and not only as a schedule: a stale one argued for a year that the run could "
            + "not be shortened at all (#863)."
        );
    }

    Target TestOrder => definition => definition
        .Description("Prints the order Test starts the test assemblies in, or rewrites the cost list from the last run")
        .Executes(() => {
            if (UpdateTestCost) {
                var measured = MeasuredTestCosts();

                TestCostFile.WriteAllLines([
                    "# The wall of each test assembly in seconds, longest first, read out of the TRX",
                        "# `Times` of a full Test run. Regenerate with",
                        "# `./build.sh TestOrder --update-test-cost`, which reads the TRX that run already",
                        "# wrote and reruns nothing.",
                        "#",
                        "# ⚠ A name here that is no longer a test project in Vixen.slnx fails TestOrder and",
                        "# Test. A test project with no line here is scheduled first, not last. And ⚠ a",
                        "# number here that the run disagrees with by more than both",
                        $"# {TestCostDrift.MinimumSeconds:0} s and {TestCostDrift.MinimumRatio:0.0}× fails Test as well, in the",
                        "# configuration below — these numbers are read as evidence and not only scheduled",
                        "# on, and a stale one argued that the run could not be shortened at all (#863).",
                        "#",
                        $"{TestCostDrift.ConfigurationMarker} {Configuration}",
                        "",
                        .. measured.Select(measurement =>
                            $"{measurement.Seconds.ToString("0.0", CultureInfo.InvariantCulture),-8} {measurement.Project}"
                        )
                ]);

                Log.Information("Wrote {Count} measurement(s) to {File}.", measured.Count, TestCostFile);

                return;
            }

            var order = OrderedTestProjects();
            var costs = TestCosts();

            Log.Information("{Count} test assembly(ies), longest first:", order.Count);

            foreach (var project in order) {
                Log.Information(
                    "  {Seconds,8} {Project}",
                    costs.TryGetValue(project.NameWithoutExtension, out var seconds)
                        ? seconds.ToString("0.0", CultureInfo.InvariantCulture)
                        : "?",
                    RootDirectory.GetRelativePathTo(project).ToUnixRelativePath()
                );
            }

            // The same comparison Test enforces, reported rather than enforced, because this target
            // is a printer somebody types. ⚠ And it says which of the two it is doing: with no TRX
            // to read — a fresh clone, an agent worktree — a check that stayed quiet would be
            // indistinguishable from one that passed.
            if (!TestResultsDirectory.Exists() || TestResultsDirectory.GlobFiles("*.trx").Count == 0) {
                Log.Information(
                    "No TRX in {Directory}, so the costs above were not compared with anything. Run Test.",
                    TestResultsDirectory
                );

                return;
            }

            var drifted = TestCostDrift.Find(costs, MeasuredTestCosts());

            if (drifted.Count == 0) {
                Log.Information("Every cost above is within {Seconds} s or {Ratio}× of the last run.",
                    TestCostDrift.MinimumSeconds,
                    TestCostDrift.MinimumRatio
                );

                return;
            }

            foreach (var entry in drifted) {
                Log.Warning("{Drift}", entry.Describe());
            }

            Log.Warning("Rerun with --update-test-cost to rewrite {File} from those measurements.", TestCostFile.Name);
        }
        );
}
