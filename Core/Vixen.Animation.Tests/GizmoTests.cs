// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Constraints;
using Vixen.Animation.Ecs;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Diagnostics;
using Vixen.Engine.Frames;
using Xunit;

namespace Vixen.Animation.Tests;

/// <summary>What an author sees when the constraints are switched on in the viewport.</summary>
public class GizmoTests {
    readonly Skeleton skeleton = TestRigs.Chain();

    /// <summary>
    ///     ⚠ <b>The one line the whole pass exists for</b>: from where the effector ended up to where
    ///     it was wanted. A goal that cannot be reached draws it long; a goal that lands draws it
    ///     short, and both are visible without reading a number.
    /// </summary>
    [Fact]
    public void TheMissIsDrawnFromTheEffectorToWhereTheGoalWanted() {
        var draw = new DebugDraw();
        var (stack, pose) = Solved(new Vector3(0f, 5f, 0f));

        ConstraintGizmos.Draw(draw, stack, Model(pose));

        var tip = TestRigs.ModelPositions(pose)[2];
        var line = Assert.Single(
            draw.Lines.ToArray(),
            entry => Close(entry.From, tip) && Close(entry.To, new Vector3(0f, 5f, 0f))
        );

        // A two-metre chain asked for five metres: three metres out, so the colour is fully saturated
        // against any sane tolerance.
        Assert.True(line.Colour.R > line.Colour.G, "an unreachable goal reads as red");
    }

    /// <summary>A goal that lands draws the same line green, and the two are told apart at a glance.</summary>
    [Fact]
    public void AGoalThatLandsIsDrawnGreenAndOneThatMissesRed() {
        // ⚠ Chains off, or the grading is read off the chain links: they are drawn amber, which shares
        // its blue channel with the graded scale and would make every character look like a failure.
        var quiet = new ConstraintGizmoStyle { Chains = false, Readout = false };

        var reachable = new DebugDraw();
        var (near, nearPose) = Solved(new Vector3(0.8f, 1.6f, 0f));

        ConstraintGizmos.Draw(reachable, near, Model(nearPose), quiet);

        var missed = new DebugDraw();
        var (far, farPose) = Solved(new Vector3(0f, 5f, 0f));

        ConstraintGizmos.Draw(missed, far, Model(farPose), quiet);

        Assert.True(Worst(reachable) < Worst(missed), "the reachable goal grades better than the unreachable one");
        Assert.True(Worst(reachable) < 0.5f, "a goal that landed is on the green half of the scale");
    }

    /// <summary>
    ///     ⚠ <b>A goal that never resolved is not a goal that landed</b>, and a residual of zero says
    ///     both. The unresolved one is drawn grey — no colour on the scale at all — because an author
    ///     told "satisfied" about a binding that is missing will look everywhere but at the binding.
    /// </summary>
    [Fact]
    public void AGoalThatNeverResolvedIsGreyRatherThanGreen() {
        var draw = new DebugDraw();
        var stack = new ConstraintStack(skeleton);
        var pose = new SkeletonPose(skeleton);

        // An entity frame with nothing bound to the slot: the ordinary case for a goal whose other
        // party has not spawned yet.
        stack.Add(
            new PositionGoal {
                Effector = 2,
                Chain = new(0, 2),
                Goal = new EntityFrame("prop")
            }
        );

        stack.Solve(pose.Bones, 1f / 60f);
        ConstraintGizmos.Draw(draw, stack, Model(pose));

        // Nothing resolved, so there is nothing in LastSolved and no miss line at all — the effector
        // cross and the axes are all there is to draw.
        Assert.Empty(stack.LastSolved.ToArray());
        Assert.DoesNotContain(draw.Lines.ToArray(), static entry => entry.Colour.G > 0.8f && entry.Colour.R < 0.3f);
    }

    /// <summary>The joints the solver was allowed to move, so "why did the spine turn" is answerable.</summary>
    [Fact]
    public void TheChainIsDrawnAlongTheJointsTheGoalMayMove() {
        var draw = new DebugDraw();
        var (stack, pose) = Solved(new Vector3(0.8f, 1.6f, 0f));

        ConstraintGizmos.Draw(draw, stack, Model(pose));

        var model = TestRigs.ModelPositions(pose);
        var links = draw.Lines.ToArray().Count(
            entry => (Close(entry.From, model[2]) && Close(entry.To, model[1]))
                || (Close(entry.From, model[1]) && Close(entry.To, model[0]))
        );

        Assert.Equal(2, links);
    }

    /// <summary>A single-joint goal has no chain to draw, and drawing one would be a lie about reach.</summary>
    [Fact]
    public void AGoalWithNoChainDrawsNoChain() {
        var draw = new DebugDraw();
        var stack = new ConstraintStack(skeleton);
        var pose = new SkeletonPose(skeleton);

        stack.Add(new PositionGoal { Effector = 2, Goal = new WorldFrame(new Vector3(0f, 1.9f, 0f)) });
        stack.Solve(pose.Bones, 1f / 60f);

        ConstraintGizmos.Draw(draw, stack, Model(pose));

        var model = TestRigs.ModelPositions(pose);

        Assert.DoesNotContain(
            draw.Lines.ToArray(),
            entry => Close(entry.From, model[2]) && Close(entry.To, model[1])
        );
    }

    /// <summary>Switching a part off switches it off, so a busy viewport can be quietened.</summary>
    [Fact]
    public void TheStyleTurnsPartsOff() {
        var (stack, pose) = Solved(new Vector3(0.8f, 1.6f, 0f));

        var everything = new DebugDraw();
        ConstraintGizmos.Draw(everything, stack, Model(pose));

        var quiet = new DebugDraw();

        ConstraintGizmos.Draw(
            quiet,
            stack,
            Model(pose),
            new ConstraintGizmoStyle { Chains = false, Readout = false }
        );

        Assert.True(quiet.Lines.Length < everything.Lines.Length);
        Assert.Empty(quiet.Texts.ToArray());
        Assert.NotEmpty(everything.Texts.ToArray());
    }

    /// <summary>The readout says which goal and how far off, in units somebody can act on.</summary>
    [Fact]
    public void TheReadoutNamesTheGoalAndSaysHowFarOff() {
        var draw = new DebugDraw();
        var (stack, pose) = Solved(new Vector3(0f, 5f, 0f), label: "right hand");

        ConstraintGizmos.Draw(draw, stack, Model(pose));

        var text = Assert.Single(draw.Texts.ToArray());

        Assert.Contains("right hand", text.Text, StringComparison.Ordinal);
        Assert.Contains("cm", text.Text, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>Everything is drawn where the character is standing, not at the model origin.</b> A
    ///     gizmo pass in model space puts every character's gizmos on top of each other at the world
    ///     origin, which is the failure that makes the whole feature useless in a populated scene.
    /// </summary>
    [Fact]
    public void TheGizmosFollowTheCharacterRatherThanSittingAtTheOrigin() {
        var draw = new DebugDraw();
        var (stack, pose) = Solved(new Vector3(0f, 1.9f, 0f));

        stack.WorldTransform = new BoneTransform(new Vector3(10f, 0f, -4f), Quaternion.Identity, Vector3.One);
        ConstraintGizmos.Draw(draw, stack, Model(pose));

        Assert.All(
            draw.Lines.ToArray(),
            static entry => Assert.True(entry.From.X > 8f, $"drawn at {entry.From}, which is not where the character is")
        );
    }

    /// <summary>Every proxy shape on a body, for the shape editor rather than for one goal.</summary>
    [Fact]
    public void EveryProxyShapeCanBeDrawnAtOnce() {
        var draw = new DebugDraw();
        var pose = new SkeletonPose(skeleton);

        var shapes = new ProxyShapes(
            ProxyShapeSet.Of(
                "body",
                null,
                new ProxyShape {
                    Name = Symbol.Intern("belly"),
                    Kind = ShapeKind.Sphere,
                    Joint = 1,
                    Dimensions = ShapeParams.Sphere(0.2f)
                },
                new ProxyShape {
                    Name = Symbol.Intern("torso"),
                    Kind = ShapeKind.Capsule,
                    Joint = 1,
                    Dimensions = ShapeParams.Capsule(0.15f, 0.3f)
                }
            )
        );

        ConstraintGizmos.DrawShapes(draw, shapes, Model(pose), BoneTransform.Identity);

        Assert.NotEmpty(draw.Lines.ToArray());
    }

    /// <summary>
    ///     ⚠ <b>Off by default and one character at a time.</b> A scene of thirty constrained
    ///     characters drawn at once is a thousand lines and nothing legible, so a pass that could not
    ///     be narrowed would be one nobody switches on twice.
    /// </summary>
    [Fact]
    public void TheSystemDrawsNothingUntilItIsAskedAndThenOnlyWhoItIsAskedAbout() {
        using var world = new World(nameof(TheSystemDrawsNothingUntilItIsAskedAndThenOnlyWhoItIsAskedAbout));

        var draw = new DebugDraw();
        var system = new ConstraintGizmoSystem(draw);

        var first = Animated(new Vector3(0f, 5f, 0f));
        var second = Animated(new Vector3(0f, 5f, 0f));

        var one = world.Create(new AnimatorComponent { Value = first });
        world.Create(new AnimatorComponent { Value = second });

        // Solved for real first, so what is drawn below is a solve's answer and not an empty pass.
        new AnimationSystem().Run(world, 1f / 60f);

        system.Run(world);

        Assert.Equal(0, system.LastDrawnCount);
        Assert.Empty(draw.Lines.ToArray());

        system.Enabled = true;
        system.Run(world);

        Assert.Equal(2, system.LastDrawnCount);
        Assert.NotEmpty(draw.Lines.ToArray());

        var both = draw.Lines.Length;

        draw.Clear();
        system.Only = one;
        system.Run(world);

        Assert.Equal(1, system.LastDrawnCount);
        Assert.True(draw.Lines.Length < both, "narrowing to one character draws less than two do");
    }

    /// <summary>
    ///     ⚠ <b>Animation had no one-line registration and physics has had one for ages.</b>
    ///     `EngineLoop` cannot include these in its default set — the dependency only runs one way,
    ///     so the engine has no name for an animator — which left every game having to know the
    ///     passes exist and what order they go in.
    /// </summary>
    [Fact]
    public void ALoopRegistersTheAnimationPassesInOneLine() {
        using var loop = new EngineLoop();

        loop.AddAnimation();

        var gizmos = loop.AddConstraintGizmos(new DebugDraw());

        // ⚠ Handed back rather than chained, because registering it is not the interesting half:
        // a scene of thirty constrained characters drawn at once is a thousand lines and nothing
        // legible, so getting hold of it to narrow it is the point.
        Assert.False(gizmos.Enabled);
        Assert.Null(gizmos.Only);

        var animator = Animated(new Vector3(0f, 1.9f, 0f));
        var entity = loop.World.Create(new AnimatorComponent { Value = animator });

        gizmos.Enabled = true;
        gizmos.Only = entity;

        loop.Frame(TimeSpan.FromSeconds(1d / 60d));

        Assert.Equal(1, gizmos.LastDrawnCount);
    }

    Animator Animated(Vector3 target) {
        var animator = new Animator(skeleton);
        var stack = new ConstraintStack(skeleton);

        stack.Add(new PositionGoal { Effector = 2, Chain = new(0, 2), Goal = new WorldFrame(target), EaseIn = 0f });
        animator.PoseProcessors.Add(stack);

        return animator;
    }

    static float Worst(DebugDraw draw) {
        var worst = 0f;

        foreach (var line in draw.Lines) {
            // Grey is not on the scale; only the graded lines are compared.
            if (MathF.Abs(line.Colour.B - 0.3f) < 1e-3f) {
                worst = MathF.Max(worst, line.Colour.R);
            }
        }

        return worst;
    }

    (ConstraintStack Stack, SkeletonPose Pose) Solved(Vector3 target, string? label = null) {
        var stack = new ConstraintStack(skeleton);
        var pose = new SkeletonPose(skeleton);

        stack.Add(
            new PositionGoal {
                Effector = 2,
                Chain = new(0, 2),
                Goal = new WorldFrame(target),
                Label = label is null ? default : Symbol.Intern(label),
                EaseIn = 0f
            }
        );

        // Long enough for the ease to be over, so the residual is the solver's answer and not the
        // ramp's.
        for (var frame = 0; frame < 30; frame++) {
            pose.ResetToBindPose();
            stack.Solve(pose.Bones, 1f / 60f);
        }

        return (stack, pose);
    }

    BoneTransform[] Model(SkeletonPose pose) {
        var model = new BoneTransform[skeleton.JointCount];
        SkeletonPose.ComputeModelSpace(skeleton, pose.Bones, model);

        return model;
    }

    static bool Close(Vector3 left, Vector3 right) => (left - right).Length() < 1e-3f;
}
