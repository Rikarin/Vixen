// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Assets;
using Vixen.Core.IO;
using Vixen.Core.Serialization;
using Vixen.Core.Serialization.Storage;
using Vixen.Editor.Assets.Content;
using Vixen.Editor.Core;
using Vixen.Engine.Scenes;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>
///     Doc 17's Editor variant: a catalog over what the import produced, with nothing packed.
/// </summary>
/// <remarks>
///     <para>
///         <b>The convergence point the address system and the virtual file system have.</b> They are
///         otherwise separate — an address is a key in a catalog and resolves to a content-addressed
///         chunk, never to a path — and this is the one place that matters: the chunks stay in the
///         <c>Library/</c> the import wrote them to, and the catalog names no bundle at all.
///     </para>
///     <para>
///         ⚠ <b>The same planner decides the addresses here and in a shipped build</b>, which is the
///         property that makes testing against this worth anything. A mode that resolved an address
///         differently from the thing it stands in for would be a mode that hides exactly the bugs it
///         exists to find.
///     </para>
/// </remarks>
public sealed class LooseContentTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-loose-" + Guid.NewGuid().ToString("N"));

    public void Dispose() {
        if (Directory.Exists(root)) {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task An_address_resolves_to_a_chunk_in_the_artefact_store_with_nothing_packed() {
        var workspace = await Imported("Textures/crate.txt", "the crate");

        List<ContentDiagnostic> said = [];
        var written = LooseContent.Write(workspace, said.Add);

        Assert.True(written.Succeeded, string.Join(Environment.NewLine, said.Select(diagnostic => diagnostic.Message)));
        Assert.Equal(1, written.Addresses);
        Assert.Equal(workspace.Paths.Library, written.Directory);

        // Nothing was packed, which is the whole point: making a change visible costs the import of
        // the one asset that changed and no pack of anything.
        Assert.Empty(Directory.GetFiles(written.Directory, "*.bundle"));

        var catalog = CatalogFormat.Read(
            File.ReadAllBytes(Path.Combine(written.Directory, ContentPipeline.CatalogFileName))
        );

        Assert.True(catalog.TryGet("Assets/Textures/crate.txt", out var entry));

        // ⚠ An empty bundle name is what makes it loose, and it is a state the format and the runtime
        // both already understood — `AssetManager.MountFor` returns without mounting anything for it.
        Assert.Empty(entry.Bundle);
        Assert.Empty(catalog.Bundles);

        // And the chunk it names is really in the store the import wrote, read the way a player reads
        // it: through the virtual file system, with nothing of the project's own machinery in reach.
        var files = new VirtualFileSystem();
        files.Mount(new("/library"), new PhysicalFileProvider(workspace.Paths.Library, isReadOnly: true));

        var database = new ObjectDatabase(
            new FileOdbBackend(files, new("/library/" + LooseContent.ArtifactFolderName), isReadOnly: true)
        );

        var assets = new AssetManager(catalog, new LocalBundleSource(files, new("/library")), database);

        // Opened rather than loaded, because a .txt is the raw importer's and its chunk is a blob
        // somebody streams rather than an object with a contract. It is the same resolution either
        // way — address to chunk id to the store that holds it — which is what is under test.
        using var stream = assets.Open("Assets/Textures/crate.txt", TestContext.Current.CancellationToken);
        using var reader = new StreamReader(stream);

        Assert.Equal("the crate", reader.ReadToEnd());
    }

    /// <summary>
    ///     ⚠ The scenes-in-build manifest is written here too, so a player pointed at a project opens
    ///     the level the Build Settings list names. Without it this mode would boot into an empty
    ///     world, which is the one difference from a shipped build nobody would think to look for.
    /// </summary>
    [Fact]
    public async Task The_scene_manifest_is_written_beside_it() {
        var workspace = await Imported("Scenes/Level1.vxscene", Level);

        var settings = new ProjectSettingsStore(workspace.Paths);

        settings.Get<PlayerBuildSettings>().Scenes.Add("Assets/Scenes/Level1.vxscene");
        settings.Save<PlayerBuildSettings>();

        // Written through a second store and read through the workspace's own, which is the seam a
        // build actually crosses: the file is what a build reads, not an object somebody is holding.
        var reopened = new ProjectWorkspace(workspace.Paths);
        reopened.Cache.TryLoad(reopened.CacheFile);
        reopened.Database.Scan();

        var written = LooseContent.Write(reopened, _ => { });

        Assert.True(written.Succeeded);

        var manifest = Serializer.Read<SceneManifest>(
            File.ReadAllBytes(Path.Combine(written.Directory, ContentPipeline.SceneManifestFileName))
        );

        Assert.Equal(["Assets/Scenes/Level1.vxscene"], manifest.Scenes);
    }

    /// <summary>The smallest thing that compiles, so the scene case has a real chunk behind it.</summary>
    const string Level = """
                         version: 1
                         name: Level1
                         roots:
                           - id: 0123456789abcdef0123456789abcdef
                             name: Root
                         """;

    async Task<ProjectWorkspace> Imported(string relativePath, string contents) {
        var absolute = Path.Combine(root, "Assets", relativePath.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, contents, Encoding.UTF8);

        var workspace = new ProjectWorkspace(new ProjectPaths(root));

        await ContentPipeline.ImportAsync(
            workspace,
            ProjectWorkspace.HostTarget,
            _ => { },
            cancellationToken: TestContext.Current.CancellationToken
        );

        return workspace;
    }
}
