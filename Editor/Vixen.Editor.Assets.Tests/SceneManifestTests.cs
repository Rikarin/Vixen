// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Assets;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Mathematics;
using Vixen.Core.Serialization;
using Vixen.Core.Yaml.Meta;
using Vixen.Ecs;
using Vixen.Editor.Assets.Content;
using Vixen.Editor.Core;
using Vixen.Engine.Cameras;
using Vixen.Engine.Scenes;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>
///     The scenes-in-build list, from the file a person edits to the manifest a player boots from.
/// </summary>
/// <remarks>
///     <para>
///         <b>The half of doc 20's B7 that had no reader.</b> The list under <c>ProjectSettings/</c>
///         is project-relative paths because a person merges it; a player has no asset database, so
///         what ships is the addresses those paths resolved to. This is the translation, and the two
///         ways it can be asked to translate something unshippable.
///     </para>
///     <para>
///         ⚠ <b>Both refusals are the point rather than defensiveness.</b> A scene that is packed into
///         no bundle produces a build that succeeds, ships and starts to an empty world — the failure
///         found by a tester rather than by the person who caused it, which is exactly what doc 08
///         says a build-time check is for.
///     </para>
/// </remarks>
public sealed class SceneManifestTests : IDisposable {
    /// <summary>A root with a camera under it, which is the smallest scene that could draw.</summary>
    const string Level = """
                         version: 1
                         name: Level1
                         roots:
                           - id: 0123456789abcdef0123456789abcdef
                             name: Root
                             position: 1 2 3
                             children:
                               - id: 11111111111111111111111111111111
                                 name: Camera
                                 position: 0 5 -10
                                 components:
                                   - !Camera
                                     fieldOfView: 1.2
                         """;

    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-scenes-" + Guid.NewGuid().ToString("N"));

    public void Dispose() {
        if (Directory.Exists(root)) {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task The_listed_scenes_reach_the_player_as_addresses_in_the_listed_order() {
        Scene("Scenes/Menu.vxscene", "scenes/menu");
        Scene("Scenes/Level1.vxscene", "scenes/level-1");

        var workspace = Listing("Assets/Scenes/Level1.vxscene", "Assets/Scenes/Menu.vxscene");
        var manifest = await BuildAndRead(workspace);

        // The list's order and not the catalog's, which sorts by address — "the first entry is what
        // the game opens with" is the whole content of this file, so a build that reordered it would
        // silently change which level a player starts in.
        Assert.Equal(["scenes/level-1", "scenes/menu"], manifest.Scenes);
        Assert.Equal(SceneManifest.Current, manifest.Version);
    }

    /// <summary>
    ///     A project that lists no scenes still ships a manifest, and it is empty. A build that wrote
    ///     nothing would leave the previous run's file in the output directory, and the player would
    ///     go on opening a level nobody had listed for it.
    /// </summary>
    [Fact]
    public async Task A_project_with_no_scenes_in_the_build_ships_an_empty_manifest() {
        Scene("Scenes/Menu.vxscene", "scenes/menu");

        var manifest = await BuildAndRead(Listing());

        Assert.Empty(manifest.Scenes);
    }

    [Fact]
    public async Task A_scene_in_the_build_with_no_address_refuses_the_build_and_says_which() {
        Scene("Scenes/Level1.vxscene", address: null);

        var workspace = Listing("Assets/Scenes/Level1.vxscene");
        var (built, said) = await Build(workspace);

        Assert.False(built.Succeeded);

        var refusal = Assert.Single(
            said,
            diagnostic => diagnostic.Severity == ImportSeverity.Error
                && diagnostic.Path == "Assets/Scenes/Level1.vxscene"
        );

        Assert.Contains("no address", refusal.Message, StringComparison.Ordinal);

        // And nothing was written, so a refused build cannot be mistaken for one that produced a
        // player — the output directory is what a publish copies.
        Assert.False(
            File.Exists(Path.Combine(workspace.DefaultOutput("Windows"), ContentPipeline.SceneManifestFileName))
        );
    }

    /// <summary>
    ///     ⚠ Somebody else's rename or delete arriving in a checkout, which is the failure this list
    ///     actually has. It refuses rather than warning because the alternative is a player that opens
    ///     the <i>second</i> scene in the list and looks like a bug in the game.
    /// </summary>
    [Fact]
    public async Task A_scene_in_the_build_that_names_nothing_refuses_the_build() {
        Scene("Scenes/Menu.vxscene", "scenes/menu");

        var (built, said) = await Build(Listing("Assets/Scenes/Gone.vxscene", "Assets/Scenes/Menu.vxscene"));

        Assert.False(built.Succeeded);

        Assert.Contains(
            said,
            diagnostic => diagnostic.Severity == ImportSeverity.Error
                && diagnostic.Path == "Assets/Scenes/Gone.vxscene"
                && diagnostic.Message.Contains("not in this project", StringComparison.Ordinal)
        );
    }

    /// <summary>
    ///     ⚠ Every bad entry is reported, and then it refuses once. A build that stopped at the first
    ///     would make fixing a stale list a sequence of builds, each a full import long.
    /// </summary>
    [Fact]
    public async Task Every_unshippable_entry_is_named_rather_than_only_the_first() {
        var (_, said) = await Build(Listing("Assets/Scenes/Gone.vxscene", "Assets/Scenes/AlsoGone.vxscene"));

        Assert.Equal(2, said.Count(diagnostic => diagnostic.Severity == ImportSeverity.Error));
    }

    /// <summary>
    ///     ⚠ <b>The whole chain in one test, because every link of it was built separately and none
    ///     of them had ever been asked to agree.</b> An authored <c>.vxscene</c> is compiled, packed
    ///     into a bundle, named by the catalog, listed in the manifest — and then the address the
    ///     manifest holds is loaded back out of that build and turned into the world the file
    ///     described. A suite that stopped at "the manifest holds the right string" would pass with a
    ///     build that shipped no bundle for it.
    /// </summary>
    [Fact]
    public async Task The_address_in_the_manifest_loads_the_scene_the_file_described() {
        Scene("Scenes/Level1.vxscene", "scenes/level-1");

        var workspace = Listing("Assets/Scenes/Level1.vxscene");
        var (built, said) = await Build(workspace);

        Assert.True(built.Succeeded, string.Join(Environment.NewLine, said.Select(diagnostic => diagnostic.Message)));

        var output = built.OutputDirectory;

        var manifest = Serializer.Read<SceneManifest>(
            File.ReadAllBytes(Path.Combine(output, ContentPipeline.SceneManifestFileName))
        );

        // Read the way a player reads it: the catalog beside the manifest, over the bundles beside
        // both, with nothing of the project's Library/ in reach.
        var files = new VirtualFileSystem();
        files.Mount(new("/content"), new PhysicalFileProvider(output, isReadOnly: true));

        var catalog = CatalogFormat.Read(File.ReadAllBytes(Path.Combine(output, ContentPipeline.CatalogFileName)));
        var assets = new AssetManager(catalog, new LocalBundleSource(files, new("/content")));

        var asset = assets.Load<SceneAsset>(Assert.Single(manifest.Scenes), TestContext.Current.CancellationToken).Result;

        using var world = new World();
        var scenes = new SceneManager(world);
        var created = new Entity[2];
        var scene = asset.Load(scenes, created);

        Assert.Equal("Level1", scenes.NameOf(scene));
        Assert.Equal(2, scenes.CountIn(scene));
        Assert.Equal(created[0], Hierarchy.ParentOf(world, created[1]));

        // The transform and the component both came through, which is what makes it a scene rather
        // than a count of entities: a level that loads with no camera draws nothing.
        Assert.Equal(new Vector3(1, 2, 3), world.Read<LocalTransform>(created[0]).Position);
        Assert.Equal(new Vector3(0, 5, -10), world.Read<LocalTransform>(created[1]).Position);
        Assert.Equal(1.2f, world.Read<Camera>(created[1]).FieldOfView, 4);
    }

    /// <summary>Writes a <c>.vxscene</c>, with an address on its sidecar when it is meant to ship.</summary>
    void Scene(string relativePath, string? address = null) {
        var absolute = Path.Combine(root, "Assets", relativePath.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, Level);

        if (address is null) {
            return;
        }

        AssetMetaFile.WriteFile(
            AssetMetaFile.PathFor(absolute),
            new() { Guid = AssetId.New(), Addressable = new() { Address = address } }
        );
    }

    /// <summary>Opens the workspace with the project's Build Settings naming these scenes.</summary>
    /// <remarks>
    ///     Written as the file rather than poked into the store, because the file is what the build
    ///     reads: an editor holding an unsaved edit and a build reading the disk is the disagreement
    ///     <c>ProjectWorkspace.Settings</c> is arranged to make impossible.
    /// </remarks>
    ProjectWorkspace Listing(params string[] scenes) {
        Directory.CreateDirectory(Path.Combine(root, "Assets"));

        var paths = new ProjectPaths(root);
        var settings = new ProjectSettingsStore(paths);

        settings.Get<PlayerBuildSettings>().Scenes.AddRange(scenes);
        settings.Save<PlayerBuildSettings>();

        return new(paths);
    }

    static async Task<(ContentBuildSummary Built, List<ContentDiagnostic> Said)> Build(ProjectWorkspace workspace) {
        List<ContentDiagnostic> said = [];

        await ContentPipeline.ImportAsync(
            workspace,
            "Windows",
            _ => { },
            cancellationToken: TestContext.Current.CancellationToken
        );

        var built = ContentPipeline.Build(workspace, "Windows", workspace.DefaultOutput("Windows"), said.Add);

        return (built, said);
    }

    static async Task<SceneManifest> BuildAndRead(ProjectWorkspace workspace) {
        var (built, said) = await Build(workspace);

        Assert.True(built.Succeeded, string.Join(Environment.NewLine, said.Select(diagnostic => diagnostic.Message)));

        return Serializer.Read<SceneManifest>(
            File.ReadAllBytes(Path.Combine(built.OutputDirectory, ContentPipeline.SceneManifestFileName))
        );
    }
}
