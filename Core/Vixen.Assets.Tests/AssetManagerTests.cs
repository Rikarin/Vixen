// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Serialization.Storage;
using Xunit;

namespace Vixen.Assets.Tests;

/// <summary>What a shipped asset written into a real bundle looks like coming back out.</summary>
[DataContract("TestAsset")]
public sealed class TestAsset {
    /// <summary>What it is called.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Something to check came back.</summary>
    public int Value { get; set; }
}

/// <summary>Something else, so that asking for the wrong type has a wrong type to be.</summary>
[DataContract("OtherAsset")]
public sealed class OtherAsset {
    /// <summary>Anything.</summary>
    public int Value { get; set; }
}

/// <summary>
///     Loading, over a real bundle written by <c>BundleWriter</c> and read back through
///     <c>LocalBundleSource</c> — so the path under test is the one a game runs, not a stub of it.
/// </summary>
public sealed class AssetManagerTests {
    [Fact]
    public async Task AnAddressLoadsToTheAssetThatWasWritten() {
        var world = new World().With("ui/hero", new TestAsset { Name = "hero", Value = 42 }).Build();

        var handle = world.Assets.LoadAsync<TestAsset>("ui/hero", TestContext.Current.CancellationToken);
        var asset = await handle.Completion.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal("hero", asset.Name);
        Assert.Equal(42, asset.Value);
        Assert.Equal(AssetStatus.Loaded, handle.Status);
        Assert.Same(asset, handle.Result);
    }

    [Fact]
    public void TheBlockingFormComesBackAlreadyLoaded() {
        var world = new World().With("ui/hero", new TestAsset { Name = "hero" }).Build();

        var handle = world.Assets.Load<TestAsset>("ui/hero", TestContext.Current.CancellationToken);

        Assert.Equal(AssetStatus.Loaded, handle.Status);
        Assert.Equal("hero", handle.Result.Name);
    }

    /// <summary>
    ///     Two callers asking for one address get one asset. That is the whole point of a manager
    ///     sitting between the catalog and the database — without it, two scenes sharing a texture is
    ///     two textures.
    /// </summary>
    [Fact]
    public async Task TwoCallersAskingForOneAddressGetOneAsset() {
        var world = new World().With("ui/hero", new TestAsset { Name = "hero" }).Build();

        var first = world.Assets.LoadAsync<TestAsset>("ui/hero", TestContext.Current.CancellationToken);
        var second = world.Assets.LoadAsync<TestAsset>("ui/hero", TestContext.Current.CancellationToken);

        Assert.Same(await Settled(first), await Settled(second));
        Assert.Equal(2, world.Assets.ClaimCount("ui/hero"));
    }

    /// <summary>
    ///     And it stays loaded while either of them still wants it. Unloading on the first release
    ///     is the bug this counting exists to prevent.
    /// </summary>
    [Fact]
    public async Task ItStaysLoadedWhileAnyoneStillWantsIt() {
        var world = new World().With("ui/hero", new TestAsset()).Build();

        var first = world.Assets.LoadAsync<TestAsset>("ui/hero", TestContext.Current.CancellationToken);
        var second = world.Assets.LoadAsync<TestAsset>("ui/hero", TestContext.Current.CancellationToken);
        await Task.WhenAll(Settled(first), Settled(second));

        first.Release();
        Assert.True(world.Assets.IsLoaded("ui/hero"));

        second.Release();
        Assert.False(world.Assets.IsLoaded("ui/hero"));
        Assert.Equal(0, world.Assets.LoadedCount);
    }

    /// <summary>
    ///     <para>
    ///         Releasing twice throws rather than shrugging. A no-op is the tempting choice and it is
    ///         exactly what turns a double release into someone else's asset being unloaded: the
    ///         second call decrements a count another holder is relying on, and the failure surfaces
    ///         much later as a disposed object nobody released.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task ReleasingTwiceSaysSoRatherThanUnloadingSomeoneElsesClaim() {
        var world = new World().With("ui/hero", new TestAsset()).Build();

        var mine = world.Assets.LoadAsync<TestAsset>("ui/hero", TestContext.Current.CancellationToken);
        var yours = world.Assets.LoadAsync<TestAsset>("ui/hero", TestContext.Current.CancellationToken);
        await Settled(mine);

        mine.Release();

        var failure = Assert.Throws<InvalidOperationException>(mine.Release);
        Assert.Contains("already released", failure.Message, StringComparison.Ordinal);

        // Yours is untouched, which is the property the throw is protecting.
        Assert.True(world.Assets.IsLoaded("ui/hero"));
        Assert.Equal(1, world.Assets.ClaimCount("ui/hero"));
        yours.Release();
    }

    [Fact]
    public async Task AReleasedHandleWillNotHandOverItsResult() {
        var world = new World().With("ui/hero", new TestAsset()).Build();

        var handle = world.Assets.LoadAsync<TestAsset>("ui/hero", TestContext.Current.CancellationToken);
        await Settled(handle);
        handle.Release();

        Assert.Equal(AssetStatus.Released, handle.Status);
        Assert.Throws<InvalidOperationException>(() => handle.Result);
    }

    /// <summary>
    ///     Loading a material claims the texture it points at, so the texture survives exactly as
    ///     long as some material needs it. Without that, a shared dependency is unloaded by whichever
    ///     dependent happens to finish first.
    /// </summary>
    [Fact]
    public async Task ADependencyIsClaimedByEverythingThatNeedsIt() {
        var world = new World()
            .With("shared/texture", new TestAsset { Name = "texture" })
            .With("mat/a", new TestAsset { Name = "a" }, dependencies: ["shared/texture"])
            .With("mat/b", new TestAsset { Name = "b" }, dependencies: ["shared/texture"])
            .Build();

        var a = world.Assets.LoadAsync<TestAsset>("mat/a", TestContext.Current.CancellationToken);
        var b = world.Assets.LoadAsync<TestAsset>("mat/b", TestContext.Current.CancellationToken);
        await Task.WhenAll(Settled(a), Settled(b));

        Assert.Equal(2, world.Assets.ClaimCount("shared/texture"));

        a.Release();
        Assert.True(world.Assets.IsLoaded("shared/texture"));

        b.Release();
        Assert.False(world.Assets.IsLoaded("shared/texture"));
    }

    [Fact]
    public async Task AHandleKnowsEverythingItClaimed() {
        var world = new World()
            .With("shader", new TestAsset())
            .With("texture", new TestAsset())
            .With("material", new TestAsset(), dependencies: ["texture", "shader"])
            .Build();

        var handle = world.Assets.LoadAsync<TestAsset>("material", TestContext.Current.CancellationToken);
        await Settled(handle);

        Assert.Equal(["material", "shader", "texture"], handle.Acquired.Order(StringComparer.Ordinal));
        handle.Release();
        Assert.Equal(0, world.Assets.LoadedCount);
    }

    /// <summary>
    ///     A scope releases what was loaded through it, which is the answer to the leak that
    ///     hand-written release calls become as soon as a load path grows a second exit.
    /// </summary>
    [Fact]
    public async Task AScopeReleasesEverythingLoadedThroughIt() {
        var world = new World()
            .With("ui/hero", new TestAsset())
            .With("ui/villain", new TestAsset())
            .Build();

        using (var scope = world.Assets.Scope()) {
            await Settled(scope.LoadAsync<TestAsset>("ui/hero", TestContext.Current.CancellationToken));
            await Settled(scope.LoadAsync<TestAsset>("ui/villain", TestContext.Current.CancellationToken));

            Assert.Equal(2, scope.Count);
            Assert.Equal(2, world.Assets.LoadedCount);
        }

        Assert.Equal(0, world.Assets.LoadedCount);
    }

    [Fact]
    public async Task AScopeLeavesAloneWhatItDidNotLoad() {
        var world = new World().With("ui/hero", new TestAsset()).Build();

        var outside = world.Assets.LoadAsync<TestAsset>("ui/hero", TestContext.Current.CancellationToken);
        await Settled(outside);

        using (var scope = world.Assets.Scope()) {
            await Settled(scope.LoadAsync<TestAsset>("ui/hero", TestContext.Current.CancellationToken));
        }

        Assert.True(world.Assets.IsLoaded("ui/hero"));
        Assert.Equal(1, world.Assets.ClaimCount("ui/hero"));
        outside.Release();
    }

    [Fact]
    public void AnAddressTheCatalogDoesNotHaveFailsBeforeAnythingIsClaimed() {
        var world = new World().With("ui/hero", new TestAsset()).Build();

        Assert.Throws<AddressNotFoundException>(() => world.Assets.LoadAsync<TestAsset>("ui/missing", TestContext.Current.CancellationToken));
        Assert.Equal(0, world.Assets.LoadedCount);
    }

    /// <summary>
    ///     A failed load has to give back everything it managed to claim on the way, or every broken
    ///     asset leaks its dependencies and the leak is invisible until memory runs out.
    /// </summary>
    [Fact]
    public async Task AFailedLoadGivesBackEverythingItClaimed() {
        var world = new World()
            .With("texture", new TestAsset())
            .WithMissingChunk("material", dependencies: ["texture"])
            .Build();

        var handle = world.Assets.LoadAsync<TestAsset>("material", TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<Exception>(async () => await handle);

        Assert.Equal(AssetStatus.Failed, handle.Status);
        Assert.Equal(0, world.Assets.LoadedCount);
    }

    /// <summary>
    ///     A bundle the build did not produce says which one and why, rather than surfacing as a
    ///     chunk that is mysteriously absent.
    /// </summary>
    [Fact]
    public async Task ABundleThatIsNotThereNamesItself() {
        // The catalog names a bundle the build never wrote, which is what a mismatched
        // catalog-and-bundles pair looks like from the runtime's side.
        var world = new World().With("ui/hero", new TestAsset()).Build(bundleName: "NotWritten", writtenAs: "Main");

        var failure = await Assert.ThrowsAsync<BundleUnavailableException>(
            async () => await Settled(world.Assets.LoadAsync<TestAsset>("ui/hero", TestContext.Current.CancellationToken))
        );

        Assert.Equal("NotWritten", failure.Bundle);
        Assert.Equal(0, world.Assets.LoadedCount);
    }

    /// <summary>
    ///     Asking for the right address as the wrong type is a mistake worth naming, because the
    ///     alternative is a cast failure somewhere else entirely.
    /// </summary>
    [Fact]
    public async Task TheRightAddressAsTheWrongTypeSaysBoth() {
        var world = new World().With("ui/hero", new TestAsset()).Build();

        var failure = await Assert.ThrowsAnyAsync<Exception>(
            async () => await Settled(world.Assets.LoadAsync<OtherAsset>("ui/hero", TestContext.Current.CancellationToken))
        );

        Assert.Contains("ui/hero", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, world.Assets.LoadedCount);
    }

    [Fact]
    public async Task EverythingCarryingALabelLoadsAtOnce() {
        var world = new World()
            .With("ui/hero", new TestAsset { Name = "hero" }, labels: ["ui"])
            .With("ui/villain", new TestAsset { Name = "villain" }, labels: ["ui"])
            .With("level/rock", new TestAsset { Name = "rock" })
            .Build();

        var handles = world.Assets.LoadByLabelAsync<TestAsset>("ui", TestContext.Current.CancellationToken);

        Assert.Equal(2, handles.Length);
        Assert.Equal(["hero", "villain"], await Names(handles));
    }

    [Fact]
    public async Task EverythingMatchingAGlobLoadsAtOnce() {
        var world = new World()
            .With("level/rock", new TestAsset { Name = "rock" })
            .With("level/tree", new TestAsset { Name = "tree" })
            .With("level/props/barrel", new TestAsset { Name = "barrel" })
            .Build();

        var handles = world.Assets.LoadMatchingAsync<TestAsset>("level/*", TestContext.Current.CancellationToken);

        Assert.Equal(["rock", "tree"], await Names(handles));
    }

    static async Task<string[]> Names(ImmutableArray<AssetHandle<TestAsset>> handles) {
        var names = new List<string>();

        foreach (var handle in handles) {
            names.Add((await Settled(handle)).Name);
        }

        names.Sort(StringComparer.Ordinal);
        return [.. names];
    }

    /// <summary>The handle's load, with the test's cancellation token attached.</summary>
    static Task<T> Settled<T>(AssetHandle<T> handle) where T : class =>
        handle.Completion.WaitAsync(TestContext.Current.CancellationToken);



    /// <summary>A catalog, a bundle and a manager over both — put together the way a build does.</summary>
    sealed class World {
        readonly List<(string Address, object? Asset, string[] Dependencies, string[] Labels)> planned = [];

        public AssetManager Assets { get; private set; } = null!;

        public World With(string address, object asset, string[]? dependencies = null, string[]? labels = null) {
            planned.Add((address, asset, dependencies ?? [], labels ?? []));
            return this;
        }

        /// <summary>An entry whose chunk the build never wrote — a broken content build, in one line.</summary>
        public World WithMissingChunk(string address, string[]? dependencies = null) {
            planned.Add((address, null, dependencies ?? [], []));
            return this;
        }

        public World Build(string bundleName = "Main", string? writtenAs = null) {
            var files = new VirtualFileSystem();
            var storage = new MemoryFileProvider();
            files.Mount(new("/store"), storage);
            files.Mount(new("/bundles"), storage);

            var scratch = new FileOdbBackend(files, new("/store/odb"));
            var writing = new ObjectDatabase(scratch);
            var entries = new List<CatalogEntry>();

            foreach (var (address, asset, dependencies, labels) in planned) {
                var id = asset is TestAsset written
                    ? writing.Write(written)
                    // Nothing wrote this one, so the id names content that is not there.
                    : ContentHash.Compute(System.Text.Encoding.UTF8.GetBytes(address));

                entries.Add(
                    new(address, id, bundleName, ContentProvider.Local, [.. dependencies], [.. labels], 0)
                );
            }

            var bundle = new BundleWriter();
            bundle.AddAll(scratch);
            using (var target = files.OpenWrite(new($"/bundles/{writtenAs ?? bundleName}.bundle"))) {
                target.Write(bundle.Build());
            }

            var catalog = new ContentCatalog(
                CatalogFormat.Version,
                default,
                "Windows",
                entries,
                [new(bundleName, "", default, 0, 0, CompressionMethod.Lz4, [])]
            );

            Assets = new(catalog, new LocalBundleSource(files, new("/bundles")));
            return this;
        }
    }
}
