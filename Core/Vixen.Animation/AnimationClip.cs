// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Animation.Constraints;
using Vixen.Core.Mathematics;
using Vixen.Rendering;

namespace Vixen.Animation;

/// <summary>What happens when a clip's time runs past its end.</summary>
public enum WrapMode {
    /// <summary>Stops on the last frame and stays there.</summary>
    Clamp,

    /// <summary>Starts again from the beginning.</summary>
    Loop,

    /// <summary>Plays backwards to the beginning, then forwards again.</summary>
    PingPong
}

/// <summary>
///     A clip as the runtime plays it: keys resolved to joint indices, sampled by time, with events
///     and a root joint.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="AnimationClipData" /> is what an import writes — channels addressed by joint
///         <em>name</em>, because an importer has no skeleton to resolve them against and no
///         guarantee the skeleton it would resolve against is the one the clip will be played on.
///         This is the resolution: a clip is baked against a skeleton once, at load, and the names
///         never appear again. A frame does array indexing.
///     </para>
///     <para>
///         <b>A clip is shared and immutable.</b> A hundred enemies playing the same run cycle hold
///         one of these between them and a playback time each, so nothing about sampling may depend
///         on who is sampling — which is why <see cref="Sample" /> takes a time and a destination
///         and keeps nothing.
///     </para>
///     <para>
///         <b>Keys are found in constant time, through an index and not a cursor.</b> The usual
///         optimisation is a cursor — remember where the last sample landed and step forward from
///         there — and it cannot live here, because a clip is shared by every instance playing it;
///         living on the player means a hint per track per clip per instance, over a set of clips
///         that changes as a blend tree's parameter moves. The index is the same win without the
///         state: each long track carries a table of one entry per key, mapping a uniform slice of
///         the clip's duration to the key at or before it, so a lookup is one multiply, one array
///         read and — because there is on average one key per slice — about one comparison. It is
///         O(1) rather than the cursor's amortised O(1), it is correct for a random seek as well as
///         for forward playback, and being immutable it is safe to sample from several threads at
///         once. It costs four bytes a key against the twelve to sixteen the key itself takes, and
///         short tracks (eight keys or fewer) skip it and binary-search instead. Measured, a random
///         seek costs what a forward step costs and thirty times the keys costs fifteen percent more
///         — see <c>Benchmarks/Vixen.Benchmarks.Animation</c>, where the residual growth is the
///         working set rather than the search.
///     </para>
///     <para>
///         <b>Rotations are packed into eight bytes.</b> See <see cref="PackedQuaternion" /> — half
///         the memory and half the cache traffic on the tracks that dominate a skeletal clip, for an
///         angular error five orders of magnitude below what blending them in <c>float</c> preserves.
///     </para>
/// </remarks>
public sealed class AnimationClip {
    /// <summary>Tracks at or below this many keys are searched rather than indexed.</summary>
    /// <remarks>
    ///     Eight keys is at most three comparisons, which is the cost of the index's own array read
    ///     plus its advance. Below that the table is memory spent to save nothing — and an exporter
    ///     that emitted two keys for a joint that barely moves produces a great many such tracks.
    /// </remarks>
    const int IndexThreshold = 8;

    readonly Track[] tracks;
    readonly float[] positionTimes;
    readonly Vector3[] positions;
    readonly float[] rotationTimes;
    readonly PackedQuaternion[] rotations;
    readonly float[] scaleTimes;
    readonly Vector3[] scales;
    readonly int[] buckets;
    readonly AnimationEvent[] events;
    readonly string[] shapes;
    readonly WeightTrack[] weightTracks;
    readonly float[] weightTimes;
    readonly float[] weightValues;

    AnimationClip(
        string name,
        float duration,
        Skeleton skeleton,
        Track[] tracks,
        float[] positionTimes,
        Vector3[] positions,
        float[] rotationTimes,
        PackedQuaternion[] rotations,
        float[] scaleTimes,
        Vector3[] scales,
        int[] buckets,
        AnimationEvent[] events,
        int rootJoint,
        int unresolvedChannels,
        ConstraintTrack? constraints,
        WeightTracks weights
    ) {
        shapes = weights.Shapes;
        weightTracks = weights.Tracks;
        weightTimes = weights.Times;
        weightValues = weights.Values;
        Name = name;
        Duration = duration;
        Skeleton = skeleton;
        RootJoint = rootJoint;
        UnresolvedChannels = unresolvedChannels;
        this.tracks = tracks;
        this.positionTimes = positionTimes;
        this.positions = positions;
        this.rotationTimes = rotationTimes;
        this.rotations = rotations;
        this.scaleTimes = scaleTimes;
        this.scales = scales;
        this.buckets = buckets;
        this.events = events;
        Constraints = constraints;
    }

    /// <summary>What the clip is called.</summary>
    public string Name { get; }

    /// <summary>How long it plays for, in seconds. Never zero — a zero-length clip is one frame long.</summary>
    public float Duration { get; }

    /// <summary>The skeleton it was baked against, and the only one it may be sampled onto.</summary>
    public Skeleton Skeleton { get; }

    /// <summary>Which joint carries the character's motion through the world, or −1 if none does.</summary>
    public int RootJoint { get; }

    /// <summary>How many joints it actually drives.</summary>
    public int TrackCount => tracks.Length;

    /// <summary>
    ///     How many of the imported channels named a joint this skeleton does not have.
    /// </summary>
    /// <remarks>
    ///     Reported rather than thrown, because a clip authored on a rig with fingers being played
    ///     on a rig without them is a normal thing to do and the answer is to drop the fingers. It
    ///     is also what a clip retargeted to the wrong skeleton looks like, and an editor that shows
    ///     the count is how somebody notices.
    /// </remarks>
    public int UnresolvedChannels { get; }

    /// <summary>The events authored on it, ordered by time.</summary>
    public ReadOnlySpan<AnimationEvent> Events => events;

    /// <summary>The constraints authored on it, or <see langword="null" /> if it carries none.</summary>
    /// <remarks>
    ///     ⚠ <b>Shared, like the clip itself.</b> Every character playing this clip reads the same
    ///     goal objects, so nothing per-character may be written to one — which is why a residual
    ///     belongs to <see cref="ConstraintStack" /> and not to a goal.
    /// </remarks>
    public ConstraintTrack? Constraints { get; }

    /// <summary>The blend shapes this clip drives, by name, in the order it stores them.</summary>
    /// <remarks>
    ///     ⚠ <b>Membership is the fact, not the value.</b> A shape is in here because the clip carries
    ///     a curve for it, and a curve that is flat at zero is still a curve — it is what returns a
    ///     face to rest and holds it there against a layer underneath. A shape that is <em>not</em> in
    ///     here is one this clip says nothing about, and the two must not be confused: the first
    ///     overwrites, the second leaves alone.
    /// </remarks>
    public ReadOnlySpan<string> Shapes => shapes;

    /// <summary>How many blend shapes it drives.</summary>
    public int ShapeCount => shapes.Length;

    /// <summary>Where a shape's weight is at a moment in the clip.</summary>
    /// <param name="time">When, in seconds. Clamped to the clip.</param>
    /// <param name="destination">One weight per <see cref="Shapes" /> entry, in that order.</param>
    /// <exception cref="ArgumentException">The destination is shorter than <see cref="ShapeCount" />.</exception>
    /// <remarks>
    ///     Separate from <see cref="Sample" /> rather than folded into it, because the two have
    ///     different destinations and different lifetimes: a pose is per skeleton and is rebuilt every
    ///     frame from the bind pose, and weights are per <em>mesh</em> and are accumulated across the
    ///     clips a blend is mixing. Folding them would make every caller that wants a pose supply a
    ///     weight buffer it has no use for.
    /// </remarks>
    public void SampleWeights(float time, Span<float> destination) {
        if (destination.Length < shapes.Length) {
            throw new ArgumentException(
                $"The clip drives {shapes.Length} blend shape(s) and the destination holds "
                + $"{destination.Length}.",
                nameof(destination)
            );
        }

        var t = MathUtil.Clamp(time, 0f, Duration);

        for (var index = 0; index < weightTracks.Length; index++) {
            destination[index] = SampleWeight(weightTracks[index], t);
        }
    }

    /// <summary>Where one named shape's weight is at a moment in the clip.</summary>
    /// <param name="time">When, in seconds. Clamped to the clip.</param>
    /// <param name="shape">The shape's name, as the mesh calls it.</param>
    /// <param name="weight">Its weight, or zero when the clip does not drive it.</param>
    /// <returns>Whether the clip drives that shape at all.</returns>
    /// <remarks>
    ///     The return value is the part that matters. A caller that treated a false as a weight of
    ///     zero would push a face to rest every time it played a clip that says nothing about it,
    ///     which is the additive-layer case turned into an override by accident.
    /// </remarks>
    public bool TrySampleWeight(float time, string shape, out float weight) {
        ArgumentNullException.ThrowIfNull(shape);

        var index = IndexOfShape(shape);

        weight = index < 0 ? 0f : SampleWeight(weightTracks[index], MathUtil.Clamp(time, 0f, Duration));

        return index >= 0;
    }

    /// <summary>Where a shape sits in <see cref="Shapes" />, or −1.</summary>
    /// <param name="shape">The shape's name.</param>
    /// <returns>Its index, or −1 when the clip does not drive it.</returns>
    public int IndexOfShape(string shape) {
        ArgumentNullException.ThrowIfNull(shape);

        for (var index = 0; index < shapes.Length; index++) {
            if (string.Equals(shapes[index], shape, StringComparison.Ordinal)) {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Poses a skeleton at a moment in the clip.</summary>
    /// <param name="time">When, in seconds. Clamped to the clip; wrapping is the caller's business.</param>
    /// <param name="destination">One transform per joint of <see cref="Skeleton" />.</param>
    /// <remarks>
    ///     Every joint is written, not only the driven ones: a joint no channel touches gets the
    ///     skeleton's bind pose. The alternative — leaving it alone — would make a clip's output
    ///     depend on what happened to be in the buffer, which is exactly the kind of thing that
    ///     works until two clips with different track sets are blended.
    /// </remarks>
    public void Sample(float time, Span<BoneTransform> destination) {
        Skeleton.BindPose.CopyTo(destination);

        var t = MathUtil.Clamp(time, 0f, Duration);

        foreach (var track in tracks) {
            ref var bone = ref destination[track.Joint];

            if (track.PositionCount > 0) {
                bone.Translation = SamplePosition(track, t);
            }

            if (track.RotationCount > 0) {
                bone.Rotation = SampleRotation(track, t);
            }

            if (track.ScaleCount > 0) {
                bone.Scale = SampleScale(track, t);
            }
        }
    }

    /// <summary>Where the root joint is at a moment in the clip.</summary>
    /// <param name="time">When, in seconds.</param>
    /// <returns>The root joint's local transform, or the identity if the clip has no root joint.</returns>
    public BoneTransform SampleRoot(float time) {
        if (RootJoint < 0) {
            return BoneTransform.Identity;
        }

        var t = MathUtil.Clamp(time, 0f, Duration);
        var bone = Skeleton.BindPose[RootJoint];

        foreach (var track in tracks) {
            if (track.Joint != RootJoint) {
                continue;
            }

            if (track.PositionCount > 0) {
                bone.Translation = SamplePosition(track, t);
            }

            if (track.RotationCount > 0) {
                bone.Rotation = SampleRotation(track, t);
            }

            break;
        }

        return bone;
    }

    /// <summary>How far the root moved between two times, across any number of whole loops.</summary>
    /// <param name="from">The time playback was at.</param>
    /// <param name="to">The time it is at now.</param>
    /// <param name="loops">How many times the clip wrapped in between. Zero for an ordinary step.</param>
    /// <returns>The delta, in the character's own frame.</returns>
    /// <remarks>
    ///     The loop count is a parameter rather than something inferred from the two times, because
    ///     it cannot be inferred: <c>from = 0.9</c>, <c>to = 0.1</c> is one wrap at ordinary speed
    ///     and eleven at ten times speed, and the difference is ten strides of ground.
    /// </remarks>
    public RootMotionDelta ExtractRootMotion(float from, float to, int loops = 0) {
        if (RootJoint < 0) {
            return RootMotionDelta.None;
        }

        if (loops <= 0) {
            return RootMotionDelta.Between(SampleRoot(from), SampleRoot(to));
        }

        var start = SampleRoot(0f);
        var end = SampleRoot(Duration);

        // The tail of the pass playback was in, then whole passes, then the head of the one it is in
        // now. Each is measured in the frame the one before it ended in, which is what Chain expects.
        var delta = RootMotionDelta.Between(SampleRoot(from), end);
        var whole = RootMotionDelta.Between(start, end);

        for (var index = 1; index < loops; index++) {
            delta = RootMotionDelta.Chain(delta, whole);
        }

        return RootMotionDelta.Chain(delta, RootMotionDelta.Between(start, SampleRoot(to)));
    }

    /// <summary>Reports the events crossed by advancing from one time to another.</summary>
    /// <param name="from">The time playback was at, exclusive.</param>
    /// <param name="to">The time it is at now, inclusive.</param>
    /// <param name="loops">How many times the clip wrapped in between.</param>
    /// <param name="sink">Where the events go.</param>
    /// <param name="layer">Which layer to attribute them to.</param>
    /// <param name="state">Which state to attribute them to.</param>
    /// <param name="weight">How much that state was contributing.</param>
    /// <remarks>
    ///     <para>
    ///         Half-open on purpose. An event exactly at <paramref name="to" /> fires this frame and
    ///         one exactly at <paramref name="from" /> fired last frame, so no event fires twice and
    ///         none is skipped by a frame boundary landing on it.
    ///     </para>
    ///     <para>
    ///         A wrap emits the tail of the clip and then the head, in that order — and every event
    ///         in the clip once per whole loop in between, because a frame long enough to contain
    ///         three strides contains three footsteps whatever the frame rate says.
    ///     </para>
    /// </remarks>
    public void CollectEvents(
        float from,
        float to,
        int loops,
        AnimationEventBuffer sink,
        int layer,
        string state,
        float weight
    ) {
        ArgumentNullException.ThrowIfNull(sink);

        if (events.Length == 0) {
            return;
        }

        if (loops <= 0) {
            EmitRange(from, to, sink, layer, state, weight);
            return;
        }

        EmitRange(from, Duration, sink, layer, state, weight);

        for (var index = 1; index < loops; index++) {
            EmitRange(-1f, Duration, sink, layer, state, weight);
        }

        EmitRange(-1f, to, sink, layer, state, weight);
    }

    void EmitRange(float from, float to, AnimationEventBuffer sink, int layer, string state, float weight) {
        foreach (var authored in events) {
            if (authored.Time > from && authored.Time <= to) {
                sink.Add(new(authored, layer, state, weight));
            }
        }
    }

    /// <summary>Advances a playback time and reports how the clip's end was dealt with.</summary>
    /// <param name="time">Where playback was, in seconds.</param>
    /// <param name="delta">How far to move, which may be negative.</param>
    /// <param name="mode">What happens at the end.</param>
    /// <param name="loops">How many whole passes were crossed.</param>
    /// <returns>The new time, inside <c>[0, Duration]</c>.</returns>
    public float Advance(float time, float delta, WrapMode mode, out int loops) =>
        Advance(time, delta, mode, Duration, out loops);

    /// <summary>Advances a playback time over a given length.</summary>
    /// <param name="time">Where playback was.</param>
    /// <param name="delta">How far to move, which may be negative.</param>
    /// <param name="mode">What happens at the end.</param>
    /// <param name="length">The length being played over — a clip's duration, or a blend's.</param>
    /// <param name="loops">
    ///     How many whole passes were crossed. Non-zero only for <see cref="WrapMode.Loop" />.
    /// </param>
    /// <returns>The new time, inside <c>[0, length]</c>.</returns>
    /// <remarks>
    ///     <para>
    ///         A static over an explicit length as well as an instance method over the clip's own,
    ///         because a blend tree plays several clips of different lengths over one synchronised
    ///         length that belongs to none of them.
    ///     </para>
    ///     <para>
    ///         <b>Ping-pong reports no loops.</b> A pass backwards would have to fire the clip's
    ///         events in reverse order and produce root motion that undoes itself, and nothing in
    ///         this assembly models either. Time bounces correctly; a ping-pong clip with events on
    ///         it fires them only on the segment it is currently in, and a ping-pong clip is not a
    ///         thing to put root motion on.
    ///     </para>
    /// </remarks>
    public static float Advance(float time, float delta, WrapMode mode, float length, out int loops) {
        loops = 0;
        var advanced = time + delta;

        if (length <= 0f) {
            return 0f;
        }

        switch (mode) {
            case WrapMode.Loop: {
                var passes = MathF.Floor(advanced / length);
                loops = (int)MathF.Abs(passes);

                return MathUtil.Repeat(advanced, length);
            }

            case WrapMode.PingPong:
                return MathUtil.PingPong(advanced, length);

            default:
                return MathUtil.Clamp(advanced, 0f, length);
        }
    }

    /// <summary>Bakes an imported clip against a skeleton.</summary>
    /// <param name="data">The imported clip.</param>
    /// <param name="skeleton">The skeleton to resolve its channels against.</param>
    /// <param name="events">The events authored on it, or <see langword="null" />.</param>
    /// <param name="rootJoint">
    ///     Which joint carries the character through the world, or <see langword="null" /> to use
    ///     the skeleton's first root.
    /// </param>
    /// <param name="constraints">
    ///     The goals authored on it, or <see langword="null" /> if it carries none.
    /// </param>
    /// <returns>The runtime clip.</returns>
    public static AnimationClip Create(
        AnimationClipData data,
        Skeleton skeleton,
        IEnumerable<AnimationEvent>? events = null,
        string? rootJoint = null,
        ConstraintTrack? constraints = null
    ) {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(skeleton);

        var built = new List<Track>(data.Channels.Length);
        var positionTimes = new List<float>();
        var positions = new List<Vector3>();
        var rotationTimes = new List<float>();
        var rotations = new List<PackedQuaternion>();
        var scaleTimes = new List<float>();
        var scales = new List<Vector3>();
        var shapes = new List<string>();
        var weightTracks = new List<WeightTrack>();
        var weightTimes = new List<float>();
        var weightValues = new List<float>();
        var unresolved = 0;
        var longest = 0f;

        foreach (var channel in data.Channels) {
            // Before the joint lookup, and this is the ordering that matters: a weight channel names
            // the morphed mesh's node, which is not a joint and is not meant to be. Resolving it first
            // would put every facial curve in the model into UnresolvedChannels — the number somebody
            // watches to notice a clip playing on the wrong rig — and drown the signal it exists for.
            var weightCount = Math.Min(channel.WeightTimes.Length, channel.Weights.Length);
            var named = channel.Shape.Length > 0;

            if (weightCount > 0 && named && !shapes.Contains(channel.Shape, StringComparer.Ordinal)) {
                // Duplicates fold into the first: two channels for one shape is an exporter writing a
                // curve twice, and taking the later one silently would make which of them wins depend
                // on channel order. The first is the one an author would find in the file.
                shapes.Add(channel.Shape);
                weightTracks.Add(new(weightTimes.Count, weightCount, -1));
                weightTimes.AddRange(channel.WeightTimes.AsSpan(0, weightCount));
                weightValues.AddRange(channel.Weights.AsSpan(0, weightCount));
                longest = MathF.Max(longest, Last(channel.WeightTimes, weightCount));
            }

            var joint = skeleton.IndexOf(channel.Target);

            if (joint < 0) {
                // A weight channel that resolved is not unresolved. Anything else that named a joint
                // this rig does not have is, whether or not it carried keys — which is the count's
                // existing meaning and is left exactly as it was.
                if (weightCount == 0 || !named) {
                    unresolved++;
                }

                continue;
            }

            var positionCount = Math.Min(channel.PositionTimes.Length, channel.Positions.Length);
            var rotationCount = Math.Min(channel.RotationTimes.Length, channel.Rotations.Length);
            var scaleCount = Math.Min(channel.ScaleTimes.Length, channel.Scales.Length);

            if (positionCount == 0 && rotationCount == 0 && scaleCount == 0) {
                continue;
            }

            // The bucket tables cannot be built here: they are cut into slices of the clip's
            // duration, and an exporter that left the duration at zero means the duration is not
            // known until every channel has been walked. They are filled in below.
            var track = new Track(
                joint,
                positionTimes.Count,
                positionCount,
                -1,
                rotationTimes.Count,
                rotationCount,
                -1,
                scaleTimes.Count,
                scaleCount,
                -1
            );

            positionTimes.AddRange(channel.PositionTimes.AsSpan(0, positionCount));
            positions.AddRange(channel.Positions.AsSpan(0, positionCount));
            rotationTimes.AddRange(channel.RotationTimes.AsSpan(0, rotationCount));
            rotations.AddRange(PackedQuaternion.Pack(channel.Rotations.AsSpan(0, rotationCount)));
            scaleTimes.AddRange(channel.ScaleTimes.AsSpan(0, scaleCount));
            scales.AddRange(channel.Scales.AsSpan(0, scaleCount));

            built.Add(track);

            longest = MathF.Max(longest, Last(channel.PositionTimes, positionCount));
            longest = MathF.Max(longest, Last(channel.RotationTimes, rotationCount));
            longest = MathF.Max(longest, Last(channel.ScaleTimes, scaleCount));
        }

        AnimationEvent[] authored = events is null ? [] : [.. events.OrderBy(e => e.Time)];

        // An exporter that leaves the duration at zero is common enough to be worth handling, and
        // the last key is the only defensible answer. A genuinely zero-length clip stays one frame
        // long rather than becoming a division by zero in every caller that normalises time.
        var duration = data.Duration > 0f ? data.Duration : longest;
        duration = duration > 0f ? duration : 1f / 60f;

        var index = new List<int>();
        var times = CollectionsMarshal.AsSpan(positionTimes);
        var rotationKeys = CollectionsMarshal.AsSpan(rotationTimes);
        var scaleKeys = CollectionsMarshal.AsSpan(scaleTimes);

        for (var track = 0; track < built.Count; track++) {
            var current = built[track];

            built[track] = current with {
                PositionIndex = BuildIndex(
                    index,
                    times.Slice(current.PositionStart, current.PositionCount),
                    duration
                ),
                RotationIndex = BuildIndex(
                    index,
                    rotationKeys.Slice(current.RotationStart, current.RotationCount),
                    duration
                ),
                ScaleIndex = BuildIndex(index, scaleKeys.Slice(current.ScaleStart, current.ScaleCount), duration)
            };
        }

        var weightKeys = CollectionsMarshal.AsSpan(weightTimes);

        for (var track = 0; track < weightTracks.Count; track++) {
            var current = weightTracks[track];

            weightTracks[track] = current with {
                Index = BuildIndex(index, weightKeys.Slice(current.Start, current.Count), duration)
            };
        }

        return new(
            data.Name,
            duration,
            skeleton,
            [.. built],
            [.. positionTimes],
            [.. positions],
            [.. rotationTimes],
            [.. rotations],
            [.. scaleTimes],
            [.. scales],
            [.. index],
            authored,
            ResolveRootJoint(skeleton, rootJoint),
            unresolved,
            constraints,
            new([.. shapes], [.. weightTracks], [.. weightTimes], [.. weightValues])
        );
    }

    static int ResolveRootJoint(Skeleton skeleton, string? rootJoint) {
        if (rootJoint is not null) {
            return skeleton.IndexOf(rootJoint);
        }

        var parents = skeleton.Parents;

        for (var index = 0; index < parents.Length; index++) {
            if (parents[index] < 0) {
                return index;
            }
        }

        return -1;
    }

    static float Last(float[] times, int count) => count > 0 ? times[count - 1] : 0f;

    Vector3 SamplePosition(in Track track, float time) {
        var times = positionTimes.AsSpan(track.PositionStart, track.PositionCount);
        var values = positions.AsSpan(track.PositionStart, track.PositionCount);
        var index = FindKey(times, Index(track.PositionIndex, track.PositionCount), time, out var t);

        return index + 1 < values.Length ? Vector3.Lerp(values[index], values[index + 1], t) : values[index];
    }

    Vector3 SampleScale(in Track track, float time) {
        var times = scaleTimes.AsSpan(track.ScaleStart, track.ScaleCount);
        var values = scales.AsSpan(track.ScaleStart, track.ScaleCount);
        var index = FindKey(times, Index(track.ScaleIndex, track.ScaleCount), time, out var t);

        return index + 1 < values.Length ? Vector3.Lerp(values[index], values[index + 1], t) : values[index];
    }

    /// <summary>One weight track at a time, on the vector tracks' terms.</summary>
    /// <remarks>
    ///     ⚠ <b>Lerped and never clamped, and the two go together.</b> A corrective authored past one
    ///     and a shape authored as the negative of its neighbour are both real, so saturating here
    ///     would be this method overruling the author — and a clamp would also make the interpolation
    ///     non-linear exactly where an animator put the overshoot they wanted to see.
    /// </remarks>
    float SampleWeight(in WeightTrack track, float time) {
        var times = weightTimes.AsSpan(track.Start, track.Count);
        var values = weightValues.AsSpan(track.Start, track.Count);
        var index = FindKey(times, Index(track.Index, track.Count), time, out var t);

        return index + 1 < values.Length
            ? values[index] + ((values[index + 1] - values[index]) * t)
            : values[index];
    }

    Quaternion SampleRotation(in Track track, float time) {
        var times = rotationTimes.AsSpan(track.RotationStart, track.RotationCount);
        var values = rotations.AsSpan(track.RotationStart, track.RotationCount);
        var index = FindKey(times, Index(track.RotationIndex, track.RotationCount), time, out var t);

        return index + 1 < values.Length
            ? Quaternion.Nlerp(values[index].Unpack(), values[index + 1].Unpack(), t)
            : values[index].Unpack();
    }

    ReadOnlySpan<int> Index(int start, int count) => start < 0 ? [] : buckets.AsSpan(start, count);

    /// <summary>The key at or before a time, and how far past it the time is.</summary>
    /// <param name="times">The track's key times.</param>
    /// <param name="index">
    ///     The track's bucket table, one entry per key over the clip's duration, or empty for a track
    ///     short enough to search.
    /// </param>
    /// <param name="time">The sample time, already clamped into the clip.</param>
    /// <param name="fraction">How far from the key found to the next one, in <c>[0, 1)</c>.</param>
    int FindKey(ReadOnlySpan<float> times, ReadOnlySpan<int> index, float time, out float fraction) {
        fraction = 0f;

        if (times.Length <= 1 || time <= times[0]) {
            return 0;
        }

        if (time >= times[^1]) {
            return times.Length - 1;
        }

        var low = index.IsEmpty ? Search(times, time) : Lookup(times, index, time);
        var span = times[low + 1] - times[low];
        fraction = span > 0f ? (time - times[low]) / span : 0f;

        return low;
    }

    /// <summary>The indexed lookup: one multiply, one read, and an advance that is usually nothing.</summary>
    int Lookup(ReadOnlySpan<float> times, ReadOnlySpan<int> index, float time) {
        var slice = MathUtil.Clamp((int)(time / Duration * index.Length), 0, index.Length - 1);
        var key = index[slice];

        // The entry is the key at or before the slice's *start*, and the sample is at or after it, so
        // walking forward is the only correction that can be needed. One step on average, because
        // there is one slice per key.
        while (key + 1 < times.Length && times[key + 1] <= time) {
            key++;
        }

        return key;
    }

    /// <summary>The fallback for tracks too short to be worth indexing.</summary>
    static int Search(ReadOnlySpan<float> times, float time) {
        // The largest index whose time is not past the sample. `low` ends one past it, because the
        // loop only moves `low` when the midpoint is still behind.
        var low = 0;
        var high = times.Length - 1;

        while (low < high) {
            var middle = (low + high + 1) / 2;

            if (times[middle] <= time) {
                low = middle;
            } else {
                high = middle - 1;
            }
        }

        return low;
    }

    /// <summary>
    ///     Builds one track's bucket table: for each of <c>count</c> uniform slices of the clip, the
    ///     last key at or before the slice's start.
    /// </summary>
    /// <returns>Where the table starts in the shared array, or −1 for a track that is not indexed.</returns>
    static int BuildIndex(List<int> destination, ReadOnlySpan<float> times, float duration) {
        if (times.Length <= IndexThreshold || duration <= 0f) {
            return -1;
        }

        var start = destination.Count;
        var key = 0;

        // The key pointer only moves forward, so building the whole table is one pass over the keys
        // rather than a search per slice.
        for (var slice = 0; slice < times.Length; slice++) {
            var at = slice / (float)times.Length * duration;

            while (key + 1 < times.Length && times[key + 1] <= at) {
                key++;
            }

            destination.Add(key);
        }

        return start;
    }

    /// <summary>One shape's scalar track: where its keys are and its bucket table.</summary>
    readonly record struct WeightTrack(int Start, int Count, int Index);

    /// <summary>
    ///     The weight side of a bake, bundled so the constructor does not take four more parameters.
    /// </summary>
    /// <param name="Shapes">The shapes driven, in storage order.</param>
    /// <param name="Tracks">One track per shape, in the same order.</param>
    /// <param name="Times">Every track's key times, concatenated.</param>
    /// <param name="Values">Every track's weights, concatenated.</param>
    readonly record struct WeightTracks(string[] Shapes, WeightTrack[] Tracks, float[] Times, float[] Values);

    readonly record struct Track(
        int Joint,
        int PositionStart,
        int PositionCount,
        int PositionIndex,
        int RotationStart,
        int RotationCount,
        int RotationIndex,
        int ScaleStart,
        int ScaleCount,
        int ScaleIndex
    );
}
