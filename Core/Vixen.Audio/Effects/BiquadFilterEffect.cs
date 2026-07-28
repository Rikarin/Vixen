// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Effects;

/// <summary>Which shape a <see cref="BiquadFilterEffect" /> takes.</summary>
public enum BiquadFilterKind {
    /// <summary>Passes below the cutoff, rolls off above it at 12 dB an octave.</summary>
    LowPass,

    /// <summary>Passes above the cutoff.</summary>
    HighPass,

    /// <summary>Passes a band around the frequency, with unity gain at the centre.</summary>
    BandPass,

    /// <summary>Removes a band around the frequency and passes everything else.</summary>
    Notch,

    /// <summary>Boosts or cuts a band, leaving the rest alone. The building block of an equaliser.</summary>
    Peaking,

    /// <summary>Boosts or cuts everything below the frequency.</summary>
    LowShelf,

    /// <summary>Boosts or cuts everything above the frequency.</summary>
    HighShelf
}

/// <summary>The five coefficients of a second-order section, already normalised.</summary>
/// <param name="B0">Feed-forward, current input.</param>
/// <param name="B1">Feed-forward, one sample back.</param>
/// <param name="B2">Feed-forward, two samples back.</param>
/// <param name="A1">Feedback, one sample back.</param>
/// <param name="A2">Feedback, two samples back.</param>
/// <remarks>
///     Public because a biquad is the one filter shape that turns up everywhere — an equaliser band,
///     a muffling low-pass behind a door, the anti-alias filter a resampler wants — and every one of
///     those wants the coefficients without also wanting a bus effect wrapped around them.
/// </remarks>
public readonly record struct BiquadCoefficients(float B0, float B1, float B2, float A1, float A2) {
    /// <summary>The filter that does nothing.</summary>
    public static BiquadCoefficients Identity => new(1f, 0f, 0f, 0f, 0f);

    /// <summary>Designs a section.</summary>
    /// <param name="kind">Which shape.</param>
    /// <param name="sampleRate">The rate it will run at.</param>
    /// <param name="frequency">The cutoff or centre frequency, in hertz.</param>
    /// <param name="q">
    ///     Resonance. <c>1/√2</c> ≈ 0.7071 is the flattest response a low-pass can have — the
    ///     Butterworth case — and larger values put a peak at the cutoff.
    /// </param>
    /// <param name="gainDb">
    ///     How much to boost or cut, for <see cref="BiquadFilterKind.Peaking" /> and the two
    ///     shelves. Ignored by the others.
    /// </param>
    /// <returns>The coefficients.</returns>
    /// <remarks>
    ///     Robert Bristow-Johnson's audio EQ cookbook, which is what every implementation of these
    ///     is; the formulae are reproduced rather than invented so that a filter designed here
    ///     matches one designed in a plug-in the sound designer already knows.
    ///     <para>
    ///         The frequency is clamped below Nyquist. A cutoff above it has no meaning, and the
    ///         formulae produce an unstable filter rather than saying so — one denormal-loud block of
    ///         noise, which is a horrible way to find out about a typo in a preset.
    ///     </para>
    /// </remarks>
    public static BiquadCoefficients Design(
        BiquadFilterKind kind,
        int sampleRate,
        float frequency,
        float q = 0.70710678f,
        float gainDb = 0f
    ) {
        if (sampleRate <= 0) {
            return Identity;
        }

        var nyquist = sampleRate * 0.5f;
        var f0 = Math.Clamp(frequency, 1f, nyquist * 0.99f);
        var safeQ = Math.Max(q, 0.0001f);

        var w0 = 2f * MathF.PI * f0 / sampleRate;
        var cos = MathF.Cos(w0);
        var sin = MathF.Sin(w0);
        var alpha = sin / (2f * safeQ);
        var a = MathF.Pow(10f, gainDb / 40f);

        float b0, b1, b2, a0, a1, a2;

        switch (kind) {
            case BiquadFilterKind.HighPass:
                b0 = (1f + cos) * 0.5f;
                b1 = -(1f + cos);
                b2 = b0;
                a0 = 1f + alpha;
                a1 = -2f * cos;
                a2 = 1f - alpha;
                break;

            case BiquadFilterKind.BandPass:
                b0 = alpha;
                b1 = 0f;
                b2 = -alpha;
                a0 = 1f + alpha;
                a1 = -2f * cos;
                a2 = 1f - alpha;
                break;

            case BiquadFilterKind.Notch:
                b0 = 1f;
                b1 = -2f * cos;
                b2 = 1f;
                a0 = 1f + alpha;
                a1 = -2f * cos;
                a2 = 1f - alpha;
                break;

            case BiquadFilterKind.Peaking:
                b0 = 1f + (alpha * a);
                b1 = -2f * cos;
                b2 = 1f - (alpha * a);
                a0 = 1f + (alpha / a);
                a1 = -2f * cos;
                a2 = 1f - (alpha / a);
                break;

            case BiquadFilterKind.LowShelf: {
                var sqrtA = MathF.Sqrt(a);
                var shared = 2f * sqrtA * alpha;
                b0 = a * (a + 1f - ((a - 1f) * cos) + shared);
                b1 = 2f * a * (a - 1f - ((a + 1f) * cos));
                b2 = a * (a + 1f - ((a - 1f) * cos) - shared);
                a0 = a + 1f + ((a - 1f) * cos) + shared;
                a1 = -2f * (a - 1f + ((a + 1f) * cos));
                a2 = a + 1f + ((a - 1f) * cos) - shared;
                break;
            }

            case BiquadFilterKind.HighShelf: {
                var sqrtA = MathF.Sqrt(a);
                var shared = 2f * sqrtA * alpha;
                b0 = a * (a + 1f + ((a - 1f) * cos) + shared);
                b1 = -2f * a * (a - 1f + ((a + 1f) * cos));
                b2 = a * (a + 1f + ((a - 1f) * cos) - shared);
                a0 = a + 1f - ((a - 1f) * cos) + shared;
                a1 = 2f * (a - 1f - ((a + 1f) * cos));
                a2 = a + 1f - ((a - 1f) * cos) - shared;
                break;
            }

            case BiquadFilterKind.LowPass:
            default:
                b0 = (1f - cos) * 0.5f;
                b1 = 1f - cos;
                b2 = b0;
                a0 = 1f + alpha;
                a1 = -2f * cos;
                a2 = 1f - alpha;
                break;
        }

        var inverse = 1f / a0;
        return new BiquadCoefficients(b0 * inverse, b1 * inverse, b2 * inverse, a1 * inverse, a2 * inverse);
    }
}

/// <summary>A second-order filter across every channel of a bus.</summary>
/// <remarks>
///     <para>
///         The other half of <c>docs/plan/14</c>'s "effects (reverb, filter)". One biquad is enough
///         for the jobs a game actually asks for — the muffled low-pass behind a wall, the telephone
///         band-pass on a radio voice, the high-pass that keeps rumble out of a music bus — and
///         chaining two of them on the same bus gives 24 dB an octave for the cases that want it.
///     </para>
///     <para>
///         <b>Transposed direct form II</b>, which is the form with the best numerical behaviour at
///         float precision and needs two state values per channel rather than four.
///     </para>
/// </remarks>
public sealed class BiquadFilterEffect : IAudioEffect {
    float[] state = [];
    int stateChannels;
    int sampleRate;

    BiquadCoefficients coefficients = BiquadCoefficients.Identity;
    BiquadFilterKind designedKind;
    float designedFrequency;
    float designedQ;
    float designedGain;

    /// <summary>Which shape it is.</summary>
    public BiquadFilterKind Kind { get; set; } = BiquadFilterKind.LowPass;

    /// <summary>The cutoff or centre frequency, in hertz.</summary>
    public float Frequency { get; set; } = 1_000f;

    /// <summary>Resonance. 0.7071 is flat; higher peaks at the cutoff.</summary>
    public float Q { get; set; } = 0.70710678f;

    /// <summary>Boost or cut in decibels, for the peaking and shelf shapes.</summary>
    public float GainDb { get; set; }

    /// <inheritdoc />
    public bool Enabled { get; set; } = true;

    /// <summary>The coefficients currently in use.</summary>
    public BiquadCoefficients Coefficients => coefficients;

    /// <inheritdoc />
    public void Prepare(in AudioFormat format, int maxFrames) {
        sampleRate = format.SampleRate;
        stateChannels = format.Channels;
        state = new float[stateChannels * 2];
        designedFrequency = 0f;
        Redesign();
    }

    /// <inheritdoc />
    public void Process(Span<float> buffer, int frameCount, int channels) {
        if (!Enabled || channels != stateChannels || state.Length == 0) {
            return;
        }

        Redesign();

        var b0 = coefficients.B0;
        var b1 = coefficients.B1;
        var b2 = coefficients.B2;
        var a1 = coefficients.A1;
        var a2 = coefficients.A2;

        for (var channel = 0; channel < channels; channel++) {
            var z1 = state[channel * 2];
            var z2 = state[(channel * 2) + 1];

            for (var frame = 0; frame < frameCount; frame++) {
                var index = (frame * channels) + channel;
                var x = buffer[index];
                var y = (b0 * x) + z1;
                z1 = (b1 * x) - (a1 * y) + z2;
                z2 = (b2 * x) - (a2 * y);
                buffer[index] = y;
            }

            state[channel * 2] = z1;
            state[(channel * 2) + 1] = z2;
        }
    }

    /// <inheritdoc />
    public void Reset() => Array.Clear(state);

    /// <inheritdoc />
    public bool TrySetProperty(string name, float value) {
        switch (name) {
            case "Frequency":
                Frequency = value;
                return true;

            case "Q":
                Q = value;
                return true;

            case "GainDb":
                GainDb = value;
                return true;

            default:
                return false;
        }
    }

    /// <inheritdoc />
    public bool TryGetProperty(string name, out float value) {
        switch (name) {
            case "Frequency":
                value = Frequency;
                return true;

            case "Q":
                value = Q;
                return true;

            case "GainDb":
                value = GainDb;
                return true;

            default:
                value = 0f;
                return false;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Properties => Knobs;

    static readonly string[] Knobs = ["Frequency", "Q", "GainDb"];

    void Redesign() {
        var kind = Kind;
        var frequency = Frequency;
        var q = Q;
        var gain = GainDb;

        if (kind == designedKind && frequency == designedFrequency && q == designedQ && gain == designedGain) {
            return;
        }

        coefficients = BiquadCoefficients.Design(kind, sampleRate, frequency, q, gain);
        designedKind = kind;
        designedFrequency = frequency;
        designedQ = q;
        designedGain = gain;
    }
}
