// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Animation;

/// <summary>
///     The four things that are ever done to a pose: interpolate it, average several of them,
///     replace part of it, and add a difference on top.
/// </summary>
/// <remarks>
///     Free functions over spans rather than methods on <see cref="SkeletonPose" />, because the
///     buffers a blend tree works in are scratch — a stack of them, reused every frame — and making
///     each one a <c>SkeletonPose</c> would mean a class per intermediate result. Everything here
///     writes into the destination it is handed and allocates nothing.
/// </remarks>
public static class PoseBlend {
    /// <summary>Interpolates one pose towards another.</summary>
    /// <param name="destination">Where the result goes. May alias <paramref name="from" />.</param>
    /// <param name="from">The pose at <paramref name="amount" /> = 0.</param>
    /// <param name="to">The pose at <paramref name="amount" /> = 1.</param>
    /// <param name="amount">The interpolant, clamped to <c>[0, 1]</c>.</param>
    public static void Lerp(
        Span<BoneTransform> destination,
        ReadOnlySpan<BoneTransform> from,
        ReadOnlySpan<BoneTransform> to,
        float amount
    ) {
        var t = MathUtil.Saturate(amount);

        // The endpoints are worth special-casing: a crossfade spends most of its life at one or the
        // other, and a state machine that is not transitioning is at zero on every joint of every
        // layer, every frame.
        if (t <= 0f) {
            from.CopyTo(destination);
            return;
        }

        if (t >= 1f) {
            to.CopyTo(destination);
            return;
        }

        for (var index = 0; index < destination.Length; index++) {
            destination[index] = BoneTransform.Lerp(from[index], to[index], t);
        }
    }

    /// <summary>
    ///     Interpolates one pose towards another, joint by joint, through a mask.
    /// </summary>
    /// <param name="destination">Where the result goes. May alias <paramref name="from" />.</param>
    /// <param name="from">The pose the mask lets through where its weight is zero.</param>
    /// <param name="to">The pose the mask lets through where its weight is one.</param>
    /// <param name="mask">The per-joint weights.</param>
    /// <param name="weight">A global multiplier on the mask, clamped to <c>[0, 1]</c>.</param>
    public static void LerpMasked(
        Span<BoneTransform> destination,
        ReadOnlySpan<BoneTransform> from,
        ReadOnlySpan<BoneTransform> to,
        BoneMask mask,
        float weight = 1f
    ) {
        ArgumentNullException.ThrowIfNull(mask);

        var global = MathUtil.Saturate(weight);
        var weights = mask.Weights;

        for (var index = 0; index < destination.Length; index++) {
            destination[index] = BoneTransform.Lerp(from[index], to[index], weights[index] * global);
        }
    }

    /// <summary>Turns a posed clip into the difference an additive layer applies.</summary>
    /// <param name="destination">Where the difference goes.</param>
    /// <param name="pose">The posed clip.</param>
    /// <param name="reference">
    ///     The pose it is measured against — the clip's own first frame, or the skeleton's bind pose.
    /// </param>
    /// <remarks>
    ///     Which reference is used is the whole of what makes an additive clip work. An aim offset
    ///     authored as "the character's idle, leaning left" is only a lean if the idle is subtracted
    ///     back out; measured against the bind pose it is the entire idle plus the lean, and adding
    ///     it to a run gives a character doing both at once.
    /// </remarks>
    public static void MakeAdditive(
        Span<BoneTransform> destination,
        ReadOnlySpan<BoneTransform> pose,
        ReadOnlySpan<BoneTransform> reference
    ) {
        for (var index = 0; index < destination.Length; index++) {
            destination[index] = BoneTransform.Difference(pose[index], reference[index]);
        }
    }

    /// <summary>Applies an additive difference on top of a pose.</summary>
    /// <param name="destination">The pose to add to, written in place.</param>
    /// <param name="additive">The difference, from <see cref="MakeAdditive" />.</param>
    /// <param name="weight">How much of it to apply, clamped to <c>[0, 1]</c>.</param>
    /// <param name="mask">Which joints it reaches, or <see langword="null" /> for all of them.</param>
    public static void Add(
        Span<BoneTransform> destination,
        ReadOnlySpan<BoneTransform> additive,
        float weight,
        BoneMask? mask = null
    ) {
        var global = MathUtil.Saturate(weight);

        if (global <= 0f) {
            return;
        }

        if (mask is null) {
            for (var index = 0; index < destination.Length; index++) {
                destination[index] = BoneTransform.Add(destination[index], additive[index], global);
            }

            return;
        }

        var weights = mask.Weights;

        for (var index = 0; index < destination.Length; index++) {
            destination[index] = BoneTransform.Add(destination[index], additive[index], weights[index] * global);
        }
    }

    /// <summary>Averages any number of poses by weight, without ever holding all of them at once.</summary>
    /// <param name="destination">Where the result accumulates.</param>
    /// <returns>The accumulator.</returns>
    /// <remarks>
    ///     What a blend tree with five clips in range needs, and why it is a running average rather
    ///     than a sum divided at the end: a quaternion sum is not a rotation, so there is nothing
    ///     meaningful to divide. See <see cref="Accumulator" />.
    /// </remarks>
    public static Accumulator Average(Span<BoneTransform> destination) => new(destination);

    /// <summary>
    ///     A weighted average of poses, built one pose at a time.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Each <see cref="Add" /> interpolates the running result towards the new pose by
    ///         <c>w / (total + w)</c>, which for translations and scales is exactly the weighted
    ///         mean and is independent of the order they arrive in. For rotations it is not — a
    ///         chain of nlerps depends on its order, because there is no cheap operation on
    ///         quaternions that both averages and stays on the unit sphere. Every engine does this;
    ///         the error is below the precision of the input for the weights a blend tree produces,
    ///         and the alternative is an eigen-decomposition per joint per frame.
    ///     </para>
    ///     <para>
    ///         A <c>ref struct</c> so it cannot outlive the span it writes into, and so that
    ///         accumulating a blend costs a stack slot and no allocation.
    ///     </para>
    /// </remarks>
    public ref struct Accumulator {
        readonly Span<BoneTransform> destination;
        float total;

        internal Accumulator(Span<BoneTransform> destination) {
            this.destination = destination;
            total = 0f;
        }

        /// <summary>The weights added so far. Zero means nothing has contributed.</summary>
        public readonly float TotalWeight => total;

        /// <summary>Folds one pose in.</summary>
        /// <param name="pose">The pose.</param>
        /// <param name="weight">Its weight. Zero and negative weights are skipped.</param>
        public void Add(ReadOnlySpan<BoneTransform> pose, float weight) {
            if (weight <= 0f) {
                return;
            }

            if (total <= 0f) {
                pose[..destination.Length].CopyTo(destination);
                total = weight;

                return;
            }

            var t = weight / (total + weight);
            total += weight;

            for (var index = 0; index < destination.Length; index++) {
                destination[index] = BoneTransform.Lerp(destination[index], pose[index], t);
            }
        }

        /// <summary>
        ///     Fills the destination with a fallback if nothing contributed, and reports whether
        ///     anything did.
        /// </summary>
        /// <param name="fallback">
        ///     What an empty average means — the bind pose, in practice. Pass an empty span to leave
        ///     the destination alone.
        /// </param>
        /// <returns><see langword="true" /> if at least one pose was added.</returns>
        /// <remarks>
        ///     Worth being explicit about, because a blend tree whose weights all come out zero is a
        ///     real situation — a 2D tree sampled outside every motion's influence — and the
        ///     difference between "the bind pose" and "whatever was in the buffer last frame" is the
        ///     difference between a character standing still and one flickering.
        /// </remarks>
        public bool Finish(ReadOnlySpan<BoneTransform> fallback) {
            if (total > 0f) {
                return true;
            }

            if (!fallback.IsEmpty) {
                fallback[..destination.Length].CopyTo(destination);
            }

            return false;
        }
    }
}
