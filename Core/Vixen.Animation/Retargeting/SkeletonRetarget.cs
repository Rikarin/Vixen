// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering;

namespace Vixen.Animation.Retargeting;

/// <summary>
///     Moves an animation from the skeleton it was authored on to another one: same motion, the
///     target rig's proportions.
/// </summary>
/// <remarks>
///     <para>
///         <b>The transfer is in model space and relative to each rig's bind pose.</b> For every
///         mapped joint, the source's model-space rotation is measured against its own bind pose —
///         that difference is what the <em>animation</em> does, with the rig's resting shape divided
///         out — and the same difference is applied to the target's bind pose. A clip authored on an
///         A-pose rig therefore plays correctly on a T-pose one, which is the case the whole
///         construction exists for.
///     </para>
///     <para>
///         <b>Why not the local-space shortcut.</b> The cheap version takes the source's difference
///         from bind in its own <em>local</em> space and applies it to the target's local bind. It is
///         per-joint, needs no chain, and would let a clip be retargeted channel by channel with its
///         keys preserved. It is also wrong whenever the two rigs' bind poses differ by a rotation
///         anywhere up the chain, because the difference is then expressed in a parent frame the
///         target does not share — the elbow bends about the wrong axis by however much the two
///         shoulders disagree. That is exactly the A-pose-to-T-pose case, so the shortcut fails at
///         the only job worth doing.
///     </para>
///     <para>
///         <b>Only one joint moves.</b> Every other mapped joint keeps the target rig's own bone
///         lengths and takes rotation alone; the pelvis takes the source's model-space displacement
///         scaled by <see cref="TranslationScale" />, so a character half the height travels half as
///         far and its feet still meet the ground.
///     </para>
///     <para>
///         Not thread-safe: the model-space working buffers belong to the instance. One per
///         animator, like every other per-character object here.
///     </para>
/// </remarks>
public sealed class SkeletonRetarget {
    readonly BoneTransform[] sourceBindModel;
    readonly BoneTransform[] targetBindModel;
    readonly BoneTransform[] sourceModel;
    readonly BoneTransform[] targetModel;

    /// <summary>Creates a retarget for a mapping.</summary>
    /// <param name="map">Which joint drives which.</param>
    public SkeletonRetarget(RetargetMap map) {
        ArgumentNullException.ThrowIfNull(map);

        Map = map;
        sourceBindModel = new BoneTransform[map.Source.JointCount];
        targetBindModel = new BoneTransform[map.Target.JointCount];
        sourceModel = new BoneTransform[map.Source.JointCount];
        targetModel = new BoneTransform[map.Target.JointCount];

        SkeletonPose.ComputeModelSpace(map.Source, map.Source.BindPose, sourceBindModel);
        SkeletonPose.ComputeModelSpace(map.Target, map.Target.BindPose, targetBindModel);

        TranslationScale = DeriveScale(map, sourceBindModel, targetBindModel);
    }

    /// <summary>Which joint drives which.</summary>
    public RetargetMap Map { get; }

    /// <summary>
    ///     How much of the source's movement the target takes, as a ratio of their proportions.
    /// </summary>
    /// <remarks>
    ///     Derived from how high the two rigs' pelvises sit in their bind poses, which is the
    ///     standard proxy for "how big is this character" and the one that makes a stride land where
    ///     the feet are. Settable, because a rig with an unusual pelvis height — a quadruped, a
    ///     character modelled at a hundred units to the metre — will want its own number, and
    ///     because a designer sometimes wants a stride that is not to scale.
    /// </remarks>
    public float TranslationScale { get; set; }

    /// <summary>Retargets one pose.</summary>
    /// <param name="sourcePose">The source skeleton's local transforms.</param>
    /// <param name="targetPose">Where the target skeleton's local transforms go.</param>
    public void Apply(ReadOnlySpan<BoneTransform> sourcePose, Span<BoneTransform> targetPose) {
        SkeletonPose.ComputeModelSpace(Map.Source, sourcePose, sourceModel);

        var target = Map.Target;
        var bind = target.BindPose;
        var sourceOf = Map.SourceOf;
        var modes = Map.Modes;

        for (var index = 0; index < targetPose.Length; index++) {
            var parent = target.ParentOf(index);

            var parentModel = parent < 0
                ? BoneTransform.Identity
                : targetModel[parent];

            var driver = sourceOf[index];
            var mode = modes[index];

            if (driver < 0 || mode is RetargetMode.Ignore) {
                targetPose[index] = bind[index];
                targetModel[index] = BoneTransform.Concatenate(bind[index], parentModel);

                continue;
            }

            // What the animation does, with the source rig's resting shape divided out. In model
            // space, so it is a rotation about world axes rather than about whatever axes the
            // source's parent happened to have.
            var difference = Quaternion.Concatenate(
                Quaternion.Conjugate(sourceBindModel[driver].Rotation),
                sourceModel[driver].Rotation
            );

            var modelRotation = Quaternion.Concatenate(targetBindModel[index].Rotation, difference);

            if (mode is not RetargetMode.RotationAndTranslation) {
                // The target's own bone length is kept. That is the difference between retargeting
                // and copying, and it is why a short character's arms do not grow.
                targetPose[index] = new(
                    bind[index].Translation,
                    Quaternion.Concatenate(modelRotation, Quaternion.Conjugate(parentModel.Rotation)),
                    bind[index].Scale
                );

                targetModel[index] = BoneTransform.Concatenate(targetPose[index], parentModel);

                continue;
            }

            var displacement = (sourceModel[driver].Translation - sourceBindModel[driver].Translation)
                * TranslationScale;

            var model = new BoneTransform(
                targetBindModel[index].Translation + displacement,
                modelRotation,
                targetBindModel[index].Scale
            );

            targetPose[index] = BoneTransform.Concatenate(model, BoneTransform.Inverse(parentModel));
            targetModel[index] = model;
        }
    }

    /// <summary>Retargets a whole clip, producing one baked against the target skeleton.</summary>
    /// <param name="clip">The clip, baked against <see cref="RetargetMap.Source" />.</param>
    /// <param name="sampleRate">How many times a second the source is sampled.</param>
    /// <returns>A clip on the target skeleton, with the same duration, events and root motion.</returns>
    /// <exception cref="ArgumentException"><paramref name="clip" /> belongs to another skeleton.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>Baking, rather than retargeting every frame.</b> The result is an ordinary
    ///         <see cref="AnimationClip" /> on the target skeleton, so blend trees, masks, layers and
    ///         the state machine all work on it without knowing it was retargeted, and the per-frame
    ///         cost is zero. <see cref="Apply" /> is here for the case where a clip cannot be baked —
    ///         a live capture, a procedural source — and it is the same code.
    ///     </para>
    ///     <para>
    ///         <b>Resampling is the lossy step, and it is unavoidable.</b> The transfer composes the
    ///         whole chain, so a target joint's curve depends on its ancestors' curves and there is
    ///         no per-channel operation that could carry the source's keys across. Thirty hertz
    ///         matches what the tools that produced the clip almost certainly authored at; raise it
    ///         for anything with a snap in it, and run
    ///         <see cref="AnimationCurveCompressor.Compress(AnimationClipData)" /> over the result to take back what the
    ///         uniform grid wasted on the parts that were not moving.
    ///     </para>
    /// </remarks>
    public AnimationClip Bake(AnimationClip clip, float sampleRate = 30f) {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        if (!ReferenceEquals(clip.Skeleton, Map.Source)) {
            throw new ArgumentException(
                $"Clip '{clip.Name}' is baked against skeleton '{clip.Skeleton.Name}', and this "
                + $"retarget reads '{Map.Source.Name}'.",
                nameof(clip)
            );
        }

        var target = Map.Target;
        var samples = Math.Max(2, (int)MathF.Ceiling(clip.Duration * sampleRate) + 1);
        var step = clip.Duration / (samples - 1);

        var times = new float[samples];
        var sourcePose = new BoneTransform[Map.Source.JointCount];
        var targetPose = new BoneTransform[target.JointCount];
        var rotations = new Quaternion[target.JointCount][];
        var translations = new Vector3[target.JointCount][];
        var sourceOf = Map.SourceOf;
        var modes = Map.Modes;

        for (var joint = 0; joint < target.JointCount; joint++) {
            if (sourceOf[joint] < 0 || modes[joint] is RetargetMode.Ignore) {
                continue;
            }

            rotations[joint] = new Quaternion[samples];

            if (modes[joint] is RetargetMode.RotationAndTranslation) {
                translations[joint] = new Vector3[samples];
            }
        }

        for (var sample = 0; sample < samples; sample++) {
            var time = sample == samples - 1 ? clip.Duration : sample * step;
            times[sample] = time;

            clip.Sample(time, sourcePose);
            Apply(sourcePose, targetPose);

            for (var joint = 0; joint < target.JointCount; joint++) {
                if (rotations[joint] is { } rotation) {
                    rotation[sample] = targetPose[joint].Rotation;
                }

                if (translations[joint] is { } translation) {
                    translation[sample] = targetPose[joint].Translation;
                }
            }
        }

        var channels = new List<AnimationChannel>(Map.MappedJointCount);

        for (var joint = 0; joint < target.JointCount; joint++) {
            if (rotations[joint] is not { } rotation) {
                continue;
            }

            channels.Add(
                new() {
                    Target = target.NameOf(joint),
                    RotationTimes = times,
                    Rotations = rotation,
                    PositionTimes = translations[joint] is null ? [] : times,
                    Positions = translations[joint] ?? []
                }
            );
        }

        var data = new AnimationClipData {
            Name = clip.Name,
            Duration = clip.Duration,
            Channels = [.. channels]
        };

        var translationJoint = Map.TranslationJoint;

        return AnimationClip.Create(
            data,
            target,
            clip.Events.ToArray(),
            translationJoint >= 0 ? target.NameOf(translationJoint) : null
        );
    }

    static float DeriveScale(RetargetMap map, BoneTransform[] source, BoneTransform[] target) {
        var joint = map.TranslationJoint;

        if (joint < 0) {
            return 1f;
        }

        var driver = map.SourceOf[joint];

        if (driver < 0) {
            return 1f;
        }

        var from = MathF.Abs(source[driver].Translation.Y);
        return from > 1e-4f ? target[joint].Translation.Y / source[driver].Translation.Y : 1f;
    }
}
