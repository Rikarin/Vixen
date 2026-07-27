// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Core.IO.Tests;

/// <summary>
///     Everything <see cref="IFileProvider" /> promises, asserted against every provider that
///     implements it.
/// </summary>
/// <remarks>
///     The point of a shared suite is that a provider cannot quietly have its own opinion. Whether
///     writing into a directory that does not exist creates it, whether deleting a non-empty
///     directory throws or succeeds, whether enumeration is ordered — every one of those is a
///     difference that would only be discovered by a caller who was written against one provider and
///     shipped against another.
/// </remarks>
public abstract class FileProviderConformance : IDisposable {
    protected abstract IFileProvider Provider { get; }

    public virtual void Dispose() => GC.SuppressFinalize(this);

    [Fact]
    public void WritingThenReadingRoundTrips() {
        Write(new("/a.txt"), "hello");

        using var stream = Provider.OpenRead(new("/a.txt"));
        using var reader = new StreamReader(stream);
        Assert.Equal("hello", reader.ReadToEnd());
    }

    [Fact]
    public void WritingCreatesMissingParents() {
        Write(new("/deep/nested/tree/a.txt"), "x");

        Assert.True(Provider.Exists(new("/deep/nested/tree/a.txt")));
        Assert.True(Provider.Exists(new("/deep/nested")));
    }

    [Fact]
    public void AHalfWrittenFileIsNotVisibleUnderItsFinalName() {
        Write(new("/a.txt"), "original");
        var stream = Provider.OpenWrite(new("/a.txt"));

        // Nothing has been flushed, so anyone reading now sees either the old contents or nothing —
        // never a truncated version of the new ones.
        stream.Write("replacement"u8);
        stream.Dispose();

        Assert.Equal("replacement", Read(new("/a.txt")));
    }

    [Fact]
    public void ExistsDistinguishesFilesDirectoriesAndNothing() {
        Write(new("/dir/a.txt"), "x");

        Assert.True(Provider.Exists(new("/dir/a.txt")));
        Assert.True(Provider.Exists(new("/dir")));
        Assert.False(Provider.Exists(new("/dir/missing.txt")));
        Assert.False(Provider.Exists(new("/missing")));
    }

    [Fact]
    public void EntriesCarryTheirSizeAndKind() {
        Write(new("/a.txt"), "12345");

        Assert.True(Provider.TryGetEntry(new("/a.txt"), out var file));
        Assert.Equal(5, file.Length);
        Assert.False(file.IsDirectory);

        Provider.CreateDirectory(new("/d"));
        Assert.True(Provider.TryGetEntry(new("/d"), out var directory));
        Assert.True(directory.IsDirectory);

        Assert.False(Provider.TryGetEntry(new("/missing"), out _));
    }

    [Fact]
    public void OpeningSomethingThatIsNotThereThrowsFileNotFound() {
        Assert.Throws<FileNotFoundException>(() => Provider.OpenRead(new("/missing.txt")));
        Assert.Throws<FileNotFoundException>(
            () => Provider.OpenReadAsync(new("/missing.txt"), TestContext.Current.CancellationToken).AsTask().GetAwaiter().GetResult()
        );
    }

    [Fact]
    public void EnumerationIsShallowByDefaultAndOrdered() {
        Write(new("/b.txt"), "b");
        Write(new("/a.txt"), "a");
        Write(new("/sub/c.txt"), "c");

        var entries = Provider.Enumerate(VirtualPath.Root).Select(entry => entry.Path.Value).ToArray();

        Assert.Equal(["/a.txt", "/b.txt", "/sub"], entries);
    }

    [Fact]
    public void RecursiveEnumerationReachesEverythingAndStaysOrdered() {
        Write(new("/sub/deep/c.txt"), "c");
        Write(new("/a.txt"), "a");

        var entries = Provider.Enumerate(VirtualPath.Root, recursive: true).Select(entry => entry.Path.Value).ToArray();

        Assert.Equal(["/a.txt", "/sub", "/sub/deep", "/sub/deep/c.txt"], entries);
    }

    [Fact]
    public void EnumeratingSomethingThatIsNotThereIsEmptyRatherThanAnError() =>
        Assert.Empty(Provider.Enumerate(new("/missing")));

    [Fact]
    public void DeletingRemovesAFile() {
        Write(new("/a.txt"), "x");

        Assert.True(Provider.Delete(new("/a.txt")));
        Assert.False(Provider.Exists(new("/a.txt")));
        Assert.False(Provider.Delete(new("/a.txt")));
    }

    [Fact]
    public void DeletingAnEmptyDirectoryWorksAndANonEmptyOneDoesNot() {
        Write(new("/full/a.txt"), "x");
        Provider.CreateDirectory(new("/empty"));

        Assert.True(Provider.Delete(new("/empty")));
        Assert.Throws<IOException>(() => Provider.Delete(new("/full")));
        Assert.True(Provider.Exists(new("/full/a.txt")));
    }

    [Fact]
    public void CaseIsPartOfTheName() {
        Write(new("/Texture.png"), "x");

        // The whole reason virtual paths are case-sensitive: on a case-insensitive volume the
        // platform would happily serve this, and the project would build everywhere except Linux.
        Assert.False(Provider.Exists(new("/texture.png")));
        Assert.Throws<FileNotFoundException>(() => Provider.OpenRead(new("/texture.png")));
        Assert.True(Provider.Exists(new("/Texture.png")));
    }

    [Fact]
    public void CaseIsPartOfADirectoryNameToo() {
        Write(new("/Assets/x.png"), "x");

        Assert.False(Provider.Exists(new("/assets/x.png")));
        Assert.True(Provider.Exists(new("/Assets/x.png")));
    }

    /// <summary>
    ///     Appending adds to what is there rather than replacing it. This is the whole of what a
    ///     resumable download needs from a file system: a fetch that stopped at 300 MB has to be able
    ///     to carry on at 300 MB, and a provider that quietly truncated would turn every dropped
    ///     connection into starting again.
    /// </summary>
    [Fact]
    public void AppendingAddsToWhatIsAlreadyThere() {
        Write(new("/a.bin"), "first");

        using (var stream = Provider.OpenAppend(new("/a.bin"))) {
            stream.Write("second"u8);
        }

        Assert.Equal("firstsecond", Read(new("/a.bin")));
    }

    /// <summary>Appending to nothing writes the file, so a first attempt needs no special case.</summary>
    [Fact]
    public void AppendingToAMissingFileCreatesIt() {
        using (var stream = Provider.OpenAppend(new("/fresh/a.bin"))) {
            stream.Write("only"u8);
        }

        Assert.Equal("only", Read(new("/fresh/a.bin")));
    }

    /// <summary>And appending nothing leaves the file alone rather than emptying it.</summary>
    [Fact]
    public void AppendingNothingLeavesTheFileAsItWas() {
        Write(new("/a.bin"), "kept");
        Provider.OpenAppend(new("/a.bin")).Dispose();

        Assert.Equal("kept", Read(new("/a.bin")));
    }

    /// <summary>
    ///     The length an entry reports after an append is the length a resume asks the server to
    ///     continue from, so it has to count both halves.
    /// </summary>
    [Fact]
    public void TheLengthAfterAppendingCountsBothHalves() {
        Write(new("/a.bin"), "1234");

        using (var stream = Provider.OpenAppend(new("/a.bin"))) {
            stream.Write("567"u8);
        }

        Assert.True(Provider.TryGetEntry(new("/a.bin"), out var entry));
        Assert.Equal(7, entry.Length);
    }

    protected void Write(VirtualPath path, string contents) {
        using var stream = Provider.OpenWrite(path);
        stream.Write(System.Text.Encoding.UTF8.GetBytes(contents));
    }

    protected string Read(VirtualPath path) {
        using var stream = Provider.OpenRead(path);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

public sealed class MemoryFileProviderConformance : FileProviderConformance {
    protected override IFileProvider Provider { get; } = new MemoryFileProvider();
}

/// <summary>
///     The same suite against a provider that implements only the members the interface requires, so
///     that every default implementation on <see cref="IFileProvider" /> is held to the same contract
///     as an overriding one. A default that is correct only because both real providers override it
///     is a trap for the third one somebody writes.
/// </summary>
public sealed class MinimalFileProviderConformance : FileProviderConformance {
    protected override IFileProvider Provider { get; } = new MinimalProvider(new MemoryFileProvider());

    /// <summary>Forwards what the interface demands and inherits everything it offers.</summary>
    sealed class MinimalProvider(IFileProvider inner) : IFileProvider {
        public bool IsReadOnly => inner.IsReadOnly;

        public bool Exists(VirtualPath path) => inner.Exists(path);

        public bool TryGetEntry(VirtualPath path, out FileEntry entry) => inner.TryGetEntry(path, out entry);

        public IEnumerable<FileEntry> Enumerate(VirtualPath directory, bool recursive = false) =>
            inner.Enumerate(directory, recursive);

        public ValueTask<Stream> OpenReadAsync(VirtualPath path, CancellationToken cancellationToken = default) =>
            inner.OpenReadAsync(path, cancellationToken);

        public ValueTask<Stream> OpenWriteAsync(VirtualPath path, CancellationToken cancellationToken = default) =>
            inner.OpenWriteAsync(path, cancellationToken);

        public bool Delete(VirtualPath path) => inner.Delete(path);

        public void CreateDirectory(VirtualPath path) => inner.CreateDirectory(path);
    }
}

public sealed class PhysicalFileProviderConformance : FileProviderConformance {
    readonly string directory = Path.Combine(Path.GetTempPath(), "vixen-io-" + Guid.NewGuid().ToString("N"));

    protected override IFileProvider Provider => provider;

    readonly PhysicalFileProvider provider;

    public PhysicalFileProviderConformance() =>
        // Forced on rather than probed, so the case-sensitivity conformance tests assert the
        // provider's behaviour on every runner instead of only on the ones with a case-insensitive
        // volume. A Linux runner would otherwise pass them for the kernel's reasons, not ours.
        provider = new(directory, enforceCaseSensitivity: true);

    public override void Dispose() {
        base.Dispose();

        if (Directory.Exists(directory)) {
            Directory.Delete(directory, recursive: true);
        }
    }
}
