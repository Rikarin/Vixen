// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Serialization.Storage;

namespace Vixen.Assets;

/// <summary>What happened when a game asked whether there was new content.</summary>
public enum ContentUpdateOutcome {
    /// <summary>Nothing was configured to check, so the shipped catalog stands.</summary>
    NoRemoteConfigured,

    /// <summary>The server's catalog is the one already cached, so nothing was downloaded.</summary>
    AlreadyCurrent,

    /// <summary>A newer catalog was downloaded and laid over the shipped one.</summary>
    Updated,

    /// <summary>The server could not be reached; whatever was cached is being used instead.</summary>
    Offline,

    /// <summary>The server answered, and what it served cannot be used; whatever was cached is being used instead.</summary>
    /// <remarks>
    ///     Kept apart from <see cref="Offline" /> because they are different facts and want different
    ///     responses. Offline is a player on a train and will fix itself; rejected is a broken publish
    ///     and will not.
    /// </remarks>
    Rejected
}

/// <summary>The catalog a game is going to resolve addresses through, and how it was arrived at.</summary>
/// <param name="Catalog">What to use.</param>
/// <param name="Outcome">How it was arrived at.</param>
/// <param name="Reason">Why, when that is not obvious — the transport's message on a failure.</param>
public readonly record struct ContentUpdateResult(
    ContentCatalog Catalog,
    ContentUpdateOutcome Outcome,
    string? Reason = null
);

/// <summary>The server answered, and what it said cannot be used.</summary>
/// <param name="message">What is wrong with it.</param>
/// <remarks>
///     Internal, and never escapes <see cref="ContentUpdate.ApplyAsync" /> — it is how the fetch
///     tells the outer method which <see cref="ContentUpdateOutcome" /> to report.
/// </remarks>
sealed class BadCatalogException(string message) : Exception(message);

/// <summary>Step 2 of the boot sequence: find out whether the server has newer content.</summary>
/// <remarks>
///     <para>
///         Doc 08 lays out four steps — load the shipped catalog, check for a remote one and merge it
///         over, resolve addresses through the result, fetch bundles on demand. This is the second,
///         and it is the one that makes a content update possible without shipping a build.
///     </para>
///     <para>
///         <b>The hash file is checked first because it is tiny.</b> A catalog for a real game is
///         hundreds of kilobytes and almost always unchanged; a 32-byte file next to it turns the
///         common case — start the game, nothing is new — into one request the size of a packet. It
///         also gives the downloaded catalog something to be checked against, which a catalog fetched
///         on its own does not have.
///     </para>
///     <para>
///         <b>Being offline is an outcome, not an error.</b> A game whose CDN is unreachable has to
///         start anyway, on the newest catalog it has: the previously downloaded one if there is one,
///         and the shipped one if not. Throwing would turn a flaky connection into a game that will
///         not launch. It is still <i>reported</i>, because the same silence means a misconfigured URL
///         and nobody should have to guess which.
///     </para>
///     <para>
///         <b>An update can replace an address but not remove one.</b> That is
///         <see cref="ContentCatalog.MergedWith" />'s rule and the reason is there: the shipped
///         application still has the bundle on the device, and a runtime that forgot the address would
///         refuse to load something it is sitting on.
///     </para>
/// </remarks>
public sealed class ContentUpdate {
    readonly VirtualFileSystem files;
    readonly IContentTransport transport;

    /// <summary>Where the downloaded catalog and its hash are kept.</summary>
    public VirtualPath CacheRoot { get; }

    /// <summary>Where the catalog is served from, or empty if no remote content is configured.</summary>
    public string CatalogUrl { get; }

    /// <summary>Where the hash naming that catalog is served from.</summary>
    /// <remarks>
    ///     Alongside the catalog with <c>.hash</c> appended, so publishing a content build is copying
    ///     one directory rather than configuring two URLs that have to stay in step.
    /// </remarks>
    public string HashUrl => CatalogUrl.Length == 0 ? string.Empty : CatalogUrl + ".hash";

    /// <summary>Where the downloaded catalog is cached.</summary>
    public VirtualPath CachedCatalogPath => CacheRoot / "catalog.bin";

    /// <summary>Where the hash of the downloaded catalog is cached.</summary>
    public VirtualPath CachedHashPath => CacheRoot / "catalog.hash";

    /// <summary>Sets up the check.</summary>
    /// <param name="files">Where the cache lives.</param>
    /// <param name="cacheRoot">The cache directory.</param>
    /// <param name="transport">How to reach the server.</param>
    /// <param name="catalogUrl">Where the catalog is served, or empty for a game with no remote content.</param>
    public ContentUpdate(
        VirtualFileSystem files,
        VirtualPath cacheRoot,
        IContentTransport transport,
        string catalogUrl
    ) {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(catalogUrl);

        this.files = files;
        this.transport = transport;
        CacheRoot = cacheRoot;
        CatalogUrl = catalogUrl;
    }

    /// <summary>Which catalog is cached, if one is.</summary>
    /// <returns>Its hash, or <see langword="null" /> if nothing has been downloaded.</returns>
    public ObjectId? CachedVersion() {
        if (!files.Exists(CachedHashPath) || !files.Exists(CachedCatalogPath)) {
            return null;
        }

        using var reading = files.OpenRead(CachedHashPath);
        using var reader = new StreamReader(reading, Encoding.UTF8);

        return ObjectId.TryParse(reader.ReadToEnd().Trim(), out var id) ? id : null;
    }

    /// <summary>Checks for newer content and returns the catalog to resolve addresses through.</summary>
    /// <param name="local">The catalog that shipped with the application.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The catalog to use, and how it was arrived at.</returns>
    /// <remarks>
    ///     <b>This does not throw for anything the server does.</b> Unreachable, half-published, built
    ///     for another platform, corrupt — every one of them comes back as an outcome with a reason
    ///     and the best catalog available, because all of them happen in the field and none of them is
    ///     a reason for a game not to start. The distinction that matters to whoever reads the log is
    ///     <see cref="ContentUpdateOutcome.Offline" /> against
    ///     <see cref="ContentUpdateOutcome.Rejected" />, and that is why they are separate.
    /// </remarks>
    public async Task<ContentUpdateResult> ApplyAsync(
        ContentCatalog local,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(local);

        if (CatalogUrl.Length == 0) {
            return new(local, ContentUpdateOutcome.NoRemoteConfigured);
        }

        var cached = CachedVersion();
        ObjectId advertised;

        try {
            advertised = await FetchHashAsync(cancellationToken).ConfigureAwait(false);
        } catch (ContentTransportException failure) {
            return await FallBackAsync(local, ContentUpdateOutcome.Offline, failure.Message, cancellationToken)
                .ConfigureAwait(false);
        } catch (BadCatalogException failure) {
            return await FallBackAsync(local, ContentUpdateOutcome.Rejected, failure.Message, cancellationToken)
                .ConfigureAwait(false);
        }

        if (cached == advertised) {
            return await FallBackAsync(local, ContentUpdateOutcome.AlreadyCurrent, null, cancellationToken)
                .ConfigureAwait(false);
        }

        byte[] downloaded;

        try {
            downloaded = await FetchAsync(CatalogUrl, cancellationToken).ConfigureAwait(false);
        } catch (Exception failure) when (failure is ContentTransportException or IOException) {
            return await FallBackAsync(local, ContentUpdateOutcome.Offline, failure.Message, cancellationToken)
                .ConfigureAwait(false);
        }

        var actual = ContentHash.Compute(downloaded);

        if (actual != advertised) {
            // The hash file and the catalog disagree, which is a half-published build or a cache
            // holding one of them stale. Neither is something to guess about.
            return await FallBackAsync(
                local,
                ContentUpdateOutcome.Rejected,
                $"the hash file says {advertised} and the catalog served hashes to {actual}, so the two are from "
                + "different builds",
                cancellationToken
            ).ConfigureAwait(false);
        }

        ContentCatalog merged;

        try {
            // Parsed and merged before anything is cached, so a catalog that cannot be used is not
            // written over one that could be — the next launch would then be broken with nothing left
            // to fall back to.
            merged = local.MergedWith(CatalogFormat.Read(downloaded));
        } catch (Exception failure) when (failure is CatalogFormatException or ArgumentException) {
            return await FallBackAsync(local, ContentUpdateOutcome.Rejected, failure.Message, cancellationToken)
                .ConfigureAwait(false);
        }

        await WriteCacheAsync(downloaded, advertised, cancellationToken).ConfigureAwait(false);

        return new(merged, ContentUpdateOutcome.Updated);
    }

    /// <summary>Uses the newest catalog on the device: the last downloaded one, or the shipped one.</summary>
    async Task<ContentUpdateResult> FallBackAsync(
        ContentCatalog local,
        ContentUpdateOutcome outcome,
        string? reason,
        CancellationToken cancellationToken
    ) {
        if (CachedVersion() is null) {
            return new(local, outcome, reason);
        }

        try {
            var cached = await ReadCachedAsync(cancellationToken).ConfigureAwait(false);
            return new(local.MergedWith(cached), outcome, reason);
        } catch (Exception failure) when (failure is CatalogFormatException or ArgumentException) {
            // The cached copy is unusable too. Two failures deep, the shipped catalog is still a game
            // that starts.
            var why = $"the cached catalog is also unusable: {failure.Message}";

            return new(local, outcome, reason is null ? why : $"{reason}; {why}");
        }
    }

    async Task<ContentCatalog> ReadCachedAsync(CancellationToken cancellationToken) =>
        CatalogFormat.Read(await files.ReadAllBytesAsync(CachedCatalogPath, cancellationToken).ConfigureAwait(false));

    async Task<ObjectId> FetchHashAsync(CancellationToken cancellationToken) {
        var text = Encoding.UTF8.GetString(await FetchAsync(HashUrl, cancellationToken).ConfigureAwait(false)).Trim();

        return ObjectId.TryParse(text, out var id)
            ? id
            : throw new BadCatalogException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{HashUrl}' should hold one {ObjectId.TextLength}-character hash and holds {text.Length} characters of something else"
                )
            );
    }

    async Task<byte[]> FetchAsync(string url, CancellationToken cancellationToken) {
        using var download = await transport.GetAsync(url, 0, cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();

        await download.Body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        return buffer.ToArray();
    }

    async ValueTask WriteCacheAsync(byte[] catalog, ObjectId hash, CancellationToken cancellationToken) {
        await files.WriteAllBytesAsync(CachedCatalogPath, catalog, cancellationToken).ConfigureAwait(false);

        // The hash file is written second and read first, so a crash between the two writes leaves a
        // catalog nothing claims — which reads as "nothing cached" and is refetched, rather than as a
        // catalog that answers to the wrong name.
        await files.WriteAllTextAsync(CachedCatalogPath.Parent / "catalog.hash", hash.ToString(), cancellationToken)
            .ConfigureAwait(false);
    }
}
