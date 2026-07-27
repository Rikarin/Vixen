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
///     Declares no access, and so conflicts with every other system in its phase. That is correct
///     rather than lazy: a behaviour is arbitrary user code and may touch anything, and the
///     scheduler's only honest reading of that is "assume everything". A game that wants its
///     behaviour work to overlap with a system writes an <see cref="ISystem" /> instead, which is
///     the trade doc 04 states in as many words.
/// </remarks>
/// <param name="store">The store to walk.</param>
[UpdateInGroup(SystemPhase.Update)]
public sealed class BehaviorUpdateSystem(BehaviorStore store) : SystemBase {
    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        store.Time = context.Time;
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
        store.RunLateUpdate();
        return dependency;
    }
}
