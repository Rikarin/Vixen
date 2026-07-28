// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Editor.Assets.Content;
using Vixen.Editor.Core;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>
///     Importing and building a project through the calls the editor and the CLI both make.
/// </summary>
/// <remarks>
///     <para>
///         What the build <i>produces</i> is covered next door and by <c>Vixen.Cli.Tests</c>, which
///         drives the same pipeline through a terminal: bundles, catalogs, byte-identical rebuilds.
///         This is about the seam that was added when the orchestration was lifted out of the CLI —
///         diagnostics as values rather than console lines, and progress a bar can be driven from.
///     </para>
///     <para>
///         ⚠ <b>Real directories, because this is the layer that touches a filesystem.</b> Everything
///         above it is already tested without one; the part worth testing here is precisely the part
///         a fake would stub out.
///     </para>
/// </remarks>
public sealed class ContentPipelineTests : IDisposable {
    readonly string root = Path.Combine(
        Path.GetTempPath(),
        "vixen-pipeline-" + Guid.NewGuid().ToString("N")
    );

    public void Dispose() {
        if (Directory.Exists(root)) {
            Directory.Delete(root, recursive: true);
        }
    }

    ProjectWorkspace Workspace(params string[] files) {
        foreach (var file in files) {
            var path = Path.Combine(root, "Assets", file.Replace('/', Path.DirectorySeparatorChar));

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "contents of " + file, Encoding.UTF8);
        }

        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        return new ProjectWorkspace(new ProjectPaths(root));
    }

    [Fact]
    public async Task An_import_reports_what_the_scan_repaired() {
        var workspace = Workspace("Textures/crate.txt");
        List<ContentDiagnostic> said = [];

        await ContentPipeline.ImportAsync(
            workspace,
            "Windows",
            said.Add,
            cancellationToken: TestContext.Current.CancellationToken
        );

        // A file written by hand has no sidecar, and the scan makes one. Reported rather than done
        // quietly, because it is a file appearing in somebody's working tree.
        var created = said.Where(diagnostic => diagnostic.Stage == ContentStage.Scan).ToList();

        Assert.NotEmpty(created);
        Assert.Contains(created, diagnostic => diagnostic.Path == "Assets/Textures/crate.txt");
        Assert.All(created, diagnostic => Assert.Equal(ImportSeverity.Information, diagnostic.Severity));
    }

    [Fact]
    public async Task Every_asset_is_reported_once_and_the_last_one_finishes_the_bar() {
        var workspace = Workspace("a.txt", "b.txt", "c.txt");
        List<ImportProgress> steps = [];

        var summary = await ContentPipeline.ImportAsync(
            workspace,
            "Windows",
            _ => { },
            steps.Add,
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Once each, counted against the summary rather than against a number written here — a
        // progress feed that reported fewer steps than the summary counts is a bar that stops short.
        Assert.Equal(summary.Imported + summary.Cached + summary.Failed, steps.Count);
        Assert.Equal(steps.Count, steps.Select(step => step.Path).Distinct(StringComparer.Ordinal).Count());

        // ⚠ The fraction has to reach one, or a progress bar sits at ninety-something per cent for a
        // task that has finished — which reads as a hang rather than as a rounding choice.
        Assert.Equal(1f, steps[^1].Fraction, 3);
        Assert.True(steps.Select(step => step.Fraction).SequenceEqual(steps.Select(step => step.Fraction).Order()));
    }

    [Fact]
    public async Task Progress_carries_enough_to_say_what_happened_to_an_asset() {
        var workspace = Workspace("a.txt");
        List<ImportProgress> first = [];
        List<ImportProgress> second = [];

        await ContentPipeline.ImportAsync(
            workspace,
            "Windows",
            _ => { },
            first.Add,
            cancellationToken: TestContext.Current.CancellationToken
        );
        await ContentPipeline.ImportAsync(
            workspace,
            "Windows",
            _ => { },
            second.Add,
            cancellationToken: TestContext.Current.CancellationToken
        );

        // The whole reason progress carries the outcome rather than a path and a percentage: the
        // second run did no work, and a caller has to be able to say so.
        Assert.False(first.Single(step => step.Path.EndsWith("a.txt", StringComparison.Ordinal)).Outcome.WasCached);
        Assert.True(second.Single(step => step.Path.EndsWith("a.txt", StringComparison.Ordinal)).Outcome.WasCached);
    }

    [Fact]
    public async Task A_build_of_a_project_with_no_addresses_says_so_rather_than_failing() {
        var workspace = Workspace("a.txt");
        List<ContentDiagnostic> said = [];

        await ContentPipeline.ImportAsync(
            workspace,
            "Windows",
            _ => { },
            cancellationToken: TestContext.Current.CancellationToken
        );

        var built = ContentPipeline.Build(workspace, "Windows", workspace.DefaultOutput("Windows"), said.Add);

        // Succeeded, because a project can legitimately have nothing addressable yet — and said so,
        // because silence would look like success at packing everything.
        Assert.True(built.Succeeded);
        Assert.Equal(0, built.Addresses);

        Assert.Contains(
            said,
            diagnostic => diagnostic.Stage == ContentStage.Plan
                && diagnostic.Severity == ImportSeverity.Information
        );

        Assert.True(File.Exists(Path.Combine(built.OutputDirectory, ContentPipeline.CatalogFileName)));
    }

    [Fact]
    public void A_build_over_a_project_that_was_never_imported_produces_an_empty_catalog() {
        var workspace = Workspace("a.txt");
        List<ContentDiagnostic> said = [];

        var built = ContentPipeline.Build(workspace, "Windows", workspace.DefaultOutput("Windows"), said.Add);

        // ⚠ The trap `ContentPipeline` names: the plan reads the import cache, so a build on its own
        // packs nothing and looks exactly like a build that worked. This test exists so that the day
        // somebody makes Build import for itself, it fails and says which decision changed.
        Assert.True(built.Succeeded);
        Assert.Equal(0, built.Addresses);
        Assert.Equal(0, built.Bundles);
    }

    [Fact]
    public void A_project_directory_is_one_with_assets_in_it() {
        Directory.CreateDirectory(Path.Combine(root, "Assets"));

        Assert.True(ProjectWorkspace.IsProject(root));
        Assert.False(ProjectWorkspace.IsProject(Path.Combine(root, "Assets")));
    }

    [Fact]
    public void A_targets_output_directory_never_contains_a_separator_from_the_targets_name() {
        var workspace = Workspace();

        // "Android/Vulkan" is a target, and a target that became two directories would put a build
        // where nothing looks for it.
        var output = workspace.DefaultOutput("Android/Vulkan");

        Assert.Equal(Path.Combine(workspace.Paths.Build, "Android-Vulkan"), output);
    }
}
