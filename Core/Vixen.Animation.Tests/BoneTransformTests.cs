// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Animation.Tests;

public class BoneTransformTests {
    static readonly BoneTransform Sample = new(
        new Vector3(1f, 2f, 3f),
        Quaternion.FromAxisAngle(Vector3.Normalize(new(1f, 2f, 3f)), 0.9f),
        new Vector3(2f, 2f, 2f)
    );

    [Fact]
    public void Concatenate_MatchesMatrixMultiplicationInRowVectorOrder() {
        var parent = new BoneTransform(
            new Vector3(0f, 1f, 0f),
            Quaternion.FromAxisAngle(Vector3.UnitZ, MathUtil.PiOverTwo),
            Vector3.One
        );

        var child = new BoneTransform(new Vector3(1f, 0f, 0f), Quaternion.Identity, Vector3.One);
        var composed = BoneTransform.Concatenate(child, parent);

        // local * parent — see Vixen.Core.Mathematics/Conventions.md.
        var expected = child.ToMatrix() * parent.ToMatrix();

        Assert.True(Matrix4x4.NearEqual(expected, composed.ToMatrix(), TestRigs.Tolerance));
        TestRigs.Near(new(0f, 2f, 0f), composed.Translation);
    }

    [Fact]
    public void Inverse_ComposedWithItself_IsTheIdentity() {
        var identity = BoneTransform.Concatenate(Sample, BoneTransform.Inverse(Sample));

        TestRigs.Near(Vector3.Zero, identity.Translation);
        TestRigs.Near(Quaternion.Identity, identity.Rotation);
        TestRigs.Near(Vector3.One, identity.Scale);
    }

    [Fact]
    public void Inverse_ZeroScale_InvertsToZeroRatherThanInfinity() {
        var flattened = new BoneTransform(Vector3.Zero, Quaternion.Identity, new(1f, 0f, 1f));
        var inverse = BoneTransform.Inverse(flattened);

        Assert.Equal(0f, inverse.Scale.Y);
        Assert.False(float.IsInfinity(inverse.Scale.Y));
    }

    [Fact]
    public void Lerp_AtTheEndpoints_ReturnsTheEndpoints() {
        var other = new BoneTransform(Vector3.Zero, Quaternion.Identity, Vector3.One);

        TestRigs.Near(Sample.Translation, BoneTransform.Lerp(Sample, other, 0f).Translation);
        TestRigs.Near(other.Translation, BoneTransform.Lerp(Sample, other, 1f).Translation);
    }

    [Fact]
    public void Lerp_OutsideZeroToOne_IsClampedRatherThanExtrapolated() {
        var other = new BoneTransform(new Vector3(10f, 0f, 0f), Quaternion.Identity, Vector3.One);

        TestRigs.Near(other.Translation, BoneTransform.Lerp(Sample, other, 5f).Translation);
        TestRigs.Near(Sample.Translation, BoneTransform.Lerp(Sample, other, -5f).Translation);
    }

    [Fact]
    public void Add_ADifferenceAtFullWeight_ReproducesThePoseItWasTakenFrom() {
        var reference = new BoneTransform(
            new Vector3(0f, 1f, 0f),
            Quaternion.FromAxisAngle(Vector3.UnitX, 0.3f),
            Vector3.One
        );

        var difference = BoneTransform.Difference(Sample, reference);
        var restored = BoneTransform.Add(reference, difference, 1f);

        TestRigs.Near(Sample.Translation, restored.Translation);
        TestRigs.Near(Sample.Rotation, restored.Rotation);
        TestRigs.Near(Sample.Scale, restored.Scale);
    }

    [Fact]
    public void Add_AtZeroWeight_ChangesNothing() {
        var difference = BoneTransform.Difference(Sample, BoneTransform.Identity);
        var unchanged = BoneTransform.Add(Sample, difference, 0f);

        TestRigs.Near(Sample.Translation, unchanged.Translation);
        TestRigs.Near(Sample.Rotation, unchanged.Rotation);
    }

    [Fact]
    public void Add_ScalesTheDifferenceRatherThanBlendingTowardsIt() {
        // The distinction additive layers exist for: an aim offset at 40 % aims 40 % of the way,
        // whatever the pose underneath is doing.
        var lean = new BoneTransform(
            new Vector3(0f, 0f, 0f),
            Quaternion.FromAxisAngle(Vector3.UnitZ, 1f),
            Vector3.One
        );

        var difference = BoneTransform.Difference(lean, BoneTransform.Identity);
        var underneath = new BoneTransform(
            new Vector3(5f, 0f, 0f),
            Quaternion.FromAxisAngle(Vector3.UnitZ, 0.5f),
            Vector3.One
        );

        var result = BoneTransform.Add(underneath, difference, 0.4f);

        // The translation underneath is kept exactly — an additive difference of zero adds zero.
        TestRigs.Near(underneath.Translation, result.Translation);

        // And the rotation is the pose underneath plus 40 % of the lean, not 40 % of the way from
        // one to the other. Loose to a hundredth of a radian, because the weighting goes through
        // nlerp rather than slerp — see BoneTransform.Lerp on why that trade is taken.
        Assert.Equal(0.5f + 0.4f, result.Rotation.Angle(), 2);
    }

    [Fact]
    public void FromMatrix_RoundTripsToMatrix() {
        var back = BoneTransform.FromMatrix(Sample.ToMatrix());

        TestRigs.Near(Sample.Translation, back.Translation);
        TestRigs.Near(Sample.Rotation, back.Rotation);
        TestRigs.Near(Sample.Scale, back.Scale);
    }
}
