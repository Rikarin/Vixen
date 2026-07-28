// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Animation.Ik;

/// <summary>One joint of a look-at chain, and how much of the turn it takes.</summary>
/// <param name="Joint">The joint's index.</param>
/// <param name="Weight">
///     How much of the way to the target this joint turns, in <c>[0, 1]</c>.
/// </param>
/// <param name="MaxAngle">
///     How far it may turn from where the animation put it, in radians. Zero means unlimited.
/// </param>
/// <remarks>
///     The weights are what make a look-at read as a body rather than as a head on a stick. A chest
///     at 0.2, a neck at 0.4 and a head at 0.8 turn together, each contributing what it can; the
///     head alone at 1.0 gives the owl.
/// </remarks>
public readonly record struct LookAtJoint(int Joint, float Weight = 1f, float MaxAngle = 0f);

/// <summary>
///     Turns a chain of joints to face a point: the head-tracking solver, and the aim solver.
/// </summary>
/// <remarks>
///     <para>
///         <b>Distributed down the chain, from the root outwards.</b> Each joint turns towards the
///         target by its own weight; the joints after it inherit the turn and have less left to do.
///         That is why the joints must be listed parent-first and why each one's model transform is
///         recomputed as the pass goes: a chest that has already turned 20° means the neck is
///         starting from somewhere else than the pose said.
///     </para>
///     <para>
///         <b>The clamp is against the animated pose, not against the parent.</b> A limit expressed
///         relative to the parent would let a character with a turned chest look 90° further than
///         one without, which is not what a person authoring "the head may turn 70°" means. Measured
///         against what the animation asked for, the limit is the same wherever the body is.
///     </para>
///     <para>
///         There is no smoothing here and that is deliberate. A look-at that snaps as the target
///         moves is a target that is snapping; damping belongs to whatever decides where to look,
///         which is the only place that knows whether the character is meant to be startled.
///     </para>
/// </remarks>
public static class LookAtIk {
    const float Epsilon = 1e-6f;

    /// <summary>Turns a chain towards a point.</summary>
    /// <param name="skeleton">The skeleton the pose belongs to.</param>
    /// <param name="local">The pose, in local space, written in place.</param>
    /// <param name="model">A model-space buffer of at least the skeleton's joint count.</param>
    /// <param name="chain">
    ///     The joints, parent first — which, because a skeleton stores parents before children, is
    ///     the same as in increasing index order. A chain given the other way round turns the head
    ///     first and then the chest out from under it.
    /// </param>
    /// <param name="target">Where to look, in model space.</param>
    /// <param name="forward">
    ///     Which way a joint faces in its own space. <c>−Z</c> by default, which is the engine's
    ///     forward and what a head bone exported from a right-handed tool points along.
    /// </param>
    /// <param name="weight">A global multiplier on every joint's weight, in <c>[0, 1]</c>.</param>
    public static void Solve(
        Skeleton skeleton,
        Span<BoneTransform> local,
        Span<BoneTransform> model,
        ReadOnlySpan<LookAtJoint> chain,
        Vector3 target,
        Vector3 forward = default,
        float weight = 1f
    ) {
        ArgumentNullException.ThrowIfNull(skeleton);

        var global = MathUtil.Saturate(weight);

        if (global <= 0f || chain.IsEmpty) {
            return;
        }

        var axis = forward == Vector3.Zero ? Vector3.Forward : Vector3.Normalize(forward);

        SkeletonPose.ComputeModelSpace(skeleton, local, model);

        // Where the model-space buffer stopped being true, because a joint before it in the chain
        // turned. Refreshed lazily and only as far as the next chain joint needs — a look-at over
        // three joints must not cost three whole-skeleton passes.
        var stale = int.MaxValue;

        foreach (var link in chain) {
            var joint = link.Joint;
            var jointWeight = MathUtil.Saturate(link.Weight) * global;

            if (jointWeight <= 0f) {
                continue;
            }

            for (var index = stale; index <= joint; index++) {
                var above = skeleton.ParentOf(index);

                model[index] = above < 0
                    ? local[index]
                    : BoneTransform.Concatenate(local[index], model[above]);
            }

            stale = joint + 1;

            var parent = skeleton.ParentOf(joint);

            var parentModel = parent < 0
                ? BoneTransform.Identity
                : model[parent];

            var jointModel = model[joint];
            var toTarget = target - jointModel.Translation;

            if (toTarget.LengthSquared() <= Epsilon) {
                continue;
            }

            var facing = Quaternion.Transform(axis, jointModel.Rotation);
            var turn = Quaternion.FromToRotation(facing, Vector3.Normalize(toTarget));

            if (link.MaxAngle > 0f) {
                turn = ClampAngle(turn, link.MaxAngle);
            }

            turn = Quaternion.Nlerp(Quaternion.Identity, turn, jointWeight);

            var turnedModel = Quaternion.Concatenate(jointModel.Rotation, turn);
            local[joint].Rotation = Quaternion.Concatenate(turnedModel, Quaternion.Conjugate(parentModel.Rotation));

            // Everything after this joint in the chain composes off the updated value.
            model[joint] = new(jointModel.Translation, turnedModel, jointModel.Scale);
        }
    }

    static Quaternion ClampAngle(Quaternion rotation, float maxAngle) {
        var angle = rotation.Angle();

        if (angle <= maxAngle) {
            return rotation;
        }

        var axis = rotation.Axis();
        return axis.LengthSquared() <= Epsilon ? Quaternion.Identity : Quaternion.FromAxisAngle(axis, maxAngle);
    }
}
