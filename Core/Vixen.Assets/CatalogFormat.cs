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
    public const int Version = 1;

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

        var buildHash = ObjectId.FromBytes(body.Slice(cursor, ObjectId.SizeInBytes));
        cursor += ObjectId.SizeInBytes;

        var table = new string[ReadInt32(body, ref cursor)];

        for (var position = 0; position < table.Length; position++) {
            var length = ReadInt32(body, ref cursor);
            table[position] = Encoding.UTF8.GetString(body.Slice(cursor, length));
            cursor += length;
        }

        var target = table[ReadInt32(body, ref cursor)];

        var entries = new CatalogEntry[ReadInt32(body, ref cursor)];

        for (var position = 0; position < entries.Length; position++) {
            var address = table[ReadInt32(body, ref cursor)];
            var id = ObjectId.FromBytes(body.Slice(cursor, ObjectId.SizeInBytes));
            cursor += ObjectId.SizeInBytes;
            var bundle = table[ReadInt32(body, ref cursor)];
            var provider = (ContentProvider)body[cursor++];
            var size = ReadInt64(body, ref cursor);
            var dependencies = ReadIndices(body, ref cursor, table);
            var labels = ReadIndices(body, ref cursor, table);

            entries[position] = new(address, id, bundle, provider, dependencies, labels, size);
        }

        var bundles = new CatalogBundle[ReadInt32(body, ref cursor)];

        for (var position = 0; position < bundles.Length; position++) {
            var name = table[ReadInt32(body, ref cursor)];
            var url = table[ReadInt32(body, ref cursor)];
            var hash = ObjectId.FromBytes(body.Slice(cursor, ObjectId.SizeInBytes));
            cursor += ObjectId.SizeInBytes;
            var size = ReadInt64(body, ref cursor);
            var crc = ReadUInt32(body, ref cursor);
            var compression = (CompressionMethod)body[cursor++];
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
        var count = ReadInt32(body, ref cursor);

        if (count == 0) {
            return [];
        }

        var values = ImmutableArray.CreateBuilder<string>(count);

        for (var position = 0; position < count; position++) {
            values.Add(table[ReadInt32(body, ref cursor)]);
        }

        return values.MoveToImmutable();
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

    static void WriteInt64(Stream file, long value) {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        file.Write(buffer);
    }

    static int ReadInt32(ReadOnlySpan<byte> body, ref int cursor) {
        var value = BinaryPrimitives.ReadInt32LittleEndian(body[cursor..]);
        cursor += 4;
        return value;
    }

    static uint ReadUInt32(ReadOnlySpan<byte> body, ref int cursor) {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(body[cursor..]);
        cursor += 4;
        return value;
    }

    static long ReadInt64(ReadOnlySpan<byte> body, ref int cursor) {
        var value = BinaryPrimitives.ReadInt64LittleEndian(body[cursor..]);
        cursor += 8;
        return value;
    }
}
