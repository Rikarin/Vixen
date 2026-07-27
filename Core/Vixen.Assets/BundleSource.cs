// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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
}

/// <summary>A bundle that is not on the device and could not be fetched.</summary>
/// <param name="bundle">Which bundle.</param>
/// <param name="reason">Why not.</param>
public sealed class BundleUnavailableException(string bundle, string reason)
    : Exception($"Bundle '{bundle}' could not be opened: {reason}") {
    /// <summary>Which bundle.</summary>
    public string Bundle { get; } = bundle;
}

/// <summary>Bundles that shipped with the application.</summary>
/// <remarks>
///     Reads <c>&lt;root&gt;/&lt;name&gt;.bundle</c> and keeps each open once. A bundle backend is a
///     window onto a memory-mapped file, so opening one twice would map the same file twice for no
///     reason and leave two lifetimes to get right instead of one.
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
    /// <returns>Its path.</returns>
    public VirtualPath PathOf(CatalogBundle bundle) => root / $"{bundle.Name}.bundle";

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
                    $"nothing is at {PathOf(bundle)}. A local bundle is one that shipped with the application, so "
                    + "either the build did not produce it or the catalog and the bundles came from different builds."
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
