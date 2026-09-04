// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tools.Git;
using Serilog;

/// <summary>
///     What a working copy changed, and which projects own it.
/// </summary>
/// <remarks>
///     <para>
///         The inner loop and the gate want different things from the same target. The gate has to
///         see the whole tree, because a branch that was green before its neighbours merged is this
///         repository's most-repeated way of shipping a regression (CLAUDE.md § Build and test). A
///         developer editing four files wants an answer in seconds. <see cref="Since" /> is the
///         switch between them, and it is opt-in precisely so that the unattended run — CI, and the
///         sweep on master — keeps checking everything.
///     </para>
///     <para>
///         ⚠ <b>A selector that returns the empty set on an unrecognised path reports success by
///         running nothing</b>, which is the failure shape CLAUDE.md § "How this codebase decides
///         something is proved" names first. So the mapping here is total: every changed file either
///         maps to a project or matches <see cref="OwnedByNoProject" />, and anything else is an
///         error rather than a silent skip.
///     </para>
/// </remarks>
partial class Build {
    /// <summary>
    ///     The git ref to diff against, or <c>null</c> for "check everything".
    /// </summary>
    /// <remarks>
    ///     Shared by every target that can narrow itself, so that one spelling — <c>--since</c> —
    ///     means the same thing everywhere. Absent is the default on purpose: the targets behave
    ///     exactly as they did before anybody passes it.
    /// </remarks>
    [Parameter("Narrow a target to what changed since this git ref — the whole tree when unset")]
    readonly string Since;

    /// <summary>
    ///     Every file that differs from <paramref name="since" />, plus every file git does not yet
    ///     track, as absolute paths.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The untracked half is not decoration.</b> <c>git diff --name-only &lt;ref&gt;</c>
    ///         compares the working tree with a commit, so it reports an edited file and says
    ///         nothing at all about a file that was just created — and a brand new source file is
    ///         exactly the one most likely to be missing a header, misformatted, or covered by no
    ///         test. Leaving it out would have made this selector quietest about its worst case.
    ///     </para>
    ///     <para>
    ///         Deleted files are dropped, because there is nothing left to check in them; their
    ///         owning project is still reached by whatever else changed, or by nothing, and "nothing
    ///         changed but a deletion" is a legitimately empty result.
    ///     </para>
    /// </remarks>
    IReadOnlyList<AbsolutePath> ChangedFiles(string since) {
        // Fails loudly and immediately on a ref that does not resolve, rather than diffing against
        // something surprising. `git diff` against an unknown ref is an error too, but its message
        // is about a path, which reads as though the path were the problem.
        GitTasks.Git($"rev-parse --verify --quiet {since}^{{commit}}", RootDirectory, logOutput: false, logInvocation: false);

        var changed = GitTasks.Git($"diff --name-only {since}", RootDirectory, logOutput: false, logInvocation: false);
        var untracked = GitTasks.Git("ls-files --others --exclude-standard", RootDirectory, logOutput: false, logInvocation: false);

        return [
            .. changed.Concat(untracked)
                .Select(line => line.Text.Trim())
                .Where(line => line.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Select(line => RootDirectory / line)

                // A deletion still appears in the diff; the file does not exist to be checked.
                .Where(path => path.FileExists())
        ];
    }

    /// <summary>
    ///     The nearest <c>.csproj</c> at or above <paramref name="file" />, or <c>null</c> when the
    ///     walk reaches the repository root without finding one.
    /// </summary>
    /// <remarks>
    ///     Nearest rather than "the project that lists this file": an SDK project globs its own
    ///     directory, so directory containment <em>is</em> the ownership rule here, and reading it
    ///     off the filesystem costs one directory listing per level instead of an MSBuild
    ///     evaluation. ⚠ It also sees the out-of-solution projects — the mobile and web heads that
    ///     <see cref="Test" />, <see cref="CheckFormat" />, <see cref="CheckApi" /> and
    ///     <see cref="Pack" /> never evaluate — which is a difference from the unscoped run and is
    ///     recorded rather than hidden.
    /// </remarks>
    AbsolutePath OwningProject(AbsolutePath file) {
        for (var directory = file.Parent; directory is not null && directory != RootDirectory.Parent; directory = directory.Parent) {
            var project = directory.GlobFiles("*.csproj").FirstOrDefault();

            if (project is not null) {
                return project;
            }

            if (directory == RootDirectory) {
                break;
            }
        }

        return null;
    }

    /// <summary>
    ///     Whether a changed file is one no project can own, so that owning none of them is an
    ///     answer rather than a hole.
    /// </summary>
    /// <remarks>
    ///     The list is deliberately short and by directory. Everything else that reaches the walk
    ///     and finds no project is reported as an error: a source file outside every project is
    ///     either a project that was never added or a rule here that has gone stale, and both are
    ///     worth a message.
    /// </remarks>
    bool OwnedByNoProject(AbsolutePath file) {
        var relative = RootDirectory.GetRelativePathTo(file).ToUnixRelativePath().ToString();

        return !relative.Contains('/', StringComparison.Ordinal)
            || relative.StartsWith("docs/", StringComparison.Ordinal)
            || relative.StartsWith(".github/", StringComparison.Ordinal)
            || relative.StartsWith(".nuke/", StringComparison.Ordinal)
            || relative.StartsWith(".config/", StringComparison.Ordinal)
            || relative.StartsWith("references/", StringComparison.Ordinal)
            || relative.StartsWith("artifacts/", StringComparison.Ordinal);
    }

    /// <summary>
    ///     The projects that own <paramref name="changed" />, deduplicated and ordered.
    /// </summary>
    /// <exception cref="Exception">
    ///     When a changed file neither maps to a project nor matches <see cref="OwnedByNoProject" />.
    /// </exception>
    IReadOnlyList<AbsolutePath> ProjectsOwning(IEnumerable<AbsolutePath> changed) {
        var projects = new SortedSet<string>(StringComparer.Ordinal);
        var orphans = new List<string>();

        foreach (var file in changed) {
            var project = OwningProject(file);

            if (project is not null) {
                projects.Add(project);

                continue;
            }

            if (!OwnedByNoProject(file)) {
                orphans.Add(RootDirectory.GetRelativePathTo(file).ToUnixRelativePath());
            }
        }

        Assert.True(
            orphans.Count == 0,
            $"{orphans.Count} changed file(s) belong to no project and to no directory this build "
            + "knows is projectless, so narrowing by --since would have skipped them silently: "
            + string.Join(", ", orphans.Take(10))
        );

        return [.. projects.Select(AbsolutePath.Create)];
    }

    /// <summary>
    ///     The projects <c>Vixen.slnx</c> actually contains, by absolute path.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Narrowing has to be a subset of what the unscoped run checks, or it is not a
    ///     narrowing at all.</b> <c>build/_build.csproj</c> is the proof: it is not in the solution,
    ///     it carries 25 pre-existing <c>IDE1006</c> violations, and <c>dotnet format style</c>
    ///     exits 2 on it today — so a <c>--since</c> that mapped an edit in <c>build/</c> to its
    ///     nearest project would fail a developer for code the gate has never looked at. The
    ///     mobile and web heads outside the solution are the same case; only
    ///     <see cref="CheckArchitecture" /> is supposed to see those.
    /// </remarks>
    HashSet<AbsolutePath> SolutionProjects() {
        var projects = Solution.AllProjects.Select(project => project.Path).ToHashSet();

        // The instrument, checked before it is trusted: an empty set here would silently turn every
        // narrowed run into a run over nothing, which is the shape that reports success by doing no
        // work.
        Assert.True(projects.Count > 0, $"Read no projects at all out of {Solution.Path}.");

        return projects;
    }

    /// <summary>
    ///     The projects a narrowed target would act on: the owners of what changed, minus whatever
    ///     the solution does not contain.
    /// </summary>
    IReadOnlyList<AbsolutePath> AffectedProjectsSince(string since) {
        var solution = SolutionProjects();
        var owners = ProjectsOwning(ChangedFiles(since));
        var outside = owners.Where(project => !solution.Contains(project)).ToList();

        foreach (var project in outside) {
            Log.Information(
                "{Project} changed but is not in {Solution}, so no target that reads the solution covers it.",
                RootDirectory.GetRelativePathTo(project).ToUnixRelativePath(),
                Solution.Path.Name
            );
        }

        return [.. owners.Where(solution.Contains)];
    }

    Target AffectedProjects => definition => definition
        .Description("Prints the projects a --since run would act on, and runs nothing else")
        .Requires(() => Since)
        .Executes(() => {
                var changed = ChangedFiles(Since);

                Log.Information("{Count} file(s) changed since {Since}.", changed.Count, Since);

                foreach (var project in AffectedProjectsSince(Since)) {
                    Log.Information("  {Project}", RootDirectory.GetRelativePathTo(project).ToUnixRelativePath());
                }
            }
        );
}
