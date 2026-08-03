// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Animation.Constraints;

/// <summary>A goal that resolved, and how much of it applies this frame.</summary>
/// <param name="Goal">What it asks for.</param>
/// <param name="Frame">
///     Where it turned out to be, in model space — and for an additive goal, the frame its offset was
///     <em>measured against</em>, which is <see cref="ConstraintGoal.Reference" /> where one is set.
///     An absolute goal is placed in its frame; an additive one is only oriented by it.
/// </param>
/// <param name="Weight">
///     How much of it applies, in <c>[0, 1]</c> — the clip's blend weight, the tag's activation, the
///     handle's own weight, any suppression by label and the ease already multiplied together.
/// </param>
public readonly record struct ResolvedGoal(ConstraintGoal Goal, Frame Frame, float Weight);

/// <summary>Everything an arbiter is given, and everything it may write.</summary>
/// <remarks>
///     A <c>ref struct</c>, so nothing can hold one past the solve. The goals are sorted by chain,
///     which is what lets an arbiter walk them in one pass and treat each contiguous run as one
///     conversation.
/// </remarks>
public readonly ref struct ConstraintSolveContext {
    /// <summary>The skeleton being posed.</summary>
    public required Skeleton Skeleton { get; init; }

    /// <summary>A model-space buffer, valid on entry and rewritten freely.</summary>
    public required Span<BoneTransform> Model { get; init; }

    /// <summary>The goals that resolved, sorted by chain.</summary>
    public required ReadOnlySpan<ResolvedGoal> Goals { get; init; }

    /// <summary>Where each goal's residual goes. One slot per goal, in the same order.</summary>
    public required Span<ConstraintResidual> Residuals { get; init; }

    /// <summary>How a chain is actually moved.</summary>
    public required IChainSolver Solver { get; init; }

    /// <summary>The characters being solved together, if a scheduler grouped any.</summary>
    public ConstraintGroup Group { get; init; }

    /// <summary>How much time has passed, in seconds.</summary>
    public float DeltaTime { get; init; }
}

/// <summary>How two goals wanting the same joints resolve. The most important seam here.</summary>
/// <remarks>
///     <para>
///         Two goals wanting the same chain is the normal case, not the exception, and something has
///         to decide. <see cref="DefaultConstraintArbiter" /> is deliberately the predictable, cheap
///         one; a project that needs a staged solve — layered passes, error redistributed towards the
///         root, exact satisfaction of the top layer — installs its own and nothing else changes.
///     </para>
/// </remarks>
public interface IConstraintArbiter {
    /// <summary>Decides what every chain does, and does it.</summary>
    /// <param name="context">The goals, the pose, and what to solve them with.</param>
    /// <param name="pose">The pose, in local space, written in place.</param>
    void Solve(in ConstraintSolveContext context, Span<BoneTransform> pose);
}

/// <summary>The shipped arbiter: one weighted pass, stated with its limits.</summary>
/// <remarks>
///     <para>
///         <b>What it does.</b> Goals are grouped by the chain they move. Within a group, per kind,
///         the absolute goals are <b>averaged</b> by weight and the additive ones are <b>summed</b>
///         and applied on top — averaging two recoils would make them weaker than one, which is the
///         opposite of what additive means. Kinds are satisfied position → orientation → aim, each
///         within the freedom the previous left. Every goal reports a residual.
///     </para>
///     <para>
///         <b>How priority works.</b> A multiplier and a tie-break, not a hard ordering: the highest
///         priority present takes its share of the chain first, and the rest is what is left over. A
///         full-weight high-priority goal therefore wins outright, and a half-weight one leaves half
///         the chain to everybody else — which is continuous, so a priority that fades in does not
///         cause a jump.
///     </para>
///     <para>
///         ⚠ <b>What it is not, stated plainly because an author will hit all three.</b> It does not
///         guarantee that a high-priority goal is satisfied <em>exactly</em> when a low-priority one
///         conflicts; it does not distribute error up the hierarchy towards the root; and it does not
///         resolve cyclic goals — two hands each targeting the other — other than by the damping that
///         falls out of solving them in a fixed order. Those need a staged solve, which is a much
///         larger piece of work with a much larger authoring surface, and which is what
///         <see cref="IConstraintArbiter" /> is for.
///     </para>
///     <para>
///         ⚠ <b>There are no joint limits to clamp against yet.</b> The plan's rule says to clamp,
///         and the only limit the engine currently has is <c>TwoBoneIk</c>'s refusal to stretch a
///         bone. A skeleton that carried per-joint ranges would be clamped here; none does, and
///         pretending otherwise in a doc comment would be worse than saying so.
///     </para>
///     <para>
///         Stateless, and therefore safe to share across the animators the system evaluates in
///         parallel. Everything it accumulates is a local.
///     </para>
/// </remarks>
public sealed class DefaultConstraintArbiter : IConstraintArbiter {
    const float Epsilon = 1e-5f;

    /// <summary>The one every stack uses unless it is given another.</summary>
    public static DefaultConstraintArbiter Shared { get; } = new();

    /// <inheritdoc />
    public void Solve(in ConstraintSolveContext context, Span<BoneTransform> pose) {
        var goals = context.Goals;
        var start = 0;

        while (start < goals.Length) {
            var chain = goals[start].Goal.Solved;
            var end = start + 1;

            while (end < goals.Length && goals[end].Goal.Solved == chain) {
                end++;
            }

            SolveChain(context, pose, start, end, chain);
            start = end;
        }
    }

    static void SolveChain(
        in ConstraintSolveContext context,
        Span<BoneTransform> pose,
        int start,
        int end,
        ChainSpec chain
    ) {
        var joint = chain.Effector;

        if ((uint)joint >= (uint)context.Model.Length) {
            return;
        }

        var goals = context.Goals;
        var animated = context.Model[joint];
        var pole = Vector3.Zero;

        // Absolute contributions are averaged, so they need a running normalisation; additive ones
        // are summed, so they do not. Two accumulators per kind and no allocation anywhere.
        var positionShare = 0f;
        var positionTarget = Vector3.Zero;
        var positionAdditive = Vector3.Zero;
        var effectorOffset = Vector3.Zero;

        var orientationShare = 0f;
        var orientationTarget = Quaternion.Identity;
        var orientationAdditive = Quaternion.Identity;

        var aimShare = 0f;
        var aimTarget = Vector3.Zero;
        var aimAxis = Vector3.Forward;
        var aimOrigin = Vector3.Zero;
        var aimAdditive = Quaternion.Identity;

        for (var index = start; index < end; index++) {
            var resolved = goals[index];
            var goal = resolved.Goal;
            var weight = Share(goals, start, end, index, animated);

            // ⚠ A region goal that is already satisfied wins no share and is still being honoured, so
            // it reports its own weight rather than zero. Reporting the share would make "inside the
            // region" indistinguishable from "never ran", which is the one thing a residual has to
            // tell an author apart.
            var applied = weight > Epsilon
                ? weight
                : Slack(resolved, animated) ? MathUtil.Saturate(resolved.Weight) : 0f;

            context.Residuals[index] = new(goal.Kind, 0f, Vector3.Zero, applied);

            if (weight <= Epsilon) {
                continue;
            }

            switch (goal) {
                case PositionGoal position: {
                    if (position.Pole != Vector3.Zero) {
                        pole = position.Pole;
                    }

                    effectorOffset = position.EffectorOffset;
                    var here = Point(animated, position.EffectorOffset);

                    if (goal.Mode is GoalMode.Additive) {
                        positionAdditive += resolved.Frame.DirectionToModel(position.Offset) * weight;
                        break;
                    }

                    positionShare += weight;
                    positionTarget += position.Nearest(resolved.Frame, here) * weight;
                    break;
                }

                case DistanceGoal distance: {
                    if ((uint)distance.Other >= (uint)context.Model.Length) {
                        break;
                    }

                    var other = context.Model[distance.Other].Translation;
                    var separation = animated.Translation - other;
                    var length = separation.Length();

                    if (length <= Epsilon) {
                        break;
                    }

                    var excess = goal.Mode is GoalMode.Additive
                        ? -(distance.Min + distance.Max) * 0.5f
                        : distance.Excess(length);

                    if (MathF.Abs(excess) <= Epsilon) {
                        break;
                    }

                    // A separation is a position goal on the line joining the two joints. Folding it
                    // in here rather than solving it separately is what makes "two hands on a rifle,
                    // and the right one also on the trigger" one conversation instead of two passes
                    // that undo each other.
                    positionShare += weight;
                    positionTarget += (other + (separation / length * (length - excess))) * weight;
                    break;
                }

                case OrientationGoal orientation: {
                    var wanted = Quaternion.Concatenate(orientation.Rotation, resolved.Frame.Rotation);

                    if (goal.Mode is GoalMode.Additive) {
                        orientationAdditive = Quaternion.Concatenate(
                            orientationAdditive,
                            AimGoal.ScaleRotation(orientation.Rotation, weight)
                        );

                        break;
                    }

                    orientationShare += weight;
                    orientationTarget = Quaternion.Nlerp(orientationTarget, wanted, weight / orientationShare);
                    break;
                }

                case AimGoal aim: {
                    var origin = Point(animated, aim.Origin);

                    if (goal.Mode is GoalMode.Additive) {
                        aimAdditive = Quaternion.Concatenate(
                            aimAdditive,
                            AimGoal.ScaleRotation(aim.Deviation, weight)
                        );

                        break;
                    }

                    aimAxis = aim.Axis;
                    aimOrigin = aim.Origin;
                    aimShare += weight;
                    aimTarget += aim.Target(resolved.Frame, origin) * weight;
                    break;
                }
            }
        }

        var moved = false;

        if (positionShare > Epsilon || positionAdditive != Vector3.Zero) {
            var here = Point(animated, effectorOffset);

            // The weights are folded into the point rather than handed to the solver, so a goal at
            // half weight and a goal that is half satisfied mean the same thing, and an additive
            // offset composes on top without being weighted a second time.
            var blended = positionShare > Epsilon
                ? Vector3.Lerp(here, positionTarget / positionShare, MathUtil.Saturate(positionShare))
                : here;

            moved = context.Solver.Solve(
                context.Skeleton,
                new(chain, blended + positionAdditive, 1f, Quaternion.Identity, 0f, pole, effectorOffset),
                pose,
                context.Model
            );
        }

        if (orientationShare <= Epsilon
            && aimShare <= Epsilon
            && orientationAdditive == Quaternion.Identity
            && aimAdditive == Quaternion.Identity) {
            // ⚠ Clamped on this path too. A position goal on its own is the common case and it takes
            // this early return — leaving the clamp on the other branch alone meant limits applied to
            // a chain that also had an aim goal and to nothing else, which is the sort of bug that
            // looks like the limits working on the one rig somebody tested.
            Clamp(context, pose, chain);
            Report(context, pose, start, end, chain, animated);

            return;
        }

        if (moved) {
            // The chain moved, so where the effector faces and where it faces *from* both changed.
            // Aiming from where the hand used to be is the bug this line exists to prevent.
            SkeletonPose.ComputeModelSpace(context.Skeleton, pose, context.Model);
        }

        var current = context.Model[joint].Rotation;
        var rotation = current;

        if (orientationShare > Epsilon) {
            rotation = Quaternion.Nlerp(current, orientationTarget, MathUtil.Saturate(orientationShare));
        }

        rotation = Quaternion.Concatenate(rotation, orientationAdditive);

        if (aimShare > Epsilon) {
            // Aim runs in whatever freedom orientation left. An orientation goal at full weight has
            // already said which way the joint faces, and a full-strength aim on top of it would
            // simply overwrite that — which is not "satisfy both", it is "ignore the first".
            var origin = Point(context.Model[joint], aimOrigin);
            var facing = Quaternion.Transform(aimAxis, rotation);
            var toTarget = (aimTarget / aimShare) - origin;

            if (toTarget.LengthSquared() > Epsilon) {
                var swing = Quaternion.FromToRotation(facing, Vector3.Normalize(toTarget));
                var freedom = MathUtil.Saturate(aimShare) * (1f - MathUtil.Saturate(orientationShare));

                rotation = Quaternion.Concatenate(rotation, AimGoal.ScaleRotation(swing, freedom));
            }
        }

        rotation = Quaternion.Concatenate(rotation, aimAdditive);

        context.Solver.Solve(
            context.Skeleton,
            new(ChainSpec.Single(joint), Vector3.Zero, 0f, rotation, 1f, pole, Vector3.Zero),
            pose,
            context.Model
        );

        Clamp(context, pose, chain);
        Report(context, pose, start, end, chain, animated);
    }

    /// <summary>Brings every joint the solve moved back inside its range of motion.</summary>
    /// <returns>Whether anything was taken off.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>After the solve rather than inside it, and that is a limitation rather than a
    ///         design.</b> A solver that knew about limits could redistribute the shortfall — bend
    ///         more at the elbow because the shoulder ran out — and this cannot: it takes the
    ///         correction the solver produced and cuts the parts of it a joint may not do. What comes
    ///         out is a pose that is <em>legal</em> and further from the goal, reported as a larger
    ///         residual, which is the honest outcome. Redistribution is what a staged
    ///         <see cref="IConstraintArbiter" /> is for, and this class already says it does not do it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The model-space buffer is rebuilt when anything changed.</b> A clamped joint moves
    ///         everything below it, and <c>Report</c> measures the residual out of that buffer — so
    ///         skipping the recompute would report the miss the solver <em>thought</em> it had
    ///         achieved rather than the one the limits actually left.
    ///     </para>
    /// </remarks>
    static bool Clamp(in ConstraintSolveContext context, Span<BoneTransform> pose, ChainSpec chain) {
        if (!context.Skeleton.HasLimits) {
            return false;
        }

        var bind = context.Skeleton.BindPose;
        var clamped = false;

        // Upwards from the effector: that is the direction the parent links point, and the only walk
        // that terminates on a chain whose first joint is not actually an ancestor of its effector.
        for (var joint = chain.Effector; joint >= 0; joint = joint == chain.First ? -1 : context.Skeleton.ParentOf(joint)) {
            if ((uint)joint >= (uint)pose.Length) {
                break;
            }

            var limit = context.Skeleton.LimitOf(joint);

            if (limit.IsFree) {
                continue;
            }

            var local = pose[joint];
            var kept = limit.Clamp(local.Rotation, bind[joint].Rotation, out var cut);

            if (!cut) {
                continue;
            }

            pose[joint] = new(local.Translation, kept, local.Scale);
            clamped = true;
        }

        if (clamped) {
            SkeletonPose.ComputeModelSpace(context.Skeleton, pose, context.Model);
        }

        return clamped;
    }

    /// <summary>What one goal's slice of its chain is, after priority has taken its share.</summary>
    /// <remarks>
    ///     <para>
    ///         The highest priority present in the run takes its weight first; everything below it is
    ///         scaled by what is left. Continuous in the weights, so a priority that fades in does not
    ///         make the chain jump — which a hard ordering would.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A region goal that is already satisfied takes no share at all, and leaving that out
    ///         breaks the case regions exist for.</b> A world volume bounding where a camera may go is
    ///         a high-priority region goal, and it is satisfied almost all the time — but a satisfied
    ///         goal that still dominated the average would pin the camera wherever it happened to be
    ///         and starve the framing goal underneath it. "Satisfied anywhere inside" has to mean
    ///         <i>silent</i> anywhere inside, or a bound is a pin.
    ///     </para>
    /// </remarks>
    internal static float Share(
        ReadOnlySpan<ResolvedGoal> goals,
        int start,
        int end,
        int index,
        in BoneTransform effector
    ) {
        var goal = goals[index].Goal;
        var weight = MathUtil.Saturate(goals[index].Weight);

        if (weight <= Epsilon || Slack(goals[index], effector)) {
            return 0f;
        }

        var top = int.MinValue;

        for (var other = start; other < end; other++) {
            if (goals[other].Goal.Kind == goal.Kind && goals[other].Weight > Epsilon && !Slack(goals[other], effector)) {
                top = Math.Max(top, goals[other].Goal.Priority);
            }
        }

        if (goal.Priority >= top) {
            return weight;
        }

        var taken = 0f;

        for (var other = start; other < end; other++) {
            if (goals[other].Goal.Kind == goal.Kind
                && goals[other].Goal.Priority >= top
                && !Slack(goals[other], effector)) {
                taken += MathUtil.Saturate(goals[other].Weight);
            }
        }

        return weight * (1f - MathUtil.Saturate(taken));
    }

    /// <summary>Whether a goal is asking for nothing because it is already inside its region.</summary>
    static bool Slack(in ResolvedGoal resolved, in BoneTransform effector) {
        if (resolved.Goal is not PositionGoal position
            || position.Mode is GoalMode.Additive
            || position.Region == Vector3.Zero) {
            return false;
        }

        var here = Point(effector, position.EffectorOffset);
        return (position.Nearest(resolved.Frame, here) - here).LengthSquared() <= 1e-8f;
    }

    static void Report(
        in ConstraintSolveContext context,
        Span<BoneTransform> pose,
        int start,
        int end,
        ChainSpec chain,
        in BoneTransform animated
    ) {
        SkeletonPose.ComputeModelSpace(context.Skeleton, pose, context.Model);

        var settled = context.Model[chain.Effector];

        for (var index = start; index < end; index++) {
            var resolved = context.Goals[index];
            var applied = context.Residuals[index].Applied;

            context.Residuals[index] = resolved.Goal switch {
                PositionGoal position => PositionResidual(position, resolved.Frame, settled, animated, applied),
                OrientationGoal orientation => ConstraintResidual.Of(
                    GoalKind.Orientation,
                    Outside(
                        Angle(settled.Rotation, Quaternion.Concatenate(orientation.Rotation, resolved.Frame.Rotation)),
                        orientation.Region
                    ),
                    applied
                ),
                AimGoal aim => ConstraintResidual.Of(GoalKind.Aim, AimResidual(aim, resolved.Frame, settled), applied),
                DistanceGoal distance => ConstraintResidual.Of(
                    GoalKind.Distance,
                    (uint)distance.Other >= (uint)context.Model.Length
                        ? 0f
                        : distance.Excess((settled.Translation - context.Model[distance.Other].Translation).Length()),
                    applied
                ),
                _ => context.Residuals[index]
            };
        }
    }

    static ConstraintResidual PositionResidual(
        PositionGoal goal,
        in Frame frame,
        in BoneTransform settled,
        in BoneTransform animated,
        float applied
    ) {
        var here = Point(settled, goal.EffectorOffset);

        if (goal.Mode is not GoalMode.Additive) {
            return ConstraintResidual.Of(goal.Nearest(frame, here) - here, applied);
        }

        // An additive goal is not measured against a place; it is measured against a displacement.
        // Reporting the distance to some absolute point would be answering a question nobody asked.
        var wanted = frame.DirectionToModel(goal.Offset) * applied;
        return ConstraintResidual.Of(wanted - (here - Point(animated, goal.EffectorOffset)), applied);
    }

    static float AimResidual(AimGoal goal, in Frame frame, in BoneTransform settled) {
        var origin = Point(settled, goal.Origin);
        var toTarget = goal.Target(frame, origin) - origin;

        return toTarget.LengthSquared() <= Epsilon
            ? 0f
            : Outside(
                AngleBetween(Quaternion.Transform(goal.Axis, settled.Rotation), toTarget),
                goal.Region
            );
    }

    static Vector3 Point(in BoneTransform joint, Vector3 offset) =>
        offset == Vector3.Zero
            ? joint.Translation
            : joint.Translation + Quaternion.Transform(offset * joint.Scale, joint.Rotation);

    static float Outside(float value, float region) => MathF.Max(value - MathF.Abs(region), 0f);

    static float Angle(Quaternion from, Quaternion to) {
        var dot = MathF.Abs(MathUtil.Clamp(Quaternion.Dot(Quaternion.Normalize(from), Quaternion.Normalize(to)), -1f, 1f));
        return 2f * MathF.Acos(dot);
    }

    static float AngleBetween(Vector3 from, Vector3 to) {
        var lengths = from.Length() * to.Length();
        return lengths <= Epsilon ? 0f : MathF.Acos(MathUtil.Clamp(Vector3.Dot(from, to) / lengths, -1f, 1f));
    }
}
