// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Animation.Ik;

/// <summary>What a two-bone chain is being asked to do.</summary>
/// <param name="Root">The joint at the top of the chain — a shoulder, a hip.</param>
/// <param name="Mid">The joint in the middle — an elbow, a knee.</param>
/// <param name="Tip">The joint at the end — a wrist, an ankle.</param>
/// <param name="Position">Where the tip should be, in model space.</param>
/// <param name="Pole">
///     A point in model space the middle joint bends towards — where the elbow points.
/// </param>
/// <param name="Rotation">What the tip's orientation should be, in model space.</param>
/// <param name="PositionWeight">How much of the way to the target to go, in <c>[0, 1]</c>.</param>
/// <param name="RotationWeight">How much of the tip's orientation to force, in <c>[0, 1]</c>.</param>
/// <remarks>
///     <para>
///         <b>Model space, not world space.</b> The solver works on a pose, and a pose has no
///         opinion about where the character is. A caller with a world-space target — a ledge, a
///         ground hit — transforms it by the entity's inverse world matrix first, which is one
///         matrix inverse per character rather than one per joint.
///     </para>
///     <para>
///         <b>The pole is a point, not a direction.</b> A direction has to be re-expressed every
///         time the character turns; a point can be a socket on the character, a fixed spot in the
///         level, or a hand-authored offset from the root, and it stays meaningful in all three.
///     </para>
/// </remarks>
public readonly record struct TwoBoneIkTarget(
    int Root,
    int Mid,
    int Tip,
    Vector3 Position,
    Vector3 Pole,
    Quaternion Rotation = default,
    float PositionWeight = 1f,
    float RotationWeight = 0f
);

/// <summary>
///     Bends a two-bone chain so its end reaches a point: the arm and the leg solver, solved exactly
///     rather than iterated.
/// </summary>
/// <remarks>
///     <para>
///         <b>Analytic, and there is no reason for it not to be.</b> Two bones and a target is a
///         triangle with three known side lengths, so the interior angles come out of the law of
///         cosines in closed form — no iteration, no convergence threshold, no frame-rate-dependent
///         settling. CCD and FABRIK exist for chains longer than this, where there is no closed form;
///         using them on an elbow is paying for generality nothing needs and getting a solution that
///         is slightly different every frame.
///     </para>
///     <para>
///         <b>Only rotations change.</b> The local translations — the bone lengths — are left
///         exactly as the pose had them, which is what stops IK from stretching a character. A
///         target beyond the chain's reach gets a straightened limb pointing at it rather than a
///         longer limb, and that is the correct failure: the character visibly cannot reach, which
///         is information, where a stretched arm is a bug that looks like a bug.
///     </para>
///     <para>
///         The construction follows the standard analytic form (Ryan Juckett's, also in Unity's and
///         Unreal's two-bone constraints): correct the two interior angles to what the target's
///         distance demands, then swing the whole chain to point at the target.
///     </para>
/// </remarks>
public static class TwoBoneIk {
    const float Epsilon = 1e-5f;

    /// <summary>Solves one chain, writing the result back into the pose.</summary>
    /// <param name="skeleton">The skeleton the pose belongs to.</param>
    /// <param name="local">The pose, in local space, written in place.</param>
    /// <param name="model">
    ///     A model-space buffer of at least the skeleton's joint count. Filled by the solver; pass
    ///     the same one to consecutive solves and it is refilled each time.
    /// </param>
    /// <param name="target">What the chain should do.</param>
    /// <returns>
    ///     <see langword="false" /> if the chain is degenerate — a zero-length bone, or joints that
    ///     are not a parent chain — in which case the pose is untouched.
    /// </returns>
    public static bool Solve(
        Skeleton skeleton,
        Span<BoneTransform> local,
        Span<BoneTransform> model,
        in TwoBoneIkTarget target
    ) {
        ArgumentNullException.ThrowIfNull(skeleton);

        var weight = MathUtil.Saturate(target.PositionWeight);

        if (weight <= 0f) {
            return true;
        }

        if (skeleton.ParentOf(target.Tip) != target.Mid || skeleton.ParentOf(target.Mid) != target.Root) {
            return false;
        }

        SkeletonPose.ComputeModelSpace(skeleton, local, model);

        var rootModel = model[target.Root];
        var midModel = model[target.Mid];
        var a = rootModel.Translation;
        var b = midModel.Translation;
        var c = model[target.Tip].Translation;

        var upperLength = (b - a).Length();
        var lowerLength = (c - b).Length();

        if (upperLength <= Epsilon || lowerLength <= Epsilon) {
            return false;
        }

        // Just short of fully straight. Exactly straight is a valid pose and a singular one: the
        // plane the chain bends in is undefined there, so the next frame's pole would flip the knee.
        var reach = MathUtil.Clamp(
            (target.Position - a).Length(),
            Epsilon,
            (upperLength + lowerLength) - Epsilon
        );

        var toTip = c - a;
        var toMid = b - a;
        var toTarget = target.Position - a;

        var currentUpper = AngleBetween(toTip, toMid);
        var currentLower = AngleBetween(a - b, c - b);
        var swing = AngleBetween(toTip, toTarget);

        // Law of cosines, once per interior angle. These are the angles the triangle must have for
        // its third side to be `reach` long.
        var desiredUpper = SafeAcos(
            ((lowerLength * lowerLength) - (upperLength * upperLength) - (reach * reach))
            / (-2f * upperLength * reach)
        );

        var desiredLower = SafeAcos(
            ((reach * reach) - (upperLength * upperLength) - (lowerLength * lowerLength))
            / (-2f * upperLength * lowerLength)
        );

        // The plane the chain bends in. The pole decides it; a pole on the line through the chain
        // says nothing, and the chain's own current plane is the best available answer.
        var bendAxis = Vector3.Cross(toTip, target.Pole - a);

        if (bendAxis.LengthSquared() <= Epsilon) {
            bendAxis = Vector3.Cross(toTip, toMid);
        }

        if (bendAxis.LengthSquared() <= Epsilon) {
            return false;
        }

        bendAxis = Vector3.Normalize(bendAxis);

        var swingAxis = Vector3.Cross(toTip, toTarget);

        swingAxis = swingAxis.LengthSquared() <= Epsilon
            ? bendAxis
            : Vector3.Normalize(swingAxis);

        var bendRoot = Quaternion.FromAxisAngle(bendAxis, desiredUpper - currentUpper);
        var bendMid = Quaternion.FromAxisAngle(bendAxis, desiredLower - currentLower);
        var swingToTarget = Quaternion.FromAxisAngle(swingAxis, swing);

        // Model-space rotations after the correction. The root gets both the interior-angle change
        // and the swing that points the chain at the target; the middle joint gets its own interior
        // angle, and inherits the rest through its parent.
        var newRootModel = Quaternion.Concatenate(
            Quaternion.Concatenate(rootModel.Rotation, bendRoot),
            swingToTarget
        );

        var newMidLocal = Quaternion.Concatenate(
            Quaternion.Concatenate(midModel.Rotation, bendMid),
            Quaternion.Conjugate(rootModel.Rotation)
        );

        var parent = skeleton.ParentOf(target.Root);

        var parentRotation = parent < 0
            ? Quaternion.Identity
            : model[parent].Rotation;

        var solvedRootLocal = Quaternion.Concatenate(newRootModel, Quaternion.Conjugate(parentRotation));

        local[target.Root].Rotation = Quaternion.Nlerp(local[target.Root].Rotation, solvedRootLocal, weight);
        local[target.Mid].Rotation = Quaternion.Nlerp(local[target.Mid].Rotation, newMidLocal, weight);

        var rotationWeight = MathUtil.Saturate(target.RotationWeight);

        if (rotationWeight > 0f) {
            ApplyTipRotation(skeleton, local, model, target, rotationWeight);
        }

        return true;
    }

    static void ApplyTipRotation(
        Skeleton skeleton,
        Span<BoneTransform> local,
        Span<BoneTransform> model,
        in TwoBoneIkTarget target,
        float weight
    ) {
        // The chain moved, so the tip's parent is not where the model buffer says any more. One
        // recomposition of the two joints that changed is cheaper than a whole-skeleton pass and is
        // all that is needed to express the tip's model-space goal as a local rotation.
        var parent = skeleton.ParentOf(target.Root);

        var rootParent = parent < 0
            ? BoneTransform.Identity
            : model[parent];

        var rootModel = BoneTransform.Concatenate(local[target.Root], rootParent);
        var midModel = BoneTransform.Concatenate(local[target.Mid], rootModel);

        var desired = Quaternion.Concatenate(target.Rotation, Quaternion.Conjugate(midModel.Rotation));
        local[target.Tip].Rotation = Quaternion.Nlerp(local[target.Tip].Rotation, desired, weight);
    }

    static float AngleBetween(Vector3 from, Vector3 to) {
        var lengths = from.Length() * to.Length();
        return lengths <= Epsilon ? 0f : SafeAcos(Vector3.Dot(from, to) / lengths);
    }

    static float SafeAcos(float value) => MathF.Acos(MathUtil.Clamp(value, -1f, 1f));
}
