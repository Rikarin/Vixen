// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Xunit;

namespace Vixen.Gameplay.Items.Tests;

/// <summary>A small but complete catalog: two rarities, four affixes, three items.</summary>
public static class Content {
    public const string Sword = "items/flamebrand";
    public const string Ore = "items/copper-ore";
    public const string Trinket = "items/plain-ring";

    public static DefinitionCatalog Catalog() =>
        new DefinitionCatalogBuilder()
            .Add(
                "rarities/common",
                new ItemRarityDefinition { DisplayName = "Common", Order = 0, Affixes = 0, Tag = "Item.Rarity.Common" }
            )
            .Add(
                "rarities/legendary",
                new ItemRarityDefinition {
                    DisplayName = "Legendary",
                    Order = 4,
                    Affixes = 2,
                    Tag = "Item.Rarity.Legendary"
                }
            )
            .Add(
                "affixes/of-power",
                new AffixDefinition {
                    DisplayName = "of Power",
                    Weight = 3f,
                    Stats = [new() { Attribute = "Power", Op = ModifierOp.Add, Value = 10f, Maximum = 30f }],
                    Tags = ["Affix.Suffix.OfPower"]
                }
            )
            .Add(
                "affixes/of-precision",
                new AffixDefinition {
                    DisplayName = "of Precision",
                    Weight = 3f,
                    Stats = [new() { Attribute = "Precision", Op = ModifierOp.Add, Value = 5f, Maximum = 25f }],
                    Tags = ["Affix.Suffix.OfPrecision"]
                }
            )
            .Add(
                "affixes/of-the-bear",
                new AffixDefinition {
                    DisplayName = "of the Bear",
                    Weight = 1f,
                    Stats = [
                        new() { Attribute = "Health", Op = ModifierOp.Add, Value = 100f, Maximum = 400f },
                        new() { Attribute = "Armour", Op = ModifierOp.Add, Value = 5f, Maximum = 10f }
                    ],
                    Tags = ["Affix.Suffix.OfTheBear"]
                }
            )
            .Add(
                "affixes/keen",
                new AffixDefinition {
                    DisplayName = "Keen",
                    Weight = 2f,
                    MinimumItemLevel = 50,
                    Stats = [new() { Attribute = "CritChance", Op = ModifierOp.AddPercent, Value = 0.02f, Maximum = 0.08f }],
                    Tags = ["Affix.Prefix.Keen"],
                    RequiredItemTags = ["Item.Weapon"]
                }
            )
            .Add(
                "affixes/pools/weapon",
                new AffixPoolDefinition {
                    Affixes = ["affixes/of-power", "affixes/of-precision", "affixes/of-the-bear", "affixes/keen"]
                }
            )
            .Add(
                Sword,
                new ItemDefinition {
                    DisplayName = "Flamebrand",
                    Rarity = "rarities/legendary",
                    Slot = "Item.Slot.MainHand",
                    ItemLevel = 80,
                    MaximumDurability = 100,
                    Sockets = 2,
                    Binding = ItemBinding.OnEquip,
                    Tags = ["Item.Weapon.Sword", "Item.Source.Raid"],
                    Stats = [
                        new() { Attribute = "Power", Op = ModifierOp.Add, Value = 251f },
                        new() { Attribute = "Precision", Op = ModifierOp.Add, Value = 179f }
                    ],
                    AffixPools = ["affixes/pools/weapon"],
                    Icon = "icons/flamebrand",
                    Prefab = "prefabs/weapons/flamebrand"
                }
            )
            .Add(
                Ore,
                new ItemDefinition {
                    DisplayName = "Copper Ore",
                    Rarity = "rarities/common",
                    ItemLevel = 1,
                    MaximumStack = 250,
                    Tags = ["Item.Material.Ore"]
                }
            )
            .Add(
                Trinket,
                new ItemDefinition {
                    DisplayName = "Plain Ring",
                    Rarity = "rarities/legendary",
                    Slot = "Item.Slot.Ring",
                    ItemLevel = 10,
                    Tags = ["Item.Trinket.Ring"],
                    AffixPools = ["affixes/pools/weapon"]
                }
            )
            .Build();

    public static ItemLibrary Library() => ItemLibrary.Compile(Catalog());
}

public class ItemInstanceTests {
    [Fact]
    public void AnInstanceIsSixteenBytes() {
        // docs/plan/28 § Items: "a bank of ten thousand items is a real number". This is the number
        // that claim rests on, so it is asserted rather than intended — a field added here is a field
        // added ten thousand times, and nothing else in the tree would notice.
        Assert.Equal(16, Unsafe.SizeOf<ItemInstance>());
    }

    [Fact]
    public void AnEmptyInstanceIsNothingAtAll() {
        Assert.False(ItemInstance.Empty.IsSome);
        Assert.False(ItemInstance.Of(DefId.None).IsSome);
        Assert.False(ItemInstance.Of(DefId.From(Content.Ore), 0).IsSome);
    }

    [Fact]
    public void AStackOfZeroIsEmptyRatherThanAGhost() {
        var ore = ItemInstance.Of(DefId.From(Content.Ore), 20);

        Assert.True(ore.IsSome);
        Assert.Equal(ItemInstance.Empty, ore.WithStack(0));
        Assert.Equal(ItemInstance.Empty, ore.WithStack(-5));
    }

    [Fact]
    public void BindingIsOneWayAndUnboundItemsStayUnbound() {
        var sword = Content.Library().Get(DefId.From(Content.Sword)).Create();

        Assert.Equal(ItemBinding.OnEquip, sword.Binding);
        Assert.True(sword.IsTradeable);

        var bound = sword.Bind();

        Assert.Equal(ItemBinding.Bound, bound.Binding);
        Assert.False(bound.IsTradeable);
        Assert.Equal(bound, bound.Bind());

        // An item that never binds is not bound by being handled.
        var ore = Content.Library().Get(DefId.From(Content.Ore)).Create(10);

        Assert.Equal(ItemBinding.None, ore.Bind().Binding);
        Assert.True(ore.Bind().IsTradeable);
    }

    [Fact]
    public void CreateHonoursTheMaximumStackAndTheDurability() {
        var library = Content.Library();

        var ore = library.Get(DefId.From(Content.Ore)).Create(1000);
        var sword = library.Get(DefId.From(Content.Sword)).Create();

        Assert.Equal(250, ore.Stack);
        Assert.Equal(0, ore.Durability);
        Assert.Equal(1, sword.Stack);
        Assert.Equal(100, sword.Durability);
    }

    [Fact]
    public void AnItemWithNoAffixesToRollKeepsNoSeed() {
        var library = Content.Library();

        Assert.Equal(0u, library.Get(DefId.From(Content.Ore)).Create(1, 12345).Seed);
        Assert.Equal(12345u, library.Get(DefId.From(Content.Sword)).Create(1, 12345).Seed);
    }
}

public class ItemLibraryTests {
    [Fact]
    public void ACleanCatalogCompilesWithNothingToReport() {
        Assert.Empty(Content.Library().Problems);
        Assert.Equal(3, Content.Library().Count);
    }

    [Fact]
    public void EverythingResolvesToWhatTheAddressNamed() {
        var library = Content.Library();
        var sword = library.Get(DefId.From(Content.Sword));

        Assert.Equal("Flamebrand", sword.Definition.DisplayName);
        Assert.Equal(80, sword.ItemLevel);
        Assert.Equal(2, sword.Sockets);
        Assert.NotNull(sword.Rarity);
        Assert.Equal(4, sword.Rarity!.Order);
        Assert.Equal(2, sword.AffixCount);
        Assert.True(sword.Slot.IsSome);
        Assert.False(sword.IsStackable);
        Assert.True(library.Get(DefId.From(Content.Ore)).IsStackable);
    }

    [Fact]
    public void TheAffixPoolIsSortedByAddressRatherThanByHowTheListWasWritten() {
        // The pool's order is part of what a seed means, so it must not be a designer's list order.
        var sword = Content.Library().Get(DefId.From(Content.Sword));

        var addresses = new List<string>();

        foreach (var affix in sword.AffixPool) {
            addresses.Add(affix.Definition.Address);
        }

        Assert.Equal(
            ["affixes/keen", "affixes/of-power", "affixes/of-precision", "affixes/of-the-bear"],
            addresses
        );
    }

    [Fact]
    public void AnAddressThatNamesNothingIsReportedRatherThanThrown() {
        var catalog = new DefinitionCatalogBuilder()
            .Add(
                "items/broken",
                new ItemDefinition {
                    Rarity = "rarities/nonexistent",
                    AffixPools = ["affixes/pools/nonexistent"],
                    Tags = ["Item.Weapon"]
                }
            )
            .Build();

        var library = ItemLibrary.Compile(catalog);
        var item = library.Get(DefId.From("items/broken"));

        Assert.Equal(2, library.Problems.Count);
        Assert.Null(item.Rarity);
        Assert.Equal(0, item.AffixCount);
        Assert.Empty(item.AffixPool.ToArray());
    }

    [Fact]
    public void AnItemThisBuildDoesNotHaveIsNamedRatherThanNull() {
        var error = Assert.Throws<DefinitionNotFoundException>(
            () => Content.Library().Get(DefId.From("items/nonexistent"))
        );

        Assert.Contains("different one", error.Message, StringComparison.Ordinal);
        Assert.Null(Content.Library().Find("items/nonexistent"));
    }

    [Fact]
    public void AnItemsRarityTagCountsAsOneOfItsTags() {
        var library = Content.Library();
        var tags = Content.Catalog().Tags;
        var sword = library.Get(DefId.From(Content.Sword));

        Assert.True(sword.HasTagUnder(tags.RangeOf("Item.Weapon")));
        Assert.True(sword.HasTagUnder(tags.RangeOf("Item.Slot.MainHand")));
        Assert.True(sword.HasTagUnder(tags.RangeOf("Item.Rarity.Legendary")));
        Assert.False(sword.HasTagUnder(tags.RangeOf("Item.Material")));
    }
}

public class ItemAffixTests {
    [Fact]
    public void TheSameSeedRollsTheSameAffixes() {
        var library = Content.Library();
        var sword = library.Get(DefId.From(Content.Sword));

        for (var seed = 1u; seed < 200; seed++) {
            Assert.Equal(ItemAffixes.Roll(sword, seed), ItemAffixes.Roll(sword, seed));
        }
    }

    [Fact]
    public void ARollProducesExactlyWhatTheRarityBuys() {
        var library = Content.Library();
        var sword = library.Get(DefId.From(Content.Sword));
        var ore = library.Get(DefId.From(Content.Ore));

        for (var seed = 1u; seed < 100; seed++) {
            Assert.Equal(2, ItemAffixes.Roll(sword, seed).Length);
            Assert.Empty(ItemAffixes.Roll(ore, seed));
        }
    }

    [Fact]
    public void NoAffixIsRolledTwiceOnOneItem() {
        var library = Content.Library();
        var sword = library.Get(DefId.From(Content.Sword));

        for (var seed = 1u; seed < 500; seed++) {
            var rolled = ItemAffixes.Roll(sword, seed);

            Assert.Equal(rolled.Length, rolled.Select(affix => affix.Affix).Distinct().Count());
        }
    }

    [Fact]
    public void AnUnrolledInstanceHasNoAffixesHoweverGoodItsRarityIs() {
        var library = Content.Library();
        var sword = library.Get(DefId.From(Content.Sword));

        Assert.Empty(ItemAffixes.Roll(sword, 0));
    }

    [Fact]
    public void AnAffixTheItemDoesNotQualifyForNeverRolls() {
        var library = Content.Library();
        var keen = DefId.From("affixes/keen");

        // Keen wants Item.Weapon and item level 50. The ring is neither, and shares the pool.
        var ring = library.Get(DefId.From(Content.Trinket));

        for (var seed = 1u; seed < 500; seed++) {
            Assert.DoesNotContain(ItemAffixes.Roll(ring, seed), affix => affix.Affix == keen);
        }

        // And it does roll on the thing it is for, so the filter is not simply excluding everything.
        var sword = library.Get(DefId.From(Content.Sword));
        var seen = false;

        for (var seed = 1u; seed < 500 && !seen; seed++) {
            seen = ItemAffixes.Roll(sword, seed).Any(affix => affix.Affix == keen);
        }

        Assert.True(seen);
    }

    [Fact]
    public void WeightsAreHonouredOverALargeSample() {
        var library = Content.Library();
        var sword = library.Get(DefId.From(Content.Sword));
        var counts = new Dictionary<DefId, int>();

        for (var seed = 1u; seed <= 20000; seed++) {
            foreach (var affix in ItemAffixes.Roll(sword, seed)) {
                counts[affix.Affix] = counts.GetValueOrDefault(affix.Affix) + 1;
            }
        }

        // Weights 3, 3, 2, 1 over two draws without replacement: the ordering is what is stable, and
        // the rare one has to be genuinely rarer than the common ones rather than merely present.
        Assert.True(counts[DefId.From("affixes/of-power")] > counts[DefId.From("affixes/keen")]);
        Assert.True(counts[DefId.From("affixes/of-precision")] > counts[DefId.From("affixes/keen")]);
        Assert.True(counts[DefId.From("affixes/keen")] > counts[DefId.From("affixes/of-the-bear")]);
        Assert.Equal(40000, counts.Values.Sum());
    }

    [Fact]
    public void ARollLandsInsideTheRangeItWasAuthoredWith() {
        var library = Content.Library();
        var sword = library.Get(DefId.From(Content.Sword));

        for (var seed = 1u; seed < 1000; seed++) {
            foreach (var rolled in ItemAffixes.Roll(sword, seed)) {
                Assert.InRange(rolled.Roll, 0f, 1f);

                foreach (var stat in library.FindAffix(rolled.Affix)!.Stats) {
                    Assert.InRange(stat.At(rolled.Roll), stat.Minimum, stat.Maximum);
                }
            }
        }
    }

    [Fact]
    public void OneRollDrivesEveryStatOfAnAffix() {
        // docs: an affix with two stats rolls high on both or low on both, because the roll is per
        // affix and not per stat — which is what stops a designer's "of the Bear" being two
        // independent gambles wearing one name.
        var library = Content.Library();
        var bear = library.FindAffix(DefId.From("affixes/of-the-bear"))!;

        var health = bear.Stats[0];
        var armour = bear.Stats[1];

        Assert.Equal(health.Minimum, health.At(0f));
        Assert.Equal(armour.Minimum, armour.At(0f));
        Assert.Equal(health.Maximum, health.At(1f));
        Assert.Equal(armour.Maximum, armour.At(1f));
        Assert.Equal(250f, health.At(0.5f), 3);
        Assert.Equal(7.5f, armour.At(0.5f), 3);
    }
}

public class ItemStatsTests {
    [Fact]
    public void AnEquippedItemGrantsItsBaseStatsAndItsAffixes() {
        var library = Content.Library();
        var sword = library.Get(DefId.From(Content.Sword)).Create(1, 4242);
        var source = ModifierSource.From(DefId.From("slots/main-hand"), 1);

        var modifiers = new List<Modifier>();
        var produced = ItemStats.Compute(library, sword, source, modifiers);

        Assert.Equal(produced, modifiers.Count);
        Assert.True(produced >= 2);
        Assert.All(modifiers, modifier => Assert.Equal(source, modifier.Source));

        var power = modifiers.First(modifier => modifier.Attribute == AttributeId.From("Power"));

        Assert.Equal(251f, power.Value);
    }

    [Fact]
    public void TheStatBlockIsAPureFunctionOfTheInstance() {
        var library = Content.Library();
        var source = ModifierSource.From(DefId.From("slots/main-hand"), 1);
        var sword = library.Get(DefId.From(Content.Sword)).Create(1, 999);

        var first = new List<Modifier>();
        var second = new List<Modifier>();

        ItemStats.Compute(library, sword, source, first);
        ItemStats.Compute(library, sword, source, second);

        Assert.Equal(first, second);
    }

    [Fact]
    public void AnEquippedItemIsAnOrdinaryModifierSource() {
        // The point of producing Modifiers rather than a bespoke block: the kernel already removes
        // these exactly, so unequipping is arithmetic rather than a subtraction.
        var library = Content.Library();
        var layout = new AttributeLayoutBuilder().Add("Power", 100f).Add("Precision").Build();
        var attributes = new AttributeSet(layout);
        var slot = ModifierSource.From(DefId.From("slots/main-hand"), 1);

        var before = attributes.ValueOf(AttributeId.From("Power"));

        var modifiers = new List<Modifier>();
        ItemStats.Compute(library, library.Get(DefId.From(Content.Sword)).Create(1, 7), slot, modifiers);

        foreach (var modifier in modifiers) {
            attributes.Add(modifier);
        }

        // 100 base plus the sword's 251, plus whatever "of Power" rolled if it rolled at all.
        Assert.InRange(attributes.ValueOf(AttributeId.From("Power")), 351f, 381f);

        attributes.RemoveBySource(slot);

        Assert.Equal(before, attributes.ValueOf(AttributeId.From("Power")));
    }

    [Fact]
    public void AnEmptySlotGrantsNothing() {
        var modifiers = new List<Modifier>();

        Assert.Equal(0, ItemStats.Compute(Content.Library(), ItemInstance.Empty, ModifierSource.None, modifiers));
        Assert.Empty(modifiers);
    }

    [Fact]
    public void BrokenIsZeroDurabilityAndIndestructibleIsZeroMaximum() {
        var library = Content.Library();
        var sword = library.Get(DefId.From(Content.Sword)).Create();
        var ore = library.Get(DefId.From(Content.Ore)).Create(10);

        Assert.True(ItemStats.IsFunctional(library, sword));
        Assert.False(ItemStats.IsFunctional(library, sword with { Durability = 0 }));

        // Zero maximum durability, so its zero durability means indestructible rather than broken.
        Assert.True(ItemStats.IsFunctional(library, ore));
    }
}

public class ItemsModuleTests {
    [Fact]
    public void TheModuleBringsFourDefinitionTypesAndNoSystems() {
        var composition = new GameplayConfig()
            .Use<GameplayKernelModule>()
            .Use<ItemsModule>()
            .Build();

        Assert.Equal(5, composition.Definitions.Count);
        Assert.Empty(composition.Systems);
        Assert.Contains(composition.Definitions, entry => entry.Tag == "ItemDefinition");
        Assert.Contains(composition.Definitions, entry => entry.Tag == "AffixPoolDefinition");
    }

    [Fact]
    public void ItemsWillNotComposeWithoutTheKernel() {
        var config = new GameplayConfig().Use<ItemsModule>();

        Assert.Throws<InvalidOperationException>(config.Build);
    }
}
