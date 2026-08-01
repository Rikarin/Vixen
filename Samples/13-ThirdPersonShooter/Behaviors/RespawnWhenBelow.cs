// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Behaviors;
using Vixen.Engine.Transforms;
using Vixen.Physics.Characters;

namespace Vixen.Samples.ThirdPersonShooter;

/// <summary>Puts the player back at a spawn point when they fall out of the level.</summary>
/// <remarks>
///     <para>
///         <b>Two lines of engine behaviour worth knowing about, and this is the smallest thing that
///         uses both.</b> Writing <c>LocalTransform</c> on a character is a <i>teleport</i>: the
///         physics scene adopts a written transform on the next fixed step rather than sweeping to it,
///         which is what makes a respawn — or a rollback — reach a <c>CharacterController</c> at all.
///         And the velocity has to be cleared by hand, because the controller keeps the fall it was
///         doing and would arrive at the spawn point already moving at terminal velocity.
///     </para>
///     <para>
///         The controller is untouched. It never fell anywhere, it still holds the aim, and the camera
///         bound to it is still looking where the player was looking — which is the property doc 29
///         arranges everything else around, seen here in the smallest possible case.
///     </para>
/// </remarks>
public sealed class RespawnWhenBelow : Behavior {
    /// <summary>The height below which the player has left the level.</summary>
    public float Floor { get; init; } = -8f;

    /// <summary>The controller, kept only to make the point that it is not what moves.</summary>
    public Entity Controller { get; init; }

    /// <summary>Which spawn point to come back at.</summary>
    public int SpawnIndex { get; init; }

    /// <summary>How many times this has happened.</summary>
    public int Respawns { get; private set; }

    /// <inheritdoc />
    protected override void Update() {
        if (Position.Y > Floor || !Has<LocalTransform>()) {
            return;
        }

        var start = ThirdPersonShooterGame.SpawnPointAt(World, SpawnIndex);

        // A write, not a sweep. PhysicsScene.Adopt notices that the transform moved further than its
        // threshold and snaps the controller to it, then refreshes contacts so the first step after
        // lands on the floor rather than through it.
        ref var transform = ref Get<LocalTransform>();
        transform.Position = start.Position;

        if (Has<CharacterState>()) {
            ref var state = ref Get<CharacterState>();
            state.Velocity = Vector3.Zero;
            state.Mode = CharacterMoveMode.Falling;
        }

        Respawns++;
    }
}
