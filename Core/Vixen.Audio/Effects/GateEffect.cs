// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Effects;

/// <summary>Turns the signal down when there is nothing worth hearing in it.</summary>
/// <remarks>
///     <para>
///         <b>The other side of the compressor.</b> A compressor bends everything <em>above</em> a
///         threshold towards a lower level; a gate bends everything <em>below</em> one towards
///         silence. Same detector, same attack and release, mirrored gain computer — which is why it
///         is a hundred lines and not a subsystem.
///     </para>
///     <para>
///         <b>What it is actually for is an open microphone.</b> Thirty players in a voice session
///         are thirty room tones, thirty fans and at least one mechanical keyboard, summed. Each is
///         inaudible alone and the sum is a wash that never stops. Nothing else in the mixer removes
///         it: a fader would take the speech with it, and a filter would only make it duller.
///     </para>
///     <para>
///         <b><see cref="HoldSeconds" /> is not optional.</b> Speech dips below any useful threshold
///         between syllables, and a gate with no hold slams shut in the gaps — which is the chattering
///         that makes gated dialogue sound worse than ungated. The hold keeps it open through the dip
///         and the release closes it gently afterwards.
///     </para>
///     <para>
///         <b><see cref="RangeDb" /> rather than silence.</b> A gate that closes to nothing is
///         obvious, because the room tone it was hiding stops dead the moment somebody speaks and
///         returns when they finish. Closing to −40 or −60 dB removes the wash and leaves the
///         transition inaudible, which is what a broadcast gate does and why they are set that way.
///     </para>
///     <para>
///         It is an <see cref="ISidechainEffect" />, so a bus can be gated by <em>another</em> bus —
///         which is how a music bed is opened only while a radio channel is transmitting.
///     </para>
/// </remarks>
public sealed class GateEffect : ISidechainEffect {
    const float Floor = 1e-6f;

    float envelopeDb = -120f;
    float gainDb;
    float holdRemaining;
    int sampleRate;
    int channelCount;

    /// <summary>The level below which the gate starts closing.</summary>
    /// <remarks>
    ///     Somewhere between the noise and the speech, which for a consumer headset is usually around
    ///     −45 dB. Too high and quiet words are cut off; too low and the gate never closes.
    /// </remarks>
    public float ThresholdDb { get; set; } = -45f;

    /// <summary>How far below the threshold it must fall before the gate is fully shut.</summary>
    /// <remarks>
    ///     A soft edge, and the reason a gate is an expander rather than a switch. Six decibels of
    ///     range means a signal at the threshold is untouched and one six decibels under is closed;
    ///     between them it is somewhere in between, so a word tailing off fades rather than stops.
    /// </remarks>
    public float KneeDb { get; set; } = 6f;

    /// <summary>How far down a closed gate goes. Negative decibels; zero would be no gate at all.</summary>
    public float RangeDb { get; set; } = -60f;

    /// <summary>How quickly it opens.</summary>
    /// <remarks>
    ///     Fast, because the thing that opens it is the start of a word and the start of a word is
    ///     the part that says which word it is. A millisecond or two; much slower and consonants lose
    ///     their edge.
    /// </remarks>
    public float AttackSeconds { get; set; } = 0.002f;

    /// <summary>How long it stays open after the signal has fallen back below the threshold.</summary>
    public float HoldSeconds { get; set; } = 0.15f;

    /// <summary>How slowly it closes once the hold has run out.</summary>
    public float ReleaseSeconds { get; set; } = 0.2f;

    /// <inheritdoc />
    public bool Enabled { get; set; } = true;

    /// <summary>How far down the gate has the signal as of the end of the last block. Zero is wide open.</summary>
    /// <remarks>
    ///     <b>Where it ended, not the worst it got</b> — which is the opposite of
    ///     <c>CompressorEffect.GainReductionDb</c>, deliberately. A compressor's meter answers "how
    ///     hard did it have to work", so the extreme is the interesting number; a gate's answers
    ///     "where is it now", and a block in which it opened would otherwise report the closed value
    ///     it started from.
    /// </remarks>
    public float GainReductionDb { get; private set; }

    /// <summary>Whether the detector says somebody is talking.</summary>
    /// <remarks>
    ///     <para>
    ///         The threshold and the hold, and not the gain — so it is true the moment a word starts
    ///         rather than once the attack has finished, and it stays true through a gap without
    ///         waiting on the release. That makes it a genuine voice-activity flag rather than a
    ///         reading of the effect's own smoothing.
    ///     </para>
    ///     <para>
    ///         Useful well outside the mixer: it is what a name plate lights up from, and what a
    ///         client uses to decide whether to send a packet at all.
    ///     </para>
    /// </remarks>
    public bool IsOpen { get; private set; }

    /// <inheritdoc />
    public void Prepare(in AudioFormat format, int maxFrames) {
        sampleRate = format.SampleRate;
        channelCount = format.Channels;
        Reset();
    }

    /// <inheritdoc />
    /// <remarks>Keyed by the signal being gated, which is an ordinary gate.</remarks>
    public void Process(Span<float> buffer, int frameCount, int channels) =>
        Process(buffer, buffer, frameCount, channels);

    /// <inheritdoc />
    public void Process(Span<float> buffer, ReadOnlySpan<float> key, int frameCount, int channels) {
        if (!Enabled || channels != channelCount || sampleRate <= 0) {
            return;
        }

        var attack = Coefficient(AttackSeconds);
        var release = Coefficient(ReleaseSeconds);
        var hold = MathF.Max(HoldSeconds, 0f);
        var threshold = ThresholdDb;
        var knee = MathF.Max(KneeDb, 0.01f);
        var range = MathF.Min(RangeDb, 0f);
        var step = 1f / sampleRate;

        for (var frame = 0; frame < frameCount; frame++) {
            var offset = frame * channels;
            var loudest = 0f;

            for (var channel = 0; channel < channels; channel++) {
                loudest = MathF.Max(loudest, MathF.Abs(key[offset + channel]));
            }

            var levelDb = Decibels.FromLinear(MathF.Max(loudest, Floor));

            // The detector rises instantly and falls at the release rate, so a transient opens the
            // gate on the sample it arrives rather than after a smoothing lag. The attack below is
            // what shapes the opening; smoothing here as well would apply it twice.
            envelopeDb = levelDb > envelopeDb ? levelDb : levelDb + ((envelopeDb - levelDb) * release);

            // How far under the gate the signal is, softened across the knee so the edge is a slope
            // rather than a switch: at the threshold nothing happens, a knee below it the gate is
            // shut, and in between it is proportionally closed.
            var under = threshold - envelopeDb;
            var closed = Math.Clamp(under / knee, 0f, 1f);
            var wanted = closed * range;

            if (under <= 0f) {
                holdRemaining = hold;
            } else if (holdRemaining > 0f) {
                // Still inside the hold: the gate does not begin to close, however far the signal has
                // fallen. This is what carries a voice through the gap between two syllables.
                holdRemaining -= step;
                wanted = 0f;
            }

            // Opening uses the attack and closing the release, which is the opposite way round from a
            // compressor for the same reason: what the two are doing is mirrored.
            gainDb = wanted > gainDb
                ? wanted + ((gainDb - wanted) * attack)
                : wanted + ((gainDb - wanted) * release);

            var gain = Decibels.ToLinear(gainDb);

            for (var channel = 0; channel < channels; channel++) {
                buffer[offset + channel] *= gain;
            }
        }

        GainReductionDb = gainDb;
        IsOpen = envelopeDb >= threshold || holdRemaining > 0f;
    }

    /// <inheritdoc />
    public void Reset() {
        envelopeDb = -120f;

        // Shut, not open. A gate that resets open passes one release-time's worth of whatever is in
        // the room at the moment a scene loads, which is exactly the sound it exists to remove.
        gainDb = MathF.Min(RangeDb, 0f);
        holdRemaining = 0f;
        GainReductionDb = gainDb;
        IsOpen = false;
    }

    /// <inheritdoc />
    public bool TrySetProperty(string name, float value) {
        switch (name) {
            case "ThresholdDb":
                ThresholdDb = value;
                return true;

            case "KneeDb":
                KneeDb = value;
                return true;

            case "RangeDb":
                RangeDb = value;
                return true;

            case "AttackSeconds":
                AttackSeconds = value;
                return true;

            case "HoldSeconds":
                HoldSeconds = value;
                return true;

            case "ReleaseSeconds":
                ReleaseSeconds = value;
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

            case "KneeDb":
                value = KneeDb;
                return true;

            case "RangeDb":
                value = RangeDb;
                return true;

            case "AttackSeconds":
                value = AttackSeconds;
                return true;

            case "HoldSeconds":
                value = HoldSeconds;
                return true;

            case "ReleaseSeconds":
                value = ReleaseSeconds;
                return true;

            default:
                value = 0f;
                return false;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Properties => Knobs;

    static readonly string[] Knobs = ["ThresholdDb", "KneeDb", "RangeDb", "AttackSeconds", "HoldSeconds", "ReleaseSeconds"];

    float Coefficient(float seconds) =>
        seconds <= 0f ? 0f : MathF.Exp(-1f / (MathF.Max(seconds, 1e-6f) * sampleRate));
}
