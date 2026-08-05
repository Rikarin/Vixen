// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Messaging;
using Vixen.Net.Transport;
using Xunit;

namespace Vixen.Net.Tests;

/// <summary>
///     The packet codec: that it round-trips, that the bytes are the bytes, and — the part that
///     matters — that nothing a hostile packet can say makes the reader throw.
/// </summary>
public sealed class PacketCodecTests {
    [Fact]
    public void EverythingWrittenComesBack() {
        Span<byte> buffer = stackalloc byte[256];
        var writer = new PacketWriter(buffer);

        writer.WriteByte(0xAB);
        writer.WriteBool(true);
        writer.WriteBool(false);
        writer.WriteUInt16(4242);
        writer.WriteUInt32(0xDEADBEEF);
        writer.WriteInt32(-77);
        writer.WriteSingle(0.5f);
        writer.WriteTick(new(1234));
        writer.WriteVariable(300);
        writer.WriteBlob([1, 2, 3]);
        writer.WriteString("héllo");

        Assert.True(writer.TryFinish(out var packet));

        var reader = new PacketReader(packet);

        Assert.True(reader.TryReadByte(out var b));
        Assert.True(reader.TryReadBool(out var yes));
        Assert.True(reader.TryReadBool(out var no));
        Assert.True(reader.TryReadUInt16(out var word));
        Assert.True(reader.TryReadUInt32(out var dword));
        Assert.True(reader.TryReadInt32(out var signed));
        Assert.True(reader.TryReadSingle(out var real));
        Assert.True(reader.TryReadTick(out var tick));
        Assert.True(reader.TryReadVariable(out var variable));
        Assert.True(reader.TryReadBlob(16, out var blob));
        Assert.True(reader.TryReadString(64, out var text));

        Assert.Equal(0xAB, b);
        Assert.True(yes);
        Assert.False(no);
        Assert.Equal(4242, word);
        Assert.Equal(0xDEADBEEF, dword);
        Assert.Equal(-77, signed);
        Assert.Equal(0.5f, real);
        Assert.Equal(new Tick(1234), tick);
        Assert.Equal(300u, variable);
        Assert.Equal([1, 2, 3], blob.ToArray());
        Assert.Equal("héllo", text);
        Assert.True(reader.IsComplete);
    }

    [Fact]
    public void TheBytesAreLittleEndian_WhateverTheMachineIs() {
        Span<byte> buffer = stackalloc byte[8];
        var writer = new PacketWriter(buffer);

        writer.WriteUInt32(0x01020304);

        // Stated as bytes rather than round-tripped, because a round trip is symmetric on a
        // big-endian machine too and would pass while the wire format quietly differed.
        Assert.True(writer.TryFinish(out var packet));
        Assert.Equal([0x04, 0x03, 0x02, 0x01], packet.ToArray());
    }

    [Fact]
    public void AFloatIsItsBits() {
        Span<byte> buffer = stackalloc byte[8];
        var writer = new PacketWriter(buffer);

        writer.WriteSingle(float.NaN);
        writer.WriteSingle(float.NegativeInfinity);

        Assert.True(writer.TryFinish(out var packet));

        var reader = new PacketReader(packet);

        Assert.True(reader.TryReadSingle(out var nan));
        Assert.True(reader.TryReadSingle(out var infinity));
        Assert.True(float.IsNaN(nan));
        Assert.True(float.IsNegativeInfinity(infinity));
    }

    [Theory]
    [InlineData(0u, 1)]
    [InlineData(127u, 1)]
    [InlineData(128u, 2)]
    [InlineData(16383u, 2)]
    [InlineData(16384u, 3)]
    [InlineData(uint.MaxValue, 5)]
    public void AVariableLengthNumberCostsWhatItShould(uint value, int expectedBytes) {
        Span<byte> buffer = stackalloc byte[8];
        var writer = new PacketWriter(buffer);

        writer.WriteVariable(value);

        Assert.True(writer.TryFinish(out var packet));
        Assert.Equal(expectedBytes, packet.Length);

        var reader = new PacketReader(packet);

        Assert.True(reader.TryReadVariable(out var read));
        Assert.Equal(value, read);
        Assert.True(reader.IsComplete);
    }

    [Fact]
    public void AVariableLengthNumberThatNeverEnds_IsRefused() {
        // Six continuation bytes: an encoding no writer of ours produces, and a cheap way to walk a
        // careless reader forward through a packet.
        var packet = new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01 };
        var reader = new PacketReader(packet);

        Assert.False(reader.TryReadVariable(out _));
        Assert.True(reader.Failed);
    }

    [Fact]
    public void AVariableLengthNumberTooBigForAUInt32_IsRefused() {
        // Five bytes, but the fifth carries bits above the top of a uint.
        var packet = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x10 };
        var reader = new PacketReader(packet);

        Assert.False(reader.TryReadVariable(out _));
        Assert.True(reader.Failed);
    }

    [Fact]
    public void ALengthThatIsLongerThanThePacket_IsRefused() {
        Span<byte> buffer = stackalloc byte[8];
        var writer = new PacketWriter(buffer);

        writer.WriteVariable(1000); // says a kilobyte follows
        writer.WriteByte(1); // and here is one byte of it

        Assert.True(writer.TryFinish(out var packet));

        var reader = new PacketReader(packet);

        Assert.False(reader.TryReadBlob(4096, out var blob));
        Assert.True(reader.Failed);
        Assert.True(blob.IsEmpty);
    }

    [Fact]
    public void ABlobLongerThanTheCallerAllows_IsRefused() {
        Span<byte> buffer = stackalloc byte[64];
        var writer = new PacketWriter(buffer);

        writer.WriteBlob(new byte[32]);

        Assert.True(writer.TryFinish(out var packet));

        var reader = new PacketReader(packet);

        Assert.False(reader.TryReadBlob(16, out _));
        Assert.True(reader.Failed);
    }

    [Fact]
    public void ABooleanThatIsNeitherTrueNorFalse_IsRefused() {
        var reader = new PacketReader(new byte[] { 2 });

        Assert.False(reader.TryReadBool(out _));
        Assert.True(reader.Failed);
    }

    [Fact]
    public void ReadingPastTheEnd_FailsAndKeepsFailing() {
        var reader = new PacketReader([1, 2]);

        Assert.True(reader.TryReadByte(out _));
        Assert.False(reader.TryReadUInt32(out _));
        Assert.True(reader.Failed);

        // Sticky: the byte that is genuinely still there is not handed out, so a decoder that reads
        // its whole message and checks once at the end cannot act on half of one.
        Assert.False(reader.TryReadByte(out _));
        Assert.False(reader.IsComplete);
    }

    [Fact]
    public void AWriterThatRunsOutOfRoom_RefusesToHandOverTheTruncatedPacket() {
        Span<byte> buffer = stackalloc byte[4];
        var writer = new PacketWriter(buffer);

        writer.WriteUInt16(1);
        writer.WriteUInt32(2); // does not fit

        Assert.True(writer.Overflowed);
        Assert.False(writer.TryFinish(out var packet));
        Assert.True(packet.IsEmpty);

        // And it stays overflowed: a later small write does not quietly produce a packet with a hole
        // where the big one should have been.
        writer.WriteByte(3);
        Assert.False(writer.TryFinish(out _));
    }

    [Fact]
    public void AnEmptyStringAndANullOneAreTheSameOnTheWire() {
        Span<byte> first = stackalloc byte[8];
        Span<byte> second = stackalloc byte[8];
        var writing = new PacketWriter(first);
        var alsoWriting = new PacketWriter(second);

        writing.WriteString(null);
        alsoWriting.WriteString(string.Empty);

        Assert.True(writing.TryFinish(out var a));
        Assert.True(alsoWriting.TryFinish(out var b));
        Assert.Equal(a.ToArray(), b.ToArray());

        var reader = new PacketReader(a);

        Assert.True(reader.TryReadString(16, out var text));
        Assert.Equal(string.Empty, text);
    }

    /// <summary>A length that does not fit in an <see cref="int" /> is refused, not indexed with.</summary>
    /// <remarks>
    ///     <para>
    ///         Found by the packet fuzzer, and it took the fuzzer because the route to it is two
    ///         mistakes deep. A blob's length is a <c>uint</c> and the cap it is checked against is an
    ///         <c>int</c>, so the comparison is unsigned — which means a <i>negative</i> cap is a cap
    ///         above every length there is and stops being a cap. The length then goes to the bounds
    ///         check as <c>(int)length</c>, and a length above <c>int.MaxValue</c> casts to a negative
    ///         count that sails past <c>count &gt; Remaining</c> and throws out of <c>Span.Slice</c>.
    ///     </para>
    ///     <para>
    ///         Fixed in both places: a negative cap is refused, and the single choke point where
    ///         bytes are taken rejects a negative count. The second is the one that matters — it puts
    ///         the invariant where every read goes through it rather than at four call sites and
    ///         missing from a fifth.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ALengthTooLargeForAnInt_IsRefusedRatherThanThrowing() {
        // 0xFFFFFFFF as a variable-length integer: five bytes, the last carrying the top four bits.
        ReadOnlySpan<byte> packet = [0xFF, 0xFF, 0xFF, 0xFF, 0x0F];

        var reader = new PacketReader(packet);
        Assert.False(reader.TryReadBlob(-1, out var bytes));
        Assert.True(reader.Failed);
        Assert.True(bytes.IsEmpty);

        var withACap = new PacketReader(packet);
        Assert.False(withACap.TryReadBlob(int.MaxValue, out _));
        Assert.True(withACap.Failed);

        var asAString = new PacketReader(packet);
        Assert.False(asAString.TryReadString(-1, out var text));
        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void NoSequenceOfBytesMakesTheReaderThrow() {
        // The fuzz harness in Vixen.Fuzz is the thorough version of this and drives every read
        // in a sequence the input chooses. This is the cheap version that stays here next to the
        // codec: ten thousand random packets, decoded as if they were the real thing, asserting
        // only that the process survives having read them.
        var random = new DeterministicRandom(0xC0FFEE);
        var buffer = new byte[64];

        for (var packet = 0; packet < 10_000; packet++) {
            var length = (int)(random.NextDouble() * buffer.Length);

            for (var i = 0; i < length; i++) {
                buffer[i] = (byte)(random.NextUInt64() & 0xFF);
            }

            Decode(buffer.AsSpan(0, length));
        }
    }

    static void Decode(ReadOnlySpan<byte> packet) {
        var reader = new PacketReader(packet);

        reader.TryReadByte(out _);
        reader.TryReadBool(out _);
        reader.TryReadUInt16(out _);
        reader.TryReadUInt32(out _);
        reader.TryReadInt32(out _);
        reader.TryReadSingle(out _);
        reader.TryReadTick(out _);
        reader.TryReadVariable(out _);
        reader.TryReadBlob(32, out _);
        reader.TryReadString(32, out _);
        reader.TryReadRaw(4, out _);
        reader.TryReadRaw(-1, out _);
    }
}
