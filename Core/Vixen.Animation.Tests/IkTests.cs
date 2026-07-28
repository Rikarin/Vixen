// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Ik;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Animation.Tests;

public class IkTests {
    readonly Skeleton skeleton = TestRigs.Chain();

    static Vector3 TipOf(SkeletonPose pose) => TestRigs.ModelPositions(pose)[2];

    (SkeletonPose Pose, BoneTransform[] Model) Fresh() =>
        (new SkeletonPose(skeleton), new BoneTransform[skeleton.JointCount]);

    [Theory]
    [InlineData(1f, 0f, 1f)]
    [InlineData(1.5f, 0f, 0.5f)]
    [InlineData(0f, 0f, 1.9f)]
    [InlineData(-1f, 0f, 1f)]
    public void Solve_ReachableTarget_PutsTheTipOnIt(float x, float y, float z) {
        var (pose, model) = Fresh();
        var target = new Vector3(x, y, z);

        Assert.True(
            TwoBoneIk.Solve(skeleton, pose.Bones, model, new(0, 1, 2, target, new(0f, 1f, 5f)))
        );

        TestRigs.Near(target, TipOf(pose), $"target {target}");
    }

    [Fact]
    public void Solve_UnreachableTarget_StraightensTowardsItWithoutStretching() {
        var (pose, model) = Fresh();
        var target = new Vector3(0f, 0f, 10f);

        TwoBoneIk.Solve(skeleton, pose.Bones, model, new(0, 1, 2, target, new(0f, 1f, 5f)));

        var positions = TestRigs.ModelPositions(pose);
        var reach = (positions[2] - positions[0]).Length();

        // Two unit bones. The chain points at the target and is as long as it ever was.
        Assert.Equal(2f, reach, 1e-3f);
        TestRigs.Near(new(0f, 0f, 2f), positions[2]);
    }

    [Fact]
    public void Solve_PreservesBoneLengths() {
        var (pose, model) = Fresh();

        TwoBoneIk.Solve(skeleton, pose.Bones, model, new(0, 1, 2, new(1.2f, 0.4f, 0.7f), new(0f, 1f, 5f)));

        var positions = TestRigs.ModelPositions(pose);

        Assert.Equal(1f, (positions[1] - positions[0]).Length(), 1e-4f);
        Assert.Equal(1f, (positions[2] - positions[1]).Length(), 1e-4f);
    }

    [Fact]
    public void Solve_ThePoleDecidesWhichWayTheJointBends() {
        var target = new Vector3(0f, 1f, 1f);

        var (front, frontModel) = Fresh();
        TwoBoneIk.Solve(skeleton, front.Bones, frontModel, new(0, 1, 2, target, new(0f, 0.5f, 10f)));

        var (back, backModel) = Fresh();
        TwoBoneIk.Solve(skeleton, back.Bones, backModel, new(0, 1, 2, target, new(0f, 0.5f, -10f)));

        var frontKnee = TestRigs.ModelPositions(front)[1];
        var backKnee = TestRigs.ModelPositions(back)[1];

        TestRigs.Near(target, TipOf(front));
        TestRigs.Near(target, TipOf(back));
        Assert.True(frontKnee.Z > backKnee.Z, $"knee did not flip: {frontKnee} vs {backKnee}");
    }

    [Fact]
    public void Solve_ZeroWeight_ChangesNothing() {
        var (pose, model) = Fresh();
        var before = TipOf(pose);

        Assert.True(
            TwoBoneIk.Solve(
                skeleton,
                pose.Bones,
                model,
                new(0, 1, 2, new(1f, 0f, 1f), new(0f, 1f, 5f), PositionWeight: 0f)
            )
        );

        TestRigs.Near(before, TipOf(pose));
    }

    [Fact]
    public void Solve_JointsThatAreNotAChain_IsRefused() {
        var branching = TestRigs.Branching();
        var pose = new SkeletonPose(branching);
        var model = new BoneTransform[branching.JointCount];

        var left = branching.IndexOf("LeftArm");
        var right = branching.IndexOf("RightArm");

        Assert.False(
            TwoBoneIk.Solve(branching, pose.Bones, model, new(0, left, right, Vector3.Zero, Vector3.UnitZ))
        );
    }

    [Fact]
    public void Solve_RotationWeight_AlignsTheTip() {
        var (pose, model) = Fresh();

        var wanted = Quaternion.FromAxisAngle(Vector3.UnitX, 0.9f);

        TwoBoneIk.Solve(
            skeleton,
            pose.Bones,
            model,
            new(0, 1, 2, new(0.5f, 1.2f, 0.5f), new(0f, 1f, 5f), wanted, 1f, 1f)
        );

        var solved = new BoneTransform[skeleton.JointCount];
        pose.ComputeModelSpace(solved);

        TestRigs.Near(wanted, solved[2].Rotation);
    }

    [Fact]
    public void LookAt_SingleJoint_FacesTheTarget() {
        var (pose, model) = Fresh();

        // The tip is at (0, 2, 0) and its −Z faces the target at (0, 2, −5).
        LookAtIk.Solve(skeleton, pose.Bones, model, [new(2)], new(5f, 2f, 0f));

        var solved = new BoneTransform[skeleton.JointCount];
        pose.ComputeModelSpace(solved);

        var facing = Quaternion.Transform(Vector3.Forward, solved[2].Rotation);
        TestRigs.Near(Vector3.UnitX, facing);
    }

    [Fact]
    public void LookAt_DistributedChain_SharesTheTurnAndStillEndsUpFacingTheTarget() {
        var (pose, model) = Fresh();
        var target = new Vector3(4f, 2f, 0f);

        LookAtIk.Solve(
            skeleton,
            pose.Bones,
            model,
            [new(0, 0.3f), new(1, 0.5f), new(2, 1f)],
            target
        );

        var solved = new BoneTransform[skeleton.JointCount];
        pose.ComputeModelSpace(solved);

        // Every joint turned some of the way, and the last one closes the gap.
        Assert.NotEqual(0f, solved[0].Rotation.Angle(), 3);

        var facing = Quaternion.Transform(Vector3.Forward, solved[2].Rotation);
        TestRigs.Near(Vector3.Normalize(target - solved[2].Translation), facing);
    }

    [Fact]
    public void LookAt_MaxAngle_ClampsHowFarAJointTurns() {
        var (pose, model) = Fresh();

        LookAtIk.Solve(skeleton, pose.Bones, model, [new(2, 1f, 0.5f)], new(5f, 2f, 0f));

        var solved = new BoneTransform[skeleton.JointCount];
        pose.ComputeModelSpace(solved);

        Assert.Equal(0.5f, solved[2].Rotation.Angle(), 1e-3f);
    }

    [Fact]
    public void LookAt_ZeroWeight_ChangesNothing() {
        var (pose, model) = Fresh();

        LookAtIk.Solve(skeleton, pose.Bones, model, [new(2)], new(5f, 2f, 0f), weight: 0f);

        TestRigs.Near(Quaternion.Identity, pose[2].Rotation);
    }

    [Fact]
    public void FootPlacement_OneFootLower_DropsTheHipsAndPlantsBoth() {
        // A leg rig: pelvis, hip, knee, ankle down the Y axis, twice.
        var rig = Skeleton.Create(
            TestRigs.Build(
                "Legs",
                ("Pelvis", -1, new Vector3(0f, 2f, 0f)),
                ("LeftHip", 0, new Vector3(-0.2f, 0f, 0f)),
                ("LeftKnee", 1, new Vector3(0f, -1f, 0f)),
                ("LeftAnkle", 2, new Vector3(0f, -1f, 0f)),
                ("RightHip", 0, new Vector3(0.2f, 0f, 0f)),
                ("RightKnee", 4, new Vector3(0f, -1f, 0f)),
                ("RightAnkle", 5, new Vector3(0f, -1f, 0f))
            )
        );

        var pose = new SkeletonPose(rig);
        var model = new BoneTransform[rig.JointCount];

        var placement = new FootPlacement(
            0,
            [
                new(1, 2, 3, new(0f, 1f, 10f), 0f),
                new(4, 5, 6, new(0f, 1f, 10f), 0f)
            ]
        );

        // The left foot's ground is a quarter of a metre below where the animation put it.
        var drop = placement.Solve(
            rig,
            pose.Bones,
            model,
            [
                new(true, new Vector3(-0.2f, -0.25f, 0f), Vector3.Up),
                new(true, new Vector3(0.2f, 0f, 0f), Vector3.Up)
            ]
        );

        Assert.Equal(-0.25f, drop, 1e-3f);

        var positions = TestRigs.ModelPositions(pose);

        Assert.Equal(-0.25f, positions[3].Y, 1e-3f);
        Assert.Equal(0f, positions[6].Y, 1e-3f);
    }

    [Fact]
    public void FootPlacement_NoGroundUnderAFoot_LeavesItWhereTheAnimationPutIt() {
        var rig = Skeleton.Create(
            TestRigs.Build(
                "Leg",
                ("Pelvis", -1, new Vector3(0f, 2f, 0f)),
                ("Hip", 0, Vector3.Zero),
                ("Knee", 1, new Vector3(0f, -1f, 0f)),
                ("Ankle", 2, new Vector3(0f, -1f, 0f))
            )
        );

        var pose = new SkeletonPose(rig);
        var model = new BoneTransform[rig.JointCount];
        var before = TestRigs.ModelPositions(pose)[3];

        var placement = new FootPlacement(0, [new(1, 2, 3, new(0f, 1f, 10f), 0f)]);
        var drop = placement.Solve(rig, pose.Bones, model, [new(false, Vector3.Zero, Vector3.Up)]);

        Assert.Equal(0f, drop);
        TestRigs.Near(before, TestRigs.ModelPositions(pose)[3]);
    }

    [Fact]
    public void FootPlacement_ZeroWeight_ChangesNothing() {
        var rig = Skeleton.Create(
            TestRigs.Build(
                "Leg",
                ("Pelvis", -1, new Vector3(0f, 2f, 0f)),
                ("Hip", 0, Vector3.Zero),
                ("Knee", 1, new Vector3(0f, -1f, 0f)),
                ("Ankle", 2, new Vector3(0f, -1f, 0f))
            )
        );

        var pose = new SkeletonPose(rig);
        var model = new BoneTransform[rig.JointCount];
        var before = TestRigs.ModelPositions(pose)[3];

        var placement = new FootPlacement(0, [new(1, 2, 3, new(0f, 1f, 10f), 0f)]) { Weight = 0f };
        placement.Solve(rig, pose.Bones, model, [new(true, new Vector3(0f, -0.5f, 0f), Vector3.Up)]);

        TestRigs.Near(before, TestRigs.ModelPositions(pose)[3]);
    }
}
