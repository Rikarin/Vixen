// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Text;
using Vixen.Assets;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Serialization.Storage;
using Vixen.Editor.Assets.Content;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>
///     The last step of a content build: which file each chunk lives in, what that file is called,
///     and what a runtime is told so it can find it again.
/// </summary>
public sealed class ContentBuilderTests {
    [Fact]
    public void PackTogetherPutsAGroupInOneBundle() {
        var built = Build(
            [Group("UiCore")],
            [Asset("ui/hero", "UiCore"), Asset("ui/villain", "UiCore"), Asset("ui/shader", "UiCore")]
        );

        var bundle = Assert.Single(built.Bundles);
        Assert.Equal("UiCore", bundle.Name);
        Assert.Equal(3, built.Catalog.Count);
        Assert.All(built.Catalog.Entries, entry => Assert.Equal("UiCore", entry.Bundle));
    }

    /// <summary>
    ///     A planned reference reaches the catalog, so the thing a game holds resolves to the thing it
    ///     loads. The last link in the chain: plan, pack, and ask the catalog what the id means.
    /// </summary>
    [Fact]
    public void AReferenceSurvivesIntoTheCatalog() {
        var hero = new AssetReference(new AssetId(new("11111111-1111-1111-1111-111111111111")));

        var built = Build([Group("UiCore")], [Asset("ui/hero", "UiCore", reference: hero)]);

        Assert.True(built.Catalog.TryGetAddress(hero, out var address));
        Assert.Equal("ui/hero", address);
    }

    /// <summary>
    ///     One bundle each, so a patch ships only what changed. The names are hashed rather than
    ///     taken from the address, because an address contains slashes and a bundle name becomes a
    ///     file name.
    /// </summary>
    [Fact]
    public void PackSeparatelyGivesEachAssetItsOwnBundle() {
        var built = Build(
            [Group("UiCore", packing: BundlePacking.PackSeparately)],
            [Asset("ui/hero", "UiCore"), Asset("ui/villain", "UiCore")]
        );

        Assert.Equal(2, built.Bundles.Length);
        Assert.All(built.Bundles, bundle => Assert.DoesNotContain('/', bundle.FileName));
        Assert.Equal(2, built.Catalog.Entries.Select(entry => entry.Bundle).Distinct().Count());
    }

    /// <summary>
    ///     Things labelled together are things loaded together, which is what makes this the usual
    ///     right answer. An asset with no label falls back to the group's own bundle.
    /// </summary>
    [Fact]
    public void PackByLabelGivesEachLabelABundle() {
        var built = Build(
            [Group("Level1", packing: BundlePacking.PackTogetherByLabel)],
            [
                Asset("level1/rock", "Level1", labels: ["props"]),
                Asset("level1/tree", "Level1", labels: ["props"]),
                Asset("level1/sky", "Level1", labels: ["backdrop"]),
                Asset("level1/loose", "Level1")
            ]
        );

        Assert.Equal(
            ["Level1", "Level1_backdrop", "Level1_props"],
            built.Bundles.Select(bundle => bundle.Name).Order(StringComparer.Ordinal)
        );

        Assert.Equal("Level1_props", built.Catalog.Get("level1/rock").Bundle);
        Assert.Equal("Level1", built.Catalog.Get("level1/loose").Bundle);
    }

    /// <summary>
    ///     Only one bundle can hold an asset, so packing by label has to pick one of its labels. The
    ///     rule is "first alphabetically", and the build says so rather than leaving it to be worked
    ///     out from the output.
    /// </summary>
    [Fact]
    public void AnAssetWithTwoLabelsGoesInTheFirstAndTheBuildSaysSo() {
        var built = Build(
            [Group("Level1", packing: BundlePacking.PackTogetherByLabel)],
            [Asset("level1/rock", "Level1", labels: ["props", "boot"])]
        );

        Assert.Equal("Level1_boot", built.Catalog.Get("level1/rock").Bundle);

        var note = Assert.Single(built.Diagnostics);
        Assert.Equal(ImportSeverity.Information, note.Severity);
        Assert.Contains("first alphabetically", note.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A CDN caches by URL, so a bundle whose contents changed has to have a different one or the
    ///     cache serves the old bytes for as long as it feels like. The hash in the name is the only
    ///     thing that makes a content update land reliably.
    /// </summary>
    [Fact]
    public void HashedNamingPutsTheContentHashInTheFileName() {
        var hashed = Build([Group("UiCore")], [Asset("ui/hero", "UiCore")]);
        var plain = Build([Group("UiCore", naming: BundleNaming.Filename)], [Asset("ui/hero", "UiCore")]);

        var bundle = Assert.Single(hashed.Bundles);
        Assert.StartsWith("UiCore_", bundle.FileName, StringComparison.Ordinal);
        Assert.Contains(bundle.Hash.ToString()[..16], bundle.FileName, StringComparison.Ordinal);

        Assert.Equal("UiCore.bundle", Assert.Single(plain.Bundles).FileName);
    }

    /// <summary>
    ///     A remote group's bundles carry the URL to fetch them from; a local group's do not, because
    ///     the application already knows where its own files are.
    /// </summary>
    [Fact]
    public void ARemoteGroupsBundlesCarryTheirUrl() {
        var built = Build(
            [Group("Dlc", provider: ContentProvider.Remote, remoteUrl: "https://cdn.example/content")],
            [Asset("dlc/prop", "Dlc")]
        );

        Assert.True(built.Catalog.TryGetBundle("Dlc", out var bundle));
        Assert.StartsWith("https://cdn.example/content/Dlc_", bundle.Url, StringComparison.Ordinal);
        Assert.Equal(ContentProvider.Remote, built.Catalog.Get("dlc/prop").Provider);
    }

    [Fact]
    public void ALocalGroupsBundlesCarryNoUrl() {
        var built = Build([Group("UiCore")], [Asset("ui/hero", "UiCore")]);

        Assert.True(built.Catalog.TryGetBundle("UiCore", out var bundle));
        Assert.Empty(bundle.Url);
    }

    /// <summary>
    ///     A group of work in progress is turned off rather than deleted, and the build says what it
    ///     left out — because "my asset does not load" and "my asset was not built" are the same
    ///     symptom otherwise.
    /// </summary>
    [Fact]
    public void AGroupThatIsTurnedOffIsLeftOutAndSaidSo() {
        var built = Build(
            [Group("UiCore"), Group("WorkInProgress", includeInBuild: false)],
            [Asset("ui/hero", "UiCore"), Asset("wip/thing", "WorkInProgress")]
        );

        Assert.Equal(1, built.Catalog.Count);
        Assert.False(built.Catalog.Contains("wip/thing"));
        Assert.Contains(built.Diagnostics, note => note.Message.Contains("turned off", StringComparison.Ordinal));
    }

    /// <summary>
    ///     An asset in a group nothing defines is a build that cannot proceed: there is no policy to
    ///     apply and guessing one would ship the asset in the wrong place.
    /// </summary>
    [Fact]
    public void AnAssetInAGroupNothingDefinesStopsTheBuild() {
        var failure = Assert.Throws<ArgumentException>(
            () => Build([Group("UiCore")], [Asset("ui/hero", "Missing")])
        );

        Assert.Contains("Missing", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A chunk the import never produced is reported rather than skipped silently. The catalog
    ///     still names the address, because the alternative is an address that vanishes without
    ///     anything saying why.
    /// </summary>
    [Fact]
    public void AnAssetWhoseChunkIsMissingIsReported() {
        var world = new Chunks();
        var builder = new ContentBuilder("Windows");

        var built = builder.Build(
            [Group("UiCore")],
            [new("ui/hero", ContentHash.Compute("nothing wrote this"u8), "UiCore", [], [])],
            world.Backend
        );

        var problem = Assert.Single(built.Diagnostics);
        Assert.Equal(ImportSeverity.Error, problem.Severity);
        Assert.Contains("no such chunk", problem.Message, StringComparison.Ordinal);
        Assert.True(built.Catalog.Contains("ui/hero"));
    }

    /// <summary>
    ///     <para>
    ///         Doc 12 gates the content build on byte-identical output across three operating systems.
    ///         Two builds of the same content have to produce the same bundles and the same catalog,
    ///         whatever order the assets arrived in — which is what a build on another machine
    ///         effectively varies.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheSameContentInAnyOrderBuildsTheSameBytes() {
        var assets = new[] {
            Asset("ui/hero", "UiCore", labels: ["ui"]),
            Asset("ui/villain", "UiCore"),
            Asset("ui/shader", "UiCore")
        };

        var forwards = Build([Group("UiCore")], assets);
        var backwards = Build([Group("UiCore")], [.. assets.Reverse()]);

        Assert.Equal(
            Assert.Single(forwards.Bundles).Bytes.ToArray(),
            Assert.Single(backwards.Bundles).Bytes.ToArray()
        );

        Assert.Equal(CatalogFormat.Write(forwards.Catalog), CatalogFormat.Write(backwards.Catalog));
        Assert.Equal(forwards.Catalog.BuildHash, backwards.Catalog.BuildHash);
    }

    /// <summary>
    ///     <para>
    ///         And the build log is deterministic too. Most of the output's stability comes from
    ///         further down — <c>BundleWriter</c> writes chunks in id order and <c>CatalogFormat</c>
    ///         writes entries in address order — which means the builder's own sort has exactly one
    ///         observable effect: the order it reports things in.
    ///     </para>
    ///     <para>
    ///         Worth keeping and worth testing. A build log that reorders between runs is noise in
    ///         every diff of it, and a diff of build logs is how anyone finds out what a change to
    ///         the content actually did. Removing the sort left every other test in this file green.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheBuildLogComesOutInAddressOrderWhateverOrderTheAssetsArrivedIn() {
        var chunks = new Chunks();

        BuildableAsset[] assets = [
            new("zebra", ContentHash.Compute("zebra"u8), "UiCore", [], []),
            new("aardvark", ContentHash.Compute("aardvark"u8), "UiCore", [], []),
            new("mongoose", ContentHash.Compute("mongoose"u8), "UiCore", [], [])
        ];

        var forwards = new ContentBuilder("Windows").Build([Group("UiCore")], assets, chunks.Backend);
        var backwards = new ContentBuilder("Windows").Build([Group("UiCore")], assets.Reverse(), chunks.Backend);

        // Nothing wrote any of these chunks, so each one is reported.
        Assert.Equal(3, forwards.Diagnostics.Length);
        Assert.Equal(
            forwards.Diagnostics.Select(note => note.Message),
            backwards.Diagnostics.Select(note => note.Message)
        );

        Assert.Contains("aardvark", forwards.Diagnostics[0].Message, StringComparison.Ordinal);
        Assert.Contains("zebra", forwards.Diagnostics[2].Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     And two builds of <i>different</i> content do not, or the build hash would be useless for
    ///     deciding whether a fetched catalog describes content the device already has.
    /// </summary>
    [Fact]
    public void ABuildOfDifferentContentHasADifferentHash() {
        var one = Build([Group("UiCore")], [Asset("ui/hero", "UiCore")]);
        var other = Build([Group("UiCore")], [Asset("ui/hero", "UiCore"), Asset("ui/villain", "UiCore")]);

        Assert.NotEqual(one.Catalog.BuildHash, other.Catalog.BuildHash);
    }

    /// <summary>
    ///     The whole point, end to end: what the builder wrote is what a runtime loads. This is the
    ///     first test in the repository where an address goes in one side and an object comes out the
    ///     other.
    /// </summary>
    [Fact]
    public async Task WhatTheBuilderWritesIsWhatTheRuntimeLoads() {
        var chunks = new Chunks();
        var id = chunks.Write(new BuiltThing { Name = "hero" });

        var built = new ContentBuilder("Windows").Build(
            [Group("UiCore")],
            [new("ui/hero", id, "UiCore", [], [])],
            chunks.Backend
        );

        // Ship it: write the bundles where a LocalBundleSource will look, and hand over the catalog.
        var shipped = new VirtualFileSystem();
        var storage = new MemoryFileProvider();
        shipped.Mount(new("/bundles"), storage);

        foreach (var bundle in built.Bundles) {
            using var target = shipped.OpenWrite(new($"/bundles/{bundle.Name}.bundle"));
            target.Write(bundle.Bytes.Span);
        }

        var catalog = CatalogFormat.Read(CatalogFormat.Write(built.Catalog));
        var assets = new AssetManager(catalog, new LocalBundleSource(shipped, new("/bundles")));

        var handle = assets.LoadAsync<BuiltThing>("ui/hero", TestContext.Current.CancellationToken);
        var loaded = await handle.Completion.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal("hero", loaded.Name);
        handle.Release();
    }

    static AddressableGroup Group(
        string name,
        BundlePacking packing = BundlePacking.PackTogether,
        BundleNaming naming = BundleNaming.FilenameHash,
        ContentProvider provider = ContentProvider.Local,
        bool includeInBuild = true,
        string remoteUrl = ""
    ) =>
        new() {
            Name = name,
            Packing = packing,
            BundleNaming = naming,
            LoadPath = provider,
            IncludeInBuild = includeInBuild,
            RemoteUrl = remoteUrl
        };

    static BuildableAsset Asset(
        string address,
        string group,
        string[]? labels = null,
        string[]? dependencies = null,
        AssetReference reference = default
    ) =>
        new(address, ContentHash.Compute(Encoding.UTF8.GetBytes(address)), group, [.. labels ?? []],
            [.. dependencies ?? []], reference);

    /// <summary>Writes a chunk for every asset, so the builder has something real to pack.</summary>
    static ContentBuildResult Build(AddressableGroup[] groups, BuildableAsset[] assets) {
        var chunks = new Chunks();

        foreach (var asset in assets) {
            chunks.Seed(asset.Id, Encoding.UTF8.GetBytes(asset.Address));
        }

        return new ContentBuilder("Windows").Build(groups, assets, chunks.Backend);
    }

    /// <summary>An object database over memory, and the chunks a build is packing from.</summary>
    sealed class Chunks {
        readonly ObjectDatabase database;

        public FileOdbBackend Backend { get; }

        public Chunks() {
            var files = new VirtualFileSystem();
            files.Mount(new("/odb"), new MemoryFileProvider());
            Backend = new(files, new("/odb"));
            database = new(Backend);
        }

        public ObjectId Write<T>(T value) => database.Write(value);

        /// <summary>Stores bytes under an id the caller chose, standing in for a real chunk.</summary>
        public void Seed(ObjectId id, byte[] content) => Backend.Write(id, content);
    }
}

/// <summary>Something with a serializer, so the end-to-end test has a real object to get back.</summary>
[DataContract("BuiltThing")]
public sealed class BuiltThing {
    /// <summary>What it is called.</summary>
    public string Name { get; set; } = string.Empty;
}
