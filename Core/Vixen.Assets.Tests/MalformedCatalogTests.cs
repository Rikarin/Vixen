// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Hashing;
using Vixen.Core;
using Xunit;

namespace Vixen.Assets.Tests;

/// <summary>Catalogs whose checksum is right and whose contents are not.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every file here has a valid CRC, and that is the point of the fixture rather than an
///         inconvenience in building it.</b> The checksum says the bytes arrived as they were sent.
///         It says nothing about who sent them, and it is four bytes at the end that anybody editing
///         the file recomputes in a line — so a catalog that reaches the parser is intact, not
///         trustworthy.
///     </para>
///     <para>
///         <b>That is exactly why these checks are the ones that get left out.</b> A mutator flipping
///         a byte fails the checksum and never reaches the string table, so the parser looks robust
///         under the one test anybody thinks to run, and the counts inside it — which size arrays —
///         and the indices inside it — which read one — go unchecked.
///     </para>
/// </remarks>
public sealed class MalformedCatalogTests {
    static byte[] Valid() {
        var entry = new CatalogEntry(
            "a/b",
            new(1, 1),
            "bundle",
            ContentProvider.Local,
            ImmutableArray<string>.Empty,
            ImmutableArray<string>.Empty,
            10,
            default,
            0
        );

        return CatalogFormat.Write(new(CatalogFormat.Version, new(9, 9), "Windows", [entry], []));
    }

    /// <summary>Writes a field and repairs the trailing checksum, which is what an attacker does.</summary>
    static byte[] Patch(int offset, uint value) {
        var file = Valid();
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(offset), value);

        var checksum = new Crc32();
        checksum.Append(file.AsSpan(0, file.Length - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(file.Length - 4), checksum.GetCurrentHashAsUInt32());

        return file;
    }

    /// <summary>Where the target index sits, which is after however many strings the table holds.</summary>
    static int TargetIndexOffset() {
        var file = Valid();
        var cursor = 28;
        var count = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(cursor));
        cursor += 4;

        for (var index = 0; index < count; index++) {
            cursor += 4 + BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(cursor));
        }

        return cursor;
    }

    static long Weigh(Action action) {
        var before = GC.GetAllocatedBytesForCurrentThread();
        action();

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>A string table sized from a count nothing checked.</summary>
    /// <remarks>
    ///     A string is a four-byte length and at least no bytes, so a table of n needs 4n behind it —
    ///     which makes the bound exact rather than a cap somebody chose. 0x40000000 strings is an
    ///     eight-gigabyte array of references asked for by a hundred-byte file.
    /// </remarks>
    [Theory]
    [InlineData(0x40000000u)]
    [InlineData(0x7FFFFFFFu)]
    [InlineData(0xFFFFFFFFu)]
    public void AStringTableCountTheFileCannotSupplyIsRefusedWithoutAllocatingIt(uint count) {
        var file = Patch(28, count);

        var allocated = Weigh(() => Assert.Throws<CatalogFormatException>(() => CatalogFormat.Read(file)));

        Assert.True(allocated < 256 * 1024, $"Refusing a {file.Length}-byte catalog cost {allocated:N0} bytes.");
    }

    /// <summary>A table count the file cannot fill read past its own strings into whatever followed.</summary>
    /// <remarks>
    ///     ⚠ <b>This one used to succeed.</b> A count of three where one string was written produced a
    ///     catalog holding two strings assembled out of the bytes of the records after it — no
    ///     exception, no warning, and a catalog whose addresses are partly nonsense.
    /// </remarks>
    [Fact]
    public void AStringTableCountBeyondTheStringsWrittenIsRefused() =>
        Assert.Throws<CatalogFormatException>(() => CatalogFormat.Read(Patch(28, 400)));

    /// <summary>A string whose length runs off the end of the file.</summary>
    [Theory]
    [InlineData(0x7FFFFFFFu)]
    [InlineData(0xFFFFFFFFu)]
    public void AStringLongerThanTheFileIsRefused(uint length) =>
        Assert.Throws<CatalogFormatException>(() => CatalogFormat.Read(Patch(32, length)));

    /// <summary>An index into the string table that is not in it.</summary>
    /// <remarks>
    ///     ⚠ Raw indexing here was an <c>IndexOutOfRangeException</c> — an exception from inside a
    ///     parser whose entire contract is <see cref="CatalogFormatException" />, naming neither the
    ///     file nor the field, out of the first thing a game touches on a device.
    /// </remarks>
    [Theory]
    [InlineData(999u)]
    [InlineData(0xFFFFFFFFu)]
    public void AStringTableIndexThatIsNotInTheTableIsRefusedByName(uint index) {
        var failure = Assert.Throws<CatalogFormatException>(() => CatalogFormat.Read(Patch(TargetIndexOffset(), index)));

        Assert.Contains("string table", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>An entry count sized from a number the file cannot fill.</summary>
    [Theory]
    [InlineData(0x10000000u)]
    [InlineData(0xFFFFFFFFu)]
    public void AnEntryCountTheFileCannotSupplyIsRefusedWithoutAllocatingIt(uint count) {
        var file = Patch(TargetIndexOffset() + 4, count);

        var allocated = Weigh(() => Assert.Throws<CatalogFormatException>(() => CatalogFormat.Read(file)));

        Assert.True(allocated < 256 * 1024, $"Refusing a {file.Length}-byte catalog cost {allocated:N0} bytes.");
    }

    /// <summary>A catalog cut short is a refusal rather than a read past the end.</summary>
    [Fact]
    public void ATruncatedCatalogIsRefusedByName() {
        var file = Valid();

        // Cut the body and repair the checksum, so it is the parser rather than the CRC refusing.
        var cut = file.AsSpan(0, file.Length - 12).ToArray();
        var checksum = new Crc32();
        checksum.Append(cut.AsSpan(0, cut.Length - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(cut.AsSpan(cut.Length - 4), checksum.GetCurrentHashAsUInt32());

        Assert.Throws<CatalogFormatException>(() => CatalogFormat.Read(cut));
    }

    /// <summary>No single field can be made to produce an exception that is not the documented one.</summary>
    /// <remarks>
    ///     A sweep over every four-byte-aligned position in a small catalog, each set to the values
    ///     that break a length or an index, with the checksum repaired every time. It is the
    ///     assertion the whole file makes, stated once over the whole file rather than per field.
    /// </remarks>
    [Fact]
    public void NoFieldCanProduceAnExceptionThatIsNotACatalogFormatException() {
        var length = Valid().Length;

        for (var offset = 8; offset + 4 <= length - 4; offset++) {
            foreach (var value in new uint[] { 0, 1, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF }) {
                try {
                    CatalogFormat.Read(Patch(offset, value));
                } catch (CatalogFormatException) {
                    // The documented refusal.
                }
            }
        }
    }

    [Fact]
    public void AWellFormedCatalogStillReads() {
        var catalog = CatalogFormat.Read(Valid());

        Assert.Equal("Windows", catalog.Target);
        Assert.True(catalog.TryGet("a/b", out var entry));
        Assert.Equal("bundle", entry.Bundle);
    }
}
