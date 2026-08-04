// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using CsCheck;
using Vixen.Core;
using Vixen.Core.Serialization.Storage;
using Xunit;

namespace Vixen.Assets.Tests;

/// <summary>The file a build writes and every session parses before it can load anything.</summary>
public sealed class CatalogFormatTests {
    [Fact]
    public void ACatalogSurvivesBeingWrittenAndReadBack() {
        var original = Sample();

        var read = CatalogFormat.Read(CatalogFormat.Write(original));

        Assert.Equal(original.Version, read.Version);
        Assert.Equal(original.BuildHash, read.BuildHash);
        Assert.Equal(original.Target, read.Target);
        Assert.Equal(original.Count, read.Count);

        foreach (var entry in original.Entries) {
            Assert.True(read.TryGet(entry.Address, out var back));
            Assert.Equal(entry, back);
        }

        foreach (var bundle in original.Bundles) {
            Assert.True(read.TryGetBundle(bundle.Name, out var back));
            Assert.Equal(bundle, back);
        }
    }

    /// <summary>
    ///     <para>
    ///         The same content has to produce the same bytes. Doc 12 gates the content build on
    ///         byte-identical output across three operating systems, and a catalog that wrote its
    ///         entries in whatever order a dictionary enumerated would fail that gate intermittently
    ///         and for no reason anyone could reproduce.
    ///     </para>
    ///     <para>
    ///         Asserted by building the same catalog from inputs given in a different order, which is
    ///         what a build on another machine effectively does.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheSameContentInAnyOrderWritesTheSameBytes() {
        var entries = Sample().Entries.ToArray();
        var bundles = Sample().Bundles.ToArray();

        var forwards = new ContentCatalog(CatalogFormat.Version, new(9, 9), "Windows", entries, bundles);
        var backwards = new ContentCatalog(
            CatalogFormat.Version,
            new(9, 9),
            "Windows",
            entries.Reverse(),
            bundles.Reverse()
        );

        Assert.Equal(CatalogFormat.Write(forwards), CatalogFormat.Write(backwards));
    }

    /// <summary>
    ///     <para>
    ///         Every address appears in its own entry and again in the dependency list of everything
    ///         that points at it, so a catalog without a string table stores the average address
    ///         several times over.
    ///     </para>
    ///     <para>
    ///         Measured by the only thing that isolates it: the same catalog twice, with the shared
    ///         address short in one and fifty characters longer in the other. With a table the
    ///         difference is those fifty bytes, once. Without one it would be fifty bytes times the
    ///         two hundred entries that mention it.
    ///     </para>
    /// </summary>
    [Fact]
    public void StringsAreStoredOnceHoweverOftenTheyAreMentioned() {
        var brief = CatalogFormat.Write(Referring("shared")).Length;
        var verbose = CatalogFormat.Write(Referring("shared" + new string('x', 50))).Length;

        Assert.True(
            verbose - brief < 100,
            $"lengthening one shared address by fifty characters grew the file by {verbose - brief} bytes"
        );

        static ContentCatalog Referring(string shared) {
            var entries = new List<CatalogEntry> {
                new(shared, default, "Main", ContentProvider.Local, [], [], 0)
            };

            for (var index = 0; index < 200; index++) {
                entries.Add(new($"scene/{index}", default, "Main", ContentProvider.Local, [shared], [], 0));
            }

            return new(CatalogFormat.Version, default, "Windows", entries, []);
        }
    }

    [Fact]
    public void SomethingThatIsNotACatalogIsRefusedByItsIdentifier() {
        var failure = Assert.Throws<CatalogFormatException>(() => CatalogFormat.Read(new byte[64]));

        Assert.Contains("identifier", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A catalog arrives over the network on a content update. A truncated one would otherwise
    ///     parse into a plausible catalog missing its last few hundred addresses, which fails later,
    ///     somewhere else, as an asset that will not load.
    /// </summary>
    [Fact]
    public void ADamagedCatalogIsRefusedRatherThanPartlyRead() {
        var file = CatalogFormat.Write(Sample());
        file[file.Length / 2] ^= 0xFF;

        var failure = Assert.Throws<CatalogFormatException>(() => CatalogFormat.Read(file));

        Assert.Contains("checksum", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATruncatedCatalogIsRefusedToo() {
        var file = CatalogFormat.Write(Sample());

        Assert.Throws<CatalogFormatException>(() => CatalogFormat.Read(file.AsSpan(0, file.Length - 8)));
    }

    /// <summary>
    ///     A build and the application it ships in have to agree about the format, and a runtime that
    ///     read a newer catalog optimistically would misread every field after the first change.
    /// </summary>
    [Fact]
    public void AVersionThisDoesNotReadSaysSo() {
        var file = CatalogFormat.Write(Sample());

        // The version sits immediately after the eight-byte identifier.
        file[8] = 99;

        var checksum = new System.IO.Hashing.Crc32();
        checksum.Append(file.AsSpan(0, file.Length - 4));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            file.AsSpan(file.Length - 4),
            checksum.GetCurrentHashAsUInt32()
        );

        var failure = Assert.Throws<CatalogFormatException>(() => CatalogFormat.Read(file));

        Assert.Contains("version 99", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyCatalogIsStillACatalog() {
        var read = CatalogFormat.Read(
            CatalogFormat.Write(new(CatalogFormat.Version, default, "Windows", [], []))
        );

        Assert.Equal(0, read.Count);
        Assert.Equal("Windows", read.Target);
    }

    /// <summary>
    ///     The property the example asserts by hand, over arbitrary catalogs: whatever a build puts
    ///     in comes back out. Addresses that are prefixes of each other, empty labels, duplicated
    ///     dependencies and non-ASCII names all turn up here without anyone thinking of them.
    /// </summary>
    [Fact]
    public void AnyCatalogSurvivesTheRoundTrip() =>
        GenCatalog.Sample(catalog => {
                var read = CatalogFormat.Read(CatalogFormat.Write(catalog));

                Assert.Equal(catalog.Count, read.Count);

                foreach (var entry in catalog.Entries) {
                    Assert.True(read.TryGet(entry.Address, out var back));
                    Assert.Equal(entry, back);
                }
            },
            iter: 500
        );

    static Gen<ContentCatalog> GenCatalog =>
        Gen.Select(GenEntry.List[0, 12], GenBundle.List[0, 4])
            .Select(pair => new ContentCatalog(
                    CatalogFormat.Version,
                    default,
                    "Windows",
                    // A build cannot produce two entries at one address, so neither does this.
                    pair.Item1.DistinctBy(entry => entry.Address),
                    pair.Item2.DistinctBy(bundle => bundle.Name)
                )
            );

    static Gen<CatalogEntry> GenEntry =>
        Gen.Select(GenName, GenName, Gen.Int[0, 1], Gen.Long[0, 1 << 20], GenName.Array[0, 3], GenName.Array[0, 2])
            .Select(fields => new CatalogEntry(
                    fields.Item1,
                    default,
                    fields.Item2,
                    (ContentProvider)fields.Item3,
                    ImmutableArray.Create(fields.Item5),
                    ImmutableArray.Create(fields.Item6),
                    fields.Item4,
                    ReferenceFor(fields.Item1)
                )
            );

    /// <summary>A reference derived from an address, so the properties above cover the field.</summary>
    /// <remarks>
    ///     ⚠ <b>Injective, and it has to be.</b> <see cref="Sample" /> dedupes by address and a catalog
    ///     refuses two addresses claiming one reference, so a random id per entry would fail the
    ///     constructor on a collision and turn a property test into an intermittent one. Deriving it
    ///     from the address's position in the fixed name set gives one reference per address by
    ///     construction. The first name maps to <see cref="AssetReference.Null" />, which is what an
    ///     entry no authored asset claims looks like and is worth having in the sample.
    /// </remarks>
    static AssetReference ReferenceFor(string address) {
        var index = Array.IndexOf(Names, address);

        return index <= 0 ? AssetReference.Null : new(new AssetId(new Guid(index, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0])));
    }

    static readonly string[] Names =
        ["", "a", "ui", "ui/hero", "ui/hero/", "ui/heroic", "level1/props/barrel", "Ünïcödé", "x y"];

    static Gen<CatalogBundle> GenBundle =>
        Gen.Select(GenName, GenName, Gen.Long[0, 1 << 24], Gen.UInt, GenName.Array[0, 2])
            .Select(fields => new CatalogBundle(
                    fields.Item1,
                    fields.Item2,
                    default,
                    fields.Item3,
                    fields.Item4,
                    CompressionMethod.Lz4,
                    ImmutableArray.Create(fields.Item5)
                )
            );

    static Gen<string> GenName => Gen.OneOfConst(Names);

    static ContentCatalog Sample() =>
        new(
            CatalogFormat.Version,
            new(9, 9),
            "Windows",
            [
                new("ui/hero", new(1, 2), "UiCore", ContentProvider.Local, ["ui/shader"], ["ui", "boot"], 4096),
                new("ui/shader", new(3, 4), "UiCore", ContentProvider.Local, [], ["ui"], 512),
                new("dlc/prop", new(5, 6), "Dlc", ContentProvider.Remote, ["ui/shader"], ["dlc"], 900_000)
            ],
            [
                new("UiCore", "", new(10, 11), 4608, 0xDEADBEEF, CompressionMethod.Lz4, []),
                new("Dlc", "https://cdn.example/Dlc.bundle", new(12, 13), 400_000, 0xC0FFEE, CompressionMethod.None, ["UiCore"])
            ]
        );
    /// <summary>
    ///     The shape survives the file, which is the whole reason it is in the format rather than
    ///     alongside it. Doc 27 § Upgrades refuses a live content update for any address whose shape
    ///     is unrecorded, so an entry that lost its shape on the way through would silently make a
    ///     fleet undeployable-live rather than fail.
    /// </summary>
    [Fact]
    public void A_shape_round_trips_through_the_file() {
        var catalog = new ContentCatalog(
            CatalogFormat.Version,
            ObjectId.FromBytes(new byte[ObjectId.SizeInBytes]),
            "Windows",
            [
                new("items/sword", default, "core", ContentProvider.Local, [], [], 128, default, 0xC0FFEE_1234_5678),
                new("items/axe", default, "core", ContentProvider.Local, [], [], 64)
            ],
            []
        );

        var read = CatalogFormat.Read(CatalogFormat.Write(catalog));

        Assert.True(read.TryGet("items/sword", out var sword));
        Assert.Equal(0xC0FFEE_1234_5678ul, sword.Shape);
        Assert.True(sword.ShapeIsKnown);

        Assert.True(read.TryGet("items/axe", out var axe));
        Assert.Equal(0ul, axe.Shape);
        Assert.False(axe.ShapeIsKnown);
    }

}
