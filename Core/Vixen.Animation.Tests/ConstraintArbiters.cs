// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Constraints;
using Vixen.Animation.Moves;
using Vixen.Core.Mathematics;

namespace Vixen.Animation.Tests;

/// <summary>
///     A second arbiter, written to a different shape, run against the same suite as the shipped one.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The point of this type is that it is not the default rearranged.</b> The plan makes
///         <see cref="IConstraintArbiter" /> the most important seam in the document and says the
///         phase is not finished until something other than the default has been written against it —
///         because an interface exercised only by its own default is an interface shaped around that
///         default, and nobody finds out until somebody outside the repository tries.
///     </para>
///     <para>
///         So this one differs on every axis it can while producing the same poses:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>It does not trust the sort.</b> The stack hands goals over grouped by chain; this
///             one discovers its own groups by scanning, which is what an arbiter that wanted to
///             group by body section rather than by chain would have to do.
///         </item>
///         <item>
///             <b>Kind-major, not chain-major.</b> Three sweeps over everything rather than one pass
///             per chain.
///         </item>
///         <item>
///             <b>Priority is an exponential weight multiplier</b>, not the default's
///             take-your-share-first banding. Same answers at the ends — a full-weight high-priority
///             goal still wins — by a different rule.
///         </item>
///     </list>
///     <para>
///         What it found: nothing in the interface needed changing, and nothing internal to
///         <c>ConstraintStack</c> had to be reached for. That is the result the exercise was for.
///     </para>
/// </remarks>
sealed class WeightedTestArbiter : IConstraintArbiter {
    const float Epsilon = 1e-5f;

    public void Solve(in ConstraintSolveContext context, Span<BoneTransform> pose) {
        var goals = context.Goals;

        for (var index = 0; index < goals.Length; index++) {
            context.Residuals[index] = new(goals[index].Goal.Kind, 0f, Vector3.Zero, Effective(goals, index));
        }

        // Own grouping, by scanning for the chains present rather than by walking runs. Quadratic in
        // the number of goals on one character, which is a handful, and deliberately ignorant of the
        // order they arrived in.
        for (var index = 0; index < goals.Length; index++) {
            var chain = goals[index].Goal.Solved;

            if (First(goals, chain) != index) {
                continue;
            }

            Chain(context, pose, chain);
        }
    }

    static void Chain(in ConstraintSolveContext context, Span<BoneTransform> pose, ChainSpec chain) {
        var goals = context.Goals;
        var joint = chain.Effector;

        if ((uint)joint >= (uint)context.Model.Length) {
            return;
        }

        var animated = context.Model[joint];
        var pole = Vector3.Zero;

        var weighted = Vector3.Zero;
        var total = 0f;
        var additive = Vector3.Zero;

        for (var index = 0; index < goals.Length; index++) {
            if (goals[index].Goal.Solved != chain || goals[index].Goal is not PositionGoal position) {
                continue;
            }

            var weight = Effective(goals, index);

            if (weight <= Epsilon) {
                continue;
            }

            if (position.Pole != Vector3.Zero) {
                pole = position.Pole;
            }

            if (position.Mode is GoalMode.Additive) {
                additive += goals[index].Frame.DirectionToModel(position.Offset) * weight;
                continue;
            }

            total += weight;
            weighted += position.Nearest(goals[index].Frame, animated.Translation) * weight;
        }

        var moved = false;

        if (total > Epsilon || additive != Vector3.Zero) {
            var target = total > Epsilon
                ? Vector3.Lerp(animated.Translation, weighted / total, MathF.Min(total, 1f))
                : animated.Translation;

            moved = context.Solver.Solve(
                context.Skeleton,
                new(chain, target + additive, 1f, Quaternion.Identity, 0f, pole, Vector3.Zero),
                pose,
                context.Model
            );
        }

        if (moved) {
            SkeletonPose.ComputeModelSpace(context.Skeleton, pose, context.Model);
        }

        var rotation = context.Model[joint].Rotation;
        var turned = false;
        var orientation = 0f;

        for (var index = 0; index < goals.Length; index++) {
            if (goals[index].Goal.Solved != chain || goals[index].Goal is not OrientationGoal goal) {
                continue;
            }

            var weight = Effective(goals, index);

            if (weight <= Epsilon) {
                continue;
            }

            turned = true;

            if (goal.Mode is GoalMode.Additive) {
                rotation = Quaternion.Concatenate(rotation, AimGoal.ScaleRotation(goal.Rotation, weight));
                continue;
            }

            orientation += weight;

            rotation = Quaternion.Nlerp(
                rotation,
                Quaternion.Concatenate(goal.Rotation, goals[index].Frame.Rotation),
                MathF.Min(weight / orientation, 1f) * MathF.Min(orientation, 1f)
            );
        }

        for (var index = 0; index < goals.Length; index++) {
            if (goals[index].Goal.Solved != chain || goals[index].Goal is not AimGoal goal) {
                continue;
            }

            var weight = Effective(goals, index);

            if (weight <= Epsilon) {
                continue;
            }

            turned = true;

            if (goal.Mode is GoalMode.Additive) {
                rotation = Quaternion.Concatenate(rotation, AimGoal.ScaleRotation(goal.Deviation, weight));
                continue;
            }

            var origin = context.Model[joint].Translation
                + Quaternion.Transform(goal.Origin, context.Model[joint].Rotation);

            var toTarget = goal.Target(goals[index].Frame, origin) - origin;

            if (toTarget.LengthSquared() <= Epsilon) {
                continue;
            }

            var swing = Quaternion.FromToRotation(
                Quaternion.Transform(goal.Axis, rotation),
                Vector3.Normalize(toTarget)
            );

            rotation = Quaternion.Concatenate(
                rotation,
                AimGoal.ScaleRotation(swing, MathF.Min(weight, 1f) * (1f - MathF.Min(orientation, 1f)))
            );
        }

        if (turned) {
            context.Solver.Solve(
                context.Skeleton,
                new(ChainSpec.Single(joint), Vector3.Zero, 0f, rotation, 1f, pole, Vector3.Zero),
                pose,
                context.Model
            );
        }
    }

    static int First(ReadOnlySpan<ResolvedGoal> goals, ChainSpec chain) {
        for (var index = 0; index < goals.Length; index++) {
            if (goals[index].Goal.Solved == chain) {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Priority as an exponential multiplier rather than as a share taken off the top.</summary>
    static float Effective(ReadOnlySpan<ResolvedGoal> goals, int index) {
        var goal = goals[index].Goal;
        var top = int.MinValue;

        for (var other = 0; other < goals.Length; other++) {
            if (goals[other].Goal.Solved == goal.Solved && goals[other].Goal.Kind == goal.Kind) {
                top = Math.Max(top, goals[other].Goal.Priority);
            }
        }

        var falloff = MathF.Pow(2f, -8f * MathF.Min(top - goal.Priority, 8));
        return MathUtil.Saturate(goals[index].Weight) * falloff;
    }
}

/// <summary>A chain solver that is not an IK solver at all: it moves the joint and nothing else.</summary>
/// <remarks>
///     The second implementation of <see cref="IChainSolver" />, and deliberately a degenerate one.
///     What it proves is that nothing above the seam assumes a chain is bent rather than moved — the
///     case a project solving a prop, a camera or a single rigid body would be in.
/// </remarks>
sealed class TeleportingTestSolver : IChainSolver {
    public int Calls { get; private set; }

    public bool Solve(
        Skeleton skeleton,
        in ChainSolveRequest request,
        Span<BoneTransform> local,
        Span<BoneTransform> model
    ) {
        Calls++;

        var joint = request.Chain.Effector;

        if ((uint)joint >= (uint)local.Length) {
            return false;
        }

        SkeletonPose.ComputeModelSpace(skeleton, local, model);

        var parent = skeleton.ParentOf(joint);

        var above = parent < 0
            ? BoneTransform.Identity
            : model[parent];

        var inverse = BoneTransform.Inverse(above);

        if (request.PositionWeight > 0f) {
            local[joint].Translation = BoneTransform.Concatenate(
                new(request.Position, Quaternion.Identity, Vector3.One),
                inverse
            ).Translation;
        }

        if (request.RotationWeight > 0f) {
            local[joint].Rotation = Quaternion.Concatenate(request.Rotation, Quaternion.Conjugate(above.Rotation));
        }

        return true;
    }
}

/// <summary>A binding whose answer is computed rather than stored.</summary>
/// <remarks>
///     The second implementation of <see cref="IBindingSource" />. Everything the shipped
///     <see cref="TransformBinding" /> is not: no dictionary, no stored transform, and an existence
///     test that changes between frames — which is the path
///     <see cref="IConstraintFrame.TryResolve" />'s failure case exists for.
/// </remarks>
sealed class ComputedTestBinding(Func<Symbol, BoneTransform?> answer) : IBindingSource {
    public bool TryGetFrame(Symbol socket, out BoneTransform world) {
        if (answer(socket) is { } found) {
            world = found;
            return true;
        }

        world = default;
        return false;
    }
}

/// <summary>A frame that is a fixed point in the character's own model space.</summary>
/// <remarks>
///     The sixth implementation of <see cref="IConstraintFrame" />, written outside the assembly's
///     own five to check that nothing about the interface needs anything internal. It resolves with
///     no binding, no world round trip and no failure case.
/// </remarks>
sealed class ModelTestFrame(Vector3 position) : IConstraintFrame {
    public bool TryResolve(in ConstraintContext context, out Frame frame) {
        frame = new(new BoneTransform(position, Quaternion.Identity, Vector3.One));
        return true;
    }
}

/// <summary>A scheduler that puts every character into one group, before anybody evaluates.</summary>
/// <remarks>
///     The second implementation of <see cref="IConstraintScheduler" />, and the one that shows why
///     the pre-evaluation stage exists: it publishes each member's pose before the evaluation pass, so
///     a member can read a neighbour's without reading a pose that is halfway through being built.
/// </remarks>
sealed class OneGroupTestScheduler : IConstraintScheduler {
    public int PreEvaluationCalls { get; private set; }

    public int PoseCalls { get; private set; }

    public bool GroupBeforeEvaluation { get; set; } = true;

    public bool GroupAfterEvaluation { get; set; }

    public void PlanPreEvaluation(ReadOnlySpan<ConstraintStack> stacks, IConstraintGroupSink sink) {
        PreEvaluationCalls++;

        if (GroupBeforeEvaluation && stacks.Length > 0) {
            sink.Add(stacks);
        }
    }

    public void PlanPose(ReadOnlySpan<ConstraintStack> stacks, IConstraintGroupSink sink) {
        PoseCalls++;

        if (GroupAfterEvaluation && stacks.Length > 0) {
            sink.Add(stacks);
        }
    }
}
