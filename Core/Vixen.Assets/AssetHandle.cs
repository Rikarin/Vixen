// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Vixen.Assets;

/// <summary>Where a load has got to.</summary>
public enum AssetStatus {
    /// <summary>Still going.</summary>
    Loading,

    /// <summary>Done, and <see cref="AssetHandle{T}.Result" /> is there.</summary>
    Loaded,

    /// <summary>It did not load, and asking for the result rethrows why.</summary>
    Failed,

    /// <summary>The caller released it. The value may already be gone.</summary>
    Released
}

/// <summary>A claim on a loaded asset.</summary>
/// <typeparam name="T">What was loaded.</typeparam>
/// <remarks>
///     <para>
///         <b>A handle is a claim, not a reference.</b> Holding one keeps the asset — and everything
///         it depends on — in memory; releasing it gives that claim up. The asset itself is unloaded
///         when the last claim goes, which is what lets two scenes share a texture without either of
///         them knowing about the other.
///     </para>
///     <para>
///         <b>Releasing twice throws.</b> It is tempting to make it a no-op, and that is exactly what
///         turns a double release into someone else's asset being unloaded: the second call decrements
///         a count that a different holder is relying on, and the failure surfaces much later as a
///         disposed object nobody released. Unity's addressables are a documented source of this
///         class of bug. Throwing at the second call names it where it happened.
///     </para>
/// </remarks>
public sealed class AssetHandle<T> where T : class {
    readonly AssetManager manager;
    readonly Task<T> load;
    readonly ImmutableArray<string> acquired;
    int released;

    /// <summary>What was asked for.</summary>
    public string Address { get; }

    /// <summary>Every address this handle is holding a claim on — the asset and its dependencies.</summary>
    public ImmutableArray<string> Acquired => acquired;

    /// <summary>Where the load has got to.</summary>
    public AssetStatus Status =>
        Volatile.Read(ref released) != 0 ? AssetStatus.Released
        : !load.IsCompleted ? AssetStatus.Loading
        : load.IsCompletedSuccessfully ? AssetStatus.Loaded
        : AssetStatus.Failed;

    /// <summary>What was loaded.</summary>
    /// <exception cref="InvalidOperationException">It is still loading, or has been released.</exception>
    public T Result {
        get {
            if (Volatile.Read(ref released) != 0) {
                throw new InvalidOperationException(
                    $"'{Address}' was released, so this handle no longer has a claim on it. Read the result before "
                    + "releasing, or hold a second handle."
                );
            }

            if (!load.IsCompleted) {
                throw new InvalidOperationException(
                    $"'{Address}' is still loading. Await the handle, or use AssetManager.Load for the blocking form."
                );
            }

            // A faulted task rethrows here with its original stack, which is the whole point of
            // keeping the failure on the task rather than swallowing it into a status flag.
            return load.GetAwaiter().GetResult();
        }
    }

    internal AssetHandle(AssetManager manager, string address, ImmutableArray<string> acquired, Task<T> load) {
        this.manager = manager;
        this.load = load;
        this.acquired = acquired;
        Address = address;
    }

    /// <summary>The load, as a task.</summary>
    /// <remarks>
    ///     For the times a caller needs a <see cref="Task" /> rather than an awaitable — combining
    ///     several loads with <see cref="Task.WhenAll(Task[])" />, or attaching a timeout. Awaiting
    ///     the handle directly is the shorter way to say the same thing.
    /// </remarks>
    public Task<T> Completion => load;

    /// <summary>Makes the handle awaitable, so <c>await handle</c> gives the asset.</summary>
    /// <returns>The awaiter.</returns>
    public TaskAwaiter<T> GetAwaiter() => load.GetAwaiter();

    /// <summary>Gives up this handle's claim.</summary>
    /// <exception cref="InvalidOperationException">It was already released.</exception>
    public void Release() {
        if (Interlocked.Exchange(ref released, 1) != 0) {
            throw new InvalidOperationException(
                $"This handle on '{Address}' was already released. Releasing twice decrements a count some other "
                + "holder is relying on, and the asset it unloads is theirs rather than yours."
            );
        }

        manager.ReleaseAll(acquired);
    }
}

/// <summary>Everything loaded through it, released together.</summary>
/// <remarks>
///     <para>
///         <b>Loads go through the scope rather than being captured from ambient state.</b> Doc 08
///         sketches this as <c>using var scope = Assets.Scope();</c> with everything loaded inside
///         attaching itself, and that reads beautifully until the first <c>await</c>: a load started
///         inside the block and finishing after it has to belong somewhere, and neither answer is
///         right. Ambient state and asynchrony do not compose.
///     </para>
///     <para>
///         Since the scope exists <i>because</i> implicit release semantics leak, replacing one
///         implicit-lifetime rule with another would defeat the point. Being handed the scope you are
///         loading through is one extra word and it is never ambiguous.
///     </para>
/// </remarks>
public sealed class AssetScope : IDisposable {
    readonly AssetManager manager;
    readonly List<Action> releases = [];
    readonly Lock gate = new();
    bool disposed;

    internal AssetScope(AssetManager manager) => this.manager = manager;

    /// <summary>How many handles the scope is holding.</summary>
    public int Count {
        get {
            lock (gate) {
                return releases.Count;
            }
        }
    }

    /// <summary>Loads an asset, and holds it until the scope ends.</summary>
    /// <typeparam name="T">What to load it as.</typeparam>
    /// <param name="address">The address.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>The handle, which the scope will release.</returns>
    public AssetHandle<T> LoadAsync<T>(string address, CancellationToken cancellationToken = default)
        where T : class {
        ObjectDisposedException.ThrowIf(disposed, this);

        var handle = manager.LoadAsync<T>(address, cancellationToken);

        lock (gate) {
            releases.Add(handle.Release);
        }

        return handle;
    }

    /// <summary>Releases everything loaded through this scope.</summary>
    public void Dispose() {
        lock (gate) {
            if (disposed) {
                return;
            }

            disposed = true;

            // Last acquired, first released: it costs nothing and it means a dependency outlives the
            // thing that needed it, which is the order anything watching would expect to see.
            for (var index = releases.Count - 1; index >= 0; index--) {
                releases[index]();
            }

            releases.Clear();
        }
    }
}
