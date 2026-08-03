// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Constraints;
using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Xunit;

namespace Vixen.Animation.Tests;

/// <summary>How far a joint may turn, and what the arbiter does about it.</summary>
public class JointLimitTests {
    /// <summary>
    ///     ⚠ <b>A zeroed limit is a welded joint and the default is a free one.</b> Reading zero as
    ///     "no limit" would make a locked joint unauthorable; reading it as the default would freeze
    ///     every rig exported before the fields existed. The flag on the joint is what tells them
    ///     apart, and nothing reads a limit without it.
    /// </summary>
    [Fact]
    public void TheZeroValueIsWeldedAndTheDefaultIsFree() {
        Assert.True(JointLimit.Free.IsFree);
        Assert.False(default(JointLimit).IsFree);

        var welded = default(JointLimit);
        var turned = Quaternion.FromAxisAngle(Vector3.UnitX, 0.5f);

        Assert.Equal(Quaternion.Identity, welded.Clamp(turned, Quaternion.Identity, out var cut), Near);
        Assert.True(cut);

        Assert.Equal(turned, JointLimit.Free.Clamp(turned, Quaternion.Identity, out var free));
        Assert.False(free);
    }

    /// <summary>A lean inside the cone is left alone and one outside is brought to the edge.</summary>
    [Fact]
    public void ASwingIsClampedToTheConeAndKeepsItsDirection() {
        var limit = JointLimit.Of(30f, 0f);

        var inside = Quaternion.FromAxisAngle(Vector3.UnitX, MathUtil.DegreesToRadians(20f));

        Assert.Equal(inside, limit.Clamp(inside, Quaternion.Identity, out var untouched));
        Assert.False(untouched);

        var outside = Quaternion.FromAxisAngle(Vector3.UnitX, MathUtil.DegreesToRadians(70f));
        var clamped = limit.Clamp(outside, Quaternion.Identity, out var cut);

        Assert.True(cut);
        Assert.Equal(30f, MathUtil.RadiansToDegrees(Angle(clamped)), 1);

        // ⚠ And it leans the same way. A clamp that pulled towards the bind pose along the shortest
        // arc would swing a limb sideways as well as back, which reads as the joint sliding.
        Assert.Equal(Vector3.UnitX, Axis(clamped), Near3);
    }

    /// <summary>
    ///     ⚠ <b>Swing and twist are separated before either is clamped.</b> Clamping the whole
    ///     rotation would pull a joint's twist back whenever its swing was too wide — a forearm
    ///     straightening because a shoulder was over-rotated, which reads as the solver fighting
    ///     itself.
    /// </summary>
    [Fact]
    public void AnOverWideSwingDoesNotTakeAnAllowedTwistWithIt() {
        var limit = JointLimit.Of(20f, 90f);

        var twist = Quaternion.FromAxisAngle(Vector3.UnitY, MathUtil.DegreesToRadians(60f));
        var swing = Quaternion.FromAxisAngle(Vector3.UnitX, MathUtil.DegreesToRadians(70f));

        var clamped = limit.Clamp(Quaternion.Concatenate(twist, swing), Quaternion.Identity, out var cut);

        Assert.True(cut);

        // The twist survives at its full sixty degrees; only the swing was cut.
        var along = Vector3.Dot(new Vector3(clamped.X, clamped.Y, clamped.Z), Vector3.UnitY) * Vector3.UnitY;
        var recovered = Quaternion.Normalize(new Quaternion(along.X, along.Y, along.Z, clamped.W));

        Assert.Equal(60f, MathUtil.RadiansToDegrees(Angle(recovered)), 1);
        Assert.Equal(20f, MathUtil.RadiansToDegrees(Angle(Quaternion.Concatenate(Quaternion.Conjugate(recovered), clamped))), 1);
    }

    /// <summary>⚠ Measured from the bind pose, because that is where the artist put the joint.</summary>
    [Fact]
    public void ALimitIsMeasuredFromTheBindPoseAndNotFromTheParent() {
        var limit = JointLimit.Of(15f, 0f);
        var bind = Quaternion.FromAxisAngle(Vector3.UnitX, MathUtil.DegreesToRadians(80f));

        // Eighty degrees from the parent, and nothing at all from where it was modelled.
        Assert.Equal(bind, limit.Clamp(bind, bind, out var cut));
        Assert.False(cut);
    }

    // ── Through the arbiter ──────────────────────────────────────────────────

    /// <summary>
    ///     A rig with no limits pays a boolean, which is what makes the clamp affordable on the
    ///     ninety-nine rigs out of a hundred that declare none.
    /// </summary>
    [Fact]
    public void ARigWithNoLimitsSaysSo() {
        Assert.False(TestRigs.Chain().HasLimits);
        Assert.True(JointLimit.Free == TestRigs.Chain().LimitOf(1));

        Assert.True(Limited(20f).HasLimits);
    }

    /// <summary>
    ///     ⚠ <b>The whole point.</b> A goal the chain could reach by bending past a joint's range is
    ///     now missed instead — the pose is legal and the residual says how much that cost, which is
    ///     the honest outcome of a clamp that cannot redistribute.
    /// </summary>
    [Fact]
    public void AGoalIsMissedRatherThanReachedThroughAJointsStop() {
        var free = Solve(TestRigs.Chain(), new Vector3(1.4f, 1.4f, 0f));
        var bound = Solve(Limited(10f), new Vector3(1.4f, 1.4f, 0f));

        Assert.True(free.Residual < 0.05f, $"the unlimited rig reaches it — off by {free.Residual}");
        Assert.True(bound.Residual > free.Residual + 0.1f, $"the limited one cannot — off by {bound.Residual}");

        // And the pose it settled on really is inside the limit rather than merely reported as such.
        var bind = Limited(10f).BindPose;

        for (var joint = 0; joint < bound.Pose.Length; joint++) {
            Limited(10f).LimitOf(joint).Clamp(bound.Pose[joint].Rotation, bind[joint].Rotation, out var cut);
            Assert.False(cut, $"joint {joint} ended up outside its own limit");
        }
    }

    /// <summary>
    ///     ⚠ <b>Reach and limits are different facts and the harness reports them separately.</b> A
    ///     straight arm that is still short is answered by moving the contact; a joint at its stop is
    ///     answered by widening the limit or bending elsewhere.
    /// </summary>
    [Fact]
    public void TheHarnessTellsAJointAtItsStopFromAnArmThatIsTooShort() {
        var content = new AnimationClipContent {
            Name = "Reach",
            Data = new() { Name = "Reach", Duration = 1f },
            Constraints = [
                new() {
                    Name = "tip",
                    Effector = "Tip",
                    Chain = "Root",
                    Goal = new() { Kind = ConstraintFrameKind.World, Position = new(1.4f, 1.4f, 0f) }
                }
            ]
        };

        var bound = VariationHarness.Run(new() { Clip = content, Skeleton = Limited(10f), Samples = 4 });
        var free = VariationHarness.Run(new() { Clip = content, Skeleton = TestRigs.Chain(), Samples = 4 });

        Assert.True(bound.Cells[0].Limited, "the limited rig is sitting on its stops");
        Assert.False(free.Cells[0].Limited, "the free one has no stops to sit on");

        // Judged separately, so a project can fail on one and not the other.
        Assert.False(bound.Judge(new() { Limits = true }).Passed);
        Assert.True(free.Judge(new() { Limits = true }).Passed);
    }

    static (float Residual, BoneTransform[] Pose) Solve(Skeleton skeleton, Vector3 target) {
        var stack = new ConstraintStack(skeleton);
        var pose = new SkeletonPose(skeleton);

        var handle = stack.Add(
            new PositionGoal { Effector = 2, Chain = new(0, 2), Goal = new WorldFrame(target), EaseIn = 0f }
        );

        for (var frame = 0; frame < 30; frame++) {
            pose.ResetToBindPose();
            stack.Solve(pose.Bones, 1f / 60f);
        }

        return (MathF.Abs(handle.Residual.Magnitude), pose.Bones.ToArray());
    }

    /// <summary>The three-joint chain, with every joint held to a narrow cone.</summary>
    static Skeleton Limited(float degrees) {
        (string Name, int Parent, Vector3 Offset)[] joints = [
            ("Root", -1, Vector3.Zero),
            ("Mid", 0, Vector3.UnitY),
            ("Tip", 1, Vector3.UnitY)
        ];

        var model = new Matrix4x4[joints.Length];
        var built = new SkeletonJoint[joints.Length];

        for (var index = 0; index < joints.Length; index++) {
            var local = Matrix4x4.FromTranslation(joints[index].Offset);

            model[index] = joints[index].Parent >= 0 ? local * model[joints[index].Parent] : local;

            Matrix4x4.Invert(model[index], out var inverse);

            built[index] = new() {
                Name = joints[index].Name,
                Parent = joints[index].Parent,
                InverseBindPose = inverse,
                Limited = true,
                Swing = degrees,
                Twist = degrees
            };
        }

        return Skeleton.Create(new() { Name = "Bound", Joints = built });
    }

    static float Angle(Quaternion rotation) =>
        2f * MathF.Acos(MathUtil.Clamp(MathF.Abs(Quaternion.Normalize(rotation).W), -1f, 1f));

    static Vector3 Axis(Quaternion rotation) {
        var normalized = Quaternion.Normalize(rotation);
        var sin = MathF.Sqrt(MathF.Max(1f - (normalized.W * normalized.W), 0f));

        return sin <= 1e-6f
            ? Vector3.Zero
            : Vector3.Normalize(new Vector3(normalized.X, normalized.Y, normalized.Z) / sin);
    }

    static Quaternions Near { get; } = new();

    static Vectors Near3 { get; } = new();

    sealed class Quaternions : IEqualityComparer<Quaternion> {
        public bool Equals(Quaternion left, Quaternion right) =>
            MathF.Abs(MathF.Abs(Quaternion.Dot(left, right)) - 1f) < 1e-3f;

        public int GetHashCode(Quaternion value) => 0;
    }

    sealed class Vectors : IEqualityComparer<Vector3> {
        public bool Equals(Vector3 left, Vector3 right) => (left - right).Length() < 1e-3f;

        public int GetHashCode(Vector3 value) => 0;
    }
}
