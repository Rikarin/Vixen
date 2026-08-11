// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Threading;
using Vixen.Ecs.Systems;

namespace Vixen.Engine.Diagnostics;

/// <summary>Ages the debug geometry once a frame, after everything that might draw it has.</summary>
/// <remarks>
///     <para>
///         In <see cref="SystemPhase.PostRender" /> and nowhere else: a line asked for during a frame
///         has to survive until a renderer has had the chance to drain it, so ageing has to come
///         after the draining and not before.
///     </para>
///     <para>
///         ⚠ <b>Which means this system is wrong for a host that records its frame outside the
///         loop, and the shipped host is one.</b> <c>VixenApplication</c> runs every phase of
///         <c>EngineLoop.Frame</c> — <c>PostRender</c> included — and only then calls
///         <c>AppGraphics.Begin</c>, which is where the compositor's debug node drains the
///         accumulator. Adding this to that loop ages the frame's geometry away one call before
///         anything reads it, and the failure is silent in the worst way: the overlays report the
///         panels they drew, the accumulator reports the lines it took, and the screen is empty.
///         <c>AppGraphics.Overlays</c> therefore calls <c>DebugDraw.Advance</c> itself after the
///         frame is recorded and does <em>not</em> register this. It is right for a host whose
///         renderer is a system in the same graph — a test, an editor play mode driving the loop
///         around its own draw.
///     </para>
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
