// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Effects;

/// <summary>Changes the pitch without changing the length.</summary>
/// <remarks>
///     <para>
///         <b>Which is the one thing <c>PlaybackSettings.Pitch</c> cannot do.</b> That resamples: a
///         voice played at 2.0 is an octave up <em>and</em> half as long, which is right for a
///         footstep and wrong for anything a player is listening to the words of. A monster whose
///         voice is a human's an octave down, a radio operator two semitones up, a slowed-down
///         explosion that keeps its bass — all of them need the pitch and the duration separated.
///     </para>
///     <para>
///         <b>Time domain, two taps, crossfaded.</b> A read pointer runs through a delay line at a
///         different rate to the write pointer, which is a pitch change; it eventually catches up
///         with the write pointer, at which point it has to jump, and a jump is a click. So there are
///         two of them half a grain apart, and each is faded in while the other is faded out, so
///         whichever one is jumping is silent while it does.
///     </para>
///     <para>
///         <b>What it costs, honestly.</b> The crossfade means two copies of the signal are summed at
///         a fixed offset, which is a comb filter that moves — audible as a slight hollowness on
///         broadband material and as warble on a sustained tone. Larger grains reduce the warble and
///         increase the smearing of transients; there is no setting that has neither.
///     </para>
///     <para>
///         <b>The alternative, and why not yet.</b> A phase vocoder does this in the frequency domain
///         and sounds much better on sustained material — at the cost of an FFT pair per hop, latency
///         of a whole window, and transients that smear into a metallic ring unless it is taught
///         about them. The transform to build it on now exists (<c>Vixen.Audio.Dsp.Fft</c>) and it is
///         owed. For a monster voice, a radio and a slowed-down explosion, this is what every game
///         has always used.
///     </para>
/// </remarks>
public sealed class PitchShiftEffect : IAudioEffect {
    const float MaxGrainSeconds = 0.2f;

    float[] lines = [];
    int lineFrames;
    int cursor;
    int channelCount;
    int sampleRate;
    float grainFrames;
    float position;

    /// <summary>How far to shift, in semitones. Twelve is an octave up, −12 an octave down.</summary>
    /// <remarks>
    ///     Clamped to ±24. Past two octaves the artefacts are the effect rather than a side of it,
    ///     and anything that far from the source is better authored than processed.
    /// </remarks>
    public float Semitones { get; set; }

    /// <summary>How long each grain is.</summary>
    /// <remarks>
    ///     The one trade-off. Short grains follow transients and warble; long ones are smooth and
    ///     smear. Fifty milliseconds is the usual compromise and is what a voice wants; a percussive
    ///     sound wants half that.
    /// </remarks>
    public float GrainSeconds { get; set; } = 0.05f;

    /// <summary>How much of the shifted signal to keep, against the untouched one.</summary>
    /// <remarks>
    ///     Below one it is a harmoniser — the original and the shifted copy together, which is what a
    ///     chorus of monsters or a thickened shout is.
    /// </remarks>
    public float Mix { get; set; } = 1f;

    /// <inheritdoc />
    public bool Enabled { get; set; } = true;

    /// <summary>The playback-rate ratio the semitone setting works out to.</summary>
    public float Ratio => MathF.Pow(2f, Math.Clamp(Semitones, -24f, 24f) / 12f);

    /// <inheritdoc />
    public void Prepare(in AudioFormat format, int maxFrames) {
        sampleRate = format.SampleRate;
        channelCount = format.Channels;
        lineFrames = Math.Max(8, (int)(MaxGrainSeconds * 2f * format.SampleRate));
        lines = new float[lineFrames * channelCount];
        cursor = 0;
        position = 0f;
        grainFrames = 0f;
    }

    /// <inheritdoc />
    public void Process(Span<float> buffer, int frameCount, int channels) {
        if (!Enabled || channels != channelCount || lines.Length == 0) {
            return;
        }

        var ratio = Ratio;
        var mix = Math.Clamp(Mix, 0f, 1f);
        var grain = Math.Clamp(GrainSeconds * sampleRate, 64f, lineFrames * 0.5f);

        // Changing the grain length mid-flight would move both taps at once and step the output, so
        // the new length is taken at the start of a block and the read position rescaled with it.
        if (grainFrames <= 0f) {
            grainFrames = grain;
        } else if (MathF.Abs(grain - grainFrames) > 0.5f) {
            position *= grain / grainFrames;
            grainFrames = grain;
        }

        // How fast the tap's distance behind the write head changes. To raise the pitch the tap has
        // to advance *faster* than the write head, which means the gap between them shrinks — so the
        // drift is 1 − ratio and not ratio − 1. The read pointer then advances at
        // 1 − (1 − ratio) = ratio samples per sample, which is the definition of the shift.
        //
        // With the sign the other way round a ratio of 2 freezes the tap and produces no shift at
        // all, and a ratio of 0.5 shifts *up* by a half. Both of which it did.
        var drift = 1f - ratio;

        for (var frame = 0; frame < frameCount; frame++) {
            var offset = frame * channels;
            var write = cursor * channels;

            for (var channel = 0; channel < channels; channel++) {
                lines[write + channel] = buffer[offset + channel];
            }

            // The two taps are half a grain apart, so one is always in the middle of its window —
            // at full gain, furthest from its jump — while the other is at an edge.
            var first = position;
            var second = position + (grainFrames * 0.5f);

            if (second >= grainFrames) {
                second -= grainFrames;
            }

            // A raised cosine, which sums to exactly one across the pair. A linear crossfade would
            // dip in the middle, because two uncorrelated copies at 0.5 are quieter than one at 1.
            var firstGain = 0.5f * (1f - MathF.Cos(2f * MathF.PI * first / grainFrames));
            var secondGain = 1f - firstGain;

            for (var channel = 0; channel < channels; channel++) {
                var dry = buffer[offset + channel];

                var shifted = (Read(channel, first, channels) * firstGain)
                    + (Read(channel, second, channels) * secondGain);

                buffer[offset + channel] = dry + ((shifted - dry) * mix);
            }

            position += drift;

            while (position >= grainFrames) {
                position -= grainFrames;
            }

            while (position < 0f) {
                position += grainFrames;
            }

            cursor++;

            if (cursor >= lineFrames) {
                cursor = 0;
            }
        }
    }

    /// <inheritdoc />
    public void Reset() {
        Array.Clear(lines);
        cursor = 0;
        position = 0f;
        grainFrames = 0f;
    }

    /// <summary>Reads a tap, a fractional distance behind the write head.</summary>
    float Read(int channel, float behind, int channels) {
        var read = cursor - behind;

        while (read < 0f) {
            read += lineFrames;
        }

        var index = (int)read;
        var fraction = read - index;
        var next = index + 1 >= lineFrames ? 0 : index + 1;

        var a = lines[(index * channels) + channel];
        var b = lines[(next * channels) + channel];
        return a + ((b - a) * fraction);
    }
}
