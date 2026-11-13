// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using Xunit;

namespace Vixen.Interchange.Tests;

/// <summary>
///     The TIFF reader, against the writer beside it and against files built here byte by byte from the TIFF 6.0
///     specification, so that it does not only agree with its own writer's reading of the format.
/// </summary>
public sealed class TiffReaderTests {
    static byte[] Picture(int width, int height, int channels) =>
        [..Enumerable.Range(0, width * height * channels).Select(static at => (byte)(at * 37 % 251))];

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void EightBitsReadBackAsWritten(int channels) {
        var samples = Picture(33, 40, channels);

        var read = TiffReader.Decode(TiffWriter.Encode8(33, 40, channels, samples));

        Assert.Equal((33, 40, channels), (read.Width, read.Height, read.Channels));
        Assert.Equal(samples.Select(static s => s / 255f), read.Samples);
    }

    [Fact]
    public void SixteenBitsReadBackAsWritten() {
        ushort[] samples = [..Picture(33, 40, 3).Select(static (s, at) => (ushort)((s * 257) ^ (at & 0xF)))];

        var read = TiffReader.Decode(TiffWriter.Encode16(33, 40, 3, samples));

        Assert.Equal(samples.Select(static s => s / 65535f), read.Samples);
    }

    [Fact]
    public void FloatsReadBackAsWritten() {
        float[] samples = [..Picture(33, 40, 4).Select(static s => s / 255f - 0.25f)];

        var read = TiffReader.Decode(TiffWriter.EncodeFloat(new(33, 40, 4, samples)));

        Assert.Equal(samples, read.Samples);
    }

    /// <summary>
    ///     A big-endian, uncompressed, 16-bit RGB file with two strips and no RowsPerStrip-sized last strip, written
    ///     here from the specification: a writer that differs from TiffWriter in every choice it can make.
    /// </summary>
    [Fact]
    public void ABigEndianUncompressedFileFromTheSpecificationReads() {
        const int width = 3;
        const int height = 3;
        ushort[] samples = [..Enumerable.Range(0, width * height * 3).Select(static at => (ushort)(at * 6151))];
        var pixels = new byte[samples.Length * 2];
        for (var at = 0; at < samples.Length; at++) {
            BinaryPrimitives.WriteUInt16BigEndian(pixels.AsSpan(at * 2), samples[at]);
        }

        var row = width * 3 * 2;
        (ushort Tag, ushort Type, uint Value)[] entries = [
            (256, 3, width), (257, 3, height), (258, 3, 16), (259, 3, 1), (262, 3, 2), (273, 4, 0), (277, 3, 3),
            (278, 3, 2), (279, 4, 0)
        ];
        // Header, then pixels at 8, then the directory; strip offsets and counts (two each) go after it.
        var directory = 8 + pixels.Length;
        var arrays = directory + 2 + entries.Length * 12 + 4;
        var file = new byte[arrays + 16];
        "MM"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(4), (uint)directory);
        pixels.CopyTo(file, 8);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(directory), (ushort)entries.Length);
        for (var at = 0; at < entries.Length; at++) {
            var (tag, type, value) = entries[at];
            var place = directory + 2 + at * 12;
            BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(place), tag);
            BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(place + 2), type);
            var many = tag is 273 or 279 ? 2u : 1u;
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(place + 4), many);
            if (many == 2) {
                BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(place + 8), (uint)(arrays + (tag == 273 ? 0 : 8)));
            } else if (type == 3) {
                BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(place + 8), (ushort)value);
            } else {
                BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(place + 8), value);
            }
        }

        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(arrays), 8);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(arrays + 4), (uint)(8 + 2 * row));
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(arrays + 8), (uint)(2 * row));
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(arrays + 12), (uint)row);

        var read = TiffReader.Decode(file);

        Assert.Equal((width, height, 3), (read.Width, read.Height, read.Channels));
        Assert.Equal(samples.Select(static s => s / 65535f), read.Samples);
    }

    [Fact]
    public void WhatItDoesNotReadIsRefusedByName() {
        var file = TiffWriter.Encode8(2, 2, 1, [1, 2, 3, 4]);
        var directory = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(directory));
        for (var at = 0; at < count; at++) {
            var place = directory + 2 + at * 12;
            if (BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(place)) == 259) {
                BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(place + 8), 5); // LZW
            }
        }

        var refused = Assert.Throws<InvalidDataException>(() => TiffReader.Decode(file));
        Assert.Contains("scheme 5", refused.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => TiffReader.Decode("GIF89a.."u8));
    }
}
