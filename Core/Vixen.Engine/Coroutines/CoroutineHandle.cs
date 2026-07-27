// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Engine.Coroutines;

/// <summary>A reference to a running coroutine, valid only while it runs.</summary>
/// <remarks>
///     <para>
///         An index and a generation, the same shape as <c>JobHandle</c> and <c>Entity</c>, and for
///         the same reason: the slot behind it goes back on the free list the moment the coroutine
///         finishes, so a handle has to be able to tell "still running" from "finished, and something
///         else is here now". The generation is what tells them apart.
///     </para>
///     <para>
///         There is no <c>Cancel</c> on it. See <see cref="ICoroutineOwner" /> for why cancellation
///         is the owner's to do.
///     </para>
/// </remarks>
public readonly record struct CoroutineHandle {
    readonly CoroutineScheduler? scheduler;
    readonly int index;
    readonly int version;

    internal CoroutineHandle(CoroutineScheduler scheduler, int index, int version) {
        this.scheduler = scheduler;
        this.index = index;
        this.version = version;
    }

    /// <summary>Whether the coroutine is still going.</summary>
    /// <remarks>
    ///     <see langword="false" /> for a default handle, and for one whose coroutine has finished,
    ///     faulted or cancelled. Those are not distinguished: a coroutine's result is not something
    ///     anyone can hold on to, because the slot it was in has been reused by then.
    /// </remarks>
    public bool IsRunning => scheduler?.IsRunning(index, version) ?? false;
}
