// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.Globalization;
using System.IO.Hashing;
using Vixen.Core;
using Vixen.Core.IO;

namespace Vixen.Assets;

/// <summary>How far a download has got.</summary>
/// <param name="Bundle">Which bundle.</param>
/// <param name="Received">Bytes on disk, including anything a previous attempt left.</param>
/// <param name="Total">How many there will be when it is done.</param>
public readonly record struct BundleProgress(string Bundle, long Received, long Total) {
    /// <summary>A fraction between zero and one, or zero if the total is not known.</summary>
    public double Fraction => Total > 0 ? Math.Clamp((double)Received / Total, 0, 1) : 0;
}

/// <summary>Downloaded bundles, kept on the device and keyed by content.</summary>
/// <remarks>
///     <para>
///         <b>Keyed by hash, not by name.</b> A bundle called <c>dlc-pack-2</c> that gets rebuilt is a
///         different file with the same name, and a cache that trusted the name would serve the old
///         one forever. Filing it under its content hash means a rebuilt bundle is simply a cache
///         miss, and means two catalog versions that share an unchanged bundle share the download.
///     </para>
///     <para>
///         <b>Downloads resume.</b> Bytes go to <c>&lt;hash&gt;.part</c> as they arrive, and a fetch
///         that finds one asks the server to continue from where it stopped. This is not an
///         optimisation on a phone: a 400 MB pack over a connection that drops every few minutes never
///         finishes without it.
///     </para>
///     <para>
///         <b>Nothing is committed unverified.</b> A completed download has to be the length the
///         catalog says and hash to the CRC the catalog says before it is moved to
///         <c>&lt;hash&gt;.bundle</c>; anything else is deleted rather than kept and retried against.
///         The CRC is the catalog's, so it catches a corrupted transfer <i>and</i> a server serving
///         something else entirely at that URL.
///     </para>
///     <para>
///         <b>A cache hit is checked for length, not re-hashed.</b> Length is a metadata read and
///         catches the one failure a committed file can plausibly have — being truncated by a crash
///         mid-commit or by the OS reclaiming space. Re-hashing every cached bundle at every load
///         would put a full pass over hundreds of megabytes in front of a loading screen to catch bit
///         rot, which is not where that check belongs. <see cref="VerifyAsync" /> is there for a caller
///         who does want it.
///     </para>
/// </remarks>
public sealed class BundleCache {
    readonly Dictionary<ObjectId, Task<VirtualPath>> inFlight = [];
    readonly VirtualFileSystem files;
    readonly IContentTransport transport;
    readonly Lock gate = new();

    /// <summary>Where cached bundles are kept.</summary>
    public VirtualPath Root { get; }

    /// <summary>How many bytes to move at a time.</summary>
    /// <remarks>
    ///     Large enough that a download is not a syscall per packet, small enough that progress is
    ///     reported often enough to animate a bar.
    /// </remarks>
    public int BufferSize { get; init; } = 128 * 1024;

    /// <summary>Sets up a cache.</summary>
    /// <param name="files">Where the cache directory lives.</param>
    /// <param name="root">The cache directory.</param>
    /// <param name="transport">How to fetch.</param>
    public BundleCache(VirtualFileSystem files, VirtualPath root, IContentTransport transport) {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(transport);

        this.files = files;
        this.transport = transport;
        Root = root;
    }

    /// <summary>Where a bundle is cached once it is complete.</summary>
    /// <param name="bundle">The bundle.</param>
    /// <returns>Its path.</returns>
    public VirtualPath PathOf(CatalogBundle bundle) => Root / $"{bundle.Hash}.bundle";

    /// <summary>Where a bundle's bytes accumulate while it is being fetched.</summary>
    /// <param name="bundle">The bundle.</param>
    /// <returns>Its path.</returns>
    public VirtualPath PartialPathOf(CatalogBundle bundle) => Root / $"{bundle.Hash}.part";

    /// <summary>Whether a bundle is cached and the right length.</summary>
    /// <param name="bundle">The bundle.</param>
    /// <returns>Whether it can be read without fetching anything.</returns>
    public bool IsCached(CatalogBundle bundle) =>
        files.TryGetEntry(PathOf(bundle), out var entry)
        && !entry.IsDirectory
        && entry.Length == bundle.Size;

    /// <summary>How many bytes of a bundle a previous attempt already fetched.</summary>
    /// <param name="bundle">The bundle.</param>
    /// <returns>The length of its partial file, or zero if there is not one.</returns>
    public long ReceivedSoFar(CatalogBundle bundle) =>
        files.TryGetEntry(PartialPathOf(bundle), out var entry) && !entry.IsDirectory ? entry.Length : 0;

    /// <summary>Makes sure a bundle is on the device, fetching it if it is not.</summary>
    /// <param name="bundle">The bundle.</param>
    /// <param name="progress">Told how far the download has got, if it has to happen.</param>
    /// <param name="cancellationToken">Cancels the fetch. What has arrived is kept and resumable.</param>
    /// <returns>Where it is.</returns>
    /// <exception cref="BundleUnavailableException">It could not be fetched, or arrived corrupt.</exception>
    /// <remarks>
    ///     Two loads of the same bundle at once share one download, for the reason the asset manager
    ///     shares one deserialisation: checking "is it cached yet" and then fetching would fetch twice
    ///     under exactly the concurrency the check exists for, and the two would be writing to the same
    ///     partial file.
    /// </remarks>
    public Task<VirtualPath> EnsureAsync(
        CatalogBundle bundle,
        IProgress<BundleProgress>? progress = null,
        CancellationToken cancellationToken = default
    ) {
        if (IsCached(bundle)) {
            return Task.FromResult(PathOf(bundle));
        }

        TaskCompletionSource<VirtualPath> mine;

        lock (gate) {
            if (inFlight.TryGetValue(bundle.Hash, out var already)) {
                return already;
            }

            // The promise goes in the table before the work starts, not after it. A transport that
            // completes synchronously — a memory-backed one, a file:// mount — would otherwise run the
            // whole fetch, take its own entry out of the table, and only then have that entry written,
            // leaving a finished download registered as in flight for ever.
            mine = new(TaskCreationOptions.RunContinuationsAsynchronously);
            inFlight[bundle.Hash] = mine.Task;
        }

        _ = RunAsync(bundle, mine, progress, cancellationToken);

        return mine.Task;
    }

    /// <summary>Re-hashes a cached bundle and checks it against the catalog.</summary>
    /// <param name="bundle">The bundle.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns><see langword="false" /> if it is absent, the wrong length, or the wrong CRC.</returns>
    public async ValueTask<bool> VerifyAsync(CatalogBundle bundle, CancellationToken cancellationToken = default) {
        if (!IsCached(bundle)) {
            return false;
        }

        return await ChecksumAsync(PathOf(bundle), cancellationToken).ConfigureAwait(false) == bundle.Crc;
    }

    /// <summary>Deletes a bundle's cached copy and anything partial.</summary>
    /// <param name="bundle">The bundle.</param>
    /// <returns><see langword="false" /> if there was nothing to delete.</returns>
    public bool Evict(CatalogBundle bundle) {
        var removed = files.Delete(PathOf(bundle));
        return files.Delete(PartialPathOf(bundle)) || removed;
    }

    /// <summary>Deletes everything in the cache directory.</summary>
    /// <returns>How many files went.</returns>
    public int Clear() {
        var count = 0;

        foreach (var entry in files.Enumerate(Root).ToList()) {
            if (!entry.IsDirectory && files.Delete(entry.Path)) {
                count++;
            }
        }

        return count;
    }

    /// <summary>How much of the device the cache is using, complete and partial together.</summary>
    /// <returns>The total in bytes.</returns>
    public long TotalSize() {
        var total = 0L;

        foreach (var entry in files.Enumerate(Root)) {
            if (!entry.IsDirectory) {
                total += entry.Length;
            }
        }

        return total;
    }

    /// <summary>
    ///     Runs one fetch and hands its outcome to everyone waiting. The entry comes out of the table
    ///     whether it succeeded or failed, so a retry after a lost connection is a new attempt rather
    ///     than a second await on the failure that already happened.
    /// </summary>
    async Task RunAsync(
        CatalogBundle bundle,
        TaskCompletionSource<VirtualPath> mine,
        IProgress<BundleProgress>? progress,
        CancellationToken cancellationToken
    ) {
        try {
            var path = await FetchAsync(bundle, progress, cancellationToken).ConfigureAwait(false);
            Forget(bundle);
            mine.SetResult(path);
        } catch (Exception failure) {
            Forget(bundle);
            mine.SetException(failure);
        }
    }

    void Forget(CatalogBundle bundle) {
        lock (gate) {
            inFlight.Remove(bundle.Hash);
        }
    }

    async Task<VirtualPath> FetchAsync(
        CatalogBundle bundle,
        IProgress<BundleProgress>? progress,
        CancellationToken cancellationToken
    ) {
        try {
            if (bundle.Url.Length == 0) {
                throw new BundleUnavailableException(
                    bundle.Name,
                    "it is not cached and the catalog gives it no URL. A bundle with no URL is one that "
                    + "shipped with the application, which means the catalog and the build disagree."
                );
            }

            var partial = PartialPathOf(bundle);
            var received = ReceivedSoFar(bundle);

            // A partial longer than the whole thing is not a resumable download, it is a leftover from
            // a build that produced a different bundle under a hash collision or a truncated write.
            if (received >= bundle.Size) {
                files.Delete(partial);
                received = 0;
            }

            received = await ReceiveAsync(bundle, partial, received, progress, cancellationToken).ConfigureAwait(false);

            if (received != bundle.Size) {
                files.Delete(partial);

                throw new BundleUnavailableException(
                    bundle.Name,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"the download finished at {received} bytes and the catalog says it is {bundle.Size}"
                    )
                );
            }

            var checksum = await ChecksumAsync(partial, cancellationToken).ConfigureAwait(false);

            if (checksum != bundle.Crc) {
                // Deleted rather than kept: a resume against corrupt bytes would append good data to
                // bad and fail the same way forever.
                files.Delete(partial);

                throw new BundleUnavailableException(
                    bundle.Name,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"it arrived with CRC {checksum:x8} and the catalog says {bundle.Crc:x8}. Either the "
                        + $"transfer was corrupted or '{bundle.Url}' is not serving the bundle this catalog was built against."
                    )
                );
            }

            await CommitAsync(partial, PathOf(bundle), cancellationToken).ConfigureAwait(false);

            return PathOf(bundle);
        } catch (Exception failure) when (failure is ContentTransportException or IOException) {
            // A connection that dies part way through the body arrives here as an IOException from the
            // response stream rather than from the transport, and it is the same event as far as the
            // caller is concerned. What has been received is deliberately left on disk: the next
            // attempt resumes from it, which is the entire reason the partial file exists.
            throw new BundleUnavailableException(bundle.Name, failure.Message, failure);
        }
    }

    /// <summary>Appends to the partial file until the body runs out. Returns the new length.</summary>
    async Task<long> ReceiveAsync(
        CatalogBundle bundle,
        VirtualPath partial,
        long received,
        IProgress<BundleProgress>? progress,
        CancellationToken cancellationToken
    ) {
        using var download = await transport.GetAsync(bundle.Url, received, cancellationToken).ConfigureAwait(false);

        if (download.Offset != received) {
            if (download.Offset != 0) {
                throw new BundleUnavailableException(
                    bundle.Name,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"the server was asked to continue from byte {received} and answered from byte {download.Offset}"
                    )
                );
            }

            // The range was ignored and the body is the whole resource. Starting again is the only
            // correct move: appending it would produce a file that is too long and hashes to nothing.
            files.Delete(partial);
            received = 0;
        }

        progress?.Report(new(bundle.Name, received, bundle.Size));

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        try {
            var writing = await files.OpenAppendAsync(partial, cancellationToken).ConfigureAwait(false);

            await using (writing.ConfigureAwait(false)) {
                while (true) {
                    var read = await download.Body
                        .ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)
                        .ConfigureAwait(false);

                    if (read == 0) {
                        break;
                    }

                    await writing.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;
                    progress?.Report(new(bundle.Name, received, bundle.Size));
                }
            }
        } finally {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return received;
    }

    async ValueTask<uint> ChecksumAsync(VirtualPath path, CancellationToken cancellationToken) {
        var checksum = new Crc32();
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        try {
            var reading = await files.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);

            await using (reading.ConfigureAwait(false)) {
                while (true) {
                    var read = await reading
                        .ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)
                        .ConfigureAwait(false);

                    if (read == 0) {
                        break;
                    }

                    checksum.Append(buffer.AsSpan(0, read));
                }
            }
        } finally {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return checksum.GetCurrentHashAsUInt32();
    }

    /// <summary>
    ///     Moves a verified partial file into place. Copy-then-delete, because the file provider
    ///     interface has no rename: a provider can be a dictionary, an APK entry or an HTTP mount, and
    ///     only one of the three has a directory to rename within. The window this leaves — a crash
    ///     between the two writes — is what the length check in <see cref="IsCached" /> is for.
    /// </summary>
    async ValueTask CommitAsync(VirtualPath partial, VirtualPath final, CancellationToken cancellationToken) {
        var reading = await files.OpenReadAsync(partial, cancellationToken).ConfigureAwait(false);

        await using (reading.ConfigureAwait(false)) {
            var writing = await files.OpenWriteAsync(final, cancellationToken).ConfigureAwait(false);

            await using (writing.ConfigureAwait(false)) {
                await reading.CopyToAsync(writing, BufferSize, cancellationToken).ConfigureAwait(false);
            }
        }

        files.Delete(partial);
    }
}
