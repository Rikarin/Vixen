// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Animation;

/// <summary>
///     Where one joint is, relative to its parent: translation, rotation and scale, and never a
///     matrix.
/// </summary>
/// <remarks>
///     <para>
///         Animation is interpolation, and a matrix is the one representation that cannot be
///         interpolated. Blending two matrices component by component gives a transform that is
///         neither of them and is not rigid — the classic collapsing-elbow artefact — while blending
///         a translation, a quaternion and a scale independently gives exactly the pose halfway
///         between the two. Every clip, every blend tree and every layer in this assembly works on
///         these; the matrix is produced once, at the end, by
///         <see cref="SkeletonPose.ComputeSkinningMatrices" />.
///     </para>
///     <para>
///         Non-uniform scale is carried because exporters emit it and dropping it silently would
///         make a scaled prop detach from the hand holding it. It is <em>not</em> composed
///         correctly through a rotated parent — nothing that decomposes to TRS can be, since the
///         product of a rotation and a non-uniform scale is a shear — so a rig that scales
///         non-uniformly under a rotation gets the same answer every other engine gives, which is
///         the approximation and not the shear.
///     </para>
///     <para>
///         The fields are public and mutable on purpose; see the note in <c>.editorconfig</c>. A
///         pose is a span this module writes in place.
///     </para>
/// </remarks>
public struct BoneTransform {
    /// <summary>Offset from the parent joint.</summary>
    public Vector3 Translation;

    /// <summary>Rotation relative to the parent joint.</summary>
    public Quaternion Rotation;

    /// <summary>Scale relative to the parent joint.</summary>
    public Vector3 Scale;

    /// <summary>Creates a transform.</summary>
    /// <param name="translation">Offset from the parent.</param>
    /// <param name="rotation">Rotation relative to the parent.</param>
    /// <param name="scale">Scale relative to the parent.</param>
    public BoneTransform(Vector3 translation, Quaternion rotation, Vector3 scale) {
        Translation = translation;
        Rotation = rotation;
        Scale = scale;
    }

    /// <summary>The transform that changes nothing.</summary>
    /// <remarks>
    ///     A property and not <c>default</c>: a zeroed <see cref="BoneTransform" /> has a zero scale
    ///     and a zero quaternion, which is a joint collapsed to a point. Anything building one by
    ///     hand starts here.
    /// </remarks>
    public static BoneTransform Identity => new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    /// <summary>This transform as a matrix.</summary>
    /// <returns>The local matrix, scale then rotation then translation.</returns>
    public readonly Matrix4x4 ToMatrix() => Matrix4x4.Compose(Scale, Rotation, Translation);

    /// <summary>Splits an affine matrix back into a bone transform.</summary>
    /// <param name="matrix">The matrix.</param>
    /// <returns>The transform. Shear, which nothing here produces, is not recoverable.</returns>
    public static BoneTransform FromMatrix(in Matrix4x4 matrix) {
        Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var translation);
        return new(translation, rotation, scale);
    }

    /// <summary>
    ///     Composes two transforms: <paramref name="local" /> applied first, then
    ///     <paramref name="parent" />.
    /// </summary>
    /// <param name="local">The transform closest to the object.</param>
    /// <param name="parent">The transform applied after it.</param>
    /// <returns>The combined transform.</returns>
    /// <remarks>
    ///     Argument order matches the row-vector convention — <c>world = local * parent</c>, read
    ///     left to right. See <c>Vixen.Core.Mathematics/Conventions.md</c>.
    /// </remarks>
    public static BoneTransform Concatenate(in BoneTransform local, in BoneTransform parent) =>
        new(
            parent.Translation + Quaternion.Transform(local.Translation * parent.Scale, parent.Rotation),
            Quaternion.Concatenate(local.Rotation, parent.Rotation),
            local.Scale * parent.Scale
        );

    /// <summary>The transform that undoes this one.</summary>
    /// <param name="value">The transform to invert.</param>
    /// <returns>The inverse. A zero component of the scale inverts to zero rather than to infinity.</returns>
    public static BoneTransform Inverse(in BoneTransform value) {
        var scale = new Vector3(
            SafeReciprocal(value.Scale.X),
            SafeReciprocal(value.Scale.Y),
            SafeReciprocal(value.Scale.Z)
        );

        var rotation = Quaternion.Conjugate(value.Rotation);
        return new(Quaternion.Transform(-value.Translation, rotation) * scale, rotation, scale);
    }

    /// <summary>
    ///     Interpolates between two transforms: the blend every other operation here is built out of.
    /// </summary>
    /// <param name="from">The transform at <paramref name="amount" /> = 0.</param>
    /// <param name="to">The transform at <paramref name="amount" /> = 1.</param>
    /// <param name="amount">The interpolant, clamped to <c>[0, 1]</c>.</param>
    /// <returns>The blended transform.</returns>
    /// <remarks>
    ///     Rotation goes through <see cref="Quaternion.Nlerp" /> rather than
    ///     <see cref="Quaternion.Slerp" />: the two are indistinguishable across the arcs a blend
    ///     actually spans, and a character has a hundred joints blended several times a frame, which
    ///     is where an <c>acos</c> and two <c>sin</c>s per joint per blend stops being free. A blend
    ///     across a wide arc — a crossfade between two poses facing opposite ways — is the case
    ///     where the constant angular velocity would show, and it is also the case where the pose in
    ///     between is wrong for reasons no interpolation fixes.
    /// </remarks>
    public static BoneTransform Lerp(in BoneTransform from, in BoneTransform to, float amount) {
        var t = MathUtil.Saturate(amount);

        return new(
            Vector3.Lerp(from.Translation, to.Translation, t),
            Quaternion.Nlerp(from.Rotation, to.Rotation, t),
            Vector3.Lerp(from.Scale, to.Scale, t)
        );
    }

    /// <summary>
    ///     What <paramref name="pose" /> does that <paramref name="reference" /> does not — the
    ///     difference an additive layer applies.
    /// </summary>
    /// <param name="pose">The posed transform.</param>
    /// <param name="reference">The pose it is measured against, usually the clip's first frame.</param>
    /// <returns>The difference, in <paramref name="reference" />'s space.</returns>
    public static BoneTransform Difference(in BoneTransform pose, in BoneTransform reference) =>
        new(
            pose.Translation - reference.Translation,
            Quaternion.Concatenate(Quaternion.Conjugate(reference.Rotation), pose.Rotation),
            new Vector3(
                pose.Scale.X * SafeReciprocal(reference.Scale.X),
                pose.Scale.Y * SafeReciprocal(reference.Scale.Y),
                pose.Scale.Z * SafeReciprocal(reference.Scale.Z)
            )
        );

    /// <summary>Applies an additive difference on top of a base transform.</summary>
    /// <param name="value">What the layers below produced.</param>
    /// <param name="additive">The difference, from <see cref="Difference" />.</param>
    /// <param name="weight">How much of it to apply, clamped to <c>[0, 1]</c>.</param>
    /// <returns>The result.</returns>
    /// <remarks>
    ///     The weight scales the difference rather than blending towards the sum, so weight zero is
    ///     exactly <paramref name="value" /> and a half-weight lean is half a lean rather than a
    ///     half-way blend to a leaning pose. That distinction is the entire reason additive layers
    ///     exist: an aim offset applied at 40 % should aim 40 % of the way, whatever the locomotion
    ///     underneath is doing.
    /// </remarks>
    public static BoneTransform Add(in BoneTransform value, in BoneTransform additive, float weight) {
        var w = MathUtil.Saturate(weight);

        return new(
            value.Translation + (additive.Translation * w),
            Quaternion.Concatenate(
                value.Rotation,
                Quaternion.Nlerp(Quaternion.Identity, additive.Rotation, w)
            ),
            value.Scale * Vector3.Lerp(Vector3.One, additive.Scale, w)
        );
    }

    static float SafeReciprocal(float value) => MathUtil.IsZero(value) ? 0f : 1f / value;
}
