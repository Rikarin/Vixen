---
title: Attributes and the modifier algebra
slug: gameplay/attributes
kind: guide
area: Gameplay
summary: One stat type, three modifier buckets, one fixed evaluation order, and removal by source that cannot drift.
api: [T:Vixen.Gameplay.AttributeId, T:Vixen.Gameplay.ModifierOp, T:Vixen.Gameplay.ModifierSource, T:Vixen.Gameplay.Modifier, T:Vixen.Gameplay.AttributeRounding, T:Vixen.Gameplay.AttributeSchema, T:Vixen.Gameplay.AttributeLayout, T:Vixen.Gameplay.AttributeLayoutBuilder, T:Vixen.Gameplay.AttributeSet]
tags: [gameplay, attributes, stats, modifiers, balance]
since: 0.1
status: preview
related: [gameplay/effects, gameplay/requirements, gameplay/tags]
---

## What it is

Every number in every gameplay library — a weapon's power, a character's health, a mount's speed, a
crafting station's quality bonus — is one type. An `AttributeLayout` declares which stats a thing has,
what they default to, what they may not leave and how they are rounded; an `AttributeSet` holds one
thing's base values, the `Modifier`s acting on them, and the results.

The evaluation order is fixed, and that is the feature:

```
base  →  +flat  →  ×(1 + Σ additive%)  →  ×Π(1 + multiplicative%)  →  clamp  →  round
```

## What it is for

Ending the argument. Every game that leaves the order open gets a balance team asking whether two
50 % buffs are 100 % or 125 %, answered differently per ability, for ever. Here a designer picks
which bucket a modifier is in — `Add`, `AddPercent`, `MultiplyPercent` — and the arithmetic is never
in question again: additive percentages sum, multiplicative ones compose.

There is deliberately no `Override`. An operation that replaces a value needs a rule for what happens
when two are active, and every such rule is the argument the fixed order exists to end. A polymorph
that fixes movement speed is a large `MultiplyPercent` and a clamp on the layout, which composes with
everything else instead of silently deleting it.

## Using it

Declare the stats once, per archetype, and share the layout. Apply modifiers with a
`ModifierSource` — usually an effect's — and take them off by that source rather than by adding the
negation back.

⚠ **Removal recomputes from the survivors; it never subtracts.** That is what stops a stat landing on
99.9997 after ten cycles of a proc that grants and removes 15 %.

⚠ **Modifiers are held in a canonical order, not in arrival order.** Float addition is not
associative, so a client that applied a trinket before a raid buff and a realm that applied them the
other way round compute numbers differing in the last bit — which prediction reports as a mismatch
and a player sees as jitter.

Recomputation is dirty-flagged per stat and batched: applying twelve modifiers computes nothing, and
the first read or the frame's `Recompute` does the arithmetic once. `HasChanged` is what replication
and the UI read, and a stat whose modifiers changed but whose value did not — two buffs that cancel, a
clamp already saturated — has **not** changed.

A modifier for a stat the layout does not declare is dropped and counted in `DroppedModifiers`, not
thrown: a boss whose layout has no `DodgeChance` being handed a dodge buff is ordinary, and taking a
realm down over it would be worse than the drop. Silently dropping it, though, is how a whole stat
turns out to have done nothing for a month.

## Examples

A layout and the order, worked:

```csharp compile
using Vixen.Gameplay;

static class Balance {
    public static AttributeLayout Layout() =>
        new AttributeLayoutBuilder()
            .Add("Power", 100f)
            .Add("Health", 1000f, minimum: 0f)
            .Add("CritChance", 0.05f, minimum: 0f, maximum: 1f)
            .Build();

    public static float Worked() {
        var set = new AttributeSet(Layout());
        var power = AttributeId.From("Power");

        set.Add(new(power, ModifierOp.Add, 20f, ModifierSource.From(new(1), 1)));
        set.Add(new(power, ModifierOp.AddPercent, 0.5f, ModifierSource.From(new(2), 1)));
        set.Add(new(power, ModifierOp.MultiplyPercent, 0.1f, ModifierSource.From(new(3), 1)));

        // (100 + 20) × 1.5 × 1.1 = 198
        return set.ValueOf(power);
    }
}
```

Removal by source, and why two stacks of one buff need two sources:

```csharp compile
using Vixen.Gameplay;

static class Stacking {
    public static float Cycle(AttributeSet set, DefId buff) {
        var power = AttributeId.From("Power");

        // The instance number is what keeps two applications of one buff apart. Without it the
        // second one's expiry takes the first one's modifiers with it.
        var first = ModifierSource.From(buff, 1);
        var second = ModifierSource.From(buff, 2);

        set.Add(new(power, ModifierOp.AddPercent, 0.2f, first));
        set.Add(new(power, ModifierOp.AddPercent, 0.2f, second));
        set.RemoveBySource(second);

        return set.ValueOf(power);
    }
}
```

The frame's batch, and what replicates:

```csharp compile
using Vixen.Gameplay;

static class Replication {
    public static bool PowerMoved(AttributeSet set) {
        set.Recompute();

        var moved = set.HasChanged(AttributeId.From("Power"));

        set.ClearChanges();

        return moved;
    }
}
```

## See also

- [Effects](gameplay/effects) — where nearly every modifier comes from, and what owns its source.
- [Requirements](gameplay/requirements) — how a stat becomes a condition.
- [Gameplay tags](gameplay/tags) — the other reusable primitive.
