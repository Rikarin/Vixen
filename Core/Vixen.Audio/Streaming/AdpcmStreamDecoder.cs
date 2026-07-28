// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Streaming;

/// <summary>ADPCM behind the same seam as Vorbis and Opus, so nothing above it has to care.</summary>
/// <remarks>
///     <para>
///         <b>It needs no third-party package, which is most of the point.</b> The codec is a table
///         and twenty lines, so this lives in <c>Vixen.Audio</c> rather than in
///         <c>Vixen.Audio.Codecs</c> — a game that wants four-to-one on its effects should not have to
///         take a dependency to get it, and every platform can decode it including the browser.
///     </para>
///     <para>
///         <b>Seeking is exact and cheap, unlike the other two.</b> Vorbis has to bisect on page
///         granules and Opus decodes forward from the start; a block here begins with the sample and
///         step it starts from, so a seek is a division. That is what makes this the format for sounds
///         that start at unpredictable moments, which is what a game's effects are.
///     </para>
/// </remarks>
public sealed class AdpcmStreamDecoder : IAudioStreamDecoder {
    readonly byte[] blocks;
    readonly int samplesPerBlock;
    readonly int blockBytes;
    readonly float[] decoded;

    int blockIndex = -1;
    int withinBlock;
    long position;

    /// <summary>A decoder over compressed blocks already in memory.</summary>
    /// <param name="data">The blocks, back to back.</param>
    /// <param name="format">What they decode to.</param>
    /// <param name="samplesPerBlock">How many frames each block holds.</param>
    /// <param name="frameCount">How many frames are real, since the last block is padded.</param>
    /// <exception cref="ArgumentNullException"><paramref name="data" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The block size is not one this format has.</exception>
    public AdpcmStreamDecoder(byte[] data, in AudioFormat format, int samplesPerBlock, long frameCount = -1) {
        ArgumentNullException.ThrowIfNull(data);

        if (samplesPerBlock < 2 || samplesPerBlock % 2 == 0) {
            throw new ArgumentOutOfRangeException(
                nameof(samplesPerBlock),
                samplesPerBlock,
                "A block holds its first sample whole and the rest in pairs, so the count is odd."
            );
        }

        blocks = data;
        Format = format;
        this.samplesPerBlock = samplesPerBlock;
        blockBytes = Adpcm.BlockBytes(samplesPerBlock, format.Channels);
        decoded = new float[samplesPerBlock * format.Channels];

        var whole = blockBytes > 0 ? data.Length / blockBytes : 0;
        FrameCount = frameCount >= 0 ? frameCount : (long)whole * samplesPerBlock;
    }

    /// <inheritdoc />
    public AudioFormat Format { get; }

    /// <inheritdoc />
    public long FrameCount { get; }

    /// <inheritdoc />
    public long Position => position;

    /// <inheritdoc />
    public bool CanSeek => true;

    /// <inheritdoc />
    public int Decode(Span<float> destination, int frameCount) {
        var channels = Format.Channels;
        var wanted = Math.Min(frameCount, destination.Length / channels);
        var written = 0;

        while (written < wanted && position < FrameCount) {
            var block = (int)(position / samplesPerBlock);

            if (block != blockIndex && !Load(block)) {
                break;
            }

            withinBlock = (int)(position % samplesPerBlock);
            var taking = Math.Min(wanted - written, samplesPerBlock - withinBlock);
            taking = (int)Math.Min(taking, FrameCount - position);

            if (taking <= 0) {
                break;
            }

            decoded.AsSpan(withinBlock * channels, taking * channels)
                .CopyTo(destination[(written * channels)..]);

            written += taking;
            position += taking;
        }

        return written;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     A division and a modulo. Every block carries the state it starts from, so there is nothing
    ///     to decode forward through and nothing to converge — which is the whole reason the header
    ///     costs four bytes a block.
    /// </remarks>
    public void Seek(long frame) => position = Math.Clamp(frame, 0, FrameCount);

    /// <inheritdoc />
    public void Dispose() { }

    /// <summary>Decodes one whole block into the holding buffer.</summary>
    bool Load(int block) {
        var at = block * blockBytes;

        if (at < 0 || at + blockBytes > blocks.Length) {
            return false;
        }

        Adpcm.Decompress(blocks.AsSpan(at, blockBytes), Format.Channels, samplesPerBlock, decoded);
        blockIndex = block;
        return true;
    }
}
