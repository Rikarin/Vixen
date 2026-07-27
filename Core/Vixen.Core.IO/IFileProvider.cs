// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Core.IO;

/// <summary>One source of files, mounted somewhere in the virtual file system.</summary>
/// <remarks>
///     <para>
///         Paths passed to a provider are relative to its mount and still rooted: a provider mounted
///         at <c>/app</c> sees <c>/textures/a.png</c> when the caller asked for
///         <c>/app/textures/a.png</c>. A provider therefore does not know or care where it is
///         mounted, which is what lets the same physical provider serve <c>/project</c> in the editor
///         and <c>/app</c> in a build.
///     </para>
///     <para>
///         <b>Async-first, with sync as a documented compromise.</b> Opening a stream is the
///         operation that is genuinely remote on some platforms — a browser fetch, a bundle that has
///         to be downloaded — so it is asynchronous and everything in the runtime uses that form. The
///         synchronous overloads exist because editor and tooling code is full of straight-line file
///         work that would be worse as a state machine, and they are implemented here by blocking on
///         the asynchronous ones. A provider that can do better should override them;
///         <see cref="PhysicalFileProvider" /> does.
///     </para>
///     <para>
///         <b>Enumeration is synchronous only.</b> It is a metadata query, and every provider that
///         exists or is planned answers it from something local: a directory, a dictionary, or a
///         bundle catalog that was loaded when the bundle was. Adding an asynchronous form now would
///         be adding a state machine to satisfy no caller.
///     </para>
/// </remarks>
public interface IFileProvider {
    /// <summary>Whether this provider refuses every write.</summary>
    bool IsReadOnly { get; }

    /// <summary>Whether a file or directory exists.</summary>
    /// <param name="path">The path, relative to the mount.</param>
    /// <returns><see langword="true" /> if something is there.</returns>
    bool Exists(VirtualPath path);

    /// <summary>Reads what is known about a file or directory without opening it.</summary>
    /// <param name="path">The path, relative to the mount.</param>
    /// <param name="entry">What was found.</param>
    /// <returns><see langword="false" /> if nothing is there.</returns>
    bool TryGetEntry(VirtualPath path, out FileEntry entry);

    /// <summary>Lists the contents of a directory.</summary>
    /// <param name="directory">The directory, relative to the mount.</param>
    /// <param name="recursive">Whether to descend into subdirectories.</param>
    /// <returns>The entries, ordered by path. Empty if the directory does not exist.</returns>
    /// <remarks>
    ///     The order is part of the contract. A content build that hashes a directory listing to
    ///     decide whether anything changed must not get a different answer on ext4 than on APFS, and
    ///     the alternative — every caller remembering to sort — is the kind of rule that holds until
    ///     it does not.
    /// </remarks>
    IEnumerable<FileEntry> Enumerate(VirtualPath directory, bool recursive = false);

    /// <summary>Opens a file for reading.</summary>
    /// <param name="path">The path, relative to the mount.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>A readable stream the caller owns.</returns>
    /// <exception cref="FileNotFoundException">There is no such file.</exception>
    ValueTask<Stream> OpenReadAsync(VirtualPath path, CancellationToken cancellationToken = default);

    /// <summary>Opens a file for writing, creating or truncating it.</summary>
    /// <param name="path">The path, relative to the mount.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>A writable stream the caller owns.</returns>
    /// <exception cref="NotSupportedException">The provider is read-only.</exception>
    ValueTask<Stream> OpenWriteAsync(VirtualPath path, CancellationToken cancellationToken = default);

    /// <summary>Opens a file for writing at its end, creating it if it is not there.</summary>
    /// <param name="path">The path, relative to the mount.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>A writable stream positioned at the end of what is already there.</returns>
    /// <exception cref="NotSupportedException">The provider is read-only.</exception>
    /// <remarks>
    ///     <para>
    ///         What resuming an interrupted download is made of: a bundle cache writes what it has
    ///         received to a partial file, and picking that up after a crash or a lost connection means
    ///         adding to it rather than replacing it.
    ///     </para>
    ///     <para>
    ///         The default reads the existing contents back and rewrites them, which is correct
    ///         everywhere and wasteful. A provider whose underlying store can genuinely append should
    ///         override it — <see cref="PhysicalFileProvider" /> and <see cref="MemoryFileProvider" />
    ///         both do — because the whole point of resuming a 400 MB download is not touching the
    ///         first 300 MB again.
    ///     </para>
    /// </remarks>
    async ValueTask<Stream> OpenAppendAsync(VirtualPath path, CancellationToken cancellationToken = default) {
        byte[] existing = [];

        if (Exists(path)) {
            using var reading = await OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await reading.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            existing = buffer.ToArray();
        }

        var writing = await OpenWriteAsync(path, cancellationToken).ConfigureAwait(false);
        await writing.WriteAsync(existing, cancellationToken).ConfigureAwait(false);

        return writing;
    }

    /// <summary>Deletes a file, or an empty directory.</summary>
    /// <param name="path">The path, relative to the mount.</param>
    /// <returns><see langword="false" /> if there was nothing to delete.</returns>
    /// <exception cref="NotSupportedException">The provider is read-only.</exception>
    bool Delete(VirtualPath path);

    /// <summary>Creates a directory and any missing parents.</summary>
    /// <param name="path">The path, relative to the mount.</param>
    /// <exception cref="NotSupportedException">The provider is read-only.</exception>
    void CreateDirectory(VirtualPath path);

    /// <summary>Opens a file for reading.</summary>
    /// <param name="path">The path, relative to the mount.</param>
    /// <returns>A readable stream the caller owns.</returns>
    /// <exception cref="FileNotFoundException">There is no such file.</exception>
    Stream OpenRead(VirtualPath path) => OpenReadAsync(path).AsTask().GetAwaiter().GetResult();

    /// <summary>Opens a file for writing, creating or truncating it.</summary>
    /// <param name="path">The path, relative to the mount.</param>
    /// <returns>A writable stream the caller owns.</returns>
    /// <exception cref="NotSupportedException">The provider is read-only.</exception>
    Stream OpenWrite(VirtualPath path) => OpenWriteAsync(path).AsTask().GetAwaiter().GetResult();

    /// <summary>Opens a file for writing at its end, creating it if it is not there.</summary>
    /// <param name="path">The path, relative to the mount.</param>
    /// <returns>A writable stream positioned at the end of what is already there.</returns>
    /// <exception cref="NotSupportedException">The provider is read-only.</exception>
    Stream OpenAppend(VirtualPath path) => OpenAppendAsync(path).AsTask().GetAwaiter().GetResult();

    /// <summary>Maps a file into memory, if this provider can.</summary>
    /// <param name="path">The path, relative to the mount.</param>
    /// <param name="mapped">The mapping, which the caller disposes.</param>
    /// <returns><see langword="false" /> if this provider cannot map, or the file does not exist.</returns>
    /// <remarks>
    ///     <para>
    ///         Returning <see langword="false" /> is an ordinary answer, not a failure: a file inside
    ///         a compressed APK entry or behind an HTTP fetch cannot be mapped, and callers are
    ///         expected to fall back to <see cref="OpenReadAsync" />. What mapping buys is the
    ///         serializer reading a bundle without copying it, which is worth having where it is
    ///         possible and not worth pretending where it is not.
    ///     </para>
    ///     <para>
    ///         The returned memory stays valid until the mapping is disposed, and not one instruction
    ///         longer.
    ///     </para>
    /// </remarks>
    bool TryMap(VirtualPath path, [NotNullWhen(true)] out IMappedFile? mapped) {
        mapped = null;
        return false;
    }
}

/// <summary>A file's contents, in memory, without having been copied there.</summary>
public interface IMappedFile : IDisposable {
    /// <summary>The contents. Valid until this is disposed.</summary>
    ReadOnlyMemory<byte> Memory { get; }
}
