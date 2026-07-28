// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Numerics;

namespace Vixen.Audio.Dsp;

/// <summary>A fast Fourier transform of a fixed power-of-two size.</summary>
/// <remarks>
///     <para>
///         Two effects need this and neither could exist without it. A convolution reverb against a
///         one-second impulse response is 48 000 multiply-accumulates <em>per sample</em> done
///         directly — about two thousand times more work than a machine has — and the transform is
///         what turns it into a multiply per bin. A spectrum analyser is the transform and nothing
///         else.
///     </para>
///     <para>
///         <b>An object rather than a static method, because the tables are the point.</b> The
///         twiddle factors and the bit-reversal permutation depend only on the size, so they are
///         computed once and the transform itself allocates nothing and calls no transcendental
///         function. A static <c>Fft.Transform(span)</c> would have to rebuild them every block.
///     </para>
///     <para>
///         <b>Radix-2, iterative, decimation in time.</b> The textbook one. Radix-4 is about a third
///         faster and twice the code; a split-radix is faster still and is a research project. The
///         profile that would justify either does not exist yet, and this is not where a game's audio
///         budget goes.
///     </para>
///     <para>
///         <b>Complex in, complex out, even for real signals.</b> Audio is real, so half the input is
///         zeroes and half the output is the mirror of the other half — a real-input transform would
///         be twice as fast for the same answer. That optimisation is owed and is deliberately not
///         taken yet: it doubles the index arithmetic, and index arithmetic is where a transform goes
///         quietly wrong.
///     </para>
/// </remarks>
public sealed class Fft {
    readonly int[] reversed;
    readonly float[] cosines;
    readonly float[] sines;

    /// <summary>A transform of a size.</summary>
    /// <param name="size">How many points. Must be a power of two and at least two.</param>
    /// <exception cref="ArgumentOutOfRangeException">The size is not a power of two, or is too small.</exception>
    public Fft(int size) {
        if (size < 2 || (size & (size - 1)) != 0) {
            throw new ArgumentOutOfRangeException(
                nameof(size),
                size,
                "A radix-2 transform needs a power-of-two size of at least two."
            );
        }

        Size = size;
        reversed = new int[size];
        cosines = new float[size / 2];
        sines = new float[size / 2];

        var bits = BitOperations.Log2((uint)size);

        for (var i = 0; i < size; i++) {
            reversed[i] = (int)(ReverseBits((uint)i) >> (32 - bits));
        }

        for (var i = 0; i < size / 2; i++) {
            var angle = 2.0 * Math.PI * i / size;
            cosines[i] = (float)Math.Cos(angle);
            sines[i] = (float)Math.Sin(angle);
        }
    }

    /// <summary>How many points it transforms.</summary>
    public int Size { get; }

    /// <summary>Transforms in place, time to frequency.</summary>
    /// <param name="real">The real parts. Exactly <see cref="Size" /> long.</param>
    /// <param name="imaginary">The imaginary parts. Zero for a real signal.</param>
    public void Forward(Span<float> real, Span<float> imaginary) => Transform(real, imaginary, inverse: false);

    /// <summary>Transforms in place, frequency to time.</summary>
    /// <param name="real">The real parts.</param>
    /// <param name="imaginary">The imaginary parts.</param>
    /// <remarks>
    ///     Scaled by <c>1 / Size</c>, so a forward followed by an inverse gives back what went in.
    ///     Which convention carries the scaling is arbitrary and every library picks a different one;
    ///     this is the one that makes round-tripping the obvious thing.
    /// </remarks>
    public void Inverse(Span<float> real, Span<float> imaginary) => Transform(real, imaginary, inverse: true);

    void Transform(Span<float> real, Span<float> imaginary, bool inverse) {
        if (real.Length < Size || imaginary.Length < Size) {
            throw new ArgumentException($"A transform of size {Size} needs spans at least that long.", nameof(real));
        }

        // Decimation in time reorders the input by the bit-reversal of its index, after which every
        // butterfly reads adjacent elements. Doing it as a permutation up front is what makes the
        // rest of the loop have no index arithmetic in it at all.
        for (var i = 0; i < Size; i++) {
            var j = reversed[i];

            if (j <= i) {
                continue;
            }

            (real[i], real[j]) = (real[j], real[i]);
            (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
        }

        for (var span = 2; span <= Size; span <<= 1) {
            var half = span >> 1;
            var stride = Size / span;

            for (var start = 0; start < Size; start += span) {
                for (var k = 0; k < half; k++) {
                    var twiddle = k * stride;
                    var wr = cosines[twiddle];

                    // The forward transform's kernel is e^(−2πi k/N) and the inverse's is its
                    // conjugate, which is the only difference between the two directions.
                    var wi = inverse ? sines[twiddle] : -sines[twiddle];

                    var top = start + k;
                    var bottom = top + half;

                    var tr = (real[bottom] * wr) - (imaginary[bottom] * wi);
                    var ti = (real[bottom] * wi) + (imaginary[bottom] * wr);

                    real[bottom] = real[top] - tr;
                    imaginary[bottom] = imaginary[top] - ti;
                    real[top] += tr;
                    imaginary[top] += ti;
                }
            }
        }

        if (!inverse) {
            return;
        }

        var scale = 1f / Size;

        for (var i = 0; i < Size; i++) {
            real[i] *= scale;
            imaginary[i] *= scale;
        }
    }

    static uint ReverseBits(uint value) {
        value = ((value & 0x55555555u) << 1) | ((value >> 1) & 0x55555555u);
        value = ((value & 0x33333333u) << 2) | ((value >> 2) & 0x33333333u);
        value = ((value & 0x0F0F0F0Fu) << 4) | ((value >> 4) & 0x0F0F0F0Fu);
        value = ((value & 0x00FF00FFu) << 8) | ((value >> 8) & 0x00FF00FFu);
        return (value << 16) | (value >> 16);
    }

    /// <summary>The smallest power of two at least as large as a value.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The size to build a transform of.</returns>
    public static int NextSize(int value) {
        var size = 2;

        while (size < value) {
            size <<= 1;
        }

        return size;
    }
}
