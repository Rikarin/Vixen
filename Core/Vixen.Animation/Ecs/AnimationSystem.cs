// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Transforms;

namespace Vixen.Animation.Ecs;

/// <summary>
///     Runs every animator once a frame, and puts root motion where it was asked to.
/// </summary>
/// <remarks>
///     <para>
///         In <see cref="SystemPhase.Animation" />, which the ECS defines as "after logic has decided
///         what is playing" and before <see cref="SystemPhase.LateUpdate" /> and the transform pass.
///         That ordering is what makes root motion work: the delta is written to
///         <see cref="LocalTransform" /> here, and <c>TransformSystem</c> in
///         <see cref="SystemPhase.PreRender" /> composes the world matrix from it afterwards.
///     </para>
///     <para>
///         <b>Two queries rather than one and a test.</b> Whether an entity has a
///         <see cref="LocalTransform" /> is an archetype question, so asking it per entity would be
///         paying per frame for something the archetype already answered. An animator on an entity
///         with no transform is a legitimate thing — a UI rig, a test — and it simply does not get
///         the root-motion branch.
///     </para>
///     <para>
///         <b>Inline, for now.</b> Each animator is independent and this is the shape of work the
///         job scheduler exists for, but a parallel version has to answer for the managed component
///         store's threading first — see the README. The loop is written so that becoming parallel
///         is a change to this file and to nothing else.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.Animation)]
public sealed class AnimationSystem : SystemBase, IDeclaredAccess {
    readonly QueryDescription placed = new QueryDescription()
        .WithAll<AnimatorComponent, LocalTransform>();

    readonly QueryDescription unplaced = new QueryDescription()
        .WithAll<AnimatorComponent>()
        .WithNone<LocalTransform>();

    /// <inheritdoc />
    /// <remarks>
    ///     Declared rather than attributed, for the reason <c>TransformSystem</c> gives: naming a
    ///     component type in a generic call is what assigns it an id, and an attribute can only look
    ///     one up.
    /// </remarks>
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<AnimatorComponent>()
        .Write<LocalTransform>()
        .Write<RootMotionResult>()
        .Build();

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Run(context.World, context.Time.DeltaSeconds);
        return dependency;
    }

    /// <summary>Updates every animator in a world.</summary>
    /// <param name="world">The world.</param>
    /// <param name="deltaTime">How much time has passed, in seconds.</param>
    /// <remarks>Public so a test, a tool or an editor can step animation without a runner.</remarks>
    public void Run(World world, float deltaTime) {
        ArgumentNullException.ThrowIfNull(world);

        foreach (var chunk in world.Chunks(placed)) {
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                var entity = entities[index];
                var animator = world.Read<AnimatorComponent>(entity).Value;

                if (animator is null) {
                    continue;
                }

                animator.Update(deltaTime);
                Publish(world, entity, animator);

                if (animator.RootMotion is not RootMotionMode.Apply || animator.LastRootMotion.IsZero) {
                    continue;
                }

                ref var local = ref world.Get<LocalTransform>(entity);

                // The delta is in the character's own frame, so it composes onto the local transform
                // the same way a child's transform composes onto its parent's: motion first, then
                // where the character already was. Row-vector order — Conventions.md.
                var moved = BoneTransform.Concatenate(
                    animator.LastRootMotion.ToTransform(),
                    new(local.Position, local.Rotation, local.Scale)
                );

                local.Position = moved.Translation;
                local.Rotation = moved.Rotation;
            }
        }

        foreach (var chunk in world.Chunks(unplaced)) {
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                var animator = world.Read<AnimatorComponent>(entities[index]).Value;

                if (animator is not null) {
                    animator.Update(deltaTime);
                    Publish(world, entities[index], animator);
                }
            }
        }
    }

    static void Publish(World world, Entity entity, Animator animator) {
        if (world.Has<RootMotionResult>(entity)) {
            world.Get<RootMotionResult>(entity).Delta = animator.LastRootMotion;
        }
    }
}
