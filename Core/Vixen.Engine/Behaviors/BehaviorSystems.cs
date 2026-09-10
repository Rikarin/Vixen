// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Threading;
using Vixen.Ecs.Systems;

namespace Vixen.Engine.Behaviors;

/// <summary>Drains the lifecycle queues, once a frame, before anything else runs.</summary>
/// <remarks>
///     In <see cref="SystemPhase.EarlyUpdate" /> so that a behaviour attached during the previous
///     frame has had <c>Awake</c> and <c>OnEnable</c> before any system this frame can observe it —
///     including the transform pass, which is what makes "spawn in Update, positioned by PreRender"
///     work.
/// </remarks>
/// <param name="store">The store to drain.</param>
[UpdateInGroup(SystemPhase.EarlyUpdate)]
public sealed class BehaviorLifecycleSystem(BehaviorStore store) : SystemBase {
    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        store.Time = context.Time;
        store.RunLifecycle();
        return dependency;
    }
}

/// <summary>Calls <c>Update</c> on every enabled, started behaviour.</summary>
/// <remarks>
///     <para>
///         Declares no access, and so conflicts with every other system in its phase. That is correct
///         rather than lazy: a behaviour is arbitrary user code and may touch anything, and the
///         scheduler's only honest reading of that is "assume everything". A game that wants its
///         behaviour work to overlap with a system writes an <see cref="ISystem" /> instead, which is
///         the trade doc 04 states in as many words.
///     </para>
///     <para>
///         ⚠ <b>Which is why it hands the scheduler over and still returns <c>dependency</c>.</b>
///         A <see cref="BehaviorJobAttribute" /> batch is dispatched and completed inside
///         <c>RunUpdate</c>, at a sync point where this system conflicts with everything and nothing
///         else is running. That parallelises the indices of one bucket, which is what doc 04 § Making
///         it fast item 3 asks for; returning a handle instead would be the other question — a
///         behaviour's work overlapping a system's — and that needs an access declaration behaviours
///         do not have.
///     </para>
/// </remarks>
/// <param name="store">The store to walk.</param>
[UpdateInGroup(SystemPhase.Update)]
public sealed class BehaviorUpdateSystem(BehaviorStore store) : SystemBase {
    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        store.Time = context.Time;
        store.Jobs = context.Jobs;
        store.RunUpdate();
        return dependency;
    }
}

/// <summary>Calls <c>LateUpdate</c> on every enabled, started behaviour.</summary>
/// <param name="store">The store to walk.</param>
[UpdateInGroup(SystemPhase.LateUpdate)]
public sealed class BehaviorLateUpdateSystem(BehaviorStore store) : SystemBase {
    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        store.Time = context.Time;
        store.Jobs = context.Jobs;
        store.RunLateUpdate();
        return dependency;
    }
}
