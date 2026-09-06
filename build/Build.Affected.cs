// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.Git;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

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
    AbsolutePath? OwningProject(AbsolutePath file) {
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
    ///     narrowing at all.</b> The mobile and web heads outside the solution are the case this
    ///     exists for: only <see cref="CheckArchitecture" /> is supposed to see those, so a
    ///     <c>--since</c> that mapped a changed file to its nearest project would fail a developer
    ///     for code the gate has never looked at.
    ///     <para>
    ///         ⚠ <b><c>build/_build.csproj</c> used to be the example here and has stopped being
    ///         one.</b> The claim on this remark was that it carries pre-existing <c>IDE1006</c>
    ///         violations and exits 2 — true when written, and #584's answer was to make it a
    ///         workspace <see cref="CheckFormat" /> points at rather than to leave it uncovered. It
    ///         is still outside the solution and so still outside a narrowed run, which now makes it
    ///         the opposite kind of example: something the gate checks and the inner loop does not.
    ///     </para>
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
        .Description("Prints the projects and test projects a --since run would act on, and runs nothing else")
        .Requires(() => Since)
        .Executes(() => {
                var changed = ChangedFiles(Since);

                Log.Information("{Count} file(s) changed since {Since}.", changed.Count, Since);

                foreach (var project in AffectedProjectsSince(Since)) {
                    Log.Information("  changed: {Project}", RootDirectory.GetRelativePathTo(project).ToUnixRelativePath());
                }

                foreach (var project in AffectedTestProjectsSince(Since)) {
                    Log.Information("  test:    {Project}", RootDirectory.GetRelativePathTo(project).ToUnixRelativePath());
                }
            }
        );

    /// <summary>
    ///     Who references whom, inverted: for each project in the solution, the projects that name
    ///     it in a <c>ProjectReference</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Read out of the XML rather than out of Nuke's <see cref="Solution" />, for the reason
    ///         <see cref="CheckArchitecture" /> already gives: the reference list is an item group,
    ///         reading it is one file per project, and evaluating 395 projects through MSBuild to
    ///         learn the same thing costs minutes.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Nearly every reference in this tree is authored with Windows separators
    ///         (<c>..\Vixen.Core\Vixen.Core.csproj</c>) and normalising them here would be dead
    ///         code</b> — that was the first thing sabotaged, and removing the normalisation left
    ///         the closure at 96 test projects for <c>Vixen.Ecs</c>, unchanged, because Nuke's
    ///         <c>AbsolutePath</c> combination already handles the separator on macOS. The edge
    ///         count below is the guard that would actually notice: a graph with no edges is a
    ///         selector that reports success by selecting nothing.
    ///     </para>
    /// </remarks>
    Dictionary<AbsolutePath, List<AbsolutePath>> ReverseReferenceGraph() {
        var graph = new Dictionary<AbsolutePath, List<AbsolutePath>>();
        var edges = 0;

        foreach (var project in SolutionProjects()) {
            if (!project.FileExists()) {
                continue;
            }

            var references = XDocument.Load(project)
                .Descendants("ProjectReference")
                .Select(element => element.Attribute("Include")?.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => AbsolutePath.Create(project.Parent / value!));

            foreach (var reference in references) {
                if (!graph.TryGetValue(reference, out var dependents)) {
                    graph[reference] = dependents = [];
                }

                dependents.Add(project);
                edges++;
            }
        }

        Assert.True(edges > 0, "The reverse ProjectReference graph came out with no edges at all.");

        return graph;
    }

    /// <summary>
    ///     Every project reachable from <paramref name="roots" /> by following
    ///     <c>ProjectReference</c> backwards — the set a change in those roots can break.
    /// </summary>
    IReadOnlyCollection<AbsolutePath> DependentClosure(IEnumerable<AbsolutePath> roots) {
        var graph = ReverseReferenceGraph();
        var reached = new HashSet<AbsolutePath>(roots);
        var pending = new Stack<AbsolutePath>(reached);

        while (pending.Count > 0) {
            if (!graph.TryGetValue(pending.Pop(), out var dependents)) {
                continue;
            }

            foreach (var dependent in dependents.Where(reached.Add)) {
                pending.Push(dependent);
            }
        }

        return reached;
    }

    /// <summary>
    ///     Whether a project is one <see cref="Test" /> would run.
    /// </summary>
    /// <remarks>
    ///     By name, because that is the repository's actual rule and not a guess about it:
    ///     <c>Directory.Build.props</c> sets <c>IsTestProject</c> from
    ///     <c>MSBuildProjectName.EndsWith('.Tests')</c>, so the name is what makes a project a test
    ///     project here, and asking MSBuild would be asking it to re-derive this same condition.
    /// </remarks>
    static bool IsTestProject(AbsolutePath project) =>
        project.NameWithoutExtension.EndsWith(".Tests", StringComparison.Ordinal)
        || project.NameWithoutExtension == "Tests";

    /// <summary>The test projects a change since <paramref name="since" /> can reach.</summary>
    IReadOnlyList<AbsolutePath> AffectedTestProjectsSince(string since) {
        var affected = AffectedProjectsSince(since);

        return affected.Count == 0
            ? []
            : [.. DependentClosure(affected).Where(IsTestProject).OrderBy(project => project.ToString(), StringComparer.Ordinal)];
    }

    /// <summary>
    ///     Runs only the test projects a change can reach.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Measured on this tree: a change in <c>Editor/Vixen.Editor.Water</c> reaches 2 of the
    ///         178 test projects, <c>Raven/Vixen.Raven</c> reaches 20, <c>Core/Vixen.Ui</c> 37, and
    ///         even <c>Core/Vixen.Ecs</c> — which nearly everything depends on — reaches 96. The
    ///         leaf case is the common one and the hub case is still barely half.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This is an inner-loop convenience and must never be the gate</b>, and the reason
    ///         is not policy but arithmetic: a closure over <c>ProjectReference</c> cannot see a
    ///         dependency that is not one. A golden image, a content bundle, an <c>.rvn</c> import
    ///         closure and a test that walks the repository from the root are all invisible to it,
    ///         and every one of those exists here. <see cref="Test" /> keeps running everything, and
    ///         CI keeps running <see cref="Test" />.
    ///     </para>
    ///     <para>
    ///         One project at a time, deliberately. CLAUDE.md § Build and test says to run test
    ///         projects one at a time on a developer machine, and the whole point of a narrowed run
    ///         is to leave the machine usable — a parallel fan-out over 96 assemblies would put back
    ///         exactly what <see cref="Workers" /> was added to take away.
    ///     </para>
    /// </remarks>
    Target AffectedTests => definition => definition
        .Description("Runs the test projects reachable from what changed since --since, one at a time")
        .Requires(() => Since)
        .Executes(() => {
                var projects = AffectedTestProjectsSince(Since);

                Log.Information(
                    "{Count} test project(s) can be reached from what changed since {Since}. ⚠ A "
                    + "ProjectReference closure cannot see a golden image, a content bundle, an .rvn "
                    + "import closure or a test that walks the repository — run Test for those.",
                    projects.Count,
                    Since
                );

                // The Vulkan validation layer's loader path, which .runsettings used to carry and
                // the platform runner cannot read (#560).
                ExportLayerLibraryPath();

                foreach (var project in projects) {
                    Log.Information("Testing {Project}", RootDirectory.GetRelativePathTo(project).ToUnixRelativePath());

                    DotNetTest(settings => settings
                        .SetProjectFile(project)
                        .SetConfiguration(Configuration)
                        .SetResultsDirectory(TestResultsDirectory)
                    );
                }
            }
        );
}
