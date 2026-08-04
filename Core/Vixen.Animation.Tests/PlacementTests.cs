// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Constraints;
using Vixen.Core.Mathematics;
using Vixen.Engine.Cameras;
using Xunit;

namespace Vixen.Animation.Tests;

/// <summary>
///     The two bodies that are not skeletons — a character's placement and a camera — and the governor
///     that decides how much of any of it a frame can afford.
/// </summary>
public class PlacementTests {
    const float Step = 1f / 60f;

    static Vector3[] Heights => [new(0.85f, 0.8f, 0.85f), Vector3.One, new(1.2f, 1.3f, 1.2f)];

    // ---------------------------------------------------------------- root placement

    [Fact]
    public void AReachTooFarAsksTheCharacterToStandSomewhereElse() {
        var body = new TestBody(Vector3.One);
        var stack = new ConstraintStack(body.Skeleton);

        // Well out of the arm's reach, so an honest answer is "move, then stretch".
        stack.Add(
            new PositionGoal {
                Effector = body.Root,
                Chain = ChainSpec.Single(body.Root),
                Goal = new WorldFrame(new Vector3(0.9f, 0f, 0f)),
                Label = ConstraintLabels.Root,
                EaseIn = 0f
            }
        );

        Settle(stack, body);

        Assert.True(stack.HasRootSuggestion);
        TestRigs.Near(new(0.9f, 0f, 0f), stack.RootSuggestion.Translation);
    }

    /// <summary>⚠ A root goal moves the character, and must not also move its limbs.</summary>
    [Fact]
    public void ARootGoalIsExcludedFromThePoseSolve() {
        var body = new TestBody(Vector3.One);
        var stack = new ConstraintStack(body.Skeleton);

        stack.Add(
            new PositionGoal {
                Effector = body.Wrist,
                Chain = new(body.Shoulder, body.Wrist),
                Goal = new WorldFrame(new Vector3(2f, 0f, 0f)),
                Label = ConstraintLabels.Root,
                EaseIn = 0f
            }
        );

        var before = TestRigs.ModelPositions(body.Pose)[body.Wrist];

        Settle(stack, body);

        TestRigs.Near(before, TestRigs.ModelPositions(body.Pose)[body.Wrist], "the arm should not have moved");
        Assert.True(stack.HasRootSuggestion, "and the character should have been asked to");
        Assert.Equal(0, stack.LastAppliedCount);
    }

    /// <summary>
    ///     ⚠ The whole reason the placement pass runs first: it changes where everything else is.
    /// </summary>
    [Fact]
    public void ThePoseSolveSeesTheSuggestedPlacementRatherThanWhereTheCharacterStands() {
        var handle = new Vector3(0.75f, 1.2f, 0f);

        var moving = Reaching(handle, withPlacement: true);
        var rooted = Reaching(handle, withPlacement: false);

        // The character that is allowed to step gets its hand much closer to the handle than the one
        // that has to stretch from where it stands.
        Assert.True(
            moving < rooted * 0.5f,
            $"stepping should beat stretching: {moving:0.####} m short against {rooted:0.####} m"
        );
    }

    static float Reaching(Vector3 handle, bool withPlacement) {
        var body = new TestBody(Vector3.One);
        var stack = new ConstraintStack(body.Skeleton);

        if (withPlacement) {
            stack.Add(
                new PositionGoal {
                    Effector = body.Root,
                    Chain = ChainSpec.Single(body.Root),
                    Goal = new WorldFrame(handle),

                    // A region, because a character standing *at* a door handle is standing inside the
                    // door. Half a metre of slack is where a person stops walking and starts reaching.
                    Region = new(0.5f, 5f, 0.5f),
                    Label = ConstraintLabels.Root,
                    EaseIn = 0f
                }
            );
        }

        stack.Add(
            new PositionGoal {
                Effector = body.Wrist,
                Chain = new(body.Shoulder, body.Wrist),
                Goal = new WorldFrame(handle),
                EaseIn = 0f
            }
        );

        Settle(stack, body);

        // Measured in the world, against where the character is being asked to stand.
        var placed = BoneTransform.Concatenate(stack.RootSuggestion, stack.WorldTransform);
        var hand = BoneTransform.Concatenate(
            new BoneTransform(TestRigs.ModelPositions(body.Pose)[body.Wrist], Quaternion.Identity, Vector3.One),
            placed
        ).Translation;

        return (hand - handle).Length();
    }

    // ---------------------------------------------------------------- the camera

    /// <summary>
    ///     A shot composed against one body holds its framing across the body range, and the camera
    ///     stays inside the volume it is allowed.
    /// </summary>
    [Fact]
    public void AShotHoldsItsFramingAcrossTheBodyRangeWithoutLeavingItsVolume() {
        // Composed once: the head in the upper third, slightly left of centre.
        var composed = new Vector2(-0.2f, 0.35f);
        // The interior of a room. The camera may go anywhere inside it, and the framing solution for
        // every body in the range is inside it — which is the case where a bound costs nothing.
        var allowed = new Vector3(0.5f, 1.5f, 3f);
        var slack = new Vector3(1.5f, 1.2f, 1.5f);

        foreach (var scale in Heights) {
            var body = new TestBody(scale);
            var camera = new CameraConstraints();

            camera.Add(
                new PositionGoal {
                    Effector = 0,
                    Goal = new ScreenFrame(new JointFrame(body.Head), composed),
                    EaseIn = 0f
                }
            );

            // A region goal in world space, at a priority the framing cannot outvote.
            camera.Add(
                new PositionGoal {
                    Effector = 0,
                    Goal = new WorldFrame(allowed),
                    Region = slack,
                    Priority = 1,
                    EaseIn = 0f
                }
            );

            var shot = new CameraView(
                new BoneTransform(new Vector3(0.5f, 1.5f, 3f), Quaternion.Identity, Vector3.One),
                CameraLens.Default,
                16f / 9f
            );

            var placed = shot;

            // Twice, because the correction is a placement rather than a velocity: a second pass on
            // the corrected shot has to be a no-op, or the camera would creep every frame.
            for (var pass = 0; pass < 2; pass++) {
                placed = camera.Solve(placed, body.Subject()).View;
            }

            Assert.True(
                CameraFraming.Project(
                    body.World(body.Head),
                    placed.Transform.Translation,
                    placed.Transform.Rotation,
                    placed.Lens,
                    placed.Aspect,
                    out var at,
                    out _
                ),
                $"the head should be in front of the camera at {scale}"
            );

            Assert.Equal(composed.X, at.X, 2e-2f);
            Assert.Equal(composed.Y, at.Y, 2e-2f);

            var out1 = placed.Transform.Translation - allowed;

            Assert.True(
                MathF.Abs(out1.X) <= slack.X + 1e-3f
                && MathF.Abs(out1.Y) <= slack.Y + 1e-3f
                && MathF.Abs(out1.Z) <= slack.Z + 1e-3f,
                $"the camera left its volume at {scale}: {placed.Transform.Translation}"
            );
        }
    }

    [Fact]
    public void AVolumeThatCannotBeSatisfiedTogetherWithTheFramingKeepsTheVolume() {
        var body = new TestBody(Vector3.One);
        var camera = new CameraConstraints();
        var pinned = new Vector3(2.5f, 1.6f, 3f);

        camera.Add(
            new PositionGoal {
                Effector = 0,
                Goal = new ScreenFrame(new JointFrame(body.Head), new Vector2(0.9f, 0.9f)),
                EaseIn = 0f
            }
        );

        camera.Add(
            new PositionGoal { Effector = 0, Goal = new WorldFrame(pinned), Priority = 1, EaseIn = 0f }
        );

        var shot = new CameraView(new BoneTransform(pinned, Quaternion.Identity, Vector3.One), CameraLens.Default, 16f / 9f);
        var placed = camera.Solve(shot, body.Subject()).View;

        // ⚠ The framing loses, because the volume is what stops the lens going through the roof. That
        // is priority doing its job and not the camera failing to compose.
        TestRigs.Near(pinned, placed.Transform.Translation);
    }

    [Fact]
    public void AScreenFrameOnAPoseSolveFailsRatherThanResolvingToSomethingMeaningless() {
        var body = new TestBody(Vector3.One);

        var frame = new ScreenFrame(new JointFrame(body.Head), Vector2.Zero);

        Assert.False(
            frame.TryResolve(
                new() { Skeleton = body.Skeleton, Model = body.Model, Bindings = new() },
                out _
            )
        );
    }

    [Fact]
    public void ACameraWithNoGoalsLeavesTheShotAsTheDirectorComposedIt() {
        var body = new TestBody(Vector3.One);
        var shot = new CameraView(
            new BoneTransform(new Vector3(1f, 2f, 3f), Quaternion.Identity, Vector3.One),
            CameraLens.Default,
            16f / 9f
        );

        var result = new CameraConstraints().Solve(shot, body.Subject());

        Assert.Equal(0, result.Applied);
        TestRigs.Near(shot.Transform.Translation, result.View.Transform.Translation);
    }

    // ---------------------------------------------------------------- the rate knob

    [Fact]
    public void AHeldFrameReAppliesTheLastCorrectionRatherThanFreezingTheLimb() {
        var body = new TestBody(Vector3.One);
        var stack = new ConstraintStack(body.Skeleton) { SolveEvery = 3 };
        var target = new Vector3(0.35f, 1.2f, 0.2f);

        stack.Add(
            new PositionGoal {
                Effector = body.Wrist,
                Chain = new(body.Shoulder, body.Wrist),
                Goal = new WorldFrame(target),
                EaseIn = 0f
            }
        );

        // Solved frames, so there is a correction to hold.
        for (var frame = 0; frame < 10; frame++) {
            body.Pose.ResetToBindPose();
            stack.Solve(body.Pose.Bones, body.Model, Step);
        }

        var solved = TestRigs.ModelPositions(body.Pose)[body.Wrist];

        Assert.False(stack.WasHeld);
        TestRigs.Near(target, solved);

        // Now a held frame, on a body the animation has moved underneath.
        body.Pose.ResetToBindPose();
        body.Pose[body.Shoulder] = new(
            body.Pose[body.Shoulder].Translation,
            Quaternion.FromAxisAngle(Vector3.UnitZ, 0.25f),
            Vector3.One
        );

        var animated = TestRigs.ModelPositions(body.Pose)[body.Wrist];

        stack.Solve(body.Pose.Bones, body.Model, Step);

        Assert.True(stack.WasHeld);

        var kept = TestRigs.ModelPositions(body.Pose)[body.Wrist];

        // ⚠ Not the pose the last solve produced — the character is still animating, and writing that
        // back would freeze the limb. The *difference* is what carries over, so the hand is still
        // corrected and still moving with the shoulder.
        Assert.True((kept - animated).Length() > 0.1f, "the correction should still be applied");
        Assert.True((kept - solved).Length() > 1e-3f, "and it should not be a frozen copy of the last pose");
    }

    [Fact]
    public void SolvingEveryFrameIsTheDefaultAndHoldsNothing() {
        var body = new TestBody(Vector3.One);
        var stack = new ConstraintStack(body.Skeleton);

        stack.Add(new PositionGoal { Effector = body.Wrist, Chain = new(body.Shoulder, body.Wrist), Goal = new WorldFrame(Vector3.Zero) });

        Assert.Equal(1, stack.SolveEvery);

        for (var frame = 0; frame < 5; frame++) {
            body.Pose.ResetToBindPose();
            stack.Solve(body.Pose.Bones, body.Model, Step);
            Assert.False(stack.WasHeld);
        }
    }

    // ---------------------------------------------------------------- the governor

    /// <summary>A hundred constrained characters inside a stated budget, and a report that says how.</summary>
    [Fact]
    public void AHundredCharactersFitABudgetAndTheReportSaysWhatItCost() {
        var stacks = Crowd(100, goals: 4);
        var governor = new ConstraintGovernor { Budget = 150f };

        var report = governor.Plan(stacks);

        Assert.Equal(100, report.Characters);
        Assert.True(report.WithinBudget, report.ToString());
        Assert.Equal(0, report.AtFloor);

        // Four goals each is four hundred a frame at full rate, and the budget is a hundred and fifty:
        // the nearest characters keep everything and the rest go down the ladder.
        Assert.True(report.Full is > 0 and < 100, $"some but not all should be at full rate, {report.Full} were");
        Assert.True(report.Reduced > 0);

        var spent = 0f;

        foreach (var stack in stacks) {
            spent += stack.EstimatedCost / (float)stack.SolveEvery;
        }

        Assert.Equal(spent, report.Estimated, 1e-3f);
    }

    /// <summary>⚠ The ladder is bounded, so the governor says so rather than inventing a rung.</summary>
    [Fact]
    public void AGovernorThatRunsOutOfLadderNamesWhatItCouldNotFit() {
        var stacks = Crowd(100, goals: 8);
        var report = new ConstraintGovernor { Budget = 20f }.Plan(stacks);

        Assert.False(report.WithinBudget);
        Assert.True(report.AtFloor > 0);
        Assert.True(report.Shortfall > 0f);

        var message = report.ToString();

        Assert.Contains("at the floor", message, StringComparison.Ordinal);
        Assert.Contains("bounded", message, StringComparison.Ordinal);
        Assert.Contains("100 constrained characters", message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMostImportantCharactersAreTheOnesThatKeepASolveEveryFrame() {
        var stacks = Crowd(20, goals: 4);
        var governor = new ConstraintGovernor { Budget = 30f };

        governor.Plan(stacks);

        // Importance descends with the index in Crowd, so the first few should be the untouched ones.
        Assert.Equal(1, stacks[0].SolveEvery);
        Assert.Equal(0, stacks[0].Lod);
        Assert.True(stacks[^1].SolveEvery > 1, "the least important should have been reduced");
        Assert.True(stacks[^1].Lod > 0);
    }

    [Fact]
    public void PlanningAllocatesNothingOnceWarm() {
        var stacks = Crowd(100, goals: 4);
        var governor = new ConstraintGovernor { Budget = 150f };

        for (var frame = 0; frame < 20; frame++) {
            governor.Plan(stacks);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var frame = 0; frame < 100; frame++) {
            governor.Plan(stacks);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated == 0, $"a hundred plans allocated {allocated} bytes");
    }

    [Fact]
    public void AnEmptyWorldPlansNothingAndSaysSo() {
        var report = new ConstraintGovernor().Plan([]);

        Assert.Equal(0, report.Characters);
        Assert.True(report.WithinBudget);
    }

    static ConstraintStack[] Crowd(int characters, int goals) {
        var body = new TestBody(Vector3.One);
        var stacks = new ConstraintStack[characters];

        for (var index = 0; index < characters; index++) {
            var stack = new ConstraintStack(body.Skeleton) { Importance = characters - index };

            for (var goal = 0; goal < goals; goal++) {
                stack.Add(
                    new PositionGoal {
                        Effector = body.Wrist,
                        Chain = new(body.Shoulder, body.Wrist),
                        Goal = new WorldFrame(new Vector3(goal, 1f, 0f))
                    }
                );
            }

            stacks[index] = stack;
        }

        return stacks;
    }

    // ---------------------------------------------------------------- the rig

    static void Settle(ConstraintStack stack, TestBody body, int frames = 40) {
        for (var frame = 0; frame < frames; frame++) {
            body.Pose.ResetToBindPose();
            stack.Solve(body.Pose.Bones, body.Model, Step);
        }
    }

    /// <summary>A torso, an arm and a head, at whatever proportions.</summary>
    sealed class TestBody {
        public TestBody(Vector3 scale) {
            Skeleton = Skeleton.Create(
                TestRigs.Build(
                    "Body",
                    ("Root", -1, Vector3.Zero),
                    ("Spine", 0, new Vector3(0f, 1f, 0f) * scale),
                    ("Head", 1, new Vector3(0f, 0.55f, 0f) * scale),
                    ("Shoulder", 1, new Vector3(0.2f, 0.35f, 0f) * scale),
                    ("Elbow", 3, new Vector3(0f, -0.3f, 0f) * scale),
                    ("Wrist", 4, new Vector3(0f, -0.28f, 0f) * scale)
                )
            );

            Root = 0;
            Head = Skeleton.IndexOf("Head");
            Shoulder = Skeleton.IndexOf("Shoulder");
            Wrist = Skeleton.IndexOf("Wrist");

            Pose = new(Skeleton);
            Model = new BoneTransform[Skeleton.JointCount];

            Pose.ComputeModelSpace(Model);
        }

        public Skeleton Skeleton { get; }

        public SkeletonPose Pose { get; }

        public BoneTransform[] Model { get; }

        public int Root { get; }

        public int Head { get; }

        public int Shoulder { get; }

        public int Wrist { get; }

        public CameraSubject Subject() => new() { Skeleton = Skeleton, Model = Model };

        public Vector3 World(int joint) => Model[joint].Translation;
    }
}
