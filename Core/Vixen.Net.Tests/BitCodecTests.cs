// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Messaging;
using Vixen.Net.Transport;
using Xunit;

namespace Vixen.Net.Tests;

/// <summary>Bit packing and quantization: that they round-trip, and what they cost.</summary>
public sealed class BitCodecTests {
    [Fact]
    public void FieldsNarrowerThanAByte_PackIntoOne() {
        Span<byte> buffer = stackalloc byte[8];
        var writer = new BitWriter(buffer);

        writer.Write(1, 2);
        writer.Write(2, 2);
        writer.Write(3, 2);
        writer.WriteBool(true);
        writer.WriteBool(false);

        Assert.Equal(8, writer.BitsWritten);
        Assert.True(writer.TryFinish(out var packet));
        Assert.Single(packet.ToArray());

        // Low bits first: 01 | 10 | 11 | 1 | 0 reading the byte from its bottom up.
        Assert.Equal(0b0_1_11_10_01, packet[0]);
    }

    [Fact]
    public void AFieldThatStraddlesAByteBoundary_ComesBackWhole() {
        Span<byte> buffer = stackalloc byte[8];
        var writer = new BitWriter(buffer);

        writer.Write(5, 3);
        writer.Write(0xABCDE, 20);
        writer.Write(7, 3);

        Assert.True(writer.TryFinish(out var packet));

        var reader = new BitReader(packet);

        Assert.True(reader.TryRead(3, out var first));
        Assert.True(reader.TryRead(20, out var middle));
        Assert.True(reader.TryRead(3, out var last));
        Assert.Equal(5u, first);
        Assert.Equal(0xABCDEu, middle);
        Assert.Equal(7u, last);
    }

    [Fact]
    public void EveryWidthRoundTrips() {
        var random = new DeterministicRandom(99);
        Span<byte> buffer = stackalloc byte[512];

        for (var trial = 0; trial < 200; trial++) {
            var widths = new int[16];
            var values = new uint[16];
            var writer = new BitWriter(buffer);

            for (var i = 0; i < widths.Length; i++) {
                widths[i] = 1 + (int)(random.NextDouble() * 32);
                widths[i] = Math.Min(widths[i], 32);
                values[i] = (uint)random.NextUInt64();

                if (widths[i] < 32) {
                    values[i] &= (1u << widths[i]) - 1;
                }

                writer.Write(values[i], widths[i]);
            }

            Assert.True(writer.TryFinish(out var packet));

            var reader = new BitReader(packet);

            for (var i = 0; i < widths.Length; i++) {
                Assert.True(reader.TryRead(widths[i], out var read));
                Assert.Equal(values[i], read);
            }
        }
    }

    [Fact]
    public void TheUnusedBitsOfTheLastByteAreZero() {
        // Not cosmetic: the buffer is rented and full of somebody else's packet, and two peers
        // encoding the same values have to produce the same bytes for the determinism gate.
        Span<byte> buffer = stackalloc byte[4];
        buffer.Fill(0xFF);

        var writer = new BitWriter(buffer);
        writer.Write(1, 3);

        Assert.True(writer.TryFinish(out var packet));
        Assert.Equal([0b0000_0001], packet.ToArray());
    }

    [Fact]
    public void RunningOutOfRoom_RefusesToHandOverTheTruncatedPacket() {
        Span<byte> buffer = stackalloc byte[1];
        var writer = new BitWriter(buffer);

        writer.Write(0xF, 4);
        writer.Write(0xFF, 8);

        Assert.True(writer.Overflowed);
        Assert.False(writer.TryFinish(out var packet));
        Assert.True(packet.IsEmpty);
    }

    [Fact]
    public void ReadingPastTheEnd_FailsAndKeepsFailing() {
        var reader = new BitReader([0b1010_1010]);

        Assert.True(reader.TryRead(4, out _));
        Assert.False(reader.TryRead(8, out _));
        Assert.True(reader.Failed);
        Assert.False(reader.TryRead(1, out _));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(33)]
    public void AFieldWidthThatIsNotAFieldWidth_Throws(int width) {
        var invalid = width == 1 ? 0 : width;

        Assert.Throws<ArgumentOutOfRangeException>(
            () => {
                Span<byte> buffer = stackalloc byte[8];
                var writer = new BitWriter(buffer);
                writer.Write(1, invalid);
            }
        );
    }

    [Fact]
    public void AlignedBytesSurviveTheBitsAroundThem() {
        Span<byte> buffer = stackalloc byte[16];
        var writer = new BitWriter(buffer);

        writer.Write(3, 3);
        writer.WriteBytes([1, 2, 3]);
        writer.Write(1, 1);

        Assert.True(writer.TryFinish(out var packet));

        var reader = new BitReader(packet);

        Assert.True(reader.TryRead(3, out var before));
        Assert.True(reader.TryReadBytes(3, out var bytes));
        Assert.True(reader.TryRead(1, out var after));
        Assert.Equal(3u, before);
        Assert.Equal([1, 2, 3], bytes.ToArray());
        Assert.Equal(1u, after);
    }

    [Fact]
    public void AVariableLengthValueRoundTripsThroughBits() {
        Span<byte> buffer = stackalloc byte[16];
        var writer = new BitWriter(buffer);

        writer.Write(1, 1); // knock it off the byte boundary first
        writer.WriteVariable(300);
        writer.WriteVariable(uint.MaxValue);

        Assert.True(writer.TryFinish(out var packet));

        var reader = new BitReader(packet);

        Assert.True(reader.TryRead(1, out _));
        Assert.True(reader.TryReadVariable(out var small));
        Assert.True(reader.TryReadVariable(out var large));
        Assert.Equal(300u, small);
        Assert.Equal(uint.MaxValue, large);
    }

    [Fact]
    public void AQuantizedValueComesBackWithinItsStatedError() {
        var range = new QuantizeRange(-1000f, 1000f, 16);
        var random = new DeterministicRandom(1234);

        Assert.True(range.IsValid);
        Assert.InRange(range.MaxError, 0.01f, 0.02f);

        for (var i = 0; i < 1000; i++) {
            var value = (float)((random.NextDouble() * 2000.0) - 1000.0);
            var round = range.Decode(range.Encode(value));

            // MaxError is the quantization error. The decoded value is then rounded to the nearest
            // float, so the bound is that plus half a ULP of the result — which at 640 is 3e-5
            // against a stated 1.5e-2, and is the reason the arithmetic inside is done in double.
            var slack = range.MaxError + (Math.Abs(value) * 1.2e-7f);

            Assert.InRange(round, value - slack, value + slack);
        }
    }

    [Fact]
    public void TheEndsOfARangeAreExact() {
        var range = new QuantizeRange(0f, 1f, 8);

        Assert.Equal(0f, range.Decode(range.Encode(0f)));
        Assert.Equal(1f, range.Decode(range.Encode(1f)));
        Assert.Equal(255u, range.Levels);
    }

    [Fact]
    public void ValuesOutsideARangeStopAtItsEdge() {
        var range = new QuantizeRange(0f, 1f, 8);

        Assert.Equal(0f, range.Decode(range.Encode(-5f)));
        Assert.Equal(1f, range.Decode(range.Encode(5f)));

        // A NaN encodes as the bottom of the range rather than propagating: one NaN let into a
        // receiver's world spreads through everything it touches.
        Assert.Equal(0f, range.Decode(range.Encode(float.NaN)));
    }

    [Fact]
    public void QuantizingIsWhatMakesAFieldCheap() {
        Span<byte> buffer = stackalloc byte[16];
        var range = new QuantizeRange(-1000f, 1000f, 16);
        var writer = new BitWriter(buffer);

        writer.WriteQuantized(12.5f, range);
        writer.WriteQuantized(-999f, range);

        Assert.Equal(32, writer.BitsWritten); // two floats in the space of one
        Assert.True(writer.TryFinish(out var packet));

        var reader = new BitReader(packet);

        Assert.True(reader.TryReadQuantized(range, out var first));
        Assert.True(reader.TryReadQuantized(range, out var second));
        Assert.InRange(first, 12.5f - range.MaxError, 12.5f + range.MaxError);
        Assert.InRange(second, -999f - range.MaxError, -999f + range.MaxError);
    }

    [Fact]
    public void ARangeThatIsNotARange_IsRefused() {
        Assert.False(new QuantizeRange(1f, 0f, 8).IsValid);
        Assert.False(new QuantizeRange(0f, 1f, 0).IsValid);
        Assert.False(new QuantizeRange(0f, 1f, 33).IsValid);
        Assert.False(new QuantizeRange(0f, float.PositiveInfinity, 8).IsValid);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => {
                Span<byte> buffer = stackalloc byte[8];
                var writer = new BitWriter(buffer);
                writer.WriteQuantized(0.5f, new QuantizeRange(1f, 0f, 8));
            }
        );
    }

    [Fact]
    public void NoSequenceOfBitsMakesTheReaderThrow() {
        var random = new DeterministicRandom(0xB175);
        var buffer = new byte[32];

        for (var packet = 0; packet < 5_000; packet++) {
            for (var i = 0; i < buffer.Length; i++) {
                buffer[i] = (byte)(random.NextUInt64() & 0xFF);
            }

            var reader = new BitReader(buffer.AsSpan(0, (int)(random.NextDouble() * buffer.Length)));

            reader.TryRead(3, out _);
            reader.TryReadVariable(out _);
            reader.TryReadQuantized(new(-1f, 1f, 12), out _);
            reader.TryReadBytes(9, out _);
            reader.TryReadSingle(out _);
            reader.TryRead(32, out _);
        }
    }
}
