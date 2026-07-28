// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Platform.Web;

/// <summary>What a browser build needs told, because the page cannot be asked.</summary>
/// <remarks>
///     A record struct with defaults that work for an application whose build output sits at the
///     site root, so <c>await WebPlatform.CreateAsync()</c> is enough to get started and every field
///     is an opt-in for a page that arranges its assets differently.
/// </remarks>
public readonly record struct WebPlatformOptions() {
    /// <summary>Where <c>vixen-platform.js</c> is, or <see langword="null" /> for beside the
    /// assembly.</summary>
    /// <remarks>
    ///     <c>JSHost.ImportAsync</c> takes a path and not a stream, so the module has to be
    ///     fetchable by URL. It ships as a content file next to the assembly; a page that puts its
    ///     framework files somewhere else passes that somewhere.
    /// </remarks>
    public string? ModuleUrl { get; init; }

    /// <summary>Which canvas to draw on, as a CSS selector, or <see langword="null" /> to create
    /// one.</summary>
    /// <remarks>
    ///     A created canvas is <c>100%</c> of its parent and appended to the body, which is what a
    ///     full-page application wants. A page that embeds the view in a layout — a documentation
    ///     playground, the editor's asset browser — supplies its own element and keeps control of
    ///     where it sits.
    /// </remarks>
    public string? CanvasSelector { get; init; }

    /// <summary>Where <c>/app</c> is fetched from.</summary>
    /// <remarks>
    ///     Relative to the page. The default is where a Vixen content build puts its output when
    ///     the application head copies it to <c>wwwroot</c>.
    /// </remarks>
    public string ContentBaseUrl { get; init; } = "content/";

    /// <summary>The manifest naming everything under <see cref="ContentBaseUrl" />, relative to
    /// it.</summary>
    /// <remarks>
    ///     <b>Required, and not an optimisation.</b> HTTP has no directory listing and no
    ///     synchronous <c>HEAD</c>, and <see cref="Vixen.Core.IO.IFileProvider" />'s
    ///     <c>Exists</c>, <c>TryGetEntry</c> and <c>Enumerate</c> are synchronous. On the browser's
    ///     one thread there is no way to block for an answer — blocking does not wait, it deadlocks
    ///     the tab — so everything those three need has to be in memory before the first call. The
    ///     manifest is how it gets there. See <see cref="WebFileSystemHost" />.
    /// </remarks>
    public string ContentManifest { get; init; } = "manifest.json";

    /// <summary>The IndexedDB database backing <c>/data</c>.</summary>
    /// <remarks>
    ///     Per origin, not per page, so two applications served from the same origin want different
    ///     names — or they are two views of one save file, which is occasionally the point.
    /// </remarks>
    public string DataStoreName { get; init; } = "vixen-data";

    /// <summary>The IndexedDB database backing <c>/cache</c>.</summary>
    public string CacheStoreName { get; init; } = "vixen-cache";

    /// <summary>
    ///     Whether to ask the browser for storage it will not evict on its own, at start-up.
    /// </summary>
    /// <remarks>
    ///     Granted silently in Chromium for an installed or frequently used site, prompted for in
    ///     Firefox, and refused in Safari. Off by default because a prompt at start-up is a prompt
    ///     the user has no context for; an application that has saves worth keeping asks at a moment
    ///     that makes sense to them, through
    ///     <see cref="WebFileSystemHost.RequestPersistentStorageAsync" />.
    /// </remarks>
    public bool RequestPersistentStorage { get; init; }

    /// <summary>
    ///     Whether to mount <c>/app</c> at all, and fail loudly if its manifest is missing.
    /// </summary>
    /// <remarks>
    ///     A build whose content is entirely addressable — fetched from a remote group at run time,
    ///     which <c>docs/plan/10 § Web</c> says is where the addressable design pays off most — has
    ///     no shipped content mount and should not be made to invent one. Setting this false skips
    ///     the manifest fetch and leaves <c>/app</c> unmounted.
    /// </remarks>
    public bool MountContent { get; init; } = true;
}
