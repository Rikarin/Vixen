// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Animation;

/// <summary>What to do with the motion baked into a clip's root joint.</summary>
/// <remarks>
///     <para>
///         An artist animating a run cycle moves the character forward, because that is the only way
///         the feet can be planted. What the game wants is a character whose feet are planted
///         <em>and</em> whose position is decided by gameplay. Root motion is the reconciliation:
///         the root joint's per-frame movement is taken out of the pose and handed to whoever owns
///         the entity's transform.
///     </para>
///     <para>
///         Which is why this is a mode and not a flag. A character controller that owns its own
///         velocity wants <see cref="Extract" /> — give me the delta, I will decide what to do with
///         it, and I will not be teleported through a wall by an animation. A cutscene or a
///         traversal animation wants <see cref="Apply" />. And a clip authored in place wants
///         <see cref="Disabled" />, where the root joint is just another joint.
///     </para>
/// </remarks>
public enum RootMotionMode {
    /// <summary>The root joint animates like any other. Nothing is taken out of the pose.</summary>
    Disabled,

    /// <summary>
    ///     The delta is taken out of the pose and reported, and nothing is moved. The caller applies
    ///     it — after collision, after a speed multiplier, or not at all.
    /// </summary>
    Extract,

    /// <summary>The delta is taken out of the pose and written straight to the entity's transform.</summary>
    Apply
}

/// <summary>How far the root moved between two moments, in the character's own frame.</summary>
/// <param name="Translation">The offset, before the entity's own rotation is applied.</param>
/// <param name="Rotation">The turn.</param>
/// <remarks>
///     <para>
///         In the character's frame and not the world's, so a delta sampled once is valid for a
///         character facing any direction — which is what makes one run cycle work for a character
///         that is also being turned by input.
///     </para>
///     <para>
///         No scale. A clip that scales its root is scaling the character, and that is a pose, not
///         motion: applying it to the entity would compound every frame.
///     </para>
/// </remarks>
public readonly record struct RootMotionDelta(Vector3 Translation, Quaternion Rotation) {
    /// <summary>No movement.</summary>
    public static RootMotionDelta None => new(Vector3.Zero, Quaternion.Identity);

    /// <summary>Whether this delta moves anything.</summary>
    public bool IsZero => Translation == Vector3.Zero && Rotation.IsIdentity;

    /// <summary>
    ///     The delta from one root transform to another: apply this, then you were at
    ///     <paramref name="to" />.
    /// </summary>
    /// <param name="from">Where the root was.</param>
    /// <param name="to">Where the root is.</param>
    /// <returns>The delta.</returns>
    public static RootMotionDelta Between(in BoneTransform from, in BoneTransform to) {
        var inverseRotation = Quaternion.Conjugate(from.Rotation);

        return new(
            Quaternion.Transform(to.Translation - from.Translation, inverseRotation),
            Quaternion.Concatenate(inverseRotation, to.Rotation)
        );
    }

    /// <summary>Chains two deltas: <paramref name="first" />, then <paramref name="second" />.</summary>
    /// <param name="first">The earlier delta.</param>
    /// <param name="second">The later one, expressed in the frame the first one left behind.</param>
    /// <returns>The combined delta.</returns>
    /// <remarks>
    ///     What a frame that crosses a loop point needs: the tail of one pass, then whole passes,
    ///     then the head of the next. Each is measured in the frame the previous one ended in, so
    ///     the second's translation has to be turned by the first's rotation before it is added —
    ///     which is the difference between a looping turn-in-place ending where it should and
    ///     drifting a degree per second.
    /// </remarks>
    public static RootMotionDelta Chain(in RootMotionDelta first, in RootMotionDelta second) =>
        new(
            first.Translation + Quaternion.Transform(second.Translation, first.Rotation),
            Quaternion.Concatenate(first.Rotation, second.Rotation)
        );

    /// <summary>Scales a delta, for a clip playing at other than its authored speed.</summary>
    /// <param name="amount">The multiplier.</param>
    /// <returns>The scaled delta.</returns>
    /// <remarks>
    ///     Used to weight one motion's contribution in a blend, where the alternative — sampling the
    ///     blended pose's root twice — would give the root of a pose that is the average of two
    ///     clips, and the average of two roots is not the average of two motions.
    /// </remarks>
    public RootMotionDelta Scaled(float amount) =>
        new(Translation * amount, Quaternion.Nlerp(Quaternion.Identity, Rotation, amount));

    /// <summary>Averages deltas by weight, the same way a blend averages poses.</summary>
    /// <param name="from">The delta at <paramref name="amount" /> = 0.</param>
    /// <param name="to">The delta at <paramref name="amount" /> = 1.</param>
    /// <param name="amount">The interpolant, clamped to <c>[0, 1]</c>.</param>
    /// <returns>The blended delta.</returns>
    public static RootMotionDelta Lerp(in RootMotionDelta from, in RootMotionDelta to, float amount) {
        var t = MathUtil.Saturate(amount);

        return new(
            Vector3.Lerp(from.Translation, to.Translation, t),
            Quaternion.Nlerp(from.Rotation, to.Rotation, t)
        );
    }

    /// <summary>The delta as a transform, for composing onto an entity's local transform.</summary>
    /// <returns>The transform, unscaled.</returns>
    public BoneTransform ToTransform() => new(Translation, Rotation, Vector3.One);
}
