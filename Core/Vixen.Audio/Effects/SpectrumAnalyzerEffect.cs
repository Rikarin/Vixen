// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Dsp;

namespace Vixen.Audio.Effects;

/// <summary>Measures what is going through a bus, and changes none of it.</summary>
/// <remarks>
///     <para>
///         An effect that is not an effect: it passes the signal through untouched and publishes its
///         spectrum. <c>docs/plan/13</c> asks the audio overlay for mixer levels, which
///         <c>AudioBus.PeakLevel</c> already answers; this is the other half — <em>which</em>
///         frequencies, which is what turns "the mix is muddy" from an opinion into a picture. It is
///         also what a music visualiser reads.
///     </para>
///     <para>
///         <b>Windowed, because a transform assumes what it is given repeats forever.</b> A block cut
///         out of a continuous signal almost never joins up with itself, and the discontinuity at the
///         seam smears energy across every bin — a pure tone appears as a tone plus a wide skirt of
///         nothing. A Hann window fades the block in and out so the seam is silent, at the cost of
///         spreading each real peak over about three bins. That trade is the reason every analyser
///         ever written applies a window.
///     </para>
///     <para>
///         <b>Published through a sequence lock.</b> The audio thread fills the magnitudes and the
///         game thread copies them; neither waits, and a reader that catches a write in progress
///         retries a few times and then keeps last frame's picture. The same mechanism
///         <c>Published&lt;T&gt;</c> uses for a voice's spatial settings, and for the same reason —
///         an array of five hundred floats cannot be written atomically.
///     </para>
///     <para>
///         <b>The channels are summed before the transform.</b> A spectrum per channel would be two
///         pictures nobody wants to look at, and the interesting question — what is in the mix — is
///         about all of it.
///     </para>
/// </remarks>
public sealed class SpectrumAnalyzerEffect : IAudioEffect {
    readonly Fft fft;
    readonly float[] window;
    readonly float[] accumulator;
    readonly float[] real;
    readonly float[] imaginary;
    readonly float[] magnitudes;
    readonly float[] published;

    int written;
    int sequence;
    int sampleRate;

    /// <summary>An analyser of a size.</summary>
    /// <param name="size">
    ///     How many samples each picture is taken from. A power of two, 1 024 by default — which at
    ///     48 kHz is 21 ms of sound and about 47 Hz of resolution, the usual compromise between
    ///     telling two bass notes apart and keeping up with the music.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">The size is not a power of two.</exception>
    public SpectrumAnalyzerEffect(int size = 1_024) {
        fft = new Fft(size);
        window = new float[size];
        accumulator = new float[size];
        real = new float[size];
        imaginary = new float[size];
        magnitudes = new float[(size / 2) + 1];
        published = new float[magnitudes.Length];

        // Hann. Its skirt falls away faster than a rectangle's by about sixty decibels, which is the
        // difference between seeing a quiet tone next to a loud one and not.
        for (var i = 0; i < size; i++) {
            window[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (size - 1)));
        }
    }

    /// <summary>How many samples each picture is taken from.</summary>
    public int Size => fft.Size;

    /// <summary>How many magnitudes there are — the bins from zero up to Nyquist.</summary>
    public int BinCount => magnitudes.Length;

    /// <summary>How many hertz apart the bins are.</summary>
    public float BinWidthHz => sampleRate > 0 ? (float)sampleRate / fft.Size : 0f;

    /// <summary>How much of the previous picture each new one keeps, from 0 to just under 1.</summary>
    /// <remarks>
    ///     A visualiser driven by raw transforms flickers, because consecutive blocks of real music
    ///     genuinely differ that much. Smoothing is what makes it readable; too much and it stops
    ///     responding to the music at all. 0.6 is a reasonable place to start.
    /// </remarks>
    public float Smoothing { get; set; } = 0.6f;

    /// <inheritdoc />
    public bool Enabled { get; set; } = true;

    /// <summary>Copies the latest magnitudes out, if they can be read cleanly.</summary>
    /// <param name="destination">Where to put them. At least <see cref="BinCount" /> long.</param>
    /// <returns>
    ///     Whether a consistent picture was read. False means the audio thread was mid-publish and
    ///     the caller should keep whatever it drew last frame.
    /// </returns>
    /// <remarks>
    ///     Linear magnitudes, not decibels: a caller that wants a logarithmic display converts, and
    ///     one that wants to sum bands cannot do it after a logarithm.
    /// </remarks>
    public bool TryCopyTo(Span<float> destination) {
        if (destination.Length < magnitudes.Length) {
            return false;
        }

        for (var attempt = 0; attempt < 4; attempt++) {
            var before = Volatile.Read(ref sequence);

            if ((before & 1) != 0) {
                continue;
            }

            published.AsSpan().CopyTo(destination);
            Interlocked.MemoryBarrier();

            if (Volatile.Read(ref sequence) == before) {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public void Prepare(in AudioFormat format, int maxFrames) {
        sampleRate = format.SampleRate;
        Reset();
    }

    /// <inheritdoc />
    public void Process(Span<float> buffer, int frameCount, int channels) {
        if (!Enabled) {
            return;
        }

        for (var frame = 0; frame < frameCount; frame++) {
            var offset = frame * channels;
            var summed = 0f;

            for (var channel = 0; channel < channels; channel++) {
                summed += buffer[offset + channel];
            }

            accumulator[written++] = summed / channels;

            if (written < accumulator.Length) {
                continue;
            }

            Analyse();
            written = 0;
        }
    }

    /// <inheritdoc />
    public void Reset() {
        Array.Clear(accumulator);
        Array.Clear(magnitudes);
        written = 0;

        // Published between two increments like any other write, so a reader mid-copy notices.
        Interlocked.Increment(ref sequence);
        Array.Clear(published);
        Interlocked.Increment(ref sequence);
    }

    /// <inheritdoc />
    public bool TrySetProperty(string name, float value) {
        switch (name) {
            case "Smoothing":
                Smoothing = value;
                return true;

            default:
                return false;
        }
    }

    /// <inheritdoc />
    public bool TryGetProperty(string name, out float value) {
        switch (name) {
            case "Smoothing":
                value = Smoothing;
                return true;

            default:
                value = 0f;
                return false;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Properties => Knobs;

    static readonly string[] Knobs = ["Smoothing"];

    void Analyse() {
        for (var i = 0; i < accumulator.Length; i++) {
            real[i] = accumulator[i] * window[i];
            imaginary[i] = 0f;
        }

        fft.Forward(real, imaginary);

        // Two corrections, both of which a picture is wrong without. The window throws away about
        // half the energy, and every bin but the two ends shares its content with its mirror on the
        // other side of Nyquist — so a tone at unity reads as unity rather than as a quarter of it.
        var scale = 2f / (accumulator.Length * 0.5f);
        var smoothing = Math.Clamp(Smoothing, 0f, 0.999f);

        for (var bin = 0; bin < magnitudes.Length; bin++) {
            var value = MathF.Sqrt((real[bin] * real[bin]) + (imaginary[bin] * imaginary[bin])) * scale;

            if (bin == 0 || bin == magnitudes.Length - 1) {
                value *= 0.5f;
            }

            magnitudes[bin] = (magnitudes[bin] * smoothing) + (value * (1f - smoothing));
        }

        Interlocked.Increment(ref sequence);
        magnitudes.AsSpan().CopyTo(published);
        Interlocked.Increment(ref sequence);
    }
}
