// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.IO.Hashing;
using System.Text;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Serialization.Storage;
using Xunit;

namespace Vixen.Assets.Tests;

/// <summary>
///     Step 2 of doc 08's boot sequence, and the property the whole content system exists for: a game
///     that has shipped can be given new content without shipping a new build, and a player who has
///     already downloaded most of it downloads only what changed.
/// </summary>
public sealed class ContentUpdateTests {
    /// <summary>A game with no remote content asks nothing of the network.</summary>
    [Fact]
    public async Task WithNoUrlConfiguredNothingIsAsked() {
        var world = new UpdateWorld();
        var result = await world.Client("").ApplyAsync(world.Shipped, TestContext.Current.CancellationToken);

        Assert.Equal(ContentUpdateOutcome.NoRemoteConfigured, result.Outcome);
        Assert.Same(world.Shipped, result.Catalog);
        Assert.Equal(0, world.Transport.Requests);
    }

    /// <summary>The first launch downloads the catalog, caches it, and can see what it added.</summary>
    [Fact]
    public async Task TheFirstCheckDownloadsTheCatalogAndCachesIt() {
        var world = new UpdateWorld();
        world.Publish(world.Update("dlc/thing"));

        var client = world.Client();
        var result = await client.ApplyAsync(world.Shipped, TestContext.Current.CancellationToken);

        Assert.Equal(ContentUpdateOutcome.Updated, result.Outcome);
        Assert.Null(result.Reason);
        Assert.True(result.Catalog.Contains("dlc/thing"));

        // And the address that shipped is still there: an update lays over, it does not replace.
        Assert.True(result.Catalog.Contains("base/thing"));
        Assert.Equal(world.PublishedHash, client.CachedVersion());
    }

    /// <summary>
    ///     The common case, and the reason the hash file exists. A launch with nothing new costs one
    ///     request the size of a packet rather than a catalog that for a real game is hundreds of
    ///     kilobytes.
    /// </summary>
    [Fact]
    public async Task ALaunchWithNothingNewFetchesOnlyTheHash() {
        var world = new UpdateWorld();
        world.Publish(world.Update("dlc/thing"));

        var client = world.Client();
        await client.ApplyAsync(world.Shipped, TestContext.Current.CancellationToken);

        var afterFirst = world.Transport.BytesServed;
        var result = await client.ApplyAsync(world.Shipped, TestContext.Current.CancellationToken);

        Assert.Equal(ContentUpdateOutcome.AlreadyCurrent, result.Outcome);

        // The merged catalog still has the update in it: "already current" means nothing was
        // downloaded, not that nothing is applied.
        Assert.True(result.Catalog.Contains("dlc/thing"));
        Assert.Equal(ObjectId.TextLength, world.Transport.BytesServed - afterFirst);
    }

    /// <summary>
    ///     <b>Doc 08's exit criterion.</b> The server publishes a second build in which one of two
    ///     packs changed; the client updates and fetches that pack and nothing else. Asserted by byte
    ///     count, because "it worked" and "it re-downloaded everything" look identical from the
    ///     outside and only one of them is shippable over a phone connection.
    /// </summary>
    [Fact]
    public async Task AnUpdateFetchesOnlyTheBundlesThatChanged() {
        var world = new UpdateWorld();
        var stable = world.Bundle("stable", 4096);
        var changing = world.Bundle("changing", 2048);
        world.Publish(UpdateWorld.Update(("dlc/a", stable), ("dlc/b", changing)));

        var client = world.Client();
        var first = await client.ApplyAsync(world.Shipped, TestContext.Current.CancellationToken);
        var assets = world.Assets(first.Catalog);

        await assets.DownloadAsync(["dlc/a", "dlc/b"], null, TestContext.Current.CancellationToken);
        Assert.Equal(0, assets.DownloadSize("dlc/a", "dlc/b"));

        var afterFirstBuild = world.Transport.BytesServed;
        var requestsBefore = world.Transport.RequestedUrls.Count;

        // Build two: the second pack is rebuilt, the first is byte-for-byte what it was.
        var rebuilt = world.Bundle("changing", 2048, seed: 77);
        world.Publish(UpdateWorld.Update(("dlc/a", stable), ("dlc/b", rebuilt)));

        var second = await client.ApplyAsync(world.Shipped, TestContext.Current.CancellationToken);
        Assert.Equal(ContentUpdateOutcome.Updated, second.Outcome);

        var updated = world.Assets(second.Catalog, client);

        // Only the rebuilt pack is missing, and the unchanged one is still a cache hit — which is
        // what content-hash keying buys and what a name-keyed cache would get wrong in the other
        // direction, by serving the stale one.
        Assert.Equal(rebuilt.Size, updated.DownloadSize("dlc/a", "dlc/b"));

        await updated.DownloadAsync(["dlc/a", "dlc/b"], null, TestContext.Current.CancellationToken);

        // The pack that did not change was not asked for at all — the direct statement of the
        // property, rather than an arithmetic identity that happens to hold.
        var since = world.Transport.RequestedUrls.Skip(requestsBefore).ToList();
        Assert.DoesNotContain(stable.Url, since);
        Assert.Contains(rebuilt.Url, since);

        // And the bytes: the rebuilt pack, the new catalog, and the one hash file that said it was
        // new. Nothing else crossed the wire.
        var moved = world.Transport.BytesServed - afterFirstBuild;
        Assert.Equal(rebuilt.Size + world.CatalogSize + ObjectId.TextLength, moved);
        Assert.True(moved < stable.Size, $"{moved} bytes moved, which is more than the pack that did not change");
    }

    /// <summary>
    ///     A player on a train still gets their game, on the newest catalog that reached the device.
    ///     Throwing here turns a flaky connection into a game that will not launch.
    /// </summary>
    [Fact]
    public async Task AServerThatCannotBeReachedFallsBackToWhatWasCached() {
        var world = new UpdateWorld();
        world.Publish(world.Update("dlc/thing"));

        var client = world.Client();
        await client.ApplyAsync(world.Shipped, TestContext.Current.CancellationToken);

        world.Unplug();
        var result = await client.ApplyAsync(world.Shipped, TestContext.Current.CancellationToken);

        Assert.Equal(ContentUpdateOutcome.Offline, result.Outcome);
        Assert.True(result.Catalog.Contains("dlc/thing"));
        Assert.Contains("404", result.Reason!, StringComparison.Ordinal);
    }

    /// <summary>And a first launch with no connection starts on what shipped.</summary>
    [Fact]
    public async Task AFirstLaunchWithNoConnectionStartsOnWhatShipped() {
        var world = new UpdateWorld();
        var result = await world.Client().ApplyAsync(world.Shipped, TestContext.Current.CancellationToken);

        Assert.Equal(ContentUpdateOutcome.Offline, result.Outcome);
        Assert.True(result.Catalog.Contains("base/thing"));
        Assert.False(result.Catalog.Contains("dlc/thing"));
    }

    /// <summary>
    ///     A hash file and a catalog from different builds is a half-finished publish or a CDN holding
    ///     one of them stale. It is reported as rejected rather than offline, because a player waiting
    ///     for it to fix itself will wait for ever.
    /// </summary>
    [Fact]
    public async Task ACatalogThatDoesNotMatchItsAdvertisedHashIsRejected() {
        var world = new UpdateWorld();
        world.Publish(world.Update("dlc/thing"));
        world.Transport.Serve(UpdateWorld.CatalogUrl, [.. "not the catalog that was advertised"u8]);

        var client = world.Client();
        var result = await client.ApplyAsync(world.Shipped, TestContext.Current.CancellationToken);

        Assert.Equal(ContentUpdateOutcome.Rejected, result.Outcome);
        Assert.Contains("different builds", result.Reason!, StringComparison.Ordinal);
        Assert.Null(client.CachedVersion());
        Assert.False(result.Catalog.Contains("dlc/thing"));
    }

    /// <summary>A hash file holding something that is not a hash is the same class of problem.</summary>
    [Fact]
    public async Task AHashFileThatIsNotAHashIsRejected() {
        var world = new UpdateWorld();
        world.Publish(world.Update("dlc/thing"));
        world.Transport.Serve(UpdateWorld.CatalogUrl + ".hash", [.. "<html>404 Not Found</html>"u8]);

        var result = await world.Client().ApplyAsync(world.Shipped, TestContext.Current.CancellationToken);

        Assert.Equal(ContentUpdateOutcome.Rejected, result.Outcome);
        Assert.Contains("characters of something else", result.Reason!, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A catalog built for another platform resolves addresses to chunks in a format this device
    ///     cannot read, so it is refused — and refused the same way, without taking the game down.
    /// </summary>
    [Fact]
    public async Task AnUpdateBuiltForAnotherPlatformIsRejected() {
        var world = new UpdateWorld();
        world.Publish(world.Update("dlc/thing", target: "Android"));

        var client = world.Client();
        var result = await client.ApplyAsync(world.Shipped, TestContext.Current.CancellationToken);

        Assert.Equal(ContentUpdateOutcome.Rejected, result.Outcome);
        Assert.Contains("Android", result.Reason!, StringComparison.Ordinal);
        Assert.Null(client.CachedVersion());
    }

    /// <summary>
    ///     A catalog that hashes correctly and parses to nothing is a broken publish rather than a
    ///     broken download, and it must not overwrite a cached one that works — the next launch would
    ///     then be broken with nothing left to fall back to.
    /// </summary>
    [Fact]
    public async Task AnUnreadableCatalogIsNotWrittenOverAGoodCachedOne() {
        var world = new UpdateWorld();
        world.Publish(world.Update("dlc/thing"));

        var client = world.Client();
        await client.ApplyAsync(world.Shipped, TestContext.Current.CancellationToken);
        var good = client.CachedVersion();

        // Hashes to what the hash file says, and is not a catalog.
        var rubbish = new byte[64];
        world.PublishRaw(rubbish);

        var result = await client.ApplyAsync(world.Shipped, TestContext.Current.CancellationToken);

        Assert.Equal(ContentUpdateOutcome.Rejected, result.Outcome);
        Assert.Equal(good, client.CachedVersion());
        Assert.True(result.Catalog.Contains("dlc/thing"));
    }

    /// <summary>
    ///     The hash file is written second and read first, so a crash between the two writes reads as
    ///     "nothing cached" and is refetched, rather than as a catalog answering to the wrong name.
    /// </summary>
    [Fact]
    public async Task ACatalogWithNoHashBesideItCountsAsNothingCached() {
        var world = new UpdateWorld();
        world.Publish(world.Update("dlc/thing"));

        var client = world.Client();
        await client.ApplyAsync(world.Shipped, TestContext.Current.CancellationToken);

        world.Files.Delete(client.CachedHashPath);
        Assert.Null(client.CachedVersion());

        var result = await client.ApplyAsync(world.Shipped, TestContext.Current.CancellationToken);

        Assert.Equal(ContentUpdateOutcome.Updated, result.Outcome);
        Assert.Equal(world.PublishedHash, client.CachedVersion());
    }

    /// <summary>A shipped catalog, a server, and a client that has to decide between them.</summary>
    sealed class UpdateWorld {
        readonly Dictionary<string, CatalogBundle> bundles = new(StringComparer.Ordinal);

        public VirtualFileSystem Files { get; } = new();
        public FakeContentTransport Transport { get; } = new();
        public ContentCatalog Shipped { get; }
        public static string CatalogUrl => "https://content.example/catalog.bin";
        public ObjectId PublishedHash { get; private set; }
        public int CatalogSize { get; private set; }

        public UpdateWorld() {
            Files.Mount(new("/cache"), new MemoryFileProvider());

            Shipped = new(
                CatalogFormat.Version,
                default,
                "Windows",
                [new("base/thing", new(1, 1), "Base", ContentProvider.Local, [], [], 0)],
                [new("Base", "", new(2, 2), 16, 0, CompressionMethod.Lz4, [])]
            );
        }

        public ContentUpdate Client(string? url = null) =>
            new(Files, new("/cache"), Transport, url ?? CatalogUrl);

        /// <summary>An asset manager over a catalog, sharing one bundle cache across updates.</summary>
        public AssetManager Assets(ContentCatalog catalog, ContentUpdate? _ = null) {
            cache ??= new(Files, new("/cache/bundles"), Transport);
            source ??= new(Files, cache);

            return new(catalog, source);
        }

        BundleCache? cache;
        RemoteBundleSource? source;

        /// <summary>Makes up a bundle of a given size and serves it at a hash-named URL.</summary>
        public CatalogBundle Bundle(string name, int size, byte seed = 0) {
            var contents = new byte[size];

            for (var index = 0; index < size; index++) {
                contents[index] = (byte)((index * 17) + seed);
            }

            var hash = ContentHash.Compute(contents);
            var url = $"https://content.example/{name}-{hash}.bundle";
            Transport.Serve(url, contents);

            var bundle = new CatalogBundle(
                name,
                url,
                hash,
                size,
                Crc32.HashToUInt32(contents),
                CompressionMethod.Lz4,
                []
            );

            bundles[name] = bundle;

            return bundle;
        }

        /// <summary>An update catalog holding one address in a bundle of its own.</summary>
        public ContentCatalog Update(string address, string target = "Windows") =>
            Update(target, (address, Bundle("Dlc", 512)));

        /// <summary>An update catalog holding some addresses in named bundles.</summary>
        public static ContentCatalog Update(params (string Address, CatalogBundle Bundle)[] contents) =>
            Update("Windows", contents);

        static ContentCatalog Update(string target, params (string Address, CatalogBundle Bundle)[] contents) =>
            new(
                CatalogFormat.Version,
                default,
                target,
                contents.Select(entry => new CatalogEntry(
                        entry.Address,
                        new((ulong)entry.Address.Length, 9),
                        entry.Bundle.Name,
                        ContentProvider.Remote,
                        [],
                        [],
                        0
                    )
                ),
                contents.Select(entry => entry.Bundle).Distinct()
            );

        /// <summary>Serves a catalog and the hash file naming it.</summary>
        public void Publish(ContentCatalog catalog) => PublishRaw(CatalogFormat.Write(catalog));

        /// <summary>Serves arbitrary bytes as the catalog, with a hash file that agrees with them.</summary>
        public void PublishRaw(byte[] bytes) {
            PublishedHash = ContentHash.Compute(bytes);
            CatalogSize = bytes.Length;
            Transport.Serve(CatalogUrl, bytes);
            Transport.Serve(CatalogUrl + ".hash", Encoding.UTF8.GetBytes(PublishedHash.ToString()));
        }

        /// <summary>Takes the server away, as a train tunnel does.</summary>
        public void Unplug() => Transport.Unserve();
    }
}
