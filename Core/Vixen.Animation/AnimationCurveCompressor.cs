// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering;

namespace Vixen.Animation;

/// <summary>How far a compressed curve may stray from the one it replaces.</summary>
/// <param name="Position">In metres. A tenth of a millimetre by default.</param>
/// <param name="Rotation">
///     In radians. A twentieth of a degree by default, which at the end of a one-metre limb is under
///     a millimetre.
/// </param>
/// <param name="Scale">As a ratio. A ten-thousandth by default.</param>
/// <remarks>
///     Three tolerances rather than one, because the three tracks are not in the same units and a
///     single number would have to be wrong for two of them. The rotation one is the one that
///     matters: it is what decides how much of a hundred-joint character's data survives, and the
///     error it admits is amplified by the length of the chain below the joint — which is why the
///     default is conservative and a shoulder deserves a tighter one than a finger.
/// </remarks>
public readonly record struct CurveCompressionSettings(
    float Position = 1e-4f,
    float Rotation = 1e-3f,
    float Scale = 1e-4f
) {
    /// <summary>The defaults: visually lossless on the rigs this was measured against.</summary>
    /// <remarks>
    ///     Spelled out rather than written <c>new()</c>, and the difference is not cosmetic: a
    ///     positional <c>record struct</c>'s parameterless constructor zeroes its fields instead of
    ///     applying the parameter defaults, so <c>new()</c> and <c>default</c> both mean a tolerance
    ///     of zero — a compressor that removes only the keys whose error is bit-exact. Use this, not
    ///     <c>default</c>.
    /// </remarks>
    public static CurveCompressionSettings Default => new(1e-4f, 1e-3f, 1e-4f);

    /// <summary>Tolerances loose enough to be worth a screenshot before shipping.</summary>
    /// <remarks>
    ///     A millimetre and half a degree. Roughly four times the reduction of
    ///     <see cref="Default" /> on a typical locomotion clip, and the point at which a slow pan
    ///     across a hand starts to show it.
    /// </remarks>
    public static CurveCompressionSettings Aggressive => new(1e-3f, 8.7e-3f, 1e-3f);
}

/// <summary>What a compression pass removed.</summary>
/// <param name="KeysBefore">How many keys went in.</param>
/// <param name="KeysAfter">How many came out.</param>
/// <param name="ChannelsBefore">How many channels went in.</param>
/// <param name="ChannelsAfter">How many came out — a channel that no longer moves is dropped.</param>
public readonly record struct CurveCompressionReport(
    int KeysBefore,
    int KeysAfter,
    int ChannelsBefore,
    int ChannelsAfter
) {
    /// <summary>How much of the original is left, as a fraction.</summary>
    public float Ratio => KeysBefore > 0 ? (float)KeysAfter / KeysBefore : 1f;
}

/// <summary>
///     Removes the keys a linear sampler would have produced anyway.
/// </summary>
/// <remarks>
///     <para>
///         An exporter emits a key per frame per channel whether or not anything changed, so a
///         thirty-second idle is nine hundred keys per track saying the same thing, and a limb that
///         moves in a straight line for a second is thirty keys on a line through two of them. This
///         pass keeps a key only where dropping it would move the sampled curve further than the
///         caller is willing to accept.
///     </para>
///     <para>
///         <b>Greedy, and it fits against the anchor rather than against the neighbours.</b> Starting
///         from a kept key, the span is extended one key at a time and <em>every</em> key inside it
///         is re-checked against the straight line from the anchor to the candidate. Checking only
///         the key being dropped is the version everybody writes first, and it lets error accumulate:
///         each key is within tolerance of its own neighbours and the hundredth is a long way from
///         where it started.
///     </para>
///     <para>
///         <b>It works on <see cref="AnimationClipData" />, not on a runtime clip.</b> This is a
///         content-build pass — doc 08's model compiler is where it belongs — and its output is the
///         same record a build already writes, so nothing downstream needs to know it ran. Running it
///         at load would spend a character's loading time recomputing an answer that never changes.
///     </para>
/// </remarks>
public static class AnimationCurveCompressor {
    /// <summary>Compresses every channel of a clip.</summary>
    /// <param name="data">The clip.</param>
    /// <param name="settings">How far the result may stray.</param>
    /// <param name="report">What was removed.</param>
    /// <returns>A new clip. The input is not modified.</returns>
    public static AnimationClipData Compress(
        AnimationClipData data,
        CurveCompressionSettings settings,
        out CurveCompressionReport report
    ) {
        ArgumentNullException.ThrowIfNull(data);

        var channels = new List<AnimationChannel>(data.Channels.Length);
        var before = 0;
        var after = 0;

        foreach (var channel in data.Channels) {
            var positions = Reduce(
                channel.PositionTimes,
                channel.Positions,
                settings.Position,
                Vector3.Lerp,
                static (a, b) => (a - b).Length()
            );

            var rotations = Reduce(
                channel.RotationTimes,
                channel.Rotations,
                settings.Rotation,
                Quaternion.Nlerp,
                AngleBetween
            );

            var scales = Reduce(
                channel.ScaleTimes,
                channel.Scales,
                settings.Scale,
                Vector3.Lerp,
                static (a, b) => (a - b).Length()
            );

            before += channel.PositionTimes.Length + channel.RotationTimes.Length + channel.ScaleTimes.Length;
            after += positions.Times.Length + rotations.Times.Length + scales.Times.Length;

            if (positions.Times.Length + rotations.Times.Length + scales.Times.Length == 0) {
                // Every track collapsed to nothing, which happens to a channel an exporter emitted
                // for a joint that never moves. Dropping it saves the runtime a track to visit.
                continue;
            }

            channels.Add(
                new() {
                    Target = channel.Target,
                    PositionTimes = positions.Times,
                    Positions = positions.Values,
                    RotationTimes = rotations.Times,
                    Rotations = rotations.Values,
                    ScaleTimes = scales.Times,
                    Scales = scales.Values
                }
            );
        }

        report = new(before, after, data.Channels.Length, channels.Count);

        return new() {
            Name = data.Name,
            Duration = data.Duration,
            Channels = [.. channels]
        };
    }

    /// <summary>Compresses with the default tolerances.</summary>
    /// <param name="data">The clip.</param>
    /// <returns>A new clip.</returns>
    public static AnimationClipData Compress(AnimationClipData data) =>
        Compress(data, CurveCompressionSettings.Default, out _);

    static (float[] Times, T[] Values) Reduce<T>(
        float[] times,
        T[] values,
        float tolerance,
        Func<T, T, float, T> interpolate,
        Func<T, T, float> distance
    ) {
        var count = Math.Min(times.Length, values.Length);

        if (count == 0) {
            return ([], []);
        }

        if (count == 1) {
            return ([times[0]], [values[0]]);
        }

        // A track that never leaves its first value is one key. The sampler clamps outside the last
        // key, so one is enough to mean "this, for the whole clip".
        var constant = true;

        for (var index = 1; index < count && constant; index++) {
            constant = distance(values[index], values[0]) <= tolerance;
        }

        if (constant) {
            return ([times[0]], [values[0]]);
        }

        var keptTimes = new List<float>(count) { times[0] };
        var keptValues = new List<T>(count) { values[0] };
        var anchor = 0;

        for (var candidate = 2; candidate < count; candidate++) {
            if (Fits(anchor, candidate)) {
                continue;
            }

            // The span from the anchor to here no longer fits, so the key before it is the last one
            // that did and becomes the next anchor.
            anchor = candidate - 1;
            keptTimes.Add(times[anchor]);
            keptValues.Add(values[anchor]);
        }

        keptTimes.Add(times[count - 1]);
        keptValues.Add(values[count - 1]);

        return ([.. keptTimes], [.. keptValues]);

        bool Fits(int from, int to) {
            var span = times[to] - times[from];

            if (span <= 0f) {
                return false;
            }

            for (var index = from + 1; index < to; index++) {
                var amount = (times[index] - times[from]) / span;

                if (distance(interpolate(values[from], values[to], amount), values[index]) > tolerance) {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>The angle between two rotations, measured so that small angles survive.</summary>
    /// <remarks>
    ///     <c>2·acos(|dot|)</c> is the textbook form and it is useless here. Its derivative is
    ///     infinite at <c>dot = 1</c>, so for the near-identical rotations this pass spends all its
    ///     time comparing, a one-ulp error in the dot product comes out as a milliradian — and a
    ///     compressor whose error metric reads a thousandth of a radian for two identical
    ///     quaternions keeps every key it has. Going through the relative rotation and <c>atan2</c>
    ///     is exact at zero.
    /// </remarks>
    static float AngleBetween(Quaternion left, Quaternion right) {
        var relative = Quaternion.Concatenate(Quaternion.Conjugate(left), right);
        return 2f * MathF.Atan2(relative.Xyz.Length(), MathF.Abs(relative.W));
    }
}
