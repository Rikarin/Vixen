// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Animation.Motions;

/// <summary>
///     The half of a blend tree that does not depend on how its weights were worked out.
/// </summary>
/// <remarks>
///     A one-dimensional tree and a two-dimensional one differ entirely in how a parameter becomes a
///     set of weights, and not at all in what is done with them. This is the second half, written
///     once: evaluate the children that matter, average their poses, average their root motion, and
///     scale their events' attribution.
/// </remarks>
static class BlendEvaluator {
    /// <summary>The weight below which a child is not evaluated at all.</summary>
    /// <remarks>
    ///     A child at 0.1 % contributes a tenth of a millimetre and costs a full clip sample, a
    ///     scratch buffer and a pass over every joint. The cut-off is what keeps a nine-motion 2D
    ///     tree evaluating the three that are actually near the parameter — and it is a threshold
    ///     rather than an exact zero because gradient-band weights approach zero asymptotically and
    ///     essentially never reach it.
    /// </remarks>
    public const float MinimumWeight = 1e-3f;

    /// <summary>The weighted average of the children's lengths, over the children that contribute.</summary>
    public static float Length(
        AnimationParameters parameters,
        ReadOnlySpan<Motion> motions,
        ReadOnlySpan<float> weights
    ) {
        var length = 0f;
        var total = 0f;

        for (var index = 0; index < motions.Length; index++) {
            var weight = weights[index];

            if (weight < MinimumWeight) {
                continue;
            }

            length += motions[index].Length(parameters) * weight;
            total += weight;
        }

        return total > 0f ? MathF.Max(length / total, 1e-4f) : 1e-4f;
    }

    /// <summary>Evaluates and averages the children that contribute.</summary>
    /// <param name="context">The moment being evaluated.</param>
    /// <param name="motions">The children.</param>
    /// <param name="weights">Their weights, in the same order.</param>
    /// <param name="destination">Where the blended pose goes.</param>
    /// <param name="fallback">What an all-zero weight set means.</param>
    /// <returns>The blended root motion.</returns>
    public static RootMotionDelta Evaluate(
        in MotionContext context,
        ReadOnlySpan<Motion> motions,
        ReadOnlySpan<float> weights,
        Span<BoneTransform> destination,
        ReadOnlySpan<BoneTransform> fallback
    ) {
        var accumulator = PoseBlend.Average(destination);
        var motion = RootMotionDelta.None;
        var total = 0f;

        for (var index = 0; index < motions.Length; index++) {
            var weight = weights[index];

            if (weight < MinimumWeight) {
                continue;
            }

            using var lease = context.Scratch.Rent();

            var child = context with { Weight = context.Weight * weight };
            var delta = motions[index].Evaluate(child, lease.Pose);

            accumulator.Add(lease.Pose, weight);

            // The same running average the poses get, for the same reason: a sum of deltas over
            // weights that do not add to one would move the character further than any of its
            // motions asked for.
            motion = total <= 0f ? delta : RootMotionDelta.Lerp(motion, delta, weight / (total + weight));
            total += weight;
        }

        accumulator.Finish(fallback);
        return motion;
    }
}
