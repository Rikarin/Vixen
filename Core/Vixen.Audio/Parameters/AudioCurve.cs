// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Parameters;

/// <summary>How a curve gets from one point to the next.</summary>
public enum AudioCurveInterpolation {
    /// <summary>Straight lines.</summary>
    /// <remarks>
    ///     The default. Every unit here is one a straight line is right in — decibels, semitones, and
    ///     a filter cutoff that is swept logarithmically by the filter and not by the curve.
    /// </remarks>
    Linear = 0,

    /// <summary>Holds each point's value until the next one is reached.</summary>
    /// <remarks>
    ///     For a parameter that is really a set of states wearing a number — a weapon's three tiers, a
    ///     surface type. Interpolating between two of those produces a value neither of them meant.
    /// </remarks>
    Step = 1,

    /// <summary>Straight lines, eased at both ends of each segment.</summary>
    /// <remarks>
    ///     Smoothstep. What a value that a player drives should usually use: a linear ramp has a
    ///     corner at each point, and a corner in a gain is audible as a change of direction even
    ///     though the gain itself never jumps.
    /// </remarks>
    Smooth = 2
}

/// <summary>One point on a curve.</summary>
/// <param name="Position">Where along the parameter's range, from 0 at its minimum to 1 at its maximum.</param>
/// <param name="Value">What the curve is worth there, in whatever unit the target reads.</param>
public readonly record struct AudioCurvePoint(float Position, float Value);

/// <summary>A parameter's value on one axis, mapped to something audible.</summary>
/// <remarks>
///     <para>
///         <b>Normalised on the way in and not on the way out.</b> A curve is asked about a position
///         from 0 to 1, so the same curve can be reused by parameters with different ranges and an
///         editor can draw it without knowing what it is attached to. What comes out is in the
///         target's own unit — decibels for a gain, semitones for a pitch, hertz for a cutoff —
///         because that is what makes a straight line the right default: a linear ramp in decibels
///         sounds linear, and a linear ramp in amplitude does not.
///     </para>
///     <para>
///         <b>Immutable, and evaluated on the game thread.</b> Nothing here is touched by the audio
///         thread: <c>AudioEngine.Update</c> evaluates every curve once a frame and leaves behind a
///         handful of floats, which is what the render loop reads. So a curve may be as elaborate as
///         an editor lets somebody draw without any of it landing in a device callback.
///     </para>
/// </remarks>
public sealed class AudioCurve {
    readonly AudioCurvePoint[] points;

    /// <summary>How it gets between its points.</summary>
    public AudioCurveInterpolation Interpolation { get; }

    /// <summary>Its points, in order.</summary>
    public ReadOnlySpan<AudioCurvePoint> Points => points;

    /// <summary>A curve through some points.</summary>
    /// <param name="points">
    ///     Its points. Sorted here rather than required sorted — the order they were drawn in is not
    ///     the author's mistake to fix, and an unsorted curve otherwise evaluates to nonsense in a way
    ///     nothing reports.
    /// </param>
    /// <param name="interpolation">How to get between them.</param>
    public AudioCurve(
        ReadOnlySpan<AudioCurvePoint> points,
        AudioCurveInterpolation interpolation = AudioCurveInterpolation.Linear
    ) {
        Interpolation = interpolation;
        this.points = points.ToArray();
        Array.Sort(this.points, static (a, b) => a.Position.CompareTo(b.Position));
    }

    /// <summary>A curve that is the same everywhere.</summary>
    /// <param name="value">What it is worth.</param>
    /// <returns>The curve.</returns>
    public static AudioCurve Constant(float value) => new([new(0f, value)]);

    /// <summary>A straight line across the whole range.</summary>
    /// <param name="from">Its value at the parameter's minimum.</param>
    /// <param name="to">Its value at the parameter's maximum.</param>
    /// <returns>The curve.</returns>
    public static AudioCurve Ramp(float from, float to) => new([new(0f, from), new(1f, to)]);

    /// <summary>What the curve is worth at a position.</summary>
    /// <param name="position">From 0 to 1. Outside that it is clamped.</param>
    /// <returns>The value, in the target's unit.</returns>
    /// <remarks>
    ///     <b>Flat outside its points rather than extrapolated.</b> A curve drawn from 0.2 to 0.8 holds
    ///     its end values beyond them; continuing the last segment's slope is how a gain curve reaches
    ///     +40 dB at a parameter value nobody drew.
    /// </remarks>
    public float Evaluate(float position) {
        if (points.Length == 0) {
            return 0f;
        }

        if (points.Length == 1 || position <= points[0].Position) {
            return points[0].Value;
        }

        var last = points.Length - 1;

        if (position >= points[last].Position) {
            return points[last].Value;
        }

        // A linear scan, because a curve has a handful of points and a binary search over four of
        // them is slower than looking at all four.
        var index = 0;

        while (index < last && points[index + 1].Position <= position) {
            index++;
        }

        var start = points[index];
        var end = points[index + 1];

        if (Interpolation is AudioCurveInterpolation.Step) {
            return start.Value;
        }

        var span = end.Position - start.Position;
        var t = span > 0f ? (position - start.Position) / span : 0f;

        if (Interpolation is AudioCurveInterpolation.Smooth) {
            t = t * t * (3f - (2f * t));
        }

        return start.Value + ((end.Value - start.Value) * t);
    }
}
