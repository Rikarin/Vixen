// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Dsp;

namespace Vixen.Audio.Effects;

/// <summary>Pitch shifting in the frequency domain, where a sustained note can survive it.</summary>
/// <remarks>
///     <para>
///         <b>Why there are two pitch shifters, stated more carefully than it usually is.</b>
///         <see cref="PitchShiftEffect" /> reads the same buffer at two rates and crossfades between
///         them. The received wisdom is that this warbles on sustained tones and that a phase vocoder
///         fixes it — and measured against a held sawtooth, that is simply not true: the crossfade put
///         0.00% of its energy off the harmonic grid where this put 1.45%. A two-tap shifter reading a
///         <em>stationary periodic</em> signal is very nearly exact, because both taps sit on the same
///         repeating waveform.
///     </para>
///     <para>
///         <b>Where it does fall down is material that does not repeat.</b> Speech, vibrato, a note
///         bending, anything whose partials are moving — there the two taps are reading genuinely
///         different waveforms and the crossfade between them is a smear. That is what this is for,
///         and it is a judgement about how something sounds rather than a number a test can settle,
///         so it is written here rather than asserted somewhere.
///     </para>
///     <para>
///         So: reach for <see cref="PitchShiftEffect" /> for steady material, for anything that has
///         to be sample-accurate, and when a window of latency is unacceptable. Reach for this one for
///         voices and for music.
///     </para>
///     <para>
///         <b>What this does instead.</b> Transform each hop, and for every bin work out what
///         frequency the partial in it <em>actually</em> is — the phase moved further between hops
///         than the bin's centre frequency would explain, and the excess says by how much. Move those
///         partials to their new frequencies, then advance each one's phase by what its new frequency
///         demands rather than by what it happened to arrive with. That last step is the whole
///         technique: it keeps the partials of a note coherent from hop to hop instead of letting
///         them drift apart, which is what "phase vocoder" names.
///     </para>
///     <para>
///         <b>It costs a window of latency, and there is no version that does not.</b> Nothing can be
///         said about a frequency until enough of a cycle has been seen to measure it.
///         <see cref="Latency" /> is that, in frames — about 43 ms at the default size and 48 kHz.
///         For dialogue, monsters and music it is nothing; for a sound that has to land on a frame it
///         is disqualifying, and that is when the time-domain one is still the right answer.
///     </para>
///     <para>
///         <b>Transients still need help, and get it.</b> Phase coherence is what a phase vocoder is
///         for, and it is exactly wrong at an onset — a drum hit or a hard consonant has no steady
///         partials to keep coherent, and carrying the previous frame's phase across it smears the
///         attack into the classic watery vocoder sound. So a jump in spectral energy resets the
///         accumulated phase to what actually arrived, which puts the transient back where it was.
///         <see cref="TransientSensitivity" /> is how big a jump has to be.
///     </para>
/// </remarks>
public sealed class PitchVocoderEffect : IAudioEffect {
    /// <summary>How much of each window overlaps the next. Four is the usual compromise.</summary>
    /// <remarks>
    ///     Fewer overlaps is cheaper and audibly grainier; more is smoother and costs proportionally.
    ///     Four is what almost every implementation settles on, and the window below is chosen so that
    ///     four of them overlap-add to a constant.
    /// </remarks>
    const int Overlap = 4;

    RealFft? fft;
    float[] window = [];
    float[] real = [];
    float[] imaginary = [];
    float[] frame = [];
    float[] magnitude = [];
    float[] frequency = [];
    float[] synthMagnitude = [];
    float[] synthFrequency = [];

    float[] inFifo = [];
    float[] outFifo = [];
    float[] accumulator = [];
    float[] lastPhase = [];
    float[] sumPhase = [];
    float[] lastEnergy = [];

    int size;
    int hop;
    int bins;
    int channelCount;
    int rover;

    /// <summary>How far to shift, in semitones. Twelve is an octave up.</summary>
    public float Semitones { get; set; }

    /// <summary>How many points each transform covers. A power of two.</summary>
    /// <remarks>
    ///     <b>The trade is frequency resolution against time resolution, and it is unavoidable.</b> A
    ///     larger window separates partials that are close together — which is what a low male voice
    ///     or a chord needs — and smears anything that happens quickly. 2048 at 48 kHz is 23 Hz of
    ///     resolution and 43 ms of window, which suits speech and most music. Take it down for
    ///     percussive material and up for anything with a very low fundamental.
    /// </remarks>
    public int FftSize {
        get => requestedSize;
        set => requestedSize = Math.Clamp(Fft.NextSize(value), 256, 8_192);
    }

    int requestedSize = 2_048;

    /// <summary>How much of a jump in energy counts as a transient, as a ratio against the last frame.</summary>
    /// <remarks>
    ///     Two means "twice as loud as the frame before it". Lower resets phase more often, which
    ///     protects attacks and gives up some of the smoothness this effect exists for; zero turns
    ///     transient handling off entirely and is how to hear what it was doing.
    /// </remarks>
    public float TransientSensitivity { get; set; } = 2f;

    /// <summary>How much of the shifted signal to mix with the original.</summary>
    public float Mix { get; set; } = 1f;

    /// <inheritdoc />
    public bool Enabled { get; set; } = true;

    /// <summary>The shift as a frequency ratio.</summary>
    public float Ratio => MathF.Pow(2f, Math.Clamp(Semitones, -24f, 24f) / 12f);

    /// <summary>How many frames behind the input the output is.</summary>
    public int Latency => size > 0 ? size - hop : 0;

    /// <inheritdoc />
    public void Prepare(in AudioFormat format, int maxFrames) {
        channelCount = format.Channels;
        size = requestedSize;
        hop = size / Overlap;
        fft = new RealFft(size);
        bins = fft.Bins;

        real = new float[bins];
        imaginary = new float[bins];
        frame = new float[size];
        magnitude = new float[bins];
        frequency = new float[bins];
        synthMagnitude = new float[bins];
        synthFrequency = new float[bins];

        inFifo = new float[channelCount * size];
        outFifo = new float[channelCount * size];
        accumulator = new float[channelCount * size * 2];
        lastPhase = new float[channelCount * bins];
        sumPhase = new float[channelCount * bins];
        lastEnergy = new float[channelCount];

        // A Hann window, applied on the way in and again on the way out. Windowing twice is what
        // makes the overlap-add reconstruct exactly: four hops of a squared Hann sum to a constant,
        // where four hops of a plain one do not.
        window = new float[size];

        for (var i = 0; i < size; i++) {
            window[i] = 0.5f - (0.5f * MathF.Cos(2f * MathF.PI * i / size));
        }

        Reset();
    }

    /// <inheritdoc />
    public void Process(Span<float> buffer, int frameCount, int channels) {
        if (!Enabled || fft is null || channels != channelCount || size <= 0) {
            return;
        }

        var ratio = Ratio;
        var mix = Math.Clamp(Mix, 0f, 1f);

        // Nothing to do, and doing it anyway would still cost a window of latency — so a shift of
        // zero is left alone rather than run through as a no-op.
        if (MathF.Abs(ratio - 1f) < 1e-4f) {
            return;
        }

        var latency = size - hop;

        for (var i = 0; i < frameCount; i++) {
            for (var channel = 0; channel < channels; channel++) {
                var index = (i * channels) + channel;
                var dry = buffer[index];
                var offset = channel * size;

                inFifo[offset + rover] = dry;
                var wet = outFifo[offset + rover - latency];
                buffer[index] = dry + ((wet - dry) * mix);
            }

            if (++rover < size) {
                continue;
            }

            rover = latency;

            for (var channel = 0; channel < channels; channel++) {
                Step(channel, ratio);
            }
        }
    }

    /// <summary>One analysis and synthesis hop for one channel.</summary>
    void Step(int channel, float ratio) {
        var offset = channel * size;
        var binOffset = channel * bins;
        var expected = 2f * MathF.PI * hop / size;
        var rate = size / (float)hop;

        for (var i = 0; i < size; i++) {
            frame[i] = inFifo[offset + i] * window[i];
        }

        fft!.Forward(frame, real, imaginary);

        var energy = 0f;

        // ── Analysis: what frequency is each partial really at ────────────────────────────────
        for (var k = 0; k < bins; k++) {
            var magnitudeAt = 2f * MathF.Sqrt((real[k] * real[k]) + (imaginary[k] * imaginary[k]));
            var phase = MathF.Atan2(imaginary[k], real[k]);
            energy += magnitudeAt;

            // How much further the phase moved than this bin's own centre frequency accounts for.
            // That difference is the partial's offset from the bin, and it is the only place the
            // true frequency is written down.
            var delta = phase - lastPhase[binOffset + k] - (k * expected);
            lastPhase[binOffset + k] = phase;

            // Wrapped to ±π, because a phase that advanced by 3π is indistinguishable from one that
            // advanced by −π and the smaller answer is the one that is physically happening.
            delta = Wrap(delta);

            magnitude[k] = magnitudeAt;
            frequency[k] = k + (rate * delta / (2f * MathF.PI));
        }

        // A jump in energy is an onset. Phase coherence is what this effect is for and it is exactly
        // wrong across one — there are no steady partials to keep coherent, and carrying the old
        // phase over smears the attack.
        var transient = TransientSensitivity > 0f
            && lastEnergy[channel] > 1e-6f
            && energy > lastEnergy[channel] * TransientSensitivity;

        lastEnergy[channel] = energy;

        // ── Synthesis: move every partial, and keep what lands ────────────────────────────────
        Array.Clear(synthMagnitude);
        Array.Clear(synthFrequency);

        for (var k = 0; k < bins; k++) {
            var target = (int)(k * ratio);

            if (target >= bins) {
                break;
            }

            // Accumulated rather than assigned: shifting down puts several partials in one bin, and
            // dropping all but the last would lose most of the signal's energy.
            synthMagnitude[target] += magnitude[k];
            synthFrequency[target] = frequency[k] * ratio;
        }

        for (var k = 0; k < bins; k++) {
            // The phase this partial should have, given where it now is — not the phase it arrived
            // with. This is the step that keeps a held note coherent instead of letting its partials
            // wander apart into the watery sound a naive shifter makes.
            var advance = ((synthFrequency[k] - k) * 2f * MathF.PI / rate) + (k * expected);

            sumPhase[binOffset + k] = transient
                ? lastPhase[binOffset + k]
                : Wrap(sumPhase[binOffset + k] + advance);

            var built = sumPhase[binOffset + k];
            real[k] = synthMagnitude[k] * MathF.Cos(built) * 0.5f;
            imaginary[k] = synthMagnitude[k] * MathF.Sin(built) * 0.5f;
        }

        // Both ends of the spectrum are real for a real signal, and leaving an imaginary part on
        // either puts a DC offset or a Nyquist buzz into the output.
        imaginary[0] = 0f;
        imaginary[bins - 1] = 0f;

        fft.Inverse(real, imaginary, frame);

        var accumulatorOffset = channel * size * 2;

        for (var i = 0; i < size; i++) {
            // Windowed again on the way out, and scaled so that Overlap of them sum to unity.
            accumulator[accumulatorOffset + i] += 2f * window[i] * frame[i] / (Overlap * 0.5f);
        }

        for (var i = 0; i < hop; i++) {
            outFifo[offset + i] = accumulator[accumulatorOffset + i];
        }

        Array.Copy(accumulator, accumulatorOffset + hop, accumulator, accumulatorOffset, size);
        Array.Clear(accumulator, accumulatorOffset + size, hop);
        Array.Copy(inFifo, offset + hop, inFifo, offset, size - hop);
    }

    static float Wrap(float phase) {
        var turns = (int)(phase / MathF.PI);
        turns += turns >= 0 ? turns & 1 : -(turns & 1);
        return phase - (MathF.PI * turns);
    }

    /// <inheritdoc />
    public void Reset() {
        Array.Clear(inFifo);
        Array.Clear(outFifo);
        Array.Clear(accumulator);
        Array.Clear(lastPhase);
        Array.Clear(sumPhase);
        Array.Clear(lastEnergy);
        rover = size > 0 ? size - hop : 0;
    }

    /// <inheritdoc />
    public bool TrySetProperty(string name, float value) {
        switch (name) {
            case "Semitones": Semitones = value; return true;
            case "TransientSensitivity": TransientSensitivity = value; return true;
            case "Mix": Mix = value; return true;
            default: return false;
        }
    }

    /// <inheritdoc />
    public bool TryGetProperty(string name, out float value) {
        switch (name) {
            case "Semitones": value = Semitones; return true;
            case "TransientSensitivity": value = TransientSensitivity; return true;
            case "Mix": value = Mix; return true;
            default: value = 0f; return false;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Properties => Knobs;

    static readonly string[] Knobs = ["Semitones", "TransientSensitivity", "Mix"];
}
