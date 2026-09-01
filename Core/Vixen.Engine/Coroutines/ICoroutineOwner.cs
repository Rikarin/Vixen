// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Engine.Coroutines;

/// <summary>
///     Whatever a coroutine belongs to, and therefore whatever can cancel it.
/// </summary>
/// <remarks>
///     <para>
///         An interface rather than <c>Behavior</c> outright, so that a system, an editor tool or a
///         test can own coroutines without inventing a behaviour to hang them on. <c>Behavior</c>
///         implements it, and that is the only implementation most code will ever see.
///     </para>
///     <para>
///         <b>Cancellation is per-owner, not per-coroutine.</b> A launched coroutine and a coroutine
///         it awaits are indistinguishable once they are suspended — the second one's continuation is
///         held by the first one's state machine, not by the scheduler — so a handle cannot cancel
///         "its own" waits without also being able to name every wait beneath them. Rather than offer
///         a <c>Cancel</c> that quietly misses the nested half, the unit of cancellation is the
///         owner, which reaches all of it.
///     </para>
///     <para>
///         ⚠ <b>These two members say a coroutine <i>should</i> stop; they do not make it let go.</b>
///         Both are read at a resume point, so an owner that goes away between drains leaves its
///         continuations sitting in the scheduler's waiting lists until the next one. That is fine
///         for a game and fatal for an editor, where a detach and an assembly unload happen inside
///         one call with no frame in between — see <see cref="CoroutineScheduler.Cancel" />, which
///         is what an owner calls to be let go of rather than merely marked.
///     </para>
/// </remarks>
public interface ICoroutineOwner {
    /// <summary>Whether the owner is gone. Its coroutines cancel at their next resume point.</summary>
    bool IsDestroyed { get; }

    /// <summary>
    ///     Bumped by <c>StopCoroutines</c>. An awaitable records the value it was created under and
    ///     cancels if it no longer matches.
    /// </summary>
    /// <remarks>
    ///     A counter rather than a flag, because stopping has to be a thing that happens rather than
    ///     a state that persists: a coroutine started after the stop must not be cancelled by it. A
    ///     counter separates the two without anything having to be reset.
    /// </remarks>
    int CoroutineGeneration { get; }
}
