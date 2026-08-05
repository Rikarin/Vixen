// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Gameplay.Loot;
using Vixen.Gameplay;
using Vixen.Gameplay.Items;
using Vixen.Gameplay.Loot;
using Xunit;

namespace Tests.Gameplay.Loot;

public static class Content {
    public const string Ore = "items/copper-ore";
    public const string Cloth = "items/linen";
    public const string Mount = "items/raid-drake";

    public const string Boss = "loot/boss";
    public const string Trash = "loot/trash";

    public static DefinitionCatalog Catalog() =>
        new DefinitionCatalogBuilder()
            .Add("rarities/common", new ItemRarityDefinition { Order = 0, Tag = "Item.Rarity.Common" })
            .Add(Ore, Item("Copper Ore", 100))
            .Add(Cloth, Item("Linen", 100))
            .Add(Mount, Item("Raid Drake", 1))
            .Add(
                Trash,
                new LootTableDefinition {
                    DisplayName = "Trash",
                    Rolls = 1,
                    Entries = [
                        new() { Item = Ore, Weight = 3f, Minimum = 1, Maximum = 4 },
                        new() { Item = Cloth, Weight = 1f }
                    ]
                }
            )
            .Add(
                Boss,
                new LootTableDefinition {
                    DisplayName = "The Boss",
                    Rolls = 1,
                    Pity = new() { AttemptsBefore = 5, RampPerAttempt = 0.1f, GuaranteedAt = 15 },
                    Entries = [
                        new() { Item = Mount, Chance = 0.02f, UsesPity = true },
                        new() { Item = Ore, Weight = 1f, Minimum = 2, Maximum = 2 },
                        new() { Table = Trash, Weight = 1f }
                    ]
                }
            )
            .Build();

    public static ItemLibrary Items() => ItemLibrary.Compile(Catalog());

    public static LootLibrary Loot() => LootLibrary.Compile(Catalog());

    static ItemDefinition Item(string name, int stack) =>
        new() { DisplayName = name, Rarity = "rarities/common", MaximumStack = stack, Tags = ["Item.Material"] };
}

public class LootSimulatorTests {
    [Fact]
    public void TheSimulationAgreesWithTheEvaluatorEventForEvent() {
        // The point of the whole library: the preview runs the shipped evaluator, so the two cannot
        // report different odds. Asserted by rolling both and comparing counts exactly.
        var loot = Content.Loot();
        var table = loot.Get(DefId.From(Content.Boss));

        var simulation = LootSimulator.Run(loot, table, Content.Items(), events: 2000, firstEventId: 1, player: 1);

        var ore = 0L;

        // ⚠ With a pity store of its own, and it has to be. A pity row that drops consumes an extra
        // draw from the stream, so the same events with and without a store are genuinely different
        // rolls — which is also why the simulator starts every run from a fresh one.
        var pity = new MemoryPityStore();

        for (var eventId = 1ul; eventId <= 2000; eventId++) {
            foreach (var drop in LootEvaluator.Roll(loot, table, eventId, 1, null, pity).Drops) {
                if (drop.Item == DefId.From(Content.Ore)) {
                    ore += drop.Count;
                }
            }
        }

        Assert.Equal(ore, simulation.Items.Single(item => item.Item == DefId.From(Content.Ore)).Total);
    }

    [Fact]
    public void ARunIsReproducibleFromItsFirstEventId() {
        var loot = Content.Loot();
        var table = loot.Get(DefId.From(Content.Boss));

        var first = LootSimulator.Run(loot, table, events: 500, firstEventId: 77);
        var second = LootSimulator.Run(loot, table, events: 500, firstEventId: 77);

        Assert.Equal(first.Items, second.Items);
        Assert.Equal(first.Pity, second.Pity);

        var elsewhere = LootSimulator.Run(loot, table, events: 500, firstEventId: 78);

        Assert.NotEqual(first.Items, elsewhere.Items);
    }

    [Fact]
    public void ItemsAreNamedAndSortedMostFrequentFirst() {
        var loot = Content.Loot();
        var simulation = LootSimulator.Run(loot, loot.Get(DefId.From(Content.Boss)), Content.Items(), events: 4000);

        Assert.Contains(simulation.Items, item => item.Name == "Copper Ore");
        Assert.Contains(simulation.Items, item => item.Name == "Raid Drake");

        for (var index = 1; index < simulation.Items.Count; index++) {
            Assert.True(simulation.Items[index - 1].Events >= simulation.Items[index].Events);
        }
    }

    [Fact]
    public void StackRangesAreReportedAsTheyWereAuthored() {
        var loot = Content.Loot();
        var simulation = LootSimulator.Run(loot, loot.Get(DefId.From(Content.Boss)), Content.Items(), events: 4000);

        // Ore drops as exactly two from the boss and one to four from the trash table beneath it.
        var ore = simulation.Items.Single(item => item.Item == DefId.From(Content.Ore));

        Assert.Equal(1, ore.Smallest);
        Assert.Equal(4, ore.Largest);
    }

    [Fact]
    public void ATableThatDropsNothingOnAnOrdinaryKillSaysSo() {
        // The number a designer least expects: every weighted row conditional, so an unconditional
        // kill produces an empty window and the authored file looks fine.
        var catalog = new DefinitionCatalogBuilder()
            .Add(
                "loot/heroic-only",
                new LootTableDefinition {
                    Rolls = 1,
                    Entries = [
                        new() {
                            Item = Content.Ore,
                            Weight = 1f,
                            Conditions = [
                                new() {
                                    Kind = RequirementKind.Value,
                                    Subject = "Difficulty",
                                    Comparison = RequirementComparison.AtLeast,
                                    Value = 2f
                                }
                            ]
                        }
                    ]
                }
            )
            .Build();

        var loot = LootLibrary.Compile(catalog);
        var table = loot.Get(DefId.From("loot/heroic-only"));

        Assert.Equal(1000, LootSimulator.Run(loot, table, events: 1000).EmptyEvents);
        Assert.Equal(
            0,
            LootSimulator.Run(loot, table, events: 1000, context: new LootContext().With("Difficulty", 2f)).EmptyEvents
        );
    }

    [Fact]
    public void PityIsReportedAndItsGuaranteeIsCheckedRatherThanAssumed() {
        var loot = Content.Loot();
        var simulation = LootSimulator.Run(loot, loot.Get(DefId.From(Content.Boss)), events: 5000);

        var pity = Assert.NotNull(simulation.Pity);

        Assert.True(pity.Hits > 0);
        Assert.True(pity.Misses > 0);
        Assert.Equal(15, pity.Guarantee);
        Assert.True(pity.LongestDrought <= 15, $"a drought of {pity.LongestDrought} exceeded the guarantee");
        Assert.True(pity.GuaranteeHeld);
        Assert.InRange(pity.MeanAttempts, 0, 15);
    }

    [Fact]
    public void ATableWithNoPityReportsNone() {
        var loot = Content.Loot();

        Assert.Null(LootSimulator.Run(loot, loot.Get(DefId.From(Content.Trash)), events: 100).Pity);
    }

    [Fact]
    public void ARunOfNoEventsIsEmptyRatherThanAnError() {
        var loot = Content.Loot();
        var simulation = LootSimulator.Run(loot, loot.Get(DefId.From(Content.Boss)), events: 0);

        Assert.Equal(0, simulation.Events);
        Assert.Empty(simulation.Items);
    }
}

public class LootOutlineTests {
    [Fact]
    public void TheTreeIsFlattenedDepthFirstInAuthoredOrder() {
        var loot = Content.Loot();
        var rows = LootOutline.Of(loot, loot.Get(DefId.From(Content.Boss)), Content.Items());

        Assert.Equal(
            [
                (0, "The Boss"),
                (1, "Raid Drake"),
                (1, "Copper Ore"),
                (1, "Trash"),
                (1, "Trash"),
                (2, "Copper Ore"),
                (2, "Linen")
            ],
            rows.Select(row => (row.Depth, row.Label))
        );
    }

    [Fact]
    public void SharesAreTheAuthoredWeightsNormalised() {
        var loot = Content.Loot();
        var rows = LootOutline.Of(loot, loot.Get(DefId.From(Content.Trash)), Content.Items());

        var ore = rows.Single(row => row is { Row: 0, Depth: 1 });
        var cloth = rows.Single(row => row is { Row: 1, Depth: 1 });

        Assert.Equal(0.75f, ore.Share, 4);
        Assert.Equal(0.25f, cloth.Share, 4);
    }

    [Fact]
    public void AnIndependentRowReportsItsChanceRatherThanAShare() {
        var loot = Content.Loot();
        var rows = LootOutline.Of(loot, loot.Get(DefId.From(Content.Boss)), Content.Items());

        var mount = rows.Single(row => row.Label == "Raid Drake");

        Assert.Equal(0f, mount.Share);
        Assert.Equal(0.02f, mount.Chance, 4);
    }

    [Fact]
    public void AConditionalRowIsFlaggedBecauseItsShareIsTrueOfNoActualKill() {
        var catalog = new DefinitionCatalogBuilder()
            .Add(
                "loot/mixed",
                new LootTableDefinition {
                    Rolls = 1,
                    Entries = [
                        new() { Item = Content.Ore, Weight = 1f },
                        new() {
                            Item = Content.Cloth,
                            Weight = 1f,
                            Conditions = [new() { Kind = RequirementKind.HasTag, Subject = "Zone.Queensdale" }]
                        }
                    ]
                }
            )
            .Build();

        var loot = LootLibrary.Compile(catalog);
        var rows = LootOutline.Of(loot, loot.Get(DefId.From("loot/mixed")));

        Assert.False(rows.Single(row => row.Row == 0).Conditional);
        Assert.True(rows.Single(row => row.Row == 1).Conditional);
    }

    [Fact]
    public void ACycleIsWalkedOnceRatherThanForEver() {
        var catalog = new DefinitionCatalogBuilder()
            .Add("loot/a", new LootTableDefinition { Rolls = 1, Entries = [new() { Table = "loot/b", Weight = 1f }] })
            .Add("loot/b", new LootTableDefinition { Rolls = 1, Entries = [new() { Table = "loot/a", Weight = 1f }] })
            .Build();

        var loot = LootLibrary.Compile(catalog);
        var rows = LootOutline.Of(loot, loot.Get(DefId.From("loot/a")));

        Assert.True(rows.Count < 10, $"the outline produced {rows.Count} rows for a two-table cycle");
    }
}

public class LootTableModelTests {
    static LootTableModel Model() =>
        new(
            new() {
                Rolls = 1,
                Entries = [
                    new() { Item = Content.Ore, Weight = 1f },
                    new() { Item = Content.Cloth, Weight = 1f }
                ]
            }
        );

    [Fact]
    public void EveryGestureIsOneChange() {
        var model = Model();
        var changes = 0;
        model.Changed += _ => changes++;

        model.AddEntry();
        model.Edit(2, entry => entry.Item = Content.Mount);
        model.MoveEntry(2, 0);
        model.SetRolls(2);
        model.RemoveEntry(0);

        Assert.Equal(5, changes);
        Assert.Equal(2, model.Count);
        Assert.Equal(2, model.Table.Rolls);
    }

    [Fact]
    public void AnOperationOnARowThatIsNotThereChangesNothing() {
        var model = Model();
        var changes = 0;
        model.Changed += _ => changes++;

        Assert.False(model.RemoveEntry(9));
        Assert.False(model.MoveEntry(0, 9));
        Assert.False(model.MoveEntry(0, 0));
        Assert.False(model.Edit(-1, _ => { }));
        Assert.Equal(0, changes);
    }

    [Fact]
    public void ASnapshotIsDeepEnoughToUndoARowEdit() {
        // A shallow copy would hand the undo stack the very list the next edit mutates, which is the
        // trap a record's `with` walks straight into.
        var model = Model();
        var snapshot = model.Snapshot();

        model.Edit(0, entry => entry.Weight = 99f);
        model.RemoveEntry(1);

        Assert.Equal(1, model.Count);
        Assert.Equal(99f, model.Table.Entries[0].Weight);

        model.Restore(snapshot);

        Assert.Equal(2, model.Count);
        Assert.Equal(1f, model.Table.Entries[0].Weight);
    }

    [Fact]
    public void MovingARowIsARealEditBecauseOrderIsPartOfTheFormat() {
        var model = Model();

        Assert.True(model.MoveEntry(1, 0));
        Assert.Equal(Content.Cloth, model.Table.Entries[0].Item);
        Assert.Equal(Content.Ore, model.Table.Entries[1].Item);
    }

    [Fact]
    public void EveryRuleTheContentBuildEnforcesIsShownWhileTyping() {
        var model = new LootTableModel(
            new() {
                Rolls = 1,
                Entries = [
                    new() { Item = Content.Ore, Table = Content.Trash, Weight = 1f, Chance = 0.5f },
                    new() { Weight = 1f },
                    new() { Item = Content.Ore, Weight = 0f, Chance = 0f },
                    new() { Item = Content.Ore, Chance = 0.5f, Minimum = 5, Maximum = 2 },
                    new() { Item = Content.Ore, Weight = 1f, UsesPity = true }
                ]
            }
        );

        var problems = model.Validate();

        Assert.Contains(problems, problem => problem is { Row: 0 } && problem.Message.Contains("not both", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem is { Row: 0 } && problem.Message.Contains("both an item and a table", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem is { Row: 1 } && problem.Message.Contains("neither an item nor a table", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem is { Row: 2 } && problem.Message.Contains("never drop", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem is { Row: 3 } && problem.Message.Contains("at most", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem is { Row: 4 } && problem.Message.Contains("no chance to raise", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem is { Row: 4 } && problem.Message.Contains("no pity policy", StringComparison.Ordinal));

        // And the two the content build also refuses, so the editor and the build agree.
        var catalog = new DefinitionCatalogBuilder().Add("loot/broken", model.Table).Build();
        var built = LootLibrary.Compile(catalog).Problems;

        Assert.Contains(built, problem => problem.Contains("both a weight and a chance", StringComparison.Ordinal));
        Assert.Contains(built, problem => problem.Contains("neither an item nor a table", StringComparison.Ordinal));
    }

    [Fact]
    public void ATableThatCannotRollIsReportedAgainstTheTableRatherThanARow() {
        var model = new LootTableModel(
            new() { Rolls = 1, Entries = [new() { Item = Content.Ore, Chance = 1f }] }
        );

        Assert.Contains(model.Validate(), problem => problem is { Row: -1 } && problem.Message.Contains("no weighted rows", StringComparison.Ordinal));

        Assert.Contains(
            new LootTableModel(new()).Validate(),
            problem => problem is { Row: -1 } && problem.Message.Contains("no rows", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void APityPolicyWhoseRampNeverHappensIsReported() {
        var model = new LootTableModel(
            new() {
                Rolls = 0,
                Pity = new() { AttemptsBefore = 40, RampPerAttempt = 0.1f, GuaranteedAt = 10 },
                Entries = [new() { Item = Content.Mount, Chance = 0.01f, UsesPity = true }]
            }
        );

        Assert.Contains(model.Validate(), problem => problem.Message.Contains("never happens", StringComparison.Ordinal));
    }

    [Fact]
    public void ACleanTableHasNothingToReport() {
        var loot = Content.Loot();
        var model = new LootTableModel(loot.Get(DefId.From(Content.Boss)).Definition);

        Assert.Empty(model.Validate());
    }
}
