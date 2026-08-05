// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using Vixen.Core.Serialization.Storage;
using Xunit;

namespace Vixen.Core.Serialization.Tests;

/// <summary>Files that are not what they say they are, and what the two storage formats do about it.</summary>
/// <remarks>
///     <para>
///         <b>Every case here is a number in the file deciding how much memory to allocate or where a
///         slice lands.</b> That is the whole class of defect a storage format has, and it has two
///         failure modes rather than one: the wrong exception type — an
///         <c>ArgumentOutOfRangeException</c> out of a decoder whose contract is
///         <see cref="SerializationException" /> — and the allocation that happened before the
///         complaint.
///     </para>
///     <para>
///         The second is the one worth measuring rather than reasoning about, so the amplification
///         cases weigh the decode instead of asserting about it. A refusal that costs two hundred
///         megabytes is not a refusal.
///     </para>
/// </remarks>
public sealed class MalformedStorageTests {
    static byte[] Bundle() {
        var writer = new BundleWriter();
        writer.Add(new(1, 1), new byte[] { 1, 2, 3, 4 });
        writer.Add(new(2, 2), new byte[] { 5, 6, 7, 8 });

        return writer.Build();
    }

    /// <summary>The entry's offset and length are 32-bit unsigned, and narrowing them first is the bug.</summary>
    /// <remarks>
    ///     A length of 0x80000000 read as an <c>int</c> is negative, so <c>payloadStart + offset +
    ///     length</c> lands below the file's length and the bounds check passes on its way to a slice
    ///     that throws.
    /// </remarks>
    [Fact]
    public void AnEntryLengthAbove2GbIsRefusedRatherThanSliced() {
        var bundle = Bundle();
        BinaryPrimitives.WriteUInt32LittleEndian(bundle.AsSpan(24 + 20), 0x80000000u);

        using var backend = new BundleOdbBackend(bundle);

        Assert.Throws<SerializationException>(() => backend.TryRead(new(1, 1), out _));
    }

    /// <summary>Two lengths that each fit and together overflow.</summary>
    [Fact]
    public void AnOffsetAndLengthThatOverflowTogetherAreRefused() {
        var bundle = Bundle();
        BinaryPrimitives.WriteUInt32LittleEndian(bundle.AsSpan(24 + 16), 0x7FFFFFFFu);
        BinaryPrimitives.WriteUInt32LittleEndian(bundle.AsSpan(24 + 20), 0x7FFFFFFFu);

        using var backend = new BundleOdbBackend(bundle);

        Assert.Throws<SerializationException>(() => backend.TryRead(new(1, 1), out _));
    }

    /// <summary>An entry count whose index cannot fit in the file is refused at the door.</summary>
    /// <remarks>
    ///     ⚠ 100 million entries is 2.4 GB of index, which overflows a 32-bit
    ///     <c>HeaderSize + count × EntrySize</c> to a negative number — so a check written in
    ///     <c>int</c> concludes the index fits, and every read afterwards is past the end.
    /// </remarks>
    [Fact]
    public void AnEntryCountWhoseIndexOverflowsIsRefused() {
        var bundle = Bundle();
        BinaryPrimitives.WriteUInt32LittleEndian(bundle.AsSpan(12), 100_000_000u);

        Assert.Throws<SerializationException>(() => new BundleOdbBackend(bundle));
    }

    /// <summary>An index in the wrong order is refused rather than answering "not found".</summary>
    /// <remarks>
    ///     ⚠ <b>The failure this replaces was silent.</b> A lookup is a binary search, and a binary
    ///     search over an unsorted index does not fail — it reports content the file holds as
    ///     missing, and the error arrives much later as an asset that will not load.
    /// </remarks>
    [Fact]
    public void AnUnsortedIndexIsRefusedRatherThanSilentlyLosingEntries() {
        var bundle = Bundle();
        var first = bundle.AsSpan(24, 16).ToArray();

        bundle.AsSpan(24 + 24, 16).CopyTo(bundle.AsSpan(24));
        first.CopyTo(bundle.AsSpan(24 + 24));

        Assert.Throws<SerializationException>(() => new BundleOdbBackend(bundle));
    }

    [Fact]
    public void AWellFormedBundleStillOpensAndReads() {
        using var backend = new BundleOdbBackend(Bundle(), verifyChecksum: true);

        Assert.Equal(2, backend.Count);
        Assert.True(backend.TryRead(new(1, 1), out var blob));
        Assert.Equal([1, 2, 3, 4], blob.Bytes.ToArray());
        Assert.True(backend.Exists(new(2, 2)));
    }

    // ── the chunk container ─────────────────────────────────────────────────────────────────────

    static byte[] Blob(CompressionMethod method, ulong declared, params byte[] body) => [
        (byte)method, .. Varint(declared), .. body
    ];

    static byte[] Varint(ulong value) {
        var bytes = new List<byte>();

        while (true) {
            var current = (byte)(value & 0x7F);
            value >>= 7;

            if (value == 0) {
                bytes.Add(current);

                return [.. bytes];
            }

            bytes.Add((byte)(current | 0x80));
        }
    }

    /// <summary>A declared length no body of that size could produce is refused before it is allocated.</summary>
    /// <remarks>
    ///     ⚠ <b>Weighed rather than asserted about, because the exception type was never the
    ///     problem.</b> This case already threw <see cref="SerializationException" /> — after
    ///     allocating the two hundred megabytes it was about to complain about. A backend reading a
    ///     thousand of these is a process in permanent collection, and nothing in a log says so.
    /// </remarks>
    [Fact]
    public void AnLz4ChunkClaimingMoreThanTheFormatCanReachCostsNothingToRefuse() {
        var blob = Blob(CompressionMethod.Lz4, 200_000_000, 1, 2, 3, 4);

        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<SerializationException>(() => ChunkFormat.Unpack(blob, out _));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 64 * 1024, $"Refusing an eight-byte blob cost {allocated:N0} bytes.");
    }

    /// <summary>The same for Zstd, whose frame header is asked before a buffer exists.</summary>
    [Fact]
    public void AZstdChunkClaimingMoreThanItsFrameSaysCostsNothingToRefuse() {
        var blob = Blob(CompressionMethod.Zstd, 200_000_000, 1, 2, 3, 4);

        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<SerializationException>(() => ChunkFormat.Unpack(blob, out _));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 64 * 1024, $"Refusing an eight-byte blob cost {allocated:N0} bytes.");
    }

    /// <summary>A length near <see cref="int.MaxValue" /> was an OutOfMemoryException rather than a refusal.</summary>
    [Fact]
    public void ALengthThatWouldNotFitInAnArrayIsARefusalRatherThanAnOutOfMemory() =>
        Assert.Throws<SerializationException>(
            () => ChunkFormat.Unpack(Blob(CompressionMethod.Lz4, 0x7FFFFFFF, 1, 2, 3, 4), out _)
        );

    /// <summary>A length past what an <c>int</c> holds is refused rather than wrapping to a plausible one.</summary>
    /// <remarks>
    ///     ⚠ 2^40 narrowed to an <c>int</c> is zero, so the chunk was diagnosed as claiming nothing —
    ///     which reads as an encoder that wrote an empty chunk rather than a file that is lying.
    /// </remarks>
    [Fact]
    public void ALengthAboveIntMaxIsRefusedRatherThanTruncated() {
        var failure = Assert.Throws<SerializationException>(
            () => ChunkFormat.Unpack(Blob(CompressionMethod.Lz4, 1UL << 40, 1, 2, 3, 4), out _)
        );

        Assert.Contains("1099511627776", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A body that is not a Zstd frame is the library's exception, and this method's contract is not.</summary>
    [Fact]
    public void AZstdBodyThatIsNotAFrameIsASerializationException() =>
        Assert.Throws<SerializationException>(
            () => ChunkFormat.Unpack(Blob(CompressionMethod.Zstd, 4, 1, 2, 3, 4), out _)
        );

    /// <summary>A header naming more references than follow it is refused before the builder is sized.</summary>
    [Fact]
    public void AChunkHeaderNamingMoreReferencesThanFollowIsRefused() {
        // Version 1, a type id, and a reference count of ten million with nothing behind it.
        byte[] chunk = [1, 1, 2, 3, 4, 5, 6, 7, 8, .. Varint(10_000_000)];

        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<SerializationException>(() => ChunkFormat.ReadHeader(chunk, out _, out _));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 64 * 1024, $"Refusing a fourteen-byte header cost {allocated:N0} bytes.");
    }

    /// <summary>All three methods still round-trip, which is what the bounds above must not cost.</summary>
    [Theory]
    [InlineData(CompressionMethod.None)]
    [InlineData(CompressionMethod.Lz4)]
    [InlineData(CompressionMethod.Zstd)]
    public void AWellFormedChunkStillRoundTrips(CompressionMethod method) {
        var payload = new byte[4096];

        for (var index = 0; index < payload.Length; index++) {
            payload[index] = (byte)(index % 7);
        }

        var chunk = ChunkFormat.BuildChunk(0xC0FFEE, [new(1, 2)], payload);
        var back = ChunkFormat.Unpack(ChunkFormat.Pack(chunk, method), out _);

        Assert.Equal(chunk, back);

        var start = ChunkFormat.ReadHeader(back, out var typeId, out var references);

        Assert.Equal(0xC0FFEEUL, typeId);
        Assert.Equal([new ObjectId(1, 2)], references);
        Assert.Equal(payload, back.AsSpan(start).ToArray());
    }
}
