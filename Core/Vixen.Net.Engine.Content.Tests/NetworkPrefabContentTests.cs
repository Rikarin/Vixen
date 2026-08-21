// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Assets;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Serialization.Storage;
using Vixen.Ecs;
using Vixen.Engine.Scenes;
using Vixen.Engine.Transforms;
using Vixen.Net.Replication;
using Xunit;

namespace Vixen.Net.Engine.Content.Tests;

/// <summary>A content build in a dozen lines: chunks in a bundle, addresses in a catalog.</summary>
/// <remarks>
///     ⚠ <b>The chunks are written the way the importer writes them</b> — <c>ObjectDatabase.Write</c>
///     under <c>PrefabAsset</c>'s own type id — rather than as raw bytes, because the whole question
///     this suite is asking is whether a labelled address comes back through
///     <c>AssetManager.LoadAsync&lt;PrefabAsset&gt;</c> as a template. A harness that wrote raw bytes
///     and read them raw would pass without the asset system ever being involved.
/// </remarks>
sealed class Shipped {
    readonly List<(string Address, object? Asset, byte[]? Payload, string[] Labels)> planned = [];

    public AssetManager Assets { get; private set; } = null!;

    /// <summary>Ships a prefab at an address, as SceneImporter would.</summary>
    public Shipped Prefab(string address, PrefabAsset asset, params string[] labels) {
        planned.Add((address, asset, null, labels));

        return this;
    }

    /// <summary>Ships bytes that are not a prefab — a compressed texture, as far as this cares.</summary>
    public Shipped Raw(string address, byte[] payload, params string[] labels) {
        planned.Add((address, null, payload, labels));

        return this;
    }

    public Shipped Build() {
        var files = new VirtualFileSystem();
        var storage = new MemoryFileProvider();

        files.Mount(new("/store"), storage);
        files.Mount(new("/bundles"), storage);

        var scratch = new FileOdbBackend(files, new("/store/odb"));
        var writing = new ObjectDatabase(scratch);
        var entries = new List<CatalogEntry>();

        foreach (var (address, asset, payload, labels) in planned) {
            var id = asset is PrefabAsset prefab
                ? writing.Write(in prefab)
                : writing.WriteRaw(ContentHash.TypeId(typeof(byte[])), [], payload!);

            entries.Add(new(address, id, "Main", ContentProvider.Local, [], [.. labels], 0));
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
            entries,
            [new("Main", "", default, 0, 0, CompressionMethod.Lz4, [])]
        );

        Assets = new(catalog, new LocalBundleSource(files, new("/bundles")));

        return this;
    }
}

/// <summary>Filling the prefab registry out of a build, rather than out of a start-up path.</summary>
public class NetworkPrefabContentTests : IDisposable {
    readonly NetworkPrefabRegistry registry = new();

    public void Dispose() {
        // The templates are worlds. A game holds them for the life of the process; a test gives them
        // back, or every case in this file leaks one per prefab.
        foreach (var entry in registry.Prefabs) {
            entry.Prefab.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>A root, a barrel the designer marked networked, and a decorative sight.</summary>
    /// <returns>The root.</returns>
    static Entity Author(World world) {
        var root = Hierarchy.CreateTransform(world, LocalTransform.Identity);
        var barrel = Hierarchy.CreateTransform(world, LocalTransform.At(new(0f, 1f, 0f)));
        var sight = Hierarchy.CreateTransform(world, LocalTransform.At(new(0f, 1f, 0.5f)));

        // The opt-in NetworkPrefabRegistry documents: "a designer opts an entity into being
        // addressable by putting the component on it". NetworkObject and not NetworkId, because a
        // designer marks content and only a server allocates a handle — and because the handle is
        // the one of the two a compiled scene cannot carry.
        world.Add<NetworkObject>(barrel);

        Hierarchy.SetParent(world, sight, root);
        Hierarchy.SetParent(world, barrel, root);

        return root;
    }

    /// <summary>The same three entities, compiled — as SceneImporter would have written them.</summary>
    static PrefabAsset Turret(string name = "turret") {
        using var world = new World($"authoring:{name}");

        return new() { Name = name, Content = SceneContent.Capture(world, [Author(world)]) };
    }

    [Fact]
    public async Task EveryPrefabUnderTheLabelIsRegistered() {
        var shipped = new Shipped()
            .Prefab("prefabs/turret", Turret(), NetworkPrefabContent.Label)
            .Prefab("prefabs/crate", Turret("crate"), NetworkPrefabContent.Label)
            .Prefab("prefabs/door", Turret("door"), NetworkPrefabContent.Label)
            .Build();

        var load = await NetworkPrefabContent.LoadAsync(
            registry,
            shipped.Assets,
            TestContext.Current.CancellationToken
        );

        Assert.Empty(load.Problems);
        Assert.Equal(3, registry.Count);

        Assert.Equal(
            ["prefabs/crate", "prefabs/door", "prefabs/turret"],
            load.Registered.Select(entry => entry.Address).ToArray()
        );
    }

    /// <summary>The point of the id being a hash: neither peer sends the table.</summary>
    [Fact]
    public async Task ARegisteredPrefabsIdIsTheHashOfItsAddress() {
        var shipped = new Shipped()
            .Prefab("prefabs/turret", Turret(), NetworkPrefabContent.Label)
            .Build();

        await NetworkPrefabContent.LoadAsync(registry, shipped.Assets, TestContext.Current.CancellationToken);

        Assert.Equal(NetworkPrefabId.From("prefabs/turret"), registry.Require("prefabs/turret").Id);
        Assert.Equal(3, registry.Require("prefabs/turret").Prefab.EntityCount);
    }

    /// <summary>An unlabelled prefab is not a networked one, which is the whole opt-in.</summary>
    [Fact]
    public async Task WhatIsNotLabelledIsNotRegistered() {
        var shipped = new Shipped()
            .Prefab("prefabs/turret", Turret(), NetworkPrefabContent.Label)
            .Prefab("prefabs/bush", Turret("bush"))
            .Build();

        var load = await NetworkPrefabContent.LoadAsync(
            registry,
            shipped.Assets,
            TestContext.Current.CancellationToken
        );

        Assert.Empty(load.Problems);
        Assert.Single(load.Registered);
        Assert.False(registry.TryGet("prefabs/bush", out _));
    }

    /// <summary>A label nothing carries is an empty registry rather than a failure.</summary>
    [Fact]
    public async Task ALabelNothingCarriesRegistersNothing() {
        var shipped = new Shipped().Prefab("prefabs/turret", Turret()).Build();

        var load = await NetworkPrefabContent.LoadAsync(
            registry,
            shipped.Assets,
            TestContext.Current.CancellationToken
        );

        Assert.Empty(load.Registered);
        Assert.Empty(load.Problems);
        Assert.Equal(0, registry.Count);
    }

    /// <summary>Two groups, one registry — because a spawn carries an id and nothing else.</summary>
    [Fact]
    public async Task SeveralLabelsFillOneRegistry() {
        var shipped = new Shipped()
            .Prefab("prefabs/turret", Turret(), "creatures")
            .Prefab("prefabs/waggon", Turret("waggon"), "vehicles")
            .Build();

        var load = await NetworkPrefabContent.LoadAsync(
            registry,
            shipped.Assets,
            ["creatures", "vehicles"],
            TestContext.Current.CancellationToken
        );

        Assert.Empty(load.Problems);
        Assert.Equal(2, registry.Count);
    }

    /// <summary>A .vxgroup broad enough to sweep up a texture: the rest registers and the one is named.</summary>
    [Fact]
    public async Task SomethingElseUnderTheLabelIsAProblemAndTheRestRegisters() {
        var shipped = new Shipped()
            .Prefab("prefabs/turret", Turret(), NetworkPrefabContent.Label)
            .Raw("art/icon", [1, 2, 3, 4, 5, 6, 7, 8], NetworkPrefabContent.Label)
            .Prefab("prefabs/crate", Turret("crate"), NetworkPrefabContent.Label)
            .Build();

        var load = await NetworkPrefabContent.LoadAsync(
            registry,
            shipped.Assets,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(2, registry.Count);
        Assert.Equal(2, load.Registered.Length);

        var problem = Assert.Single(load.Problems);

        Assert.Contains("art/icon", problem, StringComparison.Ordinal);
    }

    /// <summary>Address order, so a build that fails twice fails the same way.</summary>
    [Fact]
    public async Task ProblemsComeBackInAddressOrder() {
        var shipped = new Shipped()
            .Raw("z/second", [9, 9, 9, 9], NetworkPrefabContent.Label)
            .Raw("a/first", [8, 8, 8, 8], NetworkPrefabContent.Label)
            .Build();

        var load = await NetworkPrefabContent.LoadAsync(
            registry,
            shipped.Assets,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(2, load.Problems.Length);
        Assert.Contains("a/first", load.Problems[0], StringComparison.Ordinal);
        Assert.Contains("z/second", load.Problems[1], StringComparison.Ordinal);
    }

    /// <summary>A hand-written list is the caller's, so a name that is not in the build throws.</summary>
    /// <remarks>
    ///     ⚠ The half of the label/address distinction that is easy to lose. Reporting this as a
    ///     problem would turn a typo in a list somebody typed into a prefab that can never be spawned,
    ///     with the message buried among the content's own.
    /// </remarks>
    [Fact]
    public async Task AnAddressThatIsNotInTheBuildIsAnExceptionRatherThanAProblem() {
        var shipped = new Shipped().Prefab("prefabs/turret", Turret(), NetworkPrefabContent.Label).Build();

        await Assert.ThrowsAsync<AddressNotFoundException>(
            async () => await NetworkPrefabContent.LoadFromAsync(
                registry,
                shipped.Assets,
                ["prefabs/turret", "prefabs/nothing"],
                TestContext.Current.CancellationToken
            )
        );
    }

    /// <summary>The escape hatch: a list in code rather than a group.</summary>
    [Fact]
    public async Task LoadFromRegistersExactlyTheAddressesItWasGiven() {
        var shipped = new Shipped()
            .Prefab("prefabs/turret", Turret())
            .Prefab("prefabs/crate", Turret("crate"))
            .Build();

        var load = await NetworkPrefabContent.LoadFromAsync(
            registry,
            shipped.Assets,
            ["prefabs/crate", "prefabs/crate"],
            TestContext.Current.CancellationToken
        );

        Assert.Empty(load.Problems);
        Assert.Single(load.Registered);
        Assert.False(registry.TryGet("prefabs/turret", out _));
    }

    /// <summary>
    ///     Loading the same build twice is a no-op, not a refusal — the registry's own rule is that
    ///     two templates under one address are a desync, and a second read would produce exactly that.
    /// </summary>
    [Fact]
    public async Task LoadingTheSameBuildTwiceRegistersOnce() {
        var shipped = new Shipped()
            .Prefab("prefabs/turret", Turret(), NetworkPrefabContent.Label)
            .Build();

        var first = await NetworkPrefabContent.LoadAsync(
            registry,
            shipped.Assets,
            TestContext.Current.CancellationToken
        );

        var second = await NetworkPrefabContent.LoadAsync(
            registry,
            shipped.Assets,
            TestContext.Current.CancellationToken
        );

        Assert.Empty(second.Problems);
        Assert.Equal(1, registry.Count);
        Assert.Same(first.Registered[0].Prefab, second.Registered[0].Prefab);
    }

    /// <summary>Two peers reading one build register the same ids, which is the design's whole claim.</summary>
    [Fact]
    public async Task TwoPeersReadingOneBuildAgreeWithoutAHandshake() {
        var shipped = new Shipped()
            .Prefab("prefabs/turret", Turret(), NetworkPrefabContent.Label)
            .Prefab("prefabs/crate", Turret("crate"), NetworkPrefabContent.Label)
            .Build();

        var client = new NetworkPrefabRegistry();

        try {
            var server = await NetworkPrefabContent.LoadAsync(
                registry,
                shipped.Assets,
                TestContext.Current.CancellationToken
            );

            var other = await NetworkPrefabContent.LoadAsync(
                client,
                shipped.Assets,
                TestContext.Current.CancellationToken
            );

            Assert.Equal(
                server.Registered.Select(entry => entry.Id),
                other.Registered.Select(entry => entry.Id)
            );
        } finally {
            foreach (var entry in client.Prefabs) {
                entry.Prefab.Dispose();
            }
        }
    }

    /// <summary>What is already in the registry stays, so a game may mix a build's prefabs with its own.</summary>
    [Fact]
    public async Task WhatWasRegisteredByHandSurvivesALoad() {
        using var world = new World("by-hand");

        // Not disposed here: the registry holds it now and the fixture gives every template back.
        registry.Register(
            "prefabs/built-in-code",
            Prefab.CaptureFrom(world, world.Create(default(LocalTransform)), "built")
        );

        var shipped = new Shipped()
            .Prefab("prefabs/turret", Turret(), NetworkPrefabContent.Label)
            .Build();

        var load = await NetworkPrefabContent.LoadAsync(
            registry,
            shipped.Assets,
            TestContext.Current.CancellationToken
        );

        Assert.Single(load.Registered);
        Assert.Equal(2, registry.Count);
        Assert.True(registry.TryGet("prefabs/built-in-code", out _));
    }

    /// <summary>
    ///     ⚠ <b>What the designer marked survives the content build, so a compiled prefab has as many
    ///     networked nodes as the one it was compiled from.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The A/B is the whole assertion: the same three entities registered twice, once captured
    ///         straight out of a live world and once through an <c>ObjectDatabase</c> chunk, a bundle,
    ///         a catalog and <c>AssetManager</c>. Both have to agree about which of them wants an id,
    ///         because a server and a client routinely take different doors to the same prefab and a
    ///         spawn's ids are a run counted off the root — two peers that disagree about the count do
    ///         not merely lose a component, they apply the rest to the wrong entities.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This asserted the opposite until <see cref="NetworkObject" /> existed</b>, as
    ///         <c>ANetworkIdMarkerCannotSurviveTheContentBuildYet</c>. The marker was
    ///         <see cref="NetworkId" />, which is <c>[Component]</c> and not <c>[DataContract]</c>, so
    ///         <c>SceneContent.Capture</c> dropped it silently and every prefab out of a build had
    ///         exactly one networked node whatever its author said. Kept as a test rather than
    ///         deleted, because the failure it names is invisible without one: the prefab loads, it
    ///         spawns, and the turret's barrel simply never replicates.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task ANetworkedMarkerSurvivesTheContentBuild() {
        using var world = new World("authoring:direct");

        // The same three entities, captured without going through an asset. Not disposed here — the
        // registry holds it and the fixture gives every template back.
        registry.Register("prefabs/turret-in-code", Prefab.CaptureFrom(world, Author(world), "turret"));

        var shipped = new Shipped()
            .Prefab("prefabs/turret", Turret(), NetworkPrefabContent.Label)
            .Build();

        await NetworkPrefabContent.LoadAsync(registry, shipped.Assets, TestContext.Current.CancellationToken);

        var direct = registry.Require("prefabs/turret-in-code");
        var loaded = registry.Require("prefabs/turret");

        // Three entities either way, and the same two of them addressed: the root, which is what the
        // spawn is sent to, and the barrel the designer marked. The sight is scenery and stays free.
        Assert.Equal(3, direct.Prefab.EntityCount);
        Assert.Equal(3, loaded.Prefab.EntityCount);
        Assert.Equal(direct.Networked, loaded.Networked);
        Assert.Equal([0, 1], loaded.Networked);
        Assert.Equal(2, loaded.IdCount);
    }

    /// <summary>
    ///     A template captured out of a world that has been in a session is marked by what that world
    ///     had on it, which is a <see cref="NetworkId" /> and not a <see cref="NetworkObject" />.
    /// </summary>
    /// <remarks>
    ///     The reason <c>NetworkPrefabRegistry</c> reads both. <c>Prefab.CaptureFrom</c> takes a live
    ///     subtree, and a live networked subtree carries allocated ids; refusing to count those would
    ///     make "capture this thing I am looking at as a prefab" quietly drop every node in it. Nothing
    ///     writes a <see cref="NetworkId" /> into an asset — only this direction exists.
    /// </remarks>
    [Fact]
    public void AnAllocatedIdOnALiveWorldAlsoMarksANode() {
        using var world = new World("authoring:live");

        var root = Hierarchy.CreateTransform(world, LocalTransform.Identity);
        var turret = Hierarchy.CreateTransform(world, LocalTransform.At(new(0f, 1f, 0f)));

        Hierarchy.SetParent(world, turret, root);

        var ids = new NetworkIdAllocator();

        world.Add(root, ids.Next());
        world.Add(turret, ids.Next());

        registry.Register("prefabs/live", Prefab.CaptureFrom(world, root, "live"));

        Assert.Equal([0, 1], registry.Require("prefabs/live").Networked);
    }
}
