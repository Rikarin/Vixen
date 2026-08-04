// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai.Ecs;
using Vixen.Core.Threading;
using Vixen.Ecs.Systems;
using Vixen.Engine.Diagnostics;

namespace Vixen.Ai.Diagnostics;

/// <summary>Draws the AI overlay once a frame, after everything that decides anything has run.</summary>
/// <remarks>
///     <para>
///         In <see cref="SystemPhase.PreRender" /> for <c>DiagnosticOverlaySystem</c>'s reason: an
///         overlay reads and does not simulate, so it runs after the numbers it reports exist and
///         before anything drains the geometry. <c>AiSystem</c> is in <c>Update</c> and every action
///         it started has been ticked by then, so what the overlay shows is this frame's decision
///         rather than the last one's.
///     </para>
///     <para>
///         ⚠ <b>A system rather than a call in the host, so that turning the debugger on is adding a
///         system.</b> A host that never adds it pays nothing at all — no capture, no formatting, and
///         no reference from the frame loop to an assembly it does not otherwise need.
///     </para>
/// </remarks>
/// <param name="debugger">What to draw.</param>
/// <param name="agents">The system holding the agents.</param>
/// <param name="draw">Where the geometry goes.</param>
[UpdateInGroup(SystemPhase.PreRender)]
public sealed class AiOverlaySystem(AiGameplayDebugger debugger, AiSystem agents, DebugDraw draw)
    : SystemBase, IDeclaredAccess {
    /// <summary>What is drawn.</summary>
    public AiGameplayDebugger Debugger => debugger;

    /// <inheritdoc />
    /// <remarks>
    ///     Reads the agent component and writes nothing, which is what makes it safe to leave on: an
    ///     overlay that declared a write would serialise itself against the system it reports on.
    /// </remarks>
    public SystemAccess Access { get; } = SystemAccess.Declare().Read<AiAgent>().Build();

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        debugger.Draw(draw, agents, context.World, context.Time);

        return dependency;
    }
}
