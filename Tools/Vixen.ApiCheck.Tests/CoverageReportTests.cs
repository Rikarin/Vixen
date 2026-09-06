// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>
///     The <c>Coverage</c> target's cobertura reader, over a document shaped like the ones the
///     collector actually writes.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every finding this reader has produced so far was a finding about the reader.</b> Its
///         first version walked <c>Descendants("line")</c> and counted every line twice, because a
///         cobertura class lists its lines once inside each <c>&lt;method&gt;</c> and once more in
///         its own <c>&lt;lines&gt;</c> — and the <i>rate</i> survives that to three decimal places,
///         so a table of percentages could not show it. Its second version derived the subject
///         assembly by stripping <c>.Tests</c> off the project name, which is wrong for every project
///         that renames its assembly and made the target report that those suites "never loaded the
///         assembly they are named after".
///     </para>
///     <para>
///         Both were found by linking <c>build/CoverageReport.cs</c> into a throwaway harness and
///         running it over one real document by hand, and both proofs left nothing behind. This is
///         that harness kept: the fixture below carries the duplicate-listing shape on purpose, so
///         the double count is a red test rather than an anecdote.
///     </para>
///     <para>
///         ⚠ The fixture is not invented. It was cut from a real
///         <c>Code Coverage;Format=cobertura</c> attachment produced on this tree — where the class
///         lists sum to the header's 978 of 1 193 and the descendants walk gives 1 957 of 2 387 — and
///         reduced to the smallest document that still has both lists and two packages.
///     </para>
/// </remarks>
public sealed class CoverageReportTests : IDisposable {
    /// <summary>
    ///     Two packages, and every line written twice: once under the method that owns it and once in
    ///     the class's own list. The class lists hold 3 covered of 5, which is what the header says;
    ///     counting both lists gives 6 of 10.
    /// </summary>
    const string Document = """
        <?xml version="1.0" encoding="utf-8" standalone="yes"?>
        <coverage line-rate="0.6" branch-rate="0" complexity="4" version="1.9" timestamp="1788658482"
                  lines-covered="3" lines-valid="5" branches-covered="0" branches-valid="0">
          <packages>
            <package line-rate="0.6666666666666666" branch-rate="0" complexity="2" name="renamed-subject">
              <classes>
                <class line-rate="0.6666666666666666" branch-rate="0" complexity="2" name="Held" filename="Held.cs">
                  <methods>
                    <method line-rate="0.6666666666666666" branch-rate="0" complexity="2" name="Run" signature="()">
                      <lines>
                        <line number="10" hits="4" branch="False" />
                        <line number="11" hits="4" branch="False" />
                        <line number="12" hits="0" branch="False" />
                      </lines>
                    </method>
                  </methods>
                  <lines>
                    <line number="10" hits="4" branch="False" />
                    <line number="11" hits="4" branch="False" />
                    <line number="12" hits="0" branch="False" />
                  </lines>
                </class>
              </classes>
            </package>
            <package line-rate="0.5" branch-rate="0" complexity="2" name="Some.Dependency">
              <classes>
                <class line-rate="0.5" branch-rate="0" complexity="2" name="Loaded" filename="Loaded.cs">
                  <methods>
                    <method line-rate="0.5" branch-rate="0" complexity="2" name="Also" signature="()">
                      <lines>
                        <line number="7" hits="1" branch="False" />
                        <line number="8" hits="0" branch="False" />
                      </lines>
                    </method>
                  </methods>
                  <lines>
                    <line number="7" hits="1" branch="False" />
                    <line number="8" hits="0" branch="False" />
                  </lines>
                </class>
              </classes>
            </package>
          </packages>
        </coverage>
        """;

    readonly string path = Path.Combine(
        Path.GetTempPath(),
        string.Create(CultureInfo.InvariantCulture, $"vixen-coverage-{Guid.NewGuid():N}.cobertura.xml")
    );

    /// <summary>Writes the fixture where the reader can load it.</summary>
    public CoverageReportTests() => File.WriteAllText(path, Document);

    /// <summary>Removes it again.</summary>
    public void Dispose() => File.Delete(path);

    /// <summary>
    ///     ⚠ The oracle the reader checks itself against, checked here in turn: a cobertura header
    ///     carries the totals over its packages, so a parse that agrees with them is reading the file
    ///     the way the collector wrote it.
    /// </summary>
    [Fact]
    public void TheReadingAgreesWithTheDocumentsOwnHeader() {
        Assert.Equal((3, 5), CoverageReport.AllLines(path));
        Assert.Equal(CoverageReport.DocumentLines(path), CoverageReport.AllLines(path));
    }

    /// <summary>
    ///     The half a rate cannot show. Counting a class's method lists as well as its own doubles
    ///     both figures and leaves the ratio alone, so this asserts the counts and not the rate.
    /// </summary>
    [Fact]
    public void TheLinesAreCountedOnceEvenThoughTheDocumentWritesThemTwice() {
        var (covered, total) = CoverageReport.AllLines(path);

        Assert.Equal(5, total);

        Assert.True(
            total != 10,
            "The reader counted 10 lines in a document whose header says 5 — it is walking every "
            + "<line> descendant, so it is reading each line once under its method and once more in "
            + "its class's own list. The rate is unharmed and every number is doubled."
        );

        Assert.Equal(3, covered);
    }

    /// <summary>
    ///     ⚠ The number is the subject assembly's and not the run's. A suite's report carries every
    ///     assembly the run loaded, so the document-wide figure moves with a dependency's size.
    /// </summary>
    [Fact]
    public void TheNumberIsTheSubjectAssemblysAndNotTheRuns() {
        Assert.Equal((2, 3), CoverageReport.SubjectLines([path], "renamed-subject"));
        Assert.Equal((1, 2), CoverageReport.SubjectLines([path], "Some.Dependency"));
    }

    /// <summary>
    ///     An assembly the document does not name reads as no measurement — zero of zero — which is
    ///     what lets the target say so by name instead of printing 0 %.
    /// </summary>
    [Fact]
    public void AnAssemblyTheDocumentDoesNotNameIsNoMeasurementRatherThanZeroPercent() =>
        Assert.Equal((0, 0), CoverageReport.SubjectLines([path], "Vixen.NotInThisRun"));

    /// <summary>
    ///     ⚠ cobertura names a package by <em>assembly</em> name. This repository renames ten of them,
    ///     so stripping <c>.Tests</c> off the project name is not enough — and the failure it caused
    ///     was the target announcing that a suite never loaded its own subject.
    /// </summary>
    /// <remarks>
    ///     Against the real project files rather than a fixture, because the claim is about this tree:
    ///     <c>Tools/Vixen.ApiCheck</c> really does build <c>vixen-api-check.dll</c>, and a document
    ///     from <c>Vixen.ApiCheck.Tests</c> really does carry the packages <c>vixen-api-check</c> and
    ///     <c>Vixen.ApiCheck.Tests</c> and nothing else.
    /// </remarks>
    [Fact]
    public void TheSubjectIsTheAssemblyNameWhereAProjectRenamesIt() {
        var project = Path.Combine(
            RepositoryRoot(),
            "Tools",
            "Vixen.ApiCheck.Tests",
            "Vixen.ApiCheck.Tests.csproj"
        );

        Assert.True(File.Exists(project), $"{project} is not there, so this test is asserting about nothing.");

        Assert.Equal("vixen-api-check", CoverageReport.Subject(project));
    }

    /// <summary>
    ///     And the convention still holds everywhere else, which is most of the tree — otherwise the
    ///     fix above would be a lookup that only ever answers for ten projects.
    /// </summary>
    [Fact]
    public void TheSubjectIsTheProjectNameWhereNothingIsRenamed() {
        var project = Path.Combine(RepositoryRoot(), "Core", "Vixen.Ecs.Tests", "Vixen.Ecs.Tests.csproj");

        Assert.True(File.Exists(project), $"{project} is not there, so this test is asserting about nothing.");

        Assert.Equal("Vixen.Ecs", CoverageReport.Subject(project));
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
