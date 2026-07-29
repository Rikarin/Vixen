// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Ecs.Systems;

namespace Vixen.Engine.Diagnostics.Overlays;

/// <summary>Draws the diagnostic overlays into the frame's debug geometry.</summary>
/// <remarks>
///     <para>
///         In <see cref="SystemPhase.PreRender" />, which is the last phase before the graph is
///         recorded: everything an overlay reports on has run, and the renderer has not yet drained.
///         <c>DebugDrawSystem</c> ages the result afterwards, in <c>PostRender</c>.
///     </para>
///     <para>
///         ⚠ <b><see cref="Viewport" /> is in render-target pixels and the host has to keep it
///         current.</b> Nothing here can know how big the window is — that is a platform's answer,
///         and this assembly has no platform. A stale value draws the overlays for the size the
///         window used to be, which after a resize means panels that hang off the edge.
///     </para>
/// </remarks>
/// <param name="overlays">The registry to draw.</param>
/// <param name="draw">Where the geometry goes.</param>
[UpdateInGroup(SystemPhase.PreRender)]
public sealed class DiagnosticOverlaySystem(DiagnosticOverlays overlays, DebugDraw draw) : SystemBase, IDeclaredAccess {
    /// <summary>How big the render target is, in pixels.</summary>
    public Vector2 Viewport { get; set; }

    /// <inheritdoc />
    public SystemAccess Access => SystemAccess.None;

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        overlays.Draw(draw, Viewport, context.Time);
        return dependency;
    }
}
