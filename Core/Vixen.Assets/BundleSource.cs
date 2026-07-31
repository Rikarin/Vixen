// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Serialization.Storage;

namespace Vixen.Assets;

/// <summary>Where a bundle's bytes come from.</summary>
/// <remarks>
///     <para>
///         The catalog says which bundle holds a chunk; this is what turns that name into something
///         the object database can read. Two implementations matter and they are very different
///         animals: a local bundle is a file that is definitely there, and a remote one may need
///         downloading, may fail, and may cost the player money.
///     </para>
///     <para>
///         <see cref="IsAvailable" /> is separate from <see cref="OpenAsync" /> so a caller can find
///         out what a load will cost <i>before</i> starting it. A game that wants to show a download
///         prompt has to be able to ask.
///     </para>
/// </remarks>
public interface IBundleSource {
    /// <summary>Whether the bundle can be opened without fetching anything.</summary>
    /// <param name="bundle">The bundle.</param>
    /// <returns>Whether it is here.</returns>
    bool IsAvailable(CatalogBundle bundle);

    /// <summary>Opens a bundle, fetching it first if that is what it takes.</summary>
    /// <param name="bundle">The bundle.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>A backend over its chunks. The source owns it.</returns>
    /// <exception cref="BundleUnavailableException">It is not here and could not be fetched.</exception>
    ValueTask<IOdbBackend> OpenAsync(CatalogBundle bundle, CancellationToken cancellationToken = default);

    /// <summary>Gets a bundle onto the device without opening it.</summary>
    /// <param name="bundle">The bundle.</param>
    /// <param name="progress">Told how far a download has got, if one happens.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>Nothing; the bundle is here when it completes.</returns>
    /// <exception cref="BundleUnavailableException">It could not be fetched.</exception>
    /// <remarks>
    ///     What a "download this DLC now, play it later" button is made of. Separate from
    ///     <see cref="OpenAsync" /> because opening memory-maps the file, and pre-fetching a whole
    ///     expansion pack should not map every byte of it into a process that is not going to read
    ///     them yet.
    /// </remarks>
    ValueTask EnsureAsync(
        CatalogBundle bundle,
        IProgress<BundleProgress>? progress = null,
        CancellationToken cancellationToken = default
    ) {
        cancellationToken.ThrowIfCancellationRequested();

        return IsAvailable(bundle)
            ? ValueTask.CompletedTask
            : throw new BundleUnavailableException(
                bundle.Name,
                "it is not here and this source cannot fetch anything."
            );
    }

    /// <summary>Gives back whatever local copy this source is keeping of a bundle.</summary>
    /// <param name="bundle">The bundle.</param>
    /// <returns><see langword="false" /> if there was nothing to give back, or it is in use.</returns>
    /// <remarks>
    ///     Only a caching source has anything to evict; a bundle that shipped inside the application
    ///     is not the runtime's to delete, so the default says no.
    /// </remarks>
    bool Evict(CatalogBundle bundle) => false;
}

/// <summary>A bundle that is not on the device and could not be fetched.</summary>
/// <param name="bundle">Which bundle.</param>
/// <param name="reason">Why not.</param>
/// <param name="inner">What went wrong underneath, if anything did.</param>
public sealed class BundleUnavailableException(string bundle, string reason, Exception? inner = null)
    : Exception($"Bundle '{bundle}' could not be opened: {reason}", inner) {
    /// <summary>Which bundle.</summary>
    public string Bundle { get; } = bundle;
}

/// <summary>What a bundle's file is called, on both sides of a build.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Here, in the assembly that owns the catalog, because a build writes these names and a
///         runtime reads them.</b> It was written down twice and the two disagreed:
///         <c>ContentBuilder</c> named a bundle after its group <i>and its content hash</i> — which is
///         <c>BundleNaming.FilenameHash</c>, the default — and <c>LocalBundleSource</c> looked
///         for the group's name alone. Every local bundle a real build produced was therefore
///         unopenable, and the failure arrived as <c>BundleUnavailableException</c> on the first
///         address a game asked for, having passed the catalog, the doctor and the determinism gate
///         on the way.
///     </para>
///     <para>
///         ⚠ <b>Both forms are derivable from the catalog, which is why the naming policy does not
///         need to be in it.</b> A <see cref="CatalogBundle" /> carries the name and the hash, and a
///         group's <c>BundleNaming</c> chooses between exactly these two spellings of them —
///         so a reader can name both candidates rather than the catalog having to record which was
///         chosen.
///     </para>
/// </remarks>
public static class BundleFile {
    /// <summary>What a bundle file's extension is.</summary>
    public const string Extension = ".bundle";

    /// <summary>
    ///     How much of the content hash a hashed name carries. Sixteen hex characters, which is
    ///     enough that two bundles colliding is not a thing that happens, and short enough that a
    ///     directory listing is still readable.
    /// </summary>
    public const int HashPrefixLength = 16;

    /// <summary>The name of a bundle whose group names its files after itself.</summary>
    /// <param name="bundle">The bundle's name.</param>
    /// <returns>The file name.</returns>
    public static string Named(string bundle) {
        ArgumentException.ThrowIfNullOrEmpty(bundle);
        return bundle + Extension;
    }

    /// <summary>The name of a bundle whose group appends the content hash.</summary>
    /// <param name="bundle">The bundle's name.</param>
    /// <param name="hash">Its content hash.</param>
    /// <returns>The file name.</returns>
    /// <remarks>
    ///     The hash is in the name so that two builds producing different bytes have different URLs
    ///     and a cache can never serve the old one — the only way a content update lands reliably.
    /// </remarks>
    public static string Hashed(string bundle, ObjectId hash) {
        ArgumentException.ThrowIfNullOrEmpty(bundle);
        return $"{bundle}_{hash.ToString()[..HashPrefixLength]}{Extension}";
    }
}

/// <summary>Bundles that shipped with the application.</summary>
/// <remarks>
///     Reads a bundle out of a directory by either of <see cref="BundleFile" />'s two names and keeps
///     each open once. A bundle backend is a window onto a memory-mapped file, so opening one twice
///     would map the same file twice for no reason and leave two lifetimes to get right instead of one.
/// </remarks>
public sealed class LocalBundleSource : IBundleSource, IDisposable {
    readonly Dictionary<string, IOdbBackend> opened = new(StringComparer.Ordinal);
    readonly VirtualFileSystem files;
    readonly VirtualPath root;
    readonly bool verifyChecksum;
    readonly Lock gate = new();

    /// <summary>Reads bundles from a directory.</summary>
    /// <param name="files">Where files come from.</param>
    /// <param name="root">The directory bundles live in.</param>
    /// <param name="verifyChecksum">Whether to check each bundle's checksum as it is opened.</param>
    public LocalBundleSource(VirtualFileSystem files, VirtualPath root, bool verifyChecksum = false) {
        ArgumentNullException.ThrowIfNull(files);

        this.files = files;
        this.root = root;
        this.verifyChecksum = verifyChecksum;
    }

    /// <summary>Where a bundle's file is.</summary>
    /// <param name="bundle">The bundle.</param>
    /// <returns>Its path, whether or not anything is there.</returns>
    /// <remarks>
    ///     ⚠ <b>Looked for rather than computed, because a group chooses between two names and the
    ///     catalog does not record which.</b> The hashed form is tried first: it is
    ///     <c>BundleNaming.FilenameHash</c>, which is the default and therefore what almost
    ///     every build writes. When neither is there this answers with the unhashed form, so the
    ///     failure names one path rather than two — and <see cref="OpenAsync" />'s message says both
    ///     spellings were tried.
    /// </remarks>
    public VirtualPath PathOf(CatalogBundle bundle) {
        var hashed = root / BundleFile.Hashed(bundle.Name, bundle.Hash);

        return files.Exists(hashed) ? hashed : root / BundleFile.Named(bundle.Name);
    }

    /// <inheritdoc />
    public bool IsAvailable(CatalogBundle bundle) => files.Exists(PathOf(bundle));

    /// <inheritdoc />
    public ValueTask<IOdbBackend> OpenAsync(CatalogBundle bundle, CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();

        lock (gate) {
            if (opened.TryGetValue(bundle.Name, out var already)) {
                return ValueTask.FromResult(already);
            }

            if (!IsAvailable(bundle)) {
                throw new BundleUnavailableException(
                    bundle.Name,
                    $"there is no {BundleFile.Hashed(bundle.Name, bundle.Hash)} and no "
                    + $"{BundleFile.Named(bundle.Name)} at {root}. A local bundle is one that shipped with the "
                    + "application, so either the build did not produce it or the catalog and the bundles came "
                    + "from different builds."
                );
            }

            var backend = BundleOdbBackend.Open(files, PathOf(bundle), verifyChecksum);
            opened[bundle.Name] = backend;

            return ValueTask.FromResult<IOdbBackend>(backend);
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        lock (gate) {
            foreach (var backend in opened.Values) {
                (backend as IDisposable)?.Dispose();
            }

            opened.Clear();
        }
    }
}

/// <summary>Bundles that are downloaded and cached.</summary>
/// <remarks>
///     <para>
///         Thin over <see cref="BundleCache" />, which does the fetching, resuming and verifying. What
///         this adds is the same thing <see cref="LocalBundleSource" /> adds: one open backend per
///         bundle, because a backend is a window onto a mapped file and mapping the same file twice
///         gives two lifetimes to get right instead of one.
///     </para>
///     <para>
///         <b>An open bundle cannot be evicted.</b> Deleting a file that is currently mapped is
///         permitted on Unix and refused on Windows, and the version that "works" on Unix leaves the
///         backend reading a file nothing can find. Refusing in both places is the behaviour that is
///         the same everywhere, and the caller who wanted the space back can release what is holding
///         it and ask again.
///     </para>
/// </remarks>
public sealed class RemoteBundleSource : IBundleSource, IDisposable {
    readonly Dictionary<string, IOdbBackend> opened = new(StringComparer.Ordinal);
    readonly VirtualFileSystem files;
    readonly bool verifyChecksum;
    readonly Lock gate = new();

    /// <summary>The cache behind it.</summary>
    public BundleCache Cache { get; }

    /// <summary>Reads bundles that have to be downloaded first.</summary>
    /// <param name="files">Where the cache directory lives.</param>
    /// <param name="cache">The cache.</param>
    /// <param name="verifyChecksum">Whether to check each bundle's own checksum as it is opened.</param>
    public RemoteBundleSource(VirtualFileSystem files, BundleCache cache, bool verifyChecksum = false) {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(cache);

        this.files = files;
        Cache = cache;
        this.verifyChecksum = verifyChecksum;
    }

    /// <inheritdoc />
    public bool IsAvailable(CatalogBundle bundle) => Cache.IsCached(bundle);

    /// <inheritdoc />
    public async ValueTask<IOdbBackend> OpenAsync(
        CatalogBundle bundle,
        CancellationToken cancellationToken = default
    ) {
        lock (gate) {
            if (opened.TryGetValue(bundle.Name, out var already)) {
                return already;
            }
        }

        var path = await Cache.EnsureAsync(bundle, null, cancellationToken).ConfigureAwait(false);

        lock (gate) {
            // Checked again: two loads that both missed the first check both got here, and only one
            // of them should map the file.
            if (opened.TryGetValue(bundle.Name, out var already)) {
                return already;
            }

            var backend = BundleOdbBackend.Open(files, path, verifyChecksum);
            opened[bundle.Name] = backend;

            return backend;
        }
    }

    /// <inheritdoc />
    public async ValueTask EnsureAsync(
        CatalogBundle bundle,
        IProgress<BundleProgress>? progress = null,
        CancellationToken cancellationToken = default
    ) => await Cache.EnsureAsync(bundle, progress, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public bool Evict(CatalogBundle bundle) {
        lock (gate) {
            if (opened.ContainsKey(bundle.Name)) {
                return false;
            }

            return Cache.Evict(bundle);
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        lock (gate) {
            foreach (var backend in opened.Values) {
                (backend as IDisposable)?.Dispose();
            }

            opened.Clear();
        }
    }
}

/// <summary>Sends each bundle to the source that can supply it.</summary>
/// <remarks>
///     <para>
///         A real catalog mixes the two: the base game ships inside the application and an expansion
///         is downloaded, and the asset manager is given one source rather than being taught the
///         difference. This is that one source.
///     </para>
///     <para>
///         <b>The URL decides, not <see cref="ContentProvider" />.</b> The provider is a property of an
///         <i>address</i> — it says what a load will cost the player — and what a bundle needs is a
///         property of the <i>bundle</i>. They agree in a well-formed catalog, and when they do not,
///         the one that determines whether there is anything to fetch is the one with the URL in it.
///     </para>
/// </remarks>
/// <param name="local">Bundles that shipped with the application.</param>
/// <param name="remote">Bundles that are downloaded.</param>
public sealed class RoutedBundleSource(IBundleSource local, IBundleSource remote) : IBundleSource, IDisposable {
    readonly IBundleSource local = local ?? throw new ArgumentNullException(nameof(local));
    readonly IBundleSource remote = remote ?? throw new ArgumentNullException(nameof(remote));

    /// <summary>Which source a bundle comes from.</summary>
    /// <param name="bundle">The bundle.</param>
    /// <returns>The source.</returns>
    public IBundleSource SourceFor(CatalogBundle bundle) => bundle.Url.Length == 0 ? local : remote;

    /// <inheritdoc />
    public bool IsAvailable(CatalogBundle bundle) => SourceFor(bundle).IsAvailable(bundle);

    /// <inheritdoc />
    public ValueTask<IOdbBackend> OpenAsync(CatalogBundle bundle, CancellationToken cancellationToken = default) =>
        SourceFor(bundle).OpenAsync(bundle, cancellationToken);

    /// <inheritdoc />
    public ValueTask EnsureAsync(
        CatalogBundle bundle,
        IProgress<BundleProgress>? progress = null,
        CancellationToken cancellationToken = default
    ) => SourceFor(bundle).EnsureAsync(bundle, progress, cancellationToken);

    /// <inheritdoc />
    public bool Evict(CatalogBundle bundle) => SourceFor(bundle).Evict(bundle);

    /// <inheritdoc />
    public void Dispose() {
        (local as IDisposable)?.Dispose();
        (remote as IDisposable)?.Dispose();
    }
}
