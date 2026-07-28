// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Dsp;
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
    readonly float[] listenerGains = new float[AudioFormat.MaxChannels];

    // The rate conversion's window. Sixteen frames deep, so a windowed sinc has something to look
    // at; linear only ever reads the two either side of the point, which are slots Half − 1 and Half.
    readonly float[] history = new float[SincTable.Taps * AudioFormat.MaxChannels];
    int historyHead;

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
    bool interpolating;
    double interpolationRatio = 1.0;
    int drain;

    BiquadCoefficients absorption = BiquadCoefficients.Identity;
    float absorptionZ1;
    float absorptionZ2;
    float absorptionHz;

    // The authored filter, which is a different thing from air absorption and so is a different
    // filter. Absorption is worked out from distance and is nobody's decision; this one is a
    // parameter curve saying "underwater" or "through a wall", and the two compose.
    readonly float[] lowZ1 = new float[AudioFormat.MaxChannels];
    readonly float[] lowZ2 = new float[AudioFormat.MaxChannels];
    readonly float[] highZ1 = new float[AudioFormat.MaxChannels];
    readonly float[] highZ2 = new float[AudioFormat.MaxChannels];
    BiquadCoefficients low = BiquadCoefficients.Identity;
    BiquadCoefficients high = BiquadCoefficients.Identity;
    float lowHz;
    float highHz;

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

    /// <summary>An extra bus a copy of it also goes to, or −1 for none.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The thing a bus send cannot do.</b> A send on a bus is one amount for everything
    ///         routed through it, which is right for a room's reverb and wrong for a reverb amount
    ///         that tracks how far into the room each emitter is — every source on the bus would move
    ///         together. This is the same edge drawn from one voice.
    ///     </para>
    ///     <para>
    ///         Costs nothing when unused: the mixer only takes the scratch path for a voice that has
    ///         one, so a game that never sets a per-voice send renders exactly as it did before.
    ///     </para>
    /// </remarks>
    public int SendBus = -1;

    /// <summary>How much of it goes to <see cref="SendBus" />, as a linear gain.</summary>
    public float SendLevel;

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

    /// <summary>The device frame at which it should begin. Zero is "as soon as it is seen".</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Absolute, and that is the whole point.</b> A delay counted from when the game thread
    ///         asked would be wrong by however much of a block went by between the ask and the audio
    ///         thread noticing — ten milliseconds at a 480-frame buffer, which is a flam on a musical
    ///         transition and the difference between two sounds and one. An absolute frame is a
    ///         position the audio thread can measure itself against, so the start lands on the sample
    ///         it was asked for however the two threads interleave.
    ///     </para>
    ///     <para>
    ///         A frame already gone by starts the voice at the top of the block it is noticed in,
    ///         which is the best that can be done and is counted as a late start.
    ///     </para>
    /// </remarks>
    public long StartFrame;

    /// <summary>Whether it should keep playing without being heard.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The difference between losing a sound and not rendering it.</b> A stolen voice is
    ///         gone; a virtual one still advances through its source, so the looping ambience a player
    ///         walked away from is at the right place in its loop when they walk back rather than
    ///         restarting or never returning.
    ///     </para>
    ///     <para>
    ///         Set from the game thread by the engine's ranking pass and read here. It works by
    ///         clearing the target gains, exactly as <see cref="VoiceState.Stopping" /> does, so the
    ///         voice fades out over one block and fades back in over one when it returns — no click at
    ///         either end, and no special case in the mixing loop. Once it is fully silent the loop
    ///         stops accumulating and only the source read remains, which is the whole cost of being
    ///         virtual.
    ///     </para>
    /// </remarks>
    public bool Virtual;

    /// <summary>What this voice's parameter automation last worked out, as a linear gain.</summary>
    /// <remarks>
    ///     <para>
    ///         Separate from <see cref="Gain" /> and multiplied with it, rather than folded into it,
    ///         so that the two things writing a voice's level never overwrite each other:
    ///         <c>SetGain</c> and the fades own the first, and <c>AudioEngine.Update</c> owns this. A
    ///         single field would mean a fade cancelling an automation curve or the other way round,
    ///         depending on which ran last.
    ///     </para>
    ///     <para>
    ///         A plain float, written by the game thread and read by the audio thread, like every
    ///         other scalar here — the worst case is a change landing one block late.
    ///     </para>
    /// </remarks>
    public float ParameterGain = 1f;

    /// <summary>What this voice's parameter automation last worked out, as a pitch ratio.</summary>
    public float ParameterPitch = 1f;

    /// <summary>An authored low-pass cutoff in hertz, or zero for none.</summary>
    public float ParameterLowPassHz;

    /// <summary>An authored high-pass cutoff in hertz, or zero for none.</summary>
    public float ParameterHighPassHz;

    /// <summary>How much solid geometry is between this voice and the listener: 0 clear, 1 blocked.</summary>
    /// <remarks>
    ///     <para>
    ///         Written by <see cref="AudioOcclusion" /> on the game thread and read by the parameter
    ///         automation on the same thread, so unlike the fields around it this one never crosses.
    ///         It lives here rather than beside the automation because it is a property of where the
    ///         voice is, and because a stolen slot has to be able to drop it in one place.
    ///     </para>
    ///     <para>
    ///         Nothing acts on it directly. <see cref="Vixen.Audio.Parameters.AudioBuiltinParameter.Occlusion" /> feeds it to
    ///         an authored curve, which decides what being behind a wall sounds like.
    ///     </para>
    /// </remarks>
    public float Occlusion;

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

    /// <summary>What the game thread last published, read back on the game thread.</summary>
    /// <remarks>
    ///     <b>Not the same as <see cref="Spatial" />.</b> That one is the audio thread's copy, taken
    ///     when it last rendered a block — so anything on the game thread reading it sees a position
    ///     from before the most recent <c>Play</c>, or no position at all if nothing has been
    ///     rendered yet. The occlusion pass runs on the game thread and needs the position the game
    ///     thread just set, which is this.
    /// </remarks>
    public SpatialSettings PublishedSpatial {
        get {
            var result = spatial;
            published.TryRead(ref result);
            return result;
        }
    }

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
        ? Gain * ParameterGain * LastSpatial.Attenuation * LastSpatial.ConeGain
        : Gain * ParameterGain;

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
        drain = 0;
        absorption = BiquadCoefficients.Identity;
        absorptionZ1 = 0f;
        absorptionZ2 = 0f;
        absorptionHz = 0f;
        ClearFilters();
        Array.Clear(currentGains);
        Array.Clear(history);
        historyHead = 0;

        // Everything before the sound started is silence, so the taps behind the first sample stay
        // zero. The first real frame goes where the interpolation point sits, and the rest of the
        // window is look-ahead — which is what a sinc costs and is a third of a millisecond.
        if (!ReadFrame(Slot(Centre))) {
            ended = true;
            return;
        }

        for (var tap = Centre + 1; tap < SincTable.Taps; tap++) {
            if (ReadFrame(Slot(tap))) {
                continue;
            }

            Array.Clear(history, Slot(tap), sourceChannels);
            sourceEnded = true;
            break;
        }
    }

    /// <summary>Works out the sample at the current fractional position.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Linear at unity, sinc otherwise.</b> A ratio of exactly one never moves off a
    ///         sample, so both give the source back untouched and the cheaper one wins — and that is
    ///         the common case, because the content build resamples clips to the rate they are played
    ///         at. Anything else is where the difference is: linear pitched up an octave folds the top
    ///         half of the spectrum back over the music, which is the gritty edge every cheap sampler
    ///         has.
    ///     </para>
    ///     <para>
    ///         The window is narrowed when pitching up, so what would have folded is removed before it
    ///         can. The sound loses its top octave — which is exactly what it would have lost had it
    ///         been recorded an octave higher.
    ///     </para>
    /// </remarks>
    void Interpolate() {
        var before = Slot(Centre);

        if (!interpolating) {
            Array.Copy(history, before, sample, 0, sourceChannels);
            return;
        }

        var window = SincTable.Window(fraction, interpolationRatio);

        for (var channel = 0; channel < sourceChannels; channel++) {
            var total = 0f;

            for (var tap = 0; tap < SincTable.Taps; tap++) {
                total += window[tap] * history[Slot(tap) + channel];
            }

            sample[channel] = total;
        }
    }

    /// <summary>Which tap the interpolation point sits just after.</summary>
    const int Centre = (SincTable.Taps / 2) - 1;

    /// <summary>Where a tap's frame is in the flat history, oldest first.</summary>
    int Slot(int tap) => ((historyHead + tap) % SincTable.Taps) * AudioFormat.MaxChannels;

    /// <summary>Adds this voice's contribution to a bus buffer.</summary>
    /// <param name="destination">The bus's interleaved accumulator.</param>
    /// <param name="frameCount">How many frames.</param>
    /// <param name="listeners">Where the ears are, for a spatialised voice.</param>
    /// <param name="blockStart">The device frame this block begins at, for a scheduled start.</param>
    /// <returns>Whether the voice is still alive. False means it has run out and should be collected.</returns>
    public bool Render(Span<float> destination, int frameCount, in AudioListenerSet listeners, long blockStart) {
        var state = (VoiceState)Volatile.Read(ref State);

        if (state is VoiceState.Paused) {
            return true;
        }

        if (ended || state is not (VoiceState.Playing or VoiceState.Stopping)) {
            return !ended;
        }

        var waiting = StartFrame - blockStart;

        if (waiting > 0) {
            if (waiting >= frameCount) {
                // Its moment is not in this block at all. Alive but silent, and not yet advancing —
                // a scheduled sound has not begun, so it cannot have ended.
                return true;
            }

            // Its moment is part way through. Everything before it belongs to whatever else is on
            // this bus, so the voice is handed the tail of the block and told it is that long.
            destination = destination[((int)waiting * outputChannels)..];
            frameCount -= (int)waiting;
        }

        var ratio = ComputeTargetGains(listeners, state);

        // Off at unity, which is bit-exact passthrough and the case the content build arranges for.
        interpolating = ratio != 1.0;
        interpolationRatio = ratio;
        var channels = outputChannels;
        var filtering = RetuneFilters();

        // Nothing this voice produces can reach the bus, so everything that produces it is skipped —
        // the interpolation, the filters and the accumulate. What is not skipped is the source read
        // below, which is the playhead advancing and is the entire point of a virtual voice.
        var silent = IsSilent(channels);

        // The gains move across the block rather than jumping at its edge. A step in gain is a step
        // in the waveform, and a step in the waveform is a click — which is what a source moving
        // quickly past the listener would otherwise produce a hundred times a second.
        var inverse = 1f / frameCount;

        for (var frame = 0; frame < frameCount; frame++) {
            if (ended) {
                break;
            }

            if (silent) {
                Step(ratio);
                continue;
            }

            var t = frame * inverse;
            Interpolate();

            // Before the downmix and before the panning, on the source's own channels. After the
            // pan a stereo output would run two independent filters over one signal, and the two
            // would disagree the moment either was retuned; before the downmix a stereo source keeps
            // its two filters over its two genuinely different channels, which is what it wants.
            if (filtering) {
                Filter();
            }

            var offset = frame * channels;

            if (downmix) {
                var summed = 0f;

                for (var channel = 0; channel < sourceChannels; channel++) {
                    summed += sample[channel];
                }

                summed /= sourceChannels;

                // Air absorption, which is the distance filter rather than the authored one above.
                // It sits on the mono sum rather than after the panning because it is a property of
                // the path from the source to the ears — filter after panning and the two channels
                // get two independent filters chasing one distance.
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

            Step(ratio);
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
        ParameterGain = 1f;
        ParameterPitch = 1f;
        ParameterLowPassHz = 0f;
        ParameterHighPassHz = 0f;
        Occlusion = 0f;
        SendBus = -1;
        SendLevel = 0f;
        ClearFilters();
        IsSpatial = false;
        OwnsSource = false;
        Virtual = false;
        StartFrame = 0;
        spatial = new SpatialSettings();
        published.Write(spatial);
        LastSpatial = new SpatialResult(0f, 1f, 1f, 1f);
        Array.Clear(currentGains);
        Array.Clear(targetGains);
    }

    /// <summary>Moves the read position on by one output frame's worth of source.</summary>
    void Step(double ratio) {
        fraction += ratio;

        while (fraction >= 1.0) {
            fraction -= 1.0;

            if (!Advance()) {
                ended = true;
                return;
            }
        }
    }

    bool IsSilent(int channels) {
        for (var channel = 0; channel < channels; channel++) {
            if (currentGains[channel] != 0f || targetGains[channel] != 0f) {
                return false;
            }
        }

        return true;
    }

    float Ramp(int channel, float t) =>
        currentGains[channel] + ((targetGains[channel] - currentGains[channel]) * t);

    /// <summary>Redesigns the authored filters if their cutoffs have moved enough to matter.</summary>
    /// <returns>Whether either of them is doing anything.</returns>
    /// <remarks>
    ///     The same two per cent rule as <see cref="SetAbsorption" />, and for the same reason: a
    ///     parameter seeking across its range moves a cutoff by a hair every block, and redesigning a
    ///     biquad sixty times a second for a change nobody can hear is transcendentals nobody asked
    ///     for. The filter state survives a retune — it is the signal that was passing through, and
    ///     clearing it is a click.
    /// </remarks>
    bool RetuneFilters() {
        // Above Nyquist a low-pass is not filtering anything, and designing one there produces
        // coefficients that ring. Treating it as "off" is both cheaper and what the author meant by
        // sweeping the cutoff up out of the way.
        var wantedLow = ParameterLowPassHz;
        var wantedHigh = ParameterHighPassHz;
        var ceiling = outputRate * 0.5f;

        if (wantedLow >= ceiling) {
            wantedLow = 0f;
        }

        if (wantedHigh >= ceiling) {
            wantedHigh = 0f;
        }

        if (wantedLow <= 0f) {
            lowHz = 0f;
        } else if (lowHz <= 0f || MathF.Abs(wantedLow - lowHz) >= lowHz * 0.02f) {
            lowHz = wantedLow;
            low = BiquadCoefficients.Design(BiquadFilterKind.LowPass, outputRate, wantedLow);
        }

        if (wantedHigh <= 0f) {
            highHz = 0f;
        } else if (highHz <= 0f || MathF.Abs(wantedHigh - highHz) >= highHz * 0.02f) {
            highHz = wantedHigh;
            high = BiquadCoefficients.Design(BiquadFilterKind.HighPass, outputRate, wantedHigh);
        }

        return lowHz > 0f || highHz > 0f;
    }

    void Filter() {
        for (var channel = 0; channel < sourceChannels; channel++) {
            var value = sample[channel];

            if (lowHz > 0f) {
                var filtered = (low.B0 * value) + lowZ1[channel];
                lowZ1[channel] = (low.B1 * value) - (low.A1 * filtered) + lowZ2[channel];
                lowZ2[channel] = (low.B2 * value) - (low.A2 * filtered);
                value = filtered;
            }

            if (highHz > 0f) {
                var filtered = (high.B0 * value) + highZ1[channel];
                highZ1[channel] = (high.B1 * value) - (high.A1 * filtered) + highZ2[channel];
                highZ2[channel] = (high.B2 * value) - (high.A2 * filtered);
                value = filtered;
            }

            sample[channel] = value;
        }
    }

    void ClearFilters() {
        low = BiquadCoefficients.Identity;
        high = BiquadCoefficients.Identity;
        lowHz = 0f;
        highHz = 0f;
        Array.Clear(lowZ1);
        Array.Clear(lowZ2);
        Array.Clear(highZ1);
        Array.Clear(highZ2);
    }

    double ComputeTargetGains(in AudioListenerSet listeners, VoiceState state) {
        var channels = outputChannels;

        // The voice's own gain and pitch, times whatever its parameter automation last worked out.
        // Two fields rather than one because two different things write them at two different times;
        // see the note on ParameterGain.
        var gain = Gain * ParameterGain;
        var ratio = ratioBase * Math.Clamp(Pitch * ParameterPitch, (float)MinRatio, (float)MaxRatio);

        if (IsSpatial) {
            // A failed read means the game thread was mid-write; the settings from the previous
            // block are used instead, which is one block — ten milliseconds — of staleness.
            published.TryRead(ref spatial);
            var result = Spatializer.Evaluate(listeners, spatial, channels, targetGains, listenerGains);
            LastSpatial = result;
            ratio *= result.DopplerRatio;
            SetAbsorption(result.LowPassHz);

            for (var channel = 0; channel < channels; channel++) {
                targetGains[channel] *= gain;
            }
        } else if (downmix) {
            // A mono source spread across speakers: constant power, so crossing the centre does not
            // dip.
            var pan = Math.Clamp(Pan, -1f, 1f);
            var angle = (pan + 1f) * (MathF.PI * 0.25f);
            Array.Clear(targetGains);
            targetGains[0] = MathF.Cos(angle) * gain;

            if (channels > 1) {
                targetGains[1] = MathF.Sin(angle) * gain;
            }
        } else {
            // A source whose channels already match the output: this is a balance, not a pan. At the
            // centre a stereo file must come out at unity, and equal-power panning would put it at
            // 0.707 — quieter than the same file played with no pan control at all.
            var pan = Math.Clamp(Pan, -1f, 1f);
            Array.Clear(targetGains);
            targetGains[0] = Math.Min(1f, 1f - pan) * gain;

            if (channels > 1) {
                targetGains[1] = Math.Min(1f, 1f + pan) * gain;
            }

            for (var channel = 2; channel < channels; channel++) {
                targetGains[channel] = gain;
            }
        }

        if (Virtual || state is VoiceState.Stopping) {
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

    /// <summary>How many frames of look-ahead sit between the interpolation point and the newest.</summary>
    const int Lookahead = SincTable.Taps - 2 - Centre;

    /// <summary>Slides the window on by one source frame.</summary>
    /// <returns>Whether there was one, or one still to be walked to.</returns>
    /// <remarks>
    ///     <b>Running out of source is not the end of the sound.</b> The interpolation point sits
    ///     <see cref="Lookahead" /> frames behind the newest, so when a read finally fails there are
    ///     still that many real frames in front of the point — and a voice that stopped there would
    ///     lose the end of every clip it played. Worse, a clip shorter than the window would end
    ///     before it was heard at all, which is what happens if this is left as "no more frames, no
    ///     more sound".
    ///     <para>
    ///         So the window is walked out. The cost is that a voice may render up to
    ///         <see cref="Lookahead" /> frames of silence past its source — a seventh of a millisecond,
    ///         and the price of having a window at all.
    ///     </para>
    /// </remarks>
    bool Advance() {
        if (sourceEnded && drain >= Lookahead) {
            return false;
        }

        // The oldest frame becomes the newest, which is the whole of a circular buffer.
        historyHead = (historyHead + 1) % SincTable.Taps;
        var newest = Slot(SincTable.Taps - 1);

        if (sourceEnded) {
            drain++;
            Array.Clear(history, newest, sourceChannels);
            return true;
        }

        if (!ReadFrame(history, newest)) {
            Array.Clear(history, newest, sourceChannels);
            sourceEnded = true;
        }

        return true;
    }

    bool ReadFrame(int destination) => ReadFrame(history, destination);

    bool ReadFrame(float[] destination, int at) {
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

        Array.Copy(read, readCursor * sourceChannels, destination, at, sourceChannels);
        readCursor++;
        return true;
    }
}
