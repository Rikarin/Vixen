// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Core;
using Vixen.Core.Serialization;
using Vixen.Core.Serialization.Storage;

namespace Vixen.Assets;

/// <summary>Turns addresses into loaded assets, once each, and unloads them when nobody wants them.</summary>
/// <remarks>
///     <para>
///         Three things joined: the catalog says what an address is and what it needs, the bundle
///         source says where those bytes are, and the object database turns bytes into objects. What
///         this adds is the part none of them can do alone — knowing that two callers asking for the
///         same texture should get one texture, and that it goes away when both are done.
///     </para>
///     <para>
///         <b>Dependencies are claimed by their dependents.</b> Loading a material claims the texture
///         it points at, so the texture survives exactly as long as some material needs it. That is
///         the property that makes sharing safe, and it is why a handle remembers the whole closure
///         it acquired rather than just the address that was asked for.
///     </para>
///     <para>
///         <b>Loading is deduplicated by the task, not by the result.</b> Two callers asking for the
///         same address while it is still in flight get the same <see cref="Task{TResult}" />, so the
///         work happens once even though neither of them waited for the other. Checking "is it
///         loaded yet" instead would start it twice under exactly the concurrency the check exists
///         for.
///     </para>
///     <para>
///         <b>A dependency is loaded before the thing that needs it, and shared with it.</b> The
///         closure comes back dependency-first, each address is deserialised in that order, and a
///         resolver is in force while it happens — so a material's reference to a texture lands on
///         the very object the manager already loaded rather than on a second copy of it. That is
///         what makes two materials sharing a texture mean one texture.
///     </para>
/// </remarks>
public sealed class AssetManager {
    readonly Dictionary<string, Claim> claims = new(StringComparer.Ordinal);
    readonly ObjectDatabase database;
    readonly Lock gate = new();

    /// <summary>What addresses mean.</summary>
    public ContentCatalog Catalog { get; }

    /// <summary>Where bundles come from.</summary>
    public IBundleSource Bundles { get; }

    /// <summary>How many addresses are currently held.</summary>
    public int LoadedCount {
        get {
            lock (gate) {
                return claims.Count;
            }
        }
    }

    /// <summary>Sets up a manager.</summary>
    /// <param name="catalog">What addresses mean.</param>
    /// <param name="bundles">Where bundles come from.</param>
    /// <param name="database">Where chunks are read from. A fresh one if not given.</param>
    public AssetManager(ContentCatalog catalog, IBundleSource bundles, ObjectDatabase? database = null) {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(bundles);

        Catalog = catalog;
        Bundles = bundles;
        this.database = database ?? new();
    }

    /// <summary>Starts loading an asset and everything it needs.</summary>
    /// <typeparam name="T">What to load it as.</typeparam>
    /// <param name="address">The address.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>A handle, which the caller releases.</returns>
    /// <exception cref="AddressNotFoundException">The catalog has no such address.</exception>
    public AssetHandle<T> LoadAsync<T>(string address, CancellationToken cancellationToken = default)
        where T : class {
        ArgumentNullException.ThrowIfNull(address);

        // Resolved before anything is claimed, so a misspelled address fails without leaving half a
        // closure held by a handle nobody got back.
        _ = Catalog.Get(address);
        var closure = Catalog.Closure(address);

        lock (gate) {
            foreach (var needed in closure) {
                Claimed(needed);
            }
        }

        return new(this, address, closure, LoadRootAsync<T>(address, closure, cancellationToken));
    }

    /// <summary>Loads an asset, blocking until it is there.</summary>
    /// <typeparam name="T">What to load it as.</typeparam>
    /// <param name="address">The address.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>A handle whose result is already available.</returns>
    /// <remarks>
    ///     Honest rather than hidden. A load screen genuinely wants to block, and pretending
    ///     otherwise pushes people towards <c>.Result</c> on the async form, which deadlocks on a
    ///     synchronisation context. Doc 08 puts an analyzer on calling this from an update method,
    ///     which is where it is actually a mistake.
    /// </remarks>
    public AssetHandle<T> Load<T>(string address, CancellationToken cancellationToken = default)
        where T : class {
        var handle = LoadAsync<T>(address, cancellationToken);
        handle.GetAwaiter().GetResult();

        return handle;
    }

    /// <summary>Starts loading whatever a <c>vx:</c> reference points at.</summary>
    /// <typeparam name="T">What to load it as.</typeparam>
    /// <param name="reference">The reference a component, material or prefab holds.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>A handle, which the caller releases.</returns>
    /// <exception cref="ReferenceNotFoundException">This build shipped nothing under that identity.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>The overload a runtime component actually needs.</b> An entity holds an
    ///         <see cref="AssetId" /> because that is what survives renaming a file, and every other
    ///         entry point here takes an address, so until this existed there was no way for a component
    ///         to name something loadable — see <c>ContentCatalog.TryGetAddress</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A resolution failure is its own exception, not <see cref="AddressNotFoundException" />.</b>
    ///         The two say different things to whoever reads the log: a missing address is a typo in a
    ///         call, and a missing reference is content that was not built — an asset excluded from the
    ///         build, or a scene saved against something since deleted. Reporting the second as the first
    ///         would send somebody looking for a spelling mistake in a string they never wrote.
    ///     </para>
    /// </remarks>
    public AssetHandle<T> LoadAsync<T>(AssetReference reference, CancellationToken cancellationToken = default)
        where T : class =>
        LoadAsync<T>(AddressOf(reference), cancellationToken);

    /// <summary>Loads whatever a <c>vx:</c> reference points at, blocking until it is there.</summary>
    /// <typeparam name="T">What to load it as.</typeparam>
    /// <param name="reference">The reference.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>A handle whose result is already available.</returns>
    /// <exception cref="ReferenceNotFoundException">This build shipped nothing under that identity.</exception>
    public AssetHandle<T> Load<T>(AssetReference reference, CancellationToken cancellationToken = default)
        where T : class =>
        Load<T>(AddressOf(reference), cancellationToken);

    /// <summary>What address a reference resolves to.</summary>
    /// <param name="reference">The reference.</param>
    /// <returns>The address.</returns>
    /// <exception cref="ReferenceNotFoundException">This build shipped nothing under that identity.</exception>
    /// <remarks>
    ///     Public because a caller that holds a reference and wants the address rather than the asset —
    ///     a diagnostic, a dependency walk, a "what would this cost to download" — should not have to
    ///     load it to find out.
    /// </remarks>
    public string AddressOf(AssetReference reference) =>
        Catalog.TryGetAddress(reference, out var address)
            ? address
            : throw new ReferenceNotFoundException(reference);

    /// <summary>Loads everything carrying a label.</summary>
    /// <typeparam name="T">What to load them as.</typeparam>
    /// <param name="label">The label.</param>
    /// <param name="cancellationToken">Cancels the loads.</param>
    /// <returns>One handle each, in address order.</returns>
    public ImmutableArray<AssetHandle<T>> LoadByLabelAsync<T>(
        string label,
        CancellationToken cancellationToken = default
    )
        where T : class =>
        [.. Catalog.ByLabel(label).Select(address => LoadAsync<T>(address, cancellationToken))];

    /// <summary>Loads everything matching a glob.</summary>
    /// <typeparam name="T">What to load them as.</typeparam>
    /// <param name="pattern">The pattern.</param>
    /// <param name="cancellationToken">Cancels the loads.</param>
    /// <returns>One handle each, in address order.</returns>
    public ImmutableArray<AssetHandle<T>> LoadMatchingAsync<T>(
        string pattern,
        CancellationToken cancellationToken = default
    )
        where T : class =>
        [.. Catalog.Match(pattern).Select(address => LoadAsync<T>(address, cancellationToken))];

    /// <summary>Opens a scope that releases everything loaded through it.</summary>
    /// <returns>The scope.</returns>
    public AssetScope Scope() => new(this);

    /// <summary>Opens an address's bytes as a stream, without deserialising anything.</summary>
    /// <param name="address">The address.</param>
    /// <param name="cancellationToken">Cancels the fetch, if the bundle has to be downloaded.</param>
    /// <returns>A seekable stream over its payload, which the caller disposes.</returns>
    /// <exception cref="AddressNotFoundException">The catalog has no such address.</exception>
    /// <exception cref="BundleUnavailableException">Its bundle is not here and could not be fetched.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>For the content that is streamed rather than loaded.</b> A video is the case this
    ///         exists for: a two-minute cutscene is a hundred megabytes and turning it into an object
    ///         would mean a loading screen for a cutscene longer than the cutscene — so the asset the
    ///         catalog holds is a small record naming this, and this is a stream a demuxer reads.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It claims nothing and caches nothing, unlike every <c>Load</c> here.</b> There is
    ///         no object to share, so there is nothing for a second caller to be given and nothing to
    ///         release; the stream is the caller's and the bundle stays mounted because bundles
    ///         always do. That also means two callers get two streams over the same bytes, which is
    ///         exactly what a video whose picture and sound both seek needs.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The payload is a copy, and that is a cost worth naming.</b> A bundle backend hands
    ///         out a window onto a memory-mapped file, but a chunk is stored compressed by default —
    ///         so there is no slice of the map that <i>is</i> the payload, and decompressing produces
    ///         an array. Content that is genuinely streamed should be built with
    ///         <c>CompressionMethod.None</c>, which a video already wants: a WebM is compressed
    ///         already and packing it again costs the build time and saves nothing.
    ///     </para>
    /// </remarks>
    public async ValueTask<Stream> OpenAsync(string address, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(address);

        var entry = Catalog.Get(address);

        await MountFor(address, cancellationToken).ConfigureAwait(false);

        return new MemoryStream(database.ReadRaw(entry.Id, out _), writable: false);
    }

    /// <summary>Opens an address's bytes, blocking until they are there.</summary>
    /// <param name="address">The address.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>A seekable stream over its payload, which the caller disposes.</returns>
    /// <remarks>
    ///     Honest rather than hidden, for the reason <see cref="Load{T}(string, CancellationToken)" />
    ///     is: the alternative pushes
    ///     people towards <c>.Result</c> on the asynchronous form, which deadlocks on a
    ///     synchronisation context.
    /// </remarks>
    public Stream Open(string address, CancellationToken cancellationToken = default) =>
        OpenAsync(address, cancellationToken).AsTask().GetAwaiter().GetResult();

    /// <summary>Whether an address is one this manager could open right now.</summary>
    /// <param name="address">The address.</param>
    /// <returns>Whether the catalog knows it and its bundle is on the device.</returns>
    /// <remarks>
    ///     What a title checks before committing to a cutscene, so that a missing download is a
    ///     fallback rather than an exception halfway through a fade.
    /// </remarks>
    public bool CanOpen(string address) =>
        address is not null
        && Catalog.TryGet(address, out var entry)
        && (entry.Bundle.Length == 0
            || (Catalog.TryGetBundle(entry.Bundle, out var bundle) && Bundles.IsAvailable(bundle)));

    /// <summary>How many bytes have to be downloaded before some addresses can load.</summary>
    /// <param name="addresses">The addresses.</param>
    /// <returns>The size on the wire, counting each bundle once and skipping cached ones.</returns>
    /// <remarks>
    ///     The number a "this pack is 240 MB, continue?" prompt shows, which is why it asks the bundle
    ///     source what is already here rather than reporting the pack's full size to a player who
    ///     downloaded most of it yesterday.
    /// </remarks>
    public long DownloadSize(params IEnumerable<string> addresses) =>
        Catalog.DownloadSize(addresses, Bundles.IsAvailable);

    /// <summary>Downloads everything some addresses need, without loading any of it.</summary>
    /// <param name="addresses">The addresses.</param>
    /// <param name="progress">Told how each bundle is getting on.</param>
    /// <param name="cancellationToken">Cancels the downloads. What arrived stays and resumes.</param>
    /// <returns>Nothing; the bundles are on the device when it completes.</returns>
    /// <exception cref="BundleUnavailableException">One of them could not be fetched.</exception>
    /// <remarks>
    ///     One bundle at a time on purpose. Parallel downloads make a progress bar jump about, and on
    ///     the connection this feature exists for they do not go faster — they divide the same
    ///     bandwidth into streams that each take longer to become a usable, resumable file.
    /// </remarks>
    public async Task DownloadAsync(
        IEnumerable<string> addresses,
        IProgress<BundleProgress>? progress = null,
        CancellationToken cancellationToken = default
    ) {
        foreach (var bundle in Catalog.RemoteBundlesFor(addresses)) {
            await Bundles.EnsureAsync(bundle, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Deletes the cached copies of everything some addresses need.</summary>
    /// <param name="addresses">The addresses.</param>
    /// <returns>How many bundles went.</returns>
    /// <remarks>
    ///     A bundle that something still has open is left alone and not counted, because a backend is
    ///     a window onto a mapped file and deleting the file underneath it does not close the window.
    ///     Releasing what holds it and asking again is the way to get that space back.
    /// </remarks>
    public int ClearCache(params IEnumerable<string> addresses) {
        var cleared = 0;

        foreach (var bundle in Catalog.RemoteBundlesFor(addresses)) {
            if (Bundles.Evict(bundle)) {
                cleared++;
            }
        }

        return cleared;
    }

    /// <summary>Whether an address is currently held by anyone.</summary>
    /// <param name="address">The address.</param>
    /// <returns>Whether it is.</returns>
    public bool IsLoaded(string address) {
        lock (gate) {
            return claims.ContainsKey(address);
        }
    }

    /// <summary>How many claims are outstanding on an address.</summary>
    /// <param name="address">The address.</param>
    /// <returns>The count, zero if nothing holds it.</returns>
    public int ClaimCount(string address) {
        lock (gate) {
            return claims.TryGetValue(address, out var claim) ? claim.Count : 0;
        }
    }

    internal void ReleaseAll(ImmutableArray<string> addresses) {
        lock (gate) {
            foreach (var address in addresses) {
                if (!claims.TryGetValue(address, out var claim)) {
                    continue;
                }

                if (--claim.Count > 0) {
                    continue;
                }

                claims.Remove(address);

                // Whatever it holds goes now rather than at the next collection, because a texture's
                // real cost is on the GPU and the garbage collector has no idea that exists.
                if (claim.Value is { IsCompletedSuccessfully: true, Result: IDisposable disposable }) {
                    disposable.Dispose();
                }
            }
        }
    }

    /// <summary>Finds or starts a claim. The caller holds <see cref="gate" />.</summary>
    Claim Claimed(string address) {
        if (claims.TryGetValue(address, out var existing)) {
            existing.Count++;
            return existing;
        }

        var claim = new Claim { Count = 1 };
        claims[address] = claim;

        return claim;
    }

    async Task<T> LoadRootAsync<T>(
        string address,
        ImmutableArray<string> closure,
        CancellationToken cancellationToken
    )
        where T : class {
        try {
            // Every bundle in the closure, not just the root's: a chunk's own references are resolved
            // through the database, which can only find them in a backend that is mounted.
            foreach (var needed in closure) {
                await MountFor(needed, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Dependency-first, which is the order Catalog.Closure hands them over in. Anything a
            // chunk points at is already an object by the time that chunk is read, which is what
            // lets the resolver below answer.
            //
            // ⚠ A dependency this cannot deserialise is skipped rather than fatal, and that is the
            // difference between a closure walk and a load. Plenty of shipped content is deliberately
            // not a serialized object — a mesh's cluster hierarchy and page blob are byte spans
            // VirtualGeometrySystem reads with Open, and they carry no [DataContract] because nothing
            // should ever hand them to a serializer. They are still dependencies, so they are still in
            // the closure and their bundles still have to be mounted; what they are not is something
            // to preload into an object. Failing here made a model with a distance field unloadable
            // by anything that referenced it, which is to say by every scene in the project.
            //
            // The root itself is still strict. Whoever asked for that address named a type, and
            // getting it wrong is their bug rather than the content's.
            foreach (var needed in closure) {
                if (needed != address) {
                    await Preload(needed).ConfigureAwait(false);
                }
            }

            var loaded = await Deserialise<T>(address).ConfigureAwait(false);

            return loaded as T
                ?? throw new InvalidOperationException(
                    $"'{address}' loaded as {loaded.GetType().Name} and was asked for as {typeof(T).Name}. The "
                    + "address is right and the type is not, which usually means two things share a name."
                );
        } catch {
            // Everything this handle claimed has to be given back, or a failed load leaks every
            // asset it got hold of before the one that broke.
            ReleaseAll(closure);
            throw;
        }
    }

    async ValueTask MountFor(string address, CancellationToken cancellationToken) {
        if (!Catalog.TryGet(address, out var entry)
            || entry.Bundle.Length == 0
            || !Catalog.TryGetBundle(entry.Bundle, out var bundle)) {
            // A loose chunk at edit time names no bundle and is already reachable.
            return;
        }

        database.Mount(await Bundles.OpenAsync(bundle, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    ///     Deserialises a dependency, whose static type nothing here knows: it is named by an address
    ///     and its type is in the chunk header. <see cref="ObjectDatabase.ReadObject" /> is the way
    ///     back from one to the other.
    /// </summary>
    Task<object> Deserialise(string address) => Deserialise(address, database.ReadObject);

    /// <summary>
    ///     Deserialises a dependency if anything can, and records that it could not if nothing can.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The closure walk's version of <see cref="Deserialise(string)" />. Content produced by a
    ///         tool rather than by the serializer — a mesh's cluster hierarchy, its page blob, a
    ///         compressed texture — has no type anything claims, and is read with
    ///         <see cref="OpenAsync" /> by whoever wants it. Preloading it is neither possible nor
    ///         wanted; what is wanted is that its bundle is mounted and its claim is held, and both of
    ///         those have already happened by the time this runs.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The claim holds a marker rather than nothing.</b> A null value would make the
    ///         claim look unloaded, so a second caller would try the same read again, and every load
    ///         of every model in a level would repeat the failure once per reference. The marker also
    ///         means that if something ever does resolve a reference to one of these, it gets a cast
    ///         failure naming <see cref="RawPayload" /> instead of a null nobody can trace.
    ///     </para>
    /// </remarks>
    Task<object> Preload(string address) =>
        Deserialise(
            address,
            id => database.TryReadObject(id, out var value) ? value : new RawPayload(address, id)
        );

    /// <summary>
    ///     Deserialises the address the caller asked for, with the type they asked for — which is
    ///     stricter than the header's, and catches asking for the right address as the wrong thing.
    /// </summary>
    Task<object> Deserialise<T>(string address) where T : class => Deserialise(address, id => database.Read<T>(id));

    /// <summary>
    ///     Deserialises an address once, however many callers arrive at the same moment. The work
    ///     happens outside the lock and the promise is made inside it, so nothing deserialises twice
    ///     and nothing blocks every other load while one chunk is being read.
    /// </summary>
    Task<object> Deserialise(string address, Func<ObjectId, object> read) {
        TaskCompletionSource<object>? mine = null;
        Task<object> value;

        lock (gate) {
            var claim = claims[address];

            if (claim.Value is not null) {
                return claim.Value;
            }

            mine = new(TaskCreationOptions.RunContinuationsAsynchronously);
            claim.Value = mine.Task;
            value = mine.Task;
        }

        try {
            // The resolver is in force only for this one read, and only on this thread. A reference
            // inside the chunk resolves to whatever is already loaded, which — because the closure
            // is walked dependency-first — is everything it can legitimately point at.
            using (ContentResolution.Push(new ClaimResolver(this))) {
                mine.SetResult(read(Catalog.Get(address).Id));
            }
        } catch (SerializationException failure) {
            // The database knows the chunk was written by a different type and says so with its
            // hash, which is the right message for a database and a useless one for whoever typed
            // the address. This is the only place that knows both.
            mine.SetException(
                new InvalidOperationException($"'{address}' could not be loaded: {failure.Message}", failure)
            );
        } catch (Exception failure) {
            // The claim keeps the faulted task, so a second caller gets the same failure rather than
            // retrying a read that is going to fail the same way.
            mine.SetException(failure);
        }

        return value;
    }

    /// <summary>
    ///     Answers a reference inside a chunk with the object the manager has already loaded for
    ///     that chunk id. Content addressing is what makes this a lookup rather than a search: the
    ///     id in the reference and the id in the catalog entry are the same number.
    /// </summary>
    sealed class ClaimResolver(AssetManager manager) : IContentResolver {
        public bool TryResolve(ObjectId id, out object? value) {
            lock (manager.gate) {
                foreach (var (address, claim) in manager.claims) {
                    if (claim.Value is { IsCompletedSuccessfully: true }
                        && manager.Catalog.TryGet(address, out var entry)
                        && entry.Id == id) {
                        value = claim.Value.Result;
                        return true;
                    }
                }
            }

            value = null;
            return false;
        }
    }

    sealed class Claim {
        public int Count;
        public Task<object>? Value;
    }
}

/// <summary>What a dependency turns out to be when nothing in this process can deserialise it.</summary>
/// <param name="Address">Where it is, so a message can name something a person recognises.</param>
/// <param name="Id">Which chunk, for a log that has to match a build.</param>
/// <remarks>
///     <para>
///         <b>Not a failure.</b> A content build ships plenty that was never a serialized object: a
///         compressed texture, an audio bitstream, a mesh's cluster hierarchy and page blob. Those are
///         read as bytes with <see cref="AssetManager.OpenAsync" />, and the only thing the closure
///         walk owes them is a mounted bundle and a held claim.
///     </para>
///     <para>
///         It is public so that the one way it can go wrong is legible: <c>Load&lt;T&gt;</c> on such an
///         address fails with a message naming this type, which says "you asked for an object and this
///         is bytes" rather than leaving somebody to work that out from a hash.
///     </para>
/// </remarks>
public sealed record RawPayload(string Address, ObjectId Id);
