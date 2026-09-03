// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Threading;
using Vixen.Ecs.Systems;
using Vixen.Engine.Players;
using Vixen.Engine.Transforms;
using Vixen.Physics.Characters;

namespace Vixen.Physics.Ecs;

/// <summary>Moves every character by what its <see cref="MoveIntent" /> asked for.</summary>
/// <remarks>
///     <para>
///         The fourth fixed-step pass, and it runs <b>after</b> <see cref="PhysicsStepSystem" />
///         because a character is swept against the world as it is now — sweeping first would test it
///         against the previous step's platform positions, which is
///         <see cref="CharacterController.Update" />'s own stated requirement.
///     </para>
///     <para>
///         <b>It is one half of a player and it has never heard of one.</b> What it reads is a
///         component; whether a person, a planner or a replay wrote it is not knowable from here and
///         is not supposed to be ([29](../../../docs/plan/29-players-and-possession.md)). That is also
///         what keeps this assembly free of <c>Vixen.Net</c>: the predicted step replays this system
///         over a restored world and needs nothing else from it.
///     </para>
///     <para>
///         It declares <c>Write&lt;LocalTransform&gt;</c>, which the runner reads as a conflict with
///         <see cref="PhysicsWritebackSystem" /> and serialises the two. That is correct rather than
///         unfortunate: they write disjoint entities, but nothing in a declared-access set can say so,
///         and the alternative is a declaration that lies.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.FixedUpdate)]
[UpdateAfter(typeof(PhysicsStepSystem))]
public sealed class CharacterMovementSystem(PhysicsScene scene) : SystemBase, IDeclaredAccess {
    readonly PhysicsScene scene = scene ?? throw new ArgumentNullException(nameof(scene));

    /// <inheritdoc />
    /// <remarks>
    ///     Declared at construction rather than with attributes, for the reason <c>TransformSystem</c>
    ///     gives: naming a component type in a generic call is what assigns it an id, and an attribute
    ///     can only look one up.
    /// </remarks>
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<CharacterMovement>()
        .Read<MoveIntent>()
        .Write<CharacterState>()
        .Write<CharacterBody>()
        .Write<LocalTransform>()
        .Write<PhysicsInterpolation>()

        // Written, not read: StepCharacters consumes a teleport tag put on a character and takes it
        // off again, exactly as PhysicsSyncSystem does for a rigid body.
        .Write<PhysicsTeleport>()
        .Build();

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        // The step has moved everything a character is about to sweep against, and the sweep is a
        // native call the ECS cannot see into. Nothing scheduled may still be reading what it writes.
        dependency.Complete();

        scene.StepCharacters((float)context.Time.Elapsed.TotalSeconds);
        return default;
    }
}
