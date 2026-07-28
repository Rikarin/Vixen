// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Ecs;
using Vixen.Animation.Motions;
using Vixen.Animation.StateMachine;
using Vixen.Core.Mathematics;
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
    public void SkinningSystem_NoRenderer_IsAHarmlessNoOp() {
        using var world = new World(nameof(SkinningSystem_NoRenderer_IsAHarmlessNoOp));

        world.Create(
            new AnimatorComponent { Value = Walking(RootMotionMode.Disabled) },
            new SkinnedRenderer { RenderObject = new(0) }
        );

        new SkinningSystem().Run(world);
    }
}
