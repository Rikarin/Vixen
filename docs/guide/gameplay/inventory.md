---
title: Inventory and the container algebra
slug: gameplay/inventory
kind: guide
area: Gameplay
summary: One container type with policies, and every mutation a transaction that applies entirely or not at all.
api: [T:Vixen.Gameplay.Inventory.Container, T:Vixen.Gameplay.Inventory.ContainerId, T:Vixen.Gameplay.Inventory.SlotRef, T:Vixen.Gameplay.Inventory.ContainerPolicy, T:Vixen.Gameplay.Inventory.ContainerSet, T:Vixen.Gameplay.Inventory.ContainerTransaction, T:Vixen.Gameplay.Inventory.ContainerResult, T:Vixen.Gameplay.Inventory.ContainerChange, T:Vixen.Gameplay.Inventory.ContainerFailure, T:Vixen.Gameplay.Inventory.InventoryModule]
tags: [gameplay, inventory, containers, transactions, mmo]
since: 0.1
status: preview
related: [gameplay/items, gameplay/loot, gameplay/tags]
---

## What it is

A **container** is slots, a policy, and — for an equipment set — one tag per slot. A **container set**
is every container one owner has, and the only thing allowed to change them. A **transaction** is a
list of moves that happens entirely or not at all.

Bags, equipment slots, bank tabs, guild bank tabs, mail attachments, trade windows, vendor buyback and
loot windows are all the same type with different policies.

## What it is for

Doc 28 calls this *"the part that has to be exactly right because it is where duplication bugs live"*.
One container type means one set of stacking rules, one set of capacity rules and one place a
duplication bug could be — instead of eight implementations that each got the merge case slightly
different.

The transaction is what makes it safe. A two-step move — take from the bank, put in the bag — that
fails on the second step has destroyed an item; one that applies the second step first has duplicated
one. `ContainerSet.Apply` snapshots every container a transaction touches before the first step and
restores all of them if any step fails.

## Using it

Build the containers, add them to a set, and write everything as a transaction. `Move` with different
counts and destinations is also **split** (part of a stack into an empty slot), **merge** (onto a
compatible stack) and **equip** (into an equipment slot) — so there is one validator rather than five.

`Add` puts an item anywhere that will take it, filling existing stacks first. ⚠ **All of it or none of
it**: putting 150 of 200 ore in and dropping the rest is how "you looted it and it vanished" happens,
and only the caller knows whether the right answer is to mail the remainder or refuse the loot.

⚠ **A move onto an occupied slot is refused, not silently swapped.** A UI drag means "swap"; a
scripted move means "put it there". `Move` reports `Occupied` and the UI issues `Swap` — two names for
two intentions.

⚠ **Binding is a trigger, not a flag.** A bag fires `OnPickup`; an equipment set fires `OnEquip`. A
single "binds on insert" would bind a bind-on-equip sword the moment it was looted.

`ContainerResult.Changes` is what a ledger entry is written from: it says what moved, what arrived
from nowhere and what went nowhere, and the caller decides whether that crossed an ownership boundary.

## Examples

Building a character's containers:

```csharp compile
using Vixen.Gameplay;
using Vixen.Gameplay.Items;
using Vixen.Gameplay.Inventory;

static class Character {
    public static ContainerSet Containers(ItemLibrary items, GameplayTagTable tags) =>
        new ContainerSet(items)
            .Add(new(ContainerId.From("bags/0"), 16, new() { BindsOn = ItemBinding.OnPickup }))
            .Add(new(ContainerId.From("bank/0"), 28))
            .Add(
                new(
                    ContainerId.From("equipment"),
                    2,
                    new() { AllowsStacking = false, BindsOn = ItemBinding.OnEquip },
                    [tags.Require("Item.Slot.MainHand"), tags.Require("Item.Slot.Ring")]
                )
            )
            // A trade window takes nothing bound, which is the whole mechanism binding exists for.
            .Add(new(ContainerId.From("trade/offer"), 4, new() { AllowsBound = false, AllowsStacking = false }));
}
```

Looting, equipping, and being told why not:

```csharp compile
using Vixen.Gameplay.Items;
using Vixen.Gameplay.Inventory;

static class Actions {
    public static string Loot(ContainerSet containers, in ItemInstance dropped) {
        var result = containers.Apply(new ContainerTransaction().Add(ContainerId.From("bags/0"), dropped));

        return result.Applied ? "looted" : result.Message;
    }

    // Equipping is a Move. So is splitting a stack, and so is merging two.
    public static bool Equip(ContainerSet containers, int fromSlot, int toSlot) =>
        containers.Apply(
            new ContainerTransaction().Move(
                new(ContainerId.From("bags/0"), fromSlot),
                new(ContainerId.From("equipment"), toSlot)
            )
        ).Applied;
}
```

A multi-step transaction, which either happens or does not:

```csharp compile
using Vixen.Gameplay.Inventory;

static class BankRun {
    public static ContainerResult Withdraw(ContainerSet containers) =>
        containers.Apply(
            new ContainerTransaction()
                .Move(new(ContainerId.From("bank/0"), 0), new(ContainerId.From("bags/0"), 0), 20)
                .Move(new(ContainerId.From("bank/0"), 1), new(ContainerId.From("bags/0"), 1))
                .Swap(new(ContainerId.From("bags/0"), 2), new(ContainerId.From("bags/0"), 3))
        );
}
```

## See also

- [Items](gameplay/items) — what goes in a container.
- [Loot](gameplay/loot) — where most of it comes from.
- [Gameplay tags](gameplay/tags) — what a container's `Accepts` query and its slot tags are.
