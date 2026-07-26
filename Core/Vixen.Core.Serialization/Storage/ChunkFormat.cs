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
    /// <exception cref="SerializationException">The container version is not one this build knows.</exception>
    public static int ReadHeader(ReadOnlySpan<byte> chunk, out ulong typeId, out ImmutableArray<ObjectId> references) {
        var reader = new SerializationReader(chunk);
        var version = (int)reader.ReadVarUInt64();

        if (version != Version) {
            throw new SerializationException(
                $"The chunk container is version {version} and this build reads version {Version}."
            );
        }

        typeId = reader.ReadUInt64();
        var count = (int)reader.ReadVarUInt64();

        if (count == 0) {
            references = [];
            return reader.BytesRead;
        }

        var builder = ImmutableArray.CreateBuilder<ObjectId>(count);

        for (var index = 0; index < count; index++) {
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
    /// <exception cref="SerializationException">The blob is truncated or names an unknown method.</exception>
    public static byte[] Unpack(ReadOnlySpan<byte> blob, out CompressionMethod method) {
        var reader = new SerializationReader(blob);
        method = (CompressionMethod)reader.ReadByte();
        var length = (int)reader.ReadVarUInt64();
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
                var chunk = new byte[length];
                var written = LZ4Codec.Decode(body, chunk);

                if (written != length) {
                    throw new SerializationException($"An LZ4 chunk claims {length} bytes and decoded to {written}.");
                }

                return chunk;
            }

            case CompressionMethod.Zstd: {
                using var decompressor = new ZstdSharp.Decompressor();
                var chunk = decompressor.Unwrap(body).ToArray();

                if (chunk.Length != length) {
                    throw new SerializationException($"A Zstd chunk claims {length} bytes and decoded to {chunk.Length}.");
                }

                return chunk;
            }

            default:
                throw new SerializationException(
                    $"A chunk names compression method {(byte)method}, which this build does not know."
                );
        }
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
