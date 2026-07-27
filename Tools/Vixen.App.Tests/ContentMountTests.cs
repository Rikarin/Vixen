// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Assets;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Serialization.Storage;
using Xunit;

namespace Vixen.App.Tests;

/// <summary>Something to put in a bundle, so the test loads an asset rather than a catalog entry.</summary>
[DataContract("HostTestAsset")]
public sealed class HostTestAsset {
    /// <summary>What it is called.</summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>
///     Over real bundles written to a real directory, because the thing under test is whether the
///     boot path finds content on disk — and a mock file system would agree with any answer.
/// </summary>
public sealed class ContentMountTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), $"vixen-content-{Guid.NewGuid():N}");

    public void Dispose() {
        if (Directory.Exists(root)) {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    ///     The gap this closes. <c>Vixen.Sdk</c> has been copying a content build into
    ///     <c>Content/</c> beside the binary since Phase 3, and nothing had ever opened it.
    /// </summary>
    [Fact]
    public async Task AContentBuildBesideTheBinaryIsFoundAndLoadable() {
        var app = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
        Publish(Path.Combine(app, ContentMount.FolderName), "ui/hero");

        var files = new VirtualFileSystem();
        files.Mount(MountPoints.App, new PhysicalFileProvider(app, isReadOnly: true));

        using var mount = ContentMount.Open(files);

        Assert.Null(mount.Reason);
        Assert.False(mount.IsLoose);

        var assets = Assert.IsType<AssetManager>(mount.Assets);
        var handle = assets.Load<HostTestAsset>("ui/hero", TestContext.Current.CancellationToken);

        Assert.Equal("hero", handle.Result.Name);
        await Task.CompletedTask;
    }

    /// <summary>
    ///     Doc 17 Q5b: a shipped build may be pointed somewhere else so that a bug only reproducible
    ///     in a release configuration can be poked at. The directory wins over what shipped.
    /// </summary>
    [Fact]
    public void LooseContentIsReadInsteadOfWhatTheApplicationShippedWith() {
        var app = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
        Publish(Path.Combine(app, ContentMount.FolderName), "ui/shipped");

        var loose = Path.Combine(root, "loose");
        Publish(loose, "ui/loose");

        var files = new VirtualFileSystem();
        files.Mount(MountPoints.App, new PhysicalFileProvider(app, isReadOnly: true));

        using var mount = ContentMount.Open(files, loose);

        Assert.True(mount.IsLoose);
        Assert.True(mount.Assets!.Catalog.Contains("ui/loose"));
        Assert.False(mount.Assets.Catalog.Contains("ui/shipped"));
    }

    /// <summary>
    ///     A sample that draws a triangle, a batch tool and a test each have nothing to load. A host
    ///     that refused to start over it would make the smallest possible program the hardest one to
    ///     write.
    /// </summary>
    [Fact]
    public void AnApplicationWithNoContentStartsAndSaysWhy() {
        var app = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;

        var files = new VirtualFileSystem();
        files.Mount(MountPoints.App, new PhysicalFileProvider(app, isReadOnly: true));

        using var mount = ContentMount.Open(files);

        Assert.Null(mount.Assets);
        Assert.Contains(ContentMount.CatalogFileName, mount.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A catalog truncated by a failed download or corrupted on a phone's flash happens in the
    ///     field, and an application that threw over it could not even show the message saying why.
    /// </summary>
    [Fact]
    public void ACorruptCatalogIsReportedRatherThanThrown() {
        var app = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
        var content = Directory.CreateDirectory(Path.Combine(app, ContentMount.FolderName)).FullName;
        File.WriteAllBytes(Path.Combine(content, ContentMount.CatalogFileName), [1, 2, 3, 4, 5, 6, 7, 8]);

        var files = new VirtualFileSystem();
        files.Mount(MountPoints.App, new PhysicalFileProvider(app, isReadOnly: true));

        using var mount = ContentMount.Open(files);

        Assert.Null(mount.Assets);
        Assert.NotNull(mount.Reason);
    }

    [Fact]
    public void LooseContentPointedAtNothingSaysSoRatherThanMountingIt() {
        var files = new VirtualFileSystem();
        using var mount = ContentMount.Open(files, Path.Combine(root, "nowhere"));

        Assert.Null(mount.Assets);
        Assert.True(mount.IsLoose);
        Assert.Contains("nowhere", mount.Reason, StringComparison.Ordinal);
    }

    /// <summary>Writes a one-asset content build the way `vixen content build` lays one out.</summary>
    static void Publish(string directory, string address) {
        Directory.CreateDirectory(directory);

        var scratch = new VirtualFileSystem();
        var memory = new MemoryFileProvider();
        scratch.Mount(new("/odb"), memory);

        var backend = new FileOdbBackend(scratch, new("/odb"));
        var id = new ObjectDatabase(backend).Write(new HostTestAsset { Name = address.Split('/')[^1] });

        var bundle = new BundleWriter();
        bundle.AddAll(backend);
        File.WriteAllBytes(Path.Combine(directory, "Main.bundle"), bundle.Build());

        var catalog = new ContentCatalog(
            CatalogFormat.Version,
            default,
            "Windows",
            [new(address, id, "Main", ContentProvider.Local, [], [], 0)],
            [new("Main", "", default, 0, 0, CompressionMethod.Lz4, [])]
        );

        File.WriteAllBytes(Path.Combine(directory, ContentMount.CatalogFileName), CatalogFormat.Write(catalog));
    }
}
