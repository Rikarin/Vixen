// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Constraints;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Animation.Tests;

/// <summary>A goal that moves: decomposed, decimated, and replayed on a rail it was not authored on.</summary>
public class TrajectoryTests {
    const int Captured = 121;

    // ---------------------------------------------------------------- curves

    [Fact]
    public void ACurveIsSampledByPhaseAndClampedAtItsEnds() {
        var curve = new TrajectoryCurve(
            new TrajectoryKey(0.2f, new(0f, 0f, 0f)),
            new TrajectoryKey(0.6f, new(4f, 0f, 0f))
        );

        TestRigs.Near(new(0f, 0f, 0f), curve.Sample(0f), "before the first key");
        TestRigs.Near(new(2f, 0f, 0f), curve.Sample(0.4f), "half way");
        TestRigs.Near(new(4f, 0f, 0f), curve.Sample(1f), "after the last");
    }

    [Fact]
    public void DecimationKeepsOnlyTheKeysALinearSamplerWouldNotHaveProduced() {
        var straight = new TrajectoryKey[41];

        for (var index = 0; index < straight.Length; index++) {
            var phase = index / (float)(straight.Length - 1);
            straight[index] = new(phase, new(phase * 2f, 0f, 0f));
        }

        var curve = TrajectoryCurve.Decimate(straight, 1e-4f, out var report);

        Assert.Equal(2, curve.Count);
        Assert.Equal(41, report.KeysBefore);
        Assert.Equal(2, report.KeysAfter);
        Assert.True(report.Ratio < 0.06f);

        // Forty-one keys on a line is forty-one keys saying the same thing, and the two that survive
        // still say all of it.
        for (var index = 0; index < straight.Length; index++) {
            TestRigs.Near(straight[index].Value, curve.Sample(straight[index].Phase));
        }
    }

    [Fact]
    public void ACurveThatStopsMovingBecomesOneKey() {
        var still = new TrajectoryKey[20];

        for (var index = 0; index < still.Length; index++) {
            still[index] = new(index / 19f, new(0.5f, 0f, 0f));
        }

        Assert.Equal(1, TrajectoryCurve.Decimate(still, 1e-4f, out _).Count);
    }

    /// <summary>
    ///     ⚠ <c>U</c> is an angle, so a slide across the seam reads as a jump from 0.98 to 0.02.
    /// </summary>
    /// <remarks>
    ///     A decimator handed that either keeps every key around the seam or averages through it and
    ///     sends the contact the long way round the limb. It is unwrapped before and re-wrapped after.
    /// </remarks>
    [Fact]
    public void ASlideAcrossTheSeamCompressesAndDoesNotGoTheLongWayRound() {
        var across = new SurfacePathKey[41];

        for (var index = 0; index < across.Length; index++) {
            var phase = index / (float)(across.Length - 1);
            var u = 0.9f + (phase * 0.2f);

            across[index] = new(phase, new(-1, u >= 1f ? u - 1f : u, 0.5f));
        }

        var path = SurfacePath.Decimate(across, 2e-3f, out var report);

        Assert.True(report.KeysAfter <= 3, $"a straight run should compress, kept {report.KeysAfter}");

        // Nothing on the way across ever goes backwards through the middle of the shape, which is
        // what a decimator that averaged 0.98 with 0.02 would produce.
        var previous = path.Sample(0f);

        for (var step = 1; step <= 40; step++) {
            var here = path.Sample(step / 40f);
            var moved = here.U - previous.U;

            if (moved < -0.5f) {
                moved += 1f;
            }

            Assert.True(moved is >= -1e-4f and < 0.1f, $"the contact jumped {moved:0.####} at step {step}");
            previous = here;
        }

        TestRigs.Near(new Vector3(0.1f, 0f, 0f), new Vector3(path.Sample(1f).U, 0f, 0f), "it ends where it ended");
    }

    // ---------------------------------------------------------------- the claim

    /// <summary>
    ///     A hand sliding along a rail replays on a rail of a different length and radius, and the
    ///     compressed curve is within the authored tolerance of the raw one.
    /// </summary>
    [Fact]
    public void ASlidingContactReproducesOnADifferentRailWithinTolerance() {
        const float Tolerance = 2e-3f;

        var authored = Rail(radius: 0.05f, halfHeight: 0.6f);
        var samples = Capture(authored);

        var trajectory = GoalTrajectory.Decompose(
            samples,
            new TrajectoryTolerance(Tolerance, 8.7e-3f, 1e-3f),
            out var report
        );

        // It compressed at all, or there is nothing to check.
        Assert.True(report.Ratio < 0.4f, $"the curve should have compressed, kept {report.Ratio:0.##}");

        // ⚠ Against the raw samples, at every one of them, not at the keys that survived — which is
        // where a decimator that measured its own error against its own output would pass.
        foreach (var sample in samples) {
            var error = (trajectory.Reconstruct(sample.Phase) - (sample.Origin + sample.Offset)).Length();
            Assert.True(error <= Tolerance, $"at phase {sample.Phase:0.###} the curve was {error:0.#####} m out");
        }

        // Now the other rail: twice the radius and nearly twice the length.
        var other = Rail(radius: 0.12f, halfHeight: 1.1f);
        var frame = new TrajectoryFrame(new SurfaceFrame(SurfaceCoordinate.On("rail", SurfacePoint.Side)), trajectory);

        List<Vector3> replayed = [];

        for (var step = 0; step <= 20; step++) {
            var phase = step / 20f;

            Assert.True(frame.TryResolve(Context(other, phase), out var resolved), $"at phase {phase}");

            replayed.Add(resolved.Origin);

            // The contact is on the rail it is being replayed against, at the fraction it was
            // authored at — not at the centimetres it was authored at.
            Assert.True(new ProxyShapes(other.Shapes).TryPose(Symbol.Intern("rail"), other.Model, out var rail));

            var where = ShapeGeometry.Project(rail.Shape.Kind, rail.Dimensions, rail.ToShape(resolved.Origin), out var gap);
            var wanted = trajectory.Surface!.Sample(phase);

            Assert.Equal(wanted.V, where.V, 5e-3f);
            Assert.True(gap.Length() < 5e-3f, $"the hand sat {gap.Length():0.####} m off the rail at phase {phase}");
        }

        // Longer rail, longer slide — which is the whole of why the path is normalised.
        var authoredRun = Length(samples.Select(sample => sample.Origin + sample.Offset).ToList());
        var replayedRun = Length(replayed);

        Assert.True(
            replayedRun > authoredRun * 1.5f,
            $"a rail nearly twice as long should give a longer slide: {authoredRun:0.###} m to {replayedRun:0.###} m"
        );
    }

    [Fact]
    public void ATrajectoryOverANonSurfaceFrameIsJustAMovingOffset() {
        var trajectory = new GoalTrajectory(
            TrajectoryCurve.Constant(Vector3.Zero),
            new TrajectoryCurve(
                new TrajectoryKey(0f, new(0f, 0f, 0f)),
                new TrajectoryKey(1f, new(0f, 1f, 0f))
            )
        );

        var frame = new TrajectoryFrame(new WorldFrame(new Vector3(2f, 0f, 0f)), trajectory);
        var rail = Rail(0.05f, 0.6f);

        Assert.True(frame.TryResolve(Context(rail, 0f), out var start));
        Assert.True(frame.TryResolve(Context(rail, 1f), out var end));

        TestRigs.Near(new(2f, 0f, 0f), start.Origin);
        TestRigs.Near(new(2f, 1f, 0f), end.Origin);
    }

    [Fact]
    public void ATrajectoryWhoseFrameDoesNotResolveFailsRatherThanReplayingWhereTheRailUsedToBe() {
        var trajectory = new GoalTrajectory(
            new TrajectoryCurve(new TrajectoryKey(0f, new(9f, 9f, 9f))),
            TrajectoryCurve.Constant(Vector3.Zero),
            new SurfacePath(new SurfacePathKey(0f, SurfacePoint.Side))
        );

        var frame = new TrajectoryFrame(new SurfaceFrame(SurfaceCoordinate.On("gone", SurfacePoint.Side)), trajectory);

        // ⚠ The authored origin polyline is not a fallback. A rail that has moved since the clip was
        // captured is the ordinary case, and replaying where it used to be puts the hand in the air.
        Assert.False(frame.TryResolve(Context(Rail(0.05f, 0.6f), 0.5f), out _));
    }

    // ---------------------------------------------------------------- through the stack

    [Fact]
    public void AClipCarryingATrajectoryDrivesItFromTheClipsOwnPhase() {
        var rail = Rail(0.05f, 0.6f);
        var trajectory = GoalTrajectory.Decompose(Capture(rail), TrajectoryTolerance.Default, out _);

        var stack = new ConstraintStack(rail.Skeleton) { Shapes = new(rail.Shapes) };
        var tags = new ConstraintTagBuffer();

        stack.Tags = tags;

        var track = new ConstraintTrack(
            new ConstraintTag {
                Goal = new PositionGoal {
                    Effector = rail.Wrist,
                    Chain = new(rail.Shoulder, rail.Wrist),
                    Goal = new TrajectoryFrame(
                        new SurfaceFrame(SurfaceCoordinate.On("rail", SurfacePoint.Side)),
                        trajectory
                    ),
                    EaseIn = 0f
                }
            }
        );

        List<float> along = [];

        foreach (var phase in new[] { 0.1f, 0.5f, 0.9f }) {
            for (var frame = 0; frame < 30; frame++) {
                rail.Pose.ResetToBindPose();
                tags.Clear();
                tags.Collect(track, phase, 1f);
                stack.Solve(rail.Pose.Bones, rail.Model, 1f / 60f);
            }

            Assert.True(stack.Shapes!.TryPose(Symbol.Intern("rail"), rail.Model, out var posed));

            var hand = TestRigs.ModelPositions(rail.Pose)[rail.Wrist];
            along.Add(ShapeGeometry.Project(posed.Shape.Kind, posed.Dimensions, posed.ToShape(hand), out _).V);
        }

        // The hand went down the rail as the clip played, and each stop is where the path says it is.
        Assert.True(along[0] < along[1] && along[1] < along[2], $"the hand did not slide: {string.Join(", ", along)}");

        for (var index = 0; index < along.Count; index++) {
            var phase = 0.1f + (index * 0.4f);
            Assert.Equal(trajectory.Surface!.Sample(phase).V, along[index], 2e-2f);
        }
    }

    [Fact]
    public void TwoGoalsFromClipsAtDifferentPhasesEachGetTheirOwn() {
        var rail = Rail(0.05f, 0.6f);
        var trajectory = GoalTrajectory.Decompose(Capture(rail), TrajectoryTolerance.Default, out _);
        var frame = new TrajectoryFrame(new SurfaceFrame(SurfaceCoordinate.On("rail", SurfacePoint.Side)), trajectory);

        // ⚠ The phase is per goal and not per character: a walk at 0.3 under a reach at 0.8 is the
        // ordinary case, and a context that carried one phase would drive both from whichever clip
        // happened to be gathered last.
        Assert.True(frame.TryResolve(Context(rail, 0.2f), out var early));
        Assert.True(frame.TryResolve(Context(rail, 0.8f), out var late));

        Assert.True((early.Origin - late.Origin).Length() > 0.2f, "the two phases should be far apart on the rail");
    }

    // ---------------------------------------------------------------- the rig

    static float Length(List<Vector3> path) {
        var total = 0f;

        for (var index = 1; index < path.Count; index++) {
            total += (path[index] - path[index - 1]).Length();
        }

        return total;
    }

    /// <summary>
    ///     What a capture of a hand sliding along a rail produces: a hundred and twenty-one moments,
    ///     mostly on a straight run, with a wobble in the middle that decimation has to keep.
    /// </summary>
    static TrajectorySample[] Capture(TestRail rail) {
        var samples = new TrajectorySample[Captured];

        Assert.True(new ProxyShapes(rail.Shapes).TryPose(Symbol.Intern("rail"), rail.Model, out var posed));

        for (var index = 0; index < Captured; index++) {
            var phase = index / (float)(Captured - 1);
            var point = new SurfacePoint(-1, 0.25f, MathUtil.Lerp(0.15f, 0.85f, phase));
            var sample = ShapeGeometry.Evaluate(posed.Shape.Kind, posed.Dimensions, point);
            var origin = posed.ToModel(sample.Position);

            // A centimetre of clearance that dips in the middle — the shape a decimator has to keep,
            // against a slide that is otherwise a straight line it should throw away.
            var gap = 0.01f + (0.004f * MathF.Sin(phase * MathUtil.Pi));

            samples[index] = new(
                phase,
                origin,
                Quaternion.Transform(new Vector3(0f, gap, 0f), Quaternion.Concatenate(sample.Rotation(), posed.Transform.Rotation)),
                point,
                Quaternion.Identity
            );
        }

        return samples;
    }

    static ConstraintContext Context(TestRail rail, float phase) =>
        new() {
            Skeleton = rail.Skeleton,
            Model = rail.Model,
            Bindings = new(),
            Shapes = rail.Shapes is null ? null : new ProxyShapes(rail.Shapes),
            Phase = phase
        };

    static TestRail Rail(float radius, float halfHeight) => new(radius, halfHeight);

    /// <summary>A rail beside an arm: a capsule on a fixed joint, and a chain that can reach it.</summary>
    sealed class TestRail {
        public TestRail(float radius, float halfHeight) {
            Skeleton = Skeleton.Create(
                TestRigs.Build(
                    "Rail",
                    ("Root", -1, Vector3.Zero),
                    ("Shoulder", 0, new Vector3(0f, 1.4f, 0f)),
                    ("Elbow", 1, new Vector3(0f, -0.45f, 0f)),
                    ("Wrist", 2, new Vector3(0f, -0.45f, 0f)),
                    ("RailMount", 0, new Vector3(0.4f, 1f, 0f))
                )
            );

            Shoulder = Skeleton.IndexOf("Shoulder");
            Wrist = Skeleton.IndexOf("Wrist");

            Shapes = ProxyShapeSet.Of(
                "Rail",
                null,
                new ProxyShape {
                    Name = Symbol.Intern("rail"),
                    Kind = ShapeKind.Capsule,
                    Joint = Skeleton.IndexOf("RailMount"),
                    Dimensions = ShapeParams.Capsule(radius, halfHeight)
                }
            );

            Pose = new(Skeleton);
            Model = new BoneTransform[Skeleton.JointCount];

            Pose.ComputeModelSpace(Model);
        }

        public Skeleton Skeleton { get; }

        public ProxyShapeSet Shapes { get; }

        public SkeletonPose Pose { get; }

        public BoneTransform[] Model { get; }

        public int Shoulder { get; }

        public int Wrist { get; }
    }
}
