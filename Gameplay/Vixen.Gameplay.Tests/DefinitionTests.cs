// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using Vixen.Core;
using Vixen.Core.Serialization;
using Vixen.Net.Replication;
using Xunit;

namespace Vixen.Gameplay.Tests;

[DataContract("TestItemDefinition")]
public sealed record TestItemDefinition : Definition {
    public string DisplayName { get; set; } = string.Empty;

    public int ItemLevel { get; set; }

    public List<string> Tags { get; set; } = [];

    public override void CollectTags(ICollection<string> tags) {
        ArgumentNullException.ThrowIfNull(tags);

        foreach (var tag in Tags) {
            tags.Add(tag);
        }
    }
}

public class DefIdTests {
    [Theory]
    [InlineData("items/flamebrand")]
    [InlineData("quests/queensdale/wolves")]
    [InlineData("effects/burning")]
    public void DefIdIsTheSameHashAsANetworkPrefabId(string address) {
        // docs/plan/28 § Definitions: "the same construction doc 16 uses for prefab ids and
        // NetworkSceneId". Asserted rather than remarked, because the two are computed in different
        // assemblies and nothing else would notice them drifting apart.
        Assert.Equal(NetworkPrefabId.From(address).Value, DefId.From(address).Value);
    }

    [Fact]
    public void NoAddressIsNoDefinition() {
        Assert.Equal(DefId.None, DefId.From(null));
        Assert.Equal(DefId.None, DefId.From(string.Empty));
        Assert.False(DefId.None.IsSome);
    }

    [Fact]
    public void TheSameAddressIsTheSameIdInEveryProcess() {
        Assert.Equal(DefId.From("items/flamebrand"), DefId.From("items/flamebrand"));
        Assert.NotEqual(DefId.From("items/flamebrand"), DefId.From("items/flamebrand2"));
    }
}

public class DefinitionCatalogTests {
    static TestItemDefinition Sword(params string[] tags) => new() {
        DisplayName = "Flamebrand",
        ItemLevel = 80,
        Tags = [.. tags]
    };

    [Fact]
    public void AddingStampsTheAddressAndTheIdOntoACopy() {
        var authored = Sword();

        var catalog = new DefinitionCatalogBuilder().Add("items/flamebrand", authored).Build();

        Assert.Equal(string.Empty, authored.Address);
        Assert.True(catalog.TryGet<TestItemDefinition>(DefId.From("items/flamebrand"), out var stored));
        Assert.Equal("items/flamebrand", stored!.Address);
        Assert.Equal(DefId.From("items/flamebrand"), stored.Id);
        Assert.Equal(80, stored.ItemLevel);
    }

    [Fact]
    public void ADefinitionKeepsItsDerivedTypeThroughTheStamp() {
        var catalog = new DefinitionCatalogBuilder().Add("items/flamebrand", Sword()).Build();

        Assert.IsType<TestItemDefinition>(catalog.Find(DefId.From("items/flamebrand")));
        Assert.Equal("TestItemDefinition", catalog.Find(DefId.From("items/flamebrand"))!.TypeName);
    }

    [Fact]
    public void TheSameAddressTwiceIsRefused() {
        var builder = new DefinitionCatalogBuilder().Add("items/flamebrand", Sword());

        var error = Assert.Throws<InvalidOperationException>(() => builder.Add("items/flamebrand", Sword()));

        Assert.Contains("twice", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryTagADefinitionMentionsIsBaked() {
        var catalog = new DefinitionCatalogBuilder()
            .Add("items/flamebrand", Sword("Item.Weapon.Sword", "Item.Soulbound.OnEquip"))
            .AddTag("State.InCombat")
            .Build();

        Assert.True(catalog.Tags.TryResolve("Item.Weapon.Sword", out _));
        Assert.True(catalog.Tags.TryResolve("Item.Weapon", out _));
        Assert.True(catalog.Tags.TryResolve("State.InCombat", out _));
    }

    [Fact]
    public void TheBuildHashIsAPureFunctionOfTheAddressesAndTheTags() {
        var first = new DefinitionCatalogBuilder()
            .Add("items/a", Sword("Item.Weapon"))
            .Add("items/b", Sword("Item.Armour"))
            .Build();

        var second = new DefinitionCatalogBuilder()
            .Add("items/b", Sword("Item.Armour"))
            .Add("items/a", Sword("Item.Weapon"))
            .Build();

        Assert.Equal(first.BuildHash, second.BuildHash);

        // A balance change is not a handshake failure. Doc 28's walk turns on that being true.
        var retuned = new DefinitionCatalogBuilder()
            .Add("items/a", new TestItemDefinition { ItemLevel = 81, Tags = ["Item.Weapon"] })
            .Add("items/b", Sword("Item.Armour"))
            .Build();

        Assert.Equal(first.BuildHash, retuned.BuildHash);

        var extended = new DefinitionCatalogBuilder()
            .Add("items/a", Sword("Item.Weapon"))
            .Add("items/b", Sword("Item.Armour"))
            .Add("items/c", Sword("Item.Trinket"))
            .Build();

        Assert.NotEqual(first.BuildHash, extended.BuildHash);
    }

    [Fact]
    public void OfTypeFindsOnlyTheKindAskedFor() {
        var catalog = new DefinitionCatalogBuilder()
            .Add("items/a", Sword())
            .Add("effects/burning", new EffectDefinition { Duration = 6f })
            .Build();

        Assert.Single(catalog.OfType<TestItemDefinition>());
        Assert.Single(catalog.OfType<EffectDefinition>());
        Assert.Equal(2, catalog.OfType<Definition>().Count());
    }
}

public class DefinitionSerializationTests {
    [Fact]
    public void ADefinitionReadsBackAsItsOwnKindWithoutBeingToldWhichKind() {
        var authored = new EffectDefinition {
            DisplayName = "Burning",
            Duration = 6f,
            Period = 2f,
            Stacking = EffectStacking.StackTo,
            MaximumStacks = 3,
            Tags = ["Effect.Damage.Burning"],
            Modifiers = [new() { Attribute = "Power", Op = ModifierOp.AddPercent, Value = -0.1f }]
        };

        var read = Assert.IsType<EffectDefinition>(
            DefinitionSerialization.FromBytes(DefinitionSerialization.ToBytes(authored))
        );

        Assert.Equal(authored.DisplayName, read.DisplayName);
        Assert.Equal(authored.Duration, read.Duration);
        Assert.Equal(authored.Stacking, read.Stacking);
        Assert.Equal(authored.MaximumStacks, read.MaximumStacks);
        Assert.Equal("Effect.Damage.Burning", Assert.Single(read.Tags));
        Assert.Equal(ModifierOp.AddPercent, Assert.Single(read.Modifiers).Op);
    }

    [Fact]
    public void TheSameDefinitionWritesTheSameBytesEveryTime() {
        // docs/plan/28 § Testing: "a corpus of real definitions that must survive a catalog rebuild
        // byte-identically". A deterministic artefact is what makes the content build's cache hit.
        var authored = new EffectDefinition { DisplayName = "Might", Duration = 30f, Tags = ["Effect.Buff.Might"] };

        Assert.Equal(DefinitionSerialization.ToBytes(authored), DefinitionSerialization.ToBytes(authored));
    }

    [Fact]
    public void BytesWhoseTypeNameThisBuildDoesNotHaveAreRefusedRatherThanMisread() {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new SerializationWriter(buffer);

        writer.WriteString("SomeOtherGamesDefinition");
        writer.Flush();

        var error = Assert.Throws<SerializationException>(
            () => DefinitionSerialization.FromBytes(buffer.WrittenSpan)
        );

        Assert.Contains("nothing in this build claims", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BytesThatNameSomethingThatIsNotADefinitionAreRefused() {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new SerializationWriter(buffer);

        Assert.True(SerializerRegistry.TryGetByAlias("ModifierDefinition", out var serializer));

        writer.WriteString("ModifierDefinition");
        serializer.SerializeObject(ref writer, new ModifierDefinition());
        writer.Flush();

        var bytes = buffer.WrittenSpan.ToArray();

        var error = Assert.Throws<SerializationException>(() => DefinitionSerialization.FromBytes(bytes));

        Assert.Contains("not a Definition", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADefinitionTypeWithNoContractCannotBeWritten() {
        var error = Assert.Throws<SerializationException>(
            () => DefinitionSerialization.ToBytes(new UndescribedDefinition())
        );

        Assert.Contains("[DataContract]", error.Message, StringComparison.Ordinal);
    }
}

public class DefinitionRegistryTests {
    static DefinitionCatalog Catalog(params string[] addresses) {
        var builder = new DefinitionCatalogBuilder();

        foreach (var address in addresses) {
            builder.Add(address, new TestItemDefinition { Tags = ["Item.Weapon"] });
        }

        return builder.Build();
    }

    [Fact]
    public void GetRefusesToCarryOnWithoutADefinition() {
        var registry = new DefinitionRegistry(Catalog("items/a"));

        Assert.Equal("items/a", registry.Get<TestItemDefinition>(DefId.From("items/a")).Address);

        var missing = Assert.Throws<DefinitionNotFoundException>(
            () => registry.Get<TestItemDefinition>(DefId.From("items/b"))
        );

        Assert.Contains("different one", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AskingForTheWrongKindSaysWhichKindItIs() {
        var registry = new DefinitionRegistry(Catalog("items/a"));

        var error = Assert.Throws<DefinitionNotFoundException>(
            () => registry.Get<EffectDefinition>(DefId.From("items/a"))
        );

        Assert.Contains("TestItemDefinition", error.Message, StringComparison.Ordinal);
        Assert.Contains("items/a", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAdditiveReloadApplies() {
        var registry = new DefinitionRegistry(Catalog("items/a"));

        Assert.True(registry.TryReload(Catalog("items/a", "items/b"), out var reason));
        Assert.Equal(string.Empty, reason);
        Assert.Equal(1, registry.Generation);
        Assert.True(registry.Catalog.Contains(DefId.From("items/b")));
    }

    [Fact]
    public void ARemovedAddressIsRefused() {
        var registry = new DefinitionRegistry(Catalog("items/a", "items/b"));

        Assert.False(registry.TryReload(Catalog("items/a"), out var reason));
        Assert.Contains("never additive", reason, StringComparison.Ordinal);
        Assert.Equal(0, registry.Generation);
        Assert.True(registry.Catalog.Contains(DefId.From("items/b")));
    }

    [Fact]
    public void ANewTagIsRefusedBecauseItRenumbersEveryTagAlreadyInFlight() {
        var registry = new DefinitionRegistry(Catalog("items/a"));

        var withNewTag = new DefinitionCatalogBuilder()
            .Add("items/a", new TestItemDefinition { Tags = ["Item.Weapon"] })
            .AddTag("Item.Armour")
            .Build();

        Assert.False(registry.TryReload(withNewTag, out var reason));
        Assert.Contains("rolling build update", reason, StringComparison.Ordinal);

        var error = Assert.Throws<InvalidOperationException>(() => registry.Reload(withNewTag));
        Assert.Contains("tag table changed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AChangedValueAppliesLive() {
        var registry = new DefinitionRegistry(
            new DefinitionCatalogBuilder()
                .Add("items/a", new TestItemDefinition { ItemLevel = 80, Tags = ["Item.Weapon"] })
                .Build()
        );

        registry.Reload(
            new DefinitionCatalogBuilder()
                .Add("items/a", new TestItemDefinition { ItemLevel = 81, Tags = ["Item.Weapon"] })
                .Build()
        );

        Assert.Equal(81, registry.Get<TestItemDefinition>(DefId.From("items/a")).ItemLevel);
    }

    [Fact]
    public void TheFirstLoadIsNotARenumbering() {
        var registry = new DefinitionRegistry();

        Assert.True(registry.TryReload(Catalog("items/a"), out _));
        Assert.Equal(1, registry.Generation);
    }
}
