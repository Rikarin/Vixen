// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.IO.Hashing;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Serialization.Storage;
using Xunit;

namespace Vixen.Assets.Tests;

/// <summary>
///     Loading something that is not on the device yet: the catalog says where, the cache fetches it,
///     and the asset manager never learns the difference.
/// </summary>
public sealed class RemoteContentTests {
    /// <summary>
    ///     The whole path, end to end. Nothing about the load says "remote" — the address is asked for
    ///     the same way, and the download happens because the bundle behind it has a URL.
    /// </summary>
    [Fact]
    public async Task AnAddressInARemoteBundleIsDownloadedAndLoaded() {
        var world = new RemoteWorld();
        world.Remote("dlc/thing", new ReferencedThing { Name = "downloaded" });
        var assets = world.Build();

        Assert.False(world.Source.IsAvailable(world.RemoteBundle));

        var handle = assets.LoadAsync<ReferencedThing>("dlc/thing", TestContext.Current.CancellationToken);
        var loaded = await handle.Completion.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal("downloaded", loaded.Name);
        Assert.True(world.Source.IsAvailable(world.RemoteBundle));
        Assert.Equal(1, world.Transport.Requests);
    }

    /// <summary>
    ///     What the "this pack is 240 MB, continue?" prompt reads. It has to go to zero once the pack
    ///     is here, or a player who already downloaded it is asked to do it again.
    /// </summary>
    [Fact]
    public async Task TheDownloadSizeIsWhatIsMissingRatherThanWhatItAllWeighs() {
        var world = new RemoteWorld();
        world.Remote("dlc/one", new ReferencedThing { Name = "one" });
        world.Remote("dlc/two", new ReferencedThing { Name = "two" });
        var assets = world.Build();

        var before = assets.DownloadSize("dlc/one");
        Assert.Equal(world.RemoteBundle.Size, before);

        // Both addresses are in one bundle, so wanting both costs one download rather than two.
        Assert.Equal(before, assets.DownloadSize("dlc/one", "dlc/two"));

        await assets.DownloadAsync(["dlc/one"], null, TestContext.Current.CancellationToken);

        Assert.Equal(0, assets.DownloadSize("dlc/one", "dlc/two"));
    }

    /// <summary>
    ///     A local address costs nothing to load, so it must not be counted. Charging a player for
    ///     bytes that shipped inside the application is the mistake this asserts against.
    /// </summary>
    [Fact]
    public void ALocalAddressCostsNothingToDownload() {
        var world = new RemoteWorld();
        world.Local("base/thing", new ReferencedThing { Name = "shipped" });
        var assets = world.Build();

        // Asserted on the catalog with no cache predicate as well as through the manager: a local
        // bundle is on the device, so asking the manager cannot tell "not counted because it is
        // local" apart from "not counted because it is already here".
        Assert.Empty(assets.Catalog.RemoteBundlesFor(["base/thing"]));
        Assert.Equal(0, assets.Catalog.DownloadSize(["base/thing"]));
        Assert.Equal(0, assets.DownloadSize("base/thing"));
    }

    /// <summary>
    ///     Pre-downloading gets the bytes and nothing else. A player filling their device before a
    ///     flight is not asking for every texture in the pack to be deserialised into memory.
    /// </summary>
    [Fact]
    public async Task DownloadingGetsTheBytesWithoutLoadingAnything() {
        var world = new RemoteWorld();
        world.Remote("dlc/thing", new ReferencedThing { Name = "later" });
        var assets = world.Build();

        var progress = new Reports();
        await assets.DownloadAsync(["dlc/thing"], progress, TestContext.Current.CancellationToken);

        Assert.True(world.Source.IsAvailable(world.RemoteBundle));
        Assert.Equal(0, assets.LoadedCount);
        Assert.NotEmpty(progress.Seen);
        Assert.Equal(world.RemoteBundle.Size, progress.Seen[^1].Received);

        // And loading afterwards is free, which is the point of having downloaded it.
        var handle = assets.LoadAsync<ReferencedThing>("dlc/thing", TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, world.Transport.Requests);
    }

    /// <summary>Clearing a pack's cache gives the space back, and a later load fetches it again.</summary>
    [Fact]
    public async Task ClearingACacheRemovesThePackAndTheNextLoadRefetchesIt() {
        var world = new RemoteWorld();
        world.Remote("dlc/thing", new ReferencedThing { Name = "temporary" });
        var assets = world.Build();

        await assets.DownloadAsync(["dlc/thing"], null, TestContext.Current.CancellationToken);
        Assert.Equal(1, assets.ClearCache("dlc/thing"));
        Assert.False(world.Source.IsAvailable(world.RemoteBundle));

        var handle = assets.LoadAsync<ReferencedThing>("dlc/thing", TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, world.Transport.Requests);
    }

    /// <summary>
    ///     A bundle something has open is left alone. A backend is a window onto a mapped file, and
    ///     deleting the file underneath it does not close the window — it produces a reader on
    ///     something nothing can find, which is worse than refusing.
    /// </summary>
    [Fact]
    public async Task APackThatIsCurrentlyOpenIsNotEvicted() {
        var world = new RemoteWorld();
        world.Remote("dlc/thing", new ReferencedThing { Name = "held" });
        var assets = world.Build();

        var handle = assets.LoadAsync<ReferencedThing>("dlc/thing", TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, assets.ClearCache("dlc/thing"));
        Assert.True(world.Source.IsAvailable(world.RemoteBundle));
    }

    /// <summary>
    ///     A local bundle is not the runtime's to delete: it shipped inside the application, and
    ///     "clearing the cache" must not try to reach into the install.
    /// </summary>
    [Fact]
    public void ALocalBundleIsNotEvictable() {
        var world = new RemoteWorld();
        world.Local("base/thing", new ReferencedThing { Name = "shipped" });
        var assets = world.Build();

        Assert.Equal(0, assets.ClearCache("base/thing"));
    }

    /// <summary>
    ///     The router picks by URL, and a catalog that mixes the two loads both without the manager
    ///     above it knowing there was a choice to make.
    /// </summary>
    [Fact]
    public async Task OneCatalogCanHoldBothAndBothLoad() {
        var world = new RemoteWorld();
        world.Local("base/thing", new ReferencedThing { Name = "shipped" });
        world.Remote("dlc/thing", new ReferencedThing { Name = "downloaded" });
        var assets = world.Build();

        Assert.Same(world.LocalSource, world.Source.SourceFor(world.LocalBundle));
        Assert.Same(world.RemoteSource, world.Source.SourceFor(world.RemoteBundle));

        var local = assets.LoadAsync<ReferencedThing>("base/thing", TestContext.Current.CancellationToken);
        var remote = assets.LoadAsync<ReferencedThing>("dlc/thing", TestContext.Current.CancellationToken);

        Assert.Equal("shipped", (await local.Completion.WaitAsync(TestContext.Current.CancellationToken)).Name);
        Assert.Equal("downloaded", (await remote.Completion.WaitAsync(TestContext.Current.CancellationToken)).Name);
    }

    /// <summary>
    ///     Two addresses in one downloaded pack open the file once. A backend maps the bundle, and
    ///     mapping it twice would give two lifetimes to get right instead of one.
    /// </summary>
    [Fact]
    public async Task TwoAddressesInOnePackOpenItOnce() {
        var world = new RemoteWorld();
        world.Remote("dlc/one", new ReferencedThing { Name = "one" });
        world.Remote("dlc/two", new ReferencedThing { Name = "two" });
        var assets = world.Build();

        var one = assets.LoadAsync<ReferencedThing>("dlc/one", TestContext.Current.CancellationToken);
        var two = assets.LoadAsync<ReferencedThing>("dlc/two", TestContext.Current.CancellationToken);
        await one.Completion.WaitAsync(TestContext.Current.CancellationToken);
        await two.Completion.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, world.Transport.Requests);
    }

    /// <summary>
    ///     And two opens that overlap map it once. The check outside the lock catches the sequential
    ///     case; only two callers arriving together — both having missed it, both waiting on the same
    ///     download — reach the one inside.
    /// </summary>
    [Fact]
    public async Task TwoOverlappingOpensOfOnePackMapItOnce() {
        var world = new RemoteWorld();
        world.Remote("dlc/thing", new ReferencedThing { Name = "shared" });
        world.Build();
        world.Transport.Hold();

        var first = world.RemoteSource.OpenAsync(world.RemoteBundle, TestContext.Current.CancellationToken).AsTask();
        var second = world.RemoteSource.OpenAsync(world.RemoteBundle, TestContext.Current.CancellationToken).AsTask();

        Assert.False(first.IsCompleted);

        world.Transport.Release();
        var backends = await Task.WhenAll(first, second);

        Assert.Same(backends[0], backends[1]);
        Assert.Equal(1, world.Transport.Requests);
    }

    /// <summary>
    ///     A pack the server does not have is a load that fails saying which bundle and why, rather
    ///     than an address that is mysteriously missing.
    /// </summary>
    [Fact]
    public async Task APackTheServerDoesNotHaveFailsByName() {
        var world = new RemoteWorld();
        world.Remote("dlc/thing", new ReferencedThing { Name = "gone" }, serve: false);
        var assets = world.Build();

        var handle = assets.LoadAsync<ReferencedThing>("dlc/thing", TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAsync<BundleUnavailableException>(
            async () => await handle.Completion.WaitAsync(TestContext.Current.CancellationToken)
        );

        Assert.Equal("Remote", failure.Bundle);

        // And the failed load gave back everything it had claimed rather than leaking it.
        Assert.Equal(0, assets.LoadedCount);
    }

    /// <summary>
    ///     A source that cannot fetch says so when asked to. The default is what every non-caching
    ///     source inherits, and silently succeeding would let a pre-download button report success
    ///     against a bundle that is not there.
    /// </summary>
    [Fact]
    public async Task ASourceThatCannotFetchSaysSoRatherThanPretending() {
        var files = new VirtualFileSystem();
        files.Mount(new("/bundles"), new MemoryFileProvider());
        using var source = new LocalBundleSource(files, new("/bundles"));
        var missing = new CatalogBundle("absent", "", new(3, 3), 8, 0, CompressionMethod.Lz4, []);

        // Through the interface, because that is where the default implementation lives and where
        // every caller of it sits.
        IBundleSource local = source;

        var failure = await Assert.ThrowsAsync<BundleUnavailableException>(
            async () => await local.EnsureAsync(missing, null, TestContext.Current.CancellationToken)
        );

        Assert.Contains("cannot fetch", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Collects progress on the thread that reports it. See <c>BundleCacheTests.Watching</c>.</summary>
    sealed class Reports : IProgress<BundleProgress> {
        public List<BundleProgress> Seen { get; } = [];

        public void Report(BundleProgress value) => Seen.Add(value);
    }

    /// <summary>A catalog with a local bundle, a downloadable one, and a server that has it.</summary>
    sealed class RemoteWorld {
        readonly List<(string Address, ObjectId Id, bool IsRemote)> planned = [];
        readonly VirtualFileSystem files = new();
        readonly FileOdbBackend localScratch;
        readonly FileOdbBackend remoteScratch;
        readonly ObjectDatabase localWriting;
        readonly ObjectDatabase remoteWriting;
        bool serveRemote = true;

        public FakeContentTransport Transport { get; } = new();
        public LocalBundleSource LocalSource { get; private set; } = null!;
        public RemoteBundleSource RemoteSource { get; private set; } = null!;
        public RoutedBundleSource Source { get; private set; } = null!;
        public CatalogBundle LocalBundle { get; private set; }
        public CatalogBundle RemoteBundle { get; private set; }

        public RemoteWorld() {
            var storage = new MemoryFileProvider();
            files.Mount(new("/store"), storage);
            files.Mount(new("/bundles"), storage);
            files.Mount(new("/cache"), new MemoryFileProvider());

            localScratch = new(files, new("/store/local"));
            remoteScratch = new(files, new("/store/remote"));
            localWriting = new(localScratch);
            remoteWriting = new(remoteScratch);
        }

        public void Local<T>(string address, T value) => planned.Add((address, localWriting.Write(value), false));

        public void Remote<T>(string address, T value, bool serve = true) {
            serveRemote &= serve;
            planned.Add((address, remoteWriting.Write(value), true));
        }

        public AssetManager Build() {
            LocalBundle = Pack("Local", localScratch, new("/bundles/Local.bundle"), url: "");
            RemoteBundle = Pack("Remote", remoteScratch, null, "https://content.example/Remote.bundle");

            LocalSource = new(files, new("/bundles"));
            RemoteSource = new(files, new(files, new("/cache"), Transport));
            Source = new(LocalSource, RemoteSource);

            var catalog = new ContentCatalog(
                CatalogFormat.Version,
                default,
                "Windows",
                planned.Select(entry => new CatalogEntry(
                        entry.Address,
                        entry.Id,
                        entry.IsRemote ? "Remote" : "Local",
                        entry.IsRemote ? ContentProvider.Remote : ContentProvider.Local,
                        [],
                        [],
                        0
                    )
                ),
                [LocalBundle, RemoteBundle]
            );

            return new(catalog, Source);
        }

        /// <summary>Builds a bundle, and either writes it to disk or publishes it to the server.</summary>
        CatalogBundle Pack(string name, IOdbBackend from, VirtualPath? writeTo, string url) {
            var writer = new BundleWriter();
            writer.AddAll(from);
            var bytes = writer.Build();

            if (writeTo is { } path) {
                using var target = files.OpenWrite(path);
                target.Write(bytes);
            } else if (serveRemote) {
                Transport.Serve(url, bytes);
            }

            return new(
                name,
                url,
                ContentHash.Compute(bytes),
                bytes.Length,
                Crc32.HashToUInt32(bytes),
                CompressionMethod.Lz4,
                []
            );
        }
    }
}
