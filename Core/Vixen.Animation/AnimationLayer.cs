// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.StateMachine;
using Vixen.Core.Mathematics;

namespace Vixen.Animation;

/// <summary>How a layer mixes with everything under it.</summary>
public enum LayerBlend {
    /// <summary>
    ///     It replaces what is underneath, in proportion to its weight and its mask. What a
    ///     upper-body action layer over locomotion is.
    /// </summary>
    Override,

    /// <summary>
    ///     Its pose is a difference and is added to what is underneath. What an aim offset, a lean
    ///     or a breathing layer is — see <see cref="PoseBlend.MakeAdditive" /> for what the
    ///     difference is measured against.
    /// </summary>
    Additive
}

/// <summary>
///     One state machine, a weight and a mask: the unit a character's animation is composed out of.
/// </summary>
/// <remarks>
///     <para>
///         The base layer is layer 0 and is what everything else is applied over. Its mask is
///         ignored — there is nothing underneath for a masked-out joint to fall back to, and a
///         partially-masked base layer produces joints in the bind pose, which reads as a character
///         with a broken arm rather than as an authoring mistake.
///     </para>
///     <para>
///         <b>Root motion comes from one layer.</b> Two layers both claiming to move the character
///         would move it twice, and averaging them by weight gives a character that walks at half
///         speed whenever an upper-body layer fades in. <see cref="ContributesRootMotion" /> says
///         which one owns it; by default that is the base layer and no other.
///     </para>
/// </remarks>
public sealed class AnimationLayer {
    /// <summary>Creates a layer.</summary>
    /// <param name="name">What it is called.</param>
    /// <param name="machine">The graph it runs.</param>
    /// <param name="parameters">The values that graph reads.</param>
    /// <param name="scratch">Where its blends get temporary poses.</param>
    public AnimationLayer(
        string name,
        AnimationStateMachine machine,
        AnimationParameters parameters,
        PoseScratch scratch
    ) {
        Name = name;
        States = new(machine, parameters, scratch);
    }

    /// <summary>What the layer is called.</summary>
    public string Name { get; }

    /// <summary>Where in its graph this character is.</summary>
    public StateMachineInstance States { get; }

    /// <summary>How much of it reaches the result, in <c>[0, 1]</c>.</summary>
    public float Weight { get; set; } = 1f;

    /// <summary>Which joints it reaches, or <see langword="null" /> for all of them.</summary>
    public BoneMask? Mask { get; set; }

    /// <summary>How it mixes with what is underneath.</summary>
    public LayerBlend Blend { get; set; } = LayerBlend.Override;

    /// <summary>Whether this layer's root motion is the character's.</summary>
    public bool ContributesRootMotion { get; set; }

    /// <summary>Whether it is evaluated at all.</summary>
    /// <remarks>
    ///     Separate from a zero weight, and worth having: a layer faded to zero is still evaluated,
    ///     because its state machine has to keep running or it will resume from wherever it was when
    ///     the weight was last non-zero. A disabled layer is one that has stopped.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>Whether this layer will change the pose at all this frame.</summary>
    internal bool Contributes(int index) => Enabled && (index == 0 || Weight > 1e-4f);

    /// <summary>Mixes this layer's pose into the result.</summary>
    /// <param name="destination">What the layers underneath produced.</param>
    /// <param name="pose">What this layer produced.</param>
    internal void Apply(Span<BoneTransform> destination, ReadOnlySpan<BoneTransform> pose) {
        var weight = MathUtil.Saturate(Weight);

        if (Blend is LayerBlend.Additive) {
            PoseBlend.Add(destination, pose, weight, Mask);
            return;
        }

        if (Mask is null) {
            PoseBlend.Lerp(destination, destination, pose, weight);
            return;
        }

        PoseBlend.LerpMasked(destination, destination, pose, Mask, weight);
    }
}
