// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core;

/// <summary>
///     Collects the things a subsystem owns so teardown is one call instead of a hand-maintained
///     sequence of <c>Dispose</c>s that drifts every time a field is added.
/// </summary>
/// <remarks>
///     <para>
///         Disposal runs in <b>reverse</b> order of registration, because construction order is
///         dependency order: whatever was built last is the thing that may still be using what was
///         built first.
///     </para>
///     <para>
///         Every entry is disposed even if one throws; the failures are collected and rethrown
///         together as an <see cref="AggregateException" />. A bag that gives up halfway leaks GPU
///         memory, and a leak on a shutdown path is the hardest kind to notice.
///     </para>
/// </remarks>
public sealed class DisposeBag : IDisposable, IAsyncDisposable {
    readonly Lock gate = new();
    readonly List<object> entries = [];

    bool disposed;

    /// <summary>How many entries are waiting to be disposed.</summary>
    public int Count {
        get {
            lock (gate) {
                return entries.Count;
            }
        }
    }

    /// <summary>Whether the bag has already been disposed.</summary>
    public bool IsDisposed {
        get {
            lock (gate) {
                return disposed;
            }
        }
    }

    /// <summary>
    ///     Takes ownership of <paramref name="disposable" /> and hands it straight back, so it can be
    ///     wrapped around a construction expression: <c>var device = bag.Add(new Device(…));</c>
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="disposable">The resource to own.</param>
    /// <returns><paramref name="disposable" />.</returns>
    /// <remarks>
    ///     Adding to a bag that is already disposed disposes the resource immediately rather than
    ///     throwing. Teardown races are real, and the alternative is a leak plus an exception on a
    ///     path that is already going wrong.
    /// </remarks>
    public T Add<T>(T disposable) where T : IDisposable {
        ArgumentNullException.ThrowIfNull(disposable);

        lock (gate) {
            if (!disposed) {
                entries.Add(disposable);
                return disposable;
            }
        }

        disposable.Dispose();
        return disposable;
    }

    /// <summary>Takes ownership of an asynchronously disposable resource.</summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="disposable">The resource to own.</param>
    /// <returns><paramref name="disposable" />.</returns>
    /// <remarks>
    ///     A resource added here is disposed asynchronously by <see cref="DisposeAsync" />. If the
    ///     bag is torn down through the synchronous <see cref="Dispose" /> instead, it is disposed
    ///     by blocking on the returned task — so keep async-only resources out of bags that
    ///     synchronous code owns.
    /// </remarks>
    public T AddAsync<T>(T disposable) where T : IAsyncDisposable {
        ArgumentNullException.ThrowIfNull(disposable);

        lock (gate) {
            if (!disposed) {
                entries.Add(disposable);
                return disposable;
            }
        }

        disposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return disposable;
    }

    /// <summary>Registers a callback to run at teardown, in reverse registration order.</summary>
    /// <param name="onDispose">The callback.</param>
    public void Add(Action onDispose) {
        ArgumentNullException.ThrowIfNull(onDispose);
        Add(new CallbackDisposable(onDispose));
    }

    /// <inheritdoc />
    public void Dispose() {
        var pending = Take();
        if (pending is null) {
            return;
        }

        List<Exception>? failures = null;
        for (var i = pending.Count - 1; i >= 0; i--) {
            try {
                switch (pending[i]) {
                    case IDisposable sync:
                        sync.Dispose();
                        break;
                    case IAsyncDisposable async:
                        async.DisposeAsync().AsTask().GetAwaiter().GetResult();
                        break;
                }
            } catch (Exception exception) {
                (failures ??= []).Add(exception);
            }
        }

        Throw(failures);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        var pending = Take();
        if (pending is null) {
            return;
        }

        List<Exception>? failures = null;
        for (var i = pending.Count - 1; i >= 0; i--) {
            try {
                switch (pending[i]) {
                    case IAsyncDisposable async:
                        await async.DisposeAsync().ConfigureAwait(false);
                        break;
                    case IDisposable sync:
                        sync.Dispose();
                        break;
                }
            } catch (Exception exception) {
                (failures ??= []).Add(exception);
            }
        }

        Throw(failures);
    }

    // Empties the bag under the lock and hands the entries to the caller, so a second Dispose on
    // another thread finds nothing rather than disposing everything twice.
    List<object>? Take() {
        lock (gate) {
            if (disposed) {
                return null;
            }

            disposed = true;
            if (entries.Count == 0) {
                return null;
            }

            var pending = new List<object>(entries);
            entries.Clear();
            return pending;
        }
    }

    static void Throw(List<Exception>? failures) {
        if (failures is { Count: > 0 }) {
            throw new AggregateException("One or more resources failed to dispose.", failures);
        }
    }

    sealed class CallbackDisposable(Action callback) : IDisposable {
        public void Dispose() => callback();
    }
}
