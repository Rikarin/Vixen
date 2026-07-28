// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Animation;

/// <summary>
///     One skeleton, posed: a local transform per joint, and the two derived forms anything outside
///     this assembly asks for.
/// </summary>
/// <remarks>
///     <para>
///         <b>Local space is the only space stored.</b> Everything that composes — a clip, a blend,
///         a layer, a mask — is defined joint by joint against the parent, and converting to model
///         space is a forward pass that costs one multiply per joint. Storing model space instead
///         would make every blend wrong: interpolating two model-space poses stretches the bones
///         between the joints, which is the artefact that makes a naïve crossfade look like rubber.
///     </para>
///     <para>
///         <b>One allocation, reused.</b> A pose is per character and per frame, and there are
///         several of them alive at once — the layer stack alone needs a scratch pose per layer. The
///         array is allocated when the pose is created and never again; the operations take and
///         return spans so that none of them can allocate either.
///     </para>
/// </remarks>
public sealed class SkeletonPose {
    readonly BoneTransform[] bones;

    /// <summary>Creates a pose for a skeleton, in its bind pose.</summary>
    /// <param name="skeleton">The skeleton this poses.</param>
    public SkeletonPose(Skeleton skeleton) {
        ArgumentNullException.ThrowIfNull(skeleton);

        Skeleton = skeleton;
        bones = new BoneTransform[skeleton.JointCount];
        ResetToBindPose();
    }

    /// <summary>The skeleton this poses.</summary>
    public Skeleton Skeleton { get; }

    /// <summary>How many joints it holds.</summary>
    public int JointCount => bones.Length;

    /// <summary>The local transforms, in joint order, writable in place.</summary>
    public Span<BoneTransform> Bones => bones;

    /// <summary>The local transforms, in joint order.</summary>
    public ReadOnlySpan<BoneTransform> ReadBones => bones;

    /// <summary>One joint's local transform, writable in place.</summary>
    /// <param name="joint">The joint's index.</param>
    /// <returns>A reference into the pose.</returns>
    public ref BoneTransform this[int joint] => ref bones[joint];

    /// <summary>Puts every joint back where the artist modelled it.</summary>
    public void ResetToBindPose() => Skeleton.BindPose.CopyTo(bones);

    /// <summary>Copies another pose of the same skeleton over this one.</summary>
    /// <param name="source">The pose to copy.</param>
    public void CopyFrom(SkeletonPose source) {
        ArgumentNullException.ThrowIfNull(source);
        source.ReadBones.CopyTo(bones);
    }

    /// <summary>Copies raw joint transforms over this pose.</summary>
    /// <param name="source">One transform per joint.</param>
    public void CopyFrom(ReadOnlySpan<BoneTransform> source) => source.CopyTo(bones);

    /// <summary>Composes every joint's transform relative to the model's origin.</summary>
    /// <param name="destination">One transform per joint, filled in joint order.</param>
    /// <remarks>
    ///     A single forward loop with no recursion and no visited set, which is what the
    ///     parents-precede-children invariant <see cref="Skeleton.TryCreate" /> enforces buys.
    /// </remarks>
    public void ComputeModelSpace(Span<BoneTransform> destination) =>
        ComputeModelSpace(Skeleton, bones, destination);

    /// <summary>Composes joint transforms relative to the model's origin.</summary>
    /// <param name="skeleton">The skeleton whose parents describe the hierarchy.</param>
    /// <param name="local">One local transform per joint.</param>
    /// <param name="destination">One transform per joint, filled in joint order.</param>
    public static void ComputeModelSpace(
        Skeleton skeleton,
        ReadOnlySpan<BoneTransform> local,
        Span<BoneTransform> destination
    ) {
        ArgumentNullException.ThrowIfNull(skeleton);

        var parents = skeleton.Parents;

        for (var index = 0; index < parents.Length; index++) {
            var parent = parents[index];

            destination[index] = parent < 0
                ? local[index]
                : BoneTransform.Concatenate(local[index], destination[parent]);
        }
    }

    /// <summary>
    ///     Undoes <see cref="ComputeModelSpace(Skeleton, ReadOnlySpan{BoneTransform}, Span{BoneTransform})" />:
    ///     model-space transforms back to local ones.
    /// </summary>
    /// <param name="skeleton">The skeleton whose parents describe the hierarchy.</param>
    /// <param name="model">One model-space transform per joint.</param>
    /// <param name="destination">One local transform per joint, filled in joint order.</param>
    /// <remarks>
    ///     What an IK solver needs on the way back. A solver reasons in model space — a foot is at a
    ///     world position, not at an offset from a knee — and the pose it has to hand its answer to
    ///     is local, so the round trip is part of the contract rather than an inefficiency.
    /// </remarks>
    public static void ComputeLocalSpace(
        Skeleton skeleton,
        ReadOnlySpan<BoneTransform> model,
        Span<BoneTransform> destination
    ) {
        ArgumentNullException.ThrowIfNull(skeleton);

        var parents = skeleton.Parents;

        for (var index = 0; index < parents.Length; index++) {
            var parent = parents[index];

            destination[index] = parent < 0
                ? model[index]
                : BoneTransform.Concatenate(model[index], BoneTransform.Inverse(model[parent]));
        }
    }

    /// <summary>
    ///     The bone palette GPU skinning reads: <c>inverseBindPose * jointModelSpace</c>, one matrix
    ///     per joint.
    /// </summary>
    /// <param name="destination">One matrix per joint, filled in joint order.</param>
    /// <param name="scratch">
    ///     A model-space buffer of at least <see cref="JointCount" /> entries, or empty to have one
    ///     allocated.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         Already multiplied, because that is what <c>SkinningRenderFeature</c> wants: one
    ///         multiply per joint per frame on the CPU instead of one per vertex on the GPU. A
    ///         character has a hundred joints and tens of thousands of vertices.
    ///     </para>
    ///     <para>
    ///         Model space and not world space. The object's own transform is pushed separately by
    ///         <c>TransformRenderFeature</c> and applied after skinning, so folding it in here would
    ///         apply it twice — and would stop a hundred instances of the same animation sharing a
    ///         palette, which is what makes crowd rendering possible at all.
    ///     </para>
    /// </remarks>
    public void ComputeSkinningMatrices(Span<Matrix4x4> destination, Span<BoneTransform> scratch = default) {
        var model = scratch.Length >= bones.Length ? scratch[..bones.Length] : new BoneTransform[bones.Length];
        ComputeModelSpace(model);

        var inverseBind = Skeleton.InverseBindPoses;

        for (var index = 0; index < bones.Length; index++) {
            // Row-vector order: out of model space into the joint's bind space first, then out
            // through where the joint is now. See Vixen.Core.Mathematics/Conventions.md.
            destination[index] = inverseBind[index] * model[index].ToMatrix();
        }
    }
}
