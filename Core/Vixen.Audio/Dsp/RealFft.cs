// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Dsp;

/// <summary>A transform for signals that are real, which is all of them here.</summary>
/// <remarks>
///     <para>
///         <b>Half the work for the identical answer.</b> Audio is real-valued, so a complex
///         transform of <c>N</c> samples is handed <c>N</c> zeroes it multiplies by anyway, and gives
///         back <c>N</c> bins of which the upper half is the conjugate mirror of the lower. This
///         packs the <c>N</c> real samples into an <c>N/2</c>-point complex transform — even samples
///         as the real part, odd as the imaginary — and untangles the result afterwards. Half the
///         butterflies and half the memory.
///     </para>
///     <para>
///         <b>The output is <c>N/2 + 1</c> bins and not <c>N</c>.</b> That is not a truncation; it is
///         everything there is. Bin 0 is DC and bin <c>N/2</c> is Nyquist, both of which are real for
///         a real input, and every bin above <c>N/2</c> is determined by the one below it. A caller
///         that wants the mirror can conjugate.
///     </para>
///     <para>
///         <b>DC and Nyquist share a slot, which is the one trap here.</b> Both are real, so the
///         usual packing puts Nyquist in <c>imaginary[0]</c> where DC's imaginary part would be — it
///         is always zero and the space would otherwise be wasted. This class does <em>not</em> do
///         that: it gives <c>N/2 + 1</c> honest bins, because the packing saves one float and costs
///         everybody who reads the output an explanation. Anything that needs the dense form can
///         still write it.
///     </para>
///     <para>
///         <b>Taken now because there is finally something to spend it on.</b> It was deferred for a
///         long time, and rightly: it is a pure optimisation for an identical result, and it doubles
///         the index arithmetic — which is exactly where a transform goes <em>quietly</em> wrong,
///         producing a spectrum that is subtly incorrect rather than obviously broken. The tests
///         therefore check it against the complex transform bin for bin rather than against
///         hand-worked expectations.
///     </para>
/// </remarks>
public sealed class RealFft {
    readonly Fft half;
    readonly float[] cosines;
    readonly float[] sines;
    readonly float[] evens;
    readonly float[] odds;

    /// <summary>A transform of a size.</summary>
    /// <param name="size">How many real samples. A power of two, at least four.</param>
    /// <exception cref="ArgumentOutOfRangeException">The size is not a power of two, or is too small.</exception>
    public RealFft(int size) {
        if (size < 4 || (size & (size - 1)) != 0) {
            throw new ArgumentOutOfRangeException(
                nameof(size),
                size,
                "A real transform needs a power-of-two size of at least four."
            );
        }

        Size = size;
        Bins = (size / 2) + 1;

        half = new Fft(size / 2);
        evens = new float[size / 2];
        odds = new float[size / 2];
        cosines = new float[(size / 2) + 1];
        sines = new float[(size / 2) + 1];

        // The untangling twiddles. Half a full transform's table, because the forward only ever needs
        // a quarter turn and the inverse a half — and at this size the arithmetic saved by exploiting
        // that further is worth less than the index mistakes it invites.
        for (var i = 0; i <= size / 2; i++) {
            var angle = 2.0 * Math.PI * i / size;
            cosines[i] = (float)Math.Cos(angle);
            sines[i] = (float)Math.Sin(angle);
        }
    }

    /// <summary>How many real samples go in.</summary>
    public int Size { get; }

    /// <summary>How many bins come out: <c>Size / 2 + 1</c>, DC through Nyquist.</summary>
    public int Bins { get; }

    /// <summary>Transforms real samples into a spectrum.</summary>
    /// <param name="samples">Exactly <see cref="Size" /> of them. Not modified.</param>
    /// <param name="real">Where the real parts go. At least <see cref="Bins" /> long.</param>
    /// <param name="imaginary">Where the imaginary parts go. At least <see cref="Bins" /> long.</param>
    /// <exception cref="ArgumentException">A span is the wrong length.</exception>
    public void Forward(ReadOnlySpan<float> samples, Span<float> real, Span<float> imaginary) {
        if (samples.Length < Size) {
            throw new ArgumentException($"A real transform of {Size} needs {Size} samples.", nameof(samples));
        }

        if (real.Length < Bins || imaginary.Length < Bins) {
            throw new ArgumentException($"The spectrum needs {Bins} bins.", nameof(real));
        }

        var n = Size / 2;

        // The packing: even samples become the real part of a half-length complex signal and odd
        // samples its imaginary part. One N/2 transform then contains everything the N-point
        // transform of the original would have, interleaved with itself.
        for (var i = 0; i < n; i++) {
            evens[i] = samples[2 * i];
            odds[i] = samples[(2 * i) + 1];
        }

        half.Forward(evens, odds);

        // Untangling. Z[k] holds the sum of the even-sample transform and i times the odd-sample
        // one; the two are separated by their conjugate symmetry, and then recombined with the
        // twiddle that a decimation-in-time step would have applied.
        for (var k = 0; k <= n / 2; k++) {
            var mirror = (n - k) % n;

            var evenReal = 0.5f * (evens[k] + evens[mirror]);
            var evenImaginary = 0.5f * (odds[k] - odds[mirror]);
            var oddReal = 0.5f * (odds[k] + odds[mirror]);
            var oddImaginary = -0.5f * (evens[k] - evens[mirror]);

            var cos = cosines[k];
            var sin = sines[k];

            var rotatedReal = (oddReal * cos) + (oddImaginary * sin);
            var rotatedImaginary = (oddImaginary * cos) - (oddReal * sin);

            real[k] = evenReal + rotatedReal;
            imaginary[k] = evenImaginary + rotatedImaginary;

            // The upper half comes out of the same arithmetic with the rotation subtracted, so both
            // ends of the spectrum are filled by one pass over a quarter of it.
            if (k > 0 && k < n / 2) {
                real[n - k] = evenReal - rotatedReal;
                imaginary[n - k] = -(evenImaginary - rotatedImaginary);
            }
        }

        // Nyquist: real, and equal to the alternating sum of the input.
        real[n] = evens[0] - odds[0];
        imaginary[n] = 0f;

        // And DC, which the loop above computed from the k = 0 case but which is worth being explicit
        // about: also real, and equal to the plain sum.
        imaginary[0] = 0f;
    }

    /// <summary>Turns a spectrum back into real samples.</summary>
    /// <param name="real">The real parts. At least <see cref="Bins" /> long. Not modified.</param>
    /// <param name="imaginary">The imaginary parts. At least <see cref="Bins" /> long. Not modified.</param>
    /// <param name="samples">Where the samples go. At least <see cref="Size" /> long.</param>
    /// <remarks>Scaled so that a forward followed by an inverse gives back what went in.</remarks>
    /// <exception cref="ArgumentException">A span is the wrong length.</exception>
    public void Inverse(ReadOnlySpan<float> real, ReadOnlySpan<float> imaginary, Span<float> samples) {
        if (real.Length < Bins || imaginary.Length < Bins) {
            throw new ArgumentException($"The spectrum needs {Bins} bins.", nameof(real));
        }

        if (samples.Length < Size) {
            throw new ArgumentException($"A real transform of {Size} produces {Size} samples.", nameof(samples));
        }

        var n = Size / 2;

        // Rebuilding the packed half-length spectrum, which is the untangling run backwards.
        //
        // The forward produced X[k] = E[k] + W^k·O[k], and the upper half it did not store is
        // conj(X[N/2 − k]) by the conjugate symmetry every real signal has. Two equations, two
        // unknowns: E and W^k·O separate by adding and subtracting them.
        for (var k = 0; k < n; k++) {
            var mirror = n - k;

            // conj(X[N/2 − k]). At k = 0 that is Nyquist, which is why the spectrum has to carry it.
            var upperReal = real[mirror];
            var upperImaginary = -imaginary[mirror];

            var evenReal = 0.5f * (real[k] + upperReal);
            var evenImaginary = 0.5f * (imaginary[k] + upperImaginary);
            var rotatedReal = 0.5f * (real[k] - upperReal);
            var rotatedImaginary = 0.5f * (imaginary[k] - upperImaginary);

            // Undoing the rotation is multiplying by W^−k, which is the conjugate twiddle — the same
            // table with the sine's sign flipped.
            var cos = cosines[k];
            var sin = sines[k];
            var oddReal = (rotatedReal * cos) - (rotatedImaginary * sin);
            var oddImaginary = (rotatedImaginary * cos) + (rotatedReal * sin);

            // Z = E + j·O, which repacks the two half-length transforms into the one the complex
            // inverse below will turn back into interleaved samples.
            evens[k] = evenReal - oddImaginary;
            odds[k] = evenImaginary + oddReal;
        }

        half.Inverse(evens, odds);

        for (var i = 0; i < n; i++) {
            samples[2 * i] = evens[i];
            samples[(2 * i) + 1] = odds[i];
        }
    }

    /// <summary>The magnitude of every bin, which is what a spectrum is usually wanted for.</summary>
    /// <param name="real">The real parts.</param>
    /// <param name="imaginary">The imaginary parts.</param>
    /// <param name="magnitudes">Where they go. At least <see cref="Bins" /> long.</param>
    public void Magnitudes(ReadOnlySpan<float> real, ReadOnlySpan<float> imaginary, Span<float> magnitudes) {
        for (var i = 0; i < Bins; i++) {
            magnitudes[i] = MathF.Sqrt((real[i] * real[i]) + (imaginary[i] * imaginary[i]));
        }
    }
}
