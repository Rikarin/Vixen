// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Animation.Motions;

/// <summary>One motion of a one-dimensional blend tree, and where on the axis it sits.</summary>
/// <param name="Motion">What plays there.</param>
/// <param name="Threshold">The parameter value at which it plays alone.</param>
public readonly record struct BlendTree1DChild(Motion Motion, float Threshold);

/// <summary>
///     Motions on a line, blended by one parameter: idle at 0, walk at 2, run at 6.
/// </summary>
/// <remarks>
///     <para>
///         <b>Two children contribute at a time, and never more.</b> The parameter falls between two
///         thresholds and is a linear interpolation between them; a value below the first or above
///         the last is the end motion alone. That is what makes a 1D tree cheap enough to have
///         several of, and it is also what makes it predictable — an artist placing a motion at a
///         threshold knows it plays exactly there, unblended.
///     </para>
///     <para>
///         <b>The tree's length is the weighted average of its children's</b>, and every child is
///         sampled at the same normalised time. This is the whole trick of locomotion blending: a
///         1.2-second walk and a 0.8-second run blended half and half play as a 1.0-second cycle
///         with both feet landing together, where sampling both in seconds would have them drift
///         apart and the character would appear to have four legs.
///     </para>
/// </remarks>
public sealed class BlendTree1D : Motion {
    readonly BlendTree1DChild[] children;
    readonly Motion[] motions;
    readonly float[] weights;

    /// <summary>Builds a tree.</summary>
    /// <param name="parameter">The index of the parameter that drives it.</param>
    /// <param name="children">The motions and their thresholds, in any order.</param>
    /// <exception cref="ArgumentException">There are no children.</exception>
    public BlendTree1D(int parameter, IEnumerable<BlendTree1DChild> children) {
        ArgumentNullException.ThrowIfNull(children);

        Parameter = parameter;
        this.children = [.. children.OrderBy(child => child.Threshold)];

        if (this.children.Length == 0) {
            throw new ArgumentException("A blend tree needs at least one motion.", nameof(children));
        }

        motions = new Motion[this.children.Length];
        weights = new float[this.children.Length];

        for (var index = 0; index < this.children.Length; index++) {
            motions[index] = this.children[index].Motion;
        }
    }

    /// <summary>Builds a tree, declaring the parameter if it is not already.</summary>
    /// <param name="parameters">The parameter set to resolve against.</param>
    /// <param name="parameter">The parameter's name.</param>
    /// <param name="children">The motions and their thresholds, in any order.</param>
    public BlendTree1D(AnimationParameters parameters, string parameter, IEnumerable<BlendTree1DChild> children)
        : this(Declare(parameters, parameter), children) { }

    /// <summary>The index of the parameter that drives the blend.</summary>
    public int Parameter { get; }

    /// <summary>The motions, ordered by threshold.</summary>
    public ReadOnlySpan<BlendTree1DChild> Children => children;

    /// <inheritdoc />
    public override float Length(AnimationParameters parameters) {
        ComputeWeights(parameters);
        return BlendEvaluator.Length(parameters, motions, weights);
    }

    /// <inheritdoc />
    public override RootMotionDelta Evaluate(in MotionContext context, Span<BoneTransform> destination) {
        ComputeWeights(context.Parameters);
        return BlendEvaluator.Evaluate(context, motions, weights, destination, []);
    }

    /// <summary>The weights a parameter value produces, for a test or an editor.</summary>
    /// <param name="parameters">The parameter set.</param>
    /// <param name="destination">One weight per child, in threshold order.</param>
    public void ComputeWeights(AnimationParameters parameters, Span<float> destination) {
        ComputeWeights(parameters);
        weights.AsSpan().CopyTo(destination);
    }

    void ComputeWeights(AnimationParameters parameters) {
        Array.Clear(weights);

        var value = parameters.GetFloat(Parameter);

        if (children.Length == 1 || value <= children[0].Threshold) {
            weights[0] = 1f;
            return;
        }

        var last = children.Length - 1;

        if (value >= children[last].Threshold) {
            weights[last] = 1f;
            return;
        }

        for (var index = 0; index < last; index++) {
            var low = children[index].Threshold;
            var high = children[index + 1].Threshold;

            if (value < low || value > high) {
                continue;
            }

            // Two thresholds authored at the same value: the lower-indexed motion wins outright
            // rather than the division producing an infinity. Authoring it is a mistake, and a
            // mistake that makes the character disappear is worse than one that makes it pick.
            var span = high - low;
            var t = span > 0f ? (value - low) / span : 0f;

            weights[index] = 1f - t;
            weights[index + 1] = t;

            return;
        }
    }

    static int Declare(AnimationParameters parameters, string parameter) {
        ArgumentNullException.ThrowIfNull(parameters);
        return parameters.Declare(parameter, AnimationParameterType.Float);
    }
}
