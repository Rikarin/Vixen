// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Animation.Motions;

/// <summary>Everything a motion needs to know about the moment it is being evaluated at.</summary>
/// <param name="Parameters">The values blend trees read.</param>
/// <param name="Scratch">Where a blend gets its temporary poses.</param>
/// <param name="NormalizedTime">Where playback is, in <c>[0, 1]</c> across the motion's length.</param>
/// <param name="PreviousNormalizedTime">Where it was last frame, for events and root motion.</param>
/// <param name="Loops">How many whole passes were crossed since then.</param>
/// <param name="WantsRootMotion">Whether the return value will be used.</param>
/// <param name="Events">Where events go, or <see langword="null" /> to fire none.</param>
/// <param name="Layer">Which layer to attribute events to.</param>
/// <param name="State">Which state to attribute events to.</param>
/// <param name="Weight">How much this motion is contributing overall, for event filtering.</param>
/// <param name="Constraints">
///     Where a clip's live constraint tags go, or <see langword="null" /> to report none.
/// </param>
/// <param name="Weights">
///     Where a clip's blend-shape weights go, or <see langword="null" /> to report none.
/// </param>
/// <remarks>
///     <b>Time is normalised, not seconds.</b> That is what makes a blend tree work: a walk of 1.2 s
///     and a run of 0.8 s blended half and half have to put both feet down together, which they only
///     do if both are sampled at the same fraction of their own cycle. The tree's own length is the
///     weighted average of its children's, so a character speeding up from a walk to a run has its
///     stride rate move continuously rather than jumping when the dominant clip changes.
/// </remarks>
public readonly record struct MotionContext(
    AnimationParameters Parameters,
    PoseScratch Scratch,
    float NormalizedTime,
    float PreviousNormalizedTime,
    int Loops,
    bool WantsRootMotion,
    AnimationEventBuffer? Events,
    int Layer,
    string State,
    float Weight,
    Constraints.ConstraintTagBuffer? Constraints = null,
    MorphWeightBuffer? Weights = null
);

/// <summary>
///     Something that can pose a skeleton: one clip, or a tree of them blended by parameters.
/// </summary>
/// <remarks>
///     <para>
///         One abstraction for a clip and for a blend tree, because everything above them — a state,
///         a transition, a layer — has no business knowing which it got. A state that holds a clip
///         and a state that holds a two-dimensional locomotion tree are the same state.
///     </para>
///     <para>
///         <b>Pose, root motion and events come out of one call.</b> They are three answers to the
///         same question, and asking separately would mean walking the tree three times and
///         computing the same weights three times — and, worse, computing them from parameters that
///         a script could have changed in between.
///     </para>
/// </remarks>
public abstract class Motion {
    /// <summary>What the motion is called. Shown in a debugger and in the editor.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    ///     How long one pass takes at the current parameters, in seconds.
    /// </summary>
    /// <param name="parameters">The values the blend weights are computed from.</param>
    /// <returns>The length. Never zero.</returns>
    public abstract float Length(AnimationParameters parameters);

    /// <summary>Poses the skeleton, and reports what the root did.</summary>
    /// <param name="context">Where playback is and what it may use.</param>
    /// <param name="destination">One transform per joint.</param>
    /// <returns>
    ///     How far the root moved, or <see cref="RootMotionDelta.None" /> if
    ///     <see cref="MotionContext.WantsRootMotion" /> was not set.
    /// </returns>
    public abstract RootMotionDelta Evaluate(in MotionContext context, Span<BoneTransform> destination);
}
