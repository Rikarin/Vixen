// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core.Yaml.Meta;
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

    /// <summary>The catalog resolves less than the import cache does, and says nothing about it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Why the viewport's geometry does not come through here, and the reason is not the
    ///         one three other files give.</b> They say the alternative is "waiting for a content
    ///         build" — <c>ProjectMeshSource</c>, <c>ProjectSurfaceSource</c> and
    ///         <c>EditorWorldRenderer</c> all say some form of it — and that is simply not true of
    ///         <c>LooseContent</c>, which is what this class writes: no build, no packing, no copying,
    ///         and it reads the very same import cache <c>ProjectMeshSource</c> reads. It is also
    ///         sub-asset granular, so "the catalog cannot name one mesh inside a model" is not the
    ///         reason either.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The real reason is that a catalog is what <em>ships</em>, and the editor has to
    ///         draw what is in the project.</b> <c>BuildPlanner.AddressOf</c> gives an excluded asset
    ///         no address, so it gets no catalog entry and an <c>AssetMeshSource</c> over that catalog
    ///         throws <c>ReferenceNotFoundException</c> for it — while <c>ProjectMeshSource</c>, which
    ///         matches on the id in the import record, reads it perfectly well. Exclusion is the
    ///         designed case and not an edge one: <c>AddressableInfo.Excluded</c>'s own remarks call it
    ///         "a reference FBX kept beside the one that ships", and somebody who marked a file that
    ///         way still expects to see it when they open it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it is silent, which is what makes it a defect rather than a trade.</b>
    ///         <see cref="EditorContent.Rebuild" /> returns true and reports nothing above
    ///         informational — the asset is simply not in the catalog. A viewport switched to this
    ///         path would stop drawing a subset of the project with nothing anywhere saying which
    ///         subset or why. The same refusal applies to a sub-asset whose <c>.meta</c> does not name
    ///         it and to two sub-assets whose names collide, both of which refuse the <em>whole</em>
    ///         asset — see <c>BuildPlanner.Chunks</c>.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task An_excluded_asset_is_absent_from_the_catalog_and_still_in_the_import_cache() {
        var project = Project();

        await Import(project, "Textures/crate.txt", "the crate");
        await Import(project, "Reference/backplate.txt", "the backplate");

        Exclude(project, "Reference/backplate.txt");

        using var content = new EditorContent(project);
        var problems = new List<string>();

        var rebuilt = content.Rebuild(diagnostic => {
            if (diagnostic.Severity >= Vixen.Editor.Assets.ImportSeverity.Warning) {
                problems.Add(diagnostic.Message);
            }
        });

        // ⚠ Asserted first and it is the point. A refusal would be a viewport that could say why a
        // mesh had vanished; this succeeds, so it could not.
        Assert.True(rebuilt, content.Refusal);
        Assert.Empty(problems);

        // The address path — what `AssetMeshSource` would take. One of the two assets is reachable.
        Assert.NotNull(content.Assets);
        Assert.True(content.Assets.CanOpen("Assets/Textures/crate.txt"));
        Assert.False(content.Assets.CanOpen("Assets/Reference/backplate.txt"));

        // The import-cache path — what `ProjectMeshSource` takes. Both are there, chunk and all.
        var workspace = new ProjectWorkspace(project.Paths);

        workspace.Cache.TryLoad(workspace.CacheFile);
        workspace.Database.Scan();

        var excluded = Assert.Single(
            workspace.Database.Entries,
            entry => entry.Path.EndsWith("backplate.txt", StringComparison.Ordinal)
        );

        Assert.True(workspace.Cache.TryGet(excluded.Guid, out var record));

        var artifact = Assert.Single(record!.Artifacts);

        Assert.True(workspace.Artifacts.Exists(artifact.Id));
    }

    /// <summary>Marks an already-imported asset as one that does not ship.</summary>
    static void Exclude(EditorProject project, string relative) {
        var absolute = Path.Combine(project.Paths.Assets, relative.Replace('/', Path.DirectorySeparatorChar));
        var meta = AssetMetaFile.PathFor(absolute);

        AssetMetaFile.WriteFile(meta, AssetMetaFile.ReadFile(meta) with { Addressable = new() { Excluded = true } });
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
