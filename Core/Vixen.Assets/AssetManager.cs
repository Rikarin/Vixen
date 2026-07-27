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
///         <b>What a claim on a dependency is, and is not.</b> Claiming an address mounts the bundle
///         it lives in and ties its lifetime to its dependents; it does not deserialise it. Turning a
///         reference <i>inside</i> a chunk into the loaded object it points at is the content-reference
///         machinery, which [03](../../docs/plan/03-core-foundation.md) deferred out of Phase 1 and
///         which is the next piece here. Until it lands, a dependency's bundle and its lifetime are
///         shared; its deserialised object is shared only if something loads it by address.
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
    ///     Deserialises an address once, however many callers arrive at the same moment. The work
    ///     happens outside the lock and the promise is made inside it, so nothing deserialises twice
    ///     and nothing blocks every other load while one chunk is being read.
    /// </summary>
    Task<object> Deserialise<T>(string address) where T : class {
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
            mine.SetResult(database.Read<T>(Catalog.Get(address).Id));
        } catch (SerializationException failure) {
            // The database knows the chunk was written by a different type and says so with its
            // hash, which is the right message for a database and a useless one for whoever typed
            // the address. This is the only place that knows both.
            mine.SetException(
                new InvalidOperationException(
                    $"'{address}' could not be loaded as {typeof(T).Name}: {failure.Message}",
                    failure
                )
            );
        } catch (Exception failure) {
            // The claim keeps the faulted task, so a second caller gets the same failure rather than
            // retrying a read that is going to fail the same way.
            mine.SetException(failure);
        }

        return value;
    }

    sealed class Claim {
        public int Count;
        public Task<object>? Value;
    }
}
