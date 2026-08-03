// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Animation.Constraints;

/// <summary>A goal a clip carries, over a span of its own time.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Over clip time, not wall time, and normalised.</b> A clip played at 0.7 speed keeps
///         its contacts where they were authored, and a retimed clip does not need re-marking. Both
///         fall out of the span being a fraction of the clip rather than a number of seconds.
///     </para>
///     <para>
///         <b>Weight is the product of three things</b>, and all three are continuous, which is what
///         makes the result continuous: the clip's own blend weight in the tree, this tag's
///         activation from its span and easing, and any suppression a game system applied by label.
///     </para>
/// </remarks>
public sealed class ConstraintTag {
    /// <summary>What it asks for.</summary>
    public required ConstraintGoal Goal { get; init; }

    /// <summary>Where in the clip it starts, in <c>[0, 1]</c>.</summary>
    public float Begin { get; init; }

    /// <summary>Where in the clip it ends, in <c>[0, 1]</c>. One is the whole clip.</summary>
    public float End { get; init; } = 1f;

    /// <summary>How much of the clip it takes to fade in, as a fraction.</summary>
    public float EaseIn { get; init; }

    /// <summary>How much of the clip it takes to fade out, as a fraction.</summary>
    public float EaseOut { get; init; }

    /// <summary>The most of it that ever applies, in <c>[0, 1]</c>.</summary>
    public float MaxWeight { get; init; } = 1f;

    /// <summary>How much of the tag is live at a moment in the clip.</summary>
    /// <param name="phase">Where playback is, in <c>[0, 1]</c>.</param>
    /// <returns>The activation, in <c>[0, 1]</c>.</returns>
    /// <remarks>
    ///     A span that wraps — <c>End</c> before <c>Begin</c> — is a contact that straddles the loop
    ///     point, which is the ordinary case for a foot that plants near the end of a cycle and lifts
    ///     near the start of the next.
    /// </remarks>
    public float Activation(float phase) {
        var live = End >= Begin
            ? phase >= Begin && phase <= End
            : phase >= Begin || phase <= End;

        if (!live) {
            return 0f;
        }

        var into = phase >= Begin ? phase - Begin : (phase + 1f) - Begin;
        var span = End >= Begin ? End - Begin : (End + 1f) - Begin;
        var ramp = 1f;

        if (EaseIn > 0f) {
            ramp = MathF.Min(ramp, MathUtil.Saturate(into / EaseIn));
        }

        if (EaseOut > 0f) {
            ramp = MathF.Min(ramp, MathUtil.Saturate((span - into) / EaseOut));
        }

        return ramp * MathUtil.Saturate(MaxWeight);
    }
}

/// <summary>Every goal one clip carries.</summary>
/// <remarks>
///     A separate object from the clip's channels because it is separately authored, separately
///     versioned and separately optional: most clips carry none, and the ones that do are edited by
///     somebody looking at a timeline rather than at a curve.
/// </remarks>
public sealed class ConstraintTrack {
    readonly ConstraintTag[] tags;

    /// <summary>Builds a track.</summary>
    /// <param name="tags">The tags.</param>
    public ConstraintTrack(params ReadOnlySpan<ConstraintTag> tags) => this.tags = tags.ToArray();

    /// <summary>How many tags it holds.</summary>
    public int Count => tags.Length;

    /// <summary>The tags.</summary>
    /// <returns>The tags.</returns>
    public ReadOnlySpan<ConstraintTag> Tags => tags;

    /// <summary>One tag.</summary>
    /// <param name="index">Which.</param>
    /// <returns>The tag.</returns>
    public ConstraintTag this[int index] => tags[index];
}

/// <summary>A tag that is live this frame, and how much of it.</summary>
/// <param name="Tag">The tag.</param>
/// <param name="Track">The track it came from, which with the index is what identifies it.</param>
/// <param name="Index">Its position in that track.</param>
/// <param name="Weight">
///     The clip's blend weight times the tag's own activation, in <c>[0, 1]</c>.
/// </param>
/// <param name="Phase">Where the clip that carries it is, in <c>[0, 1]</c>.</param>
public readonly record struct LiveConstraintTag(
    ConstraintTag Tag,
    ConstraintTrack Track,
    int Index,
    float Weight,
    float Phase
);

/// <summary>Where a frame's live clip constraints are collected.</summary>
/// <remarks>
///     <para>
///         The same shape as <see cref="AnimationEventBuffer" />, and for the same reason: a tag
///         becomes live in the middle of evaluating a blend tree, at a point where the pose is
///         half-built and the layer stack is mid-flight. Collecting and reading afterwards is the
///         only ordering the constraint stage can work with — it has to see <em>every</em> clip's
///         contribution before it can decide what a chain does.
///     </para>
///     <para>
///         ⚠ <b>Weights add across clips, and this is where "a hand goal holds through a blend"
///         comes from.</b> Two clips at 0.6 and 0.4 that both carry the same authored contact
///         contribute 0.6 and 0.4 of it; one that carries it and one that does not contribute 0.6 and
///         nothing, and the goal simply weakens rather than disappearing when the second clip takes
///         over. Neither case needs the stage to know that a transition is happening.
///     </para>
/// </remarks>
public sealed class ConstraintTagBuffer {
    readonly List<LiveConstraintTag> live = [];

    /// <summary>How many tags are live.</summary>
    public int Count => live.Count;

    /// <summary>One of them.</summary>
    /// <param name="index">Which.</param>
    /// <returns>The tag.</returns>
    public LiveConstraintTag this[int index] => live[index];

    /// <summary>Records a live tag.</summary>
    /// <param name="tag">The tag.</param>
    public void Add(in LiveConstraintTag tag) => live.Add(tag);

    /// <summary>Records every live tag in a track.</summary>
    /// <param name="track">The track.</param>
    /// <param name="phase">Where the clip is, in <c>[0, 1]</c>.</param>
    /// <param name="weight">How much the clip is contributing.</param>
    public void Collect(ConstraintTrack? track, float phase, float weight) {
        if (track is null || weight <= 0f) {
            return;
        }

        for (var index = 0; index < track.Count; index++) {
            var tag = track[index];
            var activation = tag.Activation(phase);

            if (activation > 0f) {
                live.Add(new(tag, track, index, activation * weight, phase));
            }
        }
    }

    /// <summary>Empties the buffer, keeping its capacity.</summary>
    public void Clear() => live.Clear();

    /// <summary>Walks this frame's live tags.</summary>
    /// <returns>The enumerator.</returns>
    public List<LiveConstraintTag>.Enumerator GetEnumerator() => live.GetEnumerator();
}
