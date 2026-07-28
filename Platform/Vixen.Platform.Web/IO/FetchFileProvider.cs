// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using Vixen.Core.IO;

namespace Vixen.Platform.Web;

/// <summary>The application's shipped content, over HTTP.</summary>
/// <remarks>
///     <para>
///         <b>This is the case doc 10 says the virtual file system exists for, in its strongest
///         form.</b> Content on the web is not a directory, is not local, and is not even
///         necessarily whole — a 300 MB bundle should be streamed in pieces rather than downloaded
///         before the first frame. Every other platform's <c>/app</c> is a directory or an archive;
///         this one is a base URL and a manifest, and the only reason the rest of the engine does
///         not care is that it asks <see cref="IFileProvider" /> rather than the file system.
///     </para>
///     <para>
///         <b>Range requests are the point.</b> <see cref="OpenReadAsync" /> hands back a seekable
///         stream that fetches <c>Range: bytes=…</c> as it is read, so the serialiser reading a
///         header out of a bundle costs one request for a few kilobytes rather than the whole
///         bundle. Small files are fetched whole instead, because a request per chunk is worse than
///         a request for the lot below the size where streaming pays.
///     </para>
///     <para>
///         <b>The synchronous half is answered from the manifest, and the synchronous
///         <c>OpenRead</c> is refused.</b> <see cref="IFileProvider" /> implements it by blocking on
///         the asynchronous one, which on the browser's single thread does not wait — it deadlocks
///         the tab, because the fetch it is waiting for can only complete on the thread it has
///         stopped. Throwing with a message that names <see cref="OpenReadAsync" /> is the one
///         behaviour that leads a caller to the fix; the engine's own runtime paths are already
///         asynchronous, and it is editor-shaped code that trips this.
///     </para>
/// </remarks>
[SupportedOSPlatform("browser")]
public sealed class FetchFileProvider : IFileProvider {
    /// <summary>Below this, a file is fetched whole rather than streamed.</summary>
    /// <remarks>
    ///     A request has a fixed cost — DNS is cached, the connection is warm, but the round trip is
    ///     not free — and 256 KB over one connection arrives in about the time a single extra
    ///     round trip takes. Above it, ranges win; below it, they lose to their own overhead.
    /// </remarks>
    public const int WholeFileThreshold = 256 * 1024;

    readonly string baseUrl;
    readonly WebContentManifest manifest;

    /// <summary>Creates the provider.</summary>
    /// <param name="baseUrl">Where content is fetched from, relative to the page. A trailing
    /// <c>/</c> is added if it is missing.</param>
    /// <param name="manifest">What is there. See <see cref="WebContentManifest" /> for why this is
    /// required rather than discovered.</param>
    public FetchFileProvider(string baseUrl, WebContentManifest manifest) {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(manifest);

        this.baseUrl = baseUrl.Length == 0 || baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        this.manifest = manifest;
    }

    /// <inheritdoc />
    public bool IsReadOnly => true;

    /// <summary>What the manifest says is here.</summary>
    public WebContentManifest Manifest => manifest;

    /// <inheritdoc />
    public bool Exists(VirtualPath path) =>
        manifest.TryGet(path.Value, out _) || manifest.HasDirectory(path.Value);

    /// <inheritdoc />
    public bool TryGetEntry(VirtualPath path, out FileEntry entry) {
        if (manifest.TryGet(path.Value, out var found)) {
            entry = new(path, found.Length, WebContentManifest.Moment(found.Modified), IsDirectory: false);
            return true;
        }

        if (manifest.HasDirectory(path.Value)) {
            entry = new(path, 0, default, IsDirectory: true);
            return true;
        }

        entry = default;
        return false;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The manifest's own, because a web server has no directories and what is under one is
    ///     entirely a question about the list of paths. See
    ///     <see cref="WebContentManifest.Enumerate" />.
    /// </remarks>
    public IEnumerable<FileEntry> Enumerate(VirtualPath directory, bool recursive = false) =>
        manifest.Enumerate(directory, recursive);

    /// <inheritdoc />
    /// <returns>
    ///     A seekable stream. Files under <see cref="WholeFileThreshold" /> are already in memory
    ///     when it is handed back; larger ones fetch ranges as they are read.
    /// </returns>
    /// <exception cref="FileNotFoundException">The manifest does not name it.</exception>
    public async ValueTask<Stream> OpenReadAsync(VirtualPath path, CancellationToken cancellationToken = default) {
        if (!manifest.TryGet(path.Value, out var entry)) {
            throw new FileNotFoundException(
                $"'{path}' is not in the content manifest. Everything under the content mount has "
                + "to be listed there, because HTTP has no directory listing and the synchronous "
                + "half of IFileProvider cannot go and look.",
                path.Value
            );
        }

        var url = UrlFor(entry);

        if (entry.Length <= WholeFileThreshold) {
            var handle = await WebInterop.FetchAll(url).WaitAsync(cancellationToken).ConfigureAwait(false);
            return new MemoryStream(WebBuffer.Take(handle), writable: false);
        }

        return new FetchStream(url, entry.Length);
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always. Content served over HTTP is read-only.</exception>
    public ValueTask<Stream> OpenWriteAsync(VirtualPath path, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            $"'{path}' is served over HTTP, which cannot be written to. Write to the data or cache mount."
        );

    /// <inheritdoc />
    /// <returns>Always <see langword="false" />.</returns>
    public bool Delete(VirtualPath path) => false;

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always.</exception>
    public void CreateDirectory(VirtualPath path) =>
        throw new NotSupportedException("Content served over HTTP is read-only.");

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">
    ///     Always. See the type's remarks: blocking on the browser's one thread does not wait.
    /// </exception>
    public Stream OpenRead(VirtualPath path) =>
        throw new NotSupportedException(
            $"'{path}' is behind an HTTP fetch and this is a browser, where blocking the calling "
            + "thread stops the fetch it is waiting for from ever completing. Use OpenReadAsync."
        );

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always.</exception>
    public Stream OpenWrite(VirtualPath path) =>
        throw new NotSupportedException("Content served over HTTP is read-only.");

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always.</exception>
    public Stream OpenAppend(VirtualPath path) =>
        throw new NotSupportedException("Content served over HTTP is read-only.");

    /// <inheritdoc />
    /// <remarks>
    ///     Never. There is no address space to map a remote resource into, and a browser offers no
    ///     equivalent — callers fall back to <see cref="OpenReadAsync" />, which the interface
    ///     documents as the ordinary outcome rather than a failure.
    /// </remarks>
    public bool TryMap(VirtualPath path, [NotNullWhen(true)] out IMappedFile? mapped) {
        mapped = null;
        return false;
    }

    /// <summary>Where a file is fetched from.</summary>
    internal string UrlFor(in WebContentEntry entry) =>
        baseUrl + (entry.Url ?? entry.Path.TrimStart('/'));

}
