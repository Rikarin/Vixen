// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Effects;

/// <summary>Turns a bus down when it — or another bus — gets loud.</summary>
/// <remarks>
///     <para>
///         <b>The reason this exists is ducking.</b> Music that stays at its mixed level under
///         dialogue makes the dialogue unintelligible, and turning the music down by hand from
///         gameplay code means every system that can produce speech knowing about the music bus.
///         Point this at the dialogue bus with <c>AudioBus.SetSidechain</c> instead and the music
///         gets out of the way whenever anybody speaks, in proportion to how loudly they do.
///     </para>
///     <para>
///         Without a key it is an ordinary compressor, which is the other half of what a bus wants:
///         a footstep bus with forty overlapping sources has a peak that wanders by 20 dB, and
///         compressing it is how it sits at one level in the mix.
///     </para>
///     <para>
///         <b>Feed-forward, and the gain computer works in decibels.</b> That is the arrangement
///         every software compressor uses: measure the input, decide a gain in dB, smooth it, apply
///         it. A feedback design — measuring the output — is what analogue circuits did because they
///         had to, and it makes the ratio depend on the signal.
///     </para>
///     <para>
///         <b>One gain for every channel.</b> Compressing channels independently pulls a stereo
///         image apart: a loud transient on the left turns the left down and the sound walks to the
///         right. The detector takes the loudest channel and every channel is scaled by the same
///         number.
///     </para>
/// </remarks>
public sealed class CompressorEffect : ISidechainEffect {
    const float Floor = 1e-9f;

    float envelopeDb;
    int sampleRate;
    int channelCount;

    /// <summary>Above this level, in decibels, the compressor starts working. 0 dB is full scale.</summary>
    public float ThresholdDb { get; set; } = -18f;

    /// <summary>How much is taken off above the threshold. 4 means 4 dB in becomes 1 dB out.</summary>
    /// <remarks>Anything above about 10 is a limiter; use <see cref="LimiterEffect" />, which is built for it.</remarks>
    public float Ratio { get; set; } = 4f;

    /// <summary>How wide the bend into the ratio is, in decibels.</summary>
    /// <remarks>
    ///     A soft knee — the default 6 dB — starts compressing gently either side of the threshold,
    ///     which is what makes a compressor inaudible as an effect. Zero is a hard knee and is what
    ///     you want when the threshold is a limit rather than a target.
    /// </remarks>
    public float KneeDb { get; set; } = 6f;

    /// <summary>How fast it reacts to something getting louder.</summary>
    /// <remarks>
    ///     Ten milliseconds by default: fast enough to catch a shout, slow enough not to flatten the
    ///     attack of every sound that goes through it. For ducking, faster is better — the first
    ///     syllable is the one that has to be heard.
    /// </remarks>
    public float AttackSeconds { get; set; } = 0.01f;

    /// <summary>How fast it recovers when the signal drops.</summary>
    /// <remarks>
    ///     Two hundred milliseconds. Much shorter and the music audibly pumps between words; much
    ///     longer and it stays out of the way after the speaker has finished.
    /// </remarks>
    public float ReleaseSeconds { get; set; } = 0.2f;

    /// <summary>A gain applied after compression, in decibels, to put the level back where it was.</summary>
    public float MakeupDb { get; set; }

    /// <inheritdoc />
    public bool Enabled { get; set; } = true;

    /// <summary>How much the compressor is currently taking off, in decibels. Never positive.</summary>
    /// <remarks>
    ///     The number a mixer's gain-reduction meter shows, and the one that answers "is the ducking
    ///     working" without anybody having to listen.
    /// </remarks>
    public float GainReductionDb { get; private set; }

    /// <inheritdoc />
    public void Prepare(in AudioFormat format, int maxFrames) {
        sampleRate = format.SampleRate;
        channelCount = format.Channels;
        envelopeDb = -120f;
        GainReductionDb = 0f;
    }

    /// <inheritdoc />
    /// <remarks>Keyed by the signal being compressed, which is an ordinary compressor.</remarks>
    public void Process(Span<float> buffer, int frameCount, int channels) =>
        Process(buffer, buffer, frameCount, channels);

    /// <inheritdoc />
    public void Process(Span<float> buffer, ReadOnlySpan<float> key, int frameCount, int channels) {
        if (!Enabled || channels != channelCount || sampleRate <= 0) {
            return;
        }

        var attack = Coefficient(AttackSeconds);
        var release = Coefficient(ReleaseSeconds);
        var threshold = ThresholdDb;
        var knee = MathF.Max(KneeDb, 0f);
        var ratio = MathF.Max(Ratio, 1f);
        var makeup = Decibels.ToLinear(MakeupDb);
        var reduction = 0f;

        for (var frame = 0; frame < frameCount; frame++) {
            var offset = frame * channels;
            var loudest = 0f;

            for (var channel = 0; channel < channels; channel++) {
                loudest = MathF.Max(loudest, MathF.Abs(key[offset + channel]));
            }

            var levelDb = Decibels.FromLinear(MathF.Max(loudest, Floor));

            // One-pole smoothing with two time constants: rise fast, fall slow. Doing it on the
            // detector rather than on the gain is what makes the attack and release times mean what
            // their names say.
            envelopeDb = levelDb > envelopeDb
                ? levelDb + ((envelopeDb - levelDb) * attack)
                : levelDb + ((envelopeDb - levelDb) * release);

            var over = envelopeDb - threshold;
            float wanted;

            if (knee > 0f && over > -knee * 0.5f && over < knee * 0.5f) {
                // The knee: a quadratic that meets the flat part and the ratio line with matching
                // slopes, so there is no corner anywhere for the ear to find.
                var t = over + (knee * 0.5f);
                wanted = -(1f - (1f / ratio)) * t * t / (2f * knee);
            } else if (over > 0f) {
                wanted = -over * (1f - (1f / ratio));
            } else {
                wanted = 0f;
            }

            reduction = MathF.Min(reduction, wanted);
            var gain = Decibels.ToLinear(wanted) * makeup;

            for (var channel = 0; channel < channels; channel++) {
                buffer[offset + channel] *= gain;
            }
        }

        GainReductionDb = reduction;
    }

    /// <inheritdoc />
    public void Reset() {
        envelopeDb = -120f;
        GainReductionDb = 0f;
    }

    /// <inheritdoc />
    public bool TrySetProperty(string name, float value) {
        switch (name) {
            case "ThresholdDb":
                ThresholdDb = value;
                return true;

            case "Ratio":
                Ratio = value;
                return true;

            case "KneeDb":
                KneeDb = value;
                return true;

            case "AttackSeconds":
                AttackSeconds = value;
                return true;

            case "ReleaseSeconds":
                ReleaseSeconds = value;
                return true;

            case "MakeupDb":
                MakeupDb = value;
                return true;

            default:
                return false;
        }
    }

    /// <inheritdoc />
    public bool TryGetProperty(string name, out float value) {
        switch (name) {
            case "ThresholdDb":
                value = ThresholdDb;
                return true;

            case "Ratio":
                value = Ratio;
                return true;

            case "KneeDb":
                value = KneeDb;
                return true;

            case "AttackSeconds":
                value = AttackSeconds;
                return true;

            case "ReleaseSeconds":
                value = ReleaseSeconds;
                return true;

            case "MakeupDb":
                value = MakeupDb;
                return true;

            default:
                value = 0f;
                return false;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Properties => Knobs;

    static readonly string[] Knobs = ["ThresholdDb", "Ratio", "KneeDb", "AttackSeconds", "ReleaseSeconds", "MakeupDb"];

    /// <summary>The one-pole coefficient for a time constant, at this sample rate.</summary>
    /// <remarks>
    ///     <c>exp(-1 / (t · fs))</c> — the standard form, which makes the time the point at which the
    ///     envelope has covered 63 % of the distance. Zero means instant, and is allowed: a ducking
    ///     compressor with a zero attack is a legitimate setting.
    /// </remarks>
    float Coefficient(float seconds) =>
        seconds <= 0f ? 0f : MathF.Exp(-1f / (MathF.Max(seconds, 1e-6f) * sampleRate));
}

/// <summary>Between a linear gain and its value in decibels.</summary>
/// <remarks>
///     Public because every mixer surface a human touches is calibrated in decibels and every buffer
///     is not, so the conversion belongs somewhere both an effect and an editor slider can reach it.
/// </remarks>
public static class Decibels {
    /// <summary>What a decibel value is as a multiplier.</summary>
    /// <param name="decibels">The value. 0 is unity, −6 is about half.</param>
    /// <returns>The linear gain.</returns>
    public static float ToLinear(float decibels) => MathF.Pow(10f, decibels * 0.05f);

    /// <summary>What a multiplier is in decibels.</summary>
    /// <param name="linear">The gain. Zero and below give −∞, reported as −120.</param>
    /// <returns>The value in decibels.</returns>
    /// <remarks>
    ///     Floored at −120 rather than returning negative infinity: −120 dB is a millionth of full
    ///     scale, which is below the noise floor of any format, and an infinity that reaches an
    ///     envelope makes every subsequent sample a NaN.
    /// </remarks>
    public static float FromLinear(float linear) => linear <= 1e-6f ? -120f : 20f * MathF.Log10(linear);
}
