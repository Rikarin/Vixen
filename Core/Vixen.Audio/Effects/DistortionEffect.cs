// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Effects;

/// <summary>Which curve a distortion bends the signal along.</summary>
public enum DistortionCurve {
    /// <summary>A hyperbolic tangent: rounds the peaks off gradually, and never quite reaches the rail.</summary>
    /// <remarks>The warm one. Adds mostly odd harmonics and stays musical a long way past sensible drive.</remarks>
    SoftClip,

    /// <summary>Flat tops. What a clamp does, made deliberate.</summary>
    /// <remarks>
    ///     Harsh, buzzy, and the right answer for a blown speaker, a broken radio, or damage feedback.
    ///     It also aliases badly, which is part of why it sounds broken.
    /// </remarks>
    HardClip,

    /// <summary>A cubic curve that goes flat exactly at the rail. The classic overdrive shape.</summary>
    Overdrive,

    /// <summary>Past the rail the waveform folds back on itself instead of flattening.</summary>
    /// <remarks>
    ///     Sounds nothing like clipping — it turns loud into a metallic, ring-modulated mess, which
    ///     is what a sci-fi transmission or a possessed voice wants.
    /// </remarks>
    Foldback
}

/// <summary>Bends the waveform, which is what every kind of distortion is.</summary>
/// <remarks>
///     <para>
///         <b>Waveshaping and nothing else.</b> There is no filtering here and there deliberately is
///         not: the tone control every guitar amplifier puts around its distortion is an equaliser,
///         and this engine has one. Building a fixed tone stack into the distortion would give one
///         opinion about what distorted sound should be, where a
///         <see cref="EqualizerEffect" /> either side of it gives all of them.
///     </para>
///     <para>
///         <b><see cref="DriveDb" /> is the whole control.</b> Every curve here does nothing to a quiet
///         signal and progressively more as it approaches the rail, so the way to get more distortion
///         is to make the signal louder before it arrives — and then to turn the result back down,
///         which is what <see cref="OutputDb" /> is for. That is what a real amplifier does too.
///     </para>
///     <para>
///         <b>It aliases, and there is no oversampling.</b> Bending a waveform makes harmonics, and
///         harmonics above Nyquist fold back down as inharmonic tones that follow the pitch in the
///         wrong direction. Oversampling by four is the fix and costs four times the arithmetic plus
///         two filters; for a radio voice or an explosion nobody notices, and for a lead guitar
///         everybody does. It is owed, and this is not the effect to put a guitar through yet.
///     </para>
/// </remarks>
public sealed class DistortionEffect : IAudioEffect {
    /// <summary>Which curve.</summary>
    public DistortionCurve Curve { get; set; } = DistortionCurve.SoftClip;

    /// <summary>How hard the signal is pushed into the curve, in decibels.</summary>
    /// <remarks>Zero is no distortion at all for any curve here, because none of them bends below the rail.</remarks>
    public float DriveDb { get; set; } = 12f;

    /// <summary>A gain applied after the curve, in decibels, to put the level back.</summary>
    public float OutputDb { get; set; } = -6f;

    /// <summary>How much of the bent signal to keep, against the untouched one.</summary>
    /// <remarks>
    ///     Parallel distortion: a mix below one keeps the original's dynamics and adds the harmonics
    ///     on top, which is how a heavy sound stays intelligible.
    /// </remarks>
    public float Mix { get; set; } = 1f;

    /// <inheritdoc />
    public bool Enabled { get; set; } = true;

    /// <inheritdoc />
    public void Prepare(in AudioFormat format, int maxFrames) { }

    /// <inheritdoc />
    public void Process(Span<float> buffer, int frameCount, int channels) {
        if (!Enabled) {
            return;
        }

        var drive = Decibels.ToLinear(DriveDb);
        var output = Decibels.ToLinear(OutputDb);
        var mix = Math.Clamp(Mix, 0f, 1f);
        var samples = frameCount * channels;
        var curve = Curve;

        for (var i = 0; i < samples; i++) {
            var dry = buffer[i];
            var shaped = Shape(dry * drive, curve) * output;
            buffer[i] = dry + ((shaped - dry) * mix);
        }
    }

    /// <inheritdoc />
    public void Reset() { }

    /// <inheritdoc />
    public bool TrySetProperty(string name, float value) {
        switch (name) {
            case "DriveDb":
                DriveDb = value;
                return true;

            case "OutputDb":
                OutputDb = value;
                return true;

            case "Mix":
                Mix = value;
                return true;

            default:
                return false;
        }
    }

    /// <summary>Bends one sample.</summary>
    /// <param name="value">The sample, already driven.</param>
    /// <param name="curve">Which curve.</param>
    /// <returns>The bent sample.</returns>
    /// <remarks>
    ///     Public and static because a waveshaper is a pure function, and because it is the one part
    ///     of this effect worth testing directly — a curve is right or wrong at a handful of values
    ///     and no ear is needed to check.
    /// </remarks>
    public static float Shape(float value, DistortionCurve curve) {
        switch (curve) {
            case DistortionCurve.HardClip:
                return Math.Clamp(value, -1f, 1f);

            case DistortionCurve.Overdrive: {
                // 1.5x − 0.5x³: flat-topped at exactly ±1, with a slope of 1.5 through zero, which is
                // why it is louder than it went in before it is louder than the rail.
                var clamped = Math.Clamp(value, -1f, 1f);
                return (1.5f * clamped) - (0.5f * clamped * clamped * clamped);
            }

            case DistortionCurve.Foldback: {
                // Reflected about the rail, repeatedly, so an input of 3 comes out as −1 rather than
                // as 1. The loop terminates because each reflection halves the distance past it.
                var folded = value;

                while (MathF.Abs(folded) > 1f) {
                    folded = folded > 0f ? 2f - folded : -2f - folded;
                }

                return folded;
            }

            case DistortionCurve.SoftClip:
            default:
                return MathF.Tanh(value);
        }
    }
}
