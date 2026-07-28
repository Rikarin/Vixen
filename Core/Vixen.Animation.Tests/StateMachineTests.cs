// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Motions;
using Vixen.Animation.StateMachine;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Animation.Tests;

public class StateMachineTests {
    readonly Skeleton skeleton = TestRigs.Chain();
    readonly AnimationParameters parameters = new();

    ClipMotion Held(string name, float x, float duration = 1f) =>
        new(
            AnimationClip.Create(
                TestRigs.Hold(name, "Mid", new Vector3(x, 0f, 0f), duration),
                skeleton
            )
        );

    StateMachineInstance Instance(AnimationStateMachine machine) =>
        new(machine, parameters, new PoseScratch(skeleton.JointCount));

    float Step(StateMachineInstance instance, float deltaTime, AnimationEventBuffer? events = null) {
        var pose = new BoneTransform[skeleton.JointCount];
        instance.Evaluate(deltaTime, pose, events, 0, 1f, false);

        return pose[1].Translation.X;
    }

    [Fact]
    public void Evaluate_NoTransitions_StaysInTheDefaultState() {
        var idle = new AnimationState("Idle", Held("Idle", 1f));
        var instance = Instance(new([idle]));

        Assert.Equal(1f, Step(instance, 0.1f));
        Assert.Equal("Idle", instance.CurrentStateName);
    }

    [Fact]
    public void Evaluate_ConditionBecomesTrue_TransitionsAndBlends() {
        var idle = new AnimationState("Idle", Held("Idle", 0f));
        var walk = new AnimationState("Walk", Held("Walk", 10f));
        idle.TransitionTo(walk, 1f).When(AnimationCondition.On(parameters, "Moving", AnimationConditionMode.If));

        var instance = Instance(new([idle, walk]));

        Assert.Equal(0f, Step(instance, 0.1f));

        parameters.SetBool("Moving", true);

        // The transition fires this step and starts at zero weight, so the pose is still the idle.
        Assert.Equal(0f, Step(instance, 0f), TestRigs.Tolerance);
        Assert.True(instance.IsTransitioning);
        Assert.Equal("Walk", instance.CurrentStateName);

        Assert.Equal(5f, Step(instance, 0.5f), TestRigs.Tolerance);
        Assert.Equal(10f, Step(instance, 0.5f), TestRigs.Tolerance);
        Assert.False(instance.IsTransitioning);
    }

    [Fact]
    public void Evaluate_ZeroDurationTransition_IsACutWithNothingLeftBlending() {
        var idle = new AnimationState("Idle", Held("Idle", 0f));
        var hit = new AnimationState("Hit", Held("Hit", 9f));
        idle.TransitionTo(hit, 0f).When(AnimationCondition.OnTrigger(parameters, "Hit"));

        var instance = Instance(new([idle, hit]));
        parameters.SetTrigger("Hit");

        Assert.Equal(9f, Step(instance, 0.016f));
        Assert.False(instance.IsTransitioning);
        Assert.Equal(1, instance.ActiveStateCount);
    }

    [Fact]
    public void Evaluate_TriggerTaken_IsConsumedSoItDoesNotFireTwice() {
        var idle = new AnimationState("Idle", Held("Idle", 0f));
        var attack = new AnimationState("Attack", Held("Attack", 1f));
        var back = new AnimationTransition(idle, 0f) { HasExitTime = true, ExitTime = 0.5f };

        idle.TransitionTo(attack, 0f).When(AnimationCondition.OnTrigger(parameters, "Attack"));
        attack.AddTransition(back);

        var instance = Instance(new([idle, attack]));

        parameters.SetTrigger("Attack");
        Step(instance, 0.016f);
        Assert.Equal("Attack", instance.CurrentStateName);

        // Back to idle, and the trigger must not send it straight back into the attack.
        Step(instance, 0.6f);
        Assert.Equal("Idle", instance.CurrentStateName);

        Step(instance, 0.016f);
        Assert.Equal("Idle", instance.CurrentStateName);
    }

    [Fact]
    public void Evaluate_TriggerOnAFailingTransition_IsNotConsumed() {
        var idle = new AnimationState("Idle", Held("Idle", 0f));
        var jump = new AnimationState("Jump", Held("Jump", 1f));

        idle.TransitionTo(jump, 0f)
            .When(AnimationCondition.OnTrigger(parameters, "Jump"))
            .When(AnimationCondition.On(parameters, "Grounded", AnimationConditionMode.If));

        var instance = Instance(new([idle, jump]));

        parameters.SetTrigger("Jump");
        Step(instance, 0.016f);
        Assert.Equal("Idle", instance.CurrentStateName);

        // The trigger survived the frame only because nothing consumed it; ClearTriggers is the
        // Animator's job, and this test drives the instance directly.
        parameters.SetBool("Grounded", true);
        Step(instance, 0.016f);
        Assert.Equal("Jump", instance.CurrentStateName);
    }

    [Fact]
    public void Evaluate_ExitTimeOnly_FiresWhenThePassCompletes() {
        var intro = new AnimationState("Intro", Held("Intro", 0f, 0.5f)) { Wrap = WrapMode.Clamp };
        var loop = new AnimationState("Loop", Held("Loop", 1f));
        intro.AddTransition(new(loop, 0f) { HasExitTime = true, ExitTime = 1f });

        var instance = Instance(new([intro, loop]));

        Step(instance, 0.2f);
        Assert.Equal("Intro", instance.CurrentStateName);

        Step(instance, 0.4f);
        Assert.Equal("Loop", instance.CurrentStateName);
    }

    [Fact]
    public void Evaluate_SelfTransition_IsRefusedUnlessAskedFor() {
        var idle = new AnimationState("Idle", Held("Idle", 0f));
        idle.TransitionTo(idle, 0f).When(AnimationCondition.On(parameters, "Always", AnimationConditionMode.IfNot));

        var instance = Instance(new([idle]));

        Step(instance, 0.5f);
        Assert.Equal(0.5f, instance.NormalizedTime, TestRigs.Tolerance);

        // Had the self-transition fired, playback would have restarted at zero every frame.
        Step(instance, 0.25f);
        Assert.Equal(0.75f, instance.NormalizedTime, TestRigs.Tolerance);
    }

    [Fact]
    public void Evaluate_AnyStateTransition_FiresFromWhereverPlaybackIs() {
        var idle = new AnimationState("Idle", Held("Idle", 0f));
        var walk = new AnimationState("Walk", Held("Walk", 1f));
        var dead = new AnimationState("Dead", Held("Dead", 99f));

        idle.TransitionTo(walk, 0f);

        var machine = new AnimationStateMachine([idle, walk, dead]);
        machine.TransitionFromAnyState(dead, 0f)
            .When(AnimationCondition.On(parameters, "Dead", AnimationConditionMode.If));

        var instance = Instance(machine);

        Step(instance, 0.016f);
        Assert.Equal("Walk", instance.CurrentStateName);

        parameters.SetBool("Dead", true);
        Step(instance, 0.016f);
        Assert.Equal("Dead", instance.CurrentStateName);
    }

    [Fact]
    public void Evaluate_NonInterruptibleTransition_RunsToCompletion() {
        var idle = new AnimationState("Idle", Held("Idle", 0f));
        var walk = new AnimationState("Walk", Held("Walk", 1f));
        var run = new AnimationState("Run", Held("Run", 2f));

        idle.TransitionTo(walk, 1f);
        walk.TransitionTo(run, 0f);

        var instance = Instance(new([idle, walk, run]));

        Step(instance, 0.1f);
        Assert.True(instance.IsTransitioning);
        Assert.Equal("Walk", instance.CurrentStateName);

        // Walk's own transition to Run must not fire while the transition into Walk is running.
        Step(instance, 0.1f);
        Assert.Equal("Walk", instance.CurrentStateName);

        Step(instance, 1f);
        Assert.Equal("Run", instance.CurrentStateName);
    }

    [Fact]
    public void Evaluate_InterruptibleByDestination_CutsTheTransitionShort() {
        var idle = new AnimationState("Idle", Held("Idle", 0f));
        var walk = new AnimationState("Walk", Held("Walk", 1f));
        var run = new AnimationState("Run", Held("Run", 2f));

        idle.AddTransition(new(walk, 1f) { Interruption = TransitionInterruption.Destination });
        walk.TransitionTo(run, 0f);

        var instance = Instance(new([idle, walk, run]));

        Step(instance, 0.1f);
        Assert.Equal("Walk", instance.CurrentStateName);

        Step(instance, 0.1f);
        Assert.Equal("Run", instance.CurrentStateName);
    }

    [Fact]
    public void Play_WithAnOffset_StartsPartWayThroughTheState() {
        var idle = new AnimationState("Idle", Held("Idle", 0f));
        var land = new AnimationState("Land", Held("Land", 1f));

        var instance = Instance(new([idle, land]));
        instance.Play("Land", 0f, 0.4f);

        Assert.Equal("Land", instance.CurrentStateName);
        Assert.Equal(0.4f, instance.NormalizedTime, TestRigs.Tolerance);
    }

    [Fact]
    public void Play_UnknownState_ReportsSoAndChangesNothing() {
        var idle = new AnimationState("Idle", Held("Idle", 0f));
        var instance = Instance(new([idle]));

        Assert.False(instance.Play("Nope"));
        Assert.Equal("Idle", instance.CurrentStateName);
    }

    [Fact]
    public void Evaluate_RepeatedInterruptions_NeverBlendMoreThanTheCap() {
        var states = new List<AnimationState>();

        for (var index = 0; index < 8; index++) {
            states.Add(new($"S{index}", Held($"S{index}", index)));
        }

        for (var index = 0; index < 7; index++) {
            states[index].AddTransition(
                new(states[index + 1], 10f) { Interruption = TransitionInterruption.Destination }
            );
        }

        var instance = Instance(new(states));

        for (var step = 0; step < 8; step++) {
            Step(instance, 0.016f);
            Assert.True(
                instance.ActiveStateCount <= StateMachineInstance.MaxConcurrentStates,
                $"{instance.ActiveStateCount} states blending"
            );
        }
    }

    [Fact]
    public void Constructor_StateAlreadyInAnotherMachine_IsRejected() {
        var shared = new AnimationState("Idle", Held("Idle", 0f));
        _ = new AnimationStateMachine([shared]);

        Assert.Throws<ArgumentException>(() => new AnimationStateMachine([shared]));
    }

    [Fact]
    public void Evaluate_EventsFromABlendingState_CarryTheStatesWeight() {
        var idle = new AnimationState(
            "Idle",
            new ClipMotion(
                AnimationClip.Create(
                    TestRigs.Hold("Idle", "Mid", Vector3.Zero),
                    skeleton,
                    [new("Breathe", 0.5f)]
                )
            )
        );

        var instance = Instance(new([idle]));
        var events = new AnimationEventBuffer();

        Step(instance, 0.6f, events);

        Assert.Equal(1, events.Count);
        Assert.Equal("Breathe", events[0].Event.Name);
        Assert.Equal("Idle", events[0].State);
        Assert.Equal(1f, events[0].Weight, TestRigs.Tolerance);
    }
}
