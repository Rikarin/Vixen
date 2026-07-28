// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Effects;

/// <summary>A chain of all-pass filters being swept, which puts moving notches in the sound.</summary>
/// <remarks>
///     <para>
///         <b>Not a flanger, and the difference is the reason both exist.</b> A flanger delays a copy
///         and interferes it with the original, so its notches land at whole multiples of one
///         frequency — a harmonic comb, which the ear hears as pitched. A phaser shifts the
///         <em>phase</em> of a copy with all-pass sections, so its notches land wherever the sections
///         put them, unrelated to each other. That is why a flanger sounds like a jet and a phaser
///         sounds like a swirl.
///     </para>
///     <para>
///         <b>An all-pass filter changes nothing you can hear on its own.</b> Its magnitude response
///         is flat — every frequency comes out at the level it went in — and all it does is delay
///         some frequencies more than others. Adding it back to the dry signal is what turns that
///         phase difference into cancellation, and sweeping where the phase rotates is what makes the
///         cancellation move.
///     </para>
///     <para>
///         <b>Stages come in pairs.</b> Each first-order section contributes one notch to the sum, so
///         four stages is the classic four-notch phaser and eight is the lush one. An odd number
///         works and is unusual.
///     </para>
/// </remarks>
public sealed class PhaserEffect : IAudioEffect {
    const int MaxStages = 12;

    float[] state = [];
    float[] feedbackState = [];
    int channelCount;
    int sampleRate;
    double phase;

    /// <summary>How many all-pass sections, and therefore how many notches.</summary>
    public int Stages { get; set; } = 4;

    /// <summary>The bottom of the sweep, in hertz.</summary>
    public float MinFrequency { get; set; } = 200f;

    /// <summary>The top of the sweep, in hertz.</summary>
    public float MaxFrequency { get; set; } = 2_000f;

    /// <summary>How many times a second the sweep goes round.</summary>
    public float RateHz { get; set; } = 0.3f;

    /// <summary>How much of the output feeds back in, which sharpens the notches.</summary>
    /// <remarks>Clamped to ±0.95. Negative moves the notches, because it inverts what is cancelling.</remarks>
    public float Feedback { get; set; } = 0.5f;

    /// <summary>How far apart the channels are swept, as a fraction of the cycle.</summary>
    public float StereoSpread { get; set; } = 0.25f;

    /// <summary>How much of the phase-shifted signal to add.</summary>
    /// <remarks>
    ///     The notches are deepest at an equal mix, because a cancellation is only complete when the
    ///     two things cancelling are the same size.
    /// </remarks>
    public float Wet { get; set; } = 0.5f;

    /// <summary>How much of the untouched signal to keep.</summary>
    public float Dry { get; set; } = 0.5f;

    /// <inheritdoc />
    public bool Enabled { get; set; } = true;

    /// <inheritdoc />
    public void Prepare(in AudioFormat format, int maxFrames) {
        sampleRate = format.SampleRate;
        channelCount = format.Channels;
        state = new float[channelCount * MaxStages];
        feedbackState = new float[channelCount];
        phase = 0.0;
    }

    /// <inheritdoc />
    public void Process(Span<float> buffer, int frameCount, int channels) {
        if (!Enabled || channels != channelCount || state.Length == 0) {
            return;
        }

        var stages = Math.Clamp(Stages, 1, MaxStages);
        var feedback = Math.Clamp(Feedback, -0.95f, 0.95f);
        var wet = Wet;
        var dry = Dry;
        var increment = Math.Max(RateHz, 0f) / sampleRate;

        var nyquist = sampleRate * 0.5f;
        var low = Math.Clamp(MathF.Min(MinFrequency, MaxFrequency), 20f, nyquist * 0.9f);
        var high = Math.Clamp(MathF.Max(MinFrequency, MaxFrequency), low, nyquist * 0.9f);
        var logLow = MathF.Log(low);
        var logHigh = MathF.Log(high);

        for (var frame = 0; frame < frameCount; frame++) {
            var offset = frame * channels;

            for (var channel = 0; channel < channels; channel++) {
                var tapPhase = phase + (channel * StereoSpread);
                var modulation = MathF.Sin(2f * MathF.PI * (float)(tapPhase - Math.Floor(tapPhase)));

                // Swept in log frequency, because the ear hears pitch that way — a linear sweep
                // spends most of its time at the top of the range, where there is one octave, and
                // rushes through the bottom, where there are six.
                var centre = MathF.Exp(logLow + ((logHigh - logLow) * ((modulation + 1f) * 0.5f)));

                // The first-order all-pass coefficient for a phase rotation at that frequency. The
                // tangent is the bilinear transform's warping, and it is what keeps the notch where
                // it was asked for as the frequency approaches Nyquist.
                var tangent = MathF.Tan(MathF.PI * centre / sampleRate);
                var coefficient = (tangent - 1f) / (tangent + 1f);

                var input = buffer[offset + channel];
                var value = input + (feedbackState[channel] * feedback);

                for (var stage = 0; stage < stages; stage++) {
                    ref var memory = ref state[(channel * MaxStages) + stage];
                    var output = (coefficient * value) + memory;
                    memory = value - (coefficient * output);
                    value = output;
                }

                feedbackState[channel] = value;
                buffer[offset + channel] = (input * dry) + (value * wet);
            }

            phase += increment;

            if (phase >= 1.0) {
                phase -= 1.0;
            }
        }
    }

    /// <inheritdoc />
    public void Reset() {
        Array.Clear(state);
        Array.Clear(feedbackState);
        phase = 0.0;
    }
}
