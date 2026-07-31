// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Assets;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Mathematics;
using Vixen.Core.Serialization;
using Vixen.Core.Serialization.Storage;
using Vixen.Engine.Scenes;
using Vixen.Platform.Headless;
using Xunit;

namespace Vixen.App.Tests;

/// <summary>
///     The boot end of the editor's scenes-in-build list: a content build that shipped a scene, and a
///     host that opens it.
/// </summary>
/// <remarks>
///     <para>
///         <b>The gap this closes.</b> <c>SceneCompiler</c> has been compiling <c>.vxscene</c> files
///         into loadable chunks and the Build Settings panel has been collecting a list of them, and
///         nothing in the boot path opened either — so a published player started to an empty world
///         whatever the project said. Every piece existed; the wire between them did not.
///     </para>
///     <para>
///         ⚠ <b>Over a real content build in a real directory</b>, for <c>ContentMountTests</c>'
///         reason: what is under test is whether the boot path finds and opens what a build wrote, and
///         a fake catalog would agree with any answer.
///     </para>
/// </remarks>
public sealed class StartupSceneTests : IDisposable {
    readonly TemporaryFileSystemHost files = new();

    public void Dispose() => files.Dispose();

    [Fact]
    public void TheFirstSceneAContentBuildShippedIsOpenedIntoTheWorld() {
        Publish(["scenes/level-1", "scenes/menu"]);

        using var application = Build(new SilentGame());

        application.Initialise();

        var scenes = Assert.IsType<SceneManager>(application.Services.Scenes);

        Assert.Equal("scenes/level-1", application.Services.Config.StartupScene);
        Assert.True(application.StartupScene.IsValid);
        Assert.Equal("level-1", scenes.NameOf(application.StartupScene));

        // The entities are in the world before OnInitialise, which is what makes the hook able to
        // find the level's camera or parent something to its player.
        Assert.Equal(2, scenes.CountIn(application.StartupScene));
        Assert.Equal(2, application.Services.Engine!.World.EntityCount);
    }

    /// <summary>
    ///     ⚠ A game that names a scene is not overridden by what the project listed. "The level this
    ///     executable is for" is a stronger statement than "the first one somebody put in the list",
    ///     and a head with its own menu scene would otherwise be unable to keep it.
    /// </summary>
    [Fact]
    public void AGameThatNamesItsOwnSceneKeepsIt() {
        Publish(["scenes/level-1", "scenes/menu"]);

        using var application = Build(new ChoosingGame("scenes/menu"));

        application.Initialise();

        Assert.Equal("scenes/menu", application.Services.Config.StartupScene);
        Assert.Equal("menu", application.Services.Scenes!.NameOf(application.StartupScene));
    }

    /// <summary>
    ///     ⚠ And an operator can say it on the command line without a rebuild, which is what makes
    ///     "it only reproduces on the third map" something a tester can hand over.
    /// </summary>
    [Fact]
    public void AnOperatorCanNameOneOnTheCommandLine() {
        Publish(["scenes/level-1", "scenes/menu"]);

        using var application = Build(new SilentGame(), "--vixen-scene", "scenes/menu");

        application.Initialise();

        Assert.Equal("menu", application.Services.Scenes!.NameOf(application.StartupScene));
    }

    /// <summary>
    ///     A sample that draws a triangle, a batch tool and a test each open no scene. Nothing is
    ///     loaded and nothing is said, because a line per run about something nobody asked for is a
    ///     line that trains people to skim the log.
    /// </summary>
    [Fact]
    public void AnApplicationThatShippedNoManifestOpensNothing() {
        using var application = Build(new SilentGame());

        application.Initialise();

        Assert.Null(application.Services.Config.StartupScene);
        Assert.False(application.StartupScene.IsValid);
        Assert.Empty(application.Services.Content.Scenes);
        Assert.Contains(ContentMount.SceneManifestFileName, application.Services.Content.SceneReason, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ An address that names nothing leaves the game running with an empty world and the reason
    ///     in the log, for the reason a broken compositor falls back rather than throwing: the thing
    ///     that would show a player the message is the thing that did not start.
    /// </summary>
    [Fact]
    public void ASceneThatCannotBeLoadedIsReportedRatherThanFatal() {
        Publish(["scenes/level-1"]);

        using var application = Build(new ChoosingGame("scenes/nowhere"));

        application.Initialise();

        Assert.False(application.StartupScene.IsValid);
        Assert.Equal(0, application.Services.Engine!.World.EntityCount);

        Assert.Contains(
            application.Services.Logs.Snapshot(),
            entry => entry.EventId == 13019 && entry.Message.Contains("scenes/nowhere", StringComparison.Ordinal)
        );
    }

    /// <summary>
    ///     ⚠ A manifest written by a newer build is refused rather than half-read: a later format may
    ///     mean something different by the order of the list, and opening the wrong level is worse
    ///     than opening none and saying so.
    /// </summary>
    [Fact]
    public void AManifestFromANewerBuildIsRefusedWithItsVersion() {
        Publish([]);

        File.WriteAllBytes(
            Path.Combine(files.ApplicationDirectory, ContentMount.FolderName, ContentMount.SceneManifestFileName),
            Serializer.ToBytes(new SceneManifest { Version = SceneManifest.Current + 1, Scenes = ["scenes/level-1"] })
        );

        using var application = Build(new SilentGame());

        Assert.Empty(application.Services.Content.Scenes);
        Assert.Null(application.Services.Config.StartupScene);
        Assert.NotNull(application.Services.Content.SceneReason);
    }

    /// <summary>A head with no engine has no world to open a scene into, and says so rather than
    /// pretending it did.</summary>
    [Fact]
    public void AHeadWithNoEngineHasNoSceneManagerAndSaysWhy() {
        Publish(["scenes/level-1"]);

        using var application = Build(new NoEngineGame());

        application.Initialise();

        Assert.Null(application.Services.Scenes);
        Assert.False(application.StartupScene.IsValid);

        Assert.Contains(
            application.Services.Logs.Snapshot(),
            entry => entry.EventId == 13019 && entry.Message.Contains("no world", StringComparison.Ordinal)
        );
    }

    /// <summary>
    ///     ⚠ <b>Doc 17's Editor variant, end to end.</b> A catalog whose entries name no bundle, an
    ///     artefact store beside it, and a host that opens the startup scene out of it — which is the
    ///     iteration loop the whole loose-content path exists for: change one asset, re-import it,
    ///     and the running player sees it without anything being packed.
    /// </summary>
    [Fact]
    public void AnUnpackedProjectLibraryIsOpenedLikeAnyOtherContent() {
        var library = Directory.CreateDirectory(Path.Combine(files.ApplicationDirectory, "..", "Library")).FullName;
        var store = Directory.CreateDirectory(Path.Combine(library, ContentMount.ArtifactFolderName)).FullName;

        var scratch = new VirtualFileSystem();
        scratch.Mount(new("/odb"), new PhysicalFileProvider(store));

        var id = new ObjectDatabase(new FileOdbBackend(scratch, new("/odb"))).Write(Scene("level-1"));

        // No bundle name, and no bundles at all — which is the whole of what makes it unpacked.
        var catalog = new ContentCatalog(
            CatalogFormat.Version,
            default,
            "Windows",
            [new("Assets/Scenes/Level1.vxscene", id, "", ContentProvider.Local, [], [], 0)],
            []
        );

        File.WriteAllBytes(Path.Combine(library, ContentMount.CatalogFileName), CatalogFormat.Write(catalog));

        File.WriteAllBytes(
            Path.Combine(library, ContentMount.SceneManifestFileName),
            Serializer.ToBytes(new SceneManifest { Scenes = ["Assets/Scenes/Level1.vxscene"] })
        );

        using var application = Build(new SilentGame(), "--vixen-loose-content", library);

        application.Initialise();

        Assert.True(application.Services.Content.IsUnpacked);
        Assert.True(application.Services.Content.IsLoose);
        Assert.Equal(2, application.Services.Scenes!.CountIn(application.StartupScene));
    }

    VixenApplication Build(Game game, params string[] arguments) =>
        VixenApp.Create(["--vixen-workers", "1", "--vixen-frame-limit", "0", .. arguments])
            .WithPlatform(new HeadlessPlatform(new() { FileSystem = files }))
            .Build(game);

    /// <summary>
    ///     Writes a content build the way <c>vixen content build</c> lays one out: a bundle of scene
    ///     chunks, the catalog that names them, and the manifest saying which of them ship and in what
    ///     order.
    /// </summary>
    void Publish(IReadOnlyList<string> addresses) {
        var directory = Directory
            .CreateDirectory(Path.Combine(files.ApplicationDirectory, ContentMount.FolderName))
            .FullName;

        var scratch = new VirtualFileSystem();
        scratch.Mount(new("/odb"), new MemoryFileProvider());

        var backend = new FileOdbBackend(scratch, new("/odb"));
        var database = new ObjectDatabase(backend);
        var entries = new List<CatalogEntry>();

        foreach (var address in addresses) {
            var id = database.Write(Scene(address.Split('/')[^1]));
            entries.Add(new(address, id, "Main", ContentProvider.Local, [], [], 0));
        }

        var bundle = new BundleWriter();
        bundle.AddAll(backend);
        File.WriteAllBytes(Path.Combine(directory, "Main.bundle"), bundle.Build());

        var catalog = new ContentCatalog(
            CatalogFormat.Version,
            default,
            "Windows",
            [.. entries],
            [new("Main", "", default, 0, 0, CompressionMethod.Lz4, [])]
        );

        File.WriteAllBytes(Path.Combine(directory, ContentMount.CatalogFileName), CatalogFormat.Write(catalog));

        File.WriteAllBytes(
            Path.Combine(directory, ContentMount.SceneManifestFileName),
            Serializer.ToBytes(new SceneManifest { Scenes = [.. addresses] })
        );
    }

    /// <summary>Two entities, a root and a child, which is enough for a load to be observable.</summary>
    static SceneAsset Scene(string name) =>
        new() {
            Name = name,
            Content = new() {
                Count = 2,
                Parents = [-1, 0],
                Positions = [Vector3.Zero, Vector3.UnitY],
                Rotations = [Quaternion.Identity, Quaternion.Identity],
                Scales = [Vector3.One, Vector3.One],
                Names = ["Root", "Child"],
                Ids = [Guid.Empty, Guid.Empty],
                Blocks = [new() { Entities = [0, 1] }]
            }
        };

    sealed class SilentGame : Game;

    sealed class ChoosingGame(string address) : Game {
        protected internal override void OnConfigure(AppConfig config) => config.StartupScene = address;
    }

    sealed class NoEngineGame : Game {
        protected internal override void OnConfigure(AppConfig config) => config.UseEngine = false;
    }
}
