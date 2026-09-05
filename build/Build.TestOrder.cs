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
///     The order <see cref="Test" /> starts the 178 test assemblies in, which is longest first.
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
///         measured walls gives a makespan of 655.0 s against the 873.3 s that ran, and 655.0 s is
///         the longest single assembly: after this the run cannot be shortened by scheduling at all,
///         only by <see cref="Test" />'s slowest assembly getting faster (#557).
///     </para>
///     <para>
///         ⚠ <b>A cost list is an instrument, and this repository's rule is to ask what it prints on
///         the day it is wrong.</b> Three answers are built in: a name in <c>build/test-cost.txt</c>
///         that is no longer a test project in the solution <em>fails</em> the target rather than
///         being ignored; a test project with no line sorts <em>first</em> rather than last, because
///         an unmeasured assembly may be the next 655 s one and starting a 1 s assembly early costs
///         nothing; and the run asserts afterwards that it produced one TRX per test project, so a
///         traversal that quietly ran a subset cannot look like a fast run.
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
    ///         itself. All 178 of these reference <c>Microsoft.NET.Test.Sdk</c> and therefore have a
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

    Target TestOrder => definition => definition
        .Description("Prints the order Test starts the test assemblies in, or rewrites the cost list from the last run")
        .Executes(() => {
            if (UpdateTestCost) {
                var measured = MeasuredTestCosts();

                TestCostFile.WriteAllLines([
                    "# The wall of each test assembly in seconds, longest first, read out of the TRX",
                        "# `Times` of a local Test run. This is a schedule and not a budget: nothing fails",
                        "# because a number here is wrong, the run is merely packed worse. Regenerate with",
                        "# `./build.sh TestOrder --update-test-cost` after a full local Test run.",
                        "#",
                        "# ⚠ A name here that is no longer a test project in Vixen.slnx fails TestOrder and",
                        "# Test. A test project with no line here is scheduled first, not last.",
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
        }
        );
}
