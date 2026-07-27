// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Vixen.Core.IO;

/// <summary>The mount table, and the only file API engine code is allowed to know about.</summary>
/// <remarks>
///     <para>
///         Mounts are matched longest-prefix-first, so a provider at <c>/app/dlc</c> takes precedence
///         over one at <c>/app</c> for anything underneath it. Matching is segment-aware, so a mount
///         at <c>/app</c> never captures <c>/application</c>.
///     </para>
///     <para>
///         Reads are lock-free. The mount table is replaced wholesale on every change and read
///         through a single volatile field, which is the right trade for a structure written a
///         handful of times at start-up and read on every file access — the same shape
///         <c>ServiceRegistry</c> uses, for the same reason.
///     </para>
/// </remarks>
public sealed class VirtualFileSystem {
    readonly Lock gate = new();

    // Sorted longest mount first, so the first segment-aware match is also the most specific one.
    volatile MountEntry[] mounts = [];

    /// <summary>The mounts, most specific first.</summary>
    public IReadOnlyList<VirtualPath> Mounts {
        get {
            var snapshot = mounts;
            var result = new VirtualPath[snapshot.Length];

            for (var index = 0; index < snapshot.Length; index++) {
                result[index] = snapshot[index].Path;
            }

            return result;
        }
    }

    /// <summary>Attaches a provider at a mount point, replacing whatever was there.</summary>
    /// <param name="path">Where to mount it.</param>
    /// <param name="provider">The provider.</param>
    /// <exception cref="ArgumentException"><paramref name="path" /> is the default value.</exception>
    public void Mount(VirtualPath path, IFileProvider provider) {
        ArgumentNullException.ThrowIfNull(provider);

        if (path.IsEmpty) {
            throw new ArgumentException("A mount needs a path.", nameof(path));
        }

        lock (gate) {
            var replaced = new List<MountEntry>(mounts.Length + 1);

            foreach (var existing in mounts) {
                if (existing.Path != path) {
                    replaced.Add(existing);
                }
            }

            replaced.Add(new(path, provider));

            // Longest first. Ties cannot happen: two mounts with the same text are the same mount,
            // and the loop above dropped the old one.
            replaced.Sort(static (left, right) => right.Path.Value.Length.CompareTo(left.Path.Value.Length));
            mounts = [.. replaced];
        }
    }

    /// <summary>Detaches whatever is mounted at a path.</summary>
    /// <param name="path">The mount point.</param>
    /// <returns><see langword="false" /> if nothing was mounted there.</returns>
    public bool Unmount(VirtualPath path) {
        lock (gate) {
            var remaining = new List<MountEntry>(mounts.Length);
            var removed = false;

            foreach (var existing in mounts) {
                if (existing.Path == path) {
                    removed = true;
                } else {
                    remaining.Add(existing);
                }
            }

            if (removed) {
                mounts = [.. remaining];
            }

            return removed;
        }
    }

    /// <summary>Finds the provider responsible for a path.</summary>
    /// <param name="path">The virtual path.</param>
    /// <param name="provider">The provider that owns it.</param>
    /// <param name="providerPath">The path as that provider sees it — rooted, with the mount removed.</param>
    /// <returns><see langword="false" /> if no mount covers the path.</returns>
    public bool TryResolve(VirtualPath path, [NotNullWhen(true)] out IFileProvider? provider, out VirtualPath providerPath) {
        foreach (var mount in mounts) {
            if (mount.Path.Contains(path)) {
                provider = mount.Provider;
                providerPath = path.RelativeTo(mount.Path);
                return true;
            }
        }

        provider = null;
        providerPath = default;
        return false;
    }

    /// <summary>Whether a file or directory exists.</summary>
    /// <param name="path">The virtual path.</param>
    /// <returns><see langword="true" /> if something is there. An unmounted path is not an error; it just is not there.</returns>
    public bool Exists(VirtualPath path) =>
        TryResolve(path, out var provider, out var providerPath) && provider.Exists(providerPath);

    /// <summary>Reads what is known about a file or directory without opening it.</summary>
    /// <param name="path">The virtual path.</param>
    /// <param name="entry">What was found, with its full virtual path.</param>
    /// <returns><see langword="false" /> if nothing is there.</returns>
    public bool TryGetEntry(VirtualPath path, out FileEntry entry) {
        if (TryResolve(path, out var provider, out var providerPath) && provider.TryGetEntry(providerPath, out var found)) {
            entry = found with { Path = path };
            return true;
        }

        entry = default;
        return false;
    }

    /// <summary>Opens a file for reading.</summary>
    /// <param name="path">The virtual path.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>A readable stream the caller owns.</returns>
    /// <exception cref="DirectoryNotFoundException">No mount covers the path.</exception>
    /// <exception cref="FileNotFoundException">There is no such file.</exception>
    public ValueTask<Stream> OpenReadAsync(VirtualPath path, CancellationToken cancellationToken = default) {
        var (provider, providerPath) = Resolve(path);
        return provider.OpenReadAsync(providerPath, cancellationToken);
    }

    /// <summary>Opens a file for writing, creating or truncating it.</summary>
    /// <param name="path">The virtual path.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>A writable stream the caller owns.</returns>
    /// <exception cref="DirectoryNotFoundException">No mount covers the path.</exception>
    /// <exception cref="NotSupportedException">The mount is read-only.</exception>
    public ValueTask<Stream> OpenWriteAsync(VirtualPath path, CancellationToken cancellationToken = default) {
        var (provider, providerPath) = Resolve(path);
        return provider.OpenWriteAsync(providerPath, cancellationToken);
    }

    /// <summary>Opens a file for writing at its end, creating it if it is not there.</summary>
    /// <param name="path">The virtual path.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>A writable stream positioned at the end of what is already there.</returns>
    /// <exception cref="DirectoryNotFoundException">No mount covers the path.</exception>
    /// <exception cref="NotSupportedException">The mount is read-only.</exception>
    public ValueTask<Stream> OpenAppendAsync(VirtualPath path, CancellationToken cancellationToken = default) {
        var (provider, providerPath) = Resolve(path);
        return provider.OpenAppendAsync(providerPath, cancellationToken);
    }

    /// <summary>Opens a file for reading. Blocking; for editor and tooling code.</summary>
    /// <param name="path">The virtual path.</param>
    /// <returns>A readable stream the caller owns.</returns>
    public Stream OpenRead(VirtualPath path) {
        var (provider, providerPath) = Resolve(path);
        return provider.OpenRead(providerPath);
    }

    /// <summary>Opens a file for writing. Blocking; for editor and tooling code.</summary>
    /// <param name="path">The virtual path.</param>
    /// <returns>A writable stream the caller owns.</returns>
    public Stream OpenWrite(VirtualPath path) {
        var (provider, providerPath) = Resolve(path);
        return provider.OpenWrite(providerPath);
    }

    /// <summary>Opens a file for writing at its end. Blocking; for editor and tooling code.</summary>
    /// <param name="path">The virtual path.</param>
    /// <returns>A writable stream positioned at the end of what is already there.</returns>
    public Stream OpenAppend(VirtualPath path) {
        var (provider, providerPath) = Resolve(path);
        return provider.OpenAppend(providerPath);
    }

    /// <summary>Deletes a file, or an empty directory.</summary>
    /// <param name="path">The virtual path.</param>
    /// <returns><see langword="false" /> if there was nothing to delete.</returns>
    public bool Delete(VirtualPath path) {
        var (provider, providerPath) = Resolve(path);
        return provider.Delete(providerPath);
    }

    /// <summary>Creates a directory and any missing parents.</summary>
    /// <param name="path">The virtual path.</param>
    public void CreateDirectory(VirtualPath path) {
        var (provider, providerPath) = Resolve(path);
        provider.CreateDirectory(providerPath);
    }

    /// <summary>Maps a file into memory, if the mount can.</summary>
    /// <param name="path">The virtual path.</param>
    /// <param name="mapped">The mapping, which the caller disposes.</param>
    /// <returns><see langword="false" /> if the mount cannot map, or the file does not exist.</returns>
    public bool TryMap(VirtualPath path, [NotNullWhen(true)] out IMappedFile? mapped) {
        if (TryResolve(path, out var provider, out var providerPath)) {
            return provider.TryMap(providerPath, out mapped);
        }

        mapped = null;
        return false;
    }

    /// <summary>Lists the contents of a directory, including mounts that live under it.</summary>
    /// <param name="directory">The virtual path of the directory.</param>
    /// <param name="recursive">Whether to descend into subdirectories.</param>
    /// <returns>The entries, with full virtual paths.</returns>
    /// <remarks>
    ///     Enumerating <c>/</c> lists the mounts, even though no provider owns the root. Without
    ///     that, the one path every tool starts from would be the one path that looks empty.
    /// </remarks>
    public IEnumerable<FileEntry> Enumerate(VirtualPath directory, bool recursive = false) {
        if (directory.IsEmpty) {
            yield break;
        }

        var seen = new HashSet<VirtualPath>();

        if (TryResolve(directory, out var provider, out var providerPath)) {
            var mountPath = MountOf(directory);

            foreach (var entry in provider.Enumerate(providerPath, recursive)) {
                var full = Rebase(mountPath, entry.Path);

                if (seen.Add(full)) {
                    yield return entry with { Path = full };
                }
            }
        }

        // Mounts nested under this directory are directories from the caller's point of view, and
        // the provider that owns the parent has no idea they exist.
        foreach (var mount in mounts) {
            if (mount.Path == directory || !directory.Contains(mount.Path)) {
                continue;
            }

            var child = ChildOf(directory, mount.Path);

            if (seen.Add(child)) {
                yield return new(child, 0, DateTimeOffset.MinValue, true);
            }

            if (!recursive) {
                continue;
            }

            foreach (var entry in Enumerate(mount.Path, recursive: true)) {
                if (seen.Add(entry.Path)) {
                    yield return entry;
                }
            }
        }
    }

    /// <summary>Reads a whole file.</summary>
    /// <param name="path">The virtual path.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The contents.</returns>
    public async ValueTask<byte[]> ReadAllBytesAsync(VirtualPath path, CancellationToken cancellationToken = default) {
        // Mapped when the provider can: for a bundle on disk this is the difference between one copy
        // and none, on a path the asset loader takes for every asset.
        if (TryMap(path, out var mapped)) {
            using (mapped) {
                return mapped.Memory.ToArray();
            }
        }

        await using var stream = await OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    /// <summary>Reads a whole file as UTF-8 text.</summary>
    /// <param name="path">The virtual path.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The contents.</returns>
    public async ValueTask<string> ReadAllTextAsync(VirtualPath path, CancellationToken cancellationToken = default) {
        await using var stream = await OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes a whole file, creating or replacing it.</summary>
    /// <param name="path">The virtual path.</param>
    /// <param name="contents">What to write.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the file is written.</returns>
    public async ValueTask WriteAllBytesAsync(
        VirtualPath path,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken = default
    ) {
        await using var stream = await OpenWriteAsync(path, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes a whole file as UTF-8 text, without a byte-order mark.</summary>
    /// <param name="path">The virtual path.</param>
    /// <param name="contents">What to write.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the file is written.</returns>
    public ValueTask WriteAllTextAsync(VirtualPath path, string contents, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(contents);

        // No BOM. Content is compared byte-for-byte by the build's determinism gate, and a BOM is
        // three bytes of platform-dependent noise in front of every file.
        return WriteAllBytesAsync(path, new UTF8Encoding(false).GetBytes(contents), cancellationToken);
    }

    static VirtualPath Rebase(VirtualPath mount, VirtualPath providerPath) =>
        providerPath.IsRoot ? mount : new(mount.IsRoot ? providerPath.Value : mount.Value + providerPath.Value);

    static VirtualPath ChildOf(VirtualPath directory, VirtualPath descendant) {
        // The first segment of the descendant below the directory: /a from /a/b/c under /.
        var remainder = descendant.RelativeTo(directory).Value;
        var next = remainder.IndexOf(VirtualPath.Separator, 1);
        var segment = next < 0 ? remainder : remainder[..next];
        return directory.IsRoot ? new(segment) : new(directory.Value + segment);
    }

    VirtualPath MountOf(VirtualPath path) {
        foreach (var mount in mounts) {
            if (mount.Path.Contains(path)) {
                return mount.Path;
            }
        }

        return default;
    }

    (IFileProvider Provider, VirtualPath Path) Resolve(VirtualPath path) {
        if (TryResolve(path, out var provider, out var providerPath)) {
            return (provider, providerPath);
        }

        var known = mounts.Length == 0 ? "nothing is mounted" : string.Join(", ", Mounts);

        throw new DirectoryNotFoundException(
            $"No mount covers '{path.Value}'. Mounted: {known}."
        );
    }

    readonly record struct MountEntry(VirtualPath Path, IFileProvider Provider);
}
