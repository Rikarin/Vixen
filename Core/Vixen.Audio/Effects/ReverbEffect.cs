// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Effects;

/// <summary>A room, built out of eight comb filters and four allpasses per channel.</summary>
/// <remarks>
///     <para>
///         Jezar's Freeverb, which is public domain and is the reverb in a surprising share of
///         everything. It was chosen over a convolution reverb for the reason a game engine always
///         chooses it: an impulse response is a per-room asset somebody has to author and a
///         partitioned convolution to run it, where this is four hundred lines of adds and one knob
///         a level designer understands. A convolution reverb is a later, larger effect that plugs
///         into the same <see cref="IAudioEffect" />.
///     </para>
///     <para>
///         <b>The delay lengths are prime-ish and mutually irrational on purpose.</b> Comb filters at
///         related lengths reinforce each other into a ringing pitch instead of a diffuse tail, which
///         is why these particular numbers and not round ones. They are quoted at 44 100 Hz, as
///         Freeverb defines them, and scaled to whatever rate the device opened at — a reverb that
///         ignored the rate would be a third shorter at 48 kHz and audibly different on two machines.
///     </para>
///     <para>
///         <b>The stereo image comes from a 23-sample offset per channel</b>, not from two different
///         reverbs. Beyond two channels each one gets its own offset and is processed independently;
///         <see cref="Width" /> only means anything with exactly two, where there is a left and a
///         right to cross-feed.
///     </para>
/// </remarks>
public sealed class ReverbEffect : IAudioEffect {
    static readonly int[] CombTuning = [1_116, 1_188, 1_277, 1_356, 1_422, 1_491, 1_557, 1_617];
    static readonly int[] AllpassTuning = [556, 441, 341, 225];

    const int StereoSpread = 23;
    const int ReferenceRate = 44_100;
    const float FixedGain = 0.015f;
    const float RoomScale = 0.28f;
    const float RoomOffset = 0.7f;
    const float DampScale = 0.4f;
    const float AllpassFeedback = 0.5f;

    Comb[,] combs = new Comb[0, 0];
    Allpass[,] allpasses = new Allpass[0, 0];
    int channelCount;

    /// <summary>How big the room is, from a cupboard at 0 to a cathedral at 1.</summary>
    public float RoomSize { get; set; } = 0.5f;

    /// <summary>How fast the high frequencies die away, from a tiled bathroom at 0 to a carpeted one at 1.</summary>
    public float Damping { get; set; } = 0.5f;

    /// <summary>How much of the reverberated signal to add.</summary>
    /// <remarks>
    ///     Defaults to a modest 0.3 with <see cref="Dry" /> left at 1. An insert effect that silenced
    ///     what was routed into it the moment it was added would be a trap, and every reverb anybody
    ///     has ever put on a bus was meant to be added to the sound rather than to replace it.
    /// </remarks>
    public float Wet { get; set; } = 0.3f;

    /// <summary>How much of the untouched signal to keep.</summary>
    public float Dry { get; set; } = 1f;

    /// <summary>How wide the stereo image of the tail is, from mono at 0 to fully spread at 1.</summary>
    public float Width { get; set; } = 1f;

    /// <inheritdoc />
    public bool Enabled { get; set; } = true;

    /// <inheritdoc />
    public void Prepare(in AudioFormat format, int maxFrames) {
        channelCount = format.Channels;
        var scale = (double)format.SampleRate / ReferenceRate;

        combs = new Comb[channelCount, CombTuning.Length];
        allpasses = new Allpass[channelCount, AllpassTuning.Length];

        for (var channel = 0; channel < channelCount; channel++) {
            var spread = channel * StereoSpread;

            for (var i = 0; i < CombTuning.Length; i++) {
                combs[channel, i] = new Comb(Scaled(CombTuning[i] + spread, scale));
            }

            for (var i = 0; i < AllpassTuning.Length; i++) {
                allpasses[channel, i] = new Allpass(Scaled(AllpassTuning[i] + spread, scale));
            }
        }
    }

    /// <inheritdoc />
    public void Process(Span<float> buffer, int frameCount, int channels) {
        if (!Enabled || channels != channelCount || channelCount == 0) {
            return;
        }

        var feedback = (Math.Clamp(RoomSize, 0f, 1f) * RoomScale) + RoomOffset;
        var damp = Math.Clamp(Damping, 0f, 1f) * DampScale;
        var wet = Math.Max(Wet, 0f);
        var dry = Math.Max(Dry, 0f);
        var width = Math.Clamp(Width, 0f, 1f);

        // The cross-feed that makes two channels a stereo image rather than two mono reverbs. With
        // one channel there is nothing to cross, and with more than two there is no agreed pairing,
        // so both fall back to the straight wet signal.
        var stereo = channels == 2;
        var wet1 = stereo ? wet * ((width * 0.5f) + 0.5f) : wet;
        var wet2 = stereo ? wet * (1f - width) * 0.5f : 0f;

        Span<float> tail = stackalloc float[AudioFormat.MaxChannels];

        for (var frame = 0; frame < frameCount; frame++) {
            var offset = frame * channels;
            var input = 0f;

            for (var channel = 0; channel < channels; channel++) {
                input += buffer[offset + channel];
            }

            input *= FixedGain;

            for (var channel = 0; channel < channels; channel++) {
                var accumulated = 0f;

                for (var i = 0; i < CombTuning.Length; i++) {
                    accumulated += combs[channel, i].Process(input, feedback, damp);
                }

                for (var i = 0; i < AllpassTuning.Length; i++) {
                    accumulated = allpasses[channel, i].Process(accumulated);
                }

                tail[channel] = accumulated;
            }

            for (var channel = 0; channel < channels; channel++) {
                var other = stereo ? tail[1 - channel] : 0f;
                buffer[offset + channel] = (tail[channel] * wet1) + (other * wet2)
                    + (buffer[offset + channel] * dry);
            }
        }
    }

    /// <inheritdoc />
    public void Reset() {
        for (var channel = 0; channel < channelCount; channel++) {
            for (var i = 0; i < CombTuning.Length; i++) {
                combs[channel, i].Reset();
            }

            for (var i = 0; i < AllpassTuning.Length; i++) {
                allpasses[channel, i].Reset();
            }
        }
    }

    static int Scaled(int tuning, double scale) => Math.Max(1, (int)Math.Round(tuning * scale));

    /// <summary>A damped feedback comb: the part that makes the tail last.</summary>
    sealed class Comb(int length) {
        readonly float[] line = new float[length];
        float store;
        int index;

        public float Process(float input, float feedback, float damp) {
            var output = line[index];

            // Denormals in a decaying tail cost two orders of magnitude on x86, and a reverb that
            // has faded to inaudible is exactly where they accumulate. Flushing here is cheaper than
            // any of the ways of turning them off.
            store = (output * (1f - damp)) + (store * damp);

            if (float.Abs(store) < 1e-20f) {
                store = 0f;
            }

            line[index] = input + (store * feedback);

            if (++index >= line.Length) {
                index = 0;
            }

            return output;
        }

        public void Reset() {
            Array.Clear(line);
            store = 0f;
            index = 0;
        }
    }

    /// <summary>An allpass: the part that turns a set of echoes into a smear.</summary>
    sealed class Allpass(int length) {
        readonly float[] line = new float[length];
        int index;

        public float Process(float input) {
            var buffered = line[index];
            var output = buffered - input;
            line[index] = input + (buffered * AllpassFeedback);

            if (++index >= line.Length) {
                index = 0;
            }

            return output;
        }

        public void Reset() {
            Array.Clear(line);
            index = 0;
        }
    }
}
