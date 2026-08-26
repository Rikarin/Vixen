// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Vixen.Core.Serialization.Storage;

/// <summary>Content-addressed storage: values in, <see cref="ObjectId" />s out.</summary>
/// <remarks>
///     <para>
///         An id is the hash of the content, which gives three things for the price of one.
///         <b>Deduplication</b>: two materials with identical parameters are one chunk, without
///         anybody comparing them. <b>Integrity</b>: a chunk that does not hash to its own name is
///         corrupt, and <see cref="Verify" /> says so. <b>Delta detection</b>: an update knows what
///         changed by comparing names, so a patch ships the chunks whose content differs and nothing
///         else.
///     </para>
///     <para>
///         Backends are searched in order, and only the first accepts writes. That is the shape the
///         editor wants: loose files first, then the bundles the last content build produced, so an
///         artefact that has been rebuilt shadows the packed one without either of them knowing.
///     </para>
/// </remarks>
public sealed class ObjectDatabase {
    readonly Lock gate = new();

    /// <summary>
    ///     The backends, replaced wholesale rather than added to, so that a read never walks a
    ///     collection somebody is mutating.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>An array and not a list, because every read enumerates it and only <see cref="Mount" />
    ///     writes it.</b> <see cref="Mount" /> took a lock and the readers did not, which is a
    ///     "collection was modified" thrown out of an unrelated <see cref="Exists" /> the moment a
    ///     bundle arrives while anything is loading — and with a parallel import there is always
    ///     something loading. Copy-on-write under the lock makes the readers correct with no lock at
    ///     all: they see the array as it was, which is a valid answer at the moment they asked.
    /// </remarks>
    volatile IOdbBackend[] backends;

    /// <summary>How chunks are compressed when the caller does not say.</summary>
    /// <remarks>
    ///     LZ4 by default: content that ships on the device is read far more often than it is
    ///     written, and decode speed is what a loading screen is made of. A content build producing
    ///     downloadable bundles sets this to <see cref="CompressionMethod.Zstd" /> instead, where the
    ///     scarce resource is the user's bandwidth rather than their CPU.
    /// </remarks>
    public CompressionMethod DefaultCompression { get; set; } = CompressionMethod.Lz4;

    /// <summary>The backends, most specific first.</summary>
    public IReadOnlyList<IOdbBackend> Backends => backends;

    /// <summary>Creates a database over any number of backends.</summary>
    /// <param name="backends">Searched in order. The first one takes the writes.</param>
    /// <remarks>
    ///     <b>Zero is allowed, and is the runtime's case.</b> This used to insist on at least one,
    ///     which was right while backends were fixed at construction; with <see cref="Mount" /> a
    ///     game legitimately starts with an empty database and gains a backend for each bundle the
    ///     catalog names as it needs it. A read from an empty database fails by saying the chunk is
    ///     missing, which is the same thing it says when the chunk is missing from a full one.
    /// </remarks>
    public ObjectDatabase(params IOdbBackend[] backends) {
        ArgumentNullException.ThrowIfNull(backends);
        this.backends = [.. backends];
    }

    /// <summary>Adds a backend that was not known when the database was created.</summary>
    /// <param name="backend">The backend.</param>
    /// <returns><see langword="false" /> if it was already mounted.</returns>
    /// <remarks>
    ///     <para>
    ///         Bundles are discovered at run time — the catalog names one, the provider fetches it,
    ///         and only then is there a backend to read from. Requiring every backend at construction
    ///         would mean opening every bundle a game might ever use before it draws a frame.
    ///     </para>
    ///     <para>
    ///         Mounted last, so a bundle never shadows the loose files an editor is rebuilding into.
    ///         Adding the same instance twice is a no-op rather than an error, because a load and the
    ///         preload that raced it both legitimately arrive here with the same bundle.
    ///     </para>
    /// </remarks>
    public bool Mount(IOdbBackend backend) {
        ArgumentNullException.ThrowIfNull(backend);

        lock (gate) {
            if (Array.IndexOf(backends, backend) >= 0) {
                return false;
            }

            backends = [.. backends, backend];
            return true;
        }
    }

    /// <summary>Serialises a value and stores it.</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="references">Other chunks this one needs, recorded in its header.</param>
    /// <param name="compression">How to store it, or <see langword="null" /> for <see cref="DefaultCompression" />.</param>
    /// <returns>The id naming the content.</returns>
    public ObjectId Write<T>(
        in T value,
        ReadOnlySpan<ObjectId> references = default,
        CompressionMethod? compression = null
    ) {
        var payload = new ArrayBufferWriter<byte>();
        Serializer.Write(payload, in value);
        return WriteRaw(ContentHash.TypeId(typeof(T)), references, payload.WrittenSpan, compression);
    }

    /// <summary>Stores an already-serialised payload.</summary>
    /// <param name="typeId">Which type wrote it, per <see cref="ContentHash.TypeId" />.</param>
    /// <param name="references">Other chunks it needs.</param>
    /// <param name="payload">The bytes.</param>
    /// <param name="compression">How to store it, or <see langword="null" /> for <see cref="DefaultCompression" />.</param>
    /// <returns>The id naming the content.</returns>
    /// <remarks>
    ///     The entry point for the content build, which produces payloads with tools that are not
    ///     this serializer — a texture compressor, an audio encoder — and still wants them
    ///     content-addressed.
    /// </remarks>
    public ObjectId WriteRaw(
        ulong typeId,
        ReadOnlySpan<ObjectId> references,
        ReadOnlySpan<byte> payload,
        CompressionMethod? compression = null
    ) {
        var chunk = ChunkFormat.BuildChunk(typeId, references, payload);

        // The id names the chunk, not the blob: compression is a storage policy and two builds that
        // disagree about it still have to agree about what the content is called.
        var id = ContentHash.Compute(chunk);

        if (!Exists(id)) {
            if (backends.Length == 0) {
                throw new InvalidOperationException(
                    "This database has no backends, so there is nowhere to write. A runtime database starts empty "
                    + "and mounts a read-only backend per bundle; writing needs one that was given at construction."
                );
            }

            backends[0].Write(id, ChunkFormat.Pack(chunk, compression ?? DefaultCompression));
        }

        return id;
    }

    /// <summary>Reads a value back.</summary>
    /// <typeparam name="T">The type it was written as.</typeparam>
    /// <param name="id">The chunk.</param>
    /// <returns>The value.</returns>
    /// <exception cref="SerializationException">The chunk is missing, or was written as another type.</exception>
    public T Read<T>(ObjectId id) {
        var chunk = ReadChunk(id);
        var payload = ChunkFormat.ReadHeader(chunk, out var typeId, out _);
        var expected = ContentHash.TypeId(typeof(T));

        if (typeId != expected) {
            // Reading a chunk as the wrong type would otherwise deserialise one type's bytes into
            // another's fields and succeed often enough to be confusing.
            throw new SerializationException(
                $"Chunk {id} was written by type {typeId:x16} and is being read as '{typeof(T)}' ({expected:x16})."
            );
        }

        return Serializer.Read<T>(chunk.AsSpan(payload));
    }

    /// <summary>Reads a value back without knowing its type.</summary>
    /// <param name="id">The chunk.</param>
    /// <returns>The value.</returns>
    /// <exception cref="SerializationException">The chunk is missing, or its type is not registered here.</exception>
    /// <remarks>
    ///     <para>
    ///         What a content loader walks a dependency graph with. Loading a material means loading
    ///         the texture it points at first, and the only thing the loader knows about that texture
    ///         is an id — the static type is in the material's own fields, which have not been read
    ///         yet.
    ///     </para>
    ///     <para>
    ///         The type comes from the chunk header and the serializer from the registry, so nothing
    ///         here reflects: every type that can appear was registered by a module initializer the
    ///         generator wrote.
    ///     </para>
    /// </remarks>
    public object ReadObject(ObjectId id) {
        if (TryReadObject(id, out var value)) {
            return value;
        }

        // The header is read a second time purely for the message. A caller that used this asked for
        // an object, and which type wrote the chunk is the first thing they need — it is what says
        // whether an assembly is missing or the content is something else entirely. Paid only on the
        // path that is about to throw.
        ChunkFormat.ReadHeader(ReadChunk(id), out var typeId, out _);

        throw new SerializationException(
            $"Chunk {id} was written by type {typeId:x16}, and nothing registered in this process claims it. "
            + "The assembly that defines it is either not loaded or has no [DataContract] on the type. If it is "
            + "a payload a tool produced — a compressed texture, a mesh page blob — read it with ReadRaw instead."
        );
    }

    /// <summary>Reads a value back without knowing its type, if anything here can.</summary>
    /// <param name="id">The chunk.</param>
    /// <param name="value">The value, or <see langword="null" /> if nothing claims its type.</param>
    /// <returns>Whether it was read.</returns>
    /// <exception cref="SerializationException">The chunk is missing.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>"Nothing claims this type" is a question and not a failure, and separating the two
    ///         is the whole point of this overload.</b> A chunk this cannot read is either a bug — an
    ///         assembly that should have been loaded and was not — or perfectly ordinary content that
    ///         was never meant to go through a serializer at all: a compressed texture, an audio
    ///         bitstream, a mesh's cluster and page blobs. <see cref="WriteRaw" /> exists to put those
    ///         in and <see cref="ReadRaw" /> to take them out, and nothing about the chunk says which
    ///         of the two situations it is in.
    ///     </para>
    ///     <para>
    ///         So a walker of dependency graphs asks rather than assumes. A missing chunk is still an
    ///         exception, because that is the same failure whoever is asking: the content is not
    ///         there.
    ///     </para>
    /// </remarks>
    public bool TryReadObject(ObjectId id, [NotNullWhen(true)] out object? value) {
        var chunk = ReadChunk(id);
        var payload = ChunkFormat.ReadHeader(chunk, out var typeId, out _);

        if (!SerializerRegistry.TryGetByTypeId(typeId, out var serializer)) {
            value = null;
            return false;
        }

        var reader = new SerializationReader(chunk.AsSpan(payload));

        value = serializer.DeserializeObject(ref reader);
        return true;
    }

    /// <summary>Reads a chunk's payload without deserialising it.</summary>
    /// <param name="id">The chunk.</param>
    /// <param name="typeId">Which type wrote it, per <see cref="ContentHash.TypeId" />.</param>
    /// <returns>The bytes, decompressed, with the chunk header stripped.</returns>
    /// <exception cref="SerializationException">It is not here.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>The counterpart to <see cref="WriteRaw" />, and it exists for the same content the
    ///         content build put in with that.</b> A payload produced by a tool that is not this
    ///         serializer — a compressed texture, an audio bitstream, a video container — has no
    ///         serializer to read it back with, so <see cref="Read{T}" /> cannot and
    ///         <see cref="ReadObject" /> cannot. This is how it comes out.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The type is reported rather than checked, which is the opposite of
    ///         <see cref="Read{T}" />.</b> There is no type to check against: the caller asked for
    ///         bytes, and what would make the check meaningful — a serializer registered for that id —
    ///         is precisely what this path is for the absence of. A caller that cares compares the
    ///         number it is handed.
    ///     </para>
    /// </remarks>
    public byte[] ReadRaw(ObjectId id, out ulong typeId) {
        var chunk = ReadChunk(id);
        var payload = ChunkFormat.ReadHeader(chunk, out typeId, out _);

        return chunk.AsSpan(payload).ToArray();
    }

    /// <summary>Reads a chunk's header without deserialising it.</summary>
    /// <param name="id">The chunk.</param>
    /// <param name="info">What it says about itself.</param>
    /// <returns><see langword="false" /> if it is not here.</returns>
    /// <remarks>
    ///     What a loader walks the dependency graph with, and what a bundle packer groups by, neither
    ///     of which needs to understand a single asset type.
    /// </remarks>
    public bool TryDescribe(ObjectId id, out ChunkInfo info) {
        if (!TryReadBlob(id, out var blob)) {
            info = default;
            return false;
        }

        using (blob) {
            var stored = blob.Bytes.Length;
            var chunk = ChunkFormat.Unpack(blob.Bytes.Span, out var compression);
            var payload = ChunkFormat.ReadHeader(chunk, out var typeId, out var references);
            info = new(id, typeId, references, chunk.Length - payload, compression, stored);
            return true;
        }
    }

    /// <summary>Whether a chunk is in any backend.</summary>
    /// <param name="id">The chunk.</param>
    /// <returns><see langword="true" /> if it is.</returns>
    public bool Exists(ObjectId id) {
        foreach (var backend in backends) {
            if (backend.Exists(id)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Re-hashes a chunk and checks that it still answers to its own name.</summary>
    /// <param name="id">The chunk.</param>
    /// <returns><see langword="false" /> if it is missing or its content does not match its id.</returns>
    /// <remarks>
    ///     The integrity check content addressing gives away. It is not free — it decompresses and
    ///     hashes — so it belongs to a verification pass rather than to every load.
    /// </remarks>
    public bool Verify(ObjectId id) {
        if (!TryReadBlob(id, out var blob)) {
            return false;
        }

        using (blob) {
            try {
                return ContentHash.Compute(ChunkFormat.Unpack(blob.Bytes.Span, out _)) == id;
            } catch (SerializationException) {
                return false;
            }
        }
    }

    /// <summary>Every chunk in every backend, without duplicates.</summary>
    /// <returns>The ids.</returns>
    public IEnumerable<ObjectId> Enumerate() {
        var seen = new HashSet<ObjectId>();

        foreach (var backend in backends) {
            foreach (var id in backend.Enumerate()) {
                if (seen.Add(id)) {
                    yield return id;
                }
            }
        }
    }

    /// <summary>Collects a chunk and everything it transitively references.</summary>
    /// <param name="roots">Where to start.</param>
    /// <returns>The closure, including the roots.</returns>
    /// <remarks>
    ///     Loading is header-read, resolve, recurse, deserialise; this is the resolve-and-recurse
    ///     half, separated out because the bundle packer needs exactly the same answer for a
    ///     completely different reason.
    /// </remarks>
    public ImmutableArray<ObjectId> Closure(params ReadOnlySpan<ObjectId> roots) {
        var seen = new HashSet<ObjectId>();
        var pending = new Stack<ObjectId>();

        foreach (var root in roots) {
            pending.Push(root);
        }

        var result = ImmutableArray.CreateBuilder<ObjectId>();

        while (pending.Count > 0) {
            var id = pending.Pop();

            // A cycle is possible the moment two assets reference each other, and content addressing
            // does not prevent it — the ids were computed before either knew about the other.
            if (!seen.Add(id) || !TryDescribe(id, out var info)) {
                continue;
            }

            result.Add(id);

            foreach (var reference in info.References) {
                pending.Push(reference);
            }
        }

        return result.ToImmutable();
    }

    byte[] ReadChunk(ObjectId id) {
        if (!TryReadBlob(id, out var blob)) {
            throw new SerializationException($"There is no chunk {id} in any of this database's {backends.Length} backends.");
        }

        using (blob) {
            return ChunkFormat.Unpack(blob.Bytes.Span, out _);
        }
    }

    bool TryReadBlob(ObjectId id, [NotNullWhen(true)] out IOdbBlob? blob) {
        foreach (var backend in backends) {
            if (backend.TryRead(id, out blob)) {
                return true;
            }
        }

        blob = null;
        return false;
    }
}
