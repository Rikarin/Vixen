// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Xunit;

namespace Vixen.Animation.Tests;

public class SkeletonTests {
    [Fact]
    public void Create_Chain_DerivesLocalBindPoseFromInverseModelSpace() {
        var skeleton = TestRigs.Chain();

        Assert.Equal(3, skeleton.JointCount);
        TestRigs.Near(Vector3.Zero, skeleton.BindPose[0].Translation);
        TestRigs.Near(Vector3.UnitY, skeleton.BindPose[1].Translation);
        TestRigs.Near(Vector3.UnitY, skeleton.BindPose[2].Translation);
    }

    [Fact]
    public void BindPose_ComposedToModelSpace_ReproducesTheInverseBindPoses() {
        var skeleton = TestRigs.Chain();
        var pose = new SkeletonPose(skeleton);
        var positions = TestRigs.ModelPositions(pose);

        TestRigs.Near(Vector3.Zero, positions[0]);
        TestRigs.Near(new(0f, 1f, 0f), positions[1]);
        TestRigs.Near(new(0f, 2f, 0f), positions[2]);
    }

    [Fact]
    public void ComputeSkinningMatrices_BindPose_IsIdentityForEveryJoint() {
        var skeleton = TestRigs.Chain();
        var pose = new SkeletonPose(skeleton);
        var palette = new Matrix4x4[skeleton.JointCount];

        pose.ComputeSkinningMatrices(palette);

        foreach (var matrix in palette) {
            Assert.True(Matrix4x4.NearEqual(Matrix4x4.Identity, matrix, TestRigs.Tolerance));
        }
    }

    [Fact]
    public void IndexOf_KnownAndUnknownNames_ResolvesOrReturnsMinusOne() {
        var skeleton = TestRigs.Branching();

        Assert.Equal(0, skeleton.IndexOf("Root"));
        Assert.Equal(3, skeleton.IndexOf("LeftHand"));
        Assert.Equal(-1, skeleton.IndexOf("Tail"));
    }

    [Fact]
    public void IsDescendantOf_AcrossBranches_OnlyFollowsTheChain() {
        var skeleton = TestRigs.Branching();
        var leftHand = skeleton.IndexOf("LeftHand");
        var leftArm = skeleton.IndexOf("LeftArm");
        var rightArm = skeleton.IndexOf("RightArm");

        Assert.True(skeleton.IsDescendantOf(leftHand, leftArm));
        Assert.True(skeleton.IsDescendantOf(leftHand, 0));
        Assert.True(skeleton.IsDescendantOf(leftArm, leftArm));
        Assert.False(skeleton.IsDescendantOf(leftHand, rightArm));
    }

    [Fact]
    public void TryCreate_ChildBeforeParent_IsRejectedWithTheJointNamed() {
        var data = new SkeletonData {
            Name = "Backwards",
            Joints = [
                new() { Name = "Child", Parent = 1 },
                new() { Name = "Parent", Parent = -1 }
            ]
        };

        Assert.False(Skeleton.TryCreate(data, out var skeleton, out var error));
        Assert.Null(skeleton);
        Assert.Contains("Child", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryCreate_UnnamedJoint_IsRejected() {
        var data = new SkeletonData {
            Name = "Anonymous",
            Joints = [new() { Name = "", Parent = -1 }]
        };

        Assert.False(Skeleton.TryCreate(data, out _, out var error));
        Assert.Contains("no name", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ComputeLocalSpace_RoundTripsComputeModelSpace() {
        var skeleton = TestRigs.Branching();
        var pose = new SkeletonPose(skeleton);

        pose[1].Rotation = Quaternion.FromAxisAngle(Vector3.UnitZ, 0.7f);
        pose[2].Translation = new(-1.5f, 0.25f, 0f);

        var model = new BoneTransform[skeleton.JointCount];
        var back = new BoneTransform[skeleton.JointCount];

        pose.ComputeModelSpace(model);
        SkeletonPose.ComputeLocalSpace(skeleton, model, back);

        for (var index = 0; index < skeleton.JointCount; index++) {
            TestRigs.Near(pose[index].Translation, back[index].Translation, $"joint {index}");
            TestRigs.Near(pose[index].Rotation, back[index].Rotation, $"joint {index}");
        }
    }
}
