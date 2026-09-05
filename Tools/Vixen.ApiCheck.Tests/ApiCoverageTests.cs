// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Xml.Linq;
using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>⚠ Verifying the instrument: what <c>CheckApi</c> does not look at, written down.</summary>
/// <remarks>
///     <para>
///         Coverage is a glob in <c>build/Build.Api.cs</c>, and a glob says nothing about what it
///         does not match. An assembly outside it still packs, so every addition, every signature
///         change and every silent removal in it passes with nothing to approve it — while the
///         target prints <c>Checking the public surface of N assemblies</c> and succeeds. Asking
///         what a gate prints on the day it does not run is the whole of this file: the answer for
///         an assembly <c>CheckApi</c> has never heard of is <em>success</em>, and no amount of
///         reading the target's output reveals it.
///     </para>
///     <para>
///         So the skipped set is committed, in <c>build/ApiUncovered.txt</c>, and these two tests
///         hold it to the tree in both directions. A project that starts packing and is checked by
///         nobody fails here rather than shipping quietly; a line for a project that has since been
///         covered, stopped packing or been deleted fails too, because a list that is allowed to
///         rot is one more instrument reporting success.
///     </para>
///     <para>
///         ⚠ The subject is <c>Vixen.slnx</c> rather than a glob over the directory tree, and that
///         is not a detail. A walk from the repository root descends into
///         <c>.claude/worktrees</c> — a whole checkout per agent — and would compare one agent's
///         copy of this repository with another's. Reading the solution also gets the
///         <c>net10.0-ios</c>, <c>-android</c> and <c>-browser</c> projects right for free: they
///         are outside it, nothing has built them, and <c>CheckApi</c> would have nothing to read.
///     </para>
/// </remarks>
public sealed class ApiCoverageTests {
    /// <summary>The tokens <c>build/ApiUncovered.txt</c> accepts, so that "no reason" is not one.</summary>
    static readonly string[] Reasons = ["editor-undecided", "library-undecided", "tool-command-line", "no-assembly"];

    [Fact]
    public void APackableProjectIsEitherCheckedOrWrittenDown() {
        var missing = PackableProjects()
            .Where(project => !IsChecked(project))
            .Where(project => !Ledger().ContainsKey(project))
            .OrderBy(project => project, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "These projects pack, so their public surface is a promise to somebody — and CheckApi "
            + "does not read it, which means an addition, a signature change or a removal in them "
            + "passes with nothing to approve it and the gate still prints success. Either cover "
            + "them in build/Build.Api.cs, or set IsPackable=false, or write the reason in "
            + "build/ApiUncovered.txt.\n  "
            + string.Join("\n  ", missing)
        );
    }

    /// <summary>
    ///     The direction that keeps the list from becoming decoration: a line whose project is now
    ///     covered, no longer packs, or no longer exists.
    /// </summary>
    [Fact]
    public void AWrittenDownProjectStillPacksAndIsStillUnchecked() {
        var packable = PackableProjects().ToHashSet(StringComparer.Ordinal);
        var stale = new List<string>();

        foreach (var (project, reason) in Ledger().OrderBy(entry => entry.Key, StringComparer.Ordinal)) {
            if (!File.Exists(Path.Combine(RepositoryRoot(), project))) {
                stale.Add($"{project}: no such project — it was renamed or removed.");
            } else if (!packable.Contains(project)) {
                stale.Add($"{project}: does not pack, or is not in Vixen.slnx — nothing was skipped, so delete the line.");
            } else if (IsChecked(project)) {
                stale.Add($"{project}: has a PublicAPI.Shipped.txt, so CheckApi does read it now — delete the line.");
            } else if (!Reasons.Contains(reason, StringComparer.Ordinal)) {
                stale.Add($"{project}: `{reason}` is not one of the reasons the file's header defines.");
            }
        }

        Assert.True(
            stale.Count == 0,
            "build/ApiUncovered.txt disagrees with the tree. A list of what a gate skips is only "
            + "worth reading while every line on it is still true.\n  "
            + string.Join("\n  ", stale)
        );
    }

    /// <summary>
    ///     Every non-test, non-generator project in <c>Vixen.slnx</c> that produces a package.
    ///     Absence of <c>IsPackable</c> means <em>yes</em>, exactly as it does in
    ///     <c>Build.Api.cs</c>: everything in the RUNTIME profile packs by profile rather than by
    ///     declaration, and the <c>TOOLING</c> profile never sets the property at all.
    /// </summary>
    static IEnumerable<string> PackableProjects() =>
        SolutionProjects()
            .Where(project => !EndsWithAny(project, ".Tests", ".Generator", ".Generators", ".Analyzers"))
            .Where(
                project => !string.Equals(
                    PropertyOf(Path.Combine(RepositoryRoot(), project), "IsPackable"),
                    "false",
                    StringComparison.OrdinalIgnoreCase
                )
            );

    /// <summary>
    ///     Coverage is read from the baseline beside the project rather than from a second copy of
    ///     <c>Build.Api.cs</c>'s glob — the file is what the gate compares against, and a
    ///     re-implementation of the glob would be the thing most likely to drift out from under
    ///     this test.
    /// </summary>
    static bool IsChecked(string project) =>
        File.Exists(Path.Combine(RepositoryRoot(), Path.GetDirectoryName(project)!, "PublicAPI.Shipped.txt"));

    static Dictionary<string, string> Ledger() {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in File.ReadAllLines(Path.Combine(RepositoryRoot(), "build", "ApiUncovered.txt"))) {
            var text = line.Trim();

            if (text.Length == 0 || text.StartsWith('#')) {
                continue;
            }

            var columns = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            Assert.True(columns.Length == 2, $"build/ApiUncovered.txt: `{text}` is not a path and a reason.");

            entries[columns[0]] = columns[1];
        }

        return entries;
    }

    /// <summary>
    ///     The solution's project paths, as it spells them, normalised to forward slashes.
    /// </summary>
    static IEnumerable<string> SolutionProjects() =>
        XDocument.Load(Path.Combine(RepositoryRoot(), "Vixen.slnx"))
            .Descendants("Project")
            .Select(element => element.Attribute("Path")?.Value)
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(path => path!.Replace('\\', '/'))
            .ToList();

    static bool EndsWithAny(string project, params string[] suffixes) {
        var name = Path.GetFileNameWithoutExtension(project);

        return suffixes.Any(suffix => name.EndsWith(suffix, StringComparison.Ordinal));
    }

    static string? PropertyOf(string project, string name) =>
        XDocument.Load(project).Descendants(name).FirstOrDefault()?.Value.Trim();

    /// <summary>Walks up from the test assembly until the repository root is recognisable.</summary>
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
