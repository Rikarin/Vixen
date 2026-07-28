// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;

namespace Vixen.Audio.Streaming;

/// <summary>Four bits a sample, decoded with an add and a table lookup.</summary>
/// <remarks>
///     <para>
///         <b>It solves a different problem from Vorbis and Opus, which is why it is worth having
///         alongside them.</b> They give ten to one and cost real processor time per voice, plus
///         decoder state per voice, plus a priming delay before the first sample comes out. That is
///         the right trade for a five-minute music track and the wrong one for a footstep. A game has
///         thousands of short sounds — footsteps, impacts, weapon foley, interface clicks — all
///         resident, all starting at unpredictable moments, many playing at once. Sixty-four Opus
///         decoders for that is not a thing anybody does.
///     </para>
///     <para>
///         <b>Four to one, for almost nothing.</b> Each sample is stored as a four-bit difference
///         from the last one, scaled by a step size that the decoder adapts as it goes — loud passages
///         get a coarse step and quiet ones a fine step, which is what "adaptive" names and what makes
///         four bits sound far better than four bits has any right to. Decoding is an add, a shift and
///         two table lookups.
///     </para>
///     <para>
///         <b>Blocks, because a sound has to start instantly.</b> The adaptation makes every sample
///         depend on the one before it, so a stream decoded from the middle would be wrong until it
///         happened to converge. Each block therefore begins with the exact sample and step it starts
///         from, which costs four bytes per channel per block and buys random access — a loop point, a
///         seek, or simply starting the sound at all without decoding from the beginning.
///     </para>
///     <para>
///         <b>This is IMA ADPCM, the one everybody has.</b> Not because it is the best of the family —
///         it is not — but because it is what tools produce, what console SDKs accelerated, and what a
///         <c>.wav</c> file with format tag 0x11 contains. A better-sounding variant nobody can author
///         for is worth less than a good-enough one every pipeline already emits.
///     </para>
/// </remarks>
public static class Adpcm {
    /// <summary>How the step size moves, per four-bit code.</summary>
    /// <remarks>
    ///     Small codes shrink the step and large ones grow it, which is the whole of the adaptation:
    ///     a quiet passage converges on a fine step within a few samples and a transient opens the
    ///     step up fast enough not to clip through the attack.
    /// </remarks>
    static readonly int[] IndexTable = [-1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8];

    /// <summary>The eighty-nine step sizes, which rise by about 11% each and span the whole range.</summary>
    /// <remarks>
    ///     Geometric rather than linear, because loudness is. Eighty-nine of them covers 16-bit range
    ///     at that ratio, and the table is the format — a decoder with a different one decodes noise.
    /// </remarks>
    static readonly int[] StepTable = [
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
        50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307,
        337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1_060, 1_166, 1_282, 1_411, 1_552, 1_707, 1_878, 2_066,
        2_272, 2_499, 2_749, 3_024, 3_327, 3_660, 4_026, 4_428, 4_871, 5_358, 5_894, 6_484, 7_132, 7_845, 8_630, 9_493,
        10_442, 11_487, 12_635, 13_899, 15_289, 16_818, 18_500, 20_350, 22_385, 24_623, 27_086, 29_794, 32_767
    ];

    /// <summary>One channel's running state: where it is and how big its steps are.</summary>
    public struct State {
        /// <summary>The last sample decoded.</summary>
        public int Predictor { get; set; }

        /// <summary>Which entry of the step table it is using.</summary>
        public int Index { get; set; }
    }

    /// <summary>How many bytes a block of a given size takes.</summary>
    /// <param name="samplesPerBlock">Frames in the block, including the one in the header.</param>
    /// <param name="channels">How many channels.</param>
    /// <returns>The size in bytes.</returns>
    public static int BlockBytes(int samplesPerBlock, int channels) =>
        channels * (4 + ((samplesPerBlock - 1) / 2));

    /// <summary>How many frames fit in a block of a given size.</summary>
    /// <param name="blockBytes">The block's size in bytes.</param>
    /// <param name="channels">How many channels.</param>
    /// <returns>The frame count.</returns>
    public static int BlockFrames(int blockBytes, int channels) =>
        channels <= 0 ? 0 : (((blockBytes / channels) - 4) * 2) + 1;

    /// <summary>Turns one four-bit code into the next sample.</summary>
    /// <param name="code">The code, 0 to 15.</param>
    /// <param name="state">The channel's state, advanced by this call.</param>
    /// <returns>The sample, as a 16-bit value.</returns>
    public static int Decode(int code, ref State state) {
        var step = StepTable[state.Index];

        // The reconstruction: step/8 + step/4 per set bit and so on, which is the same as
        // step × (code + 0.5) / 4 done in integers. The +step/8 is the half — without it every
        // decoded sample is biased low and a decoded file drifts quietly towards silence.
        var difference = step >> 3;

        if ((code & 4) != 0) {
            difference += step;
        }

        if ((code & 2) != 0) {
            difference += step >> 1;
        }

        if ((code & 1) != 0) {
            difference += step >> 2;
        }

        state.Predictor += (code & 8) != 0 ? -difference : difference;
        state.Predictor = Math.Clamp(state.Predictor, short.MinValue, short.MaxValue);

        state.Index = Math.Clamp(state.Index + IndexTable[code], 0, StepTable.Length - 1);
        return state.Predictor;
    }

    /// <summary>Turns one sample into a four-bit code, and advances the state exactly as the decoder will.</summary>
    /// <param name="sample">The sample, as a 16-bit value.</param>
    /// <param name="state">The channel's state, advanced by this call.</param>
    /// <returns>The code, 0 to 15.</returns>
    /// <remarks>
    ///     <b>The encoder runs the decoder.</b> It has to: the step adapts on what was actually
    ///     written, so an encoder that tracked the original signal instead would drift out of step
    ///     with every decoder in the world within a few dozen samples.
    /// </remarks>
    public static int Encode(int sample, ref State state) {
        var step = StepTable[state.Index];
        var delta = sample - state.Predictor;
        var code = 0;

        if (delta < 0) {
            code = 8;
            delta = -delta;
        }

        // Three bits of magnitude, greedily: each is worth half the last, which is the same
        // decomposition the decoder reverses.
        if (delta >= step) {
            code |= 4;
            delta -= step;
        }

        step >>= 1;

        if (delta >= step) {
            code |= 2;
            delta -= step;
        }

        step >>= 1;

        if (delta >= step) {
            code |= 1;
        }

        // Advanced through the decoder rather than by hand, so the two can never disagree.
        Decode(code, ref state);
        return code;
    }

    /// <summary>Compresses interleaved audio into blocks.</summary>
    /// <param name="samples">Interleaved, normalised to ±1.</param>
    /// <param name="channels">How many channels are interleaved.</param>
    /// <param name="samplesPerBlock">Frames per block. 505 is what most tools emit for mono.</param>
    /// <returns>The blocks, back to back.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The channel count or block size is not usable.</exception>
    public static byte[] Compress(ReadOnlySpan<float> samples, int channels, int samplesPerBlock = 505) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);

        if (samplesPerBlock < 2 || samplesPerBlock % 2 == 0) {
            throw new ArgumentOutOfRangeException(
                nameof(samplesPerBlock),
                samplesPerBlock,
                "A block holds its first sample whole and the rest in pairs, so the count is odd."
            );
        }

        var frames = samples.Length / channels;
        var blocks = (frames + samplesPerBlock - 1) / samplesPerBlock;
        var blockBytes = BlockBytes(samplesPerBlock, channels);
        var output = new byte[blocks * blockBytes];
        var states = new State[channels];

        for (var block = 0; block < blocks; block++) {
            var start = block * samplesPerBlock;
            var at = block * blockBytes;

            // The header: every block starts from a known sample and a known step, which is what
            // makes a block decodable on its own.
            for (var channel = 0; channel < channels; channel++) {
                var first = At(samples, frames, channels, start, channel);
                states[channel].Predictor = first;
                states[channel].Index = Math.Clamp(states[channel].Index, 0, StepTable.Length - 1);

                BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(at + (channel * 4)), (short)first);
                output[at + (channel * 4) + 2] = (byte)states[channel].Index;
                output[at + (channel * 4) + 3] = 0;
            }

            var payload = at + (channels * 4);

            for (var i = 1; i < samplesPerBlock; i++) {
                for (var channel = 0; channel < channels; channel++) {
                    var sample = At(samples, frames, channels, start + i, channel);
                    var code = Encode(sample, ref states[channel]);
                    var index = payload + (channel * ((samplesPerBlock - 1) / 2)) + ((i - 1) / 2);

                    // Low nibble first, which is what every reader of this format expects.
                    if ((i - 1) % 2 == 0) {
                        output[index] = (byte)code;
                    } else {
                        output[index] |= (byte)(code << 4);
                    }
                }
            }
        }

        return output;
    }

    /// <summary>Decodes one block into interleaved samples.</summary>
    /// <param name="block">The block.</param>
    /// <param name="channels">How many channels.</param>
    /// <param name="samplesPerBlock">How many frames it holds.</param>
    /// <param name="destination">Where they go, normalised to ±1.</param>
    /// <returns>How many frames were written.</returns>
    public static int Decompress(
        ReadOnlySpan<byte> block,
        int channels,
        int samplesPerBlock,
        Span<float> destination
    ) {
        var states = new State[channels];
        var written = 0;

        for (var channel = 0; channel < channels; channel++) {
            states[channel].Predictor = BinaryPrimitives.ReadInt16LittleEndian(block[(channel * 4)..]);
            states[channel].Index = Math.Clamp(block[(channel * 4) + 2], 0, StepTable.Length - 1);
            destination[channel] = states[channel].Predictor / 32_768f;
        }

        written++;
        var payload = channels * 4;
        var perChannel = (samplesPerBlock - 1) / 2;

        for (var i = 1; i < samplesPerBlock; i++) {
            if ((i * channels) + channels > destination.Length) {
                break;
            }

            for (var channel = 0; channel < channels; channel++) {
                var index = payload + (channel * perChannel) + ((i - 1) / 2);

                if (index >= block.Length) {
                    return written;
                }

                var packed = block[index];
                var code = (i - 1) % 2 == 0 ? packed & 0x0F : packed >> 4;
                destination[(i * channels) + channel] = Decode(code, ref states[channel]) / 32_768f;
            }

            written++;
        }

        return written;
    }

    /// <summary>One sample as a 16-bit integer, clamped, with the tail of the last block repeated.</summary>
    static int At(ReadOnlySpan<float> samples, int frames, int channels, int frame, int channel) {
        // A block that runs past the end holds the last real sample rather than silence: a jump to
        // zero is a click, and the samples past the end are never played anyway.
        var clamped = Math.Min(frame, frames - 1);
        var value = clamped < 0 ? 0f : samples[(clamped * channels) + channel];
        return (int)Math.Clamp(MathF.Round(value * 32_767f), short.MinValue, short.MaxValue);
    }
}
