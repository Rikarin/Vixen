---
title: Loot tables
slug: gameplay/loot
kind: guide
area: Gameplay
summary: Weighted rows with conditions, pity the engine owns rather than a game's private counter, and a roll reproducible from its event id.
api: [T:Vixen.Gameplay.Loot.LootTableDefinition, T:Vixen.Gameplay.Loot.LootEntryDefinition, T:Vixen.Gameplay.Loot.PityPolicyDefinition, T:Vixen.Gameplay.Loot.SalvageDefinition, T:Vixen.Gameplay.Loot.LootTable, T:Vixen.Gameplay.Loot.LootEntry, T:Vixen.Gameplay.Loot.LootLibrary, T:Vixen.Gameplay.Loot.LootContext, T:Vixen.Gameplay.Loot.LootEvaluator, T:Vixen.Gameplay.Loot.LootDrop, T:Vixen.Gameplay.Loot.LootResult, T:Vixen.Gameplay.Loot.LootDistribution, T:Vixen.Gameplay.Loot.PityKey, T:Vixen.Gameplay.Loot.IPityStore, T:Vixen.Gameplay.Loot.MemoryPityStore, T:Vixen.Gameplay.Loot.LootModule]
tags: [gameplay, loot, drops, pity, random]
since: 0.1
status: preview
related: [gameplay/items, gameplay/inventory, gameplay/randomness, gameplay/loot-editor, gameplay/interaction]
---

## What it is

A **loot table** is a tree of rows. A row drops an item or rolls another table; it is either one of
the table's weighted picks or an independent chance of its own; and it can carry conditions that
decide whether it is in the table for this particular kill.

A **pity policy** turns a run of bad luck into a guarantee, and the counter behind it is durable state
the engine owns.

## What it is for

Everything random that gives a player something: a boss drop, a chest, a gathering node, a salvage, a
reward bag. One evaluator, so the editor's drop simulator and the realm cannot disagree about the odds
— which is the point of the table being a library rather than a script.

And it is auditable. The roll is seeded from `(eventId, player)` and nothing else, so a drop can be
recomputed a year later from a number in a log. *"The log says you rolled a 3"* is answerable.

## Using it

Author tables as `.vxdef` content and compile them into a `LootLibrary`; check `Problems` in the
content build. Roll with the id of the event that caused the drop.

⚠ **The evaluation order is part of the format.** Independent rows first, in the order they were
authored; then the weighted picks; a nested table is rolled where its row sits; within a row the count
is drawn before the affix seed. Reordering any of that changes what every recorded event id produces.

⚠ **A row whose conditions fail is *absent*, not skipped** — the remaining weights renormalise over
what is left. That is what "only on Heroic" means; the alternative is a table that quietly drops
nothing one kill in five and nobody notices for a month.

⚠ **A row is weighted or independent, never both.** A weight puts it in the pick; a chance is rolled
on its own, which is what "and always drops two tokens" is. A row with both is refused at compile
time.

⚠ **`IPityStore` is an interface because the counter must survive a crash.** Doc 28: a pity counter
that resets on a realm crash is a support ticket. The realm supplies a durable one;
`MemoryPityStore` is for a test and the editor's preview.

## Examples

A boss table:

```yaml
# Assets/Loot/boss.vxdef
!LootTableDefinition
displayName: The Boss
rolls: 1
pity: { attemptsBefore: 10, rampPerAttempt: 0.05, guaranteedAt: 30 }
entries:
  # Independent: always, and rarely.
  - { item: items/heroic-token, chance: 1.0, minimum: 2, maximum: 2 }
  - { item: items/raid-drake, chance: 0.01, usesPity: true }

  # Weighted: exactly one of these wins per roll.
  - item: items/flamebrand
    weight: 1
    conditions:
      - { kind: Value, subject: Difficulty, comparison: AtLeast, value: 2 }
  - { item: items/copper-ore, weight: 3, minimum: 2, maximum: 4 }
  - { table: loot/trash, weight: 1 }
```

Rolling it, and putting what dropped somewhere:

```csharp compile
using Vixen.Gameplay;
using Vixen.Gameplay.Items;
using Vixen.Gameplay.Inventory;
using Vixen.Gameplay.Loot;

static class Kill {
    public static ContainerResult Reward(
        LootLibrary loot,
        ItemLibrary items,
        ContainerSet bags,
        IPityStore pity,
        ulong eventId,
        ulong player
    ) {
        var table = loot.Get(DefId.From("loot/boss"));
        var context = new LootContext().With("Difficulty", 2f);
        var result = LootEvaluator.Roll(loot, table, eventId, player, context, pity);

        var transaction = new ContainerTransaction();

        foreach (var instance in result.Materialise(items)) {
            transaction.Add(ContainerId.From("bags/0"), instance);
        }

        // All of it or none of it: a full bag must not swallow half the reward.
        return bags.Apply(transaction);
    }
}
```

Showing a player their pity progress with the number the realm will actually roll against:

```csharp compile
using Vixen.Gameplay.Loot;

static class Odds {
    public static float MountChance(LootTable boss, IPityStore pity, PityKey key) =>
        boss.Pity is { } policy ? LootEvaluator.PityChance(0.01f, policy, pity.AttemptsOf(key)) : 0.01f;
}
```

## See also

- [Items](gameplay/items) — what a drop turns into, and where its seed goes.
- [Inventory](gameplay/inventory) — where it ends up.
- [Gameplay randomness](gameplay/randomness) — the stream underneath, and why it is reproducible.
- [Authoring a loot table](gameplay/loot-editor) — the model, the outline and the drop simulator.
