// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Constraints;
using Vixen.Animation.Ik;
using Vixen.Animation.Motions;
using Vixen.Animation.StateMachine;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Animation.Tests;

/// <summary>
///     The constraint stage. Every behavioural test runs against both arbiters, which is the whole
///     point of there being two.
/// </summary>
public class ConstraintTests {
    const float Step = 1f / 60f;

    readonly Skeleton skeleton = TestRigs.Chain();

    /// <summary>Both arbiters, by name, so the theory data stays serialisable.</summary>
    public static TheoryData<string> Arbiters => ["default", "weighted"];

    static IConstraintArbiter Arbiter(string name) =>
        name == "weighted" ? new WeightedTestArbiter() : DefaultConstraintArbiter.Shared;

    ConstraintStack Stack(string arbiter = "default") => new(skeleton, Arbiter(arbiter));

    /// <summary>One frame: the layers rebuild the pose, then the stage corrects it.</summary>
    /// <remarks>
    ///     ⚠ <b>The reset is not scaffolding, it is the frame.</b> The stage writes corrections into
    ///     the pose in place, and what it corrects is whatever the layers produced this frame — so a
    ///     test that solved twice onto the same buffer would be correcting its own last correction,
    ///     and a goal easing out would never get back to the animated pose because there would not be
    ///     one to get back to. Here the bind pose stands in for the layer mix.
    /// </remarks>
    static void Frame(ConstraintStack stack, SkeletonPose pose, BoneTransform[] model) {
        pose.ResetToBindPose();
        stack.Solve(pose.Bones, model, Step);
    }

    /// <summary>Solves a stack for a while, so anything easing has settled.</summary>
    static Vector3 Settle(ConstraintStack stack, SkeletonPose pose, BoneTransform[] model, int frames = 60) {
        for (var frame = 0; frame < frames; frame++) {
            Frame(stack, pose, model);
        }

        return TestRigs.ModelPositions(pose)[2];
    }

    (SkeletonPose Pose, BoneTransform[] Model) Fresh() =>
        (new SkeletonPose(skeleton), new BoneTransform[skeleton.JointCount]);

    // ---------------------------------------------------------------- goals

    [Theory]
    [MemberData(nameof(Arbiters))]
    public void APositionGoalPutsTheHandWhereItWasAsked(string arbiter) {
        var stack = Stack(arbiter);
        var (pose, model) = Fresh();
        var target = new Vector3(1f, 1f, 0.5f);

        stack.Add(new PositionGoal { Effector = 2, Chain = new(0, 2), Goal = new ModelTestFrame(target) });

        TestRigs.Near(target, Settle(stack, pose, model));
    }

    [Theory]
    [MemberData(nameof(Arbiters))]
    public void AGoalAtHalfWeightGetsHalfwayThere(string arbiter) {
        var stack = Stack(arbiter);
        var (pose, model) = Fresh();
        var target = new Vector3(0.6f, 1.6f, 0f);

        stack.Add(
            new PositionGoal {
                Effector = 2,
                Chain = new(0, 2),
                Goal = new ModelTestFrame(target),
                Weight = 0.5f
            }
        );

        var settled = Settle(stack, pose, model);
        var midpoint = Vector3.Lerp(new(0f, 2f, 0f), target, 0.5f);

        // The bones do not stretch, so the hand lands on the arc through the midpoint rather than on
        // the midpoint itself. What has to hold is that it went half the distance, not that it went
        // to the average of two points.
        Assert.True(
            (settled - midpoint).Length() < 0.06f,
            $"half weight should land near {midpoint}, got {settled}"
        );
    }

    [Theory]
    [MemberData(nameof(Arbiters))]
    public void ARegionGoalIsSatisfiedWhereItStandsAndDoesNotMoveTheArm(string arbiter) {
        var stack = Stack(arbiter);
        var (pose, model) = Fresh();

        var handle = stack.Add(
            new PositionGoal {
                Effector = 2,
                Chain = new(0, 2),
                Goal = new ModelTestFrame(new(0f, 1.7f, 0f)),
                Region = new(0.5f, 0.5f, 0.5f)
            }
        );

        var settled = Settle(stack, pose, model);

        TestRigs.Near(new(0f, 2f, 0f), settled, "the hand is already inside the region");
        Assert.True(handle.Residual.Satisfied, $"residual should be zero inside a region, was {handle.Residual}");
    }

    [Fact]
    public void ADistanceGoalPullsTwoJointsInsideItsInterval() {
        var stack = Stack();
        var (pose, model) = Fresh();

        // ⚠ The other joint is the chain's own root, which the solve rotates but does not move. Pick
        // a joint the chain <em>does</em> move and the two ends chase each other: the target is
        // recomputed from a pose the last solve changed, which is exactly the cyclic case the shipped
        // arbiter says up front that it does not resolve. Worth knowing when authoring one.
        var handle = stack.Add(new DistanceGoal { Effector = 2, Other = 0, Chain = new(0, 2), Max = 1.5f });

        Settle(stack, pose, model);

        var positions = TestRigs.ModelPositions(pose);

        Assert.Equal(1.5f, (positions[2] - positions[0]).Length(), 0.01f);
        Assert.True(handle.Residual.Satisfied, $"it should be inside the interval: {handle.Residual}");
    }

    // ---------------------------------------------------------------- additive

    [Theory]
    [MemberData(nameof(Arbiters))]
    public void TwoAdditiveRecoilsComposeRatherThanAverage(string arbiter) {
        var (pose, model) = Fresh();
        var kick = new Vector3(0f, 0f, -0.1f);

        var one = Recoil(arbiter, kick, 1);
        var two = Recoil(arbiter, kick, 2);

        var single = (Settle(one.Stack, pose, model) - new Vector3(0f, 2f, 0f)).Length();

        (pose, model) = Fresh();

        var pair = (Settle(two.Stack, pose, model) - new Vector3(0f, 2f, 0f)).Length();

        // ⚠ The whole of what "additive" means. Averaging two identical recoils gives one recoil,
        // which is the opposite of what a second shot does.
        Assert.True(
            pair > single * 1.8f,
            $"two recoils should be about twice one: one moved {single:0.####}, two moved {pair:0.####}"
        );
    }

    (ConstraintStack Stack, ConstraintHandle[] Handles) Recoil(string arbiter, Vector3 kick, int count) {
        var stack = Stack(arbiter);
        var handles = new ConstraintHandle[count];

        for (var index = 0; index < count; index++) {
            handles[index] = stack.Add(
                new PositionGoal {
                    Effector = 2,
                    Chain = new(0, 2),
                    Mode = GoalMode.Additive,
                    Offset = kick,
                    EaseIn = 0f
                }
            );
        }

        return (stack, handles);
    }

    [Fact]
    public void AnAdditiveGoalReportsTheOffsetItFailedToApply() {
        var stack = Stack();
        var (pose, model) = Fresh();

        // Ten metres, off a two-metre chain. It cannot possibly land, and the residual is the only
        // way an author finds that out short of watching the arm point uselessly.
        var handle = stack.Add(
            new PositionGoal {
                Effector = 2,
                Chain = new(0, 2),
                Mode = GoalMode.Additive,
                Offset = new(0f, 0f, -10f),
                EaseIn = 0f
            }
        );

        Settle(stack, pose, model);

        Assert.True(handle.Residual.Ran);
        Assert.True(handle.Residual.Magnitude > 5f, $"it fell metres short and should say so: {handle.Residual}");
    }

    // ---------------------------------------------------------------- aim

    [Theory]
    [MemberData(nameof(Arbiters))]
    public void AnAimGoalKeepsItsPointOfAimAtHalfAndTwiceTheAuthoredDistance(string arbiter) {
        const float Authored = 4f;
        const float Deviation = 5f;

        // What the clip was authored to do: miss the centre of the target by this much, sideways.
        var wanted = Authored * MathF.Tan(MathUtil.DegreesToRadians(Deviation));

        foreach (var distance in new[] { Authored * 0.5f, Authored, Authored * 2f }) {
            var stack = Stack(arbiter);
            var (pose, model) = Fresh();

            stack.Add(
                new AimGoal {
                    Effector = 2,
                    Chain = ChainSpec.Single(2),
                    Goal = new ModelTestFrame(new(0f, 2f, distance)),
                    Axis = new(0f, 0f, 1f),
                    Deviation = Quaternion.FromAxisAngle(Vector3.Up, MathUtil.DegreesToRadians(Deviation)),
                    AuthoredDistance = Authored,
                    EaseIn = 0f
                }
            );

            Settle(stack, pose, model);

            pose.ComputeModelSpace(model);

            var facing = Quaternion.Transform(new Vector3(0f, 0f, 1f), model[2].Rotation);
            var lateral = MathF.Abs(distance * facing.X / facing.Z);

            // ⚠ The reason an aim goal stores an angle and a distance rather than a point. Store the
            // point and the same authored intent sprays past the window at twice the range.
            Assert.True(
                MathF.Abs(lateral - wanted) < wanted * 0.02f,
                $"at {distance} m the point of aim should be {wanted:0.####} m off centre, was {lateral:0.####}"
            );
        }
    }

    // ---------------------------------------------------------------- clip tags

    [Theory]
    [MemberData(nameof(Arbiters))]
    public void AHandGoalHoldsThroughABlendBetweenTwoClipsThatBothCarryIt(string arbiter) {
        var target = new Vector3(0.8f, 1.4f, 0f);
        var stack = Stack(arbiter);
        var (pose, model) = Fresh();
        var tags = new ConstraintTagBuffer();

        stack.Tags = tags;

        var first = Track(target);
        var second = Track(target);

        // Every point of the crossfade, not only the ends. A goal that held at 0 and 1 and sagged at
        // 0.5 would pass a two-point test and look like a hand slipping off a ledge.
        for (var step = 0; step <= 10; step++) {
            var fade = step / 10f;

            for (var frame = 0; frame < 40; frame++) {
                tags.Clear();
                tags.Collect(first, 0.5f, 1f - fade);
                tags.Collect(second, 0.5f, fade);
                Frame(stack, pose, model);
            }

            TestRigs.Near(target, TestRigs.ModelPositions(pose)[2], $"at a blend of {fade}");
        }
    }

    [Fact]
    public void AHandGoalWeakensSmoothlyIntoAClipThatDoesNotCarryIt() {
        var target = new Vector3(0.8f, 1.4f, 0f);
        var stack = Stack();
        var (pose, model) = Fresh();
        var tags = new ConstraintTagBuffer();

        stack.Tags = tags;

        var carrying = Track(target);
        var reach = new List<float>();

        for (var step = 0; step <= 20; step++) {
            var fade = step / 20f;

            for (var frame = 0; frame < 40; frame++) {
                tags.Clear();
                tags.Collect(carrying, 0.5f, 1f - fade);
                Frame(stack, pose, model);
            }

            reach.Add((TestRigs.ModelPositions(pose)[2] - target).Length());
        }

        Assert.True(reach[0] < 1e-3f, $"it should start on the goal, was {reach[0]:0.####} away");
        Assert.True(reach[^1] > 0.5f, $"it should end on the animated pose, was {reach[^1]:0.####} away");

        for (var index = 1; index < reach.Count; index++) {
            Assert.True(
                reach[index] >= reach[index - 1] - 1e-4f,
                $"the hand went back towards the goal at step {index}: {reach[index - 1]:0.####} → {reach[index]:0.####}"
            );
        }
    }

    [Fact]
    public void AClipReportsItsTagsThroughTheAnimator() {
        var target = new Vector3(0.5f, 1.5f, 0f);
        var clip = AnimationClip.Create(TestRigs.Hold("Reach", "Root", Vector3.Zero), skeleton, null, null, Track(target));

        var animator = new Animator(skeleton);
        animator.AddLayer("Base", new([new AnimationState("Reach", new ClipMotion(clip))]));

        var stack = new ConstraintStack(skeleton);
        animator.PoseProcessors.Add(stack);

        for (var frame = 0; frame < 60; frame++) {
            animator.Update(Step);
        }

        Assert.Equal(1, animator.Constraints.Count);
        TestRigs.Near(target, TestRigs.ModelPositions(animator.Pose)[2]);
    }

    static ConstraintTrack Track(Vector3 target) =>
        new(
            new ConstraintTag {
                Goal = new PositionGoal {
                    Effector = 2,
                    Chain = new(0, 2),
                    Goal = new ModelTestFrame(target),
                    EaseIn = 0f
                }
            }
        );

    [Fact]
    public void ATagOutsideItsSpanDoesNothingAndOneStraddlingTheLoopPointDoes() {
        var tag = new ConstraintTag {
            Goal = new PositionGoal { Effector = 2 },
            Begin = 0.8f,
            End = 0.2f
        };

        Assert.Equal(0f, tag.Activation(0.5f));
        Assert.Equal(1f, tag.Activation(0.9f));
        Assert.Equal(1f, tag.Activation(0.1f));
    }

    // ---------------------------------------------------------------- continuity

    [Fact]
    public void AGoalThatStopsResolvingEasesOutInsteadOfSnapping() {
        var stack = Stack();
        var (pose, model) = Fresh();
        var prop = new TransformBinding { Transform = new(new Vector3(0.9f, 1.3f, 0f), Quaternion.Identity, Vector3.One) };

        stack.Bindings.Set("held-item", prop);

        stack.Add(
            new PositionGoal {
                Effector = 2,
                Chain = new(0, 2),
                Goal = new EntityFrame("held-item"),
                EaseIn = 0f,
                EaseOut = 0.5f
            }
        );

        var held = Settle(stack, pose, model);

        TestRigs.Near(new(0.9f, 1.3f, 0f), held);

        // The prop despawns. Nothing about the goal changed; what it names simply stopped existing.
        prop.IsValid = false;

        var previous = held;
        var frames = 0;

        for (; frames < 120; frames++) {
            Frame(stack, pose, model);

            var now = TestRigs.ModelPositions(pose)[2];

            // ⚠ The assertion the whole of D18 is for. A snap is one frame moving the hand the whole
            // way; the bar is a third of the total over one sixtieth of a second.
            Assert.True(
                (now - previous).Length() < 0.05f,
                $"the hand jumped {(now - previous).Length():0.####} m in one frame at frame {frames}"
            );

            previous = now;

            if ((now - new Vector3(0f, 2f, 0f)).Length() < 1e-3f) {
                break;
            }
        }

        Assert.True(frames > 10, $"a half-second ease should take about thirty frames, took {frames}");
        Assert.True(frames < 90, $"it should have finished easing out, still going after {frames} frames");
    }

    [Fact]
    public void AGoalThatNeverResolvedDoesNotDragTheLimbAnywhere() {
        var stack = Stack();
        var (pose, model) = Fresh();

        stack.Add(new PositionGoal { Effector = 2, Chain = new(0, 2), Goal = new EntityFrame("not-bound") });

        TestRigs.Near(new(0f, 2f, 0f), Settle(stack, pose, model));
    }

    [Fact]
    public void DisposingAHandleEasesTheGoalOutAndThenForgetsIt() {
        var stack = Stack();
        var (pose, model) = Fresh();

        var handle = stack.Add(
            new PositionGoal {
                Effector = 2,
                Chain = new(0, 2),
                Goal = new ModelTestFrame(new(0.9f, 1.3f, 0f)),
                EaseIn = 0f,
                EaseOut = 0.25f
            }
        );

        Settle(stack, pose, model);
        handle.Dispose();

        Frame(stack, pose, model);
        Assert.True(handle.IsAlive, "one frame after release it should still be easing out");

        Settle(stack, pose, model);

        Assert.False(handle.IsAlive);
        Assert.Equal(0, stack.LastAppliedCount);
        TestRigs.Near(new(0f, 2f, 0f), TestRigs.ModelPositions(pose)[2]);
    }

    [Fact]
    public void ResetForgetsEveryGoalsHistory() {
        var stack = Stack();
        var (pose, model) = Fresh();

        var handle = stack.Add(
            new PositionGoal {
                Effector = 2,
                Chain = new(0, 2),
                Goal = new ModelTestFrame(new(0.9f, 1.3f, 0f)),
                EaseIn = 1f
            }
        );

        for (var frame = 0; frame < 15; frame++) {
            Frame(stack, pose, model);
        }

        var partway = (TestRigs.ModelPositions(pose)[2] - new Vector3(0f, 2f, 0f)).Length();

        Assert.True(partway > 1e-3f, "it should be part of the way in");

        stack.Reset();

        Frame(stack, pose, model);

        var restarted = (TestRigs.ModelPositions(pose)[2] - new Vector3(0f, 2f, 0f)).Length();

        Assert.True(
            restarted < partway,
            $"after a reset the ease starts again: was {partway:0.####} in, restarted at {restarted:0.####}"
        );

        _ = handle;
    }

    // ---------------------------------------------------------------- labels

    [Fact]
    public void SuppressingALabelHandsTheJointBackAndReleasingItTakesItAgain() {
        var stack = Stack();
        var (pose, model) = Fresh();
        var label = Symbol.Intern("look-at");

        stack.Add(
            new PositionGoal {
                Effector = 2,
                Chain = new(0, 2),
                Goal = new ModelTestFrame(new(0.9f, 1.3f, 0f)),
                Label = label,
                EaseIn = 0f,
                EaseOut = 0f
            }
        );

        TestRigs.Near(new(0.9f, 1.3f, 0f), Settle(stack, pose, model));

        stack.Suppress(label, 0f);
        Assert.Equal(0f, stack.Suppression(label));
        TestRigs.Near(new(0f, 2f, 0f), Settle(stack, pose, model), "the gesture system has the arm");

        stack.Release(label);
        TestRigs.Near(new(0.9f, 1.3f, 0f), Settle(stack, pose, model), "and gives it back");
    }

    [Fact]
    public void ActiveWalksOnlyTheHandlesWearingALabel() {
        var stack = Stack();
        var grip = Symbol.Intern("grip");

        stack.Add(new PositionGoal { Effector = 2, Label = grip });
        stack.Add(new PositionGoal { Effector = 1 });
        stack.Add(new OrientationGoal { Effector = 2, Label = grip });

        var labelled = 0;
        var all = 0;

        foreach (var handle in stack.Active(grip)) {
            labelled++;
            Assert.Equal(grip, handle.Goal.Label);
        }

        foreach (var _ in stack.Active()) {
            all++;
        }

        Assert.Equal(2, labelled);
        Assert.Equal(3, all);
    }

    // ---------------------------------------------------------------- priority

    [Theory]
    [MemberData(nameof(Arbiters))]
    public void AHigherPriorityGoalAtFullWeightWinsOutright(string arbiter) {
        var stack = Stack(arbiter);
        var (pose, model) = Fresh();
        var wins = new Vector3(0.9f, 1.3f, 0f);
        var loses = new Vector3(-0.9f, 1.3f, 0f);

        stack.Add(new PositionGoal { Effector = 2, Chain = new(0, 2), Goal = new ModelTestFrame(loses) });

        stack.Add(
            new PositionGoal {
                Effector = 2,
                Chain = new(0, 2),
                Goal = new ModelTestFrame(wins),
                Priority = 1
            }
        );

        var settled = Settle(stack, pose, model);

        // The two goals are 1.8 m apart. Both rules — a share taken off the top, and an exponential
        // multiplier — have to land essentially on the winner, and averaging would land at the origin.
        Assert.True((settled - wins).Length() < 0.02f, $"the higher priority should win, landed at {settled}");
    }

    // ---------------------------------------------------------------- the frame

    [Fact]
    public void AWorldFrameIsBroughtIntoTheCharactersOwnSpace() {
        var stack = Stack();
        var (pose, model) = Fresh();

        // The character is standing ten metres away, turned around.
        stack.WorldTransform = new(
            new Vector3(10f, 0f, 0f),
            Quaternion.FromAxisAngle(Vector3.Up, MathUtil.Pi),
            Vector3.One
        );

        stack.Add(
            new PositionGoal {
                Effector = 2,
                Chain = new(0, 2),
                Goal = new WorldFrame(new Vector3(10.9f, 1.3f, 0f))
            }
        );

        // A metre to the character's own left, because it is facing the other way.
        TestRigs.Near(new(-0.9f, 1.3f, 0f), Settle(stack, pose, model));
    }

    [Fact]
    public void ASocketThatIsNotThereFailsRatherThanResolvingToTheProp() {
        var stack = Stack();
        var (pose, model) = Fresh();

        stack.Bindings.Set(
            "held-item",
            new ComputedTestBinding(
                socket => socket == Symbol.Intern("grip")
                    ? new BoneTransform(new Vector3(0.9f, 1.3f, 0f), Quaternion.Identity, Vector3.One)
                    : null
            )
        );

        stack.Add(
            new PositionGoal {
                Effector = 2,
                Chain = new(0, 2),
                Goal = new SocketFrame("held-item", "muzzle")
            }
        );

        TestRigs.Near(new(0f, 2f, 0f), Settle(stack, pose, model), "a missing socket is not the prop's origin");
    }

    [Fact]
    public void AProvidedFrameLastsExactlyOneSolve() {
        var stack = Stack();
        var (pose, model) = Fresh();

        stack.Add(
            new PositionGoal {
                Effector = 2,
                Chain = new(0, 2),
                Goal = new ProvidedFrame("ledge"),
                EaseIn = 0f,
                EaseOut = 0f
            }
        );

        for (var frame = 0; frame < 10; frame++) {
            stack.Bindings.Provide("ledge", new BoneTransform(new(0.9f, 1.3f, 0f), Quaternion.Identity, Vector3.One));
            Frame(stack, pose, model);
        }

        TestRigs.Near(new(0.9f, 1.3f, 0f), TestRigs.ModelPositions(pose)[2]);

        // The provider stops. Nothing unbound anything; the frame simply was not written.
        for (var frame = 0; frame < 10; frame++) {
            Frame(stack, pose, model);
        }

        TestRigs.Near(new(0f, 2f, 0f), TestRigs.ModelPositions(pose)[2]);
    }

    // ---------------------------------------------------------------- seams

    [Fact]
    public void EveryChainGoesThroughTheSolverSeamAndNothingReachesPastIt() {
        var solver = new TeleportingTestSolver();
        var stack = new ConstraintStack(skeleton, solver: solver);
        var (pose, model) = Fresh();

        stack.Add(
            new PositionGoal {
                Effector = 2,
                Chain = new(0, 2),
                Goal = new ModelTestFrame(new(5f, 5f, 5f)),
                EaseIn = 0f
            }
        );

        Frame(stack, pose, model);

        Assert.True(solver.Calls > 0);

        // A solver that simply moves the joint reaches a target no chain could, which is the proof
        // that nothing above the seam is assuming an arm.
        TestRigs.Near(new(5f, 5f, 5f), TestRigs.ModelPositions(pose)[2]);
    }

    // ---------------------------------------------------------------- cost

    [Fact]
    public void TheSolveAllocatesNothingOnceWarm() {
        var stack = Stack();
        var (pose, model) = Fresh();
        var tags = new ConstraintTagBuffer();
        var track = Track(new(0.6f, 1.5f, 0f));

        stack.Tags = tags;
        stack.Bindings.Set("held-item", new TransformBinding().Socket("grip", BoneTransform.Identity));

        stack.Add(
            new PositionGoal {
                Effector = 2,
                Chain = new(0, 2),
                Goal = new SocketFrame("held-item", "grip"),
                EaseIn = 0f
            }
        );

        stack.Add(new OrientationGoal { Effector = 2, Chain = new(0, 2), Goal = new ModelTestFrame(Vector3.Zero) });

        for (var frame = 0; frame < 200; frame++) {
            tags.Clear();
            tags.Collect(track, 0.5f, 1f);
            stack.Bindings.Provide("ledge", BoneTransform.Identity);
            Frame(stack, pose, model);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var frame = 0; frame < 200; frame++) {
            tags.Clear();
            tags.Collect(track, 0.5f, 1f);
            stack.Bindings.Provide("ledge", BoneTransform.Identity);
            Frame(stack, pose, model);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated == 0, $"two hundred solves allocated {allocated} bytes");
    }

    // ---------------------------------------------------------------- foot placement

    [Fact]
    public void FootPlacementOverTheStageMatchesTheStandaloneSolver() {
        var rig = Legs();
        var chains = new FootChain[] {
            new(rig.IndexOf("HipL"), rig.IndexOf("KneeL"), rig.IndexOf("AnkleL"), new(-0.1f, 0.5f, 3f), 0.05f),
            new(rig.IndexOf("HipR"), rig.IndexOf("KneeR"), rig.IndexOf("AnkleR"), new(0.1f, 0.5f, 3f), 0.05f)
        };

        ReadOnlySpan<GroundContact> contacts = [
            new(true, new(-0.1f, 0f, 0f), Vector3.Up),
            new(true, new(0.1f, -0.12f, 0f), Vector3.Normalize(new(0.2f, 1f, 0f)))
        ];

        var standalonePose = new SkeletonPose(rig);
        var standaloneModel = new BoneTransform[rig.JointCount];
        var standalone = new FootPlacement(rig.IndexOf("Pelvis"), chains);
        var drop = standalone.Solve(rig, standalonePose.Bones, standaloneModel, contacts);

        var stagedPose = new SkeletonPose(rig);
        var stagedModel = new BoneTransform[rig.JointCount];
        var stack = new ConstraintStack(rig);
        var preset = new FootPlacementPreset(stack, rig.IndexOf("Pelvis"), chains);

        var stagedDrop = preset.Update(stagedPose.Bones, stagedModel, contacts);
        stack.Solve(stagedPose.Bones, stagedModel, Step);

        Assert.Equal(drop, stagedDrop, 1e-5f);

        var expected = TestRigs.ModelPositions(standalonePose);
        var actual = TestRigs.ModelPositions(stagedPose);

        for (var joint = 0; joint < rig.JointCount; joint++) {
            Assert.True(
                (expected[joint] - actual[joint]).Length() < 1e-3f,
                $"{rig.NameOf(joint)}: standalone {expected[joint]}, staged {actual[joint]}"
            );
        }
    }

    static Skeleton Legs() =>
        Skeleton.Create(
            TestRigs.Build(
                "Legs",
                ("Pelvis", -1, new Vector3(0f, 1f, 0f)),
                ("HipL", 0, new Vector3(-0.1f, -0.05f, 0f)),
                ("KneeL", 1, new Vector3(0f, -0.45f, 0f)),
                ("AnkleL", 2, new Vector3(0f, -0.45f, 0f)),
                ("HipR", 0, new Vector3(0.1f, -0.05f, 0f)),
                ("KneeR", 4, new Vector3(0f, -0.45f, 0f)),
                ("AnkleR", 5, new Vector3(0f, -0.45f, 0f))
            )
        );

    // ---------------------------------------------------------------- degenerate input

    [Fact]
    public void AGoalOnAJointThatIsNotThereIsIgnoredRatherThanThrowing() {
        var stack = Stack();
        var (pose, model) = Fresh();

        stack.Add(new PositionGoal { Effector = 99, Chain = new(90, 99), Goal = new ModelTestFrame(Vector3.Zero) });
        stack.Add(new DistanceGoal { Effector = 2, Other = 99, Chain = new(0, 2), Min = 1f });

        Frame(stack, pose, model);

        TestRigs.Near(new(0f, 2f, 0f), TestRigs.ModelPositions(pose)[2]);
    }
}
