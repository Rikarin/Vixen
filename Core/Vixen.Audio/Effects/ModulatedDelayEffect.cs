// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Effects;

/// <summary>Which of the three effects a modulated delay is being.</summary>
/// <remarks>
///     The kind sets nothing on its own — it is what the factory methods configure, and it is kept
///     as a value so a preset can say what it meant rather than leaving a reader to infer "chorus"
///     from a delay of 22 ms.
/// </remarks>
public enum ModulatedDelayKind {
    /// <summary>A short, resonant sweep with feedback. Jet planes and 1970s guitars.</summary>
    Flanger,

    /// <summary>A longer, gentler sweep with several taps. One voice sounding like three.</summary>
    Chorus,

    /// <summary>The sweep alone, with none of the dry signal. A wobble in the pitch.</summary>
    Vibrato
}

/// <summary>A delay whose length is being moved by a slow oscillator.</summary>
/// <remarks>
///     <para>
///         <b>One effect, because they are one effect.</b> A flanger is a 1–10 ms delay swept with
///         feedback, a chorus is a 15–40 ms delay swept with more depth and no feedback, and a
///         vibrato is either of those with the dry signal turned off. Writing them as three classes
///         would be the same two hundred lines three times, and the interesting differences would be
///         buried in the duplication rather than visible as the defaults they are. <see cref="Chorus" />,
///         <see cref="Flanger" /> and <see cref="Vibrato" /> hand back the presets.
///     </para>
///     <para>
///         <b>Why they sound different at all.</b> A flanger's delay is short enough that the dry and
///         delayed copies interfere across the whole audible range, producing a comb of notches at
///         harmonically related frequencies — which the ear hears as one moving resonance. A chorus's
///         delay is longer than the ear's fusion window, so the copies are heard as separate near-unison
///         voices instead. Same arithmetic, and the number that separates them is about 15 ms.
///     </para>
///     <para>
///         <b>The delay is read at a fractional position and interpolated.</b> That is the whole
///         thing: rounding the sweep to whole samples makes it step, and a stepping delay is a click
///         per step rather than a sweep. Linear interpolation loses a little top end at long delays
///         and is what almost every implementation uses.
///     </para>
///     <para>
///         <b>The channels are swept out of phase with each other.</b> Modulating both identically
///         gives an effect that is undeniably present and completely mono; ninety degrees apart is
///         what makes a chorus wide.
///     </para>
/// </remarks>
public sealed class ModulatedDelayEffect : IAudioEffect {
    const float MaxDelaySeconds = 0.05f;
    const int MaxVoices = 4;

    float[] lines = [];
    float[] feedbackState = [];
    int lineFrames;
    int cursor;
    int channelCount;
    int sampleRate;
    double phase;

    /// <summary>What it is being. Descriptive; the numbers below are what actually decide.</summary>
    public ModulatedDelayKind Kind { get; init; } = ModulatedDelayKind.Chorus;

    /// <summary>The middle of the sweep, in seconds.</summary>
    /// <remarks>Below about 15 ms it flanges and above it choruses. Clamped to 50 ms.</remarks>
    public float DelaySeconds { get; set; } = 0.022f;

    /// <summary>How far either side of that the sweep travels, in seconds.</summary>
    public float DepthSeconds { get; set; } = 0.004f;

    /// <summary>How many times a second the oscillator goes round.</summary>
    /// <remarks>
    ///     Slow. Above about 5 Hz it stops sounding like movement and starts sounding like a broken
    ///     tape; the interesting range for both effects is a fraction of a hertz to about three.
    /// </remarks>
    public float RateHz { get; set; } = 0.4f;

    /// <summary>How much of the output feeds back in. Negative inverts, which is a flanger's other voice.</summary>
    /// <remarks>Clamped to ±0.95: at one the resonance never decays.</remarks>
    public float Feedback { get; set; }

    /// <summary>How many taps to read the line at, spread evenly around the oscillator.</summary>
    /// <remarks>
    ///     One is a flanger. Three is the classic chorus — the taps are at different points in the
    ///     sweep, so they are detuned differently from each other, which is what makes it sound like
    ///     several players rather than one player through an effect.
    /// </remarks>
    public int Voices { get; set; } = 1;

    /// <summary>How far apart the channels are swept, as a fraction of the cycle.</summary>
    /// <remarks>0.25 is a quarter cycle — ninety degrees — which is the usual stereo spread.</remarks>
    public float StereoSpread { get; set; } = 0.25f;

    /// <summary>How much of the swept signal to add.</summary>
    public float Wet { get; set; } = 0.5f;

    /// <summary>How much of the untouched signal to keep. Zero turns any of these into a vibrato.</summary>
    public float Dry { get; set; } = 1f;

    /// <inheritdoc />
    public bool Enabled { get; set; } = true;

    /// <summary>A chorus: three taps, a slow deep sweep, no feedback.</summary>
    /// <returns>The effect.</returns>
    public static ModulatedDelayEffect Chorus() => new() {
        Kind = ModulatedDelayKind.Chorus,
        DelaySeconds = 0.022f,
        DepthSeconds = 0.006f,
        RateHz = 0.35f,
        Feedback = 0f,
        Voices = 3,
        Wet = 0.5f,
        Dry = 1f
    };

    /// <summary>A flanger: one tap, short, fast, and resonant.</summary>
    /// <returns>The effect.</returns>
    public static ModulatedDelayEffect Flanger() => new() {
        Kind = ModulatedDelayKind.Flanger,
        DelaySeconds = 0.004f,
        DepthSeconds = 0.003f,
        RateHz = 0.25f,
        Feedback = 0.7f,
        Voices = 1,
        Wet = 0.7f,
        Dry = 1f
    };

    /// <summary>A vibrato: the sweep alone, with none of the dry signal to interfere with.</summary>
    /// <returns>The effect.</returns>
    public static ModulatedDelayEffect Vibrato() => new() {
        Kind = ModulatedDelayKind.Vibrato,
        DelaySeconds = 0.006f,
        DepthSeconds = 0.002f,
        RateHz = 5f,
        Feedback = 0f,
        Voices = 1,
        Wet = 1f,
        Dry = 0f
    };

    /// <inheritdoc />
    public void Prepare(in AudioFormat format, int maxFrames) {
        sampleRate = format.SampleRate;
        channelCount = format.Channels;
        lineFrames = Math.Max(4, (int)(MaxDelaySeconds * format.SampleRate) + 4);
        lines = new float[lineFrames * channelCount];
        feedbackState = new float[channelCount];
        cursor = 0;
        phase = 0.0;
    }

    /// <inheritdoc />
    public void Process(Span<float> buffer, int frameCount, int channels) {
        if (!Enabled || channels != channelCount || lines.Length == 0) {
            return;
        }

        var taps = Math.Clamp(Voices, 1, MaxVoices);
        var feedback = Math.Clamp(Feedback, -0.95f, 0.95f);
        var wet = Wet;
        var dry = Dry;
        var spread = StereoSpread;

        // In samples, and kept away from both ends of the line: the read has to stay at least one
        // sample behind the write or it reads what has not been written yet, and at least one ahead
        // of the far end or the interpolation runs off it.
        var centre = Math.Clamp(DelaySeconds * sampleRate, 2f, lineFrames - 3f);
        var depth = Math.Clamp(DepthSeconds * sampleRate, 0f, centre - 2f);
        var increment = Math.Max(RateHz, 0f) / sampleRate;

        for (var frame = 0; frame < frameCount; frame++) {
            var offset = frame * channels;
            var write = cursor * channels;

            for (var channel = 0; channel < channels; channel++) {
                var input = buffer[offset + channel];
                var swept = 0f;

                for (var tap = 0; tap < taps; tap++) {
                    // Each channel and each tap sits at its own point in the cycle. The taps are
                    // spread over the whole cycle and the channels by StereoSpread.
                    var tapPhase = phase + (channel * spread) + ((float)tap / taps);
                    var modulation = MathF.Sin(2f * MathF.PI * (float)(tapPhase - Math.Floor(tapPhase)));
                    swept += Read(channel, centre + (depth * modulation), channels);
                }

                swept /= taps;

                // The feedback is taken from the swept signal of the previous sample, which is what
                // makes a flanger resonate rather than merely interfere.
                lines[write + channel] = input + (feedbackState[channel] * feedback);
                feedbackState[channel] = swept;
                buffer[offset + channel] = (input * dry) + (swept * wet);
            }

            phase += increment;

            if (phase >= 1.0) {
                phase -= 1.0;
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
        Array.Clear(feedbackState);
        cursor = 0;
        phase = 0.0;
    }

    /// <inheritdoc />
    public bool TrySetProperty(string name, float value) {
        switch (name) {
            case "DelaySeconds":
                DelaySeconds = value;
                return true;

            case "DepthSeconds":
                DepthSeconds = value;
                return true;

            case "RateHz":
                RateHz = value;
                return true;

            case "Feedback":
                Feedback = value;
                return true;

            case "StereoSpread":
                StereoSpread = value;
                return true;

            case "Wet":
                Wet = value;
                return true;

            case "Dry":
                Dry = value;
                return true;

            default:
                return false;
        }
    }

    /// <inheritdoc />
    public bool TryGetProperty(string name, out float value) {
        switch (name) {
            case "DelaySeconds":
                value = DelaySeconds;
                return true;

            case "DepthSeconds":
                value = DepthSeconds;
                return true;

            case "RateHz":
                value = RateHz;
                return true;

            case "Feedback":
                value = Feedback;
                return true;

            case "StereoSpread":
                value = StereoSpread;
                return true;

            case "Wet":
                value = Wet;
                return true;

            case "Dry":
                value = Dry;
                return true;

            default:
                value = 0f;
                return false;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Properties => Knobs;

    static readonly string[] Knobs = ["DelaySeconds", "DepthSeconds", "RateHz", "Feedback", "StereoSpread", "Wet", "Dry"];

    /// <summary>Reads the line a fractional number of samples behind the write head.</summary>
    /// <remarks>
    ///     The interpolation is the effect. Rounding to whole samples turns a smooth sweep into a
    ///     staircase, and every step of that staircase is a discontinuity in the waveform — which is
    ///     to say, a click, several times a second, for as long as the effect is switched on.
    /// </remarks>
    float Read(int channel, float samplesBack, int channels) {
        var position = cursor - samplesBack;

        while (position < 0f) {
            position += lineFrames;
        }

        var index = (int)position;
        var fraction = position - index;
        var next = index + 1 >= lineFrames ? 0 : index + 1;

        var a = lines[(index * channels) + channel];
        var b = lines[(next * channels) + channel];
        return a + ((b - a) * fraction);
    }
}
