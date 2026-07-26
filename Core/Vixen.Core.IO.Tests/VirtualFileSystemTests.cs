// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Core.IO.Tests;

public class VirtualFileSystemTests {
    [Fact]
    public void AMountedProviderSeesPathsRelativeToItsMount() {
        var vfs = new VirtualFileSystem();
        var provider = new MemoryFileProvider();
        provider.Seed(new("/textures/x.png"), "content");
        vfs.Mount(MountPoints.App, provider);

        Assert.True(vfs.TryResolve(new("/app/textures/x.png"), out var resolved, out var providerPath));
        Assert.Same(provider, resolved);
        Assert.Equal("/textures/x.png", providerPath.Value);
        Assert.True(vfs.Exists(new("/app/textures/x.png")));
    }

    [Fact]
    public void TheLongestMountWins() {
        var vfs = new VirtualFileSystem();
        var app = new MemoryFileProvider();
        var dlc = new MemoryFileProvider();

        // Registered shortest-first, so a table that kept insertion order would answer with `app`.
        vfs.Mount(MountPoints.App, app);
        vfs.Mount(new("/app/dlc"), dlc);

        Assert.True(vfs.TryResolve(new("/app/dlc/a.bin"), out var resolved, out var providerPath));
        Assert.Same(dlc, resolved);
        Assert.Equal("/a.bin", providerPath.Value);

        Assert.True(vfs.TryResolve(new("/app/other.bin"), out resolved, out _));
        Assert.Same(app, resolved);
    }

    [Fact]
    public void AMountDoesNotCaptureASiblingWithTheSamePrefix() {
        var vfs = new VirtualFileSystem();
        vfs.Mount(MountPoints.App, new MemoryFileProvider());

        Assert.False(vfs.TryResolve(new("/application/x"), out _, out _));
    }

    [Fact]
    public void MountingOverAnExistingMountReplacesIt() {
        var vfs = new VirtualFileSystem();
        var first = new MemoryFileProvider();
        var second = new MemoryFileProvider();

        vfs.Mount(MountPoints.App, first);
        vfs.Mount(MountPoints.App, second);

        Assert.Single(vfs.Mounts);
        Assert.True(vfs.TryResolve(new("/app/x"), out var resolved, out _));
        Assert.Same(second, resolved);
    }

    [Fact]
    public void UnmountingRemovesTheProvider() {
        var vfs = new VirtualFileSystem();
        vfs.Mount(MountPoints.App, new MemoryFileProvider());

        Assert.True(vfs.Unmount(MountPoints.App));
        Assert.False(vfs.Unmount(MountPoints.App));
        Assert.False(vfs.Exists(new("/app/x")));
    }

    [Fact]
    public void AnUnmountedPathIsAbsentForQueriesAndAnErrorForOpens() {
        var vfs = new VirtualFileSystem();

        // Absent, because "is there a file at /nowhere/x" has an answer and it is no.
        Assert.False(vfs.Exists(new("/nowhere/x")));
        Assert.False(vfs.TryGetEntry(new("/nowhere/x"), out _));

        // An error, because "open /nowhere/x" does not: the caller asked for something that cannot
        // exist, and returning a missing-file error would send them looking for the file.
        var thrown = Assert.Throws<DirectoryNotFoundException>(() => vfs.OpenRead(new("/nowhere/x")));
        Assert.Contains("nothing is mounted", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadingAndWritingRoundTrip() {
        var vfs = new VirtualFileSystem();
        vfs.Mount(MountPoints.Data, new MemoryFileProvider());
        var path = new VirtualPath("/data/saves/slot1.json");

        await vfs.WriteAllTextAsync(path, "{\"level\":3}", TestContext.Current.CancellationToken);

        Assert.True(vfs.Exists(path));
        Assert.Equal("{\"level\":3}", await vfs.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.True(vfs.TryGetEntry(path, out var entry));
        Assert.Equal(path, entry.Path);
        Assert.Equal(11, entry.Length);
        Assert.False(entry.IsDirectory);
    }

    [Fact]
    public async Task TextIsWrittenWithoutAByteOrderMark() {
        var vfs = new VirtualFileSystem();
        vfs.Mount(MountPoints.Data, new MemoryFileProvider());
        var path = new VirtualPath("/data/x.txt");

        await vfs.WriteAllTextAsync(path, "hello", TestContext.Current.CancellationToken);
        var bytes = await vfs.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal("hello"u8.ToArray(), bytes);
    }

    [Fact]
    public void EnumeratingTheRootListsTheMounts() {
        var vfs = new VirtualFileSystem();
        vfs.Mount(MountPoints.App, new MemoryFileProvider());
        vfs.Mount(MountPoints.Data, new MemoryFileProvider());

        var entries = vfs.Enumerate(VirtualPath.Root).ToArray();

        Assert.Equal(2, entries.Length);
        Assert.All(entries, entry => Assert.True(entry.IsDirectory));
        Assert.Contains(entries, entry => entry.Path == MountPoints.App);
        Assert.Contains(entries, entry => entry.Path == MountPoints.Data);
    }

    [Fact]
    public void EnumerationReturnsFullVirtualPaths() {
        var vfs = new VirtualFileSystem();
        var provider = new MemoryFileProvider();
        provider.Seed(new("/a/x.png"), "x");
        provider.Seed(new("/a/y.png"), "y");
        provider.Seed(new("/b/z.png"), "z");
        vfs.Mount(MountPoints.App, provider);

        var shallow = vfs.Enumerate(new("/app")).Select(entry => entry.Path.Value).ToArray();
        Assert.Equal(["/app/a", "/app/b"], shallow);

        var deep = vfs.Enumerate(new("/app"), recursive: true)
            .Where(entry => !entry.IsDirectory)
            .Select(entry => entry.Path.Value)
            .ToArray();

        Assert.Equal(["/app/a/x.png", "/app/a/y.png", "/app/b/z.png"], deep);
    }

    [Fact]
    public void ARecursiveEnumerationDescendsIntoNestedMounts() {
        var vfs = new VirtualFileSystem();
        var app = new MemoryFileProvider();
        app.Seed(new("/base.txt"), "base");
        var dlc = new MemoryFileProvider();
        dlc.Seed(new("/extra.txt"), "extra");

        vfs.Mount(MountPoints.App, app);
        vfs.Mount(new("/app/dlc"), dlc);

        var files = vfs.Enumerate(new("/app"), recursive: true)
            .Where(entry => !entry.IsDirectory)
            .Select(entry => entry.Path.Value)
            .ToArray();

        Assert.Contains("/app/base.txt", files);
        Assert.Contains("/app/dlc/extra.txt", files);
    }

    [Fact]
    public void AReadOnlyMountRefusesWrites() {
        var vfs = new VirtualFileSystem();
        vfs.Mount(MountPoints.App, new MemoryFileProvider(isReadOnly: true));

        Assert.Throws<NotSupportedException>(() => vfs.OpenWrite(new("/app/x")));
        Assert.Throws<NotSupportedException>(() => vfs.Delete(new("/app/x")));
    }

    [Fact]
    public async Task ReadingUsesTheMappingWhenTheProviderHasOne() {
        var vfs = new VirtualFileSystem();
        var provider = new MemoryFileProvider();
        provider.Seed(new("/x.bin"), [1, 2, 3, 4]);
        vfs.Mount(MountPoints.App, provider);

        Assert.True(vfs.TryMap(new("/app/x.bin"), out var mapped));

        using (mapped) {
            Assert.Equal([1, 2, 3, 4], mapped.Memory.ToArray());
        }

        Assert.Equal([1, 2, 3, 4], await vfs.ReadAllBytesAsync(new("/app/x.bin"), TestContext.Current.CancellationToken));
    }
}
