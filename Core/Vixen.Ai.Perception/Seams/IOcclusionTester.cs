// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Physics;
using Vixen.Physics.Ecs;
using Vixen.Physics.Queries;

namespace Vixen.Ai.Perception;

/// <summary>Whether there is anything between two points.</summary>
/// <remarks>
///     <para>
///         The last and most expensive of <see cref="SightSettings" />'s three tests, and the only one
///         that leaves this assembly. It is a seam rather than a direct call into
///         <see cref="PhysicsWorld" /> for two reasons, and neither of them is testability: a game
///         with smoke grenades, fog volumes, one-way glass or a stealth mechanic needs sight blocked
///         by things that are not collision geometry, and a game with no physics world at all — a
///         top-down strategy, a management sim — needs the radius and the cone without linking a
///         solver.
///     </para>
///     <para>
///         ⚠ <b>An addition to doc 37 § Part 4's seam table, made here.</b> The table lists ten seams
///         and this is not one of them; the document assumed the trace was a direct physics call.
///         Writing it that way made <see cref="SightSettings.Occlusion" /> a flag with only one
///         meaning and put a <see cref="PhysicsWorld" /> in the constructor of a system that a
///         gridless game has no use for.
///     </para>
/// </remarks>
public interface IOcclusionTester {
    /// <summary>Whether the line between two points is clear.</summary>
    /// <param name="world">The world both entities are in.</param>
    /// <param name="listener">Who is looking.</param>
    /// <param name="source">What is being looked at.</param>
    /// <param name="from">The eye.</param>
    /// <param name="target">The point on the source being aimed at.</param>
    /// <returns>Whether the source can be seen.</returns>
    bool IsClear(World world, Entity listener, Entity source, Vector3 from, Vector3 target);
}

/// <summary>Nothing ever blocks anything.</summary>
/// <remarks>
///     The second implementation, and a real configuration rather than a stub: a top-down game with
///     no vertical geometry, a minimap ping, a boss that always knows. It is also what
///     <see cref="SightSettings.Occlusion" /> being false resolves to, so the flag and the seam do not
///     both have to be consulted on the hot path.
/// </remarks>
public sealed class OpenSightlines : IOcclusionTester {
    /// <summary>The one there needs to be.</summary>
    public static OpenSightlines Instance { get; } = new();

    /// <inheritdoc />
    public bool IsClear(World world, Entity listener, Entity source, Vector3 from, Vector3 target) => true;
}

/// <summary>A raycast against the physics world.</summary>
/// <param name="physics">The world to trace in.</param>
/// <param name="blockers">Which layers stop sight, or null for all of them.</param>
/// <remarks>
///     <para>
///         ⚠ <b>Two bodies have to be got out of the way and they are handled differently.</b> The
///         listener's own body is excluded by the query, because the eye is usually inside its own
///         capsule and a ray that starts inside a convex shape is not reliably outside it. The
///         source's body cannot be excluded — the closest hit <i>being</i> the source is the
///         affirmative answer — so it is compared against instead. Excluding both would report a
///         clear line to something standing behind a wall whose collider the source is inside.
///     </para>
///     <para>
///         <b>The layer mask is here rather than on <see cref="SightSettings" />.</b> Which layers
///         block sight is a fact about the physics world, and a settings record that carried one
///         would be a settings record that cannot be used by <see cref="OpenSightlines" />.
///     </para>
/// </remarks>
public sealed class PhysicsOcclusion(PhysicsWorld physics, PhysicsLayerMask? blockers = null) : IOcclusionTester {
    readonly PhysicsWorld physics = physics ?? throw new ArgumentNullException(nameof(physics));

    /// <summary>Which layers stop sight.</summary>
    public PhysicsLayerMask Blockers { get; } = blockers ?? PhysicsLayerMask.All;

    /// <inheritdoc />
    public bool IsClear(World world, Entity listener, Entity source, Vector3 from, Vector3 target) {
        ArgumentNullException.ThrowIfNull(world);

        var ray = target - from;
        var distance = ray.Length();

        if (distance < 1e-4f) {
            return true;
        }

        var filter = new QueryFilter { Layers = Blockers, IgnoreBody = BodyOf(world, listener) };

        if (!physics.Raycast(from, ray / distance, distance, out var hit, filter)) {
            return true;
        }

        return hit.Body == BodyOf(world, source);
    }

    static Physics.Bodies.BodyHandle BodyOf(World world, Entity entity) =>
        !entity.IsNull && world.IsAlive(entity) && world.Has<PhysicsBody>(entity)
            ? world.Get<PhysicsBody>(entity).Handle
            : Physics.Bodies.BodyHandle.None;
}
