// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.Collections.Immutable;
using K4os.Compression.LZ4;

namespace Vixen.Core.Serialization.Storage;

/// <summary>What a stored object looks like, without its payload.</summary>
/// <param name="Id">The content id.</param>
/// <param name="TypeId">Which type wrote it, per <see cref="ContentHash.TypeId" />.</param>
/// <param name="References">Other chunks this one needs.</param>
/// <param name="PayloadLength">How many bytes the payload occupies uncompressed.</param>
/// <param name="Compression">How it is stored.</param>
/// <param name="StoredLength">How many bytes it occupies in the backend.</param>
public readonly record struct ChunkInfo(
    ObjectId Id,
    ulong TypeId,
    ImmutableArray<ObjectId> References,
    int PayloadLength,
    CompressionMethod Compression,
    int StoredLength
);

/// <summary>The chunk container: a header, a payload, and a compression wrapper around both.</summary>
/// <remarks>
///     <para>
///         Two layers, and the boundary between them is the whole design.
///         The <b>chunk</b> — header plus payload — is what gets hashed into an
///         <see cref="ObjectId" />. The <b>blob</b> — a compression byte, the uncompressed length,
///         and the possibly-compressed chunk — is what a backend stores.
///     </para>
///     <para>
///         Keeping compression outside the hashed region is what makes the id a property of the
///         content rather than of the policy. Two builds that disagree about whether to LZ4 a mesh
///         still produce the same id for it, so an incremental update sees no change, a bundle built
///         with different settings still deduplicates against the loose files, and the determinism
///         gate compares content instead of comparing settings.
///     </para>
///     <para>
///         The header carries the references rather than leaving them implicit in the payload,
///         because loading has to be able to resolve a graph without deserialising it first: read
///         header, load what it names, then deserialise. That is also what lets a bundle packer see
///         the dependency graph without understanding a single asset type.
///     </para>
/// </remarks>
public static class ChunkFormat {
    /// <summary>The container version, bumped when the header layout changes.</summary>
    public const int Version = 1;

    /// <summary>Payloads below this are stored uncompressed whatever the policy says.</summary>
    /// <remarks>
    ///     Compressing two hundred bytes spends a frame header to save a handful, and costs a decode
    ///     on every load forever. The threshold is where LZ4 starts to pay for itself on the kind of
    ///     data an engine stores.
    /// </remarks>
    public const int MinimumCompressedSize = 256;

    /// <summary>The most an LZ4 body may claim to decode to, as a multiple of its own length.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The declared length is the attacker's number and it is the one that gets
    ///         allocated</b>, so it is checked against the bytes that are actually present before a
    ///         buffer exists. Eight bytes claiming two hundred megabytes is not a corrupt chunk to be
    ///         diagnosed after the allocation, it is a two-hundred-megabyte allocation bought for
    ///         eight bytes, and a backend reading a thousand of those is a process in permanent
    ///         collection.
    ///     </para>
    ///     <para>
    ///         <b>255 is LZ4's own ceiling rather than a figure somebody picked.</b> A block-format
    ///         sequence is a token, a two-byte offset and a run of length-extension bytes, each of
    ///         which adds 255 to the match — so the ratio tends to 255 and cannot exceed it. A body
    ///         claiming more than this is not compressed data at all, whatever else it might be.
    ///     </para>
    /// </remarks>
    public const int MaximumLz4Expansion = 255;

    /// <summary>The same, for Zstd.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>A policy cap rather than a property of the format, which is why it is a separate
    ///         number.</b> Zstd's run-length blocks encode a hundred and twenty-eight kilobytes of one
    ///         byte in about five, so the format's own ceiling is in the tens of thousands and bounds
    ///         nothing worth bounding. This is the ratio past which a chunk is assumed to be a claim
    ///         rather than a payload; it is well above what the engine's own content reaches, and it
    ///         is here to be changed in one place if some asset ever proves otherwise.
    ///     </para>
    ///     <para>
    ///         It is the second of two checks and not the only one: the frame header is asked what it
    ///         decodes to first, so a body that is not a Zstd frame is refused before anything is
    ///         allocated at all.
    ///     </para>
    /// </remarks>
    public const int MaximumZstdExpansion = 4096;

    /// <summary>Builds a chunk: the header and payload that together get hashed.</summary>
    /// <param name="typeId">Which type wrote the payload.</param>
    /// <param name="references">Other chunks the payload needs.</param>
    /// <param name="payload">The serialised bytes.</param>
    /// <returns>The chunk.</returns>
    public static byte[] BuildChunk(ulong typeId, ReadOnlySpan<ObjectId> references, ReadOnlySpan<byte> payload) {
        var buffer = new ArrayBufferWriter<byte>(payload.Length + 32 + (references.Length * ObjectId.SizeInBytes));
        var writer = new SerializationWriter(buffer);
        writer.WriteVarUInt64(Version);
        writer.WriteUInt64(typeId);
        writer.WriteVarUInt64((ulong)references.Length);

        foreach (var reference in references) {
            writer.WriteUInt64(reference.High);
            writer.WriteUInt64(reference.Low);
        }

        writer.WriteBytes(payload);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Reads a chunk's header.</summary>
    /// <param name="chunk">The chunk.</param>
    /// <param name="typeId">Which type wrote it.</param>
    /// <param name="references">What it needs.</param>
    /// <returns>Where the payload starts.</returns>
    /// <exception cref="SerializationException">
    ///     The container version is not one this build knows, or it names more references than follow it.
    /// </exception>
    public static int ReadHeader(ReadOnlySpan<byte> chunk, out ulong typeId, out ImmutableArray<ObjectId> references) {
        var reader = new SerializationReader(chunk);
        var version = (int)reader.ReadVarUInt64();

        if (version != Version) {
            throw new SerializationException(
                $"The chunk container is version {version} and this build reads version {Version}."
            );
        }

        typeId = reader.ReadUInt64();

        var count = reader.ReadVarUInt64();

        if (count == 0) {
            references = [];
            return reader.BytesRead;
        }

        // ⚠ Against what is left rather than against nothing, and before the builder is sized. A
        // reference is two fixed 64-bit fields, so a chunk that has the bytes for n of them cannot
        // honestly declare more — and a chunk that declares a hundred million buys a builder for a
        // hundred million out of a varint five bytes long.
        if (count > (ulong)(reader.Remaining / (ObjectId.SizeInBytes))) {
            throw new SerializationException(
                $"A chunk header names {count} references and {reader.Remaining} bytes follow it, which is not "
                + "enough to hold them. The chunk is truncated or the count is not one."
            );
        }

        var builder = ImmutableArray.CreateBuilder<ObjectId>((int)count);

        for (var index = 0UL; index < count; index++) {
            var high = reader.ReadUInt64();
            builder.Add(new(high, reader.ReadUInt64()));
        }

        references = builder.MoveToImmutable();
        return reader.BytesRead;
    }

    /// <summary>Wraps a chunk for storage, compressing it if that is worth doing.</summary>
    /// <param name="chunk">The chunk.</param>
    /// <param name="method">The compression to attempt.</param>
    /// <returns>The blob a backend stores.</returns>
    public static byte[] Pack(ReadOnlySpan<byte> chunk, CompressionMethod method) {
        var chosen = chunk.Length < MinimumCompressedSize ? CompressionMethod.None : method;
        var compressed = chosen == CompressionMethod.None ? null : Compress(chunk, chosen);

        // Compression that made it bigger is compression that did not happen. This is not
        // hypothetical: it is what every already-compressed texture payload does.
        if (compressed is null || compressed.Length >= chunk.Length) {
            chosen = CompressionMethod.None;
            compressed = null;
        }

        var body = compressed ?? chunk.ToArray();
        var buffer = new ArrayBufferWriter<byte>(body.Length + 16);
        var writer = new SerializationWriter(buffer);
        writer.WriteByte((byte)chosen);
        writer.WriteVarUInt64((ulong)chunk.Length);
        writer.WriteBytes(body);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Unwraps a stored blob back into a chunk.</summary>
    /// <param name="blob">The blob.</param>
    /// <param name="method">How it was stored.</param>
    /// <returns>The chunk.</returns>
    /// <exception cref="SerializationException">
    ///     The blob is truncated, names an unknown method, claims a length its body cannot justify, or
    ///     does not decode.
    /// </exception>
    /// <remarks>
    ///     ⚠ <b>Nothing is allocated on the strength of the declared length until the body has been
    ///     shown capable of producing it.</b> See <see cref="MaximumLz4Expansion" />: the length is a
    ///     number from the file, and a decoder that allocates it first and complains second has
    ///     already done the expensive half of what it is refusing.
    /// </remarks>
    public static byte[] Unpack(ReadOnlySpan<byte> blob, out CompressionMethod method) {
        var reader = new SerializationReader(blob);
        method = (CompressionMethod)reader.ReadByte();

        // Read wide and narrowed only after the range test. Narrowing first turns a declared length
        // of 2^40 into zero, and the chunk that "claims 0 bytes" is then diagnosed as an encoder bug
        // rather than as the lie it is.
        var declared = reader.ReadVarUInt64();

        if (declared > int.MaxValue) {
            throw new SerializationException(
                $"A chunk claims {declared} bytes, which is past the largest array this runtime can hold."
            );
        }

        var length = (int)declared;
        var body = reader.ReadBytes(reader.Remaining);

        switch (method) {
            case CompressionMethod.None:
                if (body.Length != length) {
                    throw new SerializationException(
                        $"An uncompressed chunk claims {length} bytes and holds {body.Length}."
                    );
                }

                return body.ToArray();

            case CompressionMethod.Lz4: {
                Affordable(length, body.Length, MaximumLz4Expansion, "LZ4");

                var chunk = new byte[length];
                var written = LZ4Codec.Decode(body, chunk);

                if (written != length) {
                    throw new SerializationException($"An LZ4 chunk claims {length} bytes and decoded to {written}.");
                }

                return chunk;
            }

            case CompressionMethod.Zstd: {
                Affordable(length, body.Length, MaximumZstdExpansion, "Zstd");

                // The frame header, which is a read of the first few bytes and not a decode. A body
                // that is not a frame is refused here, before the buffer below exists — which is what
                // keeps a blob of arbitrary bytes from costing whatever it felt like declaring.
                if (Declared(body) != (ulong)length) {
                    throw new SerializationException(
                        $"A Zstd chunk claims {length} bytes and its frame header does not say the same, so it is "
                        + "not the chunk this blob's header describes."
                    );
                }

                var chunk = new byte[length];
                using var decompressor = new ZstdSharp.Decompressor();
                int written;

                try {
                    written = decompressor.Unwrap(body, chunk);
                }
#pragma warning disable CA1031 // The library's own exception type is not part of this method's contract.
                catch (Exception failure) {
                    throw new SerializationException($"A Zstd chunk did not decode: {failure.Message}", failure);
                }
#pragma warning restore CA1031

                if (written != length) {
                    throw new SerializationException($"A Zstd chunk claims {length} bytes and decoded to {written}.");
                }

                return chunk;
            }

            default:
                throw new SerializationException(
                    $"A chunk names compression method {(byte)method}, which this build does not know."
                );
        }
    }

    /// <summary>Refuses a declared length the stored bytes could not have produced.</summary>
    static void Affordable(int length, int stored, int expansion, string what) {
        if ((long)stored * expansion < length) {
            throw new SerializationException(
                $"A {what} chunk of {stored} stored bytes claims to decode to {length}, which is past the "
                + $"{expansion}× this format can reach. The blob is corrupt, or is a small message asking for a "
                + "large allocation."
            );
        }
    }

    /// <summary>What a Zstd frame header says it decodes to, or <see cref="ulong.MaxValue" /> for anything else.</summary>
    /// <remarks>
    ///     A frame the engine wrote always carries its content size, because <c>Compressor.Wrap</c>
    ///     knows it — so requiring one costs nothing here and refuses a streamed frame this format
    ///     does not produce, along with every body that is not a frame at all.
    /// </remarks>
    static ulong Declared(ReadOnlySpan<byte> body) {
        try {
            return ZstdSharp.Decompressor.GetDecompressedSize(body);
        }
#pragma warning disable CA1031 // An unreadable header is the answer, not an exception to propagate.
        catch (Exception) {
            return ulong.MaxValue;
        }
#pragma warning restore CA1031
    }

    static byte[]? Compress(ReadOnlySpan<byte> chunk, CompressionMethod method) {
        switch (method) {
            case CompressionMethod.Lz4: {
                var buffer = new byte[LZ4Codec.MaximumOutputSize(chunk.Length)];
                var written = LZ4Codec.Encode(chunk, buffer, LZ4Level.L00_FAST);
                return written <= 0 ? null : buffer.AsSpan(0, written).ToArray();
            }

            case CompressionMethod.Zstd: {
                using var compressor = new ZstdSharp.Compressor();
                return compressor.Wrap(chunk).ToArray();
            }

            default:
                return null;
        }
    }
}
