// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Ik;
using Vixen.Core.Mathematics;

namespace Vixen.Animation.Constraints;

/// <summary>One chain, and what the arbiter decided it should do.</summary>
/// <param name="Chain">Which joints may move.</param>
/// <param name="Position">Where the effector should end up, in model space.</param>
/// <param name="PositionWeight">How much of the way there to go, in <c>[0, 1]</c>.</param>
/// <param name="Rotation">What the effector's orientation should be, in model space.</param>
/// <param name="RotationWeight">How much of that orientation to force, in <c>[0, 1]</c>.</param>
/// <param name="Pole">A model-space point the middle joint bends towards. Zero keeps the current bend.</param>
/// <param name="EffectorOffset">
///     Where on the effector joint the point being placed is, in the joint's own space.
/// </param>
public readonly record struct ChainSolveRequest(
    ChainSpec Chain,
    Vector3 Position,
    float PositionWeight,
    Quaternion Rotation,
    float RotationWeight,
    Vector3 Pole,
    Vector3 EffectorOffset
);

/// <summary>How one chain is actually moved. The seam under the arbiter.</summary>
/// <remarks>
///     <para>
///         Separate from <see cref="IConstraintArbiter" /> because they answer different questions.
///         The arbiter decides <em>what</em> a chain should do when several goals want different
///         things; this decides <em>how</em> to make a particular chain do it, which is a question
///         about limb topology — an analytic solver for two bones, an iterative one for a spine, a
///         data-driven one for a tail.
///     </para>
/// </remarks>
public interface IChainSolver {
    /// <summary>Moves a chain.</summary>
    /// <param name="skeleton">The skeleton the pose belongs to.</param>
    /// <param name="request">What the chain should do.</param>
    /// <param name="local">The pose, in local space, written in place.</param>
    /// <param name="model">A model-space buffer of at least the skeleton's joint count.</param>
    /// <returns>Whether the chain could be solved at all.</returns>
    bool Solve(
        Skeleton skeleton,
        in ChainSolveRequest request,
        Span<BoneTransform> local,
        Span<BoneTransform> model
    );
}

/// <summary>The shipped solver: analytic for two bones, a swing for one, the last two for longer.</summary>
/// <remarks>
///     <para>
///         <b>Two bones is the case that matters and it has a closed form</b>, so
///         <see cref="TwoBoneIk" /> does the work and nothing here iterates. An arm and a leg are two
///         bones; so is almost everything an author puts a contact on.
///     </para>
///     <para>
///         ⚠ <b>A chain longer than two bones is solved over its last two, and that is a documented
///         limitation rather than a hidden one.</b> Distributing error up a spine towards the root is
///         a different and much larger solver, and it is what the seam exists for. What this does
///         instead is produce something reasonable and report the shortfall as a residual, so an
///         author sees a number rather than a limb that quietly did not reach.
///     </para>
/// </remarks>
public sealed class DefaultChainSolver : IChainSolver {
    /// <summary>The one every stack uses unless it is given another.</summary>
    public static DefaultChainSolver Shared { get; } = new();

    /// <inheritdoc />
    public bool Solve(
        Skeleton skeleton,
        in ChainSolveRequest request,
        Span<BoneTransform> local,
        Span<BoneTransform> model
    ) {
        ArgumentNullException.ThrowIfNull(skeleton);

        var effector = request.Chain.Effector;

        if ((uint)effector >= (uint)local.Length) {
            return false;
        }

        // The last two bones, which for a two-bone chain is the whole of it and for a longer one is
        // the documented fallback. A chain that names its own effector as its first joint is asking
        // for nothing above to move, so it takes the rotation-only path below.
        var single = request.Chain.First == effector;
        var mid = single ? -1 : skeleton.ParentOf(effector);
        var root = mid < 0 ? -1 : skeleton.ParentOf(mid);

        if (root >= 0 && request.PositionWeight > 0f) {
            return TwoBoneIk.Solve(
                skeleton,
                local,
                model,
                new(
                    root,
                    mid,
                    effector,
                    Target(skeleton, local, model, request),
                    request.Pole == Vector3.Zero
                        ? Pole(skeleton, local, model, root, mid, effector, request.Position)
                        : request.Pole,
                    request.Rotation,
                    request.PositionWeight,
                    request.RotationWeight
                )
            );
        }

        if (request.RotationWeight <= 0f) {
            return false;
        }

        // Nothing above the effector to bend, or nothing asked of its position: all that is left is
        // its own rotation, which is still worth applying — an orientation goal on a root joint is
        // the ordinary way to stop a head rolling with the body.
        SkeletonPose.ComputeModelSpace(skeleton, local, model);

        var parent = skeleton.ParentOf(effector);

        var parentRotation = parent < 0
            ? Quaternion.Identity
            : model[parent].Rotation;

        var desired = Quaternion.Concatenate(request.Rotation, Quaternion.Conjugate(parentRotation));
        local[effector].Rotation = Quaternion.Nlerp(local[effector].Rotation, desired, request.RotationWeight);

        return true;
    }

    /// <summary>
    ///     Where the <em>joint</em> has to go for the point on it to land where it was asked to.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>An effector offset is not a target offset.</b> A grip point 6 cm into the palm placed
    ///     at a handle means the wrist goes 6 cm short of the handle, along whatever direction the
    ///     wrist is currently facing. Subtracting the offset in model space is the whole of it, and
    ///     leaving it out puts every gripped prop through the hand it is held in.
    /// </remarks>
    static Vector3 Target(
        Skeleton skeleton,
        Span<BoneTransform> local,
        Span<BoneTransform> model,
        in ChainSolveRequest request
    ) {
        if (request.EffectorOffset == Vector3.Zero) {
            return request.Position;
        }

        SkeletonPose.ComputeModelSpace(skeleton, local, model);

        var rotation = request.RotationWeight > 0f
            ? request.Rotation
            : model[request.Chain.Effector].Rotation;

        return request.Position - Quaternion.Transform(request.EffectorOffset, rotation);
    }

    /// <summary>Which way the middle joint bends when nobody said.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A goal with no pole is the common case, and getting this wrong makes the solve do
    ///         nothing at all.</b> <see cref="TwoBoneIk" /> takes the bend plane from the pole, falls
    ///         back to the chain's own current plane, and refuses the solve when both are degenerate
    ///         — which a perfectly straight chain, which is what a bind pose usually is, makes them.
    ///         A "sensible" pole extrapolated along the chain is exactly the degenerate one.
    ///     </para>
    ///     <para>
    ///         So: <b>a chain that is already bent keeps bending the way it was</b> — the middle
    ///         joint's own position names the current plane, which is what stops a knee flipping as a
    ///         target swings behind the hip. <b>A straight one bends towards the target</b>, along
    ///         the component of the target that is perpendicular to the chain, which is what a person
    ///         reaching sideways does with their elbow.
    ///     </para>
    /// </remarks>
    static Vector3 Pole(
        Skeleton skeleton,
        Span<BoneTransform> local,
        Span<BoneTransform> model,
        int root,
        int mid,
        int tip,
        Vector3 target
    ) {
        SkeletonPose.ComputeModelSpace(skeleton, local, model);

        var from = model[root].Translation;
        var bend = model[mid].Translation;
        var axis = model[tip].Translation - from;

        if (Vector3.Cross(axis, bend - from).LengthSquared() > 1e-8f) {
            return bend;
        }

        var length = axis.LengthSquared();

        if (length <= 1e-8f) {
            return bend + Vector3.Up;
        }

        var toTarget = target - from;
        var sideways = toTarget - (axis * (Vector3.Dot(toTarget, axis) / length));

        if (sideways.LengthSquared() > 1e-8f) {
            return bend + Vector3.Normalize(sideways);
        }

        // The target is straight down the chain, so no direction is better than another. Any
        // perpendicular keeps the solve from refusing; picking one deterministically keeps two
        // machines agreeing about which way the elbow went.
        var straight = Vector3.Normalize(axis);

        var reference = MathF.Abs(Vector3.Dot(straight, Vector3.Up)) > 0.99f
            ? Vector3.Forward
            : Vector3.Up;

        return bend + Vector3.Normalize(Vector3.Cross(straight, reference));
    }
}
