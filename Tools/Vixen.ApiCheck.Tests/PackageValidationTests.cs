// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>
///     The two refusals that stand in for `PackageValidation`, made to expire loudly.
/// </summary>
/// <remarks>
///     <para>
///         <c>docs/plan/12</c> § NuGet package layout asks <c>Pack</c> for
///         <c>PackageValidation</c> against the previous release, and
///         <a href="https://github.com/Rikarin/Vixen/issues/337">#337</a> has now recorded six
///         separate rounds of the same answer: the baseline half has no baseline (there has never
///         been a release), and the non-baseline half has no input (nothing multi-targets, so the
///         compatible-framework validator has nothing to compare, and nothing packs a
///         <c>runtimes/&lt;rid&gt;/lib</c> path for the compatible-runtime validator to inspect).
///     </para>
///     <para>
///         ⚠ <b>Both of those are true today and neither is true forever, and nothing was watching
///         either.</b> Six rounds each re-derived the same facts by hand and left nothing behind, so
///         the day one of them changes is a day the prose in doc 12 quietly becomes wrong — and it is
///         precisely the day the property starts being worth setting. A refusal that cannot expire
///         loudly is indistinguishable from an oversight a year later.
///     </para>
///     <para>
///         So these do not test <c>PackageValidation</c>. They test the two premises the decision not
///         to enable it rests on, and they fail by naming what changed.
///     </para>
/// </remarks>
public sealed class PackageValidationTests {
    /// <summary>
    ///     ⚠ Nothing in this tree multi-targets, which is why <c>EnablePackageValidation</c> is armed
    ///     for later rather than owed: with no baseline it enables only the compatible-framework and
    ///     compatible-runtime validators, and the first of those needs a second framework to compare.
    /// </summary>
    [Fact]
    public void TheCompatibleFrameworkValidatorStillHasNothingToCompare() {
        var multiTargeted = ProjectFiles()
            .Where(project => XDocument.Load(project)
                .Descendants()
                .Any(element => element.Name.LocalName == "TargetFrameworks")
            )
            .Select(project => Path.GetFileName(project))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            multiTargeted.Count == 0,
            $"{string.Join(", ", multiTargeted)} declares <TargetFrameworks>, so this repository "
            + "multi-targets for the first time and the compatible-framework validator now has "
            + "something to say. docs/plan/12 § NuGet package layout records EnablePackageValidation "
            + "as armed-for-later on the premise that it does not; that premise has expired. Set "
            + "EnablePackageValidation=true in Directory.Build.props, or move this test's premise."
        );
    }

    /// <summary>
    ///     ⚠ And the baseline half is blocked on a fact rather than a decision: the archive holds no
    ///     release older than the version this tree builds, so there is nothing to validate against.
    /// </summary>
    /// <remarks>
    ///     <c>docs/api-history/index.json</c> is the release ritual's own record, committed rather
    ///     than rebuilt from tags — so it is the one statement about "has this repository released"
    ///     that a test can read without a network. Today it holds one row, <c>0.1.0</c>, which is the
    ///     version <c>Directory.Build.props</c> still builds: the archive is *this* release, not a
    ///     previous one.
    /// </remarks>
    [Fact]
    public void NoReleaseOlderThanTheOneBeingBuiltIsArchivedSoThereIsNoBaselineToSet() {
        var current = VersionPrefix();

        var previous = ArchivedReleases()
            .Where(version => !string.Equals(version, current, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            previous.Count == 0,
            $"docs/api-history/index.json archives {string.Join(", ", previous)} and "
            + $"Directory.Build.props builds {current}, so a previous release exists and "
            + "PackageValidationBaselineVersion is owed rather than blocked — see #337 and "
            + "docs/plan/12 § NuGet package layout. ⚠ If that release was never pushed to a feed, "
            + "the property cannot be set either and the refusal has to be rewritten here with that "
            + "as its reason, because \"there has never been a release\" will have stopped being one."
        );
    }

    /// <summary>
    ///     The guard on both of the above: a walk that found no projects, or no archive, would agree
    ///     with everything.
    /// </summary>
    [Fact]
    public void TheWalksAboveActuallyReadSomething() {
        Assert.True(ProjectFiles().Count > 100, "Fewer than a hundred project files — the glob is wrong.");
        Assert.NotEmpty(ArchivedReleases());
        Assert.NotEmpty(VersionPrefix());
    }

    /// <summary>Every project file this tree owns.</summary>
    /// <remarks>
    ///     ⚠ The exclusions are matched on the path <em>relative to the root</em> and not on the
    ///     absolute one, and the difference is not cosmetic: this repository's agent worktrees live
    ///     under <c>.claude/worktrees/&lt;branch&gt;</c>, so an absolute <c>Contains(".claude")</c>
    ///     excludes every file in the tree whenever the tree *is* one of those worktrees — which is
    ///     what happened the first time this ran, and is why the walk states a floor it must clear.
    /// </remarks>
    static List<string> ProjectFiles() {
        var root = RepositoryRoot();

        return Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Select(project => (Path: project, Relative: Path.GetRelativePath(root, project)))
            // A whole checkout per agent lives under .claude/worktrees, and a walk that reads those
            // is comparing other branches' copies of these files with this tree's.
            .Where(project => !Segments(project.Relative).Overlaps([".claude", "artifacts", "bin", "obj"]))
            .Select(project => project.Path)
            .ToList();
    }

    static HashSet<string> Segments(string relative) =>
        new(relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparer.Ordinal);

    static List<string> ArchivedReleases() {
        using var index = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "api-history", "index.json"))
        );

        return index.RootElement.EnumerateArray()
            .Select(row => row.GetProperty("Version").GetString() ?? string.Empty)
            .Where(version => version.Length > 0)
            .ToList();
    }

    static string VersionPrefix() {
        var declared = XDocument.Load(Path.Combine(RepositoryRoot(), "Directory.Build.props"))
            .Descendants()
            .Where(element => element.Name.LocalName == "VersionPrefix")
            .Select(element => element.Value.Trim())
            .FirstOrDefault();

        Assert.False(
            string.IsNullOrEmpty(declared),
            "Directory.Build.props declares no VersionPrefix, so what this tree builds is no longer "
            + "written down where a release can compare itself with it."
        );

        return declared!;
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
