// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Core.IO;

/// <summary>A directory on the real filesystem, mounted as a subtree of the virtual one.</summary>
/// <remarks>
///     <para>
///         This is the only type in the engine that is allowed to know what a platform path looks
///         like. Everything above it says <c>/project/Assets/x.png</c>; the translation to a
///         backslash, a drive letter, or a sandbox container directory happens here and nowhere
///         else.
///     </para>
///     <para>
///         <b>Case is enforced where the filesystem will not enforce it.</b> On a case-insensitive
///         volume — every default macOS and Windows install — asking for <c>Texture.PNG</c> when the
///         file is <c>texture.png</c> succeeds, and keeps succeeding until someone builds on Linux.
///         So the provider checks that the name on disk matches the name asked for, ordinally, and
///         reports the file as missing if it does not. The check is on by default only where it is
///         needed: the constructor probes the volume once, and on a case-sensitive filesystem it
///         costs nothing because the kernel is already doing it.
///     </para>
///     <para>
///         Verified directories are cached, so the cost is one directory probe per file opened and
///         not one per path segment. It can be turned off outright for a build that has measured it
///         and does not want it.
///     </para>
/// </remarks>
public sealed class PhysicalFileProvider : IFileProvider {
    readonly string root;
    readonly Lock cacheGate = new();
    readonly HashSet<string> verifiedDirectories = new(StringComparer.Ordinal);

    /// <summary>The directory on disk this provider serves.</summary>
    public string RootDirectory => root;

    /// <inheritdoc />
    public bool IsReadOnly { get; }

    /// <summary>Whether names are checked against their real casing on disk.</summary>
    public bool EnforcesCaseSensitivity { get; }

    /// <summary>Serves a directory on disk.</summary>
    /// <param name="rootDirectory">The directory. Created if it does not exist.</param>
    /// <param name="isReadOnly">Whether to refuse writes.</param>
    /// <param name="enforceCaseSensitivity">
    ///     Whether to reject a path whose casing differs from the name on disk. When
    ///     <see langword="null" />, the volume is probed once and the check is enabled only if the
    ///     volume is case-insensitive.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="rootDirectory" /> is empty.</exception>
    public PhysicalFileProvider(string rootDirectory, bool isReadOnly = false, bool? enforceCaseSensitivity = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        IsReadOnly = isReadOnly;

        if (!isReadOnly) {
            Directory.CreateDirectory(root);
        }

        EnforcesCaseSensitivity = enforceCaseSensitivity ?? IsCaseInsensitive(root, canWrite: !isReadOnly);
    }

    /// <inheritdoc />
    public bool Exists(VirtualPath path) {
        var os = ToOsPath(path);
        return (File.Exists(os) || Directory.Exists(os)) && MatchesOnDisk(path);
    }

    /// <inheritdoc />
    public bool TryGetEntry(VirtualPath path, out FileEntry entry) {
        var os = ToOsPath(path);

        if (File.Exists(os) && MatchesOnDisk(path)) {
            var info = new FileInfo(os);
            entry = new(path, info.Length, info.LastWriteTimeUtc, false);
            return true;
        }

        if (Directory.Exists(os) && MatchesOnDisk(path)) {
            entry = new(path, 0, new DirectoryInfo(os).LastWriteTimeUtc, true);
            return true;
        }

        entry = default;
        return false;
    }

    /// <inheritdoc />
    public IEnumerable<FileEntry> Enumerate(VirtualPath directory, bool recursive = false) {
        var os = ToOsPath(directory);
        var found = new List<FileEntry>();

        if (!Directory.Exists(os)) {
            return found;
        }

        var options = new EnumerationOptions {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System
        };

        foreach (var entry in Directory.EnumerateFileSystemEntries(os, "*", options)) {
            // A name that cannot be spelled as a virtual path — one containing a backslash on a
            // Unix volume — is skipped rather than mangled. It is not addressable through the VFS,
            // and inventing a name for it would make it addressable as something it is not.
            if (!TryToVirtual(entry, out var path)) {
                continue;
            }

            if (Directory.Exists(entry)) {
                found.Add(new(path, 0, new DirectoryInfo(entry).LastWriteTimeUtc, true));
            } else {
                var info = new FileInfo(entry);
                found.Add(new(path, info.Length, info.LastWriteTimeUtc, false));
            }
        }

        // Materialised and sorted, because the interface promises an order. Streaming would be
        // cheaper for a directory large enough to matter, and would hand the content build a
        // listing whose order depends on which filesystem the developer happens to use.
        found.Sort(static (left, right) => left.Path.CompareTo(right.Path));
        return found;
    }

    /// <inheritdoc />
    public ValueTask<Stream> OpenReadAsync(VirtualPath path, CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();
        var os = OpenableFile(path);

        return ValueTask.FromResult<Stream>(
            new FileStream(
                os,
                new FileStreamOptions {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                }
            )
        );
    }

    /// <inheritdoc />
    public ValueTask<Stream> OpenWriteAsync(VirtualPath path, CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();
        var os = PrepareForWrite(path);

        return ValueTask.FromResult<Stream>(
            new FileStream(
                os,
                new FileStreamOptions {
                    Mode = FileMode.Create,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous
                }
            )
        );
    }

    /// <inheritdoc />
    public ValueTask<Stream> OpenAppendAsync(VirtualPath path, CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();
        var os = PrepareForWrite(path);

        return ValueTask.FromResult<Stream>(
            new FileStream(
                os,
                new FileStreamOptions {
                    Mode = FileMode.Append,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous
                }
            )
        );
    }

    /// <inheritdoc />
    public Stream OpenRead(VirtualPath path) =>
        new FileStream(OpenableFile(path), FileMode.Open, FileAccess.Read, FileShare.Read);

    /// <inheritdoc />
    public Stream OpenWrite(VirtualPath path) =>
        new FileStream(PrepareForWrite(path), FileMode.Create, FileAccess.Write, FileShare.None);

    /// <inheritdoc />
    public Stream OpenAppend(VirtualPath path) =>
        new FileStream(PrepareForWrite(path), FileMode.Append, FileAccess.Write, FileShare.None);

    /// <inheritdoc />
    public bool Delete(VirtualPath path) {
        ThrowIfReadOnly();
        var os = ToOsPath(path);

        if (File.Exists(os) && MatchesOnDisk(path)) {
            File.Delete(os);
            return true;
        }

        if (Directory.Exists(os) && MatchesOnDisk(path)) {
            // Never recursive. A VFS delete that quietly removed a subtree would be the single most
            // destructive call in the engine, reachable from a typo in a virtual path.
            Directory.Delete(os, recursive: false);
            Forget(os);
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public void CreateDirectory(VirtualPath path) {
        ThrowIfReadOnly();
        Directory.CreateDirectory(ToOsPath(path));
    }

    /// <inheritdoc />
    public bool TryMap(VirtualPath path, [NotNullWhen(true)] out IMappedFile? mapped) {
        mapped = null;
        var os = ToOsPath(path);

        if (!File.Exists(os) || !MatchesOnDisk(path)) {
            return false;
        }

        var length = new FileInfo(os).Length;

        // A mapping is exposed as ReadOnlyMemory<byte>, whose length is an int. Above two gigabytes
        // the caller wants a stream and a window, not one Memory, so this answers honestly instead
        // of throwing at the boundary.
        if (length is 0 or > int.MaxValue) {
            return false;
        }

        mapped = MemoryMappedFileMapping.Open(os, (int)length);
        return true;
    }

    static bool IsCaseInsensitive(string directory, bool canWrite) {
        // One probe, at construction. Asking the volume beats asking the OS: an APFS volume can be
        // formatted either way, and a case-sensitive volume can be mounted on Windows.
        //
        // When in doubt the answer is "insensitive", which turns the check on. A false positive
        // costs a cached directory listing; a false negative costs a bug on somebody else's machine.
        if (!Directory.Exists(directory)) {
            return true;
        }

        try {
            if (canWrite) {
                var probe = Path.Combine(directory, ".vixen-case-probe");

                try {
                    File.WriteAllBytes(probe, []);
                    return File.Exists(Path.Combine(directory, ".VIXEN-CASE-PROBE"));
                } finally {
                    File.Delete(probe);
                }
            }

            // A read-only root must not be written to in order to find out what it is, so the probe
            // uses something already there: take any entry with a cased letter in its name and ask
            // for it with the case flipped.
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory)) {
                var name = Path.GetFileName(entry);
                var toggled = ToggleCase(name);

                if (toggled == name) {
                    continue;
                }

                var candidate = Path.Combine(directory, toggled);
                return File.Exists(candidate) || Directory.Exists(candidate);
            }

            return true;
        } catch (IOException) {
            return true;
        } catch (UnauthorizedAccessException) {
            return true;
        }
    }

    static string ToggleCase(string name) {
        var characters = name.ToCharArray();

        for (var index = 0; index < characters.Length; index++) {
            characters[index] = char.IsUpper(characters[index])
                ? char.ToLowerInvariant(characters[index])
                : char.ToUpperInvariant(characters[index]);
        }

        return new(characters);
    }

    static bool NameExistsExactly(string parentOsPath, ReadOnlySpan<char> name) {
        var target = name.ToString();

        try {
            foreach (var found in Directory.EnumerateFileSystemEntries(parentOsPath, target)) {
                if (Path.GetFileName(found.AsSpan()).SequenceEqual(target)) {
                    return true;
                }
            }
        } catch (DirectoryNotFoundException) {
            return false;
        }

        return false;
    }

    string ToOsPath(VirtualPath path) {
        if (path.IsEmpty) {
            throw new ArgumentException("The path is the default value, which is not a path.", nameof(path));
        }

        if (path.IsRoot) {
            return root;
        }

        var relative = path.Value[1..];

        if (Path.DirectorySeparatorChar != VirtualPath.Separator) {
            relative = relative.Replace(VirtualPath.Separator, Path.DirectorySeparatorChar);
        }

        return Path.Combine(root, relative);
    }

    bool TryToVirtual(string osPath, out VirtualPath path) {
        var relative = Path.GetRelativePath(root, osPath);

        if (Path.DirectorySeparatorChar != VirtualPath.Separator) {
            relative = relative.Replace(Path.DirectorySeparatorChar, VirtualPath.Separator);
        }

        return VirtualPath.TryCreate("/" + relative, out path);
    }

    string OpenableFile(VirtualPath path) {
        var os = ToOsPath(path);

        if (!File.Exists(os) || !MatchesOnDisk(path)) {
            throw new FileNotFoundException($"There is no file at '{path.Value}'.", path.Value);
        }

        return os;
    }

    string PrepareForWrite(VirtualPath path) {
        ThrowIfReadOnly();
        var os = ToOsPath(path);
        var parent = Path.GetDirectoryName(os);

        // Parents are created, matching MemoryFileProvider. Two providers that disagree about
        // whether writing into a new folder works is a difference that only shows up in whichever
        // one has fewer tests.
        if (!string.IsNullOrEmpty(parent)) {
            Directory.CreateDirectory(parent);
        }

        return os;
    }

    bool MatchesOnDisk(VirtualPath path) {
        if (!EnforcesCaseSensitivity || path.IsRoot) {
            return true;
        }

        return VerifyDirectory(path.Parent) && NameExistsExactly(ToOsPath(path.Parent), path.FileName);
    }

    bool VerifyDirectory(VirtualPath directory) {
        if (directory.IsRoot) {
            return true;
        }

        var os = ToOsPath(directory);

        lock (cacheGate) {
            if (verifiedDirectories.Contains(os)) {
                return true;
            }
        }

        if (!VerifyDirectory(directory.Parent) || !NameExistsExactly(ToOsPath(directory.Parent), directory.FileName)) {
            return false;
        }

        lock (cacheGate) {
            verifiedDirectories.Add(os);
        }

        return true;
    }

    void Forget(string osDirectory) {
        lock (cacheGate) {
            verifiedDirectories.Remove(osDirectory);
        }
    }

    void ThrowIfReadOnly() {
        if (IsReadOnly) {
            throw new NotSupportedException($"The provider for '{root}' is read-only.");
        }
    }
}
