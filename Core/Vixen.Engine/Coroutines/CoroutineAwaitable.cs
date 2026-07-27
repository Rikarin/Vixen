// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;

namespace Vixen.Engine.Coroutines;

/// <summary>What kind of question a suspended coroutine is waiting on the answer to.</summary>
enum WaitKind : byte {
    /// <summary>Nothing but the next occurrence of the resume point.</summary>
    Tick,

    /// <summary>An amount of scaled game time.</summary>
    ScaledTime,

    /// <summary>An amount of unscaled time, which a pause does not stop.</summary>
    UnscaledTime,

    /// <summary>A predicate becoming true.</summary>
    Until,

    /// <summary>A predicate becoming false.</summary>
    While,

    /// <summary>Nothing at all: resume at the very next drain of the point, this frame if possible.</summary>
    Immediate
}

/// <summary>
///     One suspension of a coroutine — everything <c>await NextFrame()</c> and its siblings produce.
/// </summary>
/// <remarks>
///     <para>
///         Its own awaiter, which is why <see cref="GetAwaiter" /> returns itself. A separate awaiter
///         type would be a second struct copied into the state machine for no gain; the compiler is
///         happy with one as long as it has both halves.
///     </para>
///     <para>
///         <b><see cref="IsCompleted" /> is always <see langword="false" />.</b> A coroutine never
///         resumes inside the frame it suspended in, even for <c>await Seconds(0f)</c> — a zero wait
///         that completed synchronously would turn <c>while (true) await Seconds(0f);</c> into a hang
///         rather than a loop, and users write that. The single exception is
///         <see cref="WaitKind.Immediate" />, which still suspends here and is merely made ready by
///         the very next drain.
///     </para>
/// </remarks>
public readonly struct CoroutineAwaitable : ICriticalNotifyCompletion, IEquatable<CoroutineAwaitable> {
    readonly CoroutineScheduler scheduler;
    readonly ICoroutineOwner? owner;
    readonly int generation;
    readonly ResumePoint point;
    readonly WaitKind kind;
    readonly long delay;
    readonly Func<bool>? predicate;

    internal CoroutineAwaitable(
        CoroutineScheduler scheduler,
        ICoroutineOwner? owner,
        ResumePoint point,
        WaitKind kind,
        long delay,
        Func<bool>? predicate
    ) {
        this.scheduler = scheduler;
        this.owner = owner;
        this.point = point;
        this.kind = kind;
        this.delay = delay;
        this.predicate = predicate;

        // Recorded here, at the moment the wait is expressed, and not when it is resumed. That is
        // what makes StopCoroutines reach a coroutine suspended several levels down: everything
        // waiting when the call is made was created before it, so everything waiting cancels.
        generation = owner?.CoroutineGeneration ?? 0;
    }

    /// <summary>Makes this awaitable.</summary>
    /// <returns>Itself.</returns>
    public CoroutineAwaitable GetAwaiter() => this;

    /// <summary>Always <see langword="false" />: awaiting one of these always yields.</summary>
    public bool IsCompleted => false;

    /// <inheritdoc />
    public void OnCompleted(Action continuation) => UnsafeOnCompleted(continuation);

    /// <inheritdoc />
    public void UnsafeOnCompleted(Action continuation) =>
        scheduler.Suspend(continuation, owner, generation, point, kind, delay, predicate);

    /// <summary>Resumes the coroutine, or cancels it.</summary>
    /// <exception cref="OperationCanceledException">
    ///     The owner was destroyed, or its coroutines were stopped, while this was waiting.
    /// </exception>
    /// <remarks>
    ///     Cancelling by throwing rather than by never resuming, because a coroutine holds resources
    ///     in <c>using</c> and <c>finally</c> blocks and abandoning its state machine would run
    ///     neither. The cost is an exception per cancelled coroutine when a behaviour is destroyed,
    ///     which is the right trade for cleanup that actually happens.
    /// </remarks>
    public void GetResult() {
        if (scheduler.IsResumingCancelled) {
            throw new OperationCanceledException("The coroutine's owner was destroyed or stopped its coroutines.");
        }
    }

    /// <inheritdoc />
    public bool Equals(CoroutineAwaitable other) =>
        ReferenceEquals(scheduler, other.scheduler)
        && ReferenceEquals(owner, other.owner)
        && generation == other.generation
        && point == other.point
        && kind == other.kind
        && delay == other.delay
        && ReferenceEquals(predicate, other.predicate);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is CoroutineAwaitable other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(scheduler, owner, generation, point, kind, delay, predicate);

    /// <summary>Compares two waits.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they are the same wait.</returns>
    public static bool operator ==(CoroutineAwaitable left, CoroutineAwaitable right) => left.Equals(right);

    /// <summary>Compares two waits.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they differ.</returns>
    public static bool operator !=(CoroutineAwaitable left, CoroutineAwaitable right) => !left.Equals(right);
}
