// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tools.Git;
using Serilog;
using Vixen.Build;

partial class Build {
    /// <summary>The exemption list, relative to the repository root.</summary>
    const string PathCaseExemptions = "docs/PathCaseExempt.txt";

    /// <summary>
    ///     The floor below which the committed-path list is not this repository.
    /// </summary>
    /// <remarks>
    ///     <c>CheckLicenceHeaders</c>' argument, and the same number for the same reason: a
    ///     <c>git ls-files</c> that returns forty paths has failed, and forty paths with no
    ///     violations among them is indistinguishable from a clean tree.
    /// </remarks>
    const int PathCaseFloor = 3000;

    Target CheckPathCase => definition => definition
        .Description("Fails if a committed file references a committed path in the wrong case, or if two committed paths differ only in case")
        .Executes(CheckPathCaseReferences);

    /// <summary>
    ///     Fails <see cref="CheckFormat" /> if a reference names a committed path in a case Linux
    ///     will not resolve.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         doc 10 § Cross-platform discipline promised this row and nothing implemented it
    ///         (<a href="https://github.com/Rikarin/Vixen/issues/329">#329</a>). The rule itself, and
    ///         why it is stated as "folds to a committed path but is not one" rather than as "names a
    ///         file that exists", is in <see cref="PathCaseRule" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It belongs on the ubuntu leg and it is not restricted to it.</b> The check reads
    ///         git's index rather than the working tree, so it gives the same answer on all three
    ///         runners and on a developer's machine — which is the half doc 10's wording misses. A
    ///         Linux-only check tells the Windows author who introduced the defect after they have
    ///         pushed; this one can tell them before.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The exemption list is also the positive control.</b> This tree has three
    ///         reference-shaped strings that fold onto a committed path and are not paths at all —
    ///         a shouting-caps sentence in a workflow comment, a nupkg-internal entry, and a CLI
    ///         binary in a README's code fence — and every one of them must still be reported by the
    ///         scan for its exemption to be spent. So a run that quietly stopped matching anything
    ///         fails with three stale exemptions rather than reporting a clean tree, which is the
    ///         one thing this repository asks of an instrument before trusting it.
    ///     </para>
    /// </remarks>
    void CheckPathCaseReferences() {
        var committed = GitTasks
            .Git("ls-files", RootDirectory, logOutput: false, logInvocation: false)
            .Select(line => line.Text.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        Assert.True(
            committed.Count > PathCaseFloor,
            $"`git ls-files` returned {committed.Count} path(s), which is too few to be this "
            + "repository — the command failed, or it was run somewhere other than the root."
        );

        foreach (var group in PathCaseRule.Collisions(committed)) {
            Log.Error(
                "{Paths} differ only in case, so a checkout on macOS or Windows can only have one of them.",
                string.Join(" and ", group)
            );
        }

        Assert.True(
            PathCaseRule.Collisions(committed).Count == 0,
            "Committed paths differ only in case. git can hold both; a case-folding filesystem "
            + "cannot check out both, so one of them is simply missing from every macOS and Windows "
            + "working tree."
        );

        var index = PathCaseRule.Index(committed);
        var exemptFile = RootDirectory / PathCaseExemptions;

        var exempt = exemptFile.FileExists()
            ? PathCaseRule.ReadExemptions(File.ReadAllText(exemptFile))
            : [];

        var violations = new List<PathCaseRule.Violation>();
        var scanned = 0;

        foreach (var path in committed.Where(PathCaseRule.IsScannable)) {
            var file = RootDirectory / path;

            // ⚠ A committed path whose file is not on disk is not an error here, and on this
            // repository's own developer machines it is the *symptom* the collision check above
            // reports: git holds two spellings and the checkout has one.
            if (!file.FileExists()) {
                continue;
            }

            string text;

            try {
                text = File.ReadAllText(file);
            } catch (IOException) {
                continue;
            }

            scanned++;
            violations.AddRange(PathCaseRule.Scan(path, text, index));
        }

        Assert.True(
            scanned > PathCaseFloor,
            $"Read {scanned} committed text file(s), which is too few to be this repository — "
            + "PathCaseRule.BinaryExtensions has grown wrong, or the checkout is partial."
        );

        var reported = violations.Where(violation => !exempt.Contains(violation.Key())).ToList();
        var stale = exempt.Where(key => violations.All(violation => violation.Key() != key)).Order(StringComparer.Ordinal);

        foreach (var violation in reported) {
            Log.Error("{Violation}", violation.ToString());
        }

        foreach (var key in stale) {
            Log.Error("{Key} is exempt in {File} and is no longer reported — delete the line.", key, PathCaseExemptions);
        }

        Assert.True(
            reported.Count == 0,
            $"{reported.Count} reference(s) name a committed path in the wrong case. They resolve on "
            + "macOS and Windows, which fold case, and fail on Linux, on Android, and over HTTP on "
            + "the web. Fix the spelling; do not exempt it unless the string is not a path."
        );

        Assert.True(
            !stale.Any(),
            $"Every line in {PathCaseExemptions} must name a reference this run reported. One that "
            + "does not means either the reference was fixed — delete the line — or the scan has "
            + "stopped matching, in which case a clean result is evidence about the scan."
        );

        Log.Information(
            "Checked {Files} committed text files against {Paths} committed paths; {Exempt} exempt, none mis-cased.",
            scanned,
            index.Count,
            exempt.Count
        );
    }
}
