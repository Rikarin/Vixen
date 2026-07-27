// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using Vixen.Audio;
using Vixen.Editor.Assets.Audio;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

public sealed class WaveDecoderTests {
    [Fact]
    public void SixteenBitPcmArrivesUnchanged() {
        var clip = WaveDecoder.Decode(Wave.Pcm(48_000, 1, 16, [0x00, 0x00, 0xFF, 0x7F, 0x00, 0x80]));

        Assert.Equal(48_000, clip.SampleRate);
        Assert.Equal(1, clip.Channels);
        Assert.Equal(AudioSampleFormat.Int16, clip.Format);
        Assert.Equal([0, 32_767, -32_768], clip.AsInt16().ToArray());
    }

    /// <summary>
    ///     The one width people get wrong. Eight-bit WAV is <em>unsigned</em> and centred on 128;
    ///     read as signed it comes out inverted around the midpoint, which sounds like distortion
    ///     rather than like silence and so is not obvious from listening once.
    /// </summary>
    [Fact]
    public void EightBitIsUnsignedAndCentredOnOneTwentyEight() {
        var clip = WaveDecoder.Decode(Wave.Pcm(22_050, 1, 8, [128, 255, 0]));

        Assert.Equal([0, 32_512, -32_768], clip.AsInt16().ToArray());
    }

    /// <summary>
    ///     Rounded rather than truncated. Truncation biases every sample towards negative infinity,
    ///     which is a DC offset across the whole clip — audible as a click at the start and the end.
    /// </summary>
    [Fact]
    public void TwentyFourBitIsSignExtendedAndRounded() {
        // 0x000080 is +128, which rounds to 1 and truncates to 0. 0xFFFFFF is −1, which rounds to 0
        // and truncates to −1.
        var clip = WaveDecoder.Decode(Wave.Pcm(48_000, 1, 24, [0x80, 0x00, 0x00, 0xFF, 0xFF, 0xFF]));

        Assert.Equal([1, 0], clip.AsInt16().ToArray());
    }

    [Fact]
    public void ThirtyTwoBitFloatArrivesAsFloat() {
        var samples = new byte[8];
        BinaryPrimitives.WriteSingleLittleEndian(samples, 0.5f);
        BinaryPrimitives.WriteSingleLittleEndian(samples.AsSpan(4), -1f);

        var clip = WaveDecoder.Decode(Wave.Float(44_100, 2, 32, samples));

        Assert.Equal(AudioSampleFormat.Float32, clip.Format);
        Assert.Equal([0.5f, -1f], clip.AsFloat32().ToArray());
    }

    /// <summary>
    ///     Anything above two channels or above 16 bits is written as WAVE_FORMAT_EXTENSIBLE, whose
    ///     real format code hides in a GUID at the end of the <c>fmt </c> chunk. A reader that stops
    ///     at 0xFFFE rejects most of what a DAW exports.
    /// </summary>
    [Fact]
    public void ExtensibleFindsTheRealFormatInsideTheSubFormatGuid() {
        // Float, deliberately: a sub-format of PCM would be indistinguishable from a decoder that
        // read the GUID and one that assumed PCM whenever it saw 0xFFFE.
        var samples = new byte[8];
        BinaryPrimitives.WriteSingleLittleEndian(samples, 0.25f);
        BinaryPrimitives.WriteSingleLittleEndian(samples.AsSpan(4), -0.75f);

        var clip = WaveDecoder.Decode(Wave.Extensible(96_000, 2, 32, 0x0003, samples));

        Assert.Equal(96_000, clip.SampleRate);
        Assert.Equal(AudioSampleFormat.Float32, clip.Format);
        Assert.Equal([0.25f, -0.75f], clip.AsFloat32().ToArray());
    }

    /// <summary>
    ///     The failure that made the chunk walk necessary. Seeking a fixed 44 bytes works on the
    ///     files a tool writes and reads a DAW's metadata as audio — a burst of noise at the start
    ///     of the clip, diagnosed by ear rather than by a stack trace.
    /// </summary>
    [Fact]
    public void ChunksBetweenTheHeaderAndTheSamplesAreWalkedPast() {
        var clip = WaveDecoder.Decode(
            Wave.Pcm(48_000, 1, 16, [0x01, 0x00], before: [("LIST", "INFOIART Somebody"u8.ToArray())])
        );

        Assert.Equal([1], clip.AsInt16().ToArray());
    }

    /// <summary>
    ///     A chunk of odd length is followed by a pad byte that is not counted in its size. Missing
    ///     it shifts every chunk after the first odd one, so the `data` chunk is never found.
    /// </summary>
    [Fact]
    public void AnOddLengthChunkIsFollowedByAPadByte() {
        var clip = WaveDecoder.Decode(
            Wave.Pcm(48_000, 1, 16, [0x02, 0x00], before: [("note", "odd"u8.ToArray())])
        );

        Assert.Equal([2], clip.AsInt16().ToArray());
    }

    [Fact]
    public void APartialFrameAtTheEndIsDropped() {
        // Five bytes of 16-bit stereo is one whole frame and a half of another. Keeping the half
        // would swap the channels for everything after it.
        var clip = WaveDecoder.Decode(Wave.Pcm(48_000, 2, 16, [1, 0, 2, 0, 3]));

        Assert.Equal(1, clip.FrameCount);
        Assert.Equal([1, 2], clip.AsInt16().ToArray());
    }

    [Fact]
    public void SomethingThatIsNotAWaveFileSaysSoRatherThanReadingGarbage() {
        var failure = Assert.Throws<AudioFormatException>(() => WaveDecoder.Decode("not a wave at all"u8));

        Assert.Contains("RIFF", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACompressedWavIsRefusedByName() {
        // 0x0011 is IMA ADPCM, which doc 08 wants eventually and nothing here decodes.
        var failure = Assert.Throws<AudioFormatException>(
            () => WaveDecoder.Decode(Wave.Build(0x0011, 22_050, 1, 4, [0, 0, 0, 0], []))
        );

        Assert.Contains("0x0011", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileWithNoFormatChunkSaysWhatIsMissing() {
        var failure = Assert.Throws<AudioFormatException>(() => WaveDecoder.Decode(Wave.DataOnly()));

        Assert.Contains("fmt", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Builds WAV files, so the tests read as what they are testing.</summary>
    static class Wave {
        internal static byte[] Pcm(
            int rate,
            int channels,
            int bits,
            byte[] data,
            (string Id, byte[] Body)[]? before = null
        ) =>
            Build(0x0001, rate, channels, bits, data, before ?? []);

        internal static byte[] Float(int rate, int channels, int bits, byte[] data) =>
            Build(0x0003, rate, channels, bits, data, []);

        internal static byte[] Extensible(int rate, int channels, int bits, int subFormat, byte[] data) {
            var format = new byte[40];
            Write(format, 0xFFFE, rate, channels, bits);
            BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(16), 22);
            BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(24), (ushort)subFormat);

            return Riff([("fmt ", format), ("data", data)]);
        }

        internal static byte[] Build(
            int tag,
            int rate,
            int channels,
            int bits,
            byte[] data,
            (string Id, byte[] Body)[] before
        ) {
            var format = new byte[16];
            Write(format, tag, rate, channels, bits);

            return Riff([("fmt ", format), .. before, ("data", data)]);
        }

        internal static byte[] DataOnly() => Riff([("data", [1, 0])]);

        static void Write(Span<byte> format, int tag, int rate, int channels, int bits) {
            BinaryPrimitives.WriteUInt16LittleEndian(format, (ushort)tag);
            BinaryPrimitives.WriteUInt16LittleEndian(format[2..], (ushort)channels);
            BinaryPrimitives.WriteInt32LittleEndian(format[4..], rate);
            BinaryPrimitives.WriteInt32LittleEndian(format[8..], rate * channels * (bits / 8));
            BinaryPrimitives.WriteUInt16LittleEndian(format[12..], (ushort)(channels * (bits / 8)));
            BinaryPrimitives.WriteUInt16LittleEndian(format[14..], (ushort)bits);
        }

        static byte[] Riff((string Id, byte[] Body)[] chunks) {
            using var buffer = new MemoryStream();
            using var writer = new BinaryWriter(buffer);

            writer.Write("RIFF"u8);
            writer.Write(0);
            writer.Write("WAVE"u8);

            foreach (var (id, body) in chunks) {
                writer.Write(System.Text.Encoding.ASCII.GetBytes(id));
                writer.Write(body.Length);
                writer.Write(body);

                if ((body.Length & 1) != 0) {
                    writer.Write((byte)0);
                }
            }

            writer.Flush();
            var bytes = buffer.ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);

            return bytes;
        }
    }
}
