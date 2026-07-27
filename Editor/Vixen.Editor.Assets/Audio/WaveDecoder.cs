// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using Vixen.Audio;

namespace Vixen.Editor.Assets.Audio;

/// <summary>Reads a RIFF/WAVE file.</summary>
/// <remarks>
///     <para>
///         <b>Written here rather than taken from a package</b>, which is the opposite of the choice
///         made for images. A PNG decoder is a compression implementation and writing one would be
///         foolish; a WAV file is a chunk header and then the samples, and the entire specification
///         that matters fits on this page. A dependency for it would be a licence, a supply-chain
///         entry and a version to track, in exchange for about a hundred lines.
///     </para>
///     <para>
///         <b>The chunks are walked, not assumed.</b> The naive reader — seek 44 bytes, read the rest
///         — works on the files a tool writes and fails on the ones a DAW writes, because those carry
///         <c>LIST</c>, <c>fact</c>, <c>bext</c> and <c>cue </c> chunks between the header and the
///         samples. It fails by reading metadata as audio, which sounds like a burst of noise at the
///         start of the clip and is diagnosed by ear rather than by a stack trace.
///     </para>
///     <para>
///         <b>Every uncompressed layout the format has, and nothing else.</b> 8-bit unsigned, 16-, 24-
///         and 32-bit signed, and 32- and 64-bit float, in plain PCM and in <c>WAVE_FORMAT_EXTENSIBLE</c>
///         — which is what anything above two channels or above 16 bits is written as. ADPCM and the
///         rest are compressed formats hiding inside a WAV, and are refused by name.
///     </para>
/// </remarks>
public sealed class WaveDecoder : IAudioDecoder {
    /// <summary>What a <c>fmt </c> chunk's tag says the samples are.</summary>
    const int Pcm = 0x0001;
    const int Float = 0x0003;
    const int Extensible = 0xFFFE;

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [".wav", ".wave"];

    /// <inheritdoc />
    public AudioClip Decode(Stream stream, string extension) {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        return Decode(buffer.ToArray());
    }

    /// <summary>Decodes a file already in memory.</summary>
    /// <param name="bytes">The file.</param>
    /// <returns>The samples.</returns>
    /// <exception cref="AudioFormatException">It is not a readable WAVE file.</exception>
    public static AudioClip Decode(ReadOnlySpan<byte> bytes) {
        if (bytes.Length < 12
            || !bytes[..4].SequenceEqual("RIFF"u8)
            || !bytes[8..12].SequenceEqual("WAVE"u8)) {
            throw new AudioFormatException(
                "It does not begin 'RIFF' … 'WAVE', so it is not a WAV file whatever it is called."
            );
        }

        var format = 0;
        var channels = 0;
        var sampleRate = 0;
        var bits = 0;
        var data = ReadOnlySpan<byte>.Empty;
        var sawFormat = false;
        var sawData = false;

        // The RIFF payload starts after 'RIFF', the size, and 'WAVE'. Each chunk is a four-character
        // id, a little-endian size, and that many bytes — padded to an even length, which is the part
        // a hand-written reader forgets and which shifts every chunk after the first odd one.
        for (var offset = 12; offset + 8 <= bytes.Length;) {
            var id = bytes.Slice(offset, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
            var body = offset + 8;

            if (size > (uint)(bytes.Length - body)) {
                // Truncated. Taking what is there would produce a clip that plays and is wrong, so
                // the last chunk is refused rather than clamped — except for `data`, where a
                // truncated tail is a recoverable and common result of a copy that was interrupted.
                if (!id.SequenceEqual("data"u8)) {
                    throw new AudioFormatException(
                        $"Its '{Name(id)}' chunk claims {size} bytes and the file has {bytes.Length - body} left."
                    );
                }

                size = (uint)(bytes.Length - body);
            }

            if (id.SequenceEqual("fmt "u8)) {
                if (size < 16) {
                    throw new AudioFormatException($"Its 'fmt ' chunk is {size} bytes; the format needs at least 16.");
                }

                var fmt = bytes.Slice(body, (int)size);
                format = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(fmt[4..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);

                // WAVE_FORMAT_EXTENSIBLE puts the real format code in the first two bytes of a GUID
                // at the end of the chunk. Anything above two channels or above 16 bits is written
                // this way, so a reader that stops at 0xFFFE rejects most of what a DAW exports.
                if (format == Extensible) {
                    if (size < 40) {
                        throw new AudioFormatException(
                            $"It says WAVE_FORMAT_EXTENSIBLE, whose 'fmt ' chunk is 40 bytes, and its own is {size}."
                        );
                    }

                    format = BinaryPrimitives.ReadUInt16LittleEndian(fmt[24..]);
                }

                sawFormat = true;
            } else if (id.SequenceEqual("data"u8)) {
                data = bytes.Slice(body, (int)size);
                sawData = true;
            }

            // Chunks are word-aligned: an odd size is followed by a pad byte that is not counted in it.
            offset = body + (int)size + ((int)size & 1);
        }

        if (!sawFormat) {
            throw new AudioFormatException("It has no 'fmt ' chunk, so nothing says what its samples are.");
        }

        if (!sawData) {
            throw new AudioFormatException("It has no 'data' chunk, so it holds no samples.");
        }

        if (channels <= 0) {
            throw new AudioFormatException($"It says it has {channels} channels.");
        }

        if (sampleRate <= 0) {
            throw new AudioFormatException($"It says its sample rate is {sampleRate} Hz.");
        }

        return format switch {
            Pcm => FromPcm(data, channels, sampleRate, bits),
            Float => FromFloat(data, channels, sampleRate, bits),
            _ => throw new AudioFormatException(
                $"Its samples are format 0x{format:X4}, which is compressed. Only uncompressed PCM and float WAVs "
                + "are read; re-export it as PCM, or use a format with its own importer path."
            )
        };
    }

    /// <summary>Integer PCM, in every width the format allows.</summary>
    /// <remarks>
    ///     Everything becomes <see cref="AudioSampleFormat.Int16" /> except 32-bit, which becomes
    ///     float. Widening 8-bit to 16 is free and exact; narrowing 24-bit to 16 is the conversion
    ///     every engine does for effects and the importer's <c>Format</c> setting is how to keep the
    ///     precision. Narrowing 32-bit integer to 16 would throw away a great deal for no reason a
    ///     file at that width was written for.
    /// </remarks>
    static AudioClip FromPcm(ReadOnlySpan<byte> data, int channels, int sampleRate, int bits) {
        switch (bits) {
            case 8: {
                // Eight-bit WAV is *unsigned*, centred on 128. Reading it as signed produces a clip
                // that is inverted around the midpoint, which sounds like distortion rather than
                // like silence and is why this is the one width people get wrong.
                var samples = new byte[data.Length * 2];

                for (var index = 0; index < data.Length; index++) {
                    BinaryPrimitives.WriteInt16LittleEndian(
                        samples.AsSpan(index * 2),
                        (short)((data[index] - 128) << 8)
                    );
                }

                return Clip(samples, channels, sampleRate, AudioSampleFormat.Int16);
            }

            case 16:
                // Already the target layout, and already little-endian: a copy and nothing else.
                return Clip(data[..(data.Length & ~1)].ToArray(), channels, sampleRate, AudioSampleFormat.Int16);

            case 24: {
                var frames = data.Length / 3;
                var samples = new byte[frames * 2];

                for (var index = 0; index < frames; index++) {
                    var source = data.Slice(index * 3, 3);

                    // Sign-extend from 24 bits, then round to 16 rather than truncating. Truncation
                    // biases every sample towards negative infinity, which is a DC offset on the
                    // whole clip — audible as a click when it starts and stops.
                    var value = source[0] | (source[1] << 8) | (source[2] << 16);

                    if ((value & 0x800000) != 0) {
                        value -= 0x1000000;
                    }

                    var rounded = (value + 128) >> 8;
                    BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan(index * 2), Saturate(rounded));
                }

                return Clip(samples, channels, sampleRate, AudioSampleFormat.Int16);
            }

            case 32: {
                var count = data.Length / 4;
                var samples = new byte[count * 4];

                for (var index = 0; index < count; index++) {
                    var value = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(index * 4, 4));
                    BinaryPrimitives.WriteSingleLittleEndian(samples.AsSpan(index * 4), value / 2147483648f);
                }

                return Clip(samples, channels, sampleRate, AudioSampleFormat.Float32);
            }

            default:
                throw new AudioFormatException(
                    $"Its samples are {bits} bits wide. PCM WAVs are 8, 16, 24 or 32."
                );
        }
    }

    /// <summary>IEEE float, single or double.</summary>
    static AudioClip FromFloat(ReadOnlySpan<byte> data, int channels, int sampleRate, int bits) {
        switch (bits) {
            case 32:
                return Clip(data[..(data.Length & ~3)].ToArray(), channels, sampleRate, AudioSampleFormat.Float32);

            case 64: {
                var count = data.Length / 8;
                var samples = new byte[count * 4];

                for (var index = 0; index < count; index++) {
                    var value = BinaryPrimitives.ReadDoubleLittleEndian(data.Slice(index * 8, 8));
                    BinaryPrimitives.WriteSingleLittleEndian(samples.AsSpan(index * 4), (float)value);
                }

                return Clip(samples, channels, sampleRate, AudioSampleFormat.Float32);
            }

            default:
                throw new AudioFormatException($"Its samples are {bits}-bit float. IEEE float WAVs are 32 or 64.");
        }
    }

    /// <summary>Trims a partial frame off the end and makes the clip.</summary>
    /// <remarks>
    ///     A file whose data chunk does not divide into whole frames is a truncated one, and half a
    ///     frame at the end would shift every channel by one from that point — which is silence in
    ///     one ear and a channel swap in the other, on a clip that otherwise plays.
    /// </remarks>
    static AudioClip Clip(byte[] samples, int channels, int sampleRate, AudioSampleFormat format) {
        var stride = channels * (format is AudioSampleFormat.Float32 ? 4 : 2);
        var whole = samples.Length - (samples.Length % stride);

        return new() {
            SampleRate = sampleRate,
            Channels = channels,
            Format = format,
            Samples = whole == samples.Length ? samples : samples[..whole]
        };
    }

    static short Saturate(int value) => (short)Math.Clamp(value, short.MinValue, short.MaxValue);

    static string Name(ReadOnlySpan<byte> id) => System.Text.Encoding.ASCII.GetString(id);
}
