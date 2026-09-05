// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Xml.Linq;

/// <summary>
///     Reading a cobertura document, kept away from the target that runs the collector.
/// </summary>
/// <remarks>
///     Its own file with no Nuke in it so that it can be linked into a throwaway harness and run over
///     a real document — which is how the numbers in <c>Build.Coverage.cs</c>'s remarks were checked.
///     A target's body cannot be run here without a whole-solution build; a static method over an XML
///     file can.
/// </remarks>
static class CoverageReport {
    /// <summary>The assembly a test project is named after.</summary>
    /// <param name="project">The test project's assembly name, e.g. <c>Vixen.Ecs.Tests</c>.</param>
    public static string Subject(string project) {
        ArgumentNullException.ThrowIfNull(project);

        return project.EndsWith(".Tests", StringComparison.Ordinal)
            ? project[..^".Tests".Length]
            : project;
    }

    /// <summary>Covered and total lines of one assembly, across however many documents a run wrote.</summary>
    /// <param name="documents">Paths of the cobertura documents.</param>
    /// <param name="subject">The assembly to count, as cobertura names a package.</param>
    /// <remarks>
    ///     ⚠ One assembly and not the document's own <c>line-rate</c>. A suite's report carries every
    ///     assembly the run loaded, so the document-wide figure moves with a dependency's size and
    ///     says nothing about either project — measured here, 32.6 % across the run against 80.8 %
    ///     of the assembly the suite is named after.
    /// </remarks>
    public static (int Covered, int Total) SubjectLines(IEnumerable<string> documents, string subject) {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(subject);

        var covered = 0;
        var total = 0;

        foreach (var document in documents) {
            foreach (var package in XDocument.Load(document).Descendants("package")) {
                if (!string.Equals((string?)package.Attribute("name"), subject, StringComparison.Ordinal)) {
                    continue;
                }

                foreach (var line in package.Descendants("line")) {
                    total++;

                    if ((int?)line.Attribute("hits") > 0) {
                        covered++;
                    }
                }
            }
        }

        return (covered, total);
    }
}
