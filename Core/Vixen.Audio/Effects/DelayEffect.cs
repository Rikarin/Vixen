// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Effects;

/// <summary>An echo, with the repeats getting darker the way a real one does.</summary>
/// <remarks>
///     <para>
///         The second environmental effect after reverb, and the one that does the jobs reverb
///         cannot: a canyon, a stairwell, a public-address system, the quarter-note repeat under a
///         piece of music. Reverb is a smear with no discernible repeats; a delay is repeats you can
///         count, and the ear treats them as completely different things.
///     </para>
///     <para>
///         <b>The feedback path is filtered, and that is what makes it sound like a place.</b> An
///         unfiltered delay repeats the same bright signal until it fades, which is what a digital
///         delay does and what nothing in the world does — every real reflection loses its top end to
///         the surface it bounced off. One low-pass in the loop, and the repeats darken as they die.
///     </para>
///     <para>
///         <b>Feedback is clamped below one.</b> At one it never decays; above it the level doubles
///         every repeat until the master limiter is the only thing between the player and a very loud
///         noise. 0.95 is the ceiling, which is around forty audible repeats.
///     </para>
/// </remarks>
public sealed class DelayEffect : IAudioEffect {
    const float MaxFeedback = 0.95f;

    float[] lines = [];
    float[] damping = [];
    int lineFrames;
    int cursor;
    int channelCount;
    int sampleRate;

    /// <summary>The longest delay this effect can be set to.</summary>
    /// <remarks>
    ///     Fixed when <see cref="Prepare" /> runs, because it decides how much memory the delay lines
    ///     take — two seconds of stereo at 48 kHz is 768 kB. <see cref="DelaySeconds" /> can be moved
    ///     freely below it.
    /// </remarks>
    public float MaxDelaySeconds { get; init; } = 2f;

    /// <summary>How long until the first repeat.</summary>
    /// <remarks>
    ///     Changing it while the effect is running moves the read head, which pitches the tail as it
    ///     travels — the sound every tape delay makes, and a legitimate thing to automate.
    /// </remarks>
    public float DelaySeconds { get; set; } = 0.25f;

    /// <summary>How much of each repeat feeds the next. Clamped below one.</summary>
    public float Feedback { get; set; } = 0.4f;

    /// <summary>How much of the delayed signal to add.</summary>
    public float Wet { get; set; } = 0.35f;

    /// <summary>How much of the untouched signal to keep.</summary>
    public float Dry { get; set; } = 1f;

    /// <summary>Where the low-pass in the feedback path sits, in hertz.</summary>
    /// <remarks>
    ///     Set it above Nyquist to turn the darkening off and get a plain digital delay, which is
    ///     occasionally what a piece of music wants.
    /// </remarks>
    public float DampingHz { get; set; } = 4_000f;

    /// <summary>Whether the repeats alternate between the speakers.</summary>
    /// <remarks>
    ///     Each channel's feedback goes into the next one's line rather than its own, so a sound
    ///     bounces left, right, left. Only means anything with exactly two channels; with one there
    ///     is nowhere to bounce to, and beyond two there is no agreed order to go round in.
    /// </remarks>
    public bool PingPong { get; set; }

    /// <inheritdoc />
    public bool Enabled { get; set; } = true;

    /// <inheritdoc />
    public void Prepare(in AudioFormat format, int maxFrames) {
        sampleRate = format.SampleRate;
        channelCount = format.Channels;
        lineFrames = Math.Max(1, (int)(MathF.Max(MaxDelaySeconds, 0.001f) * format.SampleRate));
        lines = new float[lineFrames * channelCount];
        damping = new float[channelCount];
        cursor = 0;
    }

    /// <inheritdoc />
    public void Process(Span<float> buffer, int frameCount, int channels) {
        if (!Enabled || channels != channelCount || lines.Length == 0) {
            return;
        }

        var delay = Math.Clamp((int)(DelaySeconds * sampleRate), 1, lineFrames - 1);
        var feedback = Math.Clamp(Feedback, 0f, MaxFeedback);
        var wet = MathF.Max(Wet, 0f);
        var dry = MathF.Max(Dry, 0f);

        // A one-pole low-pass, expressed as "how much of the previous output to keep". Above Nyquist
        // the coefficient is zero and the filter disappears rather than being a branch per sample.
        var nyquist = sampleRate * 0.5f;
        var damp = DampingHz >= nyquist
            ? 0f
            : MathF.Exp(-2f * MathF.PI * Math.Clamp(DampingHz, 20f, nyquist) / sampleRate);

        var bounce = PingPong && channels == 2;

        Span<float> echoed = stackalloc float[AudioFormat.MaxChannels];
        Span<float> fed = stackalloc float[AudioFormat.MaxChannels];

        for (var frame = 0; frame < frameCount; frame++) {
            var offset = frame * channels;
            var read = ((cursor - delay + lineFrames) % lineFrames) * channels;
            var write = cursor * channels;

            // Read and damp every channel before writing any of them. Ping-pong crosses the channels
            // over, so a loop that wrote as it went would feed one channel with what it had just
            // written for the other rather than with what came round the line.
            for (var channel = 0; channel < channels; channel++) {
                echoed[channel] = lines[read + channel];

                // Damped once on its way back round, so each repeat is darker than the last rather
                // than the whole tail being filtered once.
                fed[channel] = damping[channel] =
                    (echoed[channel] * (1f - damp)) + (damping[channel] * damp);
            }

            for (var channel = 0; channel < channels; channel++) {
                var input = buffer[offset + channel];

                // The dry signal always enters its own line; it is the *feedback* that crosses, which
                // is what makes a sound bounce left, right, left instead of appearing in both at once.
                var returning = bounce ? fed[1 - channel] : fed[channel];
                lines[write + channel] = input + (returning * feedback);
                buffer[offset + channel] = (input * dry) + (echoed[channel] * wet);
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
        Array.Clear(damping);
        cursor = 0;
    }

    /// <inheritdoc />
    public bool TrySetProperty(string name, float value) {
        switch (name) {
            case "DelaySeconds":
                DelaySeconds = value;
                return true;

            case "Feedback":
                Feedback = value;
                return true;

            case "Wet":
                Wet = value;
                return true;

            case "Dry":
                Dry = value;
                return true;

            case "DampingHz":
                DampingHz = value;
                return true;

            default:
                return false;
        }
    }
}
