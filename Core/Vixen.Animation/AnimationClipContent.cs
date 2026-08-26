// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Constraints;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Rendering;

namespace Vixen.Animation;

/// <summary>A compiled <c>.vxanim</c>: what a build loads and what a skeleton is paired with.</summary>
/// <remarks>
///     <para>
///         <b>The compiled form, not the authored one.</b> An author writes curves with tangents;
///         this holds the sampled channels those curves bake down to, and the bake happens once in
///         the asset pipeline rather than every time a game loads a clip. The authored type lives in
///         <c>Vixen.Editor.Assets</c> and a runtime never meets it — which is also what keeps the
///         YAML parser out of this assembly, the same line <c>Vixen.Rendering</c> draws.
///     </para>
///     <para>
///         <b>Why this exists rather than writing <see cref="AnimationClipData" /> directly.</b> A
///         clip is three things a player needs and <see cref="AnimationClipData" /> is one of them:
///         the channels, the events authored beside them, and what happens at the end. Writing the
///         channels alone would mean a game that loaded a clip by address still had to find its
///         events somewhere else, which is the gap this row was opened to close.
///     </para>
///     <para>
///         ⚠ <b>No skeleton, and that is deliberate.</b> <see cref="AnimationClip.Create" /> resolves
///         channels against joints by name, and a clip does not know which rig it will be played on —
///         the same walk plays on every character that has the joints it names. So the artefact stays
///         rig-independent and <see cref="Bake(Skeleton, string?)" /> is what pairs the two.
///     </para>
/// </remarks>
[DataContract("AnimationClipContent")]
public sealed class AnimationClipContent {
    /// <summary>The version this reader and writer speak.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>2 since the scalar weight track landed</b> —
    ///         <c>AnimationChannel.Shape</c>/<c>WeightTimes</c>/<c>Weights</c>, which is what lets a
    ///         clip drive a blend shape.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The bump is a re-import trigger, not a compatibility fence, and the difference is
    ///         worth being exact about.</b> The generated serializer writes a member count and refuses
    ///         only <c>count &gt; MemberCount</c> — so <em>appended</em> members read back as their
    ///         defaults out of older bytes, and a version 1 artefact answers "no weight track", which
    ///         is the true answer. Nothing would break without the bump. What would happen is nothing:
    ///         the curves were dropped at import, so only going back to the source file recovers them,
    ///         and <c>AnimationClipImporter.Version</c> is this number. The cost is one content build
    ///         over the project and no runtime change at all.
    ///     </para>
    /// </remarks>
    public const int Current = 2;

    /// <summary>Which version of the format this artefact is.</summary>
    public int Version { get; set; } = Current;

    /// <summary>What the clip is called.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What happens when it runs past the end.</summary>
    /// <remarks>
    ///     Carried rather than applied. <see cref="AnimationClip" /> takes a wrap mode per call to
    ///     <see cref="AnimationClip.Advance(float, float, WrapMode, out int)" /> because a state
    ///     machine may legitimately play a looping clip once; this is what the clip was authored to
    ///     do, for whoever has no opinion of their own.
    /// </remarks>
    public WrapMode Wrap { get; set; } = WrapMode.Loop;

    /// <summary>The sampled channels.</summary>
    public AnimationClipData Data { get; set; } = new();

    /// <summary>What it raises, in time order.</summary>
    public AnimationEvent[] Events { get; set; } = [];

    /// <summary>Metadata this build did not interpret, by kind, as the YAML it was written in.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The open half of the format.</b> A clip carries more than curves — constraint
    ///         markup, gameplay tags, whatever a project needs on a timeline — and the alternative to
    ///         a reserved block is a schema that has to grow an official field per idea, with a
    ///         version bump and a flag day each time.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Text rather than a parsed tree, because this assembly has no parser.</b> A
    ///         consumer that understands a kind parses its own block; one that does not carries it
    ///         and says nothing. That is the contract: <b>unrecognised is preserved, never dropped</b>,
    ///         at both ends of the pipeline.
    ///     </para>
    /// </remarks>
    public Dictionary<string, string> Extensions { get; set; } = [];

    /// <summary>The constraints authored on it, in the order they were placed.</summary>
    /// <remarks>
    ///     ⚠ <b>Records rather than resolved goals, because a joint is named here and indexed at
    ///     <see cref="Bake(Skeleton, string?)" />.</b> The same argument <c>AnimationClipData</c>'s channels make: an
    ///     index is a fact about the rig the clip was marked up against, and one that survives a
    ///     joint being inserted is worth more than one that loads a byte faster.
    /// </remarks>
    public List<ConstraintTagRecord> Constraints { get; set; } = [];

    /// <summary>Pairs the clip with a rig.</summary>
    /// <param name="skeleton">The skeleton to resolve its channels against.</param>
    /// <param name="rootJoint">
    ///     Which joint carries the character through the world, or <see langword="null" /> for the
    ///     skeleton's first root.
    /// </param>
    /// <returns>The runtime clip.</returns>
    public AnimationClip Bake(Skeleton skeleton, string? rootJoint = null) => Bake(skeleton, null, rootJoint);

    /// <summary>Pairs the clip with a rig, resolving its constraints against a project's ladder.</summary>
    /// <param name="skeleton">The rig it will be played on.</param>
    /// <param name="ladder">
    ///     The project's priority names, or <see langword="null" /> for the shipped ladder.
    /// </param>
    /// <param name="rootJoint">Which joint carries the character, or <see langword="null" />.</param>
    /// <param name="unresolved">
    ///     Where the names of the constraints this rig cannot carry go, or <see langword="null" /> to
    ///     drop them silently.
    /// </param>
    /// <returns>The runtime clip.</returns>
    public AnimationClip Bake(
        Skeleton skeleton,
        PriorityLadder? ladder,
        string? rootJoint = null,
        ICollection<string>? unresolved = null
    ) =>
        AnimationClip.Create(
            Data,
            skeleton,
            Events,
            rootJoint,
            ConstraintTagRecord.Bake(Constraints, skeleton, ladder ?? PriorityLadder.Default, unresolved)
        );

    /// <summary>One named target's transform at a time, without a rig.</summary>
    /// <param name="target">The name the clip animates, as authored.</param>
    /// <param name="time">When, in seconds. Clamped to the clip.</param>
    /// <param name="transform">The transform, or the identity when the target is not animated.</param>
    /// <returns>Whether the clip animates that target.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Because half of what this format is for has no skeleton.</b> A door, a camera move,
    ///         a UI wobble, a lever, a rig made of separate entities — the authored clip format exists
    ///         for hand-keyed things, and most hand-keyed things are not characters.
    ///         <see cref="Bake(Skeleton, string?)" /> requires a <see cref="Skeleton" /> to resolve channels into joint
    ///         indices, and demanding one from a door means inventing a skeleton for a door.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Linear between samples, where <see cref="AnimationClip" /> is not.</b> A baked
    ///         clip slerps rotations through a bucket table built over the duration; this walks the
    ///         channel and lerps, because building that table costs more than a caller sampling three
    ///         targets a frame will ever save. <b>The two agree wherever a key is</b> — which is every
    ///         time the author put one — and differ by the usual lerp-versus-slerp margin between
    ///         them. Anything that needs the baked path's fidelity should be taking the baked path.
    ///     </para>
    ///     <para>
    ///         A channel with no keys for a component leaves that component at its rest value, for
    ///         the reason the bake gives: not writing a scale is not the same as writing one.
    ///     </para>
    /// </remarks>
    public bool TrySample(string target, float time, out BoneTransform transform) {
        ArgumentNullException.ThrowIfNull(target);

        transform = BoneTransform.Identity;

        var channels = Data.Channels;

        if (channels is null) {
            return false;
        }

        foreach (var channel in channels) {
            if (!string.Equals(channel.Target, target, StringComparison.Ordinal)) {
                continue;
            }

            var at = Math.Clamp(time, 0f, Data.Duration > 0f ? Data.Duration : time);

            transform = new(
                Interpolate(channel.PositionTimes, channel.Positions, at, Vector3.Zero, Vector3.Lerp),
                Interpolate(channel.RotationTimes, channel.Rotations, at, Quaternion.Identity, Quaternion.Slerp),
                Interpolate(channel.ScaleTimes, channel.Scales, at, Vector3.One, Vector3.Lerp)
            );

            return true;
        }

        return false;
    }

    /// <summary>One named blend shape's weight at a time, without a rig.</summary>
    /// <param name="shape">The shape's name, as the mesh calls it.</param>
    /// <param name="time">When, in seconds. Clamped to the clip.</param>
    /// <param name="weight">Its weight, or zero when the clip does not drive it.</param>
    /// <returns>Whether the clip drives that shape at all.</returns>
    /// <remarks>
    ///     <para>
    ///         <b><see cref="TrySample" />'s sibling, and it exists for the same reason.</b> A morphed
    ///         mesh does not have to be a character: a hand-keyed head on a segmented rig, a machine
    ///         that flexes, a face that blinks on a prop. <see cref="Bake(Skeleton, string?)" />
    ///         resolves channels into joint indices and a morphed mesh's node is not a joint, so
    ///         demanding a <see cref="Skeleton" /> here means inventing one for something that has
    ///         none. A character with an <c>Animator</c> should take the baked path and let
    ///         <c>BlendShapeAnimationSystem</c> land the weights.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The return value is the fact, not the weight.</b> A caller that read a false as a
    ///         weight of zero would push a face to rest every time it played a clip that says nothing
    ///         about that shape — <c>AnimationClip.TrySampleWeight</c>'s own rule, and the difference
    ///         between an additive facial layer and an accidental override.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>By name, and the name is the shape's and not the channel's target.</b> A clip may
    ///         carry the same shape name under two nodes if a model has two morphed meshes; the first
    ///         match wins, which is <c>AnimationClip.Create</c>'s rule for a duplicate and is the one
    ///         an author would find in the file.
    ///     </para>
    /// </remarks>
    public bool TrySampleWeight(string shape, float time, out float weight) {
        ArgumentNullException.ThrowIfNull(shape);

        weight = 0f;

        if (Data.Channels is not { } channels) {
            return false;
        }

        foreach (var channel in channels) {
            // The length of the time array is what says a channel has a weight track — a weight of
            // zero is an authored value and a face at rest, so "no keys" and "a key at zero" are
            // different facts. AnimationChannel.WeightTimes says so itself.
            if (channel.WeightTimes is not { Length: > 0 }
                || !string.Equals(channel.Shape, shape, StringComparison.Ordinal)) {
                continue;
            }

            var at = Math.Clamp(time, 0f, Data.Duration > 0f ? Data.Duration : time);

            weight = Interpolate(channel.WeightTimes, channel.Weights, at, 0f, static (a, b, t) => a + ((b - a) * t));

            return true;
        }

        return false;
    }

    /// <summary>One component track at a time, held at both ends.</summary>
    /// <remarks>
    ///     Held rather than extrapolated, which is <see cref="AnimationClip" />'s rule and
    ///     <c>CurveEvaluation</c>'s: a track sampled past its last key returns that key.
    /// </remarks>
    static T Interpolate<T>(float[]? times, T[]? values, float time, T rest, Func<T, T, float, T> blend) {
        if (times is null || values is null) {
            return rest;
        }

        var count = Math.Min(times.Length, values.Length);

        if (count == 0) {
            return rest;
        }

        if (count == 1 || time <= times[0]) {
            return values[0];
        }

        if (time >= times[count - 1]) {
            return values[count - 1];
        }

        var index = 0;

        while (index < count - 2 && times[index + 1] <= time) {
            index++;
        }

        var span = times[index + 1] - times[index];

        return span <= 0f
            ? values[index + 1]
            : blend(values[index], values[index + 1], (time - times[index]) / span);
    }
}
