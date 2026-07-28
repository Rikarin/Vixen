// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Effects;

/// <summary>Throws away resolution, on purpose, in both of the ways audio has any.</summary>
/// <remarks>
///     <para>
///         Digital audio has two kinds of precision — how finely a sample's value is measured, and
///         how often it is measured — and this ruins each of them independently, because they sound
///         completely different and a preset usually wants one and not the other.
///     </para>
///     <para>
///         <b><see cref="Bits" /> is quantisation noise.</b> Rounding every sample to one of a
///         handful of levels adds an error that follows the signal, which is heard as a gritty,
///         crunchy edge that gets louder as the sound gets quieter. That last part is the giveaway of
///         a low bit depth and is the opposite of how analogue noise behaves.
///     </para>
///     <para>
///         <b><see cref="Downsample" /> is aliasing.</b> Holding each sample for several outputs is
///         a sample rate reduction with no filter in front of it, so everything above the new Nyquist
///         folds back down as inharmonic tones. It is what makes something sound like it is coming
///         through a 1990s handheld rather than merely sounding rough.
///     </para>
///     <para>
///         <b>The rate divisor is fractional and that is not a novelty.</b> A phase accumulator means
///         it can be swept — from 1 to 20 over a second — which is the sound of a signal degrading,
///         and it cannot be done by an integer counter without stepping audibly on the way.
///     </para>
/// </remarks>
public sealed class BitCrusherEffect : IAudioEffect {
    float[] held = [];
    int channelCount;
    float countdown;

    /// <summary>How many bits of resolution to leave. Fractional is allowed and useful for a sweep.</summary>
    /// <remarks>
    ///     Sixteen is transparent — it is what the clip was probably stored at. Eight is an early
    ///     sampler, four is unmistakably broken, one is a square wave.
    /// </remarks>
    public float Bits { get; set; } = 8f;

    /// <summary>How many output samples each input sample is held for.</summary>
    /// <remarks>One is untouched. Two halves the effective sample rate, four quarters it.</remarks>
    public float Downsample { get; set; } = 1f;

    /// <summary>How much of the ruined signal to keep, against the untouched one.</summary>
    public float Mix { get; set; } = 1f;

    /// <inheritdoc />
    public bool Enabled { get; set; } = true;

    /// <inheritdoc />
    public void Prepare(in AudioFormat format, int maxFrames) {
        channelCount = format.Channels;
        held = new float[channelCount];
        countdown = 0f;
    }

    /// <inheritdoc />
    public void Process(Span<float> buffer, int frameCount, int channels) {
        if (!Enabled || channels != channelCount || held.Length == 0) {
            return;
        }

        var bits = Math.Clamp(Bits, 1f, 24f);
        var levels = MathF.Pow(2f, bits - 1f);
        var step = 1f / levels;
        var divisor = MathF.Max(Downsample, 1f);
        var mix = Math.Clamp(Mix, 0f, 1f);

        for (var frame = 0; frame < frameCount; frame++) {
            var offset = frame * channels;

            // A countdown rather than a count-up, so the sample is taken at the *start* of the run it
            // will be held for. Counting up takes it at the end, which means the first `divisor`
            // outputs after a reset are whatever the hold was initialised to — silence.
            //
            // One sample every `divisor` of them: a divisor of 2.5 takes two, then three, then two,
            // which averages to the rate asked for without any run being a fraction of a sample.
            var take = countdown <= 0f;

            if (take) {
                countdown += divisor;
            }

            countdown -= 1f;

            for (var channel = 0; channel < channels; channel++) {
                var dry = buffer[offset + channel];

                if (take) {
                    // Rounded rather than truncated: truncation biases every sample towards zero,
                    // which is a DC offset that changes with the signal — audible as a thump when
                    // the effect is switched on.
                    held[channel] = MathF.Round(dry * levels) * step;
                }

                buffer[offset + channel] = dry + ((held[channel] - dry) * mix);
            }
        }
    }

    /// <inheritdoc />
    public void Reset() {
        Array.Clear(held);
        countdown = 0f;
    }
}
