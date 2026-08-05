// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Serialization;
using Vixen.Core.Serialization.Storage;
using Vixen.Terrain;

namespace Vixen.Net.Fuzz.Targets;

/// <summary>The content formats: files rather than packets, and the same problem.</summary>
/// <remarks>
///     <para>
///         <b>These three break the pattern the rest of this harness follows, and the break is the
///         point.</b> Every target above reads through a <c>Try…</c> method that returns false, so
///         "nothing escapes" is checked by catching everything and finding nothing. A content format
///         does not work that way: a bundle that is not a bundle is a
///         <see cref="SerializationException" />, and refusing by throwing is the contract rather
///         than a failure of it.
///     </para>
///     <para>
///         So each of these catches <i>exactly</i> the refusal its layer documents and lets
///         everything else through to the session. That is a stronger assertion than the one the
///         packet targets make, not a weaker one: it says an
///         <c>ArgumentOutOfRangeException</c> from a slice, an <c>OutOfMemoryException</c> from a
///         length nobody checked, and an <c>InvalidDataException</c> out of a decompressor are all
///         findings — and each of those was what one of these decoders actually did before it was
///         fixed.
///     </para>
///     <para>
///         ⚠ <b>The allowance is where the real work is.</b> A file format's cost is not bounded by
///         its own length the way a packet's is: a compressed chunk is meant to expand, so "how much
///         may this allocate" cannot be answered with a small multiple and cannot be left unanswered
///         either. Each of these bounds it by the ratio the <i>format</i> permits — LZ4's 255,
///         DEFLATE's 1032 — which is a claim about the encoding rather than a number somebody picked,
///         and is exactly the claim the decoders now enforce.
///     </para>
/// </remarks>
static class ContentSeeds {
    /// <summary>A run of pseudo-random bytes, so a seed is not a page of zeroes.</summary>
    /// <remarks>
    ///     Zeroes compress to nothing and decode through one branch. A payload with structure in it
    ///     makes the compressed seeds a realistic size, which is what keeps the allowance honest.
    /// </remarks>
    public static byte[] Payload(int length, ulong seed) {
        var bytes = new byte[length];
        var state = seed | 1;

        for (var index = 0; index < length; index++) {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;

            // Three values rather than 256, so it compresses like content rather than like noise.
            bytes[index] = (byte)(state % 3);
        }

        return bytes;
    }
}

/// <summary>A <c>.bundle</c>, opened and read out.</summary>
/// <remarks>
///     <para>
///         <b>The first file a shipped game opens and the first one a content update downloads.</b>
///         A bundle's header names an entry count, each entry names an offset and a length, and all
///         three are 32-bit unsigned numbers that decide where a slice lands — so this is the same
///         shape as a packet's length prefix, with a file behind it instead of a datagram.
///     </para>
///     <para>
///         ⚠ <b>Opened with the checksum on.</b> The CRC is optional by design — verifying two
///         hundred megabytes on every open defeats the point of a lazily-read file — but the path
///         that <i>does</i> verify is the one that runs on content off a network, which is the
///         untrusted one. Leaving it off here would fuzz the trusted half.
///     </para>
///     <para>
///         Every id is enumerated and every one is read, because the index and the payload fail
///         differently: an entry can be sorted wrongly, which the enumeration cannot see and the
///         lookup answers "not found" to, and it can point past the end, which only the read finds.
///     </para>
/// </remarks>
public sealed class BundleTarget : IFuzzTarget {
    /// <inheritdoc />
    public string Name => "bundle";

    /// <inheritdoc />
    public string What => "BundleOdbBackend, opened with the checksum on, enumerated and read out";

    /// <summary>How many bytes an input may cause to be allocated.</summary>
    /// <param name="inputLength">How big the input was.</param>
    /// <returns>The allowance.</returns>
    /// <remarks>
    ///     A bundle holds the payload rather than copying it, so a read is a slice and costs one
    ///     small object. The index is twenty-four bytes an entry, so the number of reads is bounded
    ///     by the input; the multiple is what one enumerator, one blob per entry and a refusal
    ///     message come to.
    /// </remarks>
    public long AllowanceFor(int inputLength) => 4096 + (64L * inputLength);

    /// <inheritdoc />
    public void Seed(ICollection<byte[]> corpus) {
        ArgumentNullException.ThrowIfNull(corpus);

        var writer = new BundleWriter();
        writer.Add(new(1, 2), ContentSeeds.Payload(48, 0x1234));
        writer.Add(new(3, 4), ContentSeeds.Payload(16, 0x5678));
        writer.Add(new(5, 6), Array.Empty<byte>());
        corpus.Add(writer.Build());

        // A bundle with nothing in it, which is the shortest thing that is still one, and the
        // header on its own, which is the shape every "is this long enough" check is written for.
        corpus.Add(new BundleWriter().Build());
        corpus.Add(new BundleWriter().Build().AsSpan(0, BundleFormat.HeaderSize).ToArray());
    }

    /// <inheritdoc />
    public long Run(ReadOnlySpan<byte> input) {
        // The span cannot be captured by the backend, which holds a ReadOnlyMemory for as long as it
        // is open. Copying is the honest way to hand it one, and is what a caller mapping a file does
        // by a different route.
        var bytes = input.ToArray();
        long signature = 17;

        BundleOdbBackend backend;

        try {
            backend = new(bytes, verifyChecksum: true);
        } catch (SerializationException failure) {
            return (signature * 31) + failure.Message.Length;
        }

        using (backend) {
            signature = (signature * 31) + backend.Count;

            foreach (var id in backend.Enumerate()) {
                try {
                    signature = (signature * 31) + (backend.TryRead(id, out var blob) ? blob.Bytes.Length + 1 : 0);
                } catch (SerializationException failure) {
                    signature = (signature * 31) + failure.Message.Length;
                }

                // A bundle can name a great many entries in a very few bytes only if the file is
                // that long, so this is bounded by the input — but the signature is not the place to
                // fold a whole index, and a case that reads two thousand chunks is the same
                // behaviour as one that reads sixty-four of them.
                if (signature % 1_000_003 == 0) {
                    break;
                }
            }

            // Asked after the reads, because an id that enumerates and does not exist is exactly
            // what an index in the wrong order produces.
            signature = (signature * 31) + (backend.Count > 0 && backend.Exists(new(1, 2)) ? 7 : 0);
        }

        return signature;
    }
}

/// <summary>A stored blob, unwrapped back into a chunk.</summary>
/// <remarks>
///     <para>
///         <b>The one target here whose whole job is an allocation.</b> A blob is a method byte, a
///         declared uncompressed length, and a body — and the declared length is what gets allocated
///         before anything has established that the body could produce it. That is the amplification
///         the <see cref="FuzzFailure.Allocated" /> oracle exists for, stated in five bytes of
///         varint: a blob eight bytes long claiming two hundred megabytes used to cost two hundred
///         megabytes.
///     </para>
///     <para>
///         The chunk that comes out is read as a header too. A chunk's reference count is another
///         attacker-declared number that sizes a builder, and it is only reachable through a blob
///         that unpacked — so fuzzing the two separately would leave the second unreached.
///     </para>
/// </remarks>
public sealed class ChunkFormatTarget : IFuzzTarget {
    /// <inheritdoc />
    public string Name => "chunk";

    /// <inheritdoc />
    public string What => "ChunkFormat.Unpack and the chunk header behind it";

    /// <summary>How many bytes an input may cause to be allocated.</summary>
    /// <param name="inputLength">How big the input was.</param>
    /// <returns>The allowance.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A multiple of the input where the format used to allow a multiple of the
    ///         declaration, and that difference is the property under test.</b> An LZ4 body may claim
    ///         at most 255× its own length because that is the block format's ceiling, so a decode is
    ///         bounded by the bytes present; before that bound existed the ceiling was
    ///         <c>int.MaxValue</c> and had nothing to do with the input at all.
    ///     </para>
    ///     <para>
    ///         512 rather than 255 because a case that claims the maximum still pays for the buffer,
    ///         the body's own copy and a refusal message on top. Zstd's cap is higher and is not what
    ///         sets this number: reaching it needs a frame header that agrees with the declared
    ///         length, which is not a thing a mutator finds.
    ///     </para>
    /// </remarks>
    public long AllowanceFor(int inputLength) => 4096 + (512L * inputLength);

    /// <inheritdoc />
    public void Seed(ICollection<byte[]> corpus) {
        ArgumentNullException.ThrowIfNull(corpus);

        // Through the real writer, so the seeds follow the format rather than a memory of it.
        var chunk = ChunkFormat.BuildChunk(0xC0FFEE, [new(1, 2), new(3, 4)], ContentSeeds.Payload(512, 0x9E37));

        corpus.Add(ChunkFormat.Pack(chunk, CompressionMethod.None));
        corpus.Add(ChunkFormat.Pack(chunk, CompressionMethod.Lz4));
        corpus.Add(ChunkFormat.Pack(chunk, CompressionMethod.Zstd));

        // A chunk with no references and no payload, which is the shortest blob that is still one.
        corpus.Add(ChunkFormat.Pack(ChunkFormat.BuildChunk(1, [], []), CompressionMethod.None));

        // The shapes the mutator would find eventually and that cost nothing to state: each method
        // declaring a length no body of that size could produce.
        corpus.Add([(byte)CompressionMethod.Lz4, 0x80, 0x89, 0x7A, 0x5F, 1, 2, 3, 4]);
        corpus.Add([(byte)CompressionMethod.Zstd, 0x80, 0x89, 0x7A, 0x5F, 1, 2, 3, 4]);
        corpus.Add([(byte)CompressionMethod.None, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F]);
    }

    /// <inheritdoc />
    public long Run(ReadOnlySpan<byte> input) {
        long signature = 17;
        byte[] chunk;

        try {
            chunk = ChunkFormat.Unpack(input, out var method);
            signature = (signature * 31) + (int)method;
        } catch (SerializationException failure) {
            return (signature * 31) + failure.Message.Length;
        }

        signature = (signature * 31) + chunk.Length;

        try {
            var payload = ChunkFormat.ReadHeader(chunk, out var typeId, out var references);

            signature = (signature * 31) + payload + references.Length + (long)(typeId & 0xFF);
        } catch (SerializationException failure) {
            signature = (signature * 31) + failure.Message.Length;
        }

        return signature;
    }
}

/// <summary>A sixteen-bit greyscale PNG, decoded as a heightmap.</summary>
/// <remarks>
///     <para>
///         <b>The only target here whose input is a file somebody was handed.</b> A bundle and a
///         chunk are written by the content build and read by the runtime; a heightmap arrives from
///         World Machine, Gaea or Photoshop, or from a download, and is dropped on an importer by a
///         person who has no way to know what is in it. Every chunk length in it is a 32-bit number
///         that decides a slice, and its IHDR is eight bytes that decide how large two buffers are.
///     </para>
///     <para>
///         ⚠ <b><see cref="ArgumentException" /> is the refusal and nothing else is.</b> The importer
///         above this catches exactly that to report a bad file, so an
///         <c>OverflowException</c> from a dimension nobody bounded and an
///         <c>EndOfStreamException</c> from a truncated deflate stream are not refusals — they are an
///         importer crash with somebody's terrain in it, and they are what this target is here to
///         keep out.
///     </para>
/// </remarks>
public sealed class HeightmapPngTarget : IFuzzTarget {
    /// <inheritdoc />
    public string Name => "heightmap";

    /// <inheritdoc />
    public string What => "TerrainHeightmapPng.Decode, over a file an importer was handed";

    /// <summary>How many bytes an input may cause to be allocated.</summary>
    /// <param name="inputLength">How big the input was.</param>
    /// <returns>The allowance.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Large, and the size of it is the claim.</b> A PNG is meant to expand — that is
    ///         what deflate is for — so this cannot be the small multiple a packet target uses. What
    ///         it can be is DEFLATE's own ceiling: no valid stream expands more than 1032×, so the
    ///         row buffer is bounded by the IDAT bytes present, and the sample array and the two
    ///         filter rows come to about the same again.
    ///     </para>
    ///     <para>
    ///         Before that bound existed the number could not have been written down at all: forty
    ///         bytes of header declaring 500000×500000 asked for half a terabyte, and no multiple of
    ///         forty describes it.
    ///     </para>
    /// </remarks>
    public long AllowanceFor(int inputLength) => 16384 + (4096L * inputLength);

    /// <inheritdoc />
    public void Seed(ICollection<byte[]> corpus) {
        ArgumentNullException.ThrowIfNull(corpus);

        // ⚠ From the format's own encoder rather than from Editor.Assets.Tests' MinimalPng, which
        // writes eight-bit RGBA — a file this decoder refuses at the IHDR, three branches before
        // anything interesting. A seed that is rejected immediately seeds nothing.
        corpus.Add(TerrainHeightmapPng.Encode(32, 32, Slope(32, 32)));
        corpus.Add(TerrainHeightmapPng.Encode(1, 1, [0x8000]));
        corpus.Add(TerrainHeightmapPng.Encode(17, 3, Slope(17, 3)));
    }

    /// <inheritdoc />
    public long Run(ReadOnlySpan<byte> input) {
        long signature = 17;

        try {
            var image = TerrainHeightmapPng.Decode(input);

            signature = (signature * 31) + image.Width;
            signature = (signature * 31) + image.Height;
            signature = (signature * 31) + image.Samples.Length;

            if (image.Samples.Length > 0) {
                signature = (signature * 31) + image.Samples[0] + image.Samples[^1];
            }
        } catch (ArgumentException failure) {
            signature = (signature * 31) + failure.Message.Length;
        }

        return signature;
    }

    /// <summary>A gradient, which is what a heightmap is and is why they compress.</summary>
    /// <remarks>
    ///     Smooth on purpose. A seed of noise encodes to a file larger than the mutator's cap, gets
    ///     truncated on its way in, and is then a corrupt PNG rather than a valid one to mutate.
    /// </remarks>
    static ushort[] Slope(int width, int height) {
        var samples = new ushort[width * height];

        for (var y = 0; y < height; y++) {
            for (var x = 0; x < width; x++) {
                samples[(y * width) + x] = (ushort)(((x * 137) + (y * 521)) & 0xFFFF);
            }
        }

        return samples;
    }
}
