// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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
///         <b>Keys are found by binary search, and not by a cursor.</b> The usual optimisation is to
///         remember where the last sample landed, which is O(1) for forward playback. It cannot live
///         on the clip, which is shared; living on the player means one hint per track per clip per
///         instance, and a blend tree's set of active clips changes as its parameter moves — so the
///         bookkeeping is per-frame allocation or a cache with an eviction policy, to save five
///         comparisons per track. Measured against a thirty-key track it is not worth what it costs
///         to hold. It is worth revisiting for a clip with hundreds of keys per track, and the
///         README says so.
///     </para>
/// </remarks>
public sealed class AnimationClip {
    readonly Track[] tracks;
    readonly float[] positionTimes;
    readonly Vector3[] positions;
    readonly float[] rotationTimes;
    readonly Quaternion[] rotations;
    readonly float[] scaleTimes;
    readonly Vector3[] scales;
    readonly AnimationEvent[] events;

    AnimationClip(
        string name,
        float duration,
        Skeleton skeleton,
        Track[] tracks,
        float[] positionTimes,
        Vector3[] positions,
        float[] rotationTimes,
        Quaternion[] rotations,
        float[] scaleTimes,
        Vector3[] scales,
        AnimationEvent[] events,
        int rootJoint,
        int unresolvedChannels
    ) {
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
        this.events = events;
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
                bone.Translation = SampleVector(
                    positionTimes.AsSpan(track.PositionStart, track.PositionCount),
                    positions.AsSpan(track.PositionStart, track.PositionCount),
                    t
                );
            }

            if (track.RotationCount > 0) {
                bone.Rotation = SampleRotation(
                    rotationTimes.AsSpan(track.RotationStart, track.RotationCount),
                    rotations.AsSpan(track.RotationStart, track.RotationCount),
                    t
                );
            }

            if (track.ScaleCount > 0) {
                bone.Scale = SampleVector(
                    scaleTimes.AsSpan(track.ScaleStart, track.ScaleCount),
                    scales.AsSpan(track.ScaleStart, track.ScaleCount),
                    t
                );
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
                bone.Translation = SampleVector(
                    positionTimes.AsSpan(track.PositionStart, track.PositionCount),
                    positions.AsSpan(track.PositionStart, track.PositionCount),
                    t
                );
            }

            if (track.RotationCount > 0) {
                bone.Rotation = SampleRotation(
                    rotationTimes.AsSpan(track.RotationStart, track.RotationCount),
                    rotations.AsSpan(track.RotationStart, track.RotationCount),
                    t
                );
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
    /// <returns>The runtime clip.</returns>
    public static AnimationClip Create(
        AnimationClipData data,
        Skeleton skeleton,
        IEnumerable<AnimationEvent>? events = null,
        string? rootJoint = null
    ) {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(skeleton);

        var built = new List<Track>(data.Channels.Length);
        var positionTimes = new List<float>();
        var positions = new List<Vector3>();
        var rotationTimes = new List<float>();
        var rotations = new List<Quaternion>();
        var scaleTimes = new List<float>();
        var scales = new List<Vector3>();
        var unresolved = 0;
        var longest = 0f;

        foreach (var channel in data.Channels) {
            var joint = skeleton.IndexOf(channel.Target);

            if (joint < 0) {
                unresolved++;
                continue;
            }

            var positionCount = Math.Min(channel.PositionTimes.Length, channel.Positions.Length);
            var rotationCount = Math.Min(channel.RotationTimes.Length, channel.Rotations.Length);
            var scaleCount = Math.Min(channel.ScaleTimes.Length, channel.Scales.Length);

            if (positionCount == 0 && rotationCount == 0 && scaleCount == 0) {
                continue;
            }

            var track = new Track(
                joint,
                positionTimes.Count,
                positionCount,
                rotationTimes.Count,
                rotationCount,
                scaleTimes.Count,
                scaleCount
            );

            positionTimes.AddRange(channel.PositionTimes.AsSpan(0, positionCount));
            positions.AddRange(channel.Positions.AsSpan(0, positionCount));
            rotationTimes.AddRange(channel.RotationTimes.AsSpan(0, rotationCount));
            rotations.AddRange(channel.Rotations.AsSpan(0, rotationCount));
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

        return new(
            data.Name,
            duration > 0f ? duration : 1f / 60f,
            skeleton,
            built.ToArray(),
            positionTimes.ToArray(),
            positions.ToArray(),
            rotationTimes.ToArray(),
            rotations.ToArray(),
            scaleTimes.ToArray(),
            scales.ToArray(),
            authored,
            ResolveRootJoint(skeleton, rootJoint),
            unresolved
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

    static Vector3 SampleVector(ReadOnlySpan<float> times, ReadOnlySpan<Vector3> values, float time) {
        var index = FindKey(times, time, out var t);
        return index + 1 < values.Length ? Vector3.Lerp(values[index], values[index + 1], t) : values[index];
    }

    static Quaternion SampleRotation(ReadOnlySpan<float> times, ReadOnlySpan<Quaternion> values, float time) {
        var index = FindKey(times, time, out var t);

        return index + 1 < values.Length
            ? Quaternion.Nlerp(values[index], values[index + 1], t)
            : values[index];
    }

    /// <summary>The key at or before a time, and how far past it the time is.</summary>
    static int FindKey(ReadOnlySpan<float> times, float time, out float fraction) {
        fraction = 0f;

        if (times.Length <= 1 || time <= times[0]) {
            return 0;
        }

        if (time >= times[^1]) {
            return times.Length - 1;
        }

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

        var span = times[low + 1] - times[low];
        fraction = span > 0f ? (time - times[low]) / span : 0f;

        return low;
    }

    readonly record struct Track(
        int Joint,
        int PositionStart,
        int PositionCount,
        int RotationStart,
        int RotationCount,
        int ScaleStart,
        int ScaleCount
    );
}
