// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Vixen.DocGen;

/// <summary>One project, compiled, with everything the reader needs to know about it.</summary>
/// <param name="Name">The project's name.</param>
/// <param name="Compilation">Its compilation, generators already run.</param>
/// <param name="Area">Top-level folder — <c>Core</c>, <c>Editor</c>, <c>Tools</c>, …</param>
/// <param name="IsPackable">Whether it carries a <c>PublicAPI.*.txt</c>.</param>
/// <param name="GeneratedDocuments">How many source-generated documents it has.</param>
/// <param name="Errors">Compile errors, which are fatal unless the project is excused.</param>
sealed record LoadedProject(
    string Name,
    Compilation Compilation,
    string Area,
    bool IsPackable,
    int GeneratedDocuments,
    IReadOnlyList<Diagnostic> Errors
);

/// <summary>Opens `Vixen.slnx` and hands back compiled projects — docs/plan/25 § 3.1 and § 3.2.</summary>
sealed class SolutionLoader(string solutionPath, string configuration, Action<string> log) {
    /// <summary>
    ///     Workspace diagnostics that are expected, exactly as measured by the spike, and the reason
    ///     each is tolerated. Anything else fails the load.
    /// </summary>
    /// <remarks>
    ///     ⚠ The list is deliberately of *shapes*, not a count. A load that starts dropping projects
    ///     must not be able to look like a clean run, which is what a "26 warnings are fine"
    ///     threshold would have made it.
    /// </remarks>
    static readonly (string Fragment, string Reason)[] ExpectedFailures = [
        ("Duplicate source file", "the analyzer projects list AnalyzerReleases.*.md as AdditionalFiles and the SDK adds them again")
    ];

    public async Task<IReadOnlyList<LoadedProject>> LoadAsync(CancellationToken cancellationToken) {
        var unexpected = new ConcurrentBag<string>();

        // § 3.2 / RESULT.md § F1. The configuration is not cosmetic: this repository resolves its own
        // generators through ProjectReference with OutputItemType=Analyzer, so the analyzer paths
        // point at bin/<Configuration>/…. Against a tree built in another configuration the files do
        // not exist, the generators never run, and the graph silently loses everything they emit —
        // measured at 298 types and four kinds.
        using var workspace = MSBuildWorkspace.Create(new Dictionary<string, string> {
            ["Configuration"] = configuration
        });

        workspace.SkipUnrecognizedProjects = true;

        using var handler = workspace.RegisterWorkspaceFailedHandler(failure => {
            var message = failure.Diagnostic.Message;

            if (!ExpectedFailures.Any(expected =>
                message.Contains(expected.Fragment, StringComparison.Ordinal))) {
                unexpected.Add(message);
            }
        });

        log($"Opening {Path.GetFileName(solutionPath)} with Configuration={configuration}");

        var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: cancellationToken);

        if (!unexpected.IsEmpty) {
            throw new DocGenException(
                $"The workspace reported {unexpected.Count} unrecognised failures. The first is:{Environment.NewLine}"
                + $"  {unexpected.First()}{Environment.NewLine}"
                + "A load that drops projects produces a graph that describes an engine which does not exist."
            );
        }

        var root = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        var packable = PackableAssemblies(root);
        var projects = new List<LoadedProject>();

        foreach (var project in solution.Projects
            .Where(candidate => candidate.Language == LanguageNames.CSharp)
            .OrderBy(candidate => candidate.Name, StringComparer.Ordinal)) {
            var compilation = await project.GetCompilationAsync(cancellationToken)
                // RESULT.md § F2: null here means the C# language service is missing from the MEF
                // composition, which is a packaging mistake rather than a property of the project —
                // and one that otherwise produces an empty graph from a green build.
                ?? throw new DocGenException(
                    $"{project.Name} produced no compilation. Microsoft.CodeAnalysis.CSharp.Workspaces "
                    + "is what makes Project.SupportsCompilation true."
                );

            // § 3.2: the workspace has already run the generators and their trees are in the
            // compilation. Asserting that is the point — a generator that stopped running looks
            // exactly like a feature that was deleted.
            var generated = await project.GetSourceGeneratedDocumentsAsync(cancellationToken);
            var generatedCount = 0;

            foreach (var document in generated) {
                var tree = await document.GetSyntaxTreeAsync(cancellationToken);

                if (tree is not null && !compilation.ContainsSyntaxTree(tree)) {
                    compilation = compilation.AddSyntaxTrees(tree);
                }

                generatedCount++;
            }

            projects.Add(new LoadedProject(
                project.Name,
                compilation,
                AreaOf(root, project.FilePath),
                project.AssemblyName is { } name && packable.Contains(name),
                generatedCount,
                [.. compilation.GetDiagnostics(cancellationToken)
                    .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)]
            ));
        }

        return projects;
    }

    /// <summary>The assemblies `CheckApi` gates — the ones with a baseline beside the project.</summary>
    static HashSet<string> PackableAssemblies(string root) => [
        .. Directory.EnumerateFiles(root, "PublicAPI.Unshipped.txt", SearchOption.AllDirectories)
            .Select(path => Path.GetFileName(Path.GetDirectoryName(path))!)
    ];

    /// <summary>Top-level folder the project sits in, which is how the site groups the API tree.</summary>
    /// <remarks>
    ///     Area rather than packability, because <c>graph-node</c>, <c>importer</c> and
    ///     <c>replicated-component</c> live in assemblies with no baseline — a tree filtered on
    ///     `PublicAPI.*.txt` would document the engine and hide the editor (§ 6.3).
    /// </remarks>
    static string AreaOf(string root, string? projectPath) {
        if (projectPath is null) {
            return "Unknown";
        }

        var relative = Path.GetRelativePath(root, projectPath).Replace('\\', '/');
        var separator = relative.IndexOf('/');

        return separator < 0 ? "Unknown" : relative[..separator];
    }
}

/// <summary>A failure the tool states rather than a stack trace it leaks.</summary>
sealed class DocGenException(string message) : Exception(message);
