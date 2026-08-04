// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ai;

/// <summary>A registration, so an observer can be taken off again.</summary>
/// <param name="Index">Its slot in the blackboard's observer table.</param>
/// <param name="Generation">Which registration that slot is holding.</param>
/// <remarks>
///     Generational, for the reason every handle in this engine is: a decorator that unregisters
///     twice — which is what a double abort looks like — must not take somebody else's registration
///     off with it.
/// </remarks>
public readonly record struct BlackboardObserverHandle(int Index, uint Generation) {
    /// <summary>The registration that is not one.</summary>
    public static BlackboardObserverHandle Null => new(-1, 0);

    /// <summary>Whether this names a registration.</summary>
    public bool IsNull => Index < 0;
}

/// <summary>Something that wants to know when a key changes.</summary>
/// <remarks>
///     <para>
///         This is what makes an event-driven behaviour tree possible: a decorator with an observer
///         registers on the keys it reads, and a write to one of them re-evaluates it. A tree whose
///         world has not changed does nothing at all.
///     </para>
///     <para>
///         ⚠ <b>An observer is told, it does not act.</b> A notification arrives during somebody
///         else's write, which may well be the running task writing its own result — so the
///         correct response is to enqueue work, never to abort, re-enter or tick anything. The
///         stepper services what was enqueued at the top of the next step, when nothing is
///         part-way. docs/plan/37 § D6 is the argument, and the one-frame latency it costs is
///         stated in the guide rather than hidden.
///     </para>
/// </remarks>
public interface IBlackboardObserver {
    /// <summary>The key changed.</summary>
    /// <param name="blackboard">The blackboard it changed on.</param>
    /// <param name="key">Which key.</param>
    void OnBlackboardChanged(Blackboard blackboard, BlackboardKey key);
}
