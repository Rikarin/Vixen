// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Reactive;

/// <summary>Where an asynchronous derivation has got to.</summary>
public enum AsyncStatus {
    /// <summary>The work is running. A value may be present from a previous run.</summary>
    Loading,

    /// <summary>The work finished and <see cref="AsyncValue{T}.Value" /> is its result.</summary>
    Success,

    /// <summary>The work threw and <see cref="AsyncValue{T}.Error" /> says why.</summary>
    Failure
}

/// <summary>The three states of an asynchronous derivation, as one value.</summary>
/// <typeparam name="T">The result type.</typeparam>
/// <param name="Status">Where the work has got to.</param>
/// <param name="Value">The result, if there is one. Kept across a reload so a panel does not blank.</param>
/// <param name="Error">Why it failed, if it did.</param>
/// <remarks>
///     One value rather than three signals, because the three are not independent and a UI that binds
///     them separately can render a spinner over a stale value over an error message. Binding one
///     thing makes the exclusive cases exclusive.
/// </remarks>
public readonly record struct AsyncValue<T>(AsyncStatus Status, T? Value = default, Exception? Error = null) {
    /// <summary>Whether the work is running.</summary>
    public bool IsLoading => Status == AsyncStatus.Loading;

    /// <summary>Whether a result is present, even if a reload is in flight.</summary>
    public bool HasValue => Status == AsyncStatus.Success || (Status == AsyncStatus.Loading && Value is not null);
}

/// <summary>A value derived asynchronously, re-derived when what it was asked for changes.</summary>
/// <typeparam name="TRequest">What the work is asked for — the tracked part.</typeparam>
/// <typeparam name="T">The result type.</typeparam>
/// <remarks>
///     <para>
///         Split in two on purpose. The <i>request</i> is a synchronous function of signals, and it
///         is what dependency tracking sees; the <i>load</i> is asynchronous and is tracked by
///         nothing. That split is not decoration — tracking stops at the first <c>await</c>, because
///         the ambient consumer is thread-local and the continuation is on another thread, so an
///         async function that read signals after awaiting would silently record half its
///         dependencies. Making the tracked half separate and synchronous means the compiler
///         enforces what the graph can actually observe.
///     </para>
///     <para>
///         A new request cancels the one in flight. Results are handed back through
///         <see cref="EffectScheduler.Post" />, so they are applied on the owning thread at a defined
///         point in the frame — the same guarantee everything else in this assembly makes, and the
///         reason nothing here needs a lock.
///     </para>
/// </remarks>
public sealed class AsyncComputed<TRequest, T> : IReadOnlySignal<AsyncValue<T>>, IDisposable {
    readonly Signal<AsyncValue<T>> state;
    readonly Effect trigger;
    CancellationTokenSource? inFlight;
    long generation;

    /// <summary>Creates an asynchronous derivation and queues its first run.</summary>
    /// <param name="request">
    ///     What to ask for, as a function of signals. Runs with tracking on; a change to anything it
    ///     read starts the work again.
    /// </param>
    /// <param name="load">The work. Runs untracked, off the owning thread, with a cancellation token.</param>
    /// <param name="scheduler">Where it queues and where results come back. Defaults to the thread's.</param>
    public AsyncComputed(
        Func<TRequest> request,
        Func<TRequest, CancellationToken, Task<T>> load,
        EffectScheduler? scheduler = null
    ) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(load);

        var owner = scheduler ?? EffectScheduler.Default;
        state = new Signal<AsyncValue<T>>(new AsyncValue<T>(AsyncStatus.Loading));
        trigger = new Effect(() => Start(request(), load, owner), owner);
    }

    /// <summary>The current state of the derivation.</summary>
    public AsyncValue<T> Value => state.Value;

    /// <summary>Whether <see cref="Dispose" /> has been called.</summary>
    public bool IsDisposed { get; private set; }

    /// <inheritdoc />
    public AsyncValue<T> Peek() => state.Peek();

    /// <summary>Cancels anything in flight and stops re-deriving.</summary>
    public void Dispose() {
        if (IsDisposed) {
            return;
        }

        IsDisposed = true;
        trigger.Dispose();
        inFlight?.Cancel();
        inFlight?.Dispose();
        inFlight = null;
    }

    void Start(TRequest request, Func<TRequest, CancellationToken, Task<T>> load, EffectScheduler owner) {
        // Cancelled and dropped rather than disposed: the overtaken task still holds the token,
        // and disposing it out from under a running operation is how that turns into an
        // ObjectDisposedException from somewhere unrelated. The one still in flight is disposed by
        // Dispose, and a cancelled source with no timer registered costs nothing to let go of.
        inFlight?.Cancel();

        var cancellation = new CancellationTokenSource();
        inFlight = cancellation;

        // Every run is stamped, and a result whose stamp is not the current one is dropped. The
        // token alone is not enough: a task that has already produced its value cannot be cancelled,
        // and without this an overtaken request would still be able to publish a stale answer.
        var stamp = ++generation;

        // Keeping whatever value is already there is what stops a panel blanking on every keystroke
        // of a search box.
        var previous = state.Peek();
        state.Value = new AsyncValue<T>(AsyncStatus.Loading, previous.Value);

        Task<T> work;
        try {
            work = load(request, cancellation.Token);
        } catch (Exception exception) {
            Publish(owner, stamp, new AsyncValue<T>(AsyncStatus.Failure, previous.Value, exception));
            return;
        }

        _ = work.ContinueWith(
            completed => {
                var next = completed.Status switch {
                    TaskStatus.RanToCompletion => new AsyncValue<T>(AsyncStatus.Success, completed.Result),
                    TaskStatus.Canceled => (AsyncValue<T>?) null,
                    _ => new AsyncValue<T>(
                        AsyncStatus.Failure,
                        previous.Value,
                        completed.Exception?.InnerException ?? completed.Exception
                    )
                };

                if (next is not null) {
                    Publish(owner, stamp, next.Value);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    void Publish(EffectScheduler owner, long stamp, AsyncValue<T> next) => owner.Post(() => {
            if (IsDisposed || stamp != generation) {
                return;
            }

            state.Value = next;
        }
    );
}
