// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Moves;
using Vixen.Core.Mathematics;

namespace Vixen.Animation.Constraints;

/// <summary>The labels the stage reserves, because they name where a goal is solved.</summary>
/// <remarks>
///     ⚠ <b>Two labels are not ordinary labels.</b> A goal wearing one of these is solved by a
///     different pass and is <em>excluded</em> from every other, so a project that used
///     <c>root</c> to mean something of its own would find those goals silently moving the character
///     instead of its limbs. Everything else about a label — suppression, querying — works the same
///     way on these.
/// </remarks>
public static class ConstraintLabels {
    /// <summary>Solved as the character's placement, before the pose, and not by the pose solve.</summary>
    public static Symbol Root { get; } = Symbol.Intern("root");

    /// <summary>Solved as the camera's placement, after the shot, and nowhere else.</summary>
    public static Symbol Camera { get; } = Symbol.Intern("camera");
}

/// <summary>Moves one rigid transform to satisfy a set of goals. No chain, no hierarchy.</summary>
/// <remarks>
///     <para>
///         <b>The same solve twice over, which is the argument for the type existing.</b> A
///         character's root placement and a camera are the same problem: a single transform with
///         position, orientation and aim goals and nothing below it. Writing it once means the
///         camera inherits regions, additive goals, weights and priority for free, and means a bug in
///         the averaging is one bug.
///     </para>
///     <para>
///         ⚠ <b>Distance goals are ignored here and that is deliberate.</b> A distance goal is about
///         the separation of two <em>joints</em>; a body with no joints has no pair for it to be
///         about, and quietly reinterpreting it as a distance from the origin would be a different
///         constraint wearing the same name.
///     </para>
/// </remarks>
public static class RigidBodySolver {
    const float Epsilon = 1e-5f;

    /// <summary>Works out where a body should be.</summary>
    /// <param name="body">Where it is now.</param>
    /// <param name="goals">What is asked of it, resolved.</param>
    /// <param name="moved">How far it had to move, in metres.</param>
    /// <param name="turned">How far it had to turn, in radians.</param>
    /// <returns>Where it should be.</returns>
    /// <remarks>
    ///     The same rules as <see cref="DefaultConstraintArbiter" /> — absolute goals averaged by
    ///     weight, additive ones summed on top, priority taking its share off the top, position then
    ///     orientation then aim — because a body is not a special case of arbitration, only of
    ///     topology.
    /// </remarks>
    public static BoneTransform Solve(
        in BoneTransform body,
        ReadOnlySpan<ResolvedGoal> goals,
        out float moved,
        out float turned
    ) {
        var position = body.Translation;
        var rotation = body.Rotation;

        moved = 0f;
        turned = 0f;

        if (goals.IsEmpty) {
            return body;
        }

        var share = 0f;
        var target = Vector3.Zero;
        var additive = Vector3.Zero;

        for (var index = 0; index < goals.Length; index++) {
            if (goals[index].Goal is not PositionGoal goal) {
                continue;
            }

            var weight = DefaultConstraintArbiter.Share(goals, 0, goals.Length, index, new(position, rotation, Vector3.One));

            if (weight <= Epsilon) {
                continue;
            }

            var here = position + Quaternion.Transform(goal.EffectorOffset, rotation);

            if (goal.Mode is GoalMode.Additive) {
                additive += goals[index].Frame.DirectionToModel(goal.Offset) * weight;
                continue;
            }

            share += weight;
            target += goal.Nearest(goals[index].Frame, here) * weight;
        }

        if (share > Epsilon || additive != Vector3.Zero) {
            var wanted = share > Epsilon
                ? Vector3.Lerp(position, target / share, MathUtil.Saturate(share))
                : position;

            var next = wanted + additive;

            moved = (next - position).Length();
            position = next;
        }

        var orientation = 0f;
        var facing = rotation;

        for (var index = 0; index < goals.Length; index++) {
            if (goals[index].Goal is not OrientationGoal goal) {
                continue;
            }

            var weight = DefaultConstraintArbiter.Share(goals, 0, goals.Length, index, new(position, facing, Vector3.One));

            if (weight <= Epsilon) {
                continue;
            }

            if (goal.Mode is GoalMode.Additive) {
                facing = Quaternion.Concatenate(facing, AimGoal.ScaleRotation(goal.Rotation, weight));
                continue;
            }

            orientation += weight;
            facing = Quaternion.Nlerp(facing, Quaternion.Concatenate(goal.Rotation, goals[index].Frame.Rotation), weight / orientation);
        }

        if (orientation > Epsilon) {
            facing = Quaternion.Nlerp(rotation, facing, MathUtil.Saturate(orientation));
        }

        for (var index = 0; index < goals.Length; index++) {
            if (goals[index].Goal is not AimGoal goal) {
                continue;
            }

            var weight = DefaultConstraintArbiter.Share(goals, 0, goals.Length, index, new(position, facing, Vector3.One));

            if (weight <= Epsilon) {
                continue;
            }

            if (goal.Mode is GoalMode.Additive) {
                facing = Quaternion.Concatenate(facing, AimGoal.ScaleRotation(goal.Deviation, weight));
                continue;
            }

            var origin = position + Quaternion.Transform(goal.Origin, facing);
            var toTarget = goal.Target(goals[index].Frame, origin) - origin;

            if (toTarget.LengthSquared() <= Epsilon) {
                continue;
            }

            var swing = Quaternion.FromToRotation(
                Quaternion.Transform(goal.Axis, facing),
                Vector3.Normalize(toTarget)
            );

            // Aim runs in whatever freedom orientation left, exactly as it does on a chain.
            var freedom = MathUtil.Saturate(weight) * (1f - MathUtil.Saturate(orientation));

            facing = Quaternion.Concatenate(facing, AimGoal.ScaleRotation(swing, freedom));
        }

        turned = 2f * MathF.Acos(MathF.Abs(MathUtil.Clamp(Quaternion.Dot(Quaternion.Normalize(rotation), Quaternion.Normalize(facing)), -1f, 1f)));

        return new(position, facing, body.Scale);
    }
}
