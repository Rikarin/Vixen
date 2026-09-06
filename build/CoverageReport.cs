// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// <summary>The assembly a test project is named after, under the name the collector writes.</summary>
    /// <param name="testProject">
    ///     Path of the test project file, e.g. <c>Tools/Vixen.ApiCheck.Tests/Vixen.ApiCheck.Tests.csproj</c>.
    ///     A bare name is accepted and resolves by convention alone.
    /// </param>
    /// <remarks>
    ///     ⚠ <b>cobertura names a package by ASSEMBLY name, and this repository renames assemblies.</b>
    ///     Stripping <c>.Tests</c> off the project name is right for most of the tree and wrong for
    ///     every tool: <c>Tools/Vixen.ApiCheck</c> builds <c>vixen-api-check.dll</c>, so a report of
    ///     <c>Vixen.ApiCheck.Tests</c> carries the packages <c>vixen-api-check</c> and
    ///     <c>Vixen.ApiCheck.Tests</c> and nothing called <c>Vixen.ApiCheck</c> at all. The convention
    ///     alone therefore made <c>Build.Measure</c> fail that suite with "never loaded the
    ///     assembly it is named after" — a finding about the reader wearing a finding about the
    ///     suite, which is exactly the shape this file's other remark warns about. Measured on a real
    ///     document from <c>Vixen.ApiCheck.Tests</c>; there are ten renamed assemblies under
    ///     <c>Tools/</c> and <c>Raven/</c>.
    ///     <para>
    ///         So the sibling project file is asked, and the convention is only the fallback. Read as
    ///         XML rather than grepped, the way <c>AotProbeProjectFile</c> reads the probe.
    ///     </para>
    /// </remarks>
    public static string Subject(string testProject) {
        ArgumentNullException.ThrowIfNull(testProject);

        var name = Path.GetFileNameWithoutExtension(testProject);

        var stem = name.EndsWith(".Tests", StringComparison.Ordinal)
            ? name[..^".Tests".Length]
            : name;

        var parent = Path.GetDirectoryName(Path.GetDirectoryName(testProject));

        if (string.IsNullOrEmpty(parent)) {
            return stem;
        }

        var sibling = Path.Combine(parent, stem, stem + ".csproj");

        if (!File.Exists(sibling)) {
            return stem;
        }

        var declared = XDocument.Load(sibling)
            .Descendants()
            .Where(element => element.Name.LocalName == "AssemblyName")
            .Select(element => element.Value.Trim())
            .FirstOrDefault(value => value.Length > 0);

        return declared ?? stem;
    }

    /// <summary>Covered and total lines of one assembly, across however many documents a run wrote.</summary>
    /// <param name="documents">Paths of the cobertura documents.</param>
    /// <param name="subject">The assembly to count, as cobertura names a package.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ One assembly and not the document's own <c>line-rate</c>. A suite's report carries
    ///         every assembly the run loaded, so the document-wide figure moves with a dependency's
    ///         size and says nothing about either project — measured here, 32.6 % across the run
    ///         against 80.8 % of the assembly the suite is named after.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every line in a cobertura document is written twice, and counting the descendants
    ///         counts both.</b> A <c>&lt;class&gt;</c> lists its lines once inside each
    ///         <c>&lt;method&gt;</c> and once more in its own <c>&lt;lines&gt;</c>, so
    ///         <c>package.Descendants("line")</c> reports an assembly at about double its size —
    ///         measured on <c>Vixen.Core.Mathematics</c>, 8 444 of 11 318 for a package that is 4 221
    ///         of 5 658. The <i>rate</i> survives that almost intact, which is exactly why it would
    ///         not have been noticed: the two sets are near-identical copies, so the ratio is right
    ///         to three decimal places while both counts are wrong by a factor. This walks each
    ///         class's own list, which is the complete one; the method lists are not the same set,
    ///         and hold seven lines the class lists do not.
    ///     </para>
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

                var (packageCovered, packageTotal) = Lines(package);

                covered += packageCovered;
                total += packageTotal;
            }
        }

        return (covered, total);
    }

    /// <summary>What a document says about itself, in the header the collector wrote.</summary>
    /// <param name="document">Path of the cobertura document.</param>
    /// <returns>Its <c>lines-covered</c> and <c>lines-valid</c>, or −1 for either it does not carry.</returns>
    /// <remarks>
    ///     ⚠ <b>The oracle for the reading above, which is why it is a method rather than a comment.</b>
    ///     A cobertura document carries its own totals and they are the sum over its packages, so a
    ///     parse that agrees with them is reading the file the way the collector wrote it and one
    ///     that does not is off by whatever it double-counted or skipped. It is closed-form, it is in
    ///     every document, and it is what would have caught the descendants walk on the first
    ///     document anybody parsed.
    /// </remarks>
    public static (int Covered, int Total) DocumentLines(string document) {
        ArgumentNullException.ThrowIfNull(document);

        var root = XDocument.Load(document).Root;

        return ((int?)root?.Attribute("lines-covered") ?? -1, (int?)root?.Attribute("lines-valid") ?? -1);
    }

    /// <summary>Covered and total lines of every package in a document, read the same way.</summary>
    /// <param name="document">Path of the cobertura document.</param>
    /// <returns>The sum over its packages, which is what <see cref="DocumentLines" /> should say.</returns>
    public static (int Covered, int Total) AllLines(string document) {
        ArgumentNullException.ThrowIfNull(document);

        var covered = 0;
        var total = 0;

        foreach (var package in XDocument.Load(document).Descendants("package")) {
            var (packageCovered, packageTotal) = Lines(package);

            covered += packageCovered;
            total += packageTotal;
        }

        return (covered, total);
    }

    /// <summary>The lines of one package, taken from each class's own list rather than its methods'.</summary>
    /// <param name="package">A <c>&lt;package&gt;</c> element.</param>
    static (int Covered, int Total) Lines(XElement package) {
        var covered = 0;
        var total = 0;

        foreach (var line in package.Descendants("class").Elements("lines").Elements("line")) {
            total++;

            if ((int?)line.Attribute("hits") > 0) {
                covered++;
            }
        }

        return (covered, total);
    }
}
