// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Constraints;
using Vixen.Animation.Moves;
using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Xunit;

namespace Vixen.Animation.Tests;

/// <summary>Doc 34's P8: knowing when a clip is finished, rather than authoring one faster.</summary>
public class HarnessTests {
    /// <summary>
    ///     ⚠ <b>P8's exit, in one test.</b> A clip authored against one body is run across a range,
    ///     the harness fails it, the report names the configuration and the moment, the clip is fixed
    ///     from what the cell said, and the same run passes. Nothing here looks at a picture.
    /// </summary>
    [Fact]
    public void AnOverTightClipFailsIsFixedFromTheCellAndPasses() {
        var plan = Plan(Tight());
        var report = VariationHarness.Run(plan);

        var verdict = report.Judge(plan.Thresholds);

        Assert.False(verdict.Passed, verdict.Summary);

        // The cell an author is dropped onto: which body, which goal, and where in the clip.
        var worst = Assert.NotNull(report.Worst(plan.Thresholds));

        Assert.Equal("right hand", worst.Goal);
        Assert.Contains("×0.7", report.Cases[worst.Variation].Label, StringComparison.Ordinal);
        Assert.InRange(worst.At, 0f, 1f);

        // ⚠ Only the small body fails, which is the point: looking at the authored body would show
        // nothing wrong at all. A run where every configuration failed would pass this test while
        // saying nothing about variation.
        Assert.Single(verdict.Failed);
        Assert.True(report[0, 0].Reached, "the small body's arm is straight and still short");
        Assert.False(report[1, 0].Fails(plan.Thresholds), "the body it was authored against is fine");

        // ⚠ The fix is the one the cell argues for and not a threshold change: the contact is on the
        // body's own surface rather than at a point in the world, so it moves with the body it is on.
        var fixedPlan = Plan(OnTheBody());
        var after = VariationHarness.Run(fixedPlan);

        Assert.True(after.Judge(fixedPlan.Thresholds).Passed, after.Judge(fixedPlan.Thresholds).Summary);
    }

    /// <summary>The matrix is every combination, because the failures are at the corners.</summary>
    [Fact]
    public void TwoAxesAreAGridRatherThanTwoRuns() {
        var plan = new HarnessPlan {
            Clip = Content(OnTheBody()),
            Skeleton = Body(1f).Skeleton,
            Shapes = Body(1f).Shapes,
            Samples = 8,
            Variations = [
                new BodyVariation(Body(1f).Skeleton, 0.7f, 1f, 1.4f),
                new GroundVariation((0f, 0f), (12f, 0.1f))
            ]
        };

        var report = VariationHarness.Run(plan);

        Assert.Equal(6, report.Cases.Count);
        Assert.Equal(6 * report.Goals.Count, report.Cells.Length);

        // Last axis fastest, so a report read top to bottom groups by body — which is how somebody
        // reads it when the question is "which bodies is this broken on".
        Assert.Contains("×0.7", report.Cases[0].Label, StringComparison.Ordinal);
        Assert.Contains("×0.7", report.Cases[1].Label, StringComparison.Ordinal);
        Assert.Contains("×1", report.Cases[2].Label, StringComparison.Ordinal);
    }

    /// <summary>No axes at all is one configuration, which is what checking one body means.</summary>
    [Fact]
    public void NoVariationIsOneRunRatherThanNoRun() {
        var report = VariationHarness.Run(
            new() { Clip = Content(OnTheBody()), Skeleton = Body(1f).Skeleton, Shapes = Body(1f).Shapes, Samples = 4 }
        );

        Assert.Single(report.Cases);
        Assert.Equal("as authored", report.Cases[0].Label);
        Assert.True(report.Cells[0].Ran);
    }

    /// <summary>
    ///     ⚠ <b>A goal that never resolved outranks any amount of error</b>, and is a failure even
    ///     when no threshold was set for it. It is how a variation most often actually breaks, and a
    ///     report showing it as zero error would be worse than no report.
    /// </summary>
    [Fact]
    public void AGoalThatNeverResolvesIsTheWorstCellAndFailsTheRun() {
        var tag = OnTheBody();
        tag.Goal.Shape = "no-such-shape";

        var plan = Plan(tag);
        var report = VariationHarness.Run(plan);

        var worst = Assert.NotNull(report.Worst(plan.Thresholds));

        Assert.False(worst.Ran);
        Assert.False(report.Judge(plan.Thresholds).Passed);
        Assert.Contains("never resolved", report.Judge(plan.Thresholds).Summary, StringComparison.Ordinal);
    }

    /// <summary>A threshold nobody set is a build that fails for a reason nobody agreed to.</summary>
    [Fact]
    public void NothingIsJudgedUntilAThresholdIsSet() {
        var report = VariationHarness.Run(
            new() { Clip = Content(Tight()), Skeleton = Body(0.7f).Skeleton, Shapes = Body(0.7f).Shapes, Samples = 4 }
        );

        var verdict = report.Judge(default);

        Assert.True(verdict.Passed);
        Assert.Contains("no threshold", verdict.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>A hand that snaps shows in the jerk and nowhere else.</b> The residual is small on
    ///     both sides of a snap, which is exactly what makes a snap invisible in a residual plot —
    ///     so a goal that teleports its target mid-clip has to be caught by the second measurement.
    /// </summary>
    [Fact]
    public void ASnapIsCaughtByTheVelocityMeasurementAndNotByTheResidual() {
        var body = Body(1f);

        // Two tags on the same effector, one after the other, half a metre apart: at the seam the
        // hand is asked to be somewhere else entirely, and both goals are satisfiable at their own
        // ends. A residual check alone calls this clip perfect.
        var content = Content(
            Reach(new Vector3(0.25f, 1.1f, 0.2f), 0f, 0.49f),
            Reach(new Vector3(0.25f, 1.1f, -0.35f), 0.5f, 1f, "second hand")
        );

        var report = VariationHarness.Run(
            new() { Clip = content, Skeleton = body.Skeleton, Shapes = body.Shapes, Samples = 24 }
        );

        var jerk = 0f;
        var residual = 0f;

        foreach (var cell in report.Cells) {
            jerk = MathF.Max(jerk, cell.Jerk);
            residual = MathF.Max(residual, cell.Residual);
        }

        Assert.True(residual < 0.05f, $"both goals are reachable, so the residual stays small — it was {residual}");
        Assert.True(jerk > 1f, $"the snap at the seam is a large change of velocity — it was {jerk}");
    }

    /// <summary>A chain with nothing left to give is named, because it is a different fix.</summary>
    /// <remarks>
    ///     ⚠ <b>Not "joint limits hit", which is what the plan asks for and what no skeleton here
    ///     carries.</b> A straight arm still missing its goal is the failure a limit would have caught
    ///     in the cases this measures, and reporting it under the name it actually has beats
    ///     reporting a check that does not exist.
    /// </remarks>
    [Fact]
    public void AChainThatRanOutOfReachIsReportedSeparatelyFromOneThatMissed() {
        var body = Body(1f);

        var report = VariationHarness.Run(
            new() {
                Clip = Content(Reach(new Vector3(4f, 1.1f, 0f), 0f, 1f)),
                Skeleton = body.Skeleton,
                Shapes = body.Shapes,
                Samples = 4
            }
        );

        Assert.True(report.Cells[0].Reached, "four metres away from a half-metre arm is out of reach");

        var verdict = report.Judge(new() { Reach = true });

        Assert.False(verdict.Passed);
    }

    /// <summary>
    ///     A body twice the size is a body whose bones are twice as long in the same directions,
    ///     which is what "twice the size" means to everybody not writing the code.
    /// </summary>
    [Fact]
    public void ResizingARigScalesTheOffsetsAndLeavesTheRotationsAlone() {
        var original = Body(1f).Skeleton;
        var doubled = BodyVariation.Resize(original, 2f);

        var one = new SkeletonPose(original);
        var two = new SkeletonPose(doubled);

        var first = TestRigs.ModelPositions(one);
        var second = TestRigs.ModelPositions(two);

        for (var joint = 0; joint < original.JointCount; joint++) {
            TestRigs.Near(first[joint] * 2f, second[joint], original.NameOf(joint));
        }

        Assert.Equal(original.JointCount, doubled.JointCount);
    }

    /// <summary>The other half of the drill-down: a row turns back into the body it was.</summary>
    [Fact]
    public void ARowRebuildsIntoTheConfigurationItWas() {
        var plan = Plan(OnTheBody());
        var report = VariationHarness.Run(plan);

        var subject = VariationHarness.Rebuild(plan, report.Cases[0]);

        Assert.Contains("×0.7", subject.Label, StringComparison.Ordinal);
        Assert.NotNull(subject.Shapes);

        // The rebuilt body really is the smaller one, not the authored one with a label on it.
        Assert.True(
            new SkeletonPose(subject.Skeleton).Bones[1].Translation.Y
            < new SkeletonPose(Body(1f).Skeleton).Bones[1].Translation.Y
        );
    }

    /// <summary>
    ///     A declared plan is the same run as a hand-written one. The thresholds have to live
    ///     somewhere the person authoring the clip can see them and change them.
    /// </summary>
    [Fact]
    public void ADeclaredPlanResolvesIntoTheSameRun() {
        var body = Body(1f);

        var declaration = new HarnessPlanContent {
            Name = "reach",
            Clip = "Assets/Reach.vxanim",
            Rig = "Assets/Hero.gltf",
            Samples = 8,
            Bodies = [0.7f, 1f, 1.4f],
            Ground = [new() { Degrees = 0f, Height = 0f }, new() { Degrees = 10f, Height = 0.05f }],
            Thresholds = new() { Residual = 0.03f }
        };

        Assert.Equal(6, declaration.Configurations);

        var plan = declaration.Resolve(body.Skeleton, Content(OnTheBody()), body.Shapes);
        var report = VariationHarness.Run(plan);

        Assert.Equal(declaration.Configurations, report.Cases.Count);
        Assert.Equal(0.03f, plan.Thresholds.Residual, 4);
        Assert.True(report.Judge(plan.Thresholds).Passed, report.Judge(plan.Thresholds).Summary);
    }

    // ── The fixture ──────────────────────────────────────────────────────────

    static HarnessPlan Plan(params ConstraintTagRecord[] tags) {
        var body = Body(1f);

        return new() {
            Clip = Content(tags),
            Skeleton = body.Skeleton,
            Shapes = body.Shapes,
            Samples = 12,
            Thresholds = new() { Residual = 0.03f, Penetration = 0.02f },
            Variations = [new BodyVariation(body.Skeleton, 0.7f, 1f, 1.4f)]
        };
    }

    /// <summary>
    ///     A contact at a fixed point in the world. Comfortable on the body it was authored against
    ///     and six centimetres out of reach on the small one — which is the failure the whole document
    ///     is about, and which no amount of looking at the authored body would reveal.
    /// </summary>
    static ConstraintTagRecord Tight() => Reach(new Vector3(0.2f, 1.3f, 0.25f), 0f, 1f);

    /// <summary>The same contact expressed on the body's own belly, which is the fix.</summary>
    static ConstraintTagRecord OnTheBody() =>
        new() {
            Name = "right hand",
            Kind = GoalKind.Position,
            Effector = "Wrist",
            Chain = "Shoulder",
            Begin = 0f,
            End = 1f,
            Goal = new() { Kind = ConstraintFrameKind.Surface, Shape = "belly", Face = -1, U = 0.5f, V = 0.5f }
        };

    static ConstraintTagRecord Reach(Vector3 at, float begin, float end, string name = "right hand") =>
        new() {
            Name = name,
            Kind = GoalKind.Position,
            Effector = "Wrist",
            Chain = "Shoulder",
            Begin = begin,
            End = end,
            Goal = new() { Kind = ConstraintFrameKind.World, Position = at }
        };

    static AnimationClipContent Content(params ConstraintTagRecord[] tags) =>
        new() {
            Name = "Reach",
            Data = new() { Name = "Reach", Duration = 1f, Channels = [] },
            Constraints = [.. tags]
        };

    static TestBody Body(float scale) => new(scale);

    /// <summary>An arm on a spine, with a belly to put a hand on.</summary>
    sealed class TestBody {
        public TestBody(float scale) {
            Skeleton = Skeleton.Create(
                TestRigs.Build(
                    "Body",
                    ("Root", -1, Vector3.Zero),
                    ("Spine", 0, new Vector3(0f, 0.9f, 0f) * scale),
                    ("Shoulder", 1, new Vector3(0.2f, 0.35f, 0f) * scale),
                    ("Elbow", 2, new Vector3(0f, -0.32f, 0f) * scale),
                    ("Wrist", 3, new Vector3(0f, -0.3f, 0f) * scale)
                )
            );

            Shapes = ProxyShapeSet.Of(
                "Body",
                null,
                new ProxyShape {
                    Name = Symbol.Intern("belly"),
                    Kind = ShapeKind.Sphere,
                    Joint = Skeleton.IndexOf("Spine"),
                    Offset = new(new Vector3(0f, 0.1f, 0.05f) * scale, Quaternion.Identity, Vector3.One),
                    Dimensions = ShapeParams.Sphere(0.22f).Scaled(new Vector3(scale))
                }
            );
        }

        public Skeleton Skeleton { get; }

        public ProxyShapeSet Shapes { get; }
    }
}
