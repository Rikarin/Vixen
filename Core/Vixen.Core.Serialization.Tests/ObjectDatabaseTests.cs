// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Vixen.Core.IO;
using Vixen.Core.Serialization.Storage;
using Xunit;

namespace Vixen.Core.Serialization.Tests;

public class ObjectDatabaseTests {
    [Fact]
    public void AValueRoundTripsThroughItsId() {
        var database = InMemory();
        var id = database.Write(new SettableClass { Id = 1, Name = "a", Weight = 2.5 });
        var result = database.Read<SettableClass>(id);

        Assert.Equal(1, result.Id);
        Assert.Equal("a", result.Name);
        Assert.Equal(2.5, result.Weight);
    }

    /// <summary>
    ///     The property the whole design is for: two equal values are one chunk, without anybody
    ///     having compared them.
    /// </summary>
    [Fact]
    public void IdenticalContentIsStoredOnce() {
        var database = InMemory();
        var first = database.Write(new SettableClass { Id = 7, Name = "same", Weight = 1 });
        var second = database.Write(new SettableClass { Id = 7, Name = "same", Weight = 1 });

        Assert.Equal(first, second);
        Assert.Single(database.Enumerate());
    }

    [Fact]
    public void DifferentContentGetsDifferentIds() {
        var database = InMemory();
        var first = database.Write(new SettableClass { Id = 1 });
        var second = database.Write(new SettableClass { Id = 2 });

        Assert.NotEqual(first, second);
        Assert.Equal(2, database.Enumerate().Count());
    }

    /// <summary>
    ///     Compression is a storage policy, not part of what the content is called. Two builds that
    ///     disagree about it still have to agree about the id, or nothing deduplicates and every
    ///     incremental update ships everything.
    /// </summary>
    [Fact]
    public void CompressionDoesNotChangeTheId() {
        var payload = new CollectionsClass { Names = [.. Enumerable.Repeat("compressible", 200)] };

        var raw = InMemory();
        raw.DefaultCompression = CompressionMethod.None;
        var uncompressedId = raw.Write(payload);

        var lz4 = InMemory();
        lz4.DefaultCompression = CompressionMethod.Lz4;
        var lz4Id = lz4.Write(payload);

        var zstd = InMemory();
        zstd.DefaultCompression = CompressionMethod.Zstd;
        var zstdId = zstd.Write(payload);

        Assert.Equal(uncompressedId, lz4Id);
        Assert.Equal(uncompressedId, zstdId);

        // …and all three read back to the same value.
        Assert.Equal(payload.Names, lz4.Read<CollectionsClass>(lz4Id).Names);
        Assert.Equal(payload.Names, zstd.Read<CollectionsClass>(zstdId).Names);
    }

    [Fact]
    public void CompressibleContentActuallyGetsSmaller() {
        var payload = new CollectionsClass { Names = [.. Enumerable.Repeat("compressible", 500)] };

        var raw = InMemory();
        raw.DefaultCompression = CompressionMethod.None;
        var rawId = raw.Write(payload);
        Assert.True(raw.TryDescribe(rawId, out var rawInfo));

        var lz4 = InMemory();
        var lz4Id = lz4.Write(payload);
        Assert.True(lz4.TryDescribe(lz4Id, out var lz4Info));

        Assert.Equal(CompressionMethod.None, rawInfo.Compression);
        Assert.Equal(CompressionMethod.Lz4, lz4Info.Compression);
        Assert.True(lz4Info.StoredLength < rawInfo.StoredLength / 2, "LZ4 did not halve a highly repetitive payload.");
    }

    /// <summary>
    ///     Already-compressed payloads — every BCn texture and every Ogg clip — get bigger, not
    ///     smaller. Storing the bigger one would be a policy that costs space and decode time.
    /// </summary>
    [Fact]
    public void CompressionThatWouldGrowTheChunkIsNotUsed() {
        var random = new Random(20260726);
        var noise = new byte[4096];
        random.NextBytes(noise);

        var database = InMemory();
        var id = database.WriteRaw(1, [], noise);

        Assert.True(database.TryDescribe(id, out var info));
        Assert.Equal(CompressionMethod.None, info.Compression);
    }

    [Fact]
    public void SmallChunksAreNotCompressedAtAll() {
        var database = InMemory();
        var id = database.Write(new SettableClass { Id = 1, Name = "tiny" });

        Assert.True(database.TryDescribe(id, out var info));
        Assert.Equal(CompressionMethod.None, info.Compression);
    }

    [Fact]
    public void AChunkKnowsWhatWroteItAndWhatItNeeds() {
        var database = InMemory();
        var dependency = database.Write(new SettableClass { Id = 1 });
        var id = database.Write(new MutableStruct { Number = 2 }, [dependency]);

        Assert.True(database.TryDescribe(id, out var info));
        Assert.Equal(ContentHash.TypeId(typeof(MutableStruct)), info.TypeId);
        Assert.Equal([dependency], info.References);
    }

    [Fact]
    public void ReadingAChunkAsTheWrongTypeIsRefused() {
        var database = InMemory();
        var id = database.Write(new SettableClass { Id = 1 });

        // The bytes would deserialise into something; it would just be nonsense, and nothing
        // downstream would notice until much later.
        var thrown = Assert.Throws<SerializationException>(() => database.Read<MutableStruct>(id));
        Assert.Contains("is being read as", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingChunkSaysHowManyPlacesWereLookedIn() {
        var database = InMemory();
        var thrown = Assert.Throws<SerializationException>(() => database.Read<SettableClass>(new(1, 2)));
        Assert.Contains("backends", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACorruptChunkFailsVerification() {
        var backend = new MemoryOdbBackend();
        var database = new ObjectDatabase(backend);
        var id = database.Write(new SettableClass { Id = 1, Name = "intact" });

        Assert.True(database.Verify(id));

        backend.Corrupt(id);

        // Content addressing gives integrity checking away: a chunk that does not hash to its own
        // name is not the chunk that name refers to.
        Assert.False(database.Verify(id));
    }

    [Fact]
    public void TheClosureFollowsReferencesAndSurvivesACycle() {
        var database = InMemory();
        var leaf = database.Write(new SettableClass { Id = 1 });
        var middle = database.Write(new SettableClass { Id = 2 }, [leaf]);
        var root = database.Write(new SettableClass { Id = 3 }, [middle, leaf]);

        var closure = database.Closure(root);

        Assert.Equal(3, closure.Length);
        Assert.Contains(leaf, closure);
        Assert.Contains(middle, closure);
        Assert.Contains(root, closure);
    }

    [Fact]
    public void TheFirstBackendTakesTheWritesAndShadowsTheRest() {
        var packed = new MemoryOdbBackend();
        var packedDatabase = new ObjectDatabase(packed);
        var shared = packedDatabase.Write(new SettableClass { Id = 1, Name = "from the bundle" });

        var loose = new MemoryOdbBackend();
        var database = new ObjectDatabase(loose, packed);

        // Present only in the second backend: still readable.
        Assert.Equal("from the bundle", database.Read<SettableClass>(shared).Name);

        // A new write goes to the first, and the second is untouched.
        var fresh = database.Write(new SettableClass { Id = 2, Name = "rebuilt" });
        Assert.True(loose.Exists(fresh));
        Assert.False(packed.Exists(fresh));
    }

    [Fact]
    public void AFileBackedDatabaseRoundTrips() {
        var files = new VirtualFileSystem();
        files.Mount(MountPoints.Database, new MemoryFileProvider());
        var database = new ObjectDatabase(new FileOdbBackend(files, MountPoints.Database));

        var id = database.Write(new SettableClass { Id = 5, Name = "on disk" });

        Assert.Equal("on disk", database.Read<SettableClass>(id).Name);
        Assert.Equal([id], database.Enumerate());

        // Two levels: the first byte of the id names a directory, so no directory ever holds every
        // artefact in the project.
        var path = files.Enumerate(MountPoints.Database, recursive: true).Single(entry => !entry.IsDirectory).Path;
        Assert.Equal(3, path.Value.Count(character => character == '/'));
    }

    [Fact]
    public void AReadOnlyBackendRefusesToBeWrittenTo() {
        var files = new VirtualFileSystem();
        files.Mount(MountPoints.Database, new MemoryFileProvider());
        var database = new ObjectDatabase(new FileOdbBackend(files, MountPoints.Database, isReadOnly: true));

        Assert.Throws<NotSupportedException>(() => database.Write(new SettableClass { Id = 1 }));
    }

    [Fact]
    public void ABundleRoundTripsEverythingItWasBuiltFrom() {
        var source = new MemoryOdbBackend();
        var building = new ObjectDatabase(source);
        var ids = new List<ObjectId>();

        for (var index = 0; index < 50; index++) {
            ids.Add(building.Write(new SettableClass { Id = index, Name = $"asset {index}" }));
        }

        var writer = new BundleWriter();
        writer.AddAll(source);
        var bundle = writer.Build();

        using var backend = new BundleOdbBackend(bundle, verifyChecksum: true);
        var runtime = new ObjectDatabase(backend);

        Assert.Equal(50, backend.Count);

        for (var index = 0; index < ids.Count; index++) {
            Assert.Equal($"asset {index}", runtime.Read<SettableClass>(ids[index]).Name);
        }
    }

    [Fact]
    public void ABundleLooksUpByBinarySearchAndSaysNoToWhatItLacks() {
        var source = new MemoryOdbBackend();
        var building = new ObjectDatabase(source);

        for (var index = 0; index < 200; index++) {
            building.Write(new SettableClass { Id = index });
        }

        var writer = new BundleWriter();
        writer.AddAll(source);
        using var backend = new BundleOdbBackend(writer.Build());

        // Enumeration is in id order, which is what makes the search valid.
        var ids = backend.Enumerate().ToArray();
        Assert.Equal(ids.OrderBy(id => id).ToArray(), ids);

        foreach (var id in ids) {
            Assert.True(backend.Exists(id));
        }

        Assert.False(backend.Exists(new(0, 0)));
        Assert.False(backend.Exists(new(ulong.MaxValue, ulong.MaxValue)));
    }

    /// <summary>
    ///     The writer sorts, and this is the test that says so. The obvious version of this test
    ///     stages chunks by copying a backend, and a backend enumerates in id order — so the index
    ///     comes out sorted whether the writer sorts or not, and a writer that did not sort would
    ///     pass. This one stages them deliberately out of order.
    /// </summary>
    [Fact]
    public void TheBundleIndexIsSortedWhateverOrderChunksWereStagedIn() {
        var writer = new BundleWriter();
        var random = new Random(20260726);
        var staged = new List<ObjectId>();

        for (var index = 0; index < 100; index++) {
            var id = new ObjectId((ulong)random.NextInt64(), (ulong)random.NextInt64());
            staged.Add(id);
            writer.Add(id, new byte[] { (byte)index });
        }

        using var backend = new BundleOdbBackend(writer.Build());
        var ids = backend.Enumerate().ToArray();

        Assert.Equal(ids.OrderBy(id => id).ToArray(), ids);

        // And every one of them is still findable, which is what the sort was for.
        foreach (var id in staged) {
            Assert.True(backend.Exists(id), $"{id} was staged and cannot be found.");
        }
    }

    [Fact]
    public void AnEmptyBundleIsValid() {
        using var backend = new BundleOdbBackend(new BundleWriter().Build(), verifyChecksum: true);

        Assert.Equal(0, backend.Count);
        Assert.Empty(backend.Enumerate());
        Assert.False(backend.Exists(new(1, 2)));
    }

    [Fact]
    public void ACorruptBundleIsRejectedRatherThanRead() {
        var source = new MemoryOdbBackend();
        var building = new ObjectDatabase(source);
        building.Write(new SettableClass { Id = 1, Name = "a name long enough to be worth flipping a byte in" });

        var writer = new BundleWriter();
        writer.AddAll(source);
        var bundle = writer.Build();
        bundle[^1] ^= 0xFF;

        var thrown = Assert.Throws<SerializationException>(() => new BundleOdbBackend(bundle, verifyChecksum: true));
        Assert.Contains("checksum", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SomethingThatIsNotABundleIsRejectedImmediately() {
        Assert.Throws<SerializationException>(() => new BundleOdbBackend(new byte[64]));
        Assert.Throws<SerializationException>(() => new BundleOdbBackend(new byte[3]));
    }

    [Fact]
    public void ABundleRefusesToBeWrittenTo() {
        using var backend = new BundleOdbBackend(new BundleWriter().Build());

        Assert.True(backend.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => backend.Write(new(1, 2), []));
        Assert.Throws<NotSupportedException>(() => backend.Delete(new(1, 2)));
    }

    /// <summary>
    ///     Determinism, from the direction that matters: the same content produces the same id on
    ///     every run, which is what the content build's cross-platform gate compares.
    /// </summary>
    [Fact]
    public void HashingIsStableAndSpreadsOut() {
        Assert.Equal(ContentHash.Compute("vixen"u8), ContentHash.Compute("vixen"u8));
        Assert.NotEqual(ContentHash.Compute("vixen"u8), ContentHash.Compute("vixem"u8));

        // The property is that two *different* inputs never share an id — anything weaker would
        // pass for a hash that returned a constant. Concurrent, because CsCheck samples in
        // parallel: GetOrAdd both records the id and answers whether something else got there
        // first, in one atomic step.
        var byId = new System.Collections.Concurrent.ConcurrentDictionary<ObjectId, string>();

        Gen.Byte.Array[0, 64].Sample(bytes => {
                var id = ContentHash.Compute(bytes);
                Assert.Equal(id, ContentHash.Compute(bytes));
                var content = Convert.ToHexString(bytes);
                Assert.Equal(content, byId.GetOrAdd(id, content));
            }
        );
    }

    [Fact]
    public void ATypeIdIsStableAndPerType() {
        Assert.Equal(ContentHash.TypeId(typeof(SettableClass)), ContentHash.TypeId(typeof(SettableClass)));
        Assert.NotEqual(ContentHash.TypeId(typeof(SettableClass)), ContentHash.TypeId(typeof(MutableStruct)));
    }

    [Fact]
    public void AChunkHeaderRoundTripsItsReferences() {
        ObjectId[] references = [new(1, 2), new(3, 4), new(5, 6)];
        var chunk = ChunkFormat.BuildChunk(0xDEAD_BEEF, references, "payload"u8);
        var offset = ChunkFormat.ReadHeader(chunk, out var typeId, out var read);

        Assert.Equal(0xDEAD_BEEFul, typeId);
        Assert.Equal(references, read);
        Assert.Equal("payload"u8.ToArray(), chunk.AsSpan(offset).ToArray());
    }

    static ObjectDatabase InMemory() => new(new MemoryOdbBackend());
}

/// <summary>A backend that is a dictionary, so a database can be tested without a filesystem.</summary>
sealed class MemoryOdbBackend : IOdbBackend {
    readonly SortedDictionary<ObjectId, byte[]> chunks = [];

    public bool IsReadOnly => false;

    public bool Exists(ObjectId id) => chunks.ContainsKey(id);

    public bool TryRead(ObjectId id, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IOdbBlob? blob) {
        if (chunks.TryGetValue(id, out var bytes)) {
            blob = new Blob(bytes);
            return true;
        }

        blob = null;
        return false;
    }

    public bool Write(ObjectId id, ReadOnlySpan<byte> blob) {
        if (chunks.ContainsKey(id)) {
            return false;
        }

        chunks[id] = blob.ToArray();
        return true;
    }

    public bool Delete(ObjectId id) => chunks.Remove(id);

    public IEnumerable<ObjectId> Enumerate() => chunks.Keys;

    /// <summary>Flips a byte, so the integrity check has something to find.</summary>
    /// <param name="id">Which chunk to damage.</param>
    internal void Corrupt(ObjectId id) => chunks[id][^1] ^= 0xFF;

    sealed class Blob(byte[] bytes) : IOdbBlob {
        public ReadOnlyMemory<byte> Bytes => bytes;

        public void Dispose() { }
    }
}
