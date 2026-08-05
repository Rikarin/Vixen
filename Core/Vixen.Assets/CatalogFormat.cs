// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Hashing;
using System.Text;
using Vixen.Core;
using Vixen.Core.Serialization.Storage;

namespace Vixen.Assets;

/// <summary>A catalog file that is not one, or is one this cannot read.</summary>
/// <param name="message">What is wrong with it.</param>
public sealed class CatalogFormatException(string message) : Exception(message);

/// <summary>Reads and writes <c>catalog.bin</c>.</summary>
/// <remarks>
///     <para>
///         <b>Binary, and read before anything else on the device.</b> The catalog is the first thing
///         a game touches and nothing can be loaded until it is parsed, so it sits directly on the
///         boot time of every session. A JSON catalog of ten thousand entries is megabytes of text
///         and a second of parsing on a phone; this is a header, a string table and fixed-width
///         records, and it parses in one pass with no allocation per entry beyond the strings
///         themselves.
///     </para>
///     <para>
///         <b>Strings are stored once.</b> Every address appears in its own entry and again in the
///         dependency list of everything that points at it, so a catalog without a string table
///         stores the average address four or five times. The table also makes the file
///         <i>comparable</i>: two builds of the same content produce the same bytes, which is what
///         lets a content update ship a diff instead of a catalog.
///     </para>
///     <para>
///         <b>Deterministic by construction.</b> The string table is sorted, entries are written in
///         address order and bundles in name order, so nothing in the output depends on the order a
///         dictionary happened to enumerate in. Doc 12 gates the content build on producing identical
///         bytes across three operating systems, and a catalog that reordered itself per run would
///         fail that gate for no reason anyone could find.
///     </para>
/// </remarks>
public static class CatalogFormat {
    /// <summary>The eight bytes every catalog starts with.</summary>
    public static ReadOnlySpan<byte> Magic => "VXCATLOG"u8;

    /// <summary>The format version this reads and writes.</summary>
    /// <remarks>
    ///     <b>2 carries each entry's <c>vx:</c> reference.</b> Twenty fixed-width bytes per entry —
    ///     sixteen of asset id and four of sub-asset id — and not a string-table index, because a
    ///     reference is not text and putting one in the table would store its hex form for the sake of
    ///     deduplicating a string that appears exactly once. Version 1 files are refused rather than
    ///     read with empty references: a catalog that resolves addresses and silently answers no
    ///     reference is a game whose every mesh is missing, diagnosed as content that failed to load.
    /// </remarks>
    public const int Version = 3;

    /// <summary>Writes a catalog.</summary>
    /// <param name="catalog">The catalog.</param>
    /// <returns>The file's bytes.</returns>
    public static byte[] Write(ContentCatalog catalog) {
        ArgumentNullException.ThrowIfNull(catalog);

        // Sorted, so the table's indices — and therefore the whole file — do not depend on the order
        // a dictionary enumerated in.
        var strings = new SortedSet<string>(StringComparer.Ordinal) { catalog.Target };

        foreach (var entry in catalog.Entries) {
            strings.Add(entry.Address);
            strings.Add(entry.Bundle);
            strings.UnionWith(entry.Dependencies);
            strings.UnionWith(entry.Labels);
        }

        foreach (var bundle in catalog.Bundles) {
            strings.Add(bundle.Name);
            strings.Add(bundle.Url);
            strings.UnionWith(bundle.Dependencies);
        }

        var table = strings.ToArray();
        var index = new Dictionary<string, int>(table.Length, StringComparer.Ordinal);

        for (var position = 0; position < table.Length; position++) {
            index[table[position]] = position;
        }

        using var file = new MemoryStream();
        file.Write(Magic);
        WriteInt32(file, Version);

        Span<byte> hash = stackalloc byte[ObjectId.SizeInBytes];
        catalog.BuildHash.WriteTo(hash);
        file.Write(hash);

        WriteInt32(file, table.Length);

        foreach (var text in table) {
            var utf8 = Encoding.UTF8.GetBytes(text);
            WriteInt32(file, utf8.Length);
            file.Write(utf8);
        }

        WriteInt32(file, index[catalog.Target]);

        var entries = catalog.Entries.OrderBy(entry => entry.Address, StringComparer.Ordinal).ToArray();
        WriteInt32(file, entries.Length);

        foreach (var entry in entries) {
            WriteInt32(file, index[entry.Address]);
            entry.Id.WriteTo(hash);
            file.Write(hash);
            WriteInt32(file, index[entry.Bundle]);
            file.WriteByte((byte)entry.Provider);
            WriteInt64(file, entry.Size);
            WriteUInt64(file, entry.Shape);
            WriteGuid(file, entry.Reference.Asset.Value);
            WriteUInt32(file, entry.Reference.SubAsset.Value);
            WriteIndices(file, index, entry.Dependencies);
            WriteIndices(file, index, entry.Labels);
        }

        var bundles = catalog.Bundles.OrderBy(bundle => bundle.Name, StringComparer.Ordinal).ToArray();
        WriteInt32(file, bundles.Length);

        foreach (var bundle in bundles) {
            WriteInt32(file, index[bundle.Name]);
            WriteInt32(file, index[bundle.Url]);
            bundle.Hash.WriteTo(hash);
            file.Write(hash);
            WriteInt64(file, bundle.Size);
            WriteUInt32(file, bundle.Crc);
            file.WriteByte((byte)bundle.Compression);
            WriteIndices(file, index, bundle.Dependencies);
        }

        // A trailing checksum over everything before it. A catalog arrives over the network on a
        // content update, and a truncated one would otherwise parse into a plausible catalog missing
        // its last few hundred addresses — which fails later, somewhere else, as a missing asset.
        var body = file.ToArray();
        var checksum = new Crc32();
        checksum.Append(body);

        var result = new byte[body.Length + 4];
        body.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(body.Length), checksum.GetCurrentHashAsUInt32());

        return result;
    }

    /// <summary>Reads a catalog.</summary>
    /// <param name="file">The file's bytes.</param>
    /// <returns>The catalog.</returns>
    /// <exception cref="CatalogFormatException">It is not a catalog, is a version this cannot read, or is damaged.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every count and every table index is checked, and the checksum above is not what
    ///         makes that unnecessary.</b> A CRC32 says the bytes arrived as they were sent; it says
    ///         nothing about who sent them, and it is four bytes anybody who edits the file can
    ///         recompute. So a catalog that reaches this point is intact, not trustworthy — and the
    ///         counts in it size arrays while the indices in it read one.
    ///     </para>
    ///     <para>
    ///         That distinction is exactly why these checks are easy to leave out: a mutator that
    ///         flips a byte never reaches them, so the parser looks robust under the one test anybody
    ///         thinks to run. A count is therefore validated against the bytes that are actually left
    ///         rather than believed, and an index into the string table against the table's length.
    ///     </para>
    /// </remarks>
    public static ContentCatalog Read(ReadOnlySpan<byte> file) {
        if (file.Length < Magic.Length + 4 || !file[..Magic.Length].SequenceEqual(Magic)) {
            throw new CatalogFormatException("This is not a catalog: the eight-byte identifier does not match.");
        }

        var body = file[..^4];
        var checksum = new Crc32();
        checksum.Append(body);

        var declared = BinaryPrimitives.ReadUInt32LittleEndian(file[^4..]);

        if (checksum.GetCurrentHashAsUInt32() != declared) {
            throw new CatalogFormatException(
                "This catalog's checksum does not match its contents, so it arrived damaged or truncated. "
                + "Reading it anyway would produce a catalog that is missing addresses rather than one that "
                + "fails to load."
            );
        }

        var cursor = Magic.Length;
        var version = ReadInt32(body, ref cursor);

        if (version != Version) {
            throw new CatalogFormatException(
                $"This catalog is format version {version} and this runtime reads {Version}. A build and the "
                + "application it ships in have to agree about the format."
            );
        }

        var buildHash = ObjectId.FromBytes(Take(body, ref cursor, ObjectId.SizeInBytes));

        // A string is a four-byte length and at least no bytes, so a table of n needs 4n behind it.
        // Sizing the array off the count alone is how eight bytes buy a two-gigabyte allocation.
        var table = new string[Counted(body, ref cursor, 4, "the string table")];

        for (var position = 0; position < table.Length; position++) {
            var length = ReadInt32(body, ref cursor);

            if (length < 0 || length > body.Length - cursor) {
                throw new CatalogFormatException(
                    $"This catalog's string {position} claims {length} bytes and {body.Length - cursor} are left."
                );
            }

            table[position] = Encoding.UTF8.GetString(body.Slice(cursor, length));
            cursor += length;
        }

        var target = table[Index(body, ref cursor, table, "the target")];

        // An entry is two indices, an id, a provider byte, a size, a shape, a reference and two
        // index lists — 61 bytes before either list has an element.
        var entries = new CatalogEntry[Counted(body, ref cursor, 61, "the entry table")];

        for (var position = 0; position < entries.Length; position++) {
            var address = table[Index(body, ref cursor, table, "an entry's address")];
            var id = ObjectId.FromBytes(Take(body, ref cursor, ObjectId.SizeInBytes));
            var bundle = table[Index(body, ref cursor, table, "an entry's bundle")];
            var provider = (ContentProvider)Take(body, ref cursor, 1)[0];
            var size = ReadInt64(body, ref cursor);
            var shape = ReadUInt64(body, ref cursor);
            var asset = ReadGuid(body, ref cursor);
            var subAsset = ReadUInt32(body, ref cursor);
            var dependencies = ReadIndices(body, ref cursor, table);
            var labels = ReadIndices(body, ref cursor, table);

            entries[position] = new(
                address,
                id,
                bundle,
                provider,
                dependencies,
                labels,
                size,
                new AssetReference(new AssetId(asset), new SubAssetId(subAsset)),
                shape
            );
        }

        // A bundle is two indices, a hash, a size, a CRC, a compression byte and one index list — 41
        // bytes before that list has an element.
        var bundles = new CatalogBundle[Counted(body, ref cursor, 41, "the bundle table")];

        for (var position = 0; position < bundles.Length; position++) {
            var name = table[Index(body, ref cursor, table, "a bundle's name")];
            var url = table[Index(body, ref cursor, table, "a bundle's URL")];
            var hash = ObjectId.FromBytes(Take(body, ref cursor, ObjectId.SizeInBytes));
            var size = ReadInt64(body, ref cursor);
            var crc = ReadUInt32(body, ref cursor);
            var compression = (CompressionMethod)Take(body, ref cursor, 1)[0];
            var dependencies = ReadIndices(body, ref cursor, table);

            bundles[position] = new(name, url, hash, size, crc, compression, dependencies);
        }

        return new(version, buildHash, target, entries, bundles);
    }

    static void WriteIndices(Stream file, Dictionary<string, int> index, ImmutableArray<string> values) {
        WriteInt32(file, values.IsDefault ? 0 : values.Length);

        if (values.IsDefault) {
            return;
        }

        foreach (var value in values) {
            WriteInt32(file, index[value]);
        }
    }

    static ImmutableArray<string> ReadIndices(ReadOnlySpan<byte> body, ref int cursor, string[] table) {
        var count = Counted(body, ref cursor, 4, "an index list");

        if (count == 0) {
            return [];
        }

        var values = ImmutableArray.CreateBuilder<string>(count);

        for (var position = 0; position < count; position++) {
            values.Add(table[Index(body, ref cursor, table, "an index list entry")]);
        }

        return values.MoveToImmutable();
    }

    /// <summary>Reads a count and refuses one the remaining bytes could not supply.</summary>
    /// <param name="body">The file.</param>
    /// <param name="cursor">Where to read it.</param>
    /// <param name="each">The fewest bytes one element occupies.</param>
    /// <param name="what">What is being counted, for the message.</param>
    /// <returns>The count.</returns>
    /// <remarks>
    ///     ⚠ <b>The array is sized from this, so this is where a small file buys a large
    ///     allocation.</b> Every record in this format has a fixed minimum width, so the bytes left
    ///     are an upper bound on how many of them there can be — which makes the check exact rather
    ///     than a guessed cap, and makes the cost of parsing a catalog proportional to its size.
    /// </remarks>
    static int Counted(ReadOnlySpan<byte> body, ref int cursor, int each, string what) {
        var count = ReadInt32(body, ref cursor);

        if (count < 0 || (long)count * each > body.Length - cursor) {
            throw new CatalogFormatException(
                $"This catalog says {what} holds {count} things, and {body.Length - cursor} bytes are left — which "
                + $"is not enough for {each} bytes each. The file is truncated or was not written by this format."
            );
        }

        return count;
    }

    /// <summary>Reads an index into the string table and refuses one that is not in it.</summary>
    /// <remarks>
    ///     Raw indexing here is an <c>IndexOutOfRangeException</c> out of a parser whose whole
    ///     contract is <see cref="CatalogFormatException" />, and it names neither the file nor the
    ///     field — which is the difference between a report and an afternoon.
    /// </remarks>
    static int Index(ReadOnlySpan<byte> body, ref int cursor, string[] table, string what) {
        var index = ReadInt32(body, ref cursor);

        if ((uint)index >= (uint)table.Length) {
            throw new CatalogFormatException(
                $"This catalog's string table holds {table.Length} strings and {what} is index {index}."
            );
        }

        return index;
    }

    /// <summary>Takes a fixed run of bytes, refusing one that runs off the end.</summary>
    static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> body, ref int cursor, int count) {
        Room(body, cursor, count);

        var value = body.Slice(cursor, count);
        cursor += count;

        return value;
    }

    static void Room(ReadOnlySpan<byte> body, int cursor, int count) {
        if (cursor < 0 || count > body.Length - cursor) {
            throw new CatalogFormatException(
                $"This catalog ends after {body.Length} bytes and a read of {count} at {cursor} runs past it. "
                + "It arrived truncated."
            );
        }
    }

    static void WriteInt32(Stream file, int value) {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        file.Write(buffer);
    }

    static void WriteUInt32(Stream file, uint value) {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        file.Write(buffer);
    }

    static void WriteUInt64(Stream file, ulong value) {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        file.Write(buffer);
    }

    /// <remarks>
    ///     Big-endian, through <see cref="Guid.TryWriteBytes(Span{byte}, bool, out int)" />'s
    ///     <c>bigEndian: true</c>, so the bytes do not depend on the machine that wrote them. A
    ///     <see cref="Guid" />'s in-memory layout is little-endian for its first three fields on every
    ///     platform .NET runs on today, but "today" is not what a shipped content format gets to
    ///     assume, and doc 12 gates the build on three operating systems producing identical bytes.
    /// </remarks>
    static void WriteGuid(Stream file, Guid value) {
        Span<byte> buffer = stackalloc byte[16];
        value.TryWriteBytes(buffer, bigEndian: true, out _);
        file.Write(buffer);
    }

    static Guid ReadGuid(ReadOnlySpan<byte> body, ref int cursor) => new(Take(body, ref cursor, 16), bigEndian: true);

    static void WriteInt64(Stream file, long value) {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        file.Write(buffer);
    }

    // Through Take rather than off a raw slice: a read past the end here is a truncated catalog,
    // which is the case the trailing checksum exists to name, and the bare slice reports it as an
    // ArgumentOutOfRangeException from BinaryPrimitives with nothing in it about a catalog.
    static int ReadInt32(ReadOnlySpan<byte> body, ref int cursor) =>
        BinaryPrimitives.ReadInt32LittleEndian(Take(body, ref cursor, 4));

    static uint ReadUInt32(ReadOnlySpan<byte> body, ref int cursor) =>
        BinaryPrimitives.ReadUInt32LittleEndian(Take(body, ref cursor, 4));

    static long ReadInt64(ReadOnlySpan<byte> body, ref int cursor) =>
        BinaryPrimitives.ReadInt64LittleEndian(Take(body, ref cursor, 8));

    static ulong ReadUInt64(ReadOnlySpan<byte> body, ref int cursor) =>
        BinaryPrimitives.ReadUInt64LittleEndian(Take(body, ref cursor, 8));
}
