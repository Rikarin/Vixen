// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.Versioning;
using Vixen.Core.IO;

namespace Vixen.Platform.Web;

/// <summary>Where a browser tab keeps things, and what it will let us do there.</summary>
/// <remarks>
///     <para>
///         Four mounts, three storage mechanisms, and no directories anywhere:
///     </para>
///     <list type="table">
///         <item>
///             <term><c>/app</c></term>
///             <description>
///                 <see cref="FetchFileProvider" /> — HTTP, read-only, with range requests so a
///                 bundle streams rather than downloads.
///             </description>
///         </item>
///         <item>
///             <term><c>/data</c></term>
///             <description>
///                 <see cref="IndexedDbFileProvider" /> — saves and settings, evicted last of
///                 everything a browser evicts.
///             </description>
///         </item>
///         <item>
///             <term><c>/cache</c></term>
///             <description>
///                 A second <see cref="IndexedDbFileProvider" />, in its own database so that
///                 clearing the cache does not go near the saves.
///             </description>
///         </item>
///         <item>
///             <term><c>/temp</c></term>
///             <description>
///                 <see cref="MemoryFileProvider" /> — which is exactly right here. Temporary means
///                 "need not survive the session", a page's session ends when the tab closes, and
///                 writing scratch data to storage a browser then has to evict is work for nobody.
///             </description>
///         </item>
///     </list>
///     <para>
///         <b>Everything is asynchronous, and that is why this has a factory rather than a
///         constructor.</b> <see cref="IFileProvider" />'s metadata half is synchronous, on purpose:
///         every other provider answers it from a directory, a dictionary or a bundle catalog. A
///         browser has none of those, and cannot block to go and look — the WebAssembly runtime
///         shares its thread with the event loop, so waiting for a fetch prevents the fetch. So the
///         content manifest is read and the stores are opened <em>before</em> the platform exists,
///         and from then on every synchronous query is answered from memory.
///     </para>
///     <para>
///         <b><see cref="ApplicationDirectory" /> and friends are empty.</b> There are no native
///         paths in a browser at all, which is the case
///         <see cref="IFileSystemHost.ApplicationDirectory" /> already documents for content that is
///         not a directory. Anything that reads them as paths would be reading a lie; the mounts are
///         the interface.
///     </para>
/// </remarks>
[SupportedOSPlatform("browser")]
public sealed class WebFileSystemHost : IFileSystemHost, IDisposable {
    readonly FetchFileProvider? content;
    readonly IndexedDbFileProvider data;
    readonly IndexedDbFileProvider cache;
    readonly MemoryFileProvider temporary = new();

    bool disposed;

    WebFileSystemHost(FetchFileProvider? content, IndexedDbFileProvider data, IndexedDbFileProvider cache) {
        this.content = content;
        this.data = data;
        this.cache = cache;
    }

    /// <summary>Opens the stores and reads the content manifest.</summary>
    /// <param name="options">Where things are.</param>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <returns>The host, ready for its synchronous members to be called.</returns>
    /// <exception cref="IOException">
    ///     The manifest could not be fetched, or IndexedDB could not be opened.
    /// </exception>
    public static async Task<WebFileSystemHost> CreateAsync(
        WebPlatformOptions options = default,
        CancellationToken cancellationToken = default
    ) {
        var data = await IndexedDbFileProvider
            .OpenAsync(options.DataStoreName ?? "vixen-data", cancellationToken)
            .ConfigureAwait(false);

        var cache = await IndexedDbFileProvider
            .OpenAsync(options.CacheStoreName ?? "vixen-cache", cancellationToken)
            .ConfigureAwait(false);

        FetchFileProvider? content = null;

        if (options.MountContent) {
            var baseUrl = options.ContentBaseUrl ?? "content/";
            var manifest = await ReadManifestAsync(
                baseUrl,
                options.ContentManifest ?? "manifest.json",
                cancellationToken
            ).ConfigureAwait(false);

            content = new(baseUrl, manifest);
        }

        if (options.RequestPersistentStorage) {
            await RequestPersistentStorageAsync(cancellationToken).ConfigureAwait(false);
        }

        return new(content, data, cache);
    }

    /// <inheritdoc />
    /// <remarks>Empty. A browser has no native paths; see the type's remarks.</remarks>
    public string ApplicationDirectory => string.Empty;

    /// <inheritdoc />
    /// <remarks>Empty. See <see cref="ApplicationDirectory" />.</remarks>
    public string DataDirectory => string.Empty;

    /// <inheritdoc />
    /// <remarks>Empty. See <see cref="ApplicationDirectory" />.</remarks>
    public string CacheDirectory => string.Empty;

    /// <inheritdoc />
    /// <remarks>Empty. See <see cref="ApplicationDirectory" />.</remarks>
    public string TemporaryDirectory => string.Empty;

    /// <inheritdoc />
    /// <remarks>
    ///     The strongest sandbox of any target. A page cannot reach the file system at all, only its
    ///     origin's storage, and even that is subject to eviction and to the user clearing it.
    /// </remarks>
    public bool IsSandboxed => true;

    /// <summary><c>/data</c>, for a caller that wants to flush it.</summary>
    public IndexedDbFileProvider Data => data;

    /// <summary><c>/cache</c>, for a caller that wants to flush or measure it.</summary>
    public IndexedDbFileProvider Cache => cache;

    /// <summary><c>/app</c>, or <see langword="null" /> when nothing was mounted there.</summary>
    public FetchFileProvider? Content => content;

    /// <inheritdoc />
    public void MountStandardLocations(VirtualFileSystem fileSystem) {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (content is not null) {
            fileSystem.Mount(MountPoints.App, content);
        }

        fileSystem.Mount(MountPoints.Data, data);
        fileSystem.Mount(MountPoints.Cache, cache);
        fileSystem.Mount(MountPoints.Temp, temporary);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         Refuses everything, and the refusals are not the same shape as the mobile ones.
    ///         Storage is not a permission in a browser: an origin has some, always, and the user
    ///         clears it rather than granting it. Microphone, camera and notifications <em>are</em>
    ///         permissions, and each is asked for by starting the thing that needs it —
    ///         <c>getUserMedia</c> prompts, and prompting without then using the stream is how a page
    ///         gets its permission denied permanently.
    ///     </para>
    ///     <para>
    ///         So the audio backend asks for the microphone by opening a capture device and the
    ///         application asks for notifications when it has one to post. A generic "request this
    ///         permission" here would prompt at start-up, out of context, which is the pattern
    ///         browsers added permanent-denial for.
    ///     </para>
    /// </remarks>
    public ValueTask<bool> RequestPermissionAsync(
        PermissionKind permission,
        CancellationToken cancellationToken = default
    ) =>
        ValueTask.FromResult(
            permission switch {
                // An origin always has its own storage, and there is nothing to ask for.
                PermissionKind.ReadExternalStorage or PermissionKind.WriteExternalStorage => true,
                _ => false
            }
        );

    /// <summary>Waits for every pending write to <c>/data</c> and <c>/cache</c>.</summary>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <remarks>
    ///     What the platform calls when the tab is being hidden, which on the web is the last moment
    ///     there is: a hidden tab may be discarded without any further notice, and
    ///     <c>beforeunload</c> is not delivered when that happens.
    /// </remarks>
    public Task FlushAsync(CancellationToken cancellationToken = default) =>
        Task.WhenAll(data.FlushAsync(cancellationToken), cache.FlushAsync(cancellationToken));

    /// <summary>Asks the browser for storage it will not evict on its own.</summary>
    /// <param name="cancellationToken">Abandons the request.</param>
    /// <returns>Whether it was granted.</returns>
    /// <remarks>
    ///     Granted silently in Chromium for an installed or frequently used site, prompted for in
    ///     Firefox, refused in Safari. Worth asking at a moment the user has context for — after a
    ///     first save, not at start-up.
    /// </remarks>
    public static async Task<bool> RequestPersistentStorageAsync(CancellationToken cancellationToken = default) =>
        await WebInterop.PersistStorage().WaitAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        data.Dispose();
        cache.Dispose();
    }

    static async Task<WebContentManifest> ReadManifestAsync(
        string baseUrl,
        string manifestPath,
        CancellationToken cancellationToken
    ) {
        var url = (baseUrl.Length == 0 || baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/") + manifestPath;

        int handle;

        try {
            handle = await WebInterop.FetchAll(url).WaitAsync(cancellationToken).ConfigureAwait(false);
        } catch (Exception exception) when (exception is not OperationCanceledException) {
            throw new IOException(
                $"The content manifest at '{url}' could not be fetched. Every file under /app has to "
                + "be listed there: HTTP has no directory listing, and IFileProvider's synchronous "
                + "queries cannot go and look on a thread that must not block. Set "
                + "WebPlatformOptions.MountContent to false if this build has no shipped content.",
                exception
            );
        }

        return WebContentManifest.Parse(WebBuffer.Take(handle));
    }
}
