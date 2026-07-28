// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Animation;

/// <summary>
///     How much of a layer reaches each joint: a weight per joint, and the way a wave is played on
///     the arm of a character who is still running.
/// </summary>
/// <remarks>
///     <para>
///         <b>A weight and not a flag.</b> A boolean mask puts a hard seam at the joint where it
///         changes — the spine below it is running and the spine above it is waving, and the two
///         disagree by however much the clips do. A weight lets the seam be spread over three or
///         four joints, which is what makes the transition read as a body rather than as two halves
///         of one.
///     </para>
///     <para>
///         <b>Built by name, applied by index.</b> Authoring says "the upper body", which means a
///         joint and everything under it; a frame reads an array. The translation happens once, in
///         <see cref="Builder" />, against the skeleton the mask is for — so a mask cannot be
///         applied to the wrong skeleton, and a joint name that no longer exists is caught where the
///         mask is built rather than by a limb that quietly stops moving.
///     </para>
/// </remarks>
public sealed class BoneMask {
    readonly float[] weights;

    BoneMask(Skeleton skeleton, float[] weights) {
        Skeleton = skeleton;
        this.weights = weights;
    }

    /// <summary>The skeleton this mask is indexed against.</summary>
    public Skeleton Skeleton { get; }

    /// <summary>The per-joint weights, in joint order.</summary>
    public ReadOnlySpan<float> Weights => weights;

    /// <summary>How much of the layer reaches one joint.</summary>
    /// <param name="joint">The joint's index.</param>
    /// <returns>Its weight, in <c>[0, 1]</c>.</returns>
    public float this[int joint] => weights[joint];

    /// <summary>A mask that passes everything.</summary>
    /// <param name="skeleton">The skeleton to index against.</param>
    /// <returns>The mask.</returns>
    public static BoneMask Full(Skeleton skeleton) {
        ArgumentNullException.ThrowIfNull(skeleton);

        var weights = new float[skeleton.JointCount];
        Array.Fill(weights, 1f);

        return new(skeleton, weights);
    }

    /// <summary>Starts building a mask that passes nothing, to add joints to.</summary>
    /// <param name="skeleton">The skeleton to index against.</param>
    /// <returns>The builder.</returns>
    public static Builder Excluding(Skeleton skeleton) => new(skeleton, 0f);

    /// <summary>Starts building a mask that passes everything, to take joints out of.</summary>
    /// <param name="skeleton">The skeleton to index against.</param>
    /// <returns>The builder.</returns>
    public static Builder Including(Skeleton skeleton) => new(skeleton, 1f);

    /// <summary>Assembles a mask joint by joint.</summary>
    /// <remarks>
    ///     A mutable struct so that building one allocates the weight array and nothing else. It is
    ///     a load-time or authoring-time object; nothing in a frame builds a mask.
    /// </remarks>
    public struct Builder {
        readonly Skeleton skeleton;
        readonly float[] weights;

        internal Builder(Skeleton skeleton, float initial) {
            ArgumentNullException.ThrowIfNull(skeleton);

            this.skeleton = skeleton;
            weights = new float[skeleton.JointCount];

            if (initial != 0f) {
                Array.Fill(weights, initial);
            }
        }

        /// <summary>Sets one joint's weight, and optionally every joint under it.</summary>
        /// <param name="jointName">The joint's name.</param>
        /// <param name="weight">Its weight, clamped to <c>[0, 1]</c>.</param>
        /// <param name="includeDescendants">Whether the joints below it get the same weight.</param>
        /// <returns>The builder, for chaining.</returns>
        /// <remarks>
        ///     A name that is not in the skeleton is ignored rather than throwing. A mask outlives
        ///     the rig it was authored against — a character gets a new finger joint, a prop loses
        ///     one — and a missing joint means "nothing to weight", which is exactly what leaving
        ///     the array alone does. What it must not do is take down a load.
        /// </remarks>
        public Builder Set(string jointName, float weight, bool includeDescendants = true) {
            var index = skeleton.IndexOf(jointName);
            return index < 0 ? this : Set(index, weight, includeDescendants);
        }

        /// <summary>Sets one joint's weight, and optionally every joint under it.</summary>
        /// <param name="joint">The joint's index.</param>
        /// <param name="weight">Its weight, clamped to <c>[0, 1]</c>.</param>
        /// <param name="includeDescendants">Whether the joints below it get the same weight.</param>
        /// <returns>The builder, for chaining.</returns>
        public Builder Set(int joint, float weight, bool includeDescendants = true) {
            var clamped = MathUtil.Saturate(weight);
            weights[joint] = clamped;

            if (!includeDescendants) {
                return this;
            }

            // Parents precede children, so every descendant has a higher index than the joint and
            // one forward sweep finds all of them. O(joints × depth) with the ancestor walk, which
            // is authoring-time work: nothing in a frame builds a mask.
            for (var index = joint + 1; index < weights.Length; index++) {
                if (skeleton.IsDescendantOf(index, joint)) {
                    weights[index] = clamped;
                }
            }

            return this;
        }

        /// <summary>Finishes the mask.</summary>
        /// <returns>The mask.</returns>
        public readonly BoneMask Build() => new(skeleton, weights);
    }
}
