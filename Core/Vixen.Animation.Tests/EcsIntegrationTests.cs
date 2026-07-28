// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Ecs;
using Vixen.Animation.Motions;
using Vixen.Animation.StateMachine;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Animation.Tests;

public class EcsIntegrationTests {
    readonly Skeleton skeleton = TestRigs.Chain();

    Animator Walking(RootMotionMode mode) {
        var clip = AnimationClip.Create(
            TestRigs.Translate("Walk", "Root", Vector3.Zero, new(0f, 0f, -4f)),
            skeleton
        );

        var animator = new Animator(skeleton) { RootMotion = mode };
        animator.AddLayer("Base", new([new AnimationState("Walk", new ClipMotion(clip))]));

        return animator;
    }

    [Fact]
    public void Run_AnimatorWithoutATransform_StillUpdates() {
        using var world = new World(nameof(Run_AnimatorWithoutATransform_StillUpdates));
        var animator = Walking(RootMotionMode.Disabled);
        var entity = world.Create(new AnimatorComponent { Value = animator });

        new AnimationSystem().Run(world, 0.25f);

        Assert.True(world.IsAlive(entity));
        TestRigs.Near(new(0f, 0f, -1f), animator.Pose[0].Translation);
    }

    [Fact]
    public void Run_ApplyMode_MovesTheEntitysLocalTransform() {
        using var world = new World(nameof(Run_ApplyMode_MovesTheEntitysLocalTransform));

        var entity = world.Create(
            new AnimatorComponent { Value = Walking(RootMotionMode.Apply) },
            LocalTransform.Identity
        );

        new AnimationSystem().Run(world, 0.25f);

        TestRigs.Near(new(0f, 0f, -1f), world.Read<LocalTransform>(entity).Position);
    }

    [Fact]
    public void Run_ApplyMode_AccumulatesAcrossFrames() {
        using var world = new World(nameof(Run_ApplyMode_AccumulatesAcrossFrames));

        var entity = world.Create(
            new AnimatorComponent { Value = Walking(RootMotionMode.Apply) },
            LocalTransform.Identity
        );

        var system = new AnimationSystem();
        system.Run(world, 0.25f);
        system.Run(world, 0.25f);

        TestRigs.Near(new(0f, 0f, -2f), world.Read<LocalTransform>(entity).Position);
    }

    [Fact]
    public void Run_ApplyMode_MovesAlongTheEntitysOwnFacing() {
        using var world = new World(nameof(Run_ApplyMode_MovesAlongTheEntitysOwnFacing));

        var turned = LocalTransform.Identity;
        turned.Rotation = Quaternion.FromAxisAngle(Vector3.UnitY, MathUtil.PiOverTwo);

        var entity = world.Create(new AnimatorComponent { Value = Walking(RootMotionMode.Apply) }, turned);

        new AnimationSystem().Run(world, 0.25f);

        // Rotated 90° about +Y, the character's −Z points along −X.
        TestRigs.Near(new(-1f, 0f, 0f), world.Read<LocalTransform>(entity).Position);
    }

    [Fact]
    public void Run_ExtractMode_LeavesTheTransformAloneAndPublishesTheDelta() {
        using var world = new World(nameof(Run_ExtractMode_LeavesTheTransformAloneAndPublishesTheDelta));

        var entity = world.Create(
            new AnimatorComponent { Value = Walking(RootMotionMode.Extract) },
            LocalTransform.Identity,
            default(RootMotionResult)
        );

        new AnimationSystem().Run(world, 0.25f);

        TestRigs.Near(Vector3.Zero, world.Read<LocalTransform>(entity).Position);
        TestRigs.Near(new(0f, 0f, -1f), world.Read<RootMotionResult>(entity).Delta.Translation);
    }

    [Fact]
    public void Run_NullAnimator_IsSkippedRatherThanThrowing() {
        using var world = new World(nameof(Run_NullAnimator_IsSkippedRatherThanThrowing));
        world.Create(default(AnimatorComponent), LocalTransform.Identity);

        new AnimationSystem().Run(world, 0.25f);
    }

    [Fact]
    public void Run_ManyEntities_EachKeepsItsOwnPlaybackTime() {
        using var world = new World(nameof(Run_ManyEntities_EachKeepsItsOwnPlaybackTime));

        var fast = Walking(RootMotionMode.Disabled);
        var slow = Walking(RootMotionMode.Disabled);
        slow.Speed = 0.5f;

        world.Create(new AnimatorComponent { Value = fast });
        world.Create(new AnimatorComponent { Value = slow });

        new AnimationSystem().Run(world, 0.5f);

        TestRigs.Near(new(0f, 0f, -2f), fast.Pose[0].Translation);
        TestRigs.Near(new(0f, 0f, -1f), slow.Pose[0].Translation);
    }

    [Fact]
    public void Run_AcrossTheScheduler_GivesTheSameAnswerAsInline() {
        using var serial = new World(nameof(Run_AcrossTheScheduler_GivesTheSameAnswerAsInline) + "Serial");
        using var parallel = new World(nameof(Run_AcrossTheScheduler_GivesTheSameAnswerAsInline) + "Parallel");
        using var jobs = new JobScheduler(4);

        const int Characters = 64;
        var inline = new Animator[Characters];
        var scheduled = new Animator[Characters];

        for (var index = 0; index < Characters; index++) {
            inline[index] = Walking(RootMotionMode.Apply);
            scheduled[index] = Walking(RootMotionMode.Apply);

            // Different speeds, so the work per animator differs and the batches are uneven — which
            // is the case a work-stealing scheduler is allowed to reorder and must not change.
            inline[index].Speed = 0.5f + (index * 0.01f);
            scheduled[index].Speed = inline[index].Speed;

            serial.Create(new AnimatorComponent { Value = inline[index] }, LocalTransform.Identity);
            parallel.Create(new AnimatorComponent { Value = scheduled[index] }, LocalTransform.Identity);
        }

        var system = new AnimationSystem();

        for (var frame = 0; frame < 8; frame++) {
            system.Run(serial, 0.05f);
            system.Run(parallel, 0.05f, jobs);
        }

        Assert.Equal(Characters, system.LastEvaluatedCount);

        for (var index = 0; index < Characters; index++) {
            TestRigs.Near(
                inline[index].Pose[1].Translation,
                scheduled[index].Pose[1].Translation,
                $"animator {index}"
            );

            TestRigs.Near(
                inline[index].LastRootMotion.Translation,
                scheduled[index].LastRootMotion.Translation,
                $"animator {index}"
            );
        }
    }

    [Fact]
    public void Run_AcrossTheScheduler_StillMovesEveryTransform() {
        using var world = new World(nameof(Run_AcrossTheScheduler_StillMovesEveryTransform));
        using var jobs = new JobScheduler(4);

        // Above AnimationSystem.ParallelThreshold, or this would quietly test the inline path.
        var entities = new Entity[AnimationSystem.ParallelThreshold * 2];

        for (var index = 0; index < entities.Length; index++) {
            entities[index] = world.Create(
                new AnimatorComponent { Value = Walking(RootMotionMode.Apply) },
                LocalTransform.Identity
            );
        }

        new AnimationSystem().Run(world, 0.25f, jobs);

        foreach (var entity in entities) {
            TestRigs.Near(new(0f, 0f, -1f), world.Read<LocalTransform>(entity).Position);
        }
    }

    [Fact]
    public void Run_EntitiesDestroyedBetweenFrames_AreNotEvaluatedAgain() {
        using var world = new World(nameof(Run_EntitiesDestroyedBetweenFrames_AreNotEvaluatedAgain));

        var entity = world.Create(new AnimatorComponent { Value = Walking(RootMotionMode.Disabled) });
        var system = new AnimationSystem();

        system.Run(world, 0.1f);
        Assert.Equal(1, system.LastEvaluatedCount);

        world.Destroy(entity);
        system.Run(world, 0.1f);

        Assert.Equal(0, system.LastEvaluatedCount);
    }

    [Fact]
    public void SkinningSystem_NoRenderer_IsAHarmlessNoOp() {
        using var world = new World(nameof(SkinningSystem_NoRenderer_IsAHarmlessNoOp));

        world.Create(
            new AnimatorComponent { Value = Walking(RootMotionMode.Disabled) },
            new SkinnedRenderer { RenderObject = new(0) }
        );

        new SkinningSystem().Run(world);
    }
}
