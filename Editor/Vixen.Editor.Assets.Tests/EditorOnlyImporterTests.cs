// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.IO;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>Texturing's two recipes are claimed, imported to nothing, and said to be recipes (#1491).</summary>
/// <remarks>
///     ⚠ <b>Asserted against the pipeline's own registry, which every host builds on</b>, so the answer
///     is the editor's, <c>vixen</c>'s and a worker's alike — the reason the importer is here rather than
///     in a feature only the editor links.
/// </remarks>
public sealed class EditorOnlyImporterTests {
    /// <summary>The registry hands each recipe to the editor-only importer, never to the fallback.</summary>
    [Theory]
    [InlineData(".vxtexgraph")]
    [InlineData(".vxlayers")]
    public async Task A_texturing_recipe_imports_to_nothing_and_says_what_ships(string extension) {
        var registry = BuiltInImporters.Create(new ImporterContributions(), new ImporterContributions());
        var path = new VirtualPath("/Assets/Rust" + extension);
        var files = new MemoryFileProvider();

        files.Seed(path, "version: 1\n");

        Assert.True(registry.TryGetForFile(path.ToString(), out var importer));
        Assert.IsType<EditorOnlyImporter>(importer);

        var result = await importer.ImportAsync(
            new ImportContext(AssetId.New(), path, importer.CreateSettings(), files, importer.Name, "Windows"),
            TestContext.Current.CancellationToken
        );

        // ⚠ Not a Blob: the recipe is no asset, and a chunk of it is what every build used to ship.
        Assert.True(result.Succeeded);
        Assert.Empty(result.Artifacts);

        var diagnostic = Assert.Single(result.Diagnostics);

        Assert.Equal(ImportSeverity.Information, diagnostic.Severity);
        Assert.Contains(extension, diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(".vxmat", diagnostic.Message, StringComparison.Ordinal);
    }
}
