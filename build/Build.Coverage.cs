// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

/// <summary>
///     Coverage, reported and not gated.
/// </summary>
/// <remarks>
///     <para>
///         <c>docs/plan/12</c> § "Coverage, and why there is no <c>Coverage</c> target" refuses the
///         <b>gate</b> — a floor at today's number is a ratchet people route around, a per-project
///         table over ~180 projects goes stale the day a project is added, and a collector that fails
///         to attach reports 0 % or 100 % depending on the tool, which is the Null device in a third
///         costume. ⚠️ <b>None of those arguments is against the report</b>, and this is the report:
///         it prints a number and fails on nothing about the number.
///     </para>
///     <para>
///         ⚠️ <b>It fails on exactly one thing, and that is the instrument.</b> A run that produced
///         no coverage document did not measure zero — it did not measure. So a missing report is an
///         error, an empty project list is an error, and a suite whose report does not name its own
///         subject assembly is an error. Ask what this prints on the day it does not run, and the
///         answer is a failure with the project's name in it rather than a table of zeroes.
///     </para>
///     <para>
///         ⚠️ <b>The number is the subject assembly's, not the run's, and the difference is most of
///         the value.</b> Measured here: <c>Vixen.Graphics.Null.Tests</c> covers 80.8 % of
///         <c>Vixen.Graphics.Null</c> and 32.6 % of everything the run loaded, because the report
///         also carries <c>Vixen.Core</c> at 0.1 % — a figure that says nothing about either project
///         and moves whenever an unrelated dependency grows. A "per-project coverage" table built
///         from the run-wide rate would be that second number, and it is noise wearing a
///         measurement's clothes.
///     </para>
///     <para>
///         <b>The collector is the one the SDK already carries.</b>
///         <c>--collect "Code Coverage;Format=cobertura"</c> comes with <c>Microsoft.NET.Test.Sdk</c>,
///         which every test project here references, so this adds no package and no restore. It was
///         run on this machine against <c>Vixen.Graphics.Null.Tests</c> before being written down;
///         the numbers in the paragraph above are that run's.
///     </para>
///     <para>
///         ⚠️ <b>Sequential, and no <see cref="Test" />-style traversal project.</b> Instrumentation
///         multiplies a run's cost, the whole-solution <c>Test</c> already saturates a developer
///         machine at its natural concurrency, and this target has no CI job behind it — so it runs
///         one assembly at a time and expects to be pointed at a few projects rather than at all of
///         them. <c>--coverage-project</c> is that aim.
///     </para>
/// </remarks>
partial class Build {
    /// <summary>Restricts <see cref="Coverage" /> to the test projects whose name contains this.</summary>
    [Parameter("Restrict Coverage to test projects whose name contains this")]
    readonly string CoverageProject;

    /// <summary>Where the cobertura documents and the summary land.</summary>
    AbsolutePath CoverageDirectory => ArtifactsDirectory / "coverage";

    Target Coverage => definition => definition
        .Description("Reports line coverage per test project. Gates on nothing but its own instrument")
        .DependsOn(Compile)
        .Produces(CoverageDirectory / "coverage.md")
        .Executes(() => {
            var projects = OrderedTestProjects()
                .Where(project => string.IsNullOrEmpty(CoverageProject)
                    || project.NameWithoutExtension.Contains(CoverageProject, StringComparison.OrdinalIgnoreCase)
                )
                .OrderBy(project => project.NameWithoutExtension, StringComparer.Ordinal)
                .ToList();

            Assert.True(
                projects.Count > 0,
                $"--coverage-project '{CoverageProject}' matched no test project, so this run "
                + "would have measured nothing and said so in green."
            );

            CoverageDirectory.CreateOrCleanDirectory();

            var rows = new List<(string Project, string Subject, double Rate, int Covered, int Total)>();

            foreach (var project in projects) {
                var results = CoverageDirectory / project.NameWithoutExtension;

                DotNetTest(settings => settings
                    .SetProjectFile(project.ToString())
                    .SetConfiguration(Configuration)
                    .EnableNoRestore()
                    .EnableNoBuild()
                    .SetSettingsFile(RootDirectory / ".runsettings")
                    .SetResultsDirectory(results)
                    // The collector `Microsoft.NET.Test.Sdk` already carries, asked for by the
                    // name `dotnet test --collect` takes. Cobertura rather than the default
                    // `.coverage`, which is a binary needing a second tool to read.
                    .SetDataCollector("Code Coverage;Format=cobertura")
                    // ⚠ And the reason this target alone still says VSTest out loud. Every other
                    // test run here goes through Microsoft.Testing.Platform now (#560), which is
                    // about a second and two processes per assembly cheaper — but `--collect` and
                    // `--settings` are VSTest concepts the platform ignores with a *warning*, so a
                    // coverage run left on that path would have produced a report of nothing and
                    // said so in green. `Directory.Build.props` leaves the switch overridable for
                    // exactly this, and `xunit.runner.visualstudio` is still referenced so that the
                    // old path still exists to be asked for.
                    .SetProperty("TestingPlatformDotnetTestSupport", false)
                );

                rows.Add(Measure(project, results));
            }

            WriteCoverageSummary(rows);
        }
        );

    /// <summary>Reads one project's cobertura document and picks its subject assembly out of it.</summary>
    /// <param name="testProject">The test project file, e.g. <c>Core/Vixen.Ecs.Tests/Vixen.Ecs.Tests.csproj</c>.</param>
    /// <param name="results">Where that project's run wrote its attachments.</param>
    /// <remarks>
    ///     ⚠ The subject is the test project's name less <c>.Tests</c>, which is a convention rather
    ///     than a fact — <c>Directory.Build.props</c> and <c>docs/plan/02</c> both state it, and
    ///     <see cref="SolutionTestProjects" /> already relies on the same rule. Where a suite's
    ///     subject is genuinely absent from its own report, that is the finding, not an inconvenience:
    ///     a test assembly that never loaded the assembly it is named after is measuring something
    ///     else. ⚠ Which is why the whole project file goes to <see cref="CoverageReport.Subject" />
    ///     rather than its name: a package is named for the <i>assembly</i>, and ten projects here
    ///     rename theirs, so on the convention alone that finding fired on the reader's mistake.
    /// </remarks>
    static (string Project, string Subject, double Rate, int Covered, int Total) Measure(
        AbsolutePath testProject,
        AbsolutePath results
    ) {
        var project = testProject.NameWithoutExtension;
        var documents = results.GlobFiles("**/*.cobertura.xml");

        Assert.True(
            documents.Count > 0,
            $"{project} ran and wrote no cobertura document to {results}. The collector did not "
            + "attach — which is not a coverage of zero, it is no measurement at all, and a target "
            + "that reported 0 % here would be the failure this one exists to avoid."
        );

        // ⚠ The instrument, checked against the document's own arithmetic before any number is read
        // out of it. A cobertura header carries the totals over its packages, so a reading that
        // agrees with them is reading the file the way the collector wrote it. This is not
        // hypothetical: the first version of the reader walked `Descendants("line")` and counted
        // every line twice, because a class lists its lines once per method and once more in its
        // own list — 8 444 of 11 318 for a package of 4 221 of 5 658, at a rate that was right to
        // three decimal places, which is the kind of wrong nobody finds by looking at the table.
        foreach (var document in documents) {
            var summed = CoverageReport.AllLines(document);
            var header = CoverageReport.DocumentLines(document);

            Assert.True(
                summed == header,
                $"{project}'s {document} sums to {summed.Covered}/{summed.Total} lines over its "
                + $"packages and its own header says {header.Covered}/{header.Total}. The reader and "
                + "the collector disagree about what the file says, so every number below it is "
                + "unsafe — including the ones that would look plausible."
            );
        }

        var subject = CoverageReport.Subject(testProject);
        var (covered, total) = CoverageReport.SubjectLines(documents.Select(path => path.ToString()), subject);

        Assert.True(
            total > 0,
            $"{project}'s coverage report does not name {subject} at all, so the suite never loaded "
            + "the assembly it is named after. That is a finding about the suite and not a number "
            + "about the assembly."
        );

        return (project, subject, (double)covered / total, covered, total);
    }

    /// <summary>Writes the table, and logs it, and asserts nothing about any number in it.</summary>
    void WriteCoverageSummary(List<(string Project, string Subject, double Rate, int Covered, int Total)> rows) {
        var lines = new List<string> {
            "# Coverage",
            string.Empty,
            "Line coverage of each test project's own subject assembly, as last measured. No number "
            + "here gates anything — see docs/plan/12 § \"Coverage, and why there is no `Coverage` "
            + "target\" for why a floor would be worse than the gap it fills.",
            string.Empty,
            "| Subject | Lines covered | Lines | Rate |",
            "| --- | ---: | ---: | ---: |"
        };

        foreach (var row in rows.OrderBy(row => row.Rate)) {
            lines.Add(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"| `{row.Subject}` | {row.Covered} | {row.Total} | {row.Rate:P1} |"
                )
            );

            Log.Information(
                "{Subject}: {Covered}/{Total} lines ({Rate:P1}) from {Project}",
                row.Subject,
                row.Covered,
                row.Total,
                row.Rate,
                row.Project
            );
        }

        (CoverageDirectory / "coverage.md").WriteAllLines(lines);

        Log.Information("Wrote {File}", CoverageDirectory / "coverage.md");
    }
}
