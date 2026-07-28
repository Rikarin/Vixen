// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Animation.Tests;

public class PoseBlendTests {
    readonly Skeleton skeleton = TestRigs.Branching();

    static BoneTransform[] Uniform(int count, float x) {
        var pose = new BoneTransform[count];

        for (var index = 0; index < count; index++) {
            pose[index] = new(new Vector3(x, 0f, 0f), Quaternion.Identity, Vector3.One);
        }

        return pose;
    }

    [Fact]
    public void Lerp_Halfway_IsTheMidpoint() {
        var from = Uniform(3, 0f);
        var to = Uniform(3, 10f);
        var result = new BoneTransform[3];

        PoseBlend.Lerp(result, from, to, 0.5f);

        TestRigs.Near(new(5f, 0f, 0f), result[0].Translation);
    }

    [Fact]
    public void Average_ThreePoses_IsTheWeightedMeanAndIsOrderIndependent() {
        var count = skeleton.JointCount;
        var a = Uniform(count, 0f);
        var b = Uniform(count, 4f);
        var c = Uniform(count, 10f);

        var forward = new BoneTransform[count];
        var accumulator = PoseBlend.Average(forward);
        accumulator.Add(a, 1f);
        accumulator.Add(b, 2f);
        accumulator.Add(c, 1f);
        Assert.True(accumulator.Finish([]));

        var backward = new BoneTransform[count];
        var reversed = PoseBlend.Average(backward);
        reversed.Add(c, 1f);
        reversed.Add(b, 2f);
        reversed.Add(a, 1f);
        reversed.Finish([]);

        // (0 + 4·2 + 10) / 4
        TestRigs.Near(new(4.5f, 0f, 0f), forward[0].Translation);
        TestRigs.Near(forward[0].Translation, backward[0].Translation);
    }

    [Fact]
    public void Average_NothingAdded_ReportsSoAndTakesTheFallback() {
        var count = skeleton.JointCount;
        var destination = new BoneTransform[count];
        var fallback = Uniform(count, 7f);

        var accumulator = PoseBlend.Average(destination);
        accumulator.Add(fallback, 0f);

        Assert.False(accumulator.Finish(fallback));
        TestRigs.Near(new(7f, 0f, 0f), destination[0].Translation);
    }

    [Fact]
    public void LerpMasked_UpperBodyMask_ReplacesTheBranchAndLeavesTheRest() {
        var upper = BoneMask.Excluding(skeleton).Set("LeftArm", 1f).Build();
        var count = skeleton.JointCount;

        var lower = Uniform(count, 0f);
        var action = Uniform(count, 10f);
        var result = new BoneTransform[count];

        PoseBlend.LerpMasked(result, lower, action, upper);

        Assert.Equal(0f, result[skeleton.IndexOf("Root")].Translation.X);
        Assert.Equal(0f, result[skeleton.IndexOf("RightArm")].Translation.X);
        Assert.Equal(10f, result[skeleton.IndexOf("LeftArm")].Translation.X);
        Assert.Equal(10f, result[skeleton.IndexOf("LeftHand")].Translation.X);
    }

    [Fact]
    public void BoneMask_SetWithoutDescendants_ReachesOnlyTheJointNamed() {
        var mask = BoneMask.Excluding(skeleton).Set("LeftArm", 1f, includeDescendants: false).Build();

        Assert.Equal(1f, mask[skeleton.IndexOf("LeftArm")]);
        Assert.Equal(0f, mask[skeleton.IndexOf("LeftHand")]);
    }

    [Fact]
    public void BoneMask_UnknownJoint_IsIgnoredRatherThanThrowing() {
        var mask = BoneMask.Including(skeleton).Set("Tail", 0f).Build();

        foreach (var weight in mask.Weights) {
            Assert.Equal(1f, weight);
        }
    }

    [Fact]
    public void BoneMask_PartialWeight_SpreadsTheSeamAcrossJoints() {
        var mask = BoneMask.Excluding(skeleton)
            .Set("Spine", 0.5f)
            .Set("LeftArm", 1f)
            .Build();

        Assert.Equal(0f, mask[skeleton.IndexOf("Root")]);
        Assert.Equal(0.5f, mask[skeleton.IndexOf("Spine")]);
        Assert.Equal(1f, mask[skeleton.IndexOf("LeftArm")]);
        Assert.Equal(0.5f, mask[skeleton.IndexOf("RightArm")]);
    }

    [Fact]
    public void MakeAdditive_ThenAdd_ReproducesThePose() {
        var count = skeleton.JointCount;
        var reference = Uniform(count, 1f);
        var posed = Uniform(count, 4f);
        var difference = new BoneTransform[count];
        var restored = new BoneTransform[count];

        PoseBlend.MakeAdditive(difference, posed, reference);
        reference.CopyTo(restored.AsSpan());
        PoseBlend.Add(restored, difference, 1f);

        TestRigs.Near(posed[0].Translation, restored[0].Translation);
    }

    [Fact]
    public void Add_ThroughAMask_ReachesOnlyTheMaskedJoints() {
        var count = skeleton.JointCount;
        var mask = BoneMask.Excluding(skeleton).Set("RightArm", 1f).Build();

        var difference = Uniform(count, 3f);
        var destination = Uniform(count, 0f);

        PoseBlend.Add(destination, difference, 1f, mask);

        Assert.Equal(0f, destination[skeleton.IndexOf("LeftArm")].Translation.X);
        Assert.Equal(3f, destination[skeleton.IndexOf("RightArm")].Translation.X);
        Assert.Equal(3f, destination[skeleton.IndexOf("RightHand")].Translation.X);
    }

    [Fact]
    public void PoseScratch_NestedRentals_ReuseBuffersAndDoNotAlias() {
        var scratch = new PoseScratch(4);

        using (var outer = scratch.Rent()) {
            outer.Pose[0].Translation = new(1f, 0f, 0f);

            using var inner = scratch.Rent();
            inner.Pose[0].Translation = new(2f, 0f, 0f);

            Assert.Equal(1f, outer.Pose[0].Translation.X);
            Assert.Equal(2, scratch.Capacity);
        }

        // Returned, so the next rental is the same array rather than a third one.
        using var again = scratch.Rent();
        Assert.Equal(2, scratch.Capacity);
    }
}
