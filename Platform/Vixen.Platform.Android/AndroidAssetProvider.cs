// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Android.Content.Res;
using Vixen.Core.IO;

namespace Vixen.Platform.Android;

/// <summary>The APK's own assets, as a read-only file provider.</summary>
/// <remarks>
///     <para>
///         <b>This is the case doc 10 says the virtual file system exists for.</b> An asset inside an
///         APK is not a file: it is a range of a zip that may or may not be stored uncompressed,
///         there is no path on disk to hand anybody, and <c>File.OpenRead</c> cannot reach it. Every
///         other platform's content mount is a directory; this one is an
///         <see cref="AssetManager" />, and the only reason the rest of the engine does not care is
///         that it asks <see cref="IFileProvider" /> rather than the file system.
///     </para>
///     <para>
///         <b>Compressed assets are not seekable, and pretending otherwise is the bug.</b>
///         <c>AssetManager.Open</c> returns a stream over an inflater for anything the packager
///         compressed, and seeking it throws. So a read is buffered into memory when the underlying
///         stream will not seek, and left alone when it will — which is what
///         <c>AndroidUseAssetPackCompression=false</c> or an already-compressed bundle produces, and
///         is the case that matters because a content bundle is the large one.
///     </para>
///     <para>
///         <b>Existence is answered by opening.</b> <see cref="AssetManager" /> has no <c>Exists</c>,
///         and <c>List</c> on a directory is O(entries) and does not recurse. Opening and closing is
///         the honest test, and it is what any caller was about to do anyway.
///     </para>
/// </remarks>
/// <param name="assets">The activity's asset manager.</param>
internal sealed class AndroidAssetProvider(AssetManager assets) : IFileProvider {
    /// <inheritdoc />
    public bool IsReadOnly => true;

    /// <inheritdoc />
    public bool Exists(VirtualPath path) {
        try {
            using var stream = assets.Open(Native(path));
            return true;
        } catch (Java.IO.FileNotFoundException) {
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The length is the decompressed one and costs a read to find, which is why it is only
    ///     produced when asked for. The write time is the APK's, and is the same for every asset in
    ///     it — an APK is built once, so that is not an approximation but the actual answer.
    /// </remarks>
    public bool TryGetEntry(VirtualPath path, out FileEntry entry) {
        try {
            using var stream = assets.Open(Native(path));
            entry = new(path, stream.CanSeek ? stream.Length : Drain(stream), default, IsDirectory: false);
            return true;
        } catch (Java.IO.FileNotFoundException) {
            entry = default;
            return false;
        }
    }

    /// <inheritdoc />
    public IEnumerable<FileEntry> Enumerate(VirtualPath directory, bool recursive = false) {
        var names = assets.List(Native(directory)) ?? [];

        foreach (var name in names) {
            var child = directory.Combine(name);

            if (TryGetEntry(child, out var entry)) {
                yield return entry;
                continue;
            }

            // Not openable as a file, so it is a directory: AssetManager draws no other distinction.
            yield return new(child, 0, default, IsDirectory: true);

            if (recursive) {
                foreach (var descendant in Enumerate(child, recursive: true)) {
                    yield return descendant;
                }
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<Stream> OpenReadAsync(VirtualPath path, CancellationToken cancellationToken = default) {
        var stream = assets.Open(Native(path));

        // Seekable already means the packager stored it uncompressed, which is what a content bundle
        // should be — so the large files, the ones worth not copying, are the ones that avoid it.
        return ValueTask.FromResult(stream.CanSeek ? stream : Buffer(stream));
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always. An APK is read-only at run time.</exception>
    public ValueTask<Stream> OpenWriteAsync(VirtualPath path, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            $"'{path}' is inside the APK, which cannot be written to. Write to the data or cache mount."
        );

    /// <inheritdoc />
    /// <returns>Always <see langword="false" />.</returns>
    public bool Delete(VirtualPath path) => false;

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always.</exception>
    public void CreateDirectory(VirtualPath path) =>
        throw new NotSupportedException("The APK is read-only.");

    /// <inheritdoc />
    /// <remarks>
    ///     Never. An asset can be memory-mapped when it is stored uncompressed and aligned, through
    ///     <c>AssetManager.openFd</c> and the descriptor's offset and length — which is worth doing
    ///     and is not done, because it only pays off once the content build is emitting
    ///     uncompressed, aligned bundles and that is a packaging decision that has not been made.
    /// </remarks>
    public bool TryMap(VirtualPath path, [NotNullWhen(true)] out IMappedFile? mapped) {
        mapped = null;
        return false;
    }

    /// <summary>The path as the asset manager wants it: relative, with no leading separator.</summary>
    static string Native(VirtualPath path) => path.ToString().TrimStart('/');

    static MemoryStream Buffer(Stream stream) {
        using (stream) {
            var memory = new MemoryStream();
            stream.CopyTo(memory);
            memory.Position = 0;
            return memory;
        }
    }

    static long Drain(Stream stream) {
        long total = 0;
        var buffer = new byte[64 * 1024];

        int read;

        while ((read = stream.Read(buffer)) > 0) {
            total += read;
        }

        return total;
    }
}
