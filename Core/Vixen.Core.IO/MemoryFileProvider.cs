// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Vixen.Core.IO;

/// <summary>A file system that is a dictionary.</summary>
/// <remarks>
///     <para>
///         The default provider for tests, and the one <c>TestApp</c> mounts
///         (<c>docs/plan/12-build-ci-and-testing.md</c>). A test that touches the real filesystem is
///         a test that can fail because of a virus scanner, a slow disk, or another test running at
///         the same time, and none of those are the thing under test.
///     </para>
///     <para>
///         It is also the reference implementation. Every behaviour the interface promises —
///         case-sensitivity, parent creation, deleting only empty directories — is implemented here
///         in a few lines with nothing platform-specific in the way, so a disagreement between this
///         and <see cref="PhysicalFileProvider" /> is a bug in the latter.
///     </para>
/// </remarks>
public sealed class MemoryFileProvider : IFileProvider {
    readonly Lock gate = new();
    readonly Dictionary<VirtualPath, Entry> files = [];
    readonly HashSet<VirtualPath> directories = [VirtualPath.Root];

    /// <inheritdoc />
    public bool IsReadOnly { get; }

    /// <summary>How many files are stored.</summary>
    public int FileCount {
        get {
            lock (gate) {
                return files.Count;
            }
        }
    }

    /// <summary>Creates an empty, writable provider.</summary>
    public MemoryFileProvider() { }

    /// <summary>Creates an empty provider.</summary>
    /// <param name="isReadOnly">Whether to refuse writes, for testing the read-only mount path.</param>
    public MemoryFileProvider(bool isReadOnly) => IsReadOnly = isReadOnly;

    /// <summary>Adds or replaces a file. Ignores <see cref="IsReadOnly" />, because seeding is not writing.</summary>
    /// <param name="path">Where to put it.</param>
    /// <param name="contents">The contents. Copied.</param>
    public void Seed(VirtualPath path, ReadOnlySpan<byte> contents) {
        lock (gate) {
            AddDirectories(path.Parent);
            files[path] = new(contents.ToArray(), DateTimeOffset.UtcNow);
        }
    }

    /// <summary>Adds or replaces a file from UTF-8 text.</summary>
    /// <param name="path">Where to put it.</param>
    /// <param name="contents">The text.</param>
    public void Seed(VirtualPath path, string contents) {
        ArgumentNullException.ThrowIfNull(contents);
        Seed(path, new UTF8Encoding(false).GetBytes(contents));
    }

    /// <inheritdoc />
    public bool Exists(VirtualPath path) {
        lock (gate) {
            return files.ContainsKey(path) || directories.Contains(path);
        }
    }

    /// <inheritdoc />
    public bool TryGetEntry(VirtualPath path, out FileEntry entry) {
        lock (gate) {
            if (files.TryGetValue(path, out var file)) {
                entry = new(path, file.Contents.Length, file.LastWriteUtc, false);
                return true;
            }

            if (directories.Contains(path)) {
                entry = new(path, 0, DateTimeOffset.MinValue, true);
                return true;
            }
        }

        entry = default;
        return false;
    }

    /// <inheritdoc />
    public IEnumerable<FileEntry> Enumerate(VirtualPath directory, bool recursive = false) {
        var found = new List<FileEntry>();

        lock (gate) {
            foreach (var (path, file) in files) {
                if (Matches(directory, path, recursive)) {
                    found.Add(new(path, file.Contents.Length, file.LastWriteUtc, false));
                }
            }

            foreach (var path in directories) {
                if (path != directory && Matches(directory, path, recursive)) {
                    found.Add(new(path, 0, DateTimeOffset.MinValue, true));
                }
            }
        }

        // Ordered, so that an enumeration is reproducible. A content build that hashes a directory
        // listing must not depend on a dictionary's iteration order.
        found.Sort(static (left, right) => left.Path.CompareTo(right.Path));
        return found;
    }

    /// <inheritdoc />
    public ValueTask<Stream> OpenReadAsync(VirtualPath path, CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(OpenRead(path));
    }

    /// <inheritdoc />
    public ValueTask<Stream> OpenWriteAsync(VirtualPath path, CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(OpenWrite(path));
    }

    /// <inheritdoc />
    public Stream OpenRead(VirtualPath path) {
        lock (gate) {
            if (!files.TryGetValue(path, out var file)) {
                throw new FileNotFoundException($"There is no file at '{path.Value}'.", path.Value);
            }

            // Over the stored array, not a copy: a reader that mutates the buffer it was handed is a
            // bug the writable overload exists to prevent.
            return new MemoryStream(file.Contents, writable: false);
        }
    }

    /// <inheritdoc />
    public Stream OpenWrite(VirtualPath path) {
        ThrowIfReadOnly();

        lock (gate) {
            AddDirectories(path.Parent);
        }

        return new CommitOnDisposeStream(this, path);
    }

    /// <inheritdoc />
    public bool Delete(VirtualPath path) {
        ThrowIfReadOnly();

        lock (gate) {
            if (files.Remove(path)) {
                return true;
            }

            if (!directories.Contains(path) || path.IsRoot) {
                return false;
            }

            foreach (var candidate in files.Keys) {
                if (path.Contains(candidate)) {
                    throw new IOException($"The directory '{path.Value}' is not empty.");
                }
            }

            foreach (var candidate in directories) {
                if (candidate != path && path.Contains(candidate)) {
                    throw new IOException($"The directory '{path.Value}' is not empty.");
                }
            }

            return directories.Remove(path);
        }
    }

    /// <inheritdoc />
    public void CreateDirectory(VirtualPath path) {
        ThrowIfReadOnly();

        lock (gate) {
            AddDirectories(path);
        }
    }

    /// <inheritdoc />
    public bool TryMap(VirtualPath path, [NotNullWhen(true)] out IMappedFile? mapped) {
        lock (gate) {
            if (files.TryGetValue(path, out var file)) {
                mapped = new ArrayMapping(file.Contents);
                return true;
            }
        }

        mapped = null;
        return false;
    }

    static bool Matches(VirtualPath directory, VirtualPath candidate, bool recursive) {
        if (!directory.Contains(candidate) || candidate == directory) {
            return false;
        }

        return recursive || candidate.Parent == directory;
    }

    void AddDirectories(VirtualPath directory) {
        // Walks upwards until it meets a directory that already exists, which the root always does.
        while (directories.Add(directory) && !directory.IsRoot) {
            directory = directory.Parent;
        }
    }

    void ThrowIfReadOnly() {
        if (IsReadOnly) {
            throw new NotSupportedException("This provider is read-only.");
        }
    }

    void Commit(VirtualPath path, byte[] contents) {
        lock (gate) {
            files[path] = new(contents, DateTimeOffset.UtcNow);
        }
    }

    readonly record struct Entry(byte[] Contents, DateTimeOffset LastWriteUtc);

    /// <summary>A stream whose contents become the file when it is disposed.</summary>
    /// <remarks>
    ///     Committing on close rather than on write is what makes a half-written file invisible,
    ///     which is the property real filesystems only have when the writer is careful and this one
    ///     has for free.
    /// </remarks>
    sealed class CommitOnDisposeStream(MemoryFileProvider provider, VirtualPath path) : MemoryStream {
        bool committed;

        protected override void Dispose(bool disposing) {
            if (disposing && !committed) {
                committed = true;
                provider.Commit(path, ToArray());
            }

            base.Dispose(disposing);
        }
    }

    sealed class ArrayMapping(byte[] contents) : IMappedFile {
        public ReadOnlyMemory<byte> Memory => contents;

        public void Dispose() { }
    }
}
