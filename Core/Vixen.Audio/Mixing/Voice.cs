// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Effects;
using Vixen.Audio.Sources;
using Vixen.Audio.Spatial;

namespace Vixen.Audio.Mixing;

/// <summary>One sound being played: a source, a rate conversion, and a set of speaker gains.</summary>
/// <remarks>
///     <para>
///         <b>Everything is allocated in <see cref="Prepare" /> and nothing in
///         <see cref="Render" />.</b> A voice is a fixed slot in a pool that exists for the mixer's
///         whole life, so a game that plays ten thousand sounds allocates for none of them.
///     </para>
///     <para>
///         <b>Linear interpolation for the rate conversion.</b> It is two multiplies a sample and it
///         aliases above about a quarter of Nyquist when pitching up hard. That is the trade every
///         game mixer makes for per-voice resampling, and the mitigation is on the other side: the
///         content build resamples clips to the rate they will be played at, so the common ratio is
///         exactly one and the interpolator is bypassed by the arithmetic itself. A windowed-sinc
///         resampler for the pitched cases is owed and slots in behind this same loop.
///     </para>
/// </remarks>
sealed class Voice {
    // 256 frames is enough that the provider is asked for work in useful blocks and small enough
    // that sixty-four voices' buffers are half a megabyte rather than eight.
    const int ReadFrames = 256;
    const double MinRatio = 1.0 / 64.0;
    const double MaxRatio = 64.0;

    readonly float[] read = new float[ReadFrames * AudioFormat.MaxChannels];
    readonly float[] currentGains = new float[AudioFormat.MaxChannels];
    readonly float[] targetGains = new float[AudioFormat.MaxChannels];
    readonly float[] sample = new float[AudioFormat.MaxChannels];

    float[] previous = new float[AudioFormat.MaxChannels];
    float[] next = new float[AudioFormat.MaxChannels];

    Published<SpatialSettings> published;
    SpatialSettings spatial = new();

    int outputChannels;
    int outputRate;
    int sourceChannels;
    int readAvailable;
    int readCursor;
    double ratioBase;
    double fraction;
    bool sourceEnded;
    bool ended;
    bool downmix;
    bool ramped;

    BiquadCoefficients absorption = BiquadCoefficients.Identity;
    float absorptionZ1;
    float absorptionZ2;
    float absorptionHz;

    /// <summary>Which use of this slot the handle must name to reach it.</summary>
    public int Generation;

    /// <summary>The slot's state, as an <see cref="int" /> so it can be moved with a compare-and-swap.</summary>
    /// <remarks>
    ///     The one field two threads write. Every transition is a
    ///     <see cref="Interlocked.CompareExchange(ref int, int, int)" />, so a stop racing a natural
    ///     end resolves to one of them rather than to both.
    /// </remarks>
    public int State;

    /// <summary>Where the samples come from.</summary>
    public IAudioSampleProvider? Source;

    /// <summary>Which bus it sums into.</summary>
    public int Bus;

    /// <summary>Its own gain, before the bus's.</summary>
    public float Gain = 1f;

    /// <summary>Its playback rate multiplier.</summary>
    public float Pitch = 1f;

    /// <summary>Where it sits between the speakers when it is not spatialised.</summary>
    public float Pan;

    /// <summary>Whether it is placed in the world.</summary>
    public bool IsSpatial;

    /// <summary>Whether finishing this voice should also dispose its source.</summary>
    /// <remarks>
    ///     True for a stream the engine built out of a decoder, false for a provider a caller handed
    ///     in. Owning what you made and not what you were given is the whole of the rule.
    /// </remarks>
    public bool OwnsSource;

    /// <summary>How hard it is to take this voice's slot. Higher survives.</summary>
    public int Priority;

    /// <summary>What the spatialiser last worked out, for the audio debug overlay.</summary>
    public SpatialResult LastSpatial = new(0f, 1f, 1f, 1f);

    /// <summary>The source a steal has lined up, to be taken on by the audio thread.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Why the handoff is deferred rather than done where the steal is decided.</b> A
    ///         stolen voice is, by definition, one the audio thread may be in the middle of
    ///         rendering. Swapping <see cref="Source" /> from the game thread would leave
    ///         <see cref="readAvailable" /> and <see cref="readCursor" /> describing the old
    ///         provider's buffer and the new provider's channel count — an index out of range on the
    ///         audio thread, in a callback, which is a crash rather than a glitch.
    ///     </para>
    ///     <para>
    ///         So the game thread only fills these fields and asks for the stop. The audio thread
    ///         finishes the fade, and at the point where it would have marked the slot
    ///         <see cref="VoiceState.Finished" /> it picks the pending source up instead. That is the
    ///         one moment nothing is reading the render state.
    ///     </para>
    /// </remarks>
    public IAudioSampleProvider? PendingSource;

    /// <summary>Whether the pending source is the engine's to dispose.</summary>
    public bool PendingOwnsSource;

    /// <summary>Whether the pending start is paused.</summary>
    public bool PendingPaused;

    /// <summary>Non-zero when a steal is waiting to be taken on. Written last, read first.</summary>
    public int StealPending;

    /// <summary>What the steal displaced, for the game thread to dispose. Never touched by the audio thread twice.</summary>
    public IAudioSampleProvider? RetiredSource;

    /// <summary>Where in the world it is, as the audio thread last managed to read it.</summary>
    public SpatialSettings Spatial => spatial;

    /// <summary>How far through the source it is.</summary>
    public long Position => Source?.Position ?? 0;

    /// <summary>Publishes new spatial settings from the game thread.</summary>
    /// <param name="settings">Where the sound is, and how it behaves there.</param>
    public void PublishSpatial(in SpatialSettings settings) => published.Write(settings);

    /// <summary>
    ///     How loud this voice actually is right now, spatialisation included — what a steal
    ///     compares.
    /// </summary>
    /// <remarks>
    ///     The voice's own gain multiplied by what the spatialiser last worked out, so a sound that
    ///     is technically at full volume two hundred metres away scores as the near-silence it is.
    ///     Read from the game thread while the audio thread writes it: every term is a float, so the
    ///     worst case is scoring against last block's distance, which is exactly as good.
    /// </remarks>
    public float Audibility => IsSpatial
        ? Gain * LastSpatial.Attenuation * LastSpatial.ConeGain
        : Gain;

    /// <summary>Takes on a source a steal lined up, if there is one.</summary>
    /// <param name="paused">Whether the new sound should start held.</param>
    /// <returns>Whether the slot was reused rather than finished.</returns>
    /// <remarks>Runs on the audio thread, at the one moment nothing is reading the render state.</remarks>
    public bool TryTakePending(out bool paused) {
        paused = false;

        if (Volatile.Read(ref StealPending) == 0) {
            return false;
        }

        // Handed to the game thread rather than dropped here: the last reference to a provider going
        // away on the audio thread means a finaliser, and possibly a file handle, on the audio thread.
        RetiredSource = OwnsSource ? Source : null;

        Source = PendingSource;
        OwnsSource = PendingOwnsSource;
        paused = PendingPaused;
        PendingSource = null;

        Begin();
        Volatile.Write(ref StealPending, 0);
        return true;
    }

    /// <summary>Sizes the voice for a device. Called once, before anything plays.</summary>
    /// <param name="format">The device's format.</param>
    public void Prepare(in AudioFormat format) {
        outputChannels = format.Channels;
        outputRate = format.SampleRate;
    }

    /// <summary>Readies the voice to play its source from where the source currently is.</summary>
    /// <remarks>Runs on the audio thread, when the start command is drained.</remarks>
    public void Begin() {
        var source = Source;

        if (source is null) {
            ended = true;
            return;
        }

        sourceChannels = source.Format.Channels;
        ratioBase = outputRate > 0 ? (double)source.Format.SampleRate / outputRate : 1.0;

        // A source that does not match the output channel-for-channel, and any source being placed
        // in the world, is summed to one channel and then panned. A stereo sound cannot be at a
        // point in space and still be two points, and the two ways of pretending otherwise — pick a
        // channel, or spatialise each separately — are both worse than summing.
        downmix = IsSpatial || sourceChannels != outputChannels;

        readAvailable = 0;
        readCursor = 0;
        fraction = 0.0;
        sourceEnded = false;
        ended = false;
        ramped = false;
        absorption = BiquadCoefficients.Identity;
        absorptionZ1 = 0f;
        absorptionZ2 = 0f;
        absorptionHz = 0f;
        Array.Clear(currentGains);
        Array.Clear(previous);
        Array.Clear(next);

        if (!ReadFrame(previous)) {
            ended = true;
            return;
        }

        if (!ReadFrame(next)) {
            Array.Clear(next);
            sourceEnded = true;
        }
    }

    /// <summary>Adds this voice's contribution to a bus buffer.</summary>
    /// <param name="destination">The bus's interleaved accumulator.</param>
    /// <param name="frameCount">How many frames.</param>
    /// <param name="listener">Where the ears are, for a spatialised voice.</param>
    /// <returns>Whether the voice is still alive. False means it has run out and should be collected.</returns>
    public bool Render(Span<float> destination, int frameCount, in AudioListener listener) {
        var state = (VoiceState)Volatile.Read(ref State);

        if (state is VoiceState.Paused) {
            return true;
        }

        if (ended || state is not (VoiceState.Playing or VoiceState.Stopping)) {
            return !ended;
        }

        var ratio = ComputeTargetGains(listener, state);
        var channels = outputChannels;

        // The gains move across the block rather than jumping at its edge. A step in gain is a step
        // in the waveform, and a step in the waveform is a click — which is what a source moving
        // quickly past the listener would otherwise produce a hundred times a second.
        var inverse = 1f / frameCount;

        for (var frame = 0; frame < frameCount; frame++) {
            if (ended) {
                break;
            }

            var t = frame * inverse;
            var position = (float)fraction;

            for (var channel = 0; channel < sourceChannels; channel++) {
                sample[channel] = previous[channel]
                    + ((next[channel] - previous[channel]) * position);
            }

            var offset = frame * channels;

            if (downmix) {
                var summed = 0f;

                for (var channel = 0; channel < sourceChannels; channel++) {
                    summed += sample[channel];
                }

                summed /= sourceChannels;

                // Air absorption, and the one place a voice filters anything. It sits here rather
                // than on the mono sum's way out because it is a property of the path from the source
                // to the ears, so it belongs before the sound is spread across the speakers — filter
                // after panning and the two channels get two independent filters chasing one distance.
                if (absorptionHz > 0f) {
                    var filtered = (absorption.B0 * summed) + absorptionZ1;
                    absorptionZ1 = (absorption.B1 * summed) - (absorption.A1 * filtered) + absorptionZ2;
                    absorptionZ2 = (absorption.B2 * summed) - (absorption.A2 * filtered);
                    summed = filtered;
                }

                for (var channel = 0; channel < channels; channel++) {
                    destination[offset + channel] += summed * Ramp(channel, t);
                }
            } else {
                for (var channel = 0; channel < channels; channel++) {
                    destination[offset + channel] += sample[channel] * Ramp(channel, t);
                }
            }

            fraction += ratio;

            while (fraction >= 1.0) {
                fraction -= 1.0;

                if (!Advance()) {
                    ended = true;
                    break;
                }
            }
        }

        Array.Copy(targetGains, currentGains, channels);

        if (state is VoiceState.Stopping) {
            // One block of ramp to zero, and then it is over however much source was left.
            return false;
        }

        return !ended;
    }

    /// <summary>Drops the source and readies the slot for reuse.</summary>
    public void Reset() {
        Source = null;
        ended = true;
        sourceEnded = true;
        readAvailable = 0;
        readCursor = 0;
        fraction = 0.0;
        Gain = 1f;
        Pitch = 1f;
        Pan = 0f;
        Bus = 0;
        IsSpatial = false;
        OwnsSource = false;
        spatial = new SpatialSettings();
        published.Write(spatial);
        LastSpatial = new SpatialResult(0f, 1f, 1f, 1f);
        Array.Clear(currentGains);
        Array.Clear(targetGains);
    }

    float Ramp(int channel, float t) =>
        currentGains[channel] + ((targetGains[channel] - currentGains[channel]) * t);

    double ComputeTargetGains(in AudioListener listener, VoiceState state) {
        var channels = outputChannels;
        var ratio = ratioBase * Math.Clamp(Pitch, (float)MinRatio, (float)MaxRatio);

        if (IsSpatial) {
            // A failed read means the game thread was mid-write; the settings from the previous
            // block are used instead, which is one block — ten milliseconds — of staleness.
            published.TryRead(ref spatial);
            var result = Spatializer.Evaluate(listener, spatial, channels, targetGains);
            LastSpatial = result;
            ratio *= result.DopplerRatio;
            SetAbsorption(result.LowPassHz);

            for (var channel = 0; channel < channels; channel++) {
                targetGains[channel] *= Gain;
            }
        } else if (downmix) {
            // A mono source spread across speakers: constant power, so crossing the centre does not
            // dip.
            var pan = Math.Clamp(Pan, -1f, 1f);
            var angle = (pan + 1f) * (MathF.PI * 0.25f);
            Array.Clear(targetGains);
            targetGains[0] = MathF.Cos(angle) * Gain;

            if (channels > 1) {
                targetGains[1] = MathF.Sin(angle) * Gain;
            }
        } else {
            // A source whose channels already match the output: this is a balance, not a pan. At the
            // centre a stereo file must come out at unity, and equal-power panning would put it at
            // 0.707 — quieter than the same file played with no pan control at all.
            var pan = Math.Clamp(Pan, -1f, 1f);
            Array.Clear(targetGains);
            targetGains[0] = Math.Min(1f, 1f - pan) * Gain;

            if (channels > 1) {
                targetGains[1] = Math.Min(1f, 1f + pan) * Gain;
            }

            for (var channel = 2; channel < channels; channel++) {
                targetGains[channel] = Gain;
            }
        }

        if (state is VoiceState.Stopping) {
            Array.Clear(targetGains);
        }

        if (!ramped) {
            // The ramp exists to smooth *changes*. Ramping up from zero on the first block would put
            // a sixty-four-frame fade-in on every sound in the game, which is audible on anything
            // percussive and is not what a caller asking for a gain of one meant.
            Array.Copy(targetGains, currentGains, channels);
            ramped = true;
        }

        return Math.Clamp(ratio, MinRatio, MaxRatio);
    }

    /// <summary>Retunes the air-absorption filter, if distance has moved it far enough to matter.</summary>
    /// <remarks>
    ///     <para>
    ///         Redesigned per block and not per sample: a biquad costs a handful of transcendentals to
    ///         design and five multiplies to run, and a listener cannot move far enough in ten
    ///         milliseconds for the difference to be audible.
    ///     </para>
    ///     <para>
    ///         The two per cent threshold is what stops a source drifting by a centimetre a frame from
    ///         redesigning the filter sixty times a second for a cutoff nobody could hear move. The
    ///         state is deliberately <em>not</em> cleared on a retune — the filter's memory is the
    ///         signal that was passing through it, and throwing it away is a click.
    ///     </para>
    /// </remarks>
    void SetAbsorption(float hertz) {
        if (hertz <= 0f) {
            absorptionHz = 0f;
            return;
        }

        if (absorptionHz > 0f && MathF.Abs(hertz - absorptionHz) < absorptionHz * 0.02f) {
            return;
        }

        absorptionHz = hertz;
        absorption = BiquadCoefficients.Design(BiquadFilterKind.LowPass, outputRate, hertz);
    }

    bool Advance() {
        if (sourceEnded) {
            return false;
        }

        (previous, next) = (next, previous);

        if (!ReadFrame(next)) {
            Array.Clear(next);
            sourceEnded = true;
        }

        return true;
    }

    bool ReadFrame(float[] destination) {
        var source = Source;

        if (source is null) {
            return false;
        }

        if (readCursor >= readAvailable) {
            // ReadFrames, not the buffer's whole capacity in frames. The buffer is sized for 256
            // frames at the widest channel count, so a mono source could be asked for 2 048 — which
            // is efficient for a clip and wrong for anything live: a provider that answers an
            // underrun with silence would have 2 048 frames of it committed before it looked at its
            // ring again, which is forty milliseconds of a voice-chat packet arriving too late to
            // matter.
            readAvailable = source.Read(read, ReadFrames);
            readCursor = 0;

            if (readAvailable <= 0) {
                return false;
            }
        }

        Array.Copy(read, readCursor * sourceChannels, destination, 0, sourceChannels);
        readCursor++;
        return true;
    }
}
