// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Gameplay.Items;
using Xunit;

namespace Vixen.Gameplay.Loot.Tests;

/// <summary>One boss table with a rare mount behind pity, a nested trash table, and a salvage recipe.</summary>
public static class Content {
    public const string Sword = "items/flamebrand";
    public const string Ore = "items/copper-ore";
    public const string Cloth = "items/linen";
    public const string Mount = "items/raid-drake";
    public const string Heroic = "items/heroic-token";

    public const string BossTable = "loot/boss";
    public const string TrashTable = "loot/trash";
    public const string WeaponSalvage = "salvage/weapons";

    public static DefinitionCatalog Catalog() =>
        new DefinitionCatalogBuilder()
            .Add("rarities/common", new ItemRarityDefinition { Order = 0, Affixes = 0, Tag = "Item.Rarity.Common" })
            .Add(Sword, Item("Flamebrand", 1, "Item.Weapon.Sword"))
            .Add(Ore, Item("Copper Ore", 100, "Item.Material.Ore"))
            .Add(Cloth, Item("Linen", 100, "Item.Material.Cloth"))
            .Add(Mount, Item("Raid Drake", 1, "Item.Mount"))
            .Add(Heroic, Item("Heroic Token", 50, "Item.Currency.Token"))
            .Add(
                TrashTable,
                new LootTableDefinition {
                    DisplayName = "Trash",
                    Rolls = 1,
                    Entries = [
                        new() { Item = Ore, Weight = 1f, Minimum = 1, Maximum = 5 },
                        new() { Item = Cloth, Weight = 1f, Minimum = 1, Maximum = 5 }
                    ]
                }
            )
            .Add(
                BossTable,
                new LootTableDefinition {
                    DisplayName = "The Boss",
                    Rolls = 1,
                    Pity = new() { AttemptsBefore = 10, RampPerAttempt = 0.05f, GuaranteedAt = 30 },
                    Entries = [
                        // Independent rows, in authored order: always a token, sometimes a mount.
                        new() { Item = Heroic, Chance = 1f, Minimum = 2, Maximum = 2 },
                        new() { Item = Mount, Chance = 0.01f, UsesPity = true },

                        // Heroic-only, so a normal kill renormalises over what is left.
                        new() {
                            Item = Sword,
                            Weight = 1f,
                            Conditions = [
                                new() {
                                    Kind = RequirementKind.Value,
                                    Subject = "Difficulty",
                                    Comparison = RequirementComparison.AtLeast,
                                    Value = 2f
                                }
                            ]
                        },
                        new() { Item = Ore, Weight = 3f, Minimum = 2, Maximum = 4 },
                        new() { Table = TrashTable, Weight = 1f }
                    ]
                }
            )
            .Add(
                WeaponSalvage,
                new SalvageDefinition { ItemTags = ["Item.Weapon"], Table = TrashTable }
            )
            .Build();

    public static ItemLibrary Items() => ItemLibrary.Compile(Catalog());

    public static LootLibrary Loot() => LootLibrary.Compile(Catalog());

    static ItemDefinition Item(string name, int stack, string tag) =>
        new() {
            DisplayName = name,
            Rarity = "rarities/common",
            MaximumStack = stack,
            Tags = [tag]
        };
}

public class LootLibraryTests {
    [Fact]
    public void ACleanCatalogCompilesWithNothingToReport() {
        Assert.Empty(Content.Loot().Problems);
        Assert.Equal(2, Content.Loot().Count);
    }

    [Fact]
    public void ARowThatIsBothWeightedAndIndependentIsRefused() {
        var catalog = new DefinitionCatalogBuilder()
            .Add(
                "loot/confused",
                new LootTableDefinition { Entries = [new() { Item = "items/a", Weight = 1f, Chance = 0.5f }] }
            )
            .Build();

        Assert.Contains(
            LootLibrary.Compile(catalog).Problems,
            problem => problem.Contains("both a weight and a chance", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void ARowThatNamesNeitherAnItemNorATableIsRefused() {
        var catalog = new DefinitionCatalogBuilder()
            .Add("loot/empty", new LootTableDefinition { Entries = [new() { Weight = 1f }] })
            .Build();

        Assert.Contains(
            LootLibrary.Compile(catalog).Problems,
            problem => problem.Contains("neither an item nor a table", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void ANestedTableThisBuildDoesNotHaveIsReported() {
        var catalog = new DefinitionCatalogBuilder()
            .Add("loot/outer", new LootTableDefinition { Entries = [new() { Table = "loot/nonexistent", Weight = 1f }] })
            .Build();

        Assert.Contains(
            LootLibrary.Compile(catalog).Problems,
            problem => problem.Contains("loot/nonexistent", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void SalvageMatchesByTagAndAnExactItemWins() {
        var loot = Content.Loot();
        var items = Content.Items();

        var sword = items.Get(DefId.From(Content.Sword));
        var ore = items.Get(DefId.From(Content.Ore));

        Assert.Equal(DefId.From(Content.TrashTable), loot.SalvageFor(sword)!.Id);
        Assert.Null(loot.SalvageFor(ore));
    }
}

public class LootRollTests {
    [Fact]
    public void ADropIsReproducibleFromItsEventId() {
        var loot = Content.Loot();
        var table = loot.Get(DefId.From(Content.BossTable));

        for (var eventId = 1ul; eventId < 200; eventId++) {
            var first = LootEvaluator.Roll(loot, table, eventId, 42);
            var second = LootEvaluator.Roll(loot, table, eventId, 42);

            Assert.Equal(first.Drops, second.Drops);
        }
    }

    [Fact]
    public void TwoPlayersRollingTheSameEventGetDifferentDrops() {
        var loot = Content.Loot();
        var table = loot.Get(DefId.From(Content.BossTable));

        var differences = 0;

        for (var eventId = 1ul; eventId < 200; eventId++) {
            var mine = LootEvaluator.Roll(loot, table, eventId, 1);
            var yours = LootEvaluator.Roll(loot, table, eventId, 2);

            if (!mine.Drops.SequenceEqual(yours.Drops)) {
                differences++;
            }
        }

        Assert.True(differences > 150, $"only {differences} of 199 events differed between two players");
    }

    [Fact]
    public void AGuaranteedRowAlwaysDropsAndItsCountIsHonoured() {
        var loot = Content.Loot();
        var table = loot.Get(DefId.From(Content.BossTable));
        var token = DefId.From(Content.Heroic);

        for (var eventId = 1ul; eventId < 300; eventId++) {
            var result = LootEvaluator.Roll(loot, table, eventId);
            var tokens = result.Drops.Where(drop => drop.Item == token).ToList();

            Assert.Single(tokens);
            Assert.Equal(2, tokens[0].Count);
        }
    }

    [Fact]
    public void WeightsAreHonouredOverALargeSample() {
        var loot = Content.Loot();
        var table = loot.Get(DefId.From(Content.BossTable));

        var ore = 0;
        var trash = 0;
        const int Events = 40000;

        for (var eventId = 1ul; eventId <= Events; eventId++) {
            foreach (var drop in LootEvaluator.Roll(loot, table, eventId).Drops) {
                if (drop.Table == DefId.From(Content.TrashTable)) {
                    trash++;
                } else if (drop.Item == DefId.From(Content.Ore)) {
                    ore++;
                }
            }
        }

        // On a normal kill the sword's row is absent, so the pick is ore 3 against trash 1 — the
        // remaining weights renormalise over what is left rather than the table sometimes rolling
        // nothing.
        Assert.Equal(Events, ore + trash);
        Assert.InRange(ore / (double)Events, 0.73, 0.77);
    }

    [Fact]
    public void ARowWhoseConditionsFailIsAbsentRatherThanSkipped() {
        var loot = Content.Loot();
        var table = loot.Get(DefId.From(Content.BossTable));
        var sword = DefId.From(Content.Sword);

        var normal = 0;
        var heroic = 0;
        const int Events = 8000;

        for (var eventId = 1ul; eventId <= Events; eventId++) {
            if (LootEvaluator.Roll(loot, table, eventId).Drops.Any(drop => drop.Item == sword)) {
                normal++;
            }

            var context = new LootContext().With("Difficulty", 2f);

            if (LootEvaluator.Roll(loot, table, eventId, 0, context).Drops.Any(drop => drop.Item == sword)) {
                heroic++;
            }
        }

        Assert.Equal(0, normal);

        // Ore 3, trash 1, sword 1 — one in five.
        Assert.InRange(heroic / (double)Events, 0.18, 0.22);
    }

    [Fact]
    public void ANestedTableDropsWhatItsOwnRowsSay() {
        var loot = Content.Loot();
        var table = loot.Get(DefId.From(Content.TrashTable));

        for (var eventId = 1ul; eventId < 500; eventId++) {
            var drop = Assert.Single(LootEvaluator.Roll(loot, table, eventId).Drops);

            Assert.True(drop.Item == DefId.From(Content.Ore) || drop.Item == DefId.From(Content.Cloth));
            Assert.InRange(drop.Count, 1, 5);
        }
    }

    [Fact]
    public void ACycleInTheTreeStopsRatherThanRecursingForEver() {
        var catalog = new DefinitionCatalogBuilder()
            .Add("loot/a", new LootTableDefinition { Rolls = 1, Entries = [new() { Table = "loot/b", Weight = 1f }] })
            .Add("loot/b", new LootTableDefinition { Rolls = 1, Entries = [new() { Table = "loot/a", Weight = 1f }] })
            .Build();

        var loot = LootLibrary.Compile(catalog);

        Assert.Empty(LootEvaluator.Roll(loot, loot.Get(DefId.From("loot/a")), 1).Drops);
    }

    [Fact]
    public void WhatDroppedTurnsIntoInstancesWithTheirOwnSeeds() {
        var loot = Content.Loot();
        var items = Content.Items();
        var result = LootEvaluator.Roll(loot, loot.Get(DefId.From(Content.BossTable)), 99);

        var instances = result.Materialise(items);

        Assert.Equal(result.Drops.Count, instances.Count);
        Assert.All(instances, instance => Assert.True(instance.IsSome));
    }
}

public class PityTests {
    static LootTable Table(out LootLibrary loot) {
        loot = Content.Loot();

        return loot.Get(DefId.From(Content.BossTable));
    }

    [Fact]
    public void TheChanceIsFlatUntilTheRampAndCertainAtTheGuarantee() {
        var policy = new PityPolicyDefinition { AttemptsBefore = 10, RampPerAttempt = 0.05f, GuaranteedAt = 30 };

        Assert.Equal(0.01f, LootEvaluator.PityChance(0.01f, policy, 0), 5);
        Assert.Equal(0.01f, LootEvaluator.PityChance(0.01f, policy, 10), 5);
        Assert.Equal(0.06f, LootEvaluator.PityChance(0.01f, policy, 11), 5);
        Assert.Equal(0.51f, LootEvaluator.PityChance(0.01f, policy, 20), 5);
        Assert.Equal(1f, LootEvaluator.PityChance(0.01f, policy, 30), 5);
        Assert.Equal(1f, LootEvaluator.PityChance(0.01f, policy, 100), 5);
    }

    [Fact]
    public void PityReachesItsGuaranteeExactly() {
        var table = Table(out var loot);
        var pity = new MemoryPityStore();
        var mount = DefId.From(Content.Mount);
        var key = new PityKey(7, table.Id);

        // Wind the counter to one short of the guarantee without rolling, so the assertion is about
        // the guarantee rather than about luck.
        for (var attempt = 0; attempt < 30; attempt++) {
            pity.Record(key, false);
        }

        Assert.Equal(30, pity.AttemptsOf(key));

        var result = LootEvaluator.Roll(loot, table, 1234, 7, null, pity);

        Assert.Contains(result.Drops, drop => drop.Item == mount);
        Assert.Contains(result.Drops, drop => drop is { Pity: true });
        Assert.Equal(0, pity.AttemptsOf(key));
    }

    [Fact]
    public void ARunOfBadLuckIsRememberedAndADropForgetsIt() {
        var table = Table(out var loot);
        var pity = new MemoryPityStore();
        var mount = DefId.From(Content.Mount);
        var key = new PityKey(3, table.Id);

        var drops = 0;
        var longest = 0;

        for (var eventId = 1ul; eventId <= 400; eventId++) {
            var before = pity.AttemptsOf(key);
            longest = Math.Max(longest, before);

            if (LootEvaluator.Roll(loot, table, eventId, 3, null, pity).Drops.Any(drop => drop.Item == mount)) {
                drops++;
                Assert.Equal(0, pity.AttemptsOf(key));
            } else {
                Assert.Equal(before + 1, pity.AttemptsOf(key));
            }
        }

        Assert.True(drops > 0, "the mount never dropped in four hundred kills");

        // The guarantee is 30, so no run may exceed it — which is the whole promise of the policy.
        Assert.True(longest <= 30, $"a run of {longest} exceeded the guarantee of 30");
    }

    [Fact]
    public void ATableWithNoPityRowsTouchesNoCounter() {
        var loot = Content.Loot();
        var pity = new MemoryPityStore();
        var trash = loot.Get(DefId.From(Content.TrashTable));

        LootEvaluator.Roll(loot, trash, 1, 5, null, pity);

        Assert.Equal(0, pity.AttemptsOf(new(5, trash.Id)));
    }

    [Fact]
    public void PityIsPerPlayerAndPerTable() {
        var table = Table(out var loot);
        var pity = new MemoryPityStore();

        for (var eventId = 1ul; eventId <= 5; eventId++) {
            LootEvaluator.Roll(loot, table, eventId, 1, null, pity);
        }

        Assert.True(pity.AttemptsOf(new(1, table.Id)) > 0);
        Assert.Equal(0, pity.AttemptsOf(new(2, table.Id)));
        Assert.Equal(0, pity.AttemptsOf(new(1, DefId.From(Content.TrashTable))));
    }
}

public class LootDistributionTests {
    [Fact]
    public void APersonalDropRollsOncePerParticipant() {
        var loot = Content.Loot();
        var table = loot.Get(DefId.From(Content.BossTable));

        var results = LootEvaluator.Roll(loot, table, 500, LootDistribution.Personal, [1, 2, 3]);

        Assert.Equal(3, results.Count);
        Assert.Equal([1ul, 2ul, 3ul], results.Select(result => result.Player));
    }

    [Theory]
    [InlineData(LootDistribution.Group)]
    [InlineData(LootDistribution.NeedGreed)]
    [InlineData(LootDistribution.MasterLooter)]
    public void EveryOtherDistributionRollsOnceIntoOneWindow(LootDistribution distribution) {
        var loot = Content.Loot();
        var table = loot.Get(DefId.From(Content.BossTable));

        var results = LootEvaluator.Roll(loot, table, 500, distribution, [1, 2, 3]);

        Assert.Single(results);
        Assert.Equal(0ul, results[0].Player);

        // The same evaluator and the same event id, so all three windows hold the same thing — they
        // differ only in who may take it out, which is a flow rather than a roll.
        Assert.Equal(LootEvaluator.Roll(loot, table, 500).Drops, results[0].Drops);
    }
}

public class LootModuleTests {
    [Fact]
    public void LootNeedsItemsAndTheKernel() {
        Assert.Throws<InvalidOperationException>(() => new GameplayConfig().Use<LootModule>().Build());

        var composition = new GameplayConfig()
            .Use<GameplayKernelModule>()
            .Use<ItemsModule>()
            .Use<LootModule>()
            .Build();

        Assert.Contains(composition.Definitions, entry => entry.Tag == "LootTableDefinition");
        Assert.Contains(composition.Definitions, entry => entry.Tag == "SalvageDefinition");
    }
}
