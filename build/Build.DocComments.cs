// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;
using Vixen.Build;

/// <summary>
///     The half of a doc comment that no compiler in this repository is asked about.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two comments landed above the wrong member in one batch and every instrument was
///         green</b> (<a href="https://github.com/Rikarin/Vixen/issues/866">#866</a>): a green build,
///         a green 1 333-test suite, a clean <c>dotnet format whitespace</c> and a clean
///         <c>dotnet format style --severity warn</c>. Ask what those gates print on the day this
///         defect is in the tree and the answer is "success" four times, which is the shape this
///         repository calls a defect in the instrument.
///     </para>
///     <para>
///         ⚠ <b>And it is not that somebody forgot to turn a warning on.</b> A duplicated
///         <c>&lt;summary&gt;</c> is not a Roslyn diagnostic at any severity — there is none to turn
///         on. CS1572 names the other half and needs <c>GenerateDocumentationFile</c>, which
///         <c>Directory.Build.props</c> turns off for the whole tooling profile;
///         <a href="https://github.com/Rikarin/Vixen/issues/821">#821</a> switched it on for one
///         project, and neither stapled file was in it.
///     </para>
///     <para>
///         <b>So this is <see cref="CheckWhitespace" />'s shape</b>: a folder walk with no MSBuild
///         workspace, over the whole tree, in about as long. Both questions are syntactic — a block
///         with two <c>&lt;summary&gt;</c> describes two members, and a <c>&lt;param&gt;</c> naming a
///         parameter the following member does not have belongs to a different one — so nothing here
///         needs a compilation, a restore or a project graph.
///     </para>
///     <para>
///         <b>The rule itself is <c>build/DocCommentRule.cs</c> and this target is one caller of
///         it.</b> <c>DocCommentRuleTests</c> is the other, and it is what gives the rule an answer
///         somebody has read: the two batch-9 stapleings re-introduced verbatim and required to be
///         red, and a fixture of the shapes a textual draft got wrong required to be green. A gate
///         written to catch a defect and never run against one is how a rule ships as decoration.
///     </para>
/// </remarks>
partial class Build {
    AbsolutePath DocCommentExemptFile => RootDirectory / DocCommentRule.ExemptionsPath;

    Target CheckDocComments => definition => definition
        .Description("Fails if a doc comment block outside docs/DocCommentExempt.txt describes a member other than the one it is attached to")
        .Executes(CheckDocCommentPlacement);

    /// <summary>Runs the doc comment rule over the whole tree and fails on any file that is not exempt.</summary>
    void CheckDocCommentPlacement() {
        var root = RootDirectory.ToString().Replace('\\', '/');
        var exempt = DocCommentRule.Exemptions(root);
        var sources = DocCommentRule.Sources(root);

        // ⚠ The instrument, checked before it is trusted. On the day the walk stops finding files —
        // a moved root, an exclusion that swallowed the tree, a changed layout — it reports no
        // findings, and "no findings" is indistinguishable from success.
        Assert.True(
            sources.Count > 3000,
            $"Only {sources.Count} C# files were found under {root}, which cannot be this repository. The walk "
            + "is wrong, and a walk that reads nothing reports a clean tree."
        );

        List<DocCommentRule.Finding> findings = [];

        foreach (var file in sources) {
            findings.AddRange(DocCommentRule.Check(file[(root.Length + 1)..], File.ReadAllText(file)));
        }

        // ⚠ The other half of the instrument, and the one that catches a rule that has stopped
        // reading rather than a walk that has stopped finding. Every file on the exemption list is a
        // file this same run has to have flagged, so a clean sweep with a non-empty list means the
        // checks are no longer firing — the parser's documentation mode is the way that happens, and
        // it is a one-word change away at all times.
        Assert.True(
            findings.Count > 0 || exempt.Count == 0,
            $"The doc comment rule reported nothing at all while {exempt.Count} file(s) are exempt because they "
            + "violate it. The rule did not run — check that the parse still asks for documentation trivia — "
            + "before trusting a green run."
        );

        if (UpdateExemptions) {
            WriteDocCommentExemptions(findings);

            Log.Warning(
                "{File} has been rewritten. Read the diff before committing: this list is supposed to shrink, "
                + "and a commit that grows it is a commit that stapled a comment onto the wrong member.",
                RootDirectory.GetRelativePathTo(DocCommentExemptFile).ToUnixRelativePath()
            );

            return;
        }

        var (unexpected, stale) = DocCommentRule.Review(findings, exempt);

        Assert.True(
            unexpected.Count == 0,
            $"{unexpected.Count} file(s) hold a doc comment block that describes a member other than the one it "
            + "is attached to:\n"
            + string.Join('\n', findings.Where(finding => unexpected.Contains(finding.File, StringComparer.Ordinal)))
        );

        Assert.True(
            stale.Count == 0,
            $"{stale.Count} file(s) in {DocCommentExemptFile.Name} no longer hold one. Delete their lines — the "
            + "list may only shrink: " + string.Join(", ", stale.Take(20))
        );

        Log.Information(
            "Doc comments: every block in {Sources} files describes the member it is attached to, except in the "
            + "{Exempt} exempt.",
            sources.Count,
            exempt.Count
        );
    }

    /// <summary>Rewrites the exemption list from what this run found.</summary>
    /// <param name="findings">Everything the rule reported.</param>
    void WriteDocCommentExemptions(IEnumerable<DocCommentRule.Finding> findings) =>
        DocCommentExemptFile.WriteAllLines([
            .. DocCommentExemptFile
                .ReadAllLines()
                .TakeWhile(line => line.StartsWith('#') || line.Trim().Length == 0),
            .. findings
                .Select(finding => finding.File)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
        ]);
}
