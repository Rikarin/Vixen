// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Constraints;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Diagnostics;

namespace Vixen.Animation.Ecs;

/// <summary>Puts the constraint gizmos in whatever viewport is drawing debug geometry.</summary>
/// <remarks>
///     <para>
///         The editor's answer to "the viewport draws the goal", and a game's answer to "why is his
///         hand through the wall on this one machine". One system, off by default, that walks the
///         animators carrying a <see cref="ConstraintStack" /> and asks
///         <see cref="ConstraintGizmos" /> to draw what the last solve did.
///     </para>
///     <para>
///         ⚠ <b>In <see cref="SystemPhase.LateUpdate" />, after the poses exist.</b> Running it in the
///         animation phase would race the very solve it is trying to show — half the characters would
///         be drawn against this frame's answer and half against last frame's, depending on where the
///         scheduler put them.
///     </para>
///     <para>
///         ⚠ <b><see cref="Only" /> is the field that makes this usable.</b> A scene of thirty
///         constrained characters is a thousand lines and nothing legible; an author debugging one
///         interaction wants one character. Left unset it draws all of them, which is right for a
///         screenshot and wrong for everything else.
///     </para>
/// </remarks>
/// <param name="draw">Where the lines go.</param>
[UpdateInGroup(SystemPhase.LateUpdate)]
public sealed class ConstraintGizmoSystem(DebugDraw draw) : SystemBase, IDeclaredAccess {
    readonly QueryDescription query = new QueryDescription().WithAll<AnimatorComponent>();

    BoneTransform[] model = [];

    /// <inheritdoc />
    public SystemAccess Access { get; } = SystemAccess.Declare().Read<AnimatorComponent>().Build();

    /// <summary>Whether anything is drawn at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>The one character to draw, or <see langword="null" /> for every one of them.</summary>
    public Entity? Only { get; set; }

    /// <summary>What to draw and what counts as a miss.</summary>
    public ConstraintGizmoStyle Style { get; set; } = ConstraintGizmoStyle.Default;

    /// <summary>Whether every proxy shape is drawn, and not only the ones a goal is anchored to.</summary>
    /// <remarks>
    ///     What the shape editor turns on. Separate from <see cref="ConstraintGizmoStyle.Shapes" />,
    ///     which asks a much narrower question — that one draws the shape a surface goal resolved
    ///     against, and answering it costs one lookup rather than a pass over the whole set.
    /// </remarks>
    public bool AllShapes { get; set; }

    /// <summary>How many stacks the last run drew.</summary>
    public int LastDrawnCount { get; private set; }

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Run(context.World);
        return dependency;
    }

    /// <summary>Draws every constrained character in a world.</summary>
    /// <param name="world">The world.</param>
    /// <remarks>
    ///     Public for the same reason <c>AnimationSystem.Run</c> is: an editor stepping a preview and
    ///     a test asserting what was drawn both need this without a runner in the way.
    /// </remarks>
    public void Run(World world) {
        ArgumentNullException.ThrowIfNull(world);

        LastDrawnCount = 0;

        if (!Enabled) {
            return;
        }

        foreach (var chunk in world.Chunks(query)) {
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                if (Only is { } wanted && entities[index] != wanted) {
                    continue;
                }

                if (world.Read<AnimatorComponent>(entities[index]).Value is { } animator) {
                    One(animator);
                }
            }
        }
    }

    void One(Animator animator) {
        var processors = animator.PoseProcessors;

        for (var index = 0; index < processors.Count; index++) {
            if (processors[index] is not ConstraintStack stack) {
                continue;
            }

            var joints = stack.Skeleton.JointCount;

            if (model.Length < joints) {
                model = new BoneTransform[joints];
            }

            // ⚠ Recomputed from the animator's own pose rather than kept from the solve. The stack's
            // model-space buffer is scratch it reuses, and by the time this runs another character's
            // solve may well have written over it.
            SkeletonPose.ComputeModelSpace(stack.Skeleton, animator.Pose.Bones, model.AsSpan(0, joints));

            ConstraintGizmos.Draw(draw, stack, model.AsSpan(0, joints), Style);

            if (AllShapes && stack.Shapes is { } shapes) {
                ConstraintGizmos.DrawShapes(draw, shapes, model.AsSpan(0, joints), stack.WorldTransform);
            }

            LastDrawnCount++;
        }
    }
}
