// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Video.Containers;
using Xunit;

namespace Vixen.Video.Tests;

/// <summary>
///     The primitive layer, which the whole container reader rests on and which has exactly two ideas
///     in it that anybody gets wrong: ids keep their marker and sizes lose theirs.
/// </summary>
public sealed class EbmlReaderTests {
    [Theory]
    [InlineData(0x80, 1)]
    [InlineData(0xFF, 1)]
    [InlineData(0x40, 2)]
    [InlineData(0x7F, 2)]
    [InlineData(0x20, 3)]
    [InlineData(0x10, 4)]
    [InlineData(0x01, 8)]
    [InlineData(0x00, 0)]
    public void TheLeadingZeroesSayHowLongTheNumberIs(int first, int expected) =>
        Assert.Equal(expected, EbmlReader.LengthOf((byte)first));

    [Fact]
    public void AnIdKeepsItsMarkerAndASizeLosesIts() {
        // 0xA3 is a SimpleBlock and stays 0xA3; the same byte as a size means 35.
        var reader = Read([0xA3, 0xA3]);

        Assert.True(reader.TryReadElement(out var element));
        Assert.Equal(0xA3u, element.Id);
        Assert.Equal(35, element.Size);
        Assert.Equal(2, element.HeaderSize);
    }

    [Fact]
    public void AFourByteIdIsReadWhole() {
        var reader = Read([0x1A, 0x45, 0xDF, 0xA3, 0x84, 1, 2, 3, 4]);

        Assert.True(reader.TryReadElement(out var element));
        Assert.Equal(0x1A45DFA3u, element.Id);
        Assert.Equal(4, element.Size);
        Assert.Equal(5, element.HeaderSize);
    }

    [Fact]
    public void AnAllOnesSizeIsUnknownRatherThanEnormous() {
        var reader = Read([0xA3, 0xFF]);

        Assert.True(reader.TryReadElement(out var element));
        Assert.True(element.IsUnknownSize);
        Assert.Equal(-1, element.Size);
    }

    [Fact]
    public void AMultiByteSizeIsNotUnknownJustBecauseItsFirstByteIsFull() {
        // 0x7F 0xFE: the marker says two bytes, and the value has a zero bit in it, so it is 16 382
        // rather than "unknown". The all-ones test has to look at every byte.
        var reader = Read([0xA3, 0x7F, 0xFE]);

        Assert.True(reader.TryReadElement(out var element));
        Assert.False(element.IsUnknownSize);
        Assert.Equal(16_382, element.Size);
    }

    [Fact]
    public void TheEndOfTheStreamIsNotAnError() {
        var reader = Read([]);

        Assert.False(reader.TryReadElement(out _));
    }

    [Fact]
    public void AZeroByteStartsNothing() {
        var reader = Read([0x00, 0x81, 1]);

        Assert.Throws<InvalidDataException>(() => reader.TryReadElement(out _));
    }

    [Fact]
    public void ASignedIntegerIsExtendedFromItsOwnWidth() {
        var reader = Read([0xFF]);

        Assert.Equal(-1, reader.ReadSigned(1));
    }

    [Fact]
    public void AnUnsignedIntegerOfTheSameByteIsNot() {
        var reader = Read([0xFF]);

        Assert.Equal(255UL, reader.ReadUnsigned(1));
    }

    [Fact]
    public void AStringLosesItsPaddingZeroes() {
        var reader = Read([(byte)'V', (byte)'P', (byte)'9', 0, 0]);

        Assert.Equal("VP9", reader.ReadString(5));
    }

    [Fact]
    public void AFloatIsBigEndian() {
        var bytes = new byte[8];

        System.Buffers.Binary.BinaryPrimitives.WriteDoubleBigEndian(bytes, 1234.5);

        Assert.Equal(1234.5, Read(bytes).ReadFloat(8));
    }

    [Fact]
    public void SkippingPastTheEndSaysSoRatherThanSilentlyStopping() {
        var reader = Read([1, 2, 3]);

        Assert.Throws<InvalidDataException>(() => reader.Skip(9));
    }

    static EbmlReader Read(byte[] bytes) => new(new MemoryStream(bytes, writable: false));
}
