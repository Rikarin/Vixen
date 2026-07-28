// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Diagnostics;
using Vixen.Physics.Bodies;
using Vixen.Physics.Diagnostics;

namespace Vixen.Physics.Ecs;

/// <summary>Draws the scene's colliders and contacts into a <see cref="DebugDraw" />.</summary>
/// <remarks>
///     <para>
///         In <see cref="SystemPhase.PreRender" /> and after the interpolation pass, so a collider is
///         drawn where the body is rather than where the smoothed transform says — which is the whole
///         point of a physics overlay. The two disagree by up to one step, and when they do, the one
///         being investigated is the body.
///     </para>
///     <para>
///         Off by default. The overlay is a few thousand lines for a modest scene and there is no
///         cost at all to a system whose <see cref="Enabled" /> is <see langword="false" />.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.PreRender)]
[UpdateAfter(typeof(PhysicsInterpolationSystem))]
public sealed class PhysicsDebugDrawSystem(PhysicsScene scene, DebugDraw draw) : SystemBase, IDeclaredAccess {
    static readonly QueryDescription Bodies = new QueryDescription().WithAll<PhysicsBody>();

    readonly PhysicsDebugDraw renderer = new();
    readonly List<BodyHandle> handles = [];

    /// <summary>Whether the overlay is drawn at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>What is drawn.</summary>
    public PhysicsDebugOverlay Overlay {
        get => renderer.Overlay;
        set => renderer.Overlay = value;
    }

    /// <inheritdoc />
    public SystemAccess Access { get; } = SystemAccess.Declare().Read<PhysicsBody>().Build();

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        if (!Enabled || !draw.Enabled) {
            return dependency;
        }

        handles.Clear();

        foreach (var chunk in context.World.Chunks(Bodies)) {
            var bodies = chunk.ReadValues<PhysicsBody>();

            for (var index = 0; index < chunk.Count; index++) {
                handles.Add(bodies[index].Handle);
            }
        }

        renderer.Draw(scene.World, draw, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(handles));
        return dependency;
    }
}
