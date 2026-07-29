// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.IO;
using Vixen.Core.Serialization.Storage;
using Xunit;

namespace Vixen.Assets.Tests;

/// <summary>Content that is read rather than loaded.</summary>
/// <remarks>
///     <para>
///         <b>The path a video takes.</b> A two-minute cutscene is a hundred megabytes; turning it
///         into an object would mean a loading screen for a cutscene longer than the cutscene, so the
///         asset the catalog holds is a small record naming it and this is the stream a demuxer
///         reads.
///     </para>
///     <para>
///         ⚠ It is also the only way to get a payload the content build produced with a tool that is
///         not this serializer back out again — <c>Read&lt;T&gt;</c> demands a matching type id and
///         <c>ReadObject</c> demands a registered serializer, and a WebM container has neither.
///     </para>
/// </remarks>
public sealed class StreamedContentTests {
    const ulong ContainerTypeId = 0xC0FFEE_1234_5678UL;

    [Fact]
    public void APayloadComesBackAsASeekableStream() {
        var bytes = Bytes(4096);
        var world = Build(("cutscenes/intro#container", bytes));

        using var stream = world.Open("cutscenes/intro#container", TestContext.Current.CancellationToken);

        Assert.True(stream.CanSeek);
        Assert.Equal(bytes.Length, stream.Length);

        var read = new byte[bytes.Length];
        stream.ReadExactly(read);

        Assert.Equal(bytes, read);
    }

    [Fact]
    public void TwoCallersGetTwoIndependentStreams() {
        // ⚠ Not a shared object, and it matters: a video's picture and its sound each want a reader
        // when either of them loops, and a cached stream handed to both would be one file position
        // that the two of them fight over.
        var world = Build(("cutscenes/intro#container", Bytes(64)));

        using var first = world.Open("cutscenes/intro#container", TestContext.Current.CancellationToken);
        using var second = world.Open("cutscenes/intro#container", TestContext.Current.CancellationToken);

        first.Position = 32;

        Assert.NotSame(first, second);
        Assert.Equal(0, second.Position);
    }

    [Fact]
    public void OpeningClaimsNothing() {
        var world = Build(("cutscenes/intro#container", Bytes(64)));

        using (world.Open("cutscenes/intro#container", TestContext.Current.CancellationToken)) {
            // There is no object to share, so there is nothing to hold and nothing to release. A
            // claim here would be one nobody could give back — the caller has a stream, not a handle.
            Assert.Equal(0, world.ClaimCount("cutscenes/intro#container"));
        }

        Assert.Equal(0, world.LoadedCount);
    }

    [Fact]
    public void AnUnknownAddressIsRefusedByName() {
        var world = Build(("cutscenes/intro#container", Bytes(16)));

        Assert.Throws<AddressNotFoundException>(() => world.Open("cutscenes/outro#container", TestContext.Current.CancellationToken));
        Assert.False(world.CanOpen("cutscenes/outro#container"));
        Assert.True(world.CanOpen("cutscenes/intro#container"));
    }

    [Fact]
    public void TheRawReadReportsTheTypeItWasWrittenWith() {
        var database = new ObjectDatabase(new FileOdbBackend(Files(out _), new("/store/odb")));
        var bytes = Bytes(128);

        var id = database.WriteRaw(ContainerTypeId, [], bytes, CompressionMethod.None);
        var payload = database.ReadRaw(id, out var typeId);

        // Reported rather than checked, which is the opposite of Read<T>: there is no serializer to
        // check against, and that absence is exactly what this path exists for.
        Assert.Equal(ContainerTypeId, typeId);
        Assert.Equal(bytes, payload);
    }

    static byte[] Bytes(int count) {
        var bytes = new byte[count];

        for (var index = 0; index < count; index++) {
            bytes[index] = (byte) (index * 31);
        }

        return bytes;
    }

    static VirtualFileSystem Files(out MemoryFileProvider storage) {
        var files = new VirtualFileSystem();
        storage = new MemoryFileProvider();

        files.Mount(new("/store"), storage);
        files.Mount(new("/bundles"), storage);

        return files;
    }

    static AssetManager Build(params (string Address, byte[] Bytes)[] entries) {
        var files = Files(out _);
        var scratch = new FileOdbBackend(files, new("/store/odb"));
        var writing = new ObjectDatabase(scratch);
        var catalogEntries = new List<CatalogEntry>();

        foreach (var (address, bytes) in entries) {
            // ⚠ Uncompressed, which is what a streamed payload should be built as: a WebM is
            // compressed already, so packing it again costs build time and saves nothing.
            var id = writing.WriteRaw(ContainerTypeId, [], bytes, CompressionMethod.None);

            catalogEntries.Add(new(address, id, "Main", ContentProvider.Local, [], [], 0));
        }

        var bundle = new BundleWriter();
        bundle.AddAll(scratch);

        using (var target = files.OpenWrite(new("/bundles/Main.bundle"))) {
            target.Write(bundle.Build());
        }

        var catalog = new ContentCatalog(
            CatalogFormat.Version,
            default,
            "Windows",
            catalogEntries,
            [new("Main", "", default, 0, 0, CompressionMethod.None, [])]
        );

        return new AssetManager(catalog, new LocalBundleSource(files, new("/bundles")));
    }
}
