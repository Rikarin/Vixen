// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Assets;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Serialization.Storage;
using Vixen.Gameplay.Items;
using Xunit;

namespace Vixen.Gameplay.Content.Tests;

/// <summary>A content build in a dozen lines: chunks in a bundle, addresses in a catalog.</summary>
sealed class Shipped {
    readonly List<(string Address, byte[] Payload, string[] Labels)> planned = [];

    public AssetManager Assets { get; private set; } = null!;

    /// <summary>Ships a definition at an address, as the importer would.</summary>
    public Shipped Definition(string address, Definition definition, params string[] labels) {
        planned.Add((address, DefinitionSerialization.ToBytes(definition), labels));

        return this;
    }

    /// <summary>Ships bytes that are not a definition — a compressed texture, as far as this cares.</summary>
    public Shipped Raw(string address, byte[] payload, params string[] labels) {
        planned.Add((address, payload, labels));

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

        foreach (var (address, payload, labels) in planned) {
            var id = writing.WriteRaw(ContentHash.TypeId(typeof(Definition)), [], payload);

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

public class DefinitionContentTests {
    static ItemDefinition Item(string name) => new() { DisplayName = name, Slot = "Slot.Weapon" };

    [Fact]
    public async Task EveryDefinitionUnderTheLabelLandsInOneCatalog() {
        var shipped = new Shipped()
            .Definition("items/sword", Item("A sword"), DefinitionContent.Label)
            .Definition("items/shield", Item("A shield"), DefinitionContent.Label)
            .Definition("items/helm", Item("A helm"), DefinitionContent.Label)
            .Build();

        var load = await DefinitionContent.LoadAsync(shipped.Assets, TestContext.Current.CancellationToken);

        Assert.Empty(load.Problems);
        Assert.Equal(3, load.Catalog.Count);
        Assert.NotNull(load.Catalog.Find(DefId.From("items/sword")));
    }

    [Fact]
    public async Task TheAddressIsStampedOnByTheCatalogRatherThanCarriedInTheBytes() {
        // DefinitionSerialization.FromBytes answers a definition with no address on it; where it was
        // found is the content build's answer and not the file's.
        var shipped = new Shipped()
            .Definition("items/sword", Item("A sword"), DefinitionContent.Label)
            .Build();

        var load = await DefinitionContent.LoadAsync(shipped.Assets, TestContext.Current.CancellationToken);
        var definition = load.Catalog.Find(DefId.From("items/sword"))!;

        Assert.Equal("items/sword", definition.Address);
        Assert.Equal(DefId.From("items/sword"), definition.Id);
    }

    [Fact]
    public async Task NothingOutsideTheLabelIsLoaded() {
        var shipped = new Shipped()
            .Definition("items/sword", Item("A sword"), DefinitionContent.Label)
            .Definition("items/unshipped", Item("Not in the group"))
            .Build();

        var load = await DefinitionContent.LoadAsync(shipped.Assets, TestContext.Current.CancellationToken);

        Assert.Equal(1, load.Catalog.Count);
    }

    [Fact]
    public async Task SeveralLabelsBakeOneTagTable() {
        // ⚠ The reason the overload takes several. Two catalogs would number their tags separately,
        // so Slot.Weapon would be a different integer in each and every rule that crossed them would
        // be asking about the wrong tag.
        var shipped = new Shipped()
            .Definition("items/sword", Item("A sword"), "items")
            .Definition("items/wand", Item("A wand"), "quests")
            .Build();

        var load = await DefinitionContent.LoadAsync(
            shipped.Assets,
            ["items", "quests"],
            TestContext.Current.CancellationToken
        );

        Assert.Equal(2, load.Catalog.Count);
        Assert.True(load.Catalog.Tags.Resolve("Slot.Weapon").IsSome);
    }

    [Fact]
    public async Task ALabelNothingCarriesContributesNothingRatherThanFailing() {
        var shipped = new Shipped().Definition("items/sword", Item("A sword"), "items").Build();

        var load = await DefinitionContent.LoadAsync(
            shipped.Assets,
            ["items", "a-group-this-build-does-not-have"],
            TestContext.Current.CancellationToken
        );

        Assert.Empty(load.Problems);
        Assert.Equal(1, load.Catalog.Count);
    }

    [Fact]
    public async Task SomethingLabelledADefinitionAndNotOneIsReportedAndTheRestStillLoads() {
        // ⚠ A .vxgroup that is too broad is a content mistake, so it reads like every other content
        // mistake in doc 28: the rest compiles and the problem is named.
        var shipped = new Shipped()
            .Definition("items/sword", Item("A sword"), DefinitionContent.Label)
            .Raw("art/icon", [1, 2, 3, 4, 5, 6, 7, 8], DefinitionContent.Label)
            .Definition("items/shield", Item("A shield"), DefinitionContent.Label)
            .Build();

        var load = await DefinitionContent.LoadAsync(shipped.Assets, TestContext.Current.CancellationToken);

        Assert.Equal(2, load.Catalog.Count);
        Assert.Single(load.Problems);
        Assert.Contains("art/icon", load.Problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProblemsComeBackInAddressOrderWhateverOrderTheCatalogHeldThem() {
        // Not an artefact property — the catalog's own hash already sorts — but a diagnostic one: a
        // build that fails twice should fail the same way both times.
        var shipped = new Shipped()
            .Raw("z/second", [9, 9, 9, 9], DefinitionContent.Label)
            .Raw("a/first", [8, 8, 8, 8], DefinitionContent.Label)
            .Build();

        var load = await DefinitionContent.LoadAsync(shipped.Assets, TestContext.Current.CancellationToken);

        Assert.Equal(2, load.Problems.Length);
        Assert.Contains("a/first", load.Problems[0], StringComparison.Ordinal);
        Assert.Contains("z/second", load.Problems[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAddressGivenTwiceIsReadOnce() {
        var shipped = new Shipped().Definition("items/sword", Item("A sword")).Build();

        var load = await DefinitionContent.LoadFromAsync(
            shipped.Assets,
            ["items/sword", "items/sword", "items/sword"],
            TestContext.Current.CancellationToken
        );

        Assert.Empty(load.Problems);
        Assert.Equal(1, load.Catalog.Count);
    }

    [Fact]
    public async Task AnAddressThisBuildDoesNotHaveThrowsRatherThanBeingAProblem() {
        // ⚠ The line between the two. A label pointing at the wrong thing is content being wrong; an
        // address that is not in the catalog at all is the caller being wrong, and swallowing it
        // would turn a typo in a hand-written list into a rule that silently never fires.
        var shipped = new Shipped().Definition("items/sword", Item("A sword")).Build();

        await Assert.ThrowsAsync<AddressNotFoundException>(
            async () => await DefinitionContent.LoadFromAsync(
                shipped.Assets,
                ["items/nothing-here"],
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public async Task ABuildWithNoDefinitionsLoadsAnEmptyCatalogRatherThanFailing() {
        var shipped = new Shipped().Raw("art/icon", [1, 2, 3, 4]).Build();

        var load = await DefinitionContent.LoadAsync(shipped.Assets, TestContext.Current.CancellationToken);

        Assert.Empty(load.Problems);
        Assert.Equal(0, load.Catalog.Count);
    }

    [Fact]
    public async Task TheSameContentLoadedTwiceHasTheSameBuildHash() {
        // What two peers compare before a tag index means the same thing at both ends.
        var first = new Shipped()
            .Definition("items/shield", Item("A shield"), DefinitionContent.Label)
            .Definition("items/sword", Item("A sword"), DefinitionContent.Label)
            .Build();

        var second = new Shipped()
            .Definition("items/sword", Item("A sword"), DefinitionContent.Label)
            .Definition("items/shield", Item("A shield"), DefinitionContent.Label)
            .Build();

        var one = await DefinitionContent.LoadAsync(first.Assets, TestContext.Current.CancellationToken);
        var two = await DefinitionContent.LoadAsync(second.Assets, TestContext.Current.CancellationToken);

        Assert.Equal(one.Catalog.BuildHash, two.Catalog.BuildHash);
    }

    [Fact]
    public async Task ALoadedCatalogIsWhatARegistryReloadsTo() {
        // Doc 27 § Upgrades' live content reload, from this end: the catalog is replaced wholesale
        // rather than a definition at a time, which is the only swap that cannot leave two halves of
        // one build in force at once.
        var shipped = new Shipped()
            .Definition("items/sword", Item("A sword"), DefinitionContent.Label)
            .Build();

        var registry = new DefinitionRegistry();
        var load = await DefinitionContent.LoadAsync(shipped.Assets, TestContext.Current.CancellationToken);

        Assert.Equal(0, registry.Catalog.Count);

        registry.Reload(load.Catalog);

        Assert.Equal(1, registry.Catalog.Count);
        Assert.Equal(1, registry.Generation);
    }
}
