// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Animation.Motions;

/// <summary>How a two-dimensional tree turns a point into weights.</summary>
public enum Blend2DMode {
    /// <summary>
    ///     Positions are plain coordinates and distance is Euclidean. What a tree whose axes are
    ///     independent quantities wants — speed against crouch height, aim yaw against aim pitch.
    /// </summary>
    FreeformCartesian,

    /// <summary>
    ///     Positions are read as a direction and a magnitude, and motions are compared by the angle
    ///     between them as much as by the distance. What a movement tree wants: forward-run and
    ///     backward-run sit at opposite ends of an axis and must not blend into a standing pose on
    ///     the way past the origin.
    /// </summary>
    FreeformDirectional
}

/// <summary>One motion of a two-dimensional blend tree, and where on the plane it sits.</summary>
/// <param name="Motion">What plays there.</param>
/// <param name="Position">The point at which it plays alone.</param>
public readonly record struct BlendTree2DChild(Motion Motion, Vector2 Position);

/// <summary>
///     Motions on a plane, blended by two parameters: a locomotion set driven by forward and
///     sideways speed, or an aim set driven by yaw and pitch.
/// </summary>
/// <remarks>
///     <para>
///         <b>Gradient band interpolation</b>, which is Rune Skovbo Johansen's construction and what
///         Unity's freeform modes use. For each motion, the weight is how far the sample point is
///         from crossing into any <em>other</em> motion's territory: one minus the projection of
///         <c>p − pᵢ</c> onto <c>pⱼ − pᵢ</c>, taken over every other motion j and minimised, clamped
///         at zero, and normalised at the end.
///     </para>
///     <para>
///         What that buys over the obvious alternatives: inverse-distance weighting gives every
///         motion a non-zero weight everywhere, so a backward walk is always slightly playing during
///         a forward run; a Delaunay triangulation gives exact barycentric weights and needs a
///         triangulation, which is a build step, a degenerate-case problem, and something that has
///         to be rebuilt when an artist drags a motion. Gradient bands need nothing precomputed,
///         reach exactly one at each motion's own point, and reach exactly zero outside a bounded
///         neighbourhood of it.
///     </para>
///     <para>
///         <b>Directional mode compares angles, not positions.</b> A run-forward at (0, 3) and a
///         run-backward at (0, −3) are five metres per second apart in Cartesian terms and are
///         opposite directions of travel; a sample halfway between them is a standing character, and
///         Cartesian weights would say "half of each" — a character running forwards and backwards
///         at once. Directional mode maps each pair into (relative magnitude, signed angle) before
///         doing the same construction, which puts the halfway point where a person would draw it.
///     </para>
/// </remarks>
public sealed class BlendTree2D : Motion {
    /// <summary>
    ///     How much an angle counts against a magnitude difference in
    ///     <see cref="Blend2DMode.FreeformDirectional" />.
    /// </summary>
    /// <remarks>
    ///     Two, following Unity, and the number is not arbitrary: it makes a 90° difference in
    ///     direction weigh the same as a magnitude difference equal to the average magnitude, which
    ///     is the ratio at which a sideways step stops reading as a slower forward one.
    /// </remarks>
    const float DirectionalBias = 2f;

    readonly BlendTree2DChild[] children;
    readonly Motion[] motions;
    readonly Vector2[] positions;
    readonly float[] weights;

    /// <summary>Builds a tree.</summary>
    /// <param name="parameterX">The index of the parameter on the horizontal axis.</param>
    /// <param name="parameterY">The index of the parameter on the vertical axis.</param>
    /// <param name="children">The motions and their positions.</param>
    /// <param name="mode">How positions are compared.</param>
    /// <exception cref="ArgumentException">There are no children.</exception>
    public BlendTree2D(
        int parameterX,
        int parameterY,
        IEnumerable<BlendTree2DChild> children,
        Blend2DMode mode = Blend2DMode.FreeformCartesian
    ) {
        ArgumentNullException.ThrowIfNull(children);

        ParameterX = parameterX;
        ParameterY = parameterY;
        Mode = mode;
        this.children = [.. children];

        if (this.children.Length == 0) {
            throw new ArgumentException("A blend tree needs at least one motion.", nameof(children));
        }

        motions = new Motion[this.children.Length];
        positions = new Vector2[this.children.Length];
        weights = new float[this.children.Length];

        for (var index = 0; index < this.children.Length; index++) {
            motions[index] = this.children[index].Motion;
            positions[index] = this.children[index].Position;
        }
    }

    /// <summary>Builds a tree, declaring its parameters if they are not already.</summary>
    /// <param name="parameters">The parameter set to resolve against.</param>
    /// <param name="parameterX">The horizontal parameter's name.</param>
    /// <param name="parameterY">The vertical parameter's name.</param>
    /// <param name="children">The motions and their positions.</param>
    /// <param name="mode">How positions are compared.</param>
    public BlendTree2D(
        AnimationParameters parameters,
        string parameterX,
        string parameterY,
        IEnumerable<BlendTree2DChild> children,
        Blend2DMode mode = Blend2DMode.FreeformCartesian
    ) : this(
        Declare(parameters, parameterX),
        Declare(parameters, parameterY),
        children,
        mode
    ) { }

    /// <summary>The index of the parameter on the horizontal axis.</summary>
    public int ParameterX { get; }

    /// <summary>The index of the parameter on the vertical axis.</summary>
    public int ParameterY { get; }

    /// <summary>How positions are compared.</summary>
    public Blend2DMode Mode { get; }

    /// <summary>The motions and their positions.</summary>
    public ReadOnlySpan<BlendTree2DChild> Children => children;

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

    /// <summary>The weights a point produces, for a test or an editor.</summary>
    /// <param name="parameters">The parameter set.</param>
    /// <param name="destination">One weight per child, in the order they were given.</param>
    public void ComputeWeights(AnimationParameters parameters, Span<float> destination) {
        ComputeWeights(parameters);
        weights.AsSpan().CopyTo(destination);
    }

    void ComputeWeights(AnimationParameters parameters) {
        var sample = new Vector2(parameters.GetFloat(ParameterX), parameters.GetFloat(ParameterY));

        if (children.Length == 1) {
            weights[0] = 1f;
            return;
        }

        var total = 0f;

        for (var index = 0; index < positions.Length; index++) {
            var weight = 1f;

            for (var other = 0; other < positions.Length && weight > 0f; other++) {
                if (other == index) {
                    continue;
                }

                weight = MathF.Min(weight, Influence(index, other, sample));
            }

            weight = MathF.Max(weight, 0f);
            weights[index] = weight;
            total += weight;
        }

        if (total <= 0f) {
            // Every motion excluded every other, which happens when two of them sit at the same
            // point. Falling back to the nearest keeps a pose on screen; the alternative is a
            // character that vanishes into the bind pose because two motions were authored on top
            // of one another.
            Array.Clear(weights);
            weights[Nearest(sample)] = 1f;

            return;
        }

        for (var index = 0; index < weights.Length; index++) {
            weights[index] /= total;
        }
    }

    /// <summary>
    ///     How much of motion <paramref name="index" /> survives motion <paramref name="other" />
    ///     at the sample point: one at <paramref name="index" />'s own position, zero at
    ///     <paramref name="other" />'s, and linear in between.
    /// </summary>
    float Influence(int index, int other, Vector2 sample) {
        Vector2 toSample;
        Vector2 toOther;

        if (Mode is Blend2DMode.FreeformCartesian) {
            toSample = sample - positions[index];
            toOther = positions[other] - positions[index];
        } else {
            var from = positions[index];
            var to = positions[other];
            var fromLength = from.Length();
            var average = (fromLength + to.Length()) * 0.5f;

            toSample = new(
                Magnitude(sample.Length() - fromLength, average),
                SignedAngle(from, sample) * DirectionalBias
            );

            toOther = new(
                Magnitude(to.Length() - fromLength, average),
                SignedAngle(from, to) * DirectionalBias
            );
        }

        var lengthSquared = toOther.LengthSquared();

        // Coincident motions: nothing separates them, so neither excludes the other. Returning one
        // rather than dividing by zero leaves the decision to whichever pair is not degenerate.
        return lengthSquared <= 1e-12f ? 1f : 1f - (Vector2.Dot(toSample, toOther) / lengthSquared);
    }

    int Nearest(Vector2 sample) {
        var best = 0;
        var bestDistance = float.MaxValue;

        for (var index = 0; index < positions.Length; index++) {
            var distance = (positions[index] - sample).LengthSquared();

            if (distance < bestDistance) {
                bestDistance = distance;
                best = index;
            }
        }

        return best;
    }

    static float Magnitude(float difference, float average) => average > 1e-6f ? difference / average : 0f;

    /// <summary>The signed angle from one direction to another, in radians, or zero if either is the origin.</summary>
    static float SignedAngle(Vector2 from, Vector2 to) {
        if (from.LengthSquared() <= 1e-12f || to.LengthSquared() <= 1e-12f) {
            return 0f;
        }

        return MathF.Atan2((from.X * to.Y) - (from.Y * to.X), Vector2.Dot(from, to));
    }

    static int Declare(AnimationParameters parameters, string parameter) {
        ArgumentNullException.ThrowIfNull(parameters);
        return parameters.Declare(parameter, AnimationParameterType.Float);
    }
}
