// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Xunit;

namespace Vixen.Animation.Tests;

/// <summary>
///     The skeletons and clips the suite animates. One file, so a test reading a joint index can see
///     what it points at.
/// </summary>
/// <remarks>
///     The rigs are deliberately trivial and exactly measurable: a chain up the Y axis with
///     unit-length bones, so a model-space position is a whole number and an assertion about a solved
///     pose can be written by hand rather than recorded from the implementation.
/// </remarks>
static class TestRigs {
    /// <summary>How close two floats have to be for a test to call them equal.</summary>
    public const float Tolerance = 1e-4f;

    /// <summary>
    ///     A three-joint chain up the Y axis: <c>Root</c> at the origin, <c>Mid</c> a metre above it,
    ///     <c>Tip</c> a metre above that.
    /// </summary>
    public static Skeleton Chain() =>
        Skeleton.Create(
            Build(
                "Chain",
                ("Root", -1, Vector3.Zero),
                ("Mid", 0, Vector3.UnitY),
                ("Tip", 1, Vector3.UnitY)
            )
        );

    /// <summary>
    ///     A rig with two branches off a shared spine: <c>Root</c>, <c>Spine</c>, then <c>LeftArm</c>
    ///     and <c>RightArm</c>. What a mask has to be able to tell apart.
    /// </summary>
    public static Skeleton Branching() =>
        Skeleton.Create(
            Build(
                "Branching",
                ("Root", -1, Vector3.Zero),
                ("Spine", 0, Vector3.UnitY),
                ("LeftArm", 1, new Vector3(-1f, 0f, 0f)),
                ("LeftHand", 2, new Vector3(-1f, 0f, 0f)),
                ("RightArm", 1, new Vector3(1f, 0f, 0f)),
                ("RightHand", 4, new Vector3(1f, 0f, 0f))
            )
        );

    /// <summary>
    ///     Builds skeleton data from local bind offsets, inverting the model-space bind poses the way
    ///     an importer would.
    /// </summary>
    public static SkeletonData Build(string name, params (string Name, int Parent, Vector3 Offset)[] joints) {
        var model = new Matrix4x4[joints.Length];
        var built = new SkeletonJoint[joints.Length];

        for (var index = 0; index < joints.Length; index++) {
            var (jointName, parent, offset) = joints[index];
            var local = Matrix4x4.FromTranslation(offset);

            model[index] = parent < 0 ? local : local * model[parent];

            Matrix4x4.Invert(model[index], out var inverse);
            built[index] = new() { Name = jointName, Parent = parent, InverseBindPose = inverse };
        }

        return new() { Name = name, Joints = built };
    }

    /// <summary>
    ///     Builds skeleton data whose bind pose has orientation as well as offset — the rig shape
    ///     that tells an A-pose from a T-pose, and the only one that exercises retargeting's
    ///     model-space transfer.
    /// </summary>
    public static SkeletonData BuildPosed(
        string name,
        params (string Name, int Parent, Vector3 Offset, Quaternion Rotation)[] joints
    ) {
        var model = new Matrix4x4[joints.Length];
        var built = new SkeletonJoint[joints.Length];

        for (var index = 0; index < joints.Length; index++) {
            var (jointName, parent, offset, rotation) = joints[index];
            var local = Matrix4x4.Compose(Vector3.One, rotation, offset);

            model[index] = parent < 0 ? local : local * model[parent];

            Matrix4x4.Invert(model[index], out var inverse);
            built[index] = new() { Name = jointName, Parent = parent, InverseBindPose = inverse };
        }

        return new() { Name = name, Joints = built };
    }

    /// <summary>A clip that moves one joint's translation linearly between two keys.</summary>
    public static AnimationClipData Translate(
        string name,
        string joint,
        Vector3 from,
        Vector3 to,
        float duration = 1f
    ) =>
        new() {
            Name = name,
            Duration = duration,
            Channels = [
                new() {
                    Target = joint,
                    PositionTimes = [0f, duration],
                    Positions = [from, to]
                }
            ]
        };

    /// <summary>A clip that rotates one joint about an axis between two keys.</summary>
    public static AnimationClipData Rotate(
        string name,
        string joint,
        Quaternion from,
        Quaternion to,
        float duration = 1f
    ) =>
        new() {
            Name = name,
            Duration = duration,
            Channels = [
                new() {
                    Target = joint,
                    RotationTimes = [0f, duration],
                    Rotations = [from, to]
                }
            ]
        };

    /// <summary>A clip holding one joint at a constant translation.</summary>
    public static AnimationClipData Hold(string name, string joint, Vector3 at, float duration = 1f) =>
        Translate(name, joint, at, at, duration);

    /// <summary>Model-space positions of every joint in a pose.</summary>
    public static Vector3[] ModelPositions(SkeletonPose pose) {
        var model = new BoneTransform[pose.JointCount];
        pose.ComputeModelSpace(model);

        var positions = new Vector3[pose.JointCount];

        for (var index = 0; index < positions.Length; index++) {
            positions[index] = model[index].Translation;
        }

        return positions;
    }

    /// <summary>Asserts two vectors agree to <see cref="Tolerance" />.</summary>
    public static void Near(Vector3 expected, Vector3 actual, string because = "") {
        Assert.True(
            Vector3.NearEqual(expected, actual, Tolerance),
            $"expected {expected}, got {actual}. {because}"
        );
    }

    /// <summary>Asserts two rotations are the same rotation to <see cref="Tolerance" />.</summary>
    public static void Near(Quaternion expected, Quaternion actual, string because = "") {
        Assert.True(
            Quaternion.SameRotation(expected, actual, Tolerance),
            $"expected {expected}, got {actual}. {because}"
        );
    }
}
