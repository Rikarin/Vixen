// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Gameplay.Items;
using Xunit;

namespace Vixen.Gameplay.Inventory.Tests;

/// <summary>A sword, a ring, some ore and a soulbound token; a bag, a bank, equipment and a trade window.</summary>
public static class Content {
    public const string Sword = "items/flamebrand";
    public const string Ring = "items/plain-ring";
    public const string Ore = "items/copper-ore";
    public const string Token = "items/raid-token";

    public static readonly ContainerId Bag = ContainerId.From("bags/0");
    public static readonly ContainerId Bank = ContainerId.From("bank/0");
    public static readonly ContainerId Equipment = ContainerId.From("equipment");
    public static readonly ContainerId Trade = ContainerId.From("trade/offer");

    public static DefinitionCatalog Catalog() =>
        new DefinitionCatalogBuilder()
            .Add("rarities/common", new ItemRarityDefinition { Order = 0, Affixes = 0, Tag = "Item.Rarity.Common" })
            .Add(
                Sword,
                new ItemDefinition {
                    DisplayName = "Flamebrand",
                    Rarity = "rarities/common",
                    Slot = "Item.Slot.MainHand",
                    ItemLevel = 80,
                    MaximumDurability = 100,
                    Binding = ItemBinding.OnEquip,
                    Tags = ["Item.Weapon.Sword"]
                }
            )
            .Add(
                Ring,
                new ItemDefinition {
                    DisplayName = "Plain Ring",
                    Rarity = "rarities/common",
                    Slot = "Item.Slot.Ring",
                    Tags = ["Item.Trinket.Ring"]
                }
            )
            .Add(
                Ore,
                new ItemDefinition {
                    DisplayName = "Copper Ore",
                    Rarity = "rarities/common",
                    MaximumStack = 100,
                    Tags = ["Item.Material.Ore"]
                }
            )
            .Add(
                Token,
                new ItemDefinition {
                    DisplayName = "Raid Token",
                    Rarity = "rarities/common",
                    MaximumStack = 50,
                    Binding = ItemBinding.OnPickup,
                    Tags = ["Item.Currency.Token"]
                }
            )
            .Build();

    public static ItemLibrary Library() => ItemLibrary.Compile(Catalog());

    public static ContainerSet Set(ItemLibrary? library = null) {
        library ??= Library();
        var tags = Catalog().Tags;

        return new ContainerSet(library)
            .Add(new(Bag, 8, new() { BindsOn = ItemBinding.OnPickup }))
            .Add(new(Bank, 16))
            .Add(
                new(
                    Equipment,
                    2,
                    new() { AllowsStacking = false, BindsOn = ItemBinding.OnEquip },
                    [tags.Require("Item.Slot.MainHand"), tags.Require("Item.Slot.Ring")]
                )
            )
            .Add(new(Trade, 4, new() { AllowsBound = false, AllowsStacking = false }));
    }

    public static ItemInstance Make(ItemLibrary library, string address, int stack = 1) =>
        library.Get(DefId.From(address)).Create(stack);
}

public class ContainerBasicsTests {
    [Fact]
    public void ASlottedContainerNeedsOneTagPerSlot() {
        var tags = Content.Catalog().Tags;

        Assert.Throws<ArgumentException>(
            () => new Container(Content.Equipment, 2, null, [tags.Require("Item.Slot.Ring")])
        );
    }

    [Fact]
    public void AnEmptySetHoldsNothing() {
        var set = Content.Set();

        Assert.Equal(0, set.TotalItems);
        Assert.Equal(8, set.Get(Content.Bag).FreeSlots);
        Assert.Null(set.Find(ContainerId.From("nowhere")));
    }

    [Fact]
    public void OneContainerCannotBeAddedTwice() {
        var set = Content.Set();

        Assert.Throws<InvalidOperationException>(() => set.Add(new(Content.Bag, 4)));
    }
}

public class ContainerTransactionTests {
    [Fact]
    public void AnAddFillsExistingStacksBeforeEmptySlots() {
        var library = Content.Library();
        var set = Content.Set(library);

        Assert.True(set.Apply(new ContainerTransaction().Add(Content.Bag, Content.Make(library, Content.Ore, 60))).Applied);
        Assert.True(set.Apply(new ContainerTransaction().Add(Content.Bag, Content.Make(library, Content.Ore, 60))).Applied);

        var bag = set.Get(Content.Bag);

        Assert.Equal(100, bag[0].Stack);
        Assert.Equal(20, bag[1].Stack);
        Assert.Equal(120, set.CountOf(DefId.From(Content.Ore)));
    }

    [Fact]
    public void AnAddThatDoesNotFitEntirelyDoesNotFitAtAll() {
        var library = Content.Library();
        var set = new ContainerSet(library).Add(new(Content.Bag, 1));

        // 150 of something that stacks to 100: two slots' worth, and the bag has one.
        var result = set.Apply(
            new ContainerTransaction().Add(Content.Bag, ItemInstance.Of(DefId.From(Content.Ore), 150))
        );

        Assert.False(result.Applied);
        Assert.Equal(ContainerFailure.Full, result.Failure);

        // The half that would have fitted must not be there. Partial success is how "you looted it
        // and it vanished" happens.
        Assert.Equal(0, set.TotalItems);
    }

    [Fact]
    public void AMoveOfPartOfAStackIsASplit() {
        var library = Content.Library();
        var set = Content.Set(library);

        set.Apply(new ContainerTransaction().Add(Content.Bag, Content.Make(library, Content.Ore, 80)));

        Assert.True(set.Apply(new ContainerTransaction().Move(new(Content.Bag, 0), new(Content.Bag, 3), 30)).Applied);

        Assert.Equal(50, set.Get(Content.Bag)[0].Stack);
        Assert.Equal(30, set.Get(Content.Bag)[3].Stack);
        Assert.Equal(80, set.CountOf(DefId.From(Content.Ore)));
    }

    [Fact]
    public void AMoveOntoACompatibleStackIsAMerge() {
        var library = Content.Library();
        var set = Content.Set(library);

        set.Apply(
            new ContainerTransaction()
                .Insert(new(Content.Bag, 0), Content.Make(library, Content.Ore, 30))
                .Insert(new(Content.Bag, 1), Content.Make(library, Content.Ore, 40))
        );

        Assert.True(set.Apply(new ContainerTransaction().Move(new(Content.Bag, 1), new(Content.Bag, 0))).Applied);

        Assert.Equal(70, set.Get(Content.Bag)[0].Stack);
        Assert.False(set.Get(Content.Bag)[1].IsSome);
    }

    [Fact]
    public void AMergeThatWouldOverflowTheStackIsRefused() {
        var library = Content.Library();
        var set = Content.Set(library);

        set.Apply(
            new ContainerTransaction()
                .Insert(new(Content.Bag, 0), Content.Make(library, Content.Ore, 90))
                .Insert(new(Content.Bag, 1), Content.Make(library, Content.Ore, 40))
        );

        var result = set.Apply(new ContainerTransaction().Move(new(Content.Bag, 1), new(Content.Bag, 0)));

        Assert.Equal(ContainerFailure.Full, result.Failure);
        Assert.Equal(130, set.CountOf(DefId.From(Content.Ore)));
    }

    [Fact]
    public void AMoveOntoSomethingElseIsRefusedRatherThanSilentlySwapping() {
        var library = Content.Library();
        var set = Content.Set(library);

        set.Apply(
            new ContainerTransaction()
                .Insert(new(Content.Bag, 0), Content.Make(library, Content.Ore, 5))
                .Insert(new(Content.Bag, 1), Content.Make(library, Content.Sword))
        );

        var result = set.Apply(new ContainerTransaction().Move(new(Content.Bag, 0), new(Content.Bag, 1)));

        Assert.Equal(ContainerFailure.Occupied, result.Failure);
        Assert.Contains("Swap", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AStackDraggedOntoItselfIsNotDestroyed() {
        // The conservation oracle found this. The merge path writes the destination and then the
        // source, which for one slot is two writes to the same slot — the second of them
        // `stack - count`. A player drags a stack onto itself several times an hour.
        var library = Content.Library();
        var set = Content.Set(library);

        set.Apply(new ContainerTransaction().Add(Content.Bag, Content.Make(library, Content.Ore, 40)));

        Assert.True(set.Apply(new ContainerTransaction().Move(new(Content.Bag, 0), new(Content.Bag, 0))).Applied);
        Assert.Equal(40, set.CountOf(DefId.From(Content.Ore)));

        Assert.True(set.Apply(new ContainerTransaction().Move(new(Content.Bag, 0), new(Content.Bag, 0), 15)).Applied);
        Assert.Equal(40, set.CountOf(DefId.From(Content.Ore)));
    }

    [Fact]
    public void ASwapExchangesTwoSlotsInEitherDirection() {
        var library = Content.Library();
        var set = Content.Set(library);

        set.Apply(
            new ContainerTransaction()
                .Insert(new(Content.Bag, 0), Content.Make(library, Content.Ore, 5))
                .Insert(new(Content.Bank, 3), Content.Make(library, Content.Sword))
        );

        Assert.True(set.Apply(new ContainerTransaction().Swap(new(Content.Bag, 0), new(Content.Bank, 3))).Applied);

        Assert.Equal(DefId.From(Content.Sword), set.Get(Content.Bag)[0].Definition);
        Assert.Equal(DefId.From(Content.Ore), set.Get(Content.Bank)[3].Definition);
    }

    [Fact]
    public void EquippingIsAMoveIntoASlottedContainer() {
        var library = Content.Library();
        var set = Content.Set(library);

        set.Apply(new ContainerTransaction().Insert(new(Content.Bag, 0), Content.Make(library, Content.Sword)));

        Assert.True(set.Apply(new ContainerTransaction().Move(new(Content.Bag, 0), new(Content.Equipment, 0))).Applied);
        Assert.Equal(DefId.From(Content.Sword), set.Get(Content.Equipment)[0].Definition);
    }

    [Fact]
    public void AnItemDoesNotGoInTheWrongEquipmentSlot() {
        var library = Content.Library();
        var set = Content.Set(library);

        set.Apply(new ContainerTransaction().Insert(new(Content.Bag, 0), Content.Make(library, Content.Sword)));

        var result = set.Apply(new ContainerTransaction().Move(new(Content.Bag, 0), new(Content.Equipment, 1)));

        Assert.Equal(ContainerFailure.WrongSlot, result.Failure);
        Assert.Equal(DefId.From(Content.Sword), set.Get(Content.Bag)[0].Definition);
    }

    [Fact]
    public void AddSkipsSlotsThatWantSomethingElseRatherThanFailingOnThem() {
        var library = Content.Library();
        var set = Content.Set(library);

        // Slot 0 wants a main hand, slot 1 wants a ring. Adding a ring must find slot 1.
        Assert.True(set.Apply(new ContainerTransaction().Add(Content.Equipment, Content.Make(library, Content.Ring))).Applied);
        Assert.Equal(DefId.From(Content.Ring), set.Get(Content.Equipment)[1].Definition);
    }

    [Fact]
    public void AContainerThatRefusesAnItemSaysWhichContainerAndWhichItem() {
        var library = Content.Library();
        var tags = Content.Catalog().Tags;

        var set = new ContainerSet(library)
            .Add(new(Content.Bank, 4, new() { Accepts = GameplayTagQuery.Resolve(tags, all: ["Item.Material"]) }));

        var result = set.Apply(new ContainerTransaction().Add(Content.Bank, Content.Make(library, Content.Sword)));

        Assert.Equal(ContainerFailure.Rejected, result.Failure);
        Assert.Contains("Flamebrand", result.Message, StringComparison.Ordinal);

        Assert.True(set.Apply(new ContainerTransaction().Add(Content.Bank, Content.Make(library, Content.Ore, 3))).Applied);
    }

    [Fact]
    public void ANonExistentContainerOrSlotIsNamed() {
        var library = Content.Library();
        var set = Content.Set(library);

        Assert.Equal(
            ContainerFailure.NoSuchContainer,
            set.Apply(new ContainerTransaction().Move(new(ContainerId.From("nowhere"), 0), new(Content.Bag, 0))).Failure
        );

        Assert.Equal(
            ContainerFailure.NoSuchSlot,
            set.Apply(new ContainerTransaction().Move(new(Content.Bag, 99), new(Content.Bag, 0))).Failure
        );
    }

    [Fact]
    public void NothingChangesInAReadOnlyContainer() {
        var library = Content.Library();
        var vendor = ContainerId.From("vendor/stock");

        var set = new ContainerSet(library)
            .Add(new(Content.Bag, 4))
            .Add(new(vendor, 4, new() { IsReadOnly = true }));

        Assert.Equal(
            ContainerFailure.ReadOnly,
            set.Apply(new ContainerTransaction().Add(vendor, Content.Make(library, Content.Ore, 1))).Failure
        );
    }

    [Fact]
    public void AnItemThisBuildDoesNotKnowIsRefusedRatherThanStored() {
        var set = Content.Set();

        var result = set.Apply(
            new ContainerTransaction().Add(Content.Bag, ItemInstance.Of(DefId.From("items/nonexistent"), 1))
        );

        Assert.Equal(ContainerFailure.UnknownItem, result.Failure);
        Assert.Equal(0, set.TotalItems);
    }
}

public class BindingTests {
    [Fact]
    public void ABagBindsOnPickupAndAnEquipmentSetBindsOnEquip() {
        var library = Content.Library();
        var set = Content.Set(library);

        // The token binds on pickup, so arriving in the bag binds it.
        set.Apply(new ContainerTransaction().Add(Content.Bag, Content.Make(library, Content.Token, 3)));
        Assert.Equal(ItemBinding.Bound, set.Get(Content.Bag)[0].Binding);

        // The sword binds on equip, so arriving in the bag leaves it tradeable.
        set.Apply(new ContainerTransaction().Insert(new(Content.Bag, 1), Content.Make(library, Content.Sword)));
        Assert.Equal(ItemBinding.OnEquip, set.Get(Content.Bag)[1].Binding);
        Assert.True(set.Get(Content.Bag)[1].IsTradeable);

        set.Apply(new ContainerTransaction().Move(new(Content.Bag, 1), new(Content.Equipment, 0)));
        Assert.Equal(ItemBinding.Bound, set.Get(Content.Equipment)[0].Binding);
    }

    [Fact]
    public void ATradeWindowRefusesABoundItem() {
        var library = Content.Library();
        var set = Content.Set(library);

        set.Apply(new ContainerTransaction().Add(Content.Bag, Content.Make(library, Content.Token, 1)));

        var result = set.Apply(new ContainerTransaction().Move(new(Content.Bag, 0), new(Content.Trade, 0)));

        Assert.Equal(ContainerFailure.Bound, result.Failure);
        Assert.Equal(1, set.Get(Content.Bag).TotalItems);
    }

    [Fact]
    public void ABoundStackAndAnUnboundStackOfOneItemDoNotMerge() {
        var library = Content.Library();
        var set = new ContainerSet(library).Add(new(Content.Bank, 4));

        var unbound = Content.Make(library, Content.Token, 5);

        set.Apply(
            new ContainerTransaction()
                .Insert(new(Content.Bank, 0), unbound)
                .Insert(new(Content.Bank, 1), unbound.Bind())
        );

        Assert.Equal(ContainerFailure.Occupied, set.Apply(new ContainerTransaction().Move(new(Content.Bank, 1), new(Content.Bank, 0))).Failure);
        Assert.Equal(10, set.CountOf(DefId.From(Content.Token)));
    }
}

public class AtomicityTests {
    [Fact]
    public void AFailingStepUndoesEveryStepBeforeIt() {
        var library = Content.Library();
        var set = Content.Set(library);

        set.Apply(new ContainerTransaction().Add(Content.Bank, Content.Make(library, Content.Ore, 60)));

        var before = Snapshot(set);

        // Two moves that would work, then one that cannot: a sword into the ring slot.
        var result = set.Apply(
            new ContainerTransaction()
                .Move(new(Content.Bank, 0), new(Content.Bag, 0), 20)
                .Insert(new(Content.Bag, 1), Content.Make(library, Content.Sword))
                .Move(new(Content.Bag, 1), new(Content.Equipment, 1))
        );

        Assert.False(result.Applied);
        Assert.Empty(result.Changes);
        Assert.Equal(before, Snapshot(set));
    }

    [Fact]
    public void AnAppliedTransactionReportsEveryChangeInOrder() {
        var library = Content.Library();
        var set = Content.Set(library);

        set.Apply(new ContainerTransaction().Add(Content.Bank, Content.Make(library, Content.Ore, 60)));

        var result = set.Apply(
            new ContainerTransaction()
                .Move(new(Content.Bank, 0), new(Content.Bag, 0), 20)
                .Move(new(Content.Bank, 0), new(Content.Bag, 1), 10)
        );

        Assert.True(result.Applied);
        Assert.Equal(2, result.Changes.Count);
        Assert.Equal(new SlotRef(Content.Bank, 0), result.Changes[0].From);
        Assert.Equal(new SlotRef(Content.Bag, 0), result.Changes[0].To);
        Assert.Equal(20, result.Changes[0].Item.Stack);
        Assert.Equal(10, result.Changes[1].Item.Stack);
    }

    [Fact]
    public void AnEmptyTransactionDoesNothingAndSaysSo() {
        var set = Content.Set();

        var result = set.Apply(new ContainerTransaction());

        Assert.True(result.Applied);
        Assert.Empty(result.Changes);
    }

    static List<(ContainerId Container, int Slot, ItemInstance Item)> Snapshot(ContainerSet set) {
        var rows = new List<(ContainerId, int, ItemInstance)>();

        foreach (var container in set.Containers) {
            for (var slot = 0; slot < container.Capacity; slot++) {
                rows.Add((container.Id, slot, container[slot]));
            }
        }

        rows.Sort(
            static (left, right) => left.Item1.Value == right.Item1.Value
                ? left.Item2.CompareTo(right.Item2)
                : left.Item1.Value.CompareTo(right.Item1.Value)
        );

        return rows;
    }
}

/// <summary>Doc 28 § Testing's important one, and the reason the transaction type exists.</summary>
public class ConservationOracleTests {
    [Fact]
    public void ItemsAreConservedAcrossRandomisedTransactionsWithInjectedFailures() {
        var library = Content.Library();
        var set = Content.Set(library);
        var random = new GameplayRandom(0xC0FFEE);

        string[] items = [Content.Ore, Content.Sword, Content.Ring, Content.Token];
        var slots = Slots(set);

        // Everything that enters or leaves the world is counted, so that the invariant is exact
        // rather than "roughly the same". A move must never change it; an insert and a remove must
        // change it by exactly what they said.
        var expected = 0;
        var applied = 0;
        var refused = 0;

        for (var step = 0; step < 20000; step++) {
            var transaction = new ContainerTransaction();

            // One to four operations, so that rollback has more than one step to undo.
            var operations = random.NextInt(1, 5);

            for (var operation = 0; operation < operations; operation++) {
                var from = slots[random.NextInt(slots.Count)];
                var to = slots[random.NextInt(slots.Count)];

                switch (random.NextInt(6)) {
                    case 0: {
                        var address = items[random.NextInt(items.Length)];
                        transaction.Add(from.Container, Content.Make(library, address, random.NextInt(1, 40)));

                        break;
                    }

                    case 1: {
                        var address = items[random.NextInt(items.Length)];
                        transaction.Insert(to, Content.Make(library, address, random.NextInt(1, 40)));

                        break;
                    }

                    case 2:
                        transaction.Remove(from, random.NextInt(0, 10));

                        break;

                    case 3:
                        transaction.Swap(from, to);

                        break;

                    default:
                        transaction.Move(from, to, random.NextInt(0, 12));

                        break;
                }
            }

            var before = set.TotalItems;
            var result = set.Apply(transaction);

            if (!result.Applied) {
                refused++;

                // A refused transaction changed nothing at all.
                Assert.Equal(before, set.TotalItems);

                continue;
            }

            applied++;

            // What the set holds now is what it held, plus everything the changes said arrived from
            // nowhere, minus everything they said went nowhere. Every other change is a move, and a
            // move conserves by construction.
            var created = 0;
            var destroyed = 0;

            foreach (var change in result.Changes) {
                if (!change.From.IsSome) {
                    created += change.Item.Stack;
                }

                if (!change.To.IsSome) {
                    destroyed += change.Item.Stack;
                }
            }

            Assert.Equal(before + created - destroyed, set.TotalItems);

            expected += created - destroyed;
        }

        Assert.Equal(expected, set.TotalItems);

        // A run in which nothing was ever refused, or nothing ever applied, would assert nothing.
        Assert.True(applied > 1000, $"only {applied} transactions applied");
        Assert.True(refused > 1000, $"only {refused} transactions were refused");
    }

    [Fact]
    public void NoSlotEverHoldsMoreThanTheItemStacksTo() {
        var library = Content.Library();
        var set = Content.Set(library);
        var random = new GameplayRandom(77);
        var slots = Slots(set);

        for (var step = 0; step < 5000; step++) {
            var transaction = new ContainerTransaction();

            for (var operation = 0; operation < 3; operation++) {
                var from = slots[random.NextInt(slots.Count)];
                var to = slots[random.NextInt(slots.Count)];

                if (random.Chance(0.4f)) {
                    transaction.Add(from.Container, Content.Make(library, Content.Ore, random.NextInt(1, 120)));
                } else {
                    transaction.Move(from, to, random.NextInt(0, 60));
                }
            }

            set.Apply(transaction);

            foreach (var container in set.Containers) {
                foreach (var instance in container.Slots) {
                    if (!instance.IsSome) {
                        continue;
                    }

                    Assert.InRange(instance.Stack, 1, library.Get(instance.Definition).MaximumStack);
                }
            }
        }
    }

    static List<SlotRef> Slots(ContainerSet set) {
        var slots = new List<SlotRef>();

        foreach (var container in set.Containers) {
            for (var slot = 0; slot < container.Capacity; slot++) {
                slots.Add(new(container.Id, slot));
            }
        }

        return slots;
    }
}

public class InventoryModuleTests {
    [Fact]
    public void InventoryNeedsItemsAndTheKernel() {
        Assert.Throws<InvalidOperationException>(() => new GameplayConfig().Use<InventoryModule>().Build());

        var composition = new GameplayConfig()
            .Use<GameplayKernelModule>()
            .Use<ItemsModule>()
            .Use<InventoryModule>()
            .Build();

        Assert.Equal(3, composition.Modules.Count);
        Assert.Contains(InventoryModule.SlotRoot, composition.Tags);
    }
}
