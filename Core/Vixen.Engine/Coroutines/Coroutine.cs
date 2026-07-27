// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Vixen.Engine.Coroutines;

/// <summary>
///     What an <c>async</c> gameplay routine returns: a unit of work that lives across frames.
/// </summary>
/// <remarks>
///     <para>
///         <code>
///     async Coroutine Fade() {
///         await Seconds(0.5f);
///         while (Alpha &gt; 0f) {
///             Alpha -= Time.DeltaSeconds;
///             await NextFrame();
///         }
///     }
///     </code>
///         and <c>Run(Fade())</c> to start it.
///     </para>
///     <para>
///         <b>Why a type of its own rather than <see cref="ValueTask" />.</b> Two reasons, and neither
///         is decoration. The first is the builder: <see cref="CoroutineMethodBuilder" /> is attached
///         here, so every <c>async Coroutine</c> method gets a pooled state machine without the
///         author writing an attribute on each one. The second is that a coroutine is a thing you
///         start and forget, and <c>ValueTask</c>'s surface — <c>.Result</c>, <c>.AsTask()</c>,
///         <c>.GetAwaiter().GetResult()</c> — is a set of ways to block the loop thread. None of them
///         are here.
///     </para>
///     <para>
///         <b>A coroutine is consumed exactly once.</b> Either <c>await</c> it or hand it to
///         <c>Run</c>, never both, and never twice. The state machine behind it goes back to a pool
///         the moment its result is read, and reading a second time reads whatever took its place.
///         This is <see cref="ValueTask" />'s rule and UniTask's rule; it is inherent to pooling
///         rather than a choice any of the three made.
///     </para>
/// </remarks>
[AsyncMethodBuilder(typeof(CoroutineMethodBuilder))]
public readonly struct Coroutine {
    readonly ValueTask task;

    internal Coroutine(ValueTask task) => this.task = task;

    /// <summary>A coroutine that has already finished.</summary>
    public static Coroutine Completed => default;

    /// <summary>Makes this awaitable.</summary>
    /// <returns>The awaiter.</returns>
    /// <remarks>
    ///     Configured not to capture a synchronisation context. There is none on the loop thread in a
    ///     game, but there is one under a test runner, and a coroutine that resumed on the runner's
    ///     context would leave the loop thread and take the determinism of everything downstream with
    ///     it.
    /// </remarks>
    public ConfiguredValueTaskAwaitable.ConfiguredValueTaskAwaiter GetAwaiter() =>
        task.ConfigureAwait(false).GetAwaiter();

    /// <summary>Finishes when every one of them has finished.</summary>
    /// <param name="coroutines">The coroutines, already started.</param>
    /// <returns>A coroutine that completes when the last of them does.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="coroutines" /> is <see langword="null" />.</exception>
    /// <remarks>
    ///     Awaits them in turn, which is a real <c>WhenAll</c> here rather than an approximation of
    ///     one: an argument was started before it was passed in, so all of them are already running,
    ///     and waiting for each in order returns exactly when the last of them is done. The one
    ///     difference from <see cref="Task.WhenAll(Task[])" /> is that a failure is not observed the
    ///     instant it happens — every coroutine is still awaited, and the first exception is rethrown
    ///     at the end, so nothing is left unobserved.
    /// </remarks>
    public static async Coroutine WhenAll(params Coroutine[] coroutines) {
        ArgumentNullException.ThrowIfNull(coroutines);

        Exception? first = null;

        foreach (var coroutine in coroutines) {
            try {
                await coroutine;
            } catch (Exception failure) {
                first ??= failure;
            }
        }

        if (first is not null) {
            ExceptionDispatchInfo.Capture(first).Throw();
        }
    }

    internal ValueTask AsValueTask() => task;
}

/// <summary>Builds the state machine behind an <c>async Coroutine</c> method.</summary>
/// <remarks>
///     <para>
///         Every member forwards to <see cref="PoolingAsyncValueTaskMethodBuilder" />, which is the
///         whole point of the type existing. That builder rents the state machine box from a pool and
///         returns it when the result is read, so a coroutine started every frame for an hour
///         allocates once. Doing this by hand — a pool, an <c>IValueTaskSource</c>, a version token —
///         is what UniTask had to write, because Unity's runtime had no such builder. .NET has one,
///         and the honest amount of work here is to forward to it.
///     </para>
///     <para>
///         It exists at all only so that <see cref="Coroutine" /> can be the return type. A builder's
///         <c>Task</c> property has to have the method's return type, so a wrapper type needs a
///         wrapper builder; there is no way to say "use the pooling builder, but call the result
///         something else".
///     </para>
/// </remarks>
public struct CoroutineMethodBuilder {
    PoolingAsyncValueTaskMethodBuilder inner;

    /// <summary>The coroutine the method returns.</summary>
    public Coroutine Task => new(inner.Task);

    /// <summary>Creates a builder.</summary>
    /// <returns>The builder.</returns>
    public static CoroutineMethodBuilder Create() => new() { inner = PoolingAsyncValueTaskMethodBuilder.Create() };

    /// <summary>Runs the state machine up to its first suspension.</summary>
    /// <typeparam name="TStateMachine">The state machine type.</typeparam>
    /// <param name="stateMachine">The state machine.</param>
    public void Start<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine =>
        inner.Start(ref stateMachine);

    /// <summary>Associates a boxed state machine with this builder.</summary>
    /// <param name="stateMachine">The state machine.</param>
    public void SetStateMachine(IAsyncStateMachine stateMachine) => inner.SetStateMachine(stateMachine);

    /// <summary>Completes the coroutine.</summary>
    public void SetResult() => inner.SetResult();

    /// <summary>Faults the coroutine.</summary>
    /// <param name="exception">What it threw.</param>
    public void SetException(Exception exception) => inner.SetException(exception);

    /// <summary>Schedules the state machine to continue when an awaiter completes.</summary>
    /// <typeparam name="TAwaiter">The awaiter type.</typeparam>
    /// <typeparam name="TStateMachine">The state machine type.</typeparam>
    /// <param name="awaiter">The awaiter.</param>
    /// <param name="stateMachine">The state machine.</param>
    public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion
        where TStateMachine : IAsyncStateMachine =>
        inner.AwaitOnCompleted(ref awaiter, ref stateMachine);

    /// <summary>Schedules the state machine to continue, without flowing the execution context.</summary>
    /// <typeparam name="TAwaiter">The awaiter type.</typeparam>
    /// <typeparam name="TStateMachine">The state machine type.</typeparam>
    /// <param name="awaiter">The awaiter.</param>
    /// <param name="stateMachine">The state machine.</param>
    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion
        where TStateMachine : IAsyncStateMachine =>
        inner.AwaitUnsafeOnCompleted(ref awaiter, ref stateMachine);
}
