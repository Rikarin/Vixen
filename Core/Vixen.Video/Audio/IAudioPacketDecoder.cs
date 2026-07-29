// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using Vixen.Audio;

namespace Vixen.Video.Audio;

/// <summary>Turns one packet of a container's audio track into frames.</summary>
/// <remarks>
///     <para>
///         <b>Not <c>IAudioStreamDecoder</c>, and the difference is the container.</b>
///         <c>Vixen.Audio</c>'s decoder owns a file: it knows where it is, it can seek, it reads its
///         own bytes. A packet decoder owns nothing — it is handed the bytes a demuxer already
///         found and produces the samples in them. That is the shape Opus has and the shape a
///         Matroska audio track needs, and it is why <c>Vixen.Audio.Codecs.OpusPacketDecoder</c>
///         exists in exactly this form already.
///     </para>
///     <para>
///         <b>The engine implements only the uncompressed cases.</b>
///         <see cref="PcmPacketDecoder" /> is here for the same reason
///         <see cref="Codecs.UncompressedVideoCodec" /> is: so the path can be shipped and tested
///         with nothing linked. A WebM with an Opus track is played by handing
///         <see cref="MatroskaAudioStreamDecoder" /> an adapter over <c>Concentus</c>, which is a
///         dozen lines in the assembly that already references it.
///     </para>
/// </remarks>
public interface IAudioPacketDecoder : IDisposable {
    /// <summary>The rate and channel count it produces.</summary>
    AudioFormat Format { get; }

    /// <summary>The most frames one packet can turn into.</summary>
    /// <remarks>
    ///     What the caller sizes its buffer by. Opus's answer is 120 ms at 48 kHz whatever the
    ///     packet actually holds, because the codec will not say until it has looked.
    /// </remarks>
    int MaxFramesPerPacket { get; }

    /// <summary>Decodes a packet.</summary>
    /// <param name="packet">The bytes.</param>
    /// <param name="destination">
    ///     Interleaved floats, at least <see cref="MaxFramesPerPacket" /> × channels long.
    /// </param>
    /// <returns>How many frames were produced.</returns>
    /// <exception cref="InvalidDataException">The packet is not something this decoder can read.</exception>
    int Decode(ReadOnlySpan<byte> packet, Span<float> destination);

    /// <summary>Throws away any decoder state, because the stream has jumped.</summary>
    void Reset();
}

/// <summary>The decoder for audio that was never encoded.</summary>
/// <remarks>
///     <para>
///         Matroska's uncompressed codecs: <c>A_PCM/INT/LIT</c> at 8, 16, 24 or 32 bits, and
///         <c>A_PCM/FLOAT/IEEE</c> at 32 or 64. Big-endian integer PCM — <c>A_PCM/INT/BIG</c> —
///         exists in the specification and is not handled, because nothing has written it since the
///         format's first year and a byte-swapped path nobody exercises is a path that is wrong.
///     </para>
///     <para>
///         <b>8-bit is unsigned and everything else is signed.</b> That is not this module's
///         invention; it is what WAV did in 1991 and what every specification since has inherited.
///         Decoding 8-bit as signed gives a track that is loud, distorted, and centred half a scale
///         off.
///     </para>
/// </remarks>
public sealed class PcmPacketDecoder : IAudioPacketDecoder {
    readonly int bytesPerSample;
    readonly bool isFloat;

    /// <summary>Creates a decoder for a track's stated format.</summary>
    /// <param name="format">Its rate and channel count.</param>
    /// <param name="bitDepth">How many bits one sample takes.</param>
    /// <param name="isFloat">Whether the samples are IEEE floats rather than integers.</param>
    /// <exception cref="ArgumentException">The format or the depth is not one that exists.</exception>
    public PcmPacketDecoder(AudioFormat format, int bitDepth, bool isFloat) {
        if (!format.IsValid) {
            throw new ArgumentException(
                $"{format.SampleRate} Hz across {format.Channels} channels is not a format.",
                nameof(format)
            );
        }

        var valid = isFloat ? bitDepth is 32 or 64 : bitDepth is 8 or 16 or 24 or 32;

        if (!valid) {
            throw new ArgumentException(
                $"{bitDepth}-bit {(isFloat ? "float" : "integer")} PCM is not a thing.",
                nameof(bitDepth)
            );
        }

        Format = format;
        this.isFloat = isFloat;
        bytesPerSample = bitDepth / 8;
    }

    /// <inheritdoc />
    public AudioFormat Format { get; }

    /// <inheritdoc />
    /// <remarks>
    ///     A second, which is far more than any muxer puts in one block and is the only honest answer
    ///     for a codec whose packet size is whatever the writer felt like. The caller grows its
    ///     buffer to what a packet actually needs; this is the ceiling, not the expectation.
    /// </remarks>
    public int MaxFramesPerPacket => Format.SampleRate;

    /// <inheritdoc />
    public int Decode(ReadOnlySpan<byte> packet, Span<float> destination) {
        var stride = bytesPerSample * Format.Channels;

        if (stride == 0) {
            return 0;
        }

        var frames = packet.Length / stride;
        var samples = Math.Min(frames * Format.Channels, destination.Length);

        for (var index = 0; index < samples; index++) {
            destination[index] = Sample(packet[(index * bytesPerSample)..]);
        }

        return Format.Channels == 0 ? 0 : samples / Format.Channels;
    }

    /// <inheritdoc />
    /// <remarks>There is no state to throw away: every packet stands alone.</remarks>
    public void Reset() { }

    /// <inheritdoc />
    public void Dispose() { }

    float Sample(ReadOnlySpan<byte> bytes) {
        if (isFloat) {
            return bytesPerSample == 4
                ? BinaryPrimitives.ReadSingleLittleEndian(bytes)
                : (float)BinaryPrimitives.ReadDoubleLittleEndian(bytes);
        }

        return bytesPerSample switch {
            1 => (bytes[0] - 128) / 128f,
            2 => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32_768f,

            // Sign-extended out of three bytes, which no BinaryPrimitives overload does because no
            // primitive is that wide.
            3 => (((bytes[2] << 24) | (bytes[1] << 16) | (bytes[0] << 8)) >> 8) / 8_388_608f,
            _ => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2_147_483_648f
        };
    }
}
