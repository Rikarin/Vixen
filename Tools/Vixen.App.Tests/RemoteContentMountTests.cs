// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.IO.Hashing;
using Vixen.Assets;
using Vixen.Core.IO;
using Vixen.Core.Serialization.Storage;
using Xunit;

namespace Vixen.App.Tests;

/// <summary>
///     The half of the content system that was built and never plugged in: a bundle a build declared
///     remote, downloaded by a running application.
/// </summary>
/// <remarks>
///     <para>
///         <b>Every piece of this existed.</b> <c>BundleCache</c> fetches with ranges, resumes and
///         verifies; <c>RemoteBundleSource</c> maps what it cached; <c>RoutedBundleSource</c> picks
///         between local and remote by whether the catalog gave a bundle a URL. What no test covered
///         is the only question a game asks: does the <i>host</i> put any of that behind
///         <c>Services.Assets</c>. It did not — <c>ContentMount</c> built a bare
///         <c>LocalBundleSource</c>, so a remote group threw <c>BundleUnavailableException</c> on the
///         first address in it.
///     </para>
///     <para>
///         ⚠ <b>Over a real catalog and real bundle bytes through the virtual file system</b>, for
///         <c>ContentMountTests</c>' reason: the thing under test is the wiring, and a fake catalog
///         would agree with any of it.
///     </para>
/// </remarks>
public sealed class RemoteContentMountTests : IDisposable {
    const string Url = "https://cdn.example/content/Dlc_0123456789abcdef.bundle";

    readonly TemporaryFileSystemHost files = new();

    public void Dispose() => files.Dispose();

    [Fact]
    public async Task A_bundle_the_catalog_serves_from_a_url_is_downloaded_and_loaded() {
        using var transport = new FakeContentTransport();
        Publish(transport);

        using var application = Build(transport);

        var assets = Assert.IsType<AssetManager>(application.Services.Assets);
        var handle = assets.LoadAsync<HostTestAsset>("dlc/hero", TestContext.Current.CancellationToken);

        Assert.Equal("hero", (await handle.Completion.WaitAsync(TestContext.Current.CancellationToken)).Name);
        Assert.Equal([Url], transport.Requested);

        // And it landed in the cache, which is what makes the second run of the game not download it
        // again — the property the whole cache exists for.
        Assert.NotNull(application.Services.Content.Cache);
        Assert.True(application.Services.Content.Cache.TotalSize() > 0);
    }

    /// <summary>
    ///     ⚠ The pre-download path, which is the one a "get this DLC now, play it later" button uses.
    ///     It has to be answerable <i>before</i> anything is loaded, or the prompt cannot say what the
    ///     download will cost.
    /// </summary>
    [Fact]
    public async Task What_a_download_would_cost_is_answerable_before_anything_is_loaded() {
        using var transport = new FakeContentTransport();
        var size = Publish(transport);

        using var application = Build(transport);
        var assets = application.Services.Assets!;

        Assert.Equal(size, assets.DownloadSize("dlc/hero"));

        await assets.DownloadAsync(["dlc/hero"], cancellationToken: TestContext.Current.CancellationToken);

        // Cached, so it now costs nothing — and nothing was loaded on the way, which is the
        // difference between this and LoadAsync.
        Assert.Equal(0, assets.DownloadSize("dlc/hero"));
        Assert.Equal(0, assets.LoadedCount);
    }

    /// <summary>
    ///     A build whose groups are all local gets no cache, no <c>RemoteBundleSource</c> and — the
    ///     part that matters on a phone — no <c>HttpClient</c>. The reason says which it was, because
    ///     "downloads do not work" is otherwise a mystery with a group setting for an answer.
    /// </summary>
    [Fact]
    public void A_build_with_nothing_remote_pays_for_none_of_it() {
        Publish(transport: null);

        using var application = Build(transport: null);

        Assert.NotNull(application.Services.Assets);
        Assert.Null(application.Services.Content.Cache);
        Assert.Contains("URL", application.Services.Content.RemoteReason, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ A transport handed in is the caller's, which is <c>WithGraphics</c>' rule: it is a client
    ///     somebody configured with an authorisation header or a certificate pin, and it may well
    ///     outlive the game's own content mount.
    /// </summary>
    [Fact]
    public void A_transport_handed_in_is_not_disposed_with_the_application() {
        using var transport = new FakeContentTransport();
        Publish(transport);

        var application = Build(transport);

        application.Initialise();
        application.Dispose();

        Assert.False(transport.Disposed);
    }

    VixenApplication Build(IContentTransport? transport) {
        var builder = VixenApp.Create(["--vixen-workers", "1", "--vixen-frame-limit", "0"])
            .WithPlatform(new Vixen.Platform.Headless.HeadlessPlatform(new() { FileSystem = files }));

        if (transport is not null) {
            builder = builder.WithContent(transport);
        }

        return builder.Build(new SilentGame());
    }

    /// <summary>
    ///     Writes a content build whose one bundle is served from a URL rather than shipped, which is
    ///     what a group with <c>loadPath: Remote</c> produces — and serves those bytes, unless there
    ///     is no transport, in which case the bundle ships locally instead.
    /// </summary>
    /// <returns>How big the download is.</returns>
    long Publish(FakeContentTransport? transport) {
        var directory = Directory
            .CreateDirectory(Path.Combine(files.ApplicationDirectory, ContentMount.FolderName))
            .FullName;

        var scratch = new VirtualFileSystem();
        scratch.Mount(new("/odb"), new MemoryFileProvider());

        var backend = new FileOdbBackend(scratch, new("/odb"));
        var id = new ObjectDatabase(backend).Write(new HostTestAsset { Name = "hero" });

        var writer = new BundleWriter();
        writer.AddAll(backend);

        var bytes = writer.Build();
        var remote = transport is not null;

        if (remote) {
            transport!.Serve(Url, bytes);
        } else {
            File.WriteAllBytes(Path.Combine(directory, "Dlc.bundle"), bytes);
        }

        var catalog = new ContentCatalog(
            CatalogFormat.Version,
            default,
            "Windows",
            [new("dlc/hero", id, "Dlc", remote ? ContentProvider.Remote : ContentProvider.Local, [], [], 0)],
            [
                new(
                    "Dlc",
                    remote ? Url : string.Empty,
                    ContentHash.Compute(bytes),
                    bytes.Length,
                    Crc32.HashToUInt32(bytes),
                    CompressionMethod.Lz4,
                    []
                )
            ]
        );

        File.WriteAllBytes(Path.Combine(directory, ContentMount.CatalogFileName), CatalogFormat.Write(catalog));

        return bytes.Length;
    }

    sealed class SilentGame : Game;
}
