// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Animation.Tests;

public class RootMotionTests {
    readonly Skeleton skeleton = TestRigs.Chain();

    AnimationClip Walk() =>
        AnimationClip.Create(
            TestRigs.Translate("Walk", "Root", Vector3.Zero, new(0f, 0f, -4f)),
            skeleton
        );

    AnimationClip TurnInPlace() =>
        AnimationClip.Create(
            TestRigs.Rotate(
                "Turn",
                "Root",
                Quaternion.Identity,
                Quaternion.FromAxisAngle(Vector3.UnitY, MathUtil.PiOverTwo)
            ),
            skeleton
        );

    [Fact]
    public void ExtractRootMotion_WithinOnePass_IsTheDifferenceBetweenTheTwoSamples() {
        var delta = Walk().ExtractRootMotion(0f, 0.5f);

        TestRigs.Near(new(0f, 0f, -2f), delta.Translation);
        TestRigs.Near(Quaternion.Identity, delta.Rotation);
    }

    [Fact]
    public void ExtractRootMotion_AcrossALoop_AddsTheTailAndTheHead() {
        var delta = Walk().ExtractRootMotion(0.75f, 0.25f, 1);

        TestRigs.Near(new(0f, 0f, -2f), delta.Translation);
    }

    [Fact]
    public void ExtractRootMotion_SeveralLoops_ScalesWithThem() {
        var one = Walk().ExtractRootMotion(0f, 1f);

        // Three wraps starting at zero and ending halfway: the tail of the first pass, two whole
        // ones, then half of the last — three and a half strides.
        var many = Walk().ExtractRootMotion(0f, 0.5f, 3);

        TestRigs.Near(new(0f, 0f, -4f), one.Translation);
        TestRigs.Near(new(0f, 0f, -14f), many.Translation);
    }

    [Fact]
    public void ExtractRootMotion_NoRootJoint_IsZero() {
        var clip = AnimationClip.Create(
            TestRigs.Translate("Walk", "Root", Vector3.Zero, new(0f, 0f, -4f)),
            skeleton,
            rootJoint: "NoSuchJoint"
        );

        Assert.Equal(-1, clip.RootJoint);
        Assert.True(clip.ExtractRootMotion(0f, 1f).IsZero);
    }

    [Fact]
    public void Between_IsExpressedInTheStartingFrame() {
        // Facing +X, then moving one metre along the original −Z. In the character's own frame that
        // is a metre to its own side, not a metre along the world's −Z.
        var from = new BoneTransform(
            Vector3.Zero,
            Quaternion.FromAxisAngle(Vector3.UnitY, -MathUtil.PiOverTwo),
            Vector3.One
        );

        var to = new BoneTransform(new Vector3(0f, 0f, -1f), from.Rotation, Vector3.One);
        var delta = RootMotionDelta.Between(from, to);

        TestRigs.Near(new(-1f, 0f, 0f), delta.Translation);
    }

    [Fact]
    public void Chain_TurnsTheSecondDeltaByTheFirstsRotation() {
        // Walk a metre forward, turn 90°, walk a metre forward again. The result must be an L, not
        // two metres in a line.
        var straight = new RootMotionDelta(new(0f, 0f, -1f), Quaternion.Identity);

        var turn = new RootMotionDelta(
            Vector3.Zero,
            Quaternion.FromAxisAngle(Vector3.UnitY, MathUtil.PiOverTwo)
        );

        var path = RootMotionDelta.Chain(RootMotionDelta.Chain(straight, turn), straight);

        TestRigs.Near(new(-1f, 0f, -1f), path.Translation);
    }

    [Fact]
    public void ExtractRootMotion_ARotatingRootAcrossALoop_DoesNotDrift() {
        // Four quarter turns is a whole turn, and it has to come out as one whatever order the
        // loop-crossing chain composed them in.
        var delta = TurnInPlace().ExtractRootMotion(0f, 0f, 4);

        TestRigs.Near(Quaternion.Identity, delta.Rotation);
    }

    [Fact]
    public void ToTransform_ComposedOntoALocalTransform_MovesAlongTheCharactersOwnFacing() {
        var facingLeft = new BoneTransform(
            new Vector3(5f, 0f, 0f),
            Quaternion.FromAxisAngle(Vector3.UnitY, MathUtil.PiOverTwo),
            Vector3.One
        );

        var forward = new RootMotionDelta(new(0f, 0f, -1f), Quaternion.Identity);
        var moved = BoneTransform.Concatenate(forward.ToTransform(), facingLeft);

        // Rotated 90° about +Y, the character's −Z points along −X.
        TestRigs.Near(new(4f, 0f, 0f), moved.Translation);
    }
}
