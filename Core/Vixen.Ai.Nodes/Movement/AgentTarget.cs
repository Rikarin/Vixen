// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai.Nodes.Ecs;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;

namespace Vixen.Ai.Nodes;

/// <summary>Turning "the key called target" into a place in the world.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>One key, two types, and the node does not care which.</b> A designer writing
///         <c>MoveTo(target)</c> means "go to that thing", and whether the thing is an entity or a
///         remembered position is a fact about how the key got written rather than about what the
///         node does. A node per key type would be two nodes in the search popup with the same name
///         and the wrong one selected half the time.
///     </para>
///     <para>
///         An entity key follows its entity as it moves; a <c>Vector3</c> key does not. That is the
///         difference between chasing somebody and going to where they were, which is exactly the
///         pair perception's default binding writes.
///     </para>
/// </remarks>
public static class AgentTarget {
    /// <summary>Where a key says to go.</summary>
    /// <param name="context">The agent.</param>
    /// <param name="key">The key.</param>
    /// <param name="position">Where to put the place.</param>
    /// <param name="target">The entity, if the key named one and it is still alive.</param>
    /// <returns>Whether there is anywhere to go.</returns>
    public static bool TryResolve(in AgentContext context, BlackboardKey key, out Vector3 position, out Entity target) {
        position = Vector3.Zero;
        target = Entity.Null;

        var blackboard = context.Blackboard;

        if (!key.IsValid || key.Index >= blackboard.Layout.Count || !blackboard.IsSet(key)) {
            return false;
        }

        switch (blackboard.Layout[key].Type) {
            case BlackboardValueType.Vector3:
                position = blackboard.GetVector3(key);

                return true;

            case BlackboardValueType.Entity:
                target = blackboard.GetEntity(key);

                return TryPositionOf(context.World, target, out position);

            default:
                return false;
        }
    }

    /// <summary>Where an entity is.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="position">Where to put it.</param>
    /// <returns>Whether it is alive and has a transform.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    public static bool TryPositionOf(World world, Entity entity, out Vector3 position) {
        ArgumentNullException.ThrowIfNull(world);

        position = Vector3.Zero;

        if (entity.IsNull || !world.IsAlive(entity) || !world.Has<LocalTransform>(entity)) {
            return false;
        }

        position = world.Get<LocalTransform>(entity).Position;

        return true;
    }

    /// <summary>Where a key says to go, falling back to the agent's focus.</summary>
    /// <param name="context">The agent.</param>
    /// <param name="key">The key, which may be invalid.</param>
    /// <param name="position">Where to put the place.</param>
    /// <returns>Whether there is anywhere to go.</returns>
    /// <remarks>
    ///     What a node with no key configured does: fall through to <see cref="AiFocus" />, which is
    ///     the one place everything downstream reads. A node that simply failed would make "turn to
    ///     face what I am looking at" need a key that duplicates the focus.
    /// </remarks>
    public static bool TryResolveOrFocus(in AgentContext context, BlackboardKey key, out Vector3 position) {
        if (TryResolve(in context, key, out position, out _)) {
            return true;
        }

        return context.World.Has<AiFocus>(context.Entity)
            && context.World.Get<AiFocus>(context.Entity).TryResolve(context.World, out position);
    }

    /// <summary>The distance between two places, ignoring height.</summary>
    /// <param name="from">One.</param>
    /// <param name="to">The other.</param>
    /// <returns>The distance, in metres.</returns>
    /// <remarks>
    ///     ⚠ <b>Acceptance radii are horizontal, and a three-dimensional one is a bug on a slope.</b>
    ///     An agent standing on a ramp is a metre above the destination it was given at floor height,
    ///     and a spherical test then says it has not arrived while it stands on top of it.
    /// </remarks>
    public static float FlatDistance(Vector3 from, Vector3 to) {
        var dx = to.X - from.X;
        var dz = to.Z - from.Z;

        return MathF.Sqrt((dx * dx) + (dz * dz));
    }
}
