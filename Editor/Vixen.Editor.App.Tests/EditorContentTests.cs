// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Editor.Assets.Content;
using Vixen.Editor.Core;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The project's own content, opened the way a player opens a build.</summary>
/// <remarks>
///     ⚠ <b>The step between an import and a viewport that can draw what was imported, and nothing
///     took it.</b> <c>LooseContent</c> writes a catalog over the artefacts an import already left in
///     <c>Library/</c> and <c>LooseContentSource</c> turns that into an <c>AssetManager</c>; both were
///     built for this and the editor called neither, so nothing in it could resolve a mesh, a
///     material or a texture by address. What is asserted here is a real address resolving to the
///     bytes a real import wrote.
/// </remarks>
public sealed class EditorContentTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-content-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() {
        try {
            if (Directory.Exists(root)) {
                Directory.Delete(root, recursive: true);
            }
        } catch (IOException) {
            // A store the test wrote and the OS has not let go of. Not what is under test.
        }
    }

    EditorProject Project() {
        Directory.CreateDirectory(Path.Combine(root, "Assets"));

        return new(new ProjectPaths(root));
    }

    /// <summary>An imported asset is resolvable by its address, through the same catalog a build uses.</summary>
    [Fact]
    public async Task An_imported_asset_resolves_by_address() {
        var project = Project();

        await Import(project, "Textures/crate.txt", "the crate");

        using var content = new EditorContent(project);

        Assert.True(content.Rebuild(), content.Refusal);
        Assert.NotNull(content.Assets);

        using var stream = content.Assets.Open("Assets/Textures/crate.txt", TestContext.Current.CancellationToken);
        using var reader = new StreamReader(stream);

        Assert.Equal("the crate", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>An asset imported after the mount was opened is found once the catalog is rewritten.</summary>
    /// <remarks>
    ///     ⚠ <b>The failure a live-looking mount hides.</b> A catalog is a snapshot of what the plan
    ///     resolved, so without a rewrite the asset somebody has just imported is missing — from one
    ///     material, silently, until the editor is restarted.
    /// </remarks>
    [Fact]
    public async Task An_asset_imported_afterwards_is_found_once_the_catalog_is_rewritten() {
        var project = Project();

        await Import(project, "Textures/crate.txt", "the crate");

        using var content = new EditorContent(project);

        Assert.True(content.Rebuild(), content.Refusal);

        await Import(project, "Textures/barrel.txt", "the barrel");

        Assert.True(content.Rebuild(), content.Refusal);

        using var stream = content.Assets!.Open("Assets/Textures/barrel.txt", TestContext.Current.CancellationToken);
        using var reader = new StreamReader(stream);

        Assert.Equal("the barrel", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>A project that has never been imported is a refusal rather than an exception.</summary>
    /// <remarks>
    ///     ⚠ <b>It is the state every new project is in.</b> An editor that would not open one is an
    ///     editor nobody can start a project with, so the viewport draws no meshes and says why.
    /// </remarks>
    [Fact]
    public void A_project_with_no_import_says_so_and_still_opens() {
        using var content = new EditorContent(Project());

        Assert.Null(content.Assets);
        Assert.NotNull(content.Refusal);
        Assert.Contains("Import", content.Refusal, StringComparison.OrdinalIgnoreCase);
    }

    static async Task Import(EditorProject project, string relative, string contents) {
        var absolute = Path.Combine(project.Paths.Assets, relative.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        await File.WriteAllTextAsync(absolute, contents, Encoding.UTF8, TestContext.Current.CancellationToken);

        var workspace = new ProjectWorkspace(project.Paths);

        await ContentPipeline.ImportAsync(
            workspace,
            ProjectWorkspace.HostTarget,
            _ => { },
            cancellationToken: TestContext.Current.CancellationToken
        );
    }
}
