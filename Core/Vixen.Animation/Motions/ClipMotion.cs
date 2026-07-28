// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Animation.Motions;

/// <summary>One clip, played straight: the leaf every blend tree is built out of.</summary>
/// <remarks>
///     A wrapper and not the clip itself, because a clip is shared content and the things that make
///     it a <em>motion</em> — how fast it plays, whether it is additive, what it is measured against
///     — belong to the place it is used. The same walk cycle appears in a locomotion tree at speed
///     one and in a limp at speed 0.7, and there is one clip.
/// </remarks>
public sealed class ClipMotion : Motion {
    readonly BoneTransform[]? additiveReference;

    /// <summary>Wraps a clip.</summary>
    /// <param name="clip">The clip.</param>
    /// <param name="speed">How fast it plays. One is as authored.</param>
    /// <param name="additive">
    ///     Whether the pose it produces is a difference to be added rather than a pose to be blended.
    /// </param>
    /// <param name="additiveReferenceTime">
    ///     Which frame of the clip the difference is measured against, in seconds. The default —
    ///     zero — is the clip's own first frame, which is what an aim offset or a lean is authored
    ///     relative to.
    /// </param>
    public ClipMotion(AnimationClip clip, float speed = 1f, bool additive = false, float additiveReferenceTime = 0f) {
        ArgumentNullException.ThrowIfNull(clip);

        Clip = clip;
        Speed = speed;
        IsAdditive = additive;
        Name = clip.Name;

        if (!additive) {
            return;
        }

        // Sampled once, here, and not per frame. The reference is a property of how the clip was
        // authored; re-sampling it every frame would be the same answer at the cost of a second
        // full pose evaluation for every additive layer on every character.
        additiveReference = new BoneTransform[clip.Skeleton.JointCount];
        clip.Sample(additiveReferenceTime, additiveReference);
    }

    /// <summary>The clip being played.</summary>
    public AnimationClip Clip { get; }

    /// <summary>How fast it plays relative to how it was authored.</summary>
    public float Speed { get; }

    /// <summary>Whether it produces a difference rather than a pose.</summary>
    public bool IsAdditive { get; }

    /// <inheritdoc />
    /// <remarks>
    ///     Divided by the speed, so a clip at double speed reports half the length — which is what
    ///     makes a blend between a clip at 1× and the same clip at 2× land at 1.5× rather than at
    ///     whichever one the tree happened to take its length from.
    /// </remarks>
    public override float Length(AnimationParameters parameters) {
        var scaled = Clip.Duration / MathF.Max(MathF.Abs(Speed), 1e-4f);
        return MathF.Max(scaled, 1e-4f);
    }

    /// <inheritdoc />
    public override RootMotionDelta Evaluate(in MotionContext context, Span<BoneTransform> destination) {
        var duration = Clip.Duration;
        var time = context.NormalizedTime * duration;
        var previous = context.PreviousNormalizedTime * duration;

        Clip.Sample(time, destination);

        if (IsAdditive && additiveReference is not null) {
            PoseBlend.MakeAdditive(destination, destination, additiveReference);
        }

        if (context.Events is not null) {
            Clip.CollectEvents(
                previous,
                time,
                context.Loops,
                context.Events,
                context.Layer,
                context.State,
                context.Weight
            );
        }

        return context.WantsRootMotion
            ? Clip.ExtractRootMotion(previous, time, context.Loops)
            : RootMotionDelta.None;
    }
}
