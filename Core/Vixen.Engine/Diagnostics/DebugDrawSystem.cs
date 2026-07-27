// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Threading;
using Vixen.Ecs.Systems;

namespace Vixen.Engine.Diagnostics;

/// <summary>Ages the debug geometry once a frame, after everything that might draw it has.</summary>
/// <remarks>
///     In <see cref="SystemPhase.PostRender" /> and nowhere else: a line asked for during a frame has
///     to survive until a renderer has had the chance to drain it, so ageing has to come after the
///     draining and not before.
/// </remarks>
/// <param name="draw">The accumulator to age.</param>
[UpdateInGroup(SystemPhase.PostRender)]
public sealed class DebugDrawSystem(DebugDraw draw) : SystemBase, IDeclaredAccess {
    /// <inheritdoc />
    public SystemAccess Access => SystemAccess.None;

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        draw.Advance(context.Time.DeltaSeconds);
        return dependency;
    }
}
