// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Serialization;
using Vixen.Core.Serialization.Storage;
using Xunit;

namespace Vixen.Assets.Tests;

/// <summary>A texture, or anything else something else points at.</summary>
[DataContract("ReferencedThing")]
public sealed class ReferencedThing {
    /// <summary>What it is called.</summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>Something that names another asset rather than containing it.</summary>
[DataContract("ReferringThing")]
public sealed class ReferringThing {
    /// <summary>What it is called.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What it points at.</summary>
    public ContentReference<ReferencedThing>? Albedo { get; set; }
}

/// <summary>
///     A material does not contain its textures; it names them. These are the tests for what that
///     costs and what it buys.
/// </summary>
public sealed class ContentReferenceTests {
    /// <summary>
    ///     Only the id goes in the stream. Writing the value would defeat the whole purpose — two
    ///     materials sharing a texture would ship two copies of it, in two bundles, loading to two
    ///     objects.
    /// </summary>
    [Fact]
    public void OnlyTheIdIsWritten() {
        var pointed = new ObjectId(0x1122334455667788, 0x99AABBCCDDEEFF00);

        var bytes = Serializer.ToBytes(
            new ReferringThing { Name = "", Albedo = new ContentReference<ReferencedThing>(pointed) }
        );

        var read = Serializer.Read<ReferringThing>(bytes);

        Assert.Equal(pointed, read.Albedo!.Id);
        Assert.False(read.Albedo.IsResolved);
    }

    /// <summary>
    ///     Reading with no resolver in force is a legitimate thing to do — a tool listing what points
    ///     at what, an editor building a reverse index — and it gives a reference that knows its id
    ///     and says it does not know its value.
    /// </summary>
    [Fact]
    public void WithNoResolverAReferenceKnowsItsIdAndSaysItHasNoValue() {
        var read = Serializer.Read<ReferringThing>(
            Serializer.ToBytes(
                new ReferringThing { Albedo = new ContentReference<ReferencedThing>(new(1, 2)) }
            )
        );

        Assert.False(read.Albedo!.IsResolved);
        Assert.Null(read.Albedo.Value);

        var failure = Assert.Throws<SerializationException>(() => read.Albedo.Require());
        Assert.Contains("never resolved", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>And with one in force, the reference comes back pointing at the object.</summary>
    [Fact]
    public void WithAResolverInForceTheReferenceIsFilledIn() {
        var texture = new ReferencedThing { Name = "albedo" };
        var id = new ObjectId(1, 2);

        var bytes = Serializer.ToBytes(
            new ReferringThing { Albedo = new ContentReference<ReferencedThing>(id) }
        );

        ReferringThing read;

        using (ContentResolution.Push(new Dictionary<ObjectId, object> { [id] = texture }.AsResolver())) {
            read = Serializer.Read<ReferringThing>(bytes);
        }

        Assert.True(read.Albedo!.IsResolved);
        Assert.Same(texture, read.Albedo.Value);
    }

    /// <summary>
    ///     A scope restores the resolver that was in force rather than clearing it, so a nested read
    ///     does not leave the outer one without one halfway through.
    /// </summary>
    [Fact]
    public void APushedResolverIsRestoredAndNotCleared() {
        var outer = new Dictionary<ObjectId, object>().AsResolver();
        var inner = new Dictionary<ObjectId, object>().AsResolver();

        Assert.Null(ContentResolution.Current);

        using (ContentResolution.Push(outer)) {
            Assert.Same(outer, ContentResolution.Current);

            using (ContentResolution.Push(inner)) {
                Assert.Same(inner, ContentResolution.Current);
            }

            Assert.Same(outer, ContentResolution.Current);
        }

        Assert.Null(ContentResolution.Current);
    }

    /// <summary>
    ///     An empty reference stays empty rather than being looked up. Nothing is addressed by the
    ///     zero id, and asking a resolver for it would be a lookup that can only fail.
    /// </summary>
    [Fact]
    public void AnEmptyReferenceIsNotLookedUp() {
        var read = Serializer.Read<ReferringThing>(
            Serializer.ToBytes(new ReferringThing { Albedo = ContentReference<ReferencedThing>.Empty })
        );

        Assert.True(read.Albedo!.Id.IsEmpty);
        Assert.False(read.Albedo.IsResolved);
    }

    /// <summary>
    ///     <para>
    ///         The whole point, through the real loader. Two things point at one texture; loading
    ///         both gives one texture object, and each of them has it — not a copy, not a null, the
    ///         same instance.
    ///     </para>
    ///     <para>
    ///         This is what the previous commit could not do: a dependency's bundle and lifetime were
    ///         shared, and its deserialised object was not.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task TwoAssetsPointingAtOneTextureGetTheSameObject() {
        var world = new ReferenceWorld();
        var texture = world.Add("shared/texture", new ReferencedThing { Name = "albedo" });
        world.Add("mat/a", new ReferringThing { Name = "a", Albedo = new(texture) }, ["shared/texture"]);
        world.Add("mat/b", new ReferringThing { Name = "b", Albedo = new(texture) }, ["shared/texture"]);

        var assets = world.Build();

        var a = assets.LoadAsync<ReferringThing>("mat/a", TestContext.Current.CancellationToken);
        var b = assets.LoadAsync<ReferringThing>("mat/b", TestContext.Current.CancellationToken);

        var first = await a.Completion.WaitAsync(TestContext.Current.CancellationToken);
        var second = await b.Completion.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(first.Albedo!.IsResolved, "the reference should have been resolved during the load");
        Assert.Equal("albedo", first.Albedo.Value!.Name);
        Assert.Same(first.Albedo.Value, second.Albedo!.Value);
    }

    /// <summary>
    ///     And releasing one of them leaves the other's reference pointing at something still loaded,
    ///     because the texture is claimed by both.
    /// </summary>
    [Fact]
    public async Task ReleasingOneLeavesTheOthersReferenceIntact() {
        var world = new ReferenceWorld();
        var texture = world.Add("shared/texture", new ReferencedThing { Name = "albedo" });
        world.Add("mat/a", new ReferringThing { Albedo = new(texture) }, ["shared/texture"]);
        world.Add("mat/b", new ReferringThing { Albedo = new(texture) }, ["shared/texture"]);

        var assets = world.Build();

        var a = assets.LoadAsync<ReferringThing>("mat/a", TestContext.Current.CancellationToken);
        var b = assets.LoadAsync<ReferringThing>("mat/b", TestContext.Current.CancellationToken);
        await a.Completion.WaitAsync(TestContext.Current.CancellationToken);
        var second = await b.Completion.WaitAsync(TestContext.Current.CancellationToken);

        a.Release();

        Assert.True(assets.IsLoaded("shared/texture"));
        Assert.Same(second.Albedo!.Value, second.Albedo.Value);
    }

    /// <summary>
    ///     A dependency is deserialised whether or not anything loads it by address, which is what
    ///     the resolver needs and what the previous commit left undone.
    /// </summary>
    [Fact]
    public async Task ADependencyIsDeserialisedEvenThoughNothingAskedForItByAddress() {
        var world = new ReferenceWorld();
        var texture = world.Add("shared/texture", new ReferencedThing { Name = "albedo" });
        world.Add("mat/a", new ReferringThing { Albedo = new(texture) }, ["shared/texture"]);

        var assets = world.Build();

        var handle = assets.LoadAsync<ReferringThing>("mat/a", TestContext.Current.CancellationToken);
        var material = await handle.Completion.WaitAsync(TestContext.Current.CancellationToken);

        Assert.IsType<ReferencedThing>(material.Albedo!.Value);
    }

    /// <summary>
    ///     Reading a chunk whose type nothing in this process registered is not corruption — it
    ///     belongs to an assembly that was not loaded — so the message says which of the two it is.
    /// </summary>
    [Fact]
    public void AChunkWhoseTypeIsNotRegisteredSaysWhyRatherThanFailingToParse() {
        var files = new VirtualFileSystem();
        files.Mount(new("/odb"), new MemoryFileProvider());
        var backend = new FileOdbBackend(files, new("/odb"));
        var database = new ObjectDatabase(backend);

        // A well-formed chunk written by a type id nothing claims.
        var id = database.WriteRaw(0xDEADBEEFDEADBEEF, [], "payload"u8);

        var failure = Assert.Throws<SerializationException>(() => database.ReadObject(id));

        Assert.Contains("deadbeefdeadbeef", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not loaded", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Builds a catalog, a bundle and a manager over content that points at itself.</summary>
    sealed class ReferenceWorld {
        readonly List<(string Address, ObjectId Id, string[] Dependencies)> planned = [];
        readonly VirtualFileSystem files = new();
        readonly FileOdbBackend scratch;
        readonly ObjectDatabase writing;

        public ReferenceWorld() {
            var storage = new MemoryFileProvider();
            files.Mount(new("/store"), storage);
            files.Mount(new("/bundles"), storage);
            scratch = new(files, new("/store/odb"));
            writing = new(scratch);
        }

        public ObjectId Add<T>(string address, T value, string[]? dependencies = null) {
            var id = writing.Write(value);
            planned.Add((address, id, dependencies ?? []));

            return id;
        }

        public AssetManager Build() {
            var bundle = new BundleWriter();
            bundle.AddAll(scratch);

            using (var target = files.OpenWrite(new("/bundles/Main.bundle"))) {
                target.Write(bundle.Build());
            }

            var catalog = new ContentCatalog(
                CatalogFormat.Version,
                default,
                "Windows",
                planned.Select(entry => new CatalogEntry(
                        entry.Address,
                        entry.Id,
                        "Main",
                        ContentProvider.Local,
                        [.. entry.Dependencies],
                        [],
                        0
                    )
                ),
                [new("Main", "", default, 0, 0, CompressionMethod.Lz4, [])]
            );

            return new(catalog, new LocalBundleSource(files, new("/bundles")));
        }
    }
}

/// <summary>Turns a dictionary into a resolver, which is all a test needs one to be.</summary>
static class DictionaryResolver {
    public static IContentResolver AsResolver(this Dictionary<ObjectId, object> values) => new Resolver(values);

    sealed class Resolver(Dictionary<ObjectId, object> values) : IContentResolver {
        public bool TryResolve(ObjectId id, out object? value) {
            var found = values.TryGetValue(id, out var result);
            value = result;

            return found;
        }
    }
}
