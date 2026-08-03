// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Constraints;
using Vixen.Animation.Ecs;
using Vixen.Animation.Motions;
using Vixen.Animation.StateMachine;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Xunit;

namespace Vixen.Animation.Tests;

/// <summary>
///     The frame's shape: the stage before any character has a pose, and what a scheduler may claim.
/// </summary>
public class ConstraintStageTests {
    readonly Skeleton skeleton = TestRigs.Chain();

    [Fact]
    public void TheDefaultSchedulerPlansNothingAndEveryStackSolvesItself() {
        using var world = new World(nameof(TheDefaultSchedulerPlansNothingAndEveryStackSolvesItself));
        var (animator, stack, _) = Character(new(0.9f, 1.3f, 0f));

        world.Create(new AnimatorComponent { Value = animator });

        var system = new AnimationSystem();

        for (var frame = 0; frame < 60; frame++) {
            system.Run(world, 1f / 60f);
        }

        Assert.Equal(1, system.LastStackCount);
        Assert.False(stack.Scheduled, "nothing claimed it");
        Assert.False(stack.HasPublished, "the default plans no pre-evaluation group, so nothing publishes");
        TestRigs.Near(new(0.9f, 1.3f, 0f), TestRigs.ModelPositions(animator.Pose)[2]);
    }

    [Fact]
    public void AGroupedPreEvaluationSolvePublishesBeforeAnybodyEvaluates() {
        using var world = new World(nameof(AGroupedPreEvaluationSolvePublishesBeforeAnybodyEvaluates));
        var first = Character(new(0.9f, 1.3f, 0f));
        var second = Character(new(-0.9f, 1.3f, 0f));

        world.Create(new AnimatorComponent { Value = first.Animator });
        world.Create(new AnimatorComponent { Value = second.Animator });

        var scheduler = new OneGroupTestScheduler();
        var system = new AnimationSystem { Scheduler = scheduler };

        system.Run(world, 1f / 60f);

        Assert.Equal(1, scheduler.PreEvaluationCalls);
        Assert.Equal(2, system.LastStackCount);

        // ⚠ What the stage is for. Both characters were corrected and published while every animator
        // still had last frame's pose, so a member reading a neighbour reads a settled pose rather
        // than one halfway through being mixed.
        Assert.True(first.Stack.HasPublished);
        Assert.True(second.Stack.HasPublished);
        Assert.Equal(skeleton.JointCount, first.Stack.Published.Length);
        Assert.False(first.Stack.Scheduled, "the pose stage claimed nothing");
    }

    [Fact]
    public void AStackAClaimedGroupSolvesIsNotAlsoSolvedByItsAnimator() {
        using var world = new World(nameof(AStackAClaimedGroupSolvesIsNotAlsoSolvedByItsAnimator));

        var loose = Character(new(0.9f, 1.3f, 0f));
        var claimed = Character(new(0.9f, 1.3f, 0f));

        using var looseWorld = new World($"{nameof(AStackAClaimedGroupSolvesIsNotAlsoSolvedByItsAnimator)}-loose");

        looseWorld.Create(new AnimatorComponent { Value = loose.Animator });
        world.Create(new AnimatorComponent { Value = claimed.Animator });

        new AnimationSystem().Run(looseWorld, 1f / 60f);

        var scheduler = new OneGroupTestScheduler { GroupBeforeEvaluation = false, GroupAfterEvaluation = true };

        new AnimationSystem { Scheduler = scheduler }.Run(world, 1f / 60f);

        Assert.True(claimed.Stack.Scheduled);

        // Same work, once, wherever it happened. A claim that failed to stop the processor pass would
        // show up here as twice the solver calls and a pose corrected on top of itself.
        Assert.Equal(loose.Solver.Calls, claimed.Solver.Calls);

        TestRigs.Near(
            TestRigs.ModelPositions(loose.Animator.Pose)[2],
            TestRigs.ModelPositions(claimed.Animator.Pose)[2]
        );
    }

    [Fact]
    public void AWorldWithNoConstraintsNeverEntersTheStage() {
        using var world = new World(nameof(AWorldWithNoConstraintsNeverEntersTheStage));
        var animator = new Animator(skeleton);

        animator.AddLayer("Base", new([new AnimationState("Idle", Idle())]));
        world.Create(new AnimatorComponent { Value = animator });

        var scheduler = new OneGroupTestScheduler();
        var system = new AnimationSystem { Scheduler = scheduler };

        system.Run(world, 1f / 60f);

        Assert.Equal(0, system.LastStackCount);

        // Not "planned an empty group" — not called at all. The stage's cost with no constraints in
        // the world is the type test that found none.
        Assert.Equal(0, scheduler.PreEvaluationCalls);
        Assert.Equal(0, scheduler.PoseCalls);
    }

    (Animator Animator, ConstraintStack Stack, TeleportingTestSolver Solver) Character(Vector3 target) {
        var animator = new Animator(skeleton);

        animator.AddLayer("Base", new([new AnimationState("Idle", Idle())]));

        var solver = new TeleportingTestSolver();
        var stack = new ConstraintStack(skeleton, solver: solver);

        stack.Add(
            new PositionGoal {
                Effector = 2,
                Chain = new(0, 2),
                Goal = new ModelTestFrame(target),
                EaseIn = 0f
            }
        );

        animator.PoseProcessors.Add(stack);

        return (animator, stack, solver);
    }

    ClipMotion Idle() =>
        new(AnimationClip.Create(TestRigs.Hold("Idle", "Root", Vector3.Zero), skeleton));
}
