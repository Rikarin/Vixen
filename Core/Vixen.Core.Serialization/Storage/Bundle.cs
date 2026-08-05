// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO.Hashing;
using System.Text;

namespace Vixen.Core.Serialization.Storage;

/// <summary>The layout of a <c>.bundle</c>, shared by the reader and the writer.</summary>
/// <remarks>
///     <para>
///         A header, an index sorted by <see cref="ObjectId" />, and one contiguous payload region.
///         Sorted so that a lookup is a binary search over sixteen-byte keys with no allocation and
///         no dictionary to build at load time — a bundle can be opened, one chunk read out of two
///         thousand, and closed, without touching the other one thousand nine hundred and
///         ninety-nine.
///     </para>
///     <para>
///         The CRC covers the payload region only, and checking it is the caller's choice. Verifying
///         a two-hundred-megabyte bundle on every open costs a full read of a file whose whole point
///         is that it is read lazily; <c>docs/plan/08</c> puts the check on cached and remote content,
///         where the bytes arrived over a network, and not on content that shipped inside the
///         application package.
///     </para>
/// </remarks>
public static class BundleFormat {
    /// <summary>The eight bytes a bundle starts with.</summary>
    public static ReadOnlySpan<byte> Magic => "VXBUNDLE"u8;

    /// <summary>The layout version.</summary>
    public const int Version = 1;

    /// <summary>Bytes before the index.</summary>
    public const int HeaderSize = 24;

    /// <summary>Bytes per index entry: the id, then a 32-bit offset and length.</summary>
    public const int EntrySize = ObjectId.SizeInBytes + 8;

    internal static int PayloadOffset(int entryCount) => HeaderSize + (entryCount * EntrySize);
}

/// <summary>Builds a <c>.bundle</c> from chunks.</summary>
/// <remarks>
///     The content build owns the policy — which chunks go in which bundle, and what compresses how.
///     This owns the bytes. Keeping them apart is what lets the format be tested exhaustively now,
///     years before the packer that will produce them in anger.
/// </remarks>
public sealed class BundleWriter {
    readonly SortedDictionary<ObjectId, ReadOnlyMemory<byte>> entries = [];

    /// <summary>How many chunks are staged.</summary>
    public int Count => entries.Count;

    /// <summary>Stages a chunk. Adding the same id twice keeps one copy.</summary>
    /// <param name="id">The chunk.</param>
    /// <param name="blob">Its stored bytes.</param>
    public void Add(ObjectId id, ReadOnlyMemory<byte> blob) => entries[id] = blob;

    /// <summary>Copies every chunk out of a backend.</summary>
    /// <param name="backend">Where to take them from.</param>
    public void AddAll(IOdbBackend backend) {
        ArgumentNullException.ThrowIfNull(backend);

        foreach (var id in backend.Enumerate()) {
            if (backend.TryRead(id, out var blob)) {
                using (blob) {
                    Add(id, blob.Bytes.ToArray());
                }
            }
        }
    }

    /// <summary>Produces the bundle.</summary>
    /// <returns>The bytes.</returns>
    public byte[] Build() {
        var payloadLength = 0;

        foreach (var blob in entries.Values) {
            payloadLength += blob.Length;
        }

        var payloadStart = BundleFormat.PayloadOffset(entries.Count);
        var bundle = new byte[payloadStart + payloadLength];
        var index = BundleFormat.HeaderSize;
        var offset = 0;

        // SortedDictionary hands them over in id order, which is what the reader's binary search
        // requires. Writing them in any other order would be a bundle that looks fine and answers
        // "not found" for most of what it holds.
        foreach (var (id, blob) in entries) {
            id.WriteTo(bundle.AsSpan(index, ObjectId.SizeInBytes));
            BinaryPrimitives.WriteUInt32LittleEndian(bundle.AsSpan(index + ObjectId.SizeInBytes), (uint)offset);
            BinaryPrimitives.WriteUInt32LittleEndian(bundle.AsSpan(index + ObjectId.SizeInBytes + 4), (uint)blob.Length);
            blob.Span.CopyTo(bundle.AsSpan(payloadStart + offset));
            index += BundleFormat.EntrySize;
            offset += blob.Length;
        }

        BundleFormat.Magic.CopyTo(bundle);
        BinaryPrimitives.WriteUInt32LittleEndian(bundle.AsSpan(8), BundleFormat.Version);
        BinaryPrimitives.WriteUInt32LittleEndian(bundle.AsSpan(12), (uint)entries.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(bundle.AsSpan(16), Crc32.HashToUInt32(bundle.AsSpan(payloadStart)));
        BinaryPrimitives.WriteUInt32LittleEndian(bundle.AsSpan(20), 0);
        return bundle;
    }
}

/// <summary>Reads chunks out of a <c>.bundle</c>. The runtime backend.</summary>
/// <remarks>
///     Read-only, and holds the bundle rather than copying it — so a bundle backed by a
///     memory-mapped file answers a read with a slice of the map and no allocation at all.
/// </remarks>
public sealed class BundleOdbBackend : IOdbBackend, IDisposable {
    readonly ReadOnlyMemory<byte> bundle;
    readonly IDisposable? owner;
    readonly int entryCount;
    readonly int payloadStart;

    /// <inheritdoc />
    public bool IsReadOnly => true;

    /// <summary>How many chunks the bundle holds.</summary>
    public int Count => entryCount;

    /// <summary>Opens a bundle held in memory.</summary>
    /// <param name="bundle">The bytes.</param>
    /// <param name="verifyChecksum">Whether to check the payload CRC now. Costs a full read.</param>
    /// <param name="owner">Something to dispose when this is disposed, such as a file mapping.</param>
    /// <exception cref="SerializationException">
    ///     It is not a bundle, its index does not fit or is not sorted, or the checksum does not match.
    /// </exception>
    public BundleOdbBackend(ReadOnlyMemory<byte> bundle, bool verifyChecksum = false, IDisposable? owner = null) {
        this.bundle = bundle;
        this.owner = owner;
        var span = bundle.Span;

        if (span.Length < BundleFormat.HeaderSize || !span[..8].SequenceEqual(BundleFormat.Magic)) {
            throw new SerializationException("This is not a Vixen bundle: the magic does not match.");
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);

        if (version != BundleFormat.Version) {
            throw new SerializationException($"The bundle is version {version} and this build reads version {BundleFormat.Version}.");
        }

        // ⚠ In 64-bit arithmetic, and narrowed only once the count is known to fit. The count is
        // unsigned on the wire, so 0x08000000 entries makes HeaderSize + count × EntrySize overflow
        // to a negative int — which passes a "does this fit" test written in int, and leaves every
        // index read reaching past the end of a file the constructor has just accepted.
        var declared = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
        var payload = BundleFormat.HeaderSize + ((long)declared * BundleFormat.EntrySize);

        if (payload > span.Length) {
            throw new SerializationException($"The bundle claims {declared} entries, which do not fit in {span.Length} bytes.");
        }

        entryCount = (int)declared;
        payloadStart = (int)payload;

        VerifySorted();

        if (verifyChecksum) {
            var expected = BinaryPrimitives.ReadUInt32LittleEndian(span[16..]);
            var actual = Crc32.HashToUInt32(span[payloadStart..]);

            if (expected != actual) {
                throw new SerializationException(
                    $"The bundle's payload checksum is {actual:x8} and its header says {expected:x8}. "
                    + "The file is corrupt or was truncated in transit."
                );
            }
        }
    }

    /// <summary>Opens a bundle from a file, mapping it where the provider can.</summary>
    /// <param name="files">The file system.</param>
    /// <param name="path">The bundle.</param>
    /// <param name="verifyChecksum">Whether to check the payload CRC now.</param>
    /// <returns>The backend.</returns>
    public static BundleOdbBackend Open(IO.VirtualFileSystem files, IO.VirtualPath path, bool verifyChecksum = false) {
        ArgumentNullException.ThrowIfNull(files);

        if (files.TryMap(path, out var mapped)) {
            return new(mapped.Memory, verifyChecksum, mapped);
        }

        using var stream = files.OpenRead(path);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return new(buffer.ToArray(), verifyChecksum);
    }

    /// <inheritdoc />
    public bool Exists(ObjectId id) => IndexOf(id) >= 0;

    /// <inheritdoc />
    public bool TryRead(ObjectId id, [NotNullWhen(true)] out IOdbBlob? blob) {
        var index = IndexOf(id);

        if (index < 0) {
            blob = null;
            return false;
        }

        var entry = bundle.Span.Slice(BundleFormat.HeaderSize + (index * BundleFormat.EntrySize), BundleFormat.EntrySize);
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[ObjectId.SizeInBytes..]);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(entry[(ObjectId.SizeInBytes + 4)..]);

        // ⚠ In 64-bit arithmetic and before either is narrowed, which is the whole of the check.
        // Both are unsigned on the wire: a length of 0x80000000 narrowed to int is negative, the sum
        // then lands below the file's length, the bounds test passes, and the slice below throws an
        // ArgumentOutOfRangeException — an exception from inside a decoder rather than the refusal
        // this method documents.
        if (payloadStart + (long)offset + length > bundle.Length) {
            throw new SerializationException($"The bundle's entry for {id} points past the end of the file.");
        }

        blob = new Slice(bundle.Slice(payloadStart + (int)offset, (int)length));
        return true;
    }

    /// <inheritdoc />
    public bool Write(ObjectId id, ReadOnlySpan<byte> blob) =>
        throw new NotSupportedException("A bundle is built by the content build and read at runtime; it is not written to.");

    /// <inheritdoc />
    public bool Delete(ObjectId id) =>
        throw new NotSupportedException("A bundle is built by the content build and read at runtime; it is not written to.");

    /// <inheritdoc />
    public IEnumerable<ObjectId> Enumerate() {
        for (var index = 0; index < entryCount; index++) {
            yield return IdAt(index);
        }
    }

    /// <summary>Closes the bundle, releasing the mapping if it owns one.</summary>
    public void Dispose() => owner?.Dispose();

    ObjectId IdAt(int index) =>
        ObjectId.FromBytes(bundle.Span.Slice(BundleFormat.HeaderSize + (index * BundleFormat.EntrySize), ObjectId.SizeInBytes));

    /// <summary>Checks the index really is ascending, which is what the lookup assumes.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A binary search over an unsorted index does not fail — it answers "not
    ///         found".</b> So a bundle whose entries were written in the wrong order reports most of
    ///         its own contents as missing, and the failure arrives somewhere else entirely as an
    ///         asset that will not load, with a file that opens cleanly and enumerates every id it
    ///         holds. Refusing at open is the only place the ordering can be established at all.
    ///     </para>
    ///     <para>
    ///         One pass over the index and not over the payload, so it costs twenty-four bytes an
    ///         entry — the pages the binary search was going to fault in anyway — and leaves the
    ///         property this class is built around intact: a bundle is opened without reading the
    ///         chunks nobody asked for.
    ///     </para>
    /// </remarks>
    void VerifySorted() {
        for (var index = 1; index < entryCount; index++) {
            if (IdAt(index - 1).CompareTo(IdAt(index)) >= 0) {
                throw new SerializationException(
                    $"The bundle's index is not sorted at entry {index} of {entryCount}. A lookup is a binary "
                    + "search over it, so an unsorted index answers 'not found' for content the file holds."
                );
            }
        }
    }

    int IndexOf(ObjectId id) {
        var low = 0;
        var high = entryCount - 1;

        while (low <= high) {
            var middle = low + ((high - low) / 2);
            var comparison = IdAt(middle).CompareTo(id);

            if (comparison == 0) {
                return middle;
            }

            if (comparison < 0) {
                low = middle + 1;
            } else {
                high = middle - 1;
            }
        }

        return -1;
    }

    sealed class Slice(ReadOnlyMemory<byte> bytes) : IOdbBlob {
        public ReadOnlyMemory<byte> Bytes => bytes;

        public void Dispose() { }
    }
}
