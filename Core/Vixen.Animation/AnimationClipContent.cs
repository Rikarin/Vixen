// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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
///         rig-independent and <see cref="Bake" /> is what pairs the two.
///     </para>
/// </remarks>
[DataContract("AnimationClipContent")]
public sealed class AnimationClipContent {
    /// <summary>The version this reader and writer speak.</summary>
    public const int Current = 1;

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

    /// <summary>Pairs the clip with a rig.</summary>
    /// <param name="skeleton">The skeleton to resolve its channels against.</param>
    /// <param name="rootJoint">
    ///     Which joint carries the character through the world, or <see langword="null" /> for the
    ///     skeleton's first root.
    /// </param>
    /// <returns>The runtime clip.</returns>
    public AnimationClip Bake(Skeleton skeleton, string? rootJoint = null) =>
        AnimationClip.Create(Data, skeleton, Events, rootJoint);

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
    ///         <see cref="Bake" /> requires a <see cref="Skeleton" /> to resolve channels into joint
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
